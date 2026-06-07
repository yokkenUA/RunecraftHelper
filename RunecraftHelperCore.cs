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

        // ── Language-independent reward matching (offsets verified live on PoE2 0.5.x, see GHIDRA.md §8) ──
        // The visible reward is shown only as LOCALIZED text, so matching that text to poe.ninja
        // (English) fails on non-English clients. Instead we translate the localized name → the
        // item's language-independent art-id via the game's own BaseItemTypes table:
        //   BaseItemType row (stride 0x168):  +0x20 → localized name buffer,  +0x7C → +0x08 → art ".dds".
        // We reach BaseItemTypes through the loaded Expedition2Recipes table (recipe row +0x34 holds the
        // shared BaseItemTypes table object), found by walking the recipe panel's pointer graph to the
        // dat-file handle (vtable at +0x00, "…Expedition2Recipes.dat" path at +0x08, rows-vector at +0x28).
        private const int RecipeStride = 0xBA;
        private const int RecipeRewardTableOffset = 0x34;  // recipe row → BaseItemTypes table object
        private const int TableRowsVectorOffset = 0x28;    // table object → ptr to {begin,end} rows vector
        private const int DatPathOffset = 0x08;            // dat-file handle/table object → path string ptr
        private const int BaseItemTypeStride = 0x168;
        private const int BaseItemTypeNameOffset = 0x20;   // → localized display-name buffer
        private const int BaseItemTypeVisualOffset = 0x7C; // → visual-identity obj; its +0x08 → art ".dds"
        private const int VisualArtOffset = 0x08;

        private IntPtr processHandle = IntPtr.Zero;
        private int handlePid;

        private readonly List<Recipe> recipes = new();
        private readonly PriceCache priceCache = new();
        private DateTime nextAutoRefreshCheckUtc = DateTime.MinValue;

        // {Normalize(localizedName) → art-id}, built once per game session from BaseItemTypes.
        private Dictionary<string, string> nameToArtId = new(StringComparer.Ordinal);

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

            this.BuildNameToArtIfNeeded(panel);
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

                ParseNameAndCount(raw, out var count, out var name);
                this.nameToArtId.TryGetValue(name.Trim(), out var artId);
                this.recipes.Add(new Recipe(count, name, artId ?? string.Empty));
            }
        }

        // ── Reward art-id dictionary (localized name → language-independent art-id) ──────────

        // Build {Normalize(localizedName) → artId} from the game's BaseItemTypes table, once per
        // session (the table is loaded globally and stable until the game restarts). Reached via the
        // Expedition2Recipes table found by walking the open panel's pointer graph.
        private void BuildNameToArtIfNeeded(IntPtr panel)
        {
            if (this.nameToArtId.Count > 0) return;

            var handle = this.FindRecipeTableHandle(panel);
            if (handle == IntPtr.Zero) return;

            // Expedition2Recipes rows: handle+0x28 → vecObj{begin,end}.
            var recVec = this.ReadPtr(handle + TableRowsVectorOffset);
            var recBegin = this.ReadPtr(recVec);
            var recEnd = this.ReadPtr(recVec + 8);
            if (recBegin == IntPtr.Zero || (long)recEnd <= (long)recBegin) return;
            long recCount = ((long)recEnd - (long)recBegin) / RecipeStride;
            if (recCount <= 0 || recCount > 5000) return;

            // BaseItemTypes table object = first recipe's reward-FK table ptr (recipe+0x34), shared by all.
            IntPtr bitTable = IntPtr.Zero;
            for (long k = 0; k < recCount && bitTable == IntPtr.Zero; k++)
                bitTable = this.ReadPtr(recBegin + (nint)(k * RecipeStride) + RecipeRewardTableOffset);
            if (bitTable == IntPtr.Zero) return;

            var bitVec = this.ReadPtr(bitTable + TableRowsVectorOffset);
            var bitBegin = this.ReadPtr(bitVec);
            var bitEnd = this.ReadPtr(bitVec + 8);
            if (bitBegin == IntPtr.Zero || (long)bitEnd <= (long)bitBegin) return;
            long bitCount = ((long)bitEnd - (long)bitBegin) / BaseItemTypeStride;
            if (bitCount <= 0 || bitCount > 200000) return;

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (long j = 0; j < bitCount; j++)
            {
                var row = bitBegin + (nint)(j * BaseItemTypeStride);
                var name = this.ReadUtf16Z(this.ReadPtr(row + BaseItemTypeNameOffset), 64);
                if (name.Length < 2) continue;
                var vis = this.ReadPtr(row + BaseItemTypeVisualOffset);
                if (vis == IntPtr.Zero) continue;
                var art = ArtIdFromPath(this.ReadUtf16Z(this.ReadPtr(vis + VisualArtOffset), 128));
                if (art.Length == 0) continue;
                // Key by the RAW localized name (trimmed). NOT PriceCache.Normalize — that keeps only
                // a-z0-9 and would collapse every Cyrillic/CJK name to the empty string.
                dict[name.Trim()] = art; // name variants share an art-id; last wins
            }

            if (dict.Count > 0) this.nameToArtId = dict;
        }

        // Walk the open panel's pointer graph (BFS) to the loaded Expedition2Recipes dat-file handle:
        // a heap object whose +0x00 is an in-module vtable and whose +0x08 points to a path string
        // containing "Expedition2Recipes". The vtable gate keeps the (remote) string read off the
        // vast majority of nodes. Bounded by visited count + depth so it can't run away.
        private IntPtr FindRecipeTableHandle(IntPtr panel)
        {
            var seen = new HashSet<long> { (long)panel };
            var queue = new Queue<(IntPtr addr, int depth)>();
            queue.Enqueue((panel, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 40000)
            {
                var (addr, depth) = queue.Dequeue();
                visited++;

                if (IsExeAddr(this.ReadPtr(addr)))
                {
                    var pathPtr = this.ReadPtr(addr + DatPathOffset);
                    if (pathPtr != IntPtr.Zero)
                    {
                        var s = this.ReadUtf16Z(pathPtr, 80);
                        if (s.Contains("Expedition2Recipes", StringComparison.Ordinal))
                            return addr;
                    }
                }

                if (depth >= 7) continue;
                var buf = new byte[0x180];
                if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out var got)) continue;
                for (int o = 0; o + 8 <= got; o += 8)
                {
                    long v = BitConverter.ToInt64(buf, o);
                    if ((ulong)v < 0x10000 || (ulong)v > 0x7FFFFFFFFFFF) continue;
                    if (seen.Add(v)) queue.Enqueue(((IntPtr)v, depth + 1));
                }
            }
            return IntPtr.Zero;
        }

        // True for addresses inside a loaded module (exe/dll) — user-mode module region is ≥ ~0x7FF0…,
        // far above heap allocations (~0x000002…). Cheap gate for "looks like a vtable".
        private static bool IsExeAddr(IntPtr p) => (ulong)p >= 0x7FF000000000ul && (ulong)p < 0x800000000000ul;

        // Read a NUL-terminated UTF-16 string from a raw buffer pointer (the .dat string-column layout
        // — a direct char* into the file's string heap, not an MSVC std::wstring).
        private string ReadUtf16Z(IntPtr ptr, int maxChars)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            ulong u = (ulong)ptr;
            if (u < 0x10000 || u > 0x7FFFFFFFFFFF) return string.Empty;
            var buf = new byte[maxChars * 2];
            if (!ReadProcessMemory(this.processHandle, ptr, buf, (uint)buf.Length, out var read)) return string.Empty;
            int n = read / 2;
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++)
            {
                char c = (char)BitConverter.ToUInt16(buf, i * 2);
                if (c == '\0') break;
                sb.Append(c);
            }
            return sb.ToString();
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
                    // Display name priority (the ImGui font may not render the localized name):
                    //   1) poe.ninja English name (by art-id)
                    //   2) the art-id itself (readable ASCII, e.g. "ColdRune")
                    //   3) the in-game localized name (last resort)
                    string display;
                    if (!string.IsNullOrEmpty(r.ArtId)
                        && this.priceCache.TryGetNameByArtId(r.ArtId, out var enName)
                        && !string.IsNullOrEmpty(enName))
                    {
                        display = enName;
                    }
                    else if (!string.IsNullOrEmpty(r.ArtId))
                    {
                        display = r.ArtId;
                    }
                    else
                    {
                        display = r.Name;
                    }
                    ImGui.TextUnformatted(display);
                    ImGui.TableSetColumnIndex(2);
                    // Prefer the language-independent art-id match; fall back to the localized name
                    // (works on English clients / when the art-id dict couldn't be built).
                    bool havePrice =
                        (!string.IsNullOrEmpty(r.ArtId) && this.priceCache.TryGetPriceByArtId(r.ArtId, out var unit) && unit > 0)
                        || (this.priceCache.TryGetExaltedPrice(r.Name, out unit) && unit > 0);
                    if (havePrice)
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

        // The reward label embeds the quantity in a locale-dependent way:
        //   "<name> (<count>)"  — e.g. ru "Деталь доспеха (6)"
        //   "<count>x <name>"   — e.g. ko/en "6x 방어구 장인의 고철" / "6x Armourer's Scrap"
        // We strip whichever form is present so `name` is just the localized reward item name.
        private static void ParseNameAndCount(string raw, out int count, out string name)
        {
            count = 1;
            name = raw?.Trim() ?? string.Empty;
            if (name.Length == 0) return;

            // leading "<N>x " (count first)
            int i = 0;
            while (i < name.Length && char.IsDigit(name[i])) i++;
            if (i > 0 && i < name.Length && (name[i] == 'x' || name[i] == 'X'))
            {
                if (int.TryParse(name.AsSpan(0, i), out var c) && c > 0)
                {
                    count = c;
                    name = name[(i + 1)..].TrimStart();
                    return;
                }
            }

            // trailing "(<N>)" (count last)
            if (name[^1] == ')')
            {
                int open = name.LastIndexOf('(');
                if (open > 0)
                {
                    var inner = name.Substring(open + 1, name.Length - open - 2).Trim();
                    if (int.TryParse(inner, out var c) && c > 0)
                    {
                        count = c;
                        name = name[..open].TrimEnd();
                    }
                }
            }
        }

        // "Art/2DItems/Currency/CurrencyArmourQuality.dds" → "CurrencyArmourQuality" (poe.ninja art-id).
        private static string ArtIdFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int slash = path.LastIndexOf('/');
            var seg = slash >= 0 ? path[(slash + 1)..] : path;
            int dot = seg.IndexOf('.');
            return dot > 0 ? seg[..dot] : seg;
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
            // The name→art-id dict is built from the client's localized BaseItemTypes names, so it's
            // language-specific. Drop it on process change so it rebuilds (e.g. after a language switch).
            this.nameToArtId = new Dictionary<string, string>(StringComparer.Ordinal);
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

        private readonly record struct Recipe(int Count, string Name, string ArtId);
    }
}
