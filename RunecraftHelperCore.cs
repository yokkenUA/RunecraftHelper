namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class RunecraftHelperCore : PCore<RunecraftHelperSettings>
    {
        // Fixed UI path through PoE2 0.5.x's Runeshape Combinations panel:
        //   GameUi → window-container → ? → ? → ? → recipes-container
        // Child indices wiggle across game restarts, but each UiElement's Flags field encodes
        // its "role" (panel/list/row/etc.) and those bits stay stable — so we match by Flags
        // fingerprint instead of by index. The IsVisible bit (bit 0x0B / mask 0x800) is masked
        // out before comparison because it toggles when the player opens/closes the panel.
        //
        // PoE2's UI tree has many sibling UiElements sharing the same fp at each level, so a
        // greedy "pick the first/visible match" walk can step into the wrong subtree and
        // silently dead-end. WalkFp instead BACKTRACKS: at each step it tries every matching
        // sibling (visible candidates first), recurses, and keeps whichever branch reaches a
        // valid recipes-container at the bottom (see IsRecipesContainer). Mirrors the Atlas
        // plugin's resolver.
        //
        // GateStep (the window-container) is the panel-open gate: its IsVisible bit flips with
        // the panel, so that hop only accepts a visible match — when the panel is closed the
        // whole walk fails and we draw nothing.
        //
        // The recipes-container has ~320 child rows; only a handful are visible at a time (rest
        // are scrolled off / templated). Each visible row's kid[0] holds an inline std::wstring
        // "<count>x <name>" at +0x390.
        private static readonly uint[] PanelFlagFingerprints =
        {
            0x00462EF1, // window-container (its IsVisible bit toggles with the panel)
            0x00502EF3,
            0x00502EF7,
            0x00542EF1,
            0x00502EF1, // recipes-container
        };
        private const int GateStep = 0;

        private const int NameWStringOffset = 0x390;
        private const int UiElementChildrenOffset = 0x10;
        private const int UiElementFlagsOffset = 0x180;
        private const int IsVisibleBit = 0x0B;
        private const uint IsVisibleMask = 1u << IsVisibleBit; // = 0x800

        private IntPtr processHandle = IntPtr.Zero;
        private int handlePid;

        private readonly List<Recipe> recipes = new();
        private readonly PriceCache priceCache = new();
        private DateTime nextAutoRefreshCheckUtc = DateTime.MinValue;

        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private string PriceCachePathname => Path.Join(this.DllDirectory, "config", "prices.json");

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<RunecraftHelperSettings>(content)
                                ?? new RunecraftHelperSettings();
            }

            var fresh = this.priceCache.TryLoadFromDisk(this.PriceCachePathname, this.Settings.CacheTtlMinutes);
            if (!fresh)
                this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
        }

        public override void OnDisable() => this.ResetHandle();

        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname)!);
            this.Settings.LastSyncUtc = this.priceCache.LastSyncUtc;
            File.WriteAllText(this.SettingPathname, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            ImGui.TextWrapped("RunecraftHelper: window appears while the in-game Runeshape Combinations panel " +
                              "is open, listing the rewards currently visible. Prices come from poe.ninja.");

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.InputText("League", ref this.Settings.League, 64);
            ImGui.SliderInt("Refresh interval (min)", ref this.Settings.CacheTtlMinutes, 5, 60);

            ImGui.Spacing();

            var status = this.priceCache.Status;
            var lastSync = this.priceCache.LastSyncUtc;
            string statusText = status switch
            {
                PriceSyncStatus.Syncing => "syncing…",
                PriceSyncStatus.Ready => lastSync == DateTime.MinValue
                    ? "ready (no data yet)"
                    : $"updated {FormatRelative(lastSync)} ago",
                PriceSyncStatus.Error => $"error: {this.priceCache.LastError}",
                _ => "idle",
            };

            ImGui.Text($"Status: {statusText}");
            ImGui.Text($"Items cached: {this.priceCache.PriceCount}");
            if (this.priceCache.DivineToExaltedRate > 0)
                ImGui.Text($"1 Divine = {this.priceCache.DivineToExaltedRate:F2} Exalted");

            ImGui.BeginDisabled(status == PriceSyncStatus.Syncing);
            if (ImGui.Button("Refresh now"))
                this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
            ImGui.EndDisabled();
        }

        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                this.recipes.Clear();
                return;
            }

            this.MaybeAutoRefreshPrices();

            if (!this.EnsureProcess()) return;

            var panel = this.ResolvePanel();
            if (panel == IntPtr.Zero)
            {
                this.recipes.Clear();
                return;
            }

            this.ReadVisibleRecipes(panel);
            if (this.recipes.Count == 0) return;

            this.DrawWindow();
        }

        // ── Panel resolution ──────────────────────────────────────────────

        // Walk from GameUi.Address down to the recipes container by matching each step's Flags
        // fingerprint (IsVisible bit masked), backtracking across sibling matches.
        private IntPtr ResolvePanel()
        {
            var gameUi = Core.States.InGameStateObject.GameUi.Address;
            if (gameUi == IntPtr.Zero) return IntPtr.Zero;
            return this.WalkFp(gameUi, PanelFlagFingerprints, GateStep, 0);
        }

        // Recursive backtracking fp-walk. At `step`, scan `parent`'s children for ones whose
        // Flags (IsVisible bit masked) match fps[step], trying visible candidates before
        // invisible ones, and recurse into each until a branch reaches a valid recipes container
        // at the bottom. `gateStep` only accepts a visible match (the panel-open gate).
        private IntPtr WalkFp(IntPtr parentAddr, uint[] fps, int gateStep, int step)
        {
            if (step == fps.Length)
                return this.IsRecipesContainer(parentAddr) ? parentAddr : IntPtr.Zero;

            if (!this.TryReadStdVector(parentAddr + UiElementChildrenOffset, out var first, out var last))
                return IntPtr.Zero;
            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 4000) return IntPtr.Zero;

            uint target = fps[step] & ~IsVisibleMask;

            // Pass 0 = visible candidates, pass 1 = invisible — so the gate naturally prefers
            // the open instance, and other steps still fall back to invisible siblings.
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantVisible = pass == 0;
                for (int i = 0; i < n; i++)
                {
                    var childAddr = this.ReadPtr(first + (nint)(i * 8));
                    if (childAddr == IntPtr.Zero) continue;
                    if (!this.TryReadFlags(childAddr, out var flags)) continue;
                    if ((flags & ~IsVisibleMask) != target) continue;

                    bool visible = (flags & IsVisibleMask) != 0;
                    if (visible != wantVisible) continue;
                    if (step == gateStep && !visible) continue;

                    var deeper = this.WalkFp(childAddr, fps, gateStep, step + 1);
                    if (deeper != IntPtr.Zero)
                        return deeper;
                }
            }
            return IntPtr.Zero;
        }

        // Terminal validation for the fp-walk: the real recipes container holds row elements
        // whose kid[0] carries the "<count>x <name>" label as an inline std::wstring at +0x390.
        // Requiring at least one child to yield a non-empty label distinguishes it from
        // unrelated siblings that share the same 0x00502EF1 fingerprint but contain no rows.
        private bool IsRecipesContainer(IntPtr addr)
        {
            if (!this.TryReadStdVector(addr + UiElementChildrenOffset, out var first, out var last)) return false;
            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 4000) return false;

            for (int i = 0; i < n; i++)
            {
                var row = this.ReadPtr(first + (nint)(i * 8));
                if (row == IntPtr.Zero) continue;
                var label = this.GetChild(row, 0);
                if (label == IntPtr.Zero) continue;
                if (!string.IsNullOrEmpty(this.ReadStdWString(label + NameWStringOffset)))
                    return true;
            }
            return false;
        }

        // The recipes container itself uses index 0 for the row's label — that index is stable
        // because each row has a fixed layout (label first, then rune icons).
        private IntPtr GetChild(IntPtr addr, int index)
        {
            if (addr == IntPtr.Zero) return IntPtr.Zero;
            if (!this.TryReadStdVector(addr + UiElementChildrenOffset, out var first, out var last)) return IntPtr.Zero;
            long n = ((long)last - (long)first) / 8;
            if (index < 0 || index >= n) return IntPtr.Zero;
            return this.ReadPtr(first + (nint)(index * 8));
        }

        // ── Reading rows ──────────────────────────────────────────────────

        private void ReadVisibleRecipes(IntPtr panel)
        {
            this.recipes.Clear();
            if (!this.TryReadStdVector(panel + UiElementChildrenOffset, out var first, out var last)) return;
            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 4000) return;

            for (int i = 0; i < n; i++)
            {
                var row = this.ReadPtr(first + (nint)(i * 8));
                if (row == IntPtr.Zero) continue;
                if (!this.IsUiElementVisible(row)) continue;

                var label = this.GetChild(row, 0);
                if (label == IntPtr.Zero) continue;

                var raw = this.ReadStdWString(label + NameWStringOffset);
                if (string.IsNullOrEmpty(raw)) continue;

                SplitCountAndName(raw, out var count, out var name);
                this.recipes.Add(new Recipe(count, name));
            }
        }

        // ── Price refresh polling ─────────────────────────────────────────

        // Cheap once-a-minute poll: if the cache is older than the configured TTL and no sync is
        // already in flight, kick one off. The first refresh after OnEnable is initiated there;
        // this only handles long-lived sessions where the TTL eventually expires.
        private void MaybeAutoRefreshPrices()
        {
            var now = DateTime.UtcNow;
            if (now < this.nextAutoRefreshCheckUtc) return;
            this.nextAutoRefreshCheckUtc = now.AddMinutes(1);

            if (this.priceCache.Status == PriceSyncStatus.Syncing) return;
            var ttl = TimeSpan.FromMinutes(Math.Max(1, this.Settings.CacheTtlMinutes));
            if (this.priceCache.LastSyncUtc != DateTime.MinValue && now - this.priceCache.LastSyncUtc < ttl) return;

            this.priceCache.StartRefresh(this.Settings.League, this.PriceCachePathname);
        }

        // ── Drawing ───────────────────────────────────────────────────────

        private void DrawWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(360, 400), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin($"Runeshape Rewards ({this.recipes.Count})###RunecraftHelper"))
            {
                ImGui.End();
                return;
            }

            if (ImGui.BeginTable("recipes", 3,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 32f);
                ImGui.TableSetupColumn("Reward", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("ex", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                for (int i = 0; i < this.recipes.Count; i++)
                {
                    var r = this.recipes[i];
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextDisabled($"{r.Count}x");
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(r.Name);
                    ImGui.TableSetColumnIndex(2);
                    if (this.priceCache.TryGetExaltedPrice(r.Name, out var unit) && unit > 0)
                    {
                        var total = unit * Math.Max(1, r.Count);
                        ImGui.TextUnformatted(FormatExalted(total));
                    }
                    else
                    {
                        ImGui.TextDisabled("—");
                    }
                }

                ImGui.EndTable();
            }

            ImGui.End();
        }

        // ── Parsing / formatting ─────────────────────────────────────────

        private static void SplitCountAndName(string raw, out int count, out string name)
        {
            count = 0;
            name = raw;
            if (string.IsNullOrEmpty(raw)) return;

            int i = 0;
            while (i < raw.Length && char.IsDigit(raw[i])) i++;
            if (i == 0 || i >= raw.Length || raw[i] != 'x' || i + 1 >= raw.Length || raw[i + 1] != ' ') return;

            if (!int.TryParse(raw.AsSpan(0, i), out count)) count = 0;
            name = raw[(i + 2)..];
        }

        private static string FormatExalted(double value)
        {
            // Round by magnitude, then strip trailing zeros but keep at least one decimal for
            // sub-100 values — so a ~1ex reward reads "1,0 ex", not "1,000 ex".
            if (value >= 100) return $"{value:F0} ex";
            int decimals = value >= 1 ? 1 : value >= 0.1 ? 2 : 3;
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            string num = rounded.ToString("0.###");
            var sep = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            if (!num.Contains(sep)) num += sep + "0";
            return $"{num} ex";
        }

        private static string FormatRelative(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
            if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h";
            return $"{(int)span.TotalDays}d";
        }

        // ── Memory primitives ────────────────────────────────────────────

        private bool EnsureProcess()
        {
            int pid = (int)Core.Process.Pid;
            if (pid == 0)
            {
                if (this.handlePid != 0) this.ResetHandle();
                return false;
            }

            if (pid == this.handlePid && this.processHandle != IntPtr.Zero) return true;

            this.ResetHandle();
            this.processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (this.processHandle == IntPtr.Zero) return false;
            this.handlePid = pid;
            return true;
        }

        private void ResetHandle()
        {
            if (this.processHandle != IntPtr.Zero)
            {
                CloseHandle(this.processHandle);
                this.processHandle = IntPtr.Zero;
            }

            this.handlePid = 0;
        }

        private bool IsUiElementVisible(IntPtr addr)
        {
            return this.TryReadFlags(addr, out var flags) && (flags & IsVisibleMask) != 0;
        }

        private bool TryReadFlags(IntPtr addr, out uint flags)
        {
            flags = 0;
            if (addr == IntPtr.Zero) return false;
            var buf = new byte[4];
            if (!ReadProcessMemory(this.processHandle, addr + UiElementFlagsOffset, buf, (uint)buf.Length, out _)) return false;
            flags = BitConverter.ToUInt32(buf, 0);
            return true;
        }

        private IntPtr ReadPtr(IntPtr addr)
        {
            if (addr == IntPtr.Zero) return IntPtr.Zero;
            var buf = new byte[8];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return IntPtr.Zero;
            return (IntPtr)BitConverter.ToInt64(buf, 0);
        }

        private bool TryReadStdVector(IntPtr addr, out IntPtr first, out IntPtr last)
        {
            first = IntPtr.Zero;
            last = IntPtr.Zero;
            var buf = new byte[16];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return false;
            first = (IntPtr)BitConverter.ToInt64(buf, 0);
            last = (IntPtr)BitConverter.ToInt64(buf, 8);
            if (first == IntPtr.Zero) return false;
            ulong f = (ulong)(long)first;
            if (f < 0x10000 || f > 0x7FFFFFFFFFFFul) return false;
            if ((long)last < (long)first) return false;
            return true;
        }

        // MSVC std::wstring: buffer ptr at +0x00 (or 8 chars inline if cap < 8), length at +0x10, capacity at +0x18.
        private string ReadStdWString(IntPtr addr)
        {
            var buf = new byte[0x20];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return string.Empty;

            int len = BitConverter.ToInt32(buf, 0x10);
            if (len <= 0 || len > 256) return string.Empty;
            int cap = BitConverter.ToInt32(buf, 0x18);
            if (cap < len) return string.Empty;

            if (cap < 8)
            {
                int byteLen = Math.Min(len * 2, 16);
                return Encoding.Unicode.GetString(buf, 0, byteLen);
            }

            long ptr = BitConverter.ToInt64(buf, 0);
            if (ptr < 0x10000 || ptr > 0x7FFFFFFFFFFF) return string.Empty;
            var outBuf = new byte[len * 2];
            if (!ReadProcessMemory(this.processHandle, (IntPtr)ptr, outBuf, (uint)outBuf.Length, out _)) return string.Empty;
            return Encoding.Unicode.GetString(outBuf);
        }

        // ── P/Invoke ─────────────────────────────────────────────────────

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint dwSize, out int lpNumberOfBytesRead);

        private readonly record struct Recipe(int Count, string Name);
    }
}
