namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using GameHelper;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using ImGuiNET;
    using Newtonsoft.Json;

    // Monolith reward window. For each nearby runeshape monolith (the persistent
    // Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter device, which Radar also tracks),
    // resolves its anchor rune + hole position off the live RuneStation, filters the offline recipe
    // catalog (expedition2_recipes.json, built from game_dat/ by build_recipes_json.py) to the recipes
    // that monolith can roll, and prices each reward via poe.ninja.
    //
    // device→station path (no heap scan, persists out of the network bubble — docs/re-findings.md §6.10):
    //   device → StateMachine component (SM) → listener vector at SM+0x20 (begin/end)
    //          → for each node ptr N: station = *(N) − 0x98, verified by *(station+0x10) == device addr
    // anchor (docs §6.8/6.10):
    //   station+0x28 = rune row ptr; index = (rowPtr − runeTableBase)/0x6c (runeTableBase = *(*(station+0x30+0x28)))
    //   station+0x3c = anchor hole index (0-based)
    public sealed partial class RunecraftHelperCore
    {
        private const int StationOwnerOffset = 0x10;      // → device entity
        private const int StationAnchorRefOffset = 0x28;  // → Expedition2Runes row ptr
        private const int StationAnchorHolderOffset = 0x30;
        private const int StationHoleCountOffset = 0x38;   // → N (recipe hole count)
        private const int StationAnchorPosOffset = 0x3c;   // → anchor hole index (0..5)
        private const int StateMachineListenerVecOffset = 0x20;
        private const int StationListenerSubOffset = 0x98; // node[0] = station + 0x98
        private const int ExpeditionRuneStride = 0x6c;
        private const int RuneCount = 34;                  // Expedition2Runes rows 0..33

        private List<MonoRecipe> monolithRecipes = new();
        private Dictionary<int, string> runeNames = new();
        // (anchorRune, pos1based, size) → min area level at which that partial size is offered.
        // Built from Expedition2RunesWeights; gates which size<N recipes a monolith can roll.
        private Dictionary<long, int> partialMinLevel = new();
        private bool monolithLoadTried;

        private List<MonoView> monolithViews = new();
        private DateTime nextMonolithScanUtc = DateTime.MinValue;

        // ── recipe data (offline) ────────────────────────────────────────────
        private bool LoadMonolithData()
        {
            if (this.monolithRecipes.Count > 0) return true;
            if (this.monolithLoadTried) return false;
            this.monolithLoadTried = true;
            try
            {
                var path = Path.Join(this.DllDirectory, "expedition2_recipes.json");
                if (!File.Exists(path)) return false;
                var file = JsonConvert.DeserializeObject<MonoFile>(File.ReadAllText(path));
                if (file?.recipes == null) return false;
                this.monolithRecipes = file.recipes;
                this.runeNames = new Dictionary<int, string>();
                if (file.runes != null)
                    foreach (var kv in file.runes)
                        if (int.TryParse(kv.Key, out var k)) this.runeNames[k] = kv.Value;

                this.partialMinLevel = new Dictionary<long, int>();
                if (file.runeWeights != null)
                    foreach (var w in file.runeWeights)
                    {
                        long key = PartialKey(w.rune, w.pos, w.size);
                        if (!this.partialMinLevel.TryGetValue(key, out var cur) || w.minLevel < cur)
                            this.partialMinLevel[key] = w.minLevel;
                    }
                return this.monolithRecipes.Count > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunecraftHelper] recipe json load failed: {ex.Message}");
                return false;
            }
        }

        // ── window ───────────────────────────────────────────────────────────
        // Entry point: loads the catalog, rescans nearby monoliths on a timer, then draws whichever of
        // the two windows are enabled — the rewards list (ShowMonolithRewards) and/or the per-monolith
        // debug dump (ShowWindow, repurposed). Both read the same scanned this.monolithViews.
        private void DrawMonolithRewards()
        {
            if (!this.LoadMonolithData()) return;

            var now = DateTime.UtcNow;
            if (now >= this.nextMonolithScanUtc)
            {
                this.monolithViews = this.EnumerateMonoliths();
                this.nextMonolithScanUtc = now.AddMilliseconds(750);
            }

            if (this.Settings.ShowMonolithRewards) this.DrawMonolithRewardsWindow();
            if (this.Settings.ShowWindow) this.DrawMonolithDebugWindow();
        }

        private void DrawMonolithRewardsWindow()
        {
            if (this.monolithViews.Count == 0) return;

            // Auto-resize to content: the window height follows the number of monoliths and grows/shrinks
            // as recipe tables are expanded/collapsed. The constraint keeps width sane and caps height so a
            // long list can't run off-screen (it scrolls past the cap instead).
            ImGui.SetNextWindowSizeConstraints(new Vector2(260, 0), new Vector2(640, 900));
            if (ImGui.Begin("Monolith Rewards", ImGuiWindowFlags.AlwaysAutoResize))
            {
                float min = this.Settings.MonolithRewardsMinExalted;
                foreach (var v in this.monolithViews)
                {
                    double best = 0;
                    foreach (var c in v.Candidates)
                        if (c.Priced) best = Math.Max(best, c.UnitEx * c.Count);

                    string hdr = v.AnchorIdx >= 0
                        ? $"{v.AnchorName}  ·  hole {v.AnchorPos + 1}/{v.HoleCount}  ·  {v.Distance:F0}  ·  best {best:F0} ex###m{v.EntityId}"
                        : $"(anchor ?)  ·  {v.HoleCount} holes  ·  {v.Distance:F0}###m{v.EntityId}";

                    if (!ImGui.CollapsingHeader(hdr, ImGuiTreeNodeFlags.None))
                        continue;

                    if (v.AnchorIdx < 0)
                    {
                        ImGui.TextDisabled("  anchor not resolved (station unavailable)");
                        continue;
                    }

                    // Fixed table width gives the stretch column something to fill under AlwaysAutoResize
                    // (otherwise the window would collapse to the header-text width). Height 0 = auto.
                    if (ImGui.BeginTable($"t{v.EntityId}", 4,
                            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp,
                            new Vector2(430f, 0f)))
                    {
                        ImGui.TableSetupColumn("Reward", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("x", ImGuiTableColumnFlags.WidthFixed, 26f);
                        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 58f);
                        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 62f);
                        ImGui.TableHeadersRow();

                        int shown = 0;
                        foreach (var c in v.Candidates)
                        {
                            double total = c.UnitEx * c.Count;
                            if (c.Priced && total < min) continue;     // below threshold
                            if (!c.Priced && min > 0) continue;        // hide unpriced when filtering

                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            ImGui.Text(c.Reward);
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"[{c.Size}] {c.Runes}");
                            ImGui.TableSetColumnIndex(1);
                            ImGui.Text(c.Count.ToString());
                            ImGui.TableSetColumnIndex(2);
                            ImGui.Text(c.Priced ? c.UnitEx.ToString("F0") : "—");
                            ImGui.TableSetColumnIndex(3);
                            ImGui.Text(c.Priced ? total.ToString("F0") : "—");
                            shown++;
                        }

                        if (shown == 0)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableSetColumnIndex(0);
                            ImGui.TextDisabled("(nothing above threshold)");
                        }

                        ImGui.EndTable();
                    }
                }
            }

            ImGui.End();
        }

        // ── debug window (repurposes the "Show debug list window" toggle) ──────
        private int monolithDebugSel;

        // Lets a tester pick a nearby monolith and see every value the offer rule consumes, so a
        // game-vs-plugin recipe mismatch can be reported precisely. "gate" = why a recipe is offered:
        // N (size==hole count, always) or RW (partial enabled by Expedition2RunesWeights).
        private void DrawMonolithDebugWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(680, 460), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Monolith Debug###RunecraftMonolithDebug"))
            {
                ImGui.End();
                return;
            }

            if (this.monolithViews.Count == 0)
            {
                ImGui.TextDisabled("No monoliths detected in this area.");
                ImGui.End();
                return;
            }

            var labels = new string[this.monolithViews.Count];
            for (int i = 0; i < labels.Length; i++)
            {
                var mv = this.monolithViews[i];
                labels[i] = mv.AnchorIdx >= 0
                    ? $"{mv.AnchorName}  hole {mv.AnchorPos + 1}/{mv.HoleCount}  ({mv.Distance:F0})"
                    : $"(anchor ?)  {mv.HoleCount}h  ({mv.Distance:F0})";
            }
            if (this.monolithDebugSel < 0 || this.monolithDebugSel >= labels.Length) this.monolithDebugSel = 0;
            ImGui.SetNextItemWidth(420f);
            ImGui.Combo("Monolith", ref this.monolithDebugSel, labels, labels.Length);

            var v = this.monolithViews[this.monolithDebugSel];
            var red = new Vector4(1f, 0.45f, 0.45f, 1f);
            var grey = new Vector4(0.6f, 0.6f, 0.6f, 1f);

            ImGui.Separator();
            if (v.AnchorIdx < 0)
            {
                ImGui.TextColored(red, "Anchor not resolved — no recipes.");
                if (!string.IsNullOrEmpty(v.StationDiag))
                    ImGui.TextColored(red, $"  why: {v.StationDiag}");
            }
            else
                ImGui.Text($"Anchor: {v.AnchorName} (idx {v.AnchorIdx})    p={v.AnchorPos}  (hole {v.AnchorPos + 1})");

            // N: station +0x38 vs StateMachine "sockets" — flag the under-read case.
            if (v.SocketsState >= 0 && v.SocketsState != v.HoleCount)
                ImGui.TextColored(red, $"N = {v.HoleCount}  (station +0x38)    sockets state = {v.SocketsState}   <- differ");
            else
                ImGui.Text($"N = {v.HoleCount}    sockets state = {v.SocketsState}");

            ImGui.Text($"Area level: {v.AreaLevel}");
            ImGui.TextColored(grey, $"device 0x{v.EntityId:X}   station 0x{v.StationAddr:X}   +0x40={FmtI(v.Field40)}  +0x44={FmtI(v.Field44)}");
            if (!string.IsNullOrEmpty(v.SmStates))
                ImGui.TextColored(grey, $"SM states: {v.SmStates}");

            if (ImGui.Button("Copy report"))
                ImGui.SetClipboardText(BuildDebugReport(v));
            ImGui.SameLine();
            ImGui.TextDisabled($"{v.Candidates.Count} recipe(s) offered");

            ImGui.Separator();
            if (ImGui.BeginTable("mdbg", 8,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("row", ImGuiTableColumnFlags.WidthFixed, 44f);
                ImGui.TableSetupColumn("sz", ImGuiTableColumnFlags.WidthFixed, 26f);
                ImGui.TableSetupColumn("gate", ImGuiTableColumnFlags.WidthFixed, 40f);
                ImGui.TableSetupColumn("cat", ImGuiTableColumnFlags.WidthFixed, 28f);
                ImGui.TableSetupColumn("reward", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("FK / Id", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("lvl", ImGuiTableColumnFlags.WidthFixed, 56f);
                ImGui.TableSetupColumn("holes (anchor in [])", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var c in v.Candidates)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.Text(c.Row.ToString());
                    ImGui.TableSetColumnIndex(1); ImGui.Text(c.Size.ToString());
                    ImGui.TableSetColumnIndex(2); ImGui.Text(c.Full ? "N" : "RW");
                    ImGui.TableSetColumnIndex(3); ImGui.Text(c.Category.ToString());
                    ImGui.TableSetColumnIndex(4); ImGui.Text(c.Reward);
                    ImGui.TableSetColumnIndex(5); ImGui.TextColored(grey, $"{c.RewardIdx} / {c.RewardId}");
                    ImGui.TableSetColumnIndex(6); ImGui.Text($"{c.MinLevel}-{c.MaxLevel}");
                    ImGui.TableSetColumnIndex(7); ImGui.Text(MarkAnchor(c.Runes, v.AnchorPos));
                }

                if (v.Candidates.Count == 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextDisabled("(no recipes)");
                }

                ImGui.EndTable();
            }

            ImGui.End();
        }

        private static string FmtI(int x) => x == int.MinValue ? "?" : x.ToString();

        // Mark the anchor hole in a " · "-joined rune list: anchor at pos 1 of "a · b · c" → "a · [b] · c".
        private static string MarkAnchor(string runes, int pos)
        {
            if (string.IsNullOrEmpty(runes) || pos < 0) return runes;
            var parts = runes.Split(" · ");
            if (pos >= parts.Length) return runes;
            parts[pos] = "[" + parts[pos] + "]";
            return string.Join(" · ", parts);
        }

        // Discord-pasteable plain-text dump of the selected monolith.
        private static string BuildDebugReport(MonoView v)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Monolith: {v.AnchorName} (idx {v.AnchorIdx})  p={v.AnchorPos} hole{v.AnchorPos + 1}  " +
                          $"N={v.HoleCount} (sockets={v.SocketsState})  areaLvl={v.AreaLevel}  +0x40={FmtI(v.Field40)} +0x44={FmtI(v.Field44)}");
            sb.AppendLine($"device 0x{v.EntityId:X}  station 0x{v.StationAddr:X}");
            if (!string.IsNullOrEmpty(v.SmStates))
                sb.AppendLine($"SM states: {v.SmStates}");
            if (v.AnchorIdx < 0 && !string.IsNullOrEmpty(v.StationDiag))
                sb.AppendLine($"resolve failed: {v.StationDiag}");
            sb.AppendLine($"offered {v.Candidates.Count}:");
            foreach (var c in v.Candidates)
                sb.AppendLine($"  row{c.Row} size{c.Size} [{(c.Full ? "N" : "RW")}] cat{c.Category} " +
                              $"{c.Reward} (FK{c.RewardIdx} {c.RewardId}) lvl{c.MinLevel}-{c.MaxLevel} | {MarkAnchor(c.Runes, v.AnchorPos)}");
            return sb.ToString();
        }

        // ── enumeration / resolution ──────────────────────────────────────────
        private List<MonoView> EnumerateMonoliths()
        {
            var list = new List<MonoView>();
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (area == null) return list;
            int areaLevel = area.CurrentAreaLevel;

            Vector2 pg = default;
            bool havePlayer = false;
            if (area.Player != null && area.Player.TryGetComponent<Render>(out var ppr))
            {
                pg = new Vector2(ppr.GridPosition.X, ppr.GridPosition.Y);
                havePlayer = true;
            }

            foreach (var kv in area.AwakeEntities)
            {
                var e = kv.Value;
                // Validity gate: IsValid catches devices removed from the area; EntityState weeds out
                // unresolved/dead. NOTE: collecting a monolith does NOT clear these (the device persists
                // with IsValid + Life 100/100 — docs §6.11); the "collected" signal is the StateMachine
                // "activated" state checked below.
                if (e == null || !e.IsValid || e.EntityState == EntityStates.Useless) continue;
                var path = e.Path;
                if (string.IsNullOrEmpty(path) ||
                    path.IndexOf(MonolithDevicePath, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!e.TryGetComponent<StateMachine>(out var sm)) continue;

                var v = new MonoView { EntityId = e.Address.ToInt64(), AreaLevel = areaLevel };

                // Collected gate: the device persists (IsValid stays true, Life 100/100) after the player
                // collects a monolith, so the "still available" signal is its StateMachine "activated" state.
                // Observed values (live, 2026-06-15, docs §6.11): 0 = dormant/out of range, 1 = available,
                // 7 = collected. Hide ONLY the collected (==7) ones — 0 and 1 are both live monoliths.
                bool collected = false;
                var smDump = new System.Text.StringBuilder();
                foreach (var s in sm.States)
                {
                    if (smDump.Length > 0) smDump.Append(", ");
                    smDump.Append(s.Name).Append('=').Append(s.Value);
                    if (string.Equals(s.Name, "sockets", StringComparison.OrdinalIgnoreCase))
                    {
                        v.SocketsState = (int)s.Value;
                        v.HoleCount = (int)s.Value;
                    }
                    else if (string.Equals(s.Name, "activated", StringComparison.OrdinalIgnoreCase))
                        collected = s.Value == 7;
                }
                v.SmStates = smDump.ToString();
                if (collected) continue;

                if (havePlayer && e.TryGetComponent<Render>(out var r))
                    v.Distance = Vector2.Distance(pg, new Vector2(r.GridPosition.X, r.GridPosition.Y));

                if (this.TryResolveStation(sm, e.Address, out var station, out var sdiag))
                {
                    v.StationAddr = station.ToInt64();
                    if (this.TryReadAnchor(station, out var aidx, out var apos))
                    {
                        v.AnchorIdx = aidx;
                        v.AnchorPos = apos;
                        v.AnchorName = this.runeNames.TryGetValue(aidx, out var nm) ? nm : $"#{aidx}";
                        // N (hole count) authoritative from the station (+0x38) — this is what the in-game
                        // offer builder uses. The StateMachine "sockets" state can read LOW (observed 6 while
                        // the recipe N is 7), which would drop the largest recipes; override it when sane.
                        if (this.TryReadI32(station + StationHoleCountOffset, out var nHoles) && nHoles > 0 && nHoles <= 16)
                            v.HoleCount = nHoles;
                        if (this.TryReadI32(station + 0x40, out var f40)) v.Field40 = f40;
                        if (this.TryReadI32(station + 0x44, out var f44)) v.Field44 = f44;
                        this.BuildCandidates(v, areaLevel);
                    }
                    else
                    {
                        v.StationDiag = "station resolved but anchor read failed (rune table base / row ptr)";
                    }
                }
                else
                {
                    v.StationDiag = sdiag;
                }

                list.Add(v);
            }

            list.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return list;
        }

        // Walk the device StateMachine's listener vector to the RuneStation that registered on it.
        // `diag` records the failure step so community debug reports can pinpoint why big/odd monoliths
        // don't resolve (empty on success).
        private bool TryResolveStation(StateMachine sm, IntPtr deviceAddr, out IntPtr station, out string diag)
        {
            station = IntPtr.Zero;
            diag = string.Empty;
            if (sm == null || sm.Address == IntPtr.Zero) { diag = "SM component null"; return false; }
            if (!this.TryReadStdVector(sm.Address + StateMachineListenerVecOffset, out var first, out var last))
            {
                diag = "SM listener vector (+0x20) unreadable";
                return false;
            }

            long n = ((long)last - (long)first) / 8;
            if (n <= 0 || n > 256)
            {
                diag = $"listener vector size out of range (n={n})";
                return false;
            }

            for (long i = 0; i < n; i++)
            {
                var node = this.ReadPtr(first + (nint)(i * 8));
                if (node == IntPtr.Zero) continue;
                var sub = this.ReadPtr(node);            // = candidate station + 0x98
                if (sub == IntPtr.Zero) continue;
                var cand = sub - StationListenerSubOffset;
                if (this.ReadPtr(cand + StationOwnerOffset) == deviceAddr)
                {
                    station = cand;
                    return true;
                }
            }

            diag = $"no listener matched device ({n} checked)";

            return false;
        }

        // anchor rune index = (station+0x28 row ptr − table base)/0x6c; hole pos = station+0x3c.
        private bool TryReadAnchor(IntPtr station, out int idx, out int pos)
        {
            idx = -1;
            pos = -1;
            if (station == IntPtr.Zero) return false;

            this.TryReadI32(station + StationAnchorPosOffset, out pos);

            var rowPtr = this.ReadPtr(station + StationAnchorRefOffset);
            if (rowPtr == IntPtr.Zero) return false;

            // Rune-table base must be computed per station: the Expedition2Runes row array is
            // re-instantiated per area, so its base differs every map. Caching it across areas
            // makes rowPtr − base go negative on the next map (mirrors FUN_14179ed70 exactly).
            var holder = this.ReadPtr(station + StationAnchorHolderOffset);
            if (holder == IntPtr.Zero) return false;
            var p1 = this.ReadPtr(holder + 0x28);
            if (p1 == IntPtr.Zero) return false;
            long tableBase = this.ReadPtr(p1).ToInt64();
            if (tableBase == 0) return false;
            long delta = rowPtr.ToInt64() - tableBase;
            if (delta < 0 || delta % ExpeditionRuneStride != 0) return false;
            long i = delta / ExpeditionRuneStride;
            if (i < 0 || i >= RuneCount) return false;

            idx = (int)i;
            return true;
        }

        // Recipes a monolith (anchor rune at hole p, N holes, current area level) can roll.
        // Rule decoded from the in-game offer builder FUN_141e32ab0 (docs/monolith-partial-recipes.md):
        //   runeIdx[p] == anchor  AND  size <= N
        //   AND  minLevel <= areaLevel <= maxLevel
        //   AND  ( size == N   OR   Expedition2RunesWeights permits (anchor, p+1, size) at this level )
        // There is NO category/theme filter — the "theme" is emergent from which rune sits at which hole.
        private void BuildCandidates(MonoView v, int areaLevel)
        {
            if (v.AnchorIdx < 0 || v.AnchorPos < 0 || v.HoleCount <= 0) return;
            foreach (var rec in this.monolithRecipes)
            {
                if (rec.runeIdx == null || rec.runeIdx.Count <= v.AnchorPos) continue;
                if (rec.size > v.HoleCount) continue;
                if (rec.runeIdx[v.AnchorPos] != v.AnchorIdx) continue;
                // Area-level gate: tiered rewards (e.g. Thaumaturgic Flux Levels 5..18) carry a
                // [minLevel, maxLevel] band in the .dat and only the tier covering the current area
                // level is offered. Untiered recipes use 1..100, so this never drops them.
                if (areaLevel > 0 && rec.maxLevel > 0 &&
                    (areaLevel < rec.minLevel || areaLevel > rec.maxLevel)) continue;
                // Partial-size gate: size==N is always offered; size<N only when Expedition2RunesWeights
                // has a (rune, position, size) row whose minLevel the area level meets.
                if (rec.size != v.HoleCount &&
                    !this.IsPartialAllowed(v.AnchorIdx, v.AnchorPos, rec.size, areaLevel)) continue;

                var c = new MonoCand
                {
                    Size = rec.size,
                    Count = Math.Max(1, rec.rewardCount),
                    Runes = rec.runes != null ? string.Join(" · ", rec.runes) : string.Empty,
                    Row = rec.row,
                    Category = rec.category,
                    RewardIdx = rec.reward?.idx ?? -1,
                    RewardId = rec.reward?.id ?? string.Empty,
                    MinLevel = rec.minLevel,
                    MaxLevel = rec.maxLevel,
                    Full = rec.size == v.HoleCount,
                };

                if (rec.reward != null && !string.IsNullOrEmpty(rec.reward.name))
                {
                    c.Reward = rec.reward.name;
                    if (this.priceCache.TryGetExaltedPrice(rec.reward.name, out var u) && u > 0)
                    {
                        c.UnitEx = u;
                        c.Priced = true;
                    }
                }
                else
                {
                    c.Reward = string.IsNullOrEmpty(rec.description) ? $"(unique) {rec.id}" : rec.description;
                }

                v.Candidates.Add(c);
            }

            v.Candidates.Sort((a, b) => (b.UnitEx * b.Count).CompareTo(a.UnitEx * a.Count));
        }

        // True if Expedition2RunesWeights enables a partial recipe of `size` for anchor `idx` at hole
        // `pos` (0-based) given the current area level. areaLevel<=0 means level unknown → don't gate on it.
        private bool IsPartialAllowed(int idx, int pos, int size, int areaLevel)
        {
            if (!this.partialMinLevel.TryGetValue(PartialKey(idx, pos + 1, size), out var minL)) return false;
            return areaLevel <= 0 || areaLevel >= minL;
        }

        private static long PartialKey(int rune, int pos1Based, int size)
            => ((long)rune << 16) | ((long)pos1Based << 8) | (uint)size;

        private bool TryReadI32(IntPtr addr, out int val)
        {
            val = 0;
            if (addr == IntPtr.Zero) return false;
            var buf = new byte[4];
            if (!ReadProcessMemory(this.processHandle, addr, buf, (uint)buf.Length, out _)) return false;
            val = BitConverter.ToInt32(buf, 0);
            return true;
        }

        // ── models ────────────────────────────────────────────────────────────
        private sealed class MonoFile
        {
            public Dictionary<string, string>? runes { get; set; }
            public List<MonoRecipe>? recipes { get; set; }
            public List<RuneWeight>? runeWeights { get; set; }
        }

        private sealed class RuneWeight
        {
            public int rune { get; set; }
            public int pos { get; set; }   // 1-based anchor position
            public int size { get; set; }
            public int minLevel { get; set; }
        }

        private sealed class MonoRecipe
        {
            public int row { get; set; }
            public string id { get; set; } = string.Empty;
            public int size { get; set; }
            public int category { get; set; }
            public List<int>? runeIdx { get; set; }
            public List<string>? runes { get; set; }
            public MonoReward? reward { get; set; }
            public int rewardCount { get; set; }
            public string description { get; set; } = string.Empty;
            public int minLevel { get; set; }
            public int maxLevel { get; set; }
        }

        private sealed class MonoReward
        {
            public int idx { get; set; }
            public string id { get; set; } = string.Empty;
            public string name { get; set; } = string.Empty;
        }

        private sealed class MonoView
        {
            public long EntityId;          // device entity address
            public long StationAddr;       // resolved RuneStation address (0 if unresolved)
            public float Distance;
            public int HoleCount;          // N — authoritative, from station +0x38
            public int SocketsState = -1;  // StateMachine "sockets" value (for debug; can under-read N)
            public int AreaLevel;
            public int Field40 = int.MinValue; // station +0x40 (debug; not used for offers)
            public int Field44 = int.MinValue; // station +0x44 (debug)
            public int AnchorIdx = -1;
            public int AnchorPos = -1;
            public string AnchorName = "?";
            public string StationDiag = string.Empty; // why station/anchor failed to resolve (debug)
            public string SmStates = string.Empty;     // all StateMachine states "name=value" (debug)
            public List<MonoCand> Candidates = new();
        }

        private sealed class MonoCand
        {
            public string Reward = string.Empty;
            public int Count;
            public int Size;
            public double UnitEx;
            public bool Priced;
            public string Runes = string.Empty;
            // debug detail
            public int Row;
            public int Category;
            public int RewardIdx = -1;
            public string RewardId = string.Empty;
            public int MinLevel;
            public int MaxLevel;
            public bool Full;             // true = size==N (always offered); false = partial via RunesWeights
        }
    }
}
