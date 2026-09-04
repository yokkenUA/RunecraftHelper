namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using ImGuiNET;

    // Rune-chain (proliferation) valuation.
    //
    // WHAT THE GOLD SOCKET MEANS (official 0.5.4 patch notes): "Remnants now randomly choose which Rune
    // SLOT will propagate to further Monsters in Expeditions and Grand Expeditions… Added a highlight to
    // the Rune that will be propagated upon completing a Remnant in the Runeshape Recipes list." Plus the
    // base chain rule: a remnant's modifiers apply only to monsters unearthed by the explosive placed ON
    // it or by a LATER explosive. Buffing those monsters raises THEIR drops (Opulent = "Increases Monster
    // Rarity"; 0.5.3 doubled Runic Modifier magnitudes), so the propagated rune is worth real currency.
    //
    // THE KEY THAT MAKES THIS PREDICTABLE: station+0x40 holds socket POSITIONS, not runes — the frame is a
    // property of the monolith, chosen before the player touches anything. Since a socket position IS the
    // rune's index inside a recipe, for every offered recipe we already know what it would propagate:
    //     propagated(recipe) = recipe.runeIdx[glowSocket]
    // So we can RECOMMEND a recipe rather than merely report one after the fact.
    //
    // VALUE MODEL (the two sources ADD UP — picking a recipe grants both):
    //     total(recipe)  = rewardEx(recipe) + chainEx(recipe)
    //     chainEx(recipe) = baseMonsterEx × downstreamPacks × (effMult(propagated) − 1)
    //     effMult(rune)   = 1 + (lootMult[rune] − 1) × (powerInChain ? powerFactor : 1)
    //     downstreamPacks = recipe.size (its own waves — each extra runeshape adds one)
    //                     + charges still unplaced (every later charge raises ≈ one more pack)
    //
    // Magnitudes are server-side and absent from the .dat, so lootMult is calibrated, not read. Full
    // write-up incl. sources: obsidian poe2/mehanics/expedition-rune-chain.md.
    public sealed partial class RunecraftHelperCore
    {
        // Tier-list defaults (community list: Opulent > Bond > Power > Time > Death > Rebirth). LootMult
        // below 1.0 encodes a NET COST: Oath seeds immortal, loot-less waves and — because the chain waits
        // for the previous pack to die — drags the whole run; Wisdom only grants experience and burns the
        // slot a good rune could have used. Runes absent from the table are worth 1.0 (pure danger, no
        // loot effect) — see RuneEffects / obsidian poe2/expedition-runes for what each one does.
        private static readonly RuneChainEntry[] DefaultRuneChainWeights =
        {
            new RuneChainEntry { Rune = "Opulent", LootMult = 1.35f },
            new RuneChainEntry { Rune = "Bond", LootMult = 1.25f },
            new RuneChainEntry { Rune = "Power", LootMult = 1.30f },
            new RuneChainEntry { Rune = "Time", LootMult = 1.18f },
            new RuneChainEntry { Rune = "Death", LootMult = 1.15f },
            new RuneChainEntry { Rune = "Rebirth", LootMult = 1.10f },
            new RuneChainEntry { Rune = "Wisdom", LootMult = 0.95f, Avoid = true },
            new RuneChainEntry { Rune = "Oath", LootMult = 0.75f, Avoid = true },
            new RuneChainEntry { Rune = "Bait", LootMult = 1.00f, Avoid = true },
        };

        // Add any missing default row; never touches an existing one, so a re-tuned LootMult survives.
        private void EnsureRuneChainDefaults()
        {
            foreach (var d in DefaultRuneChainWeights)
                if (!this.Settings.RuneChainWeights.Exists(
                        e => string.Equals(e.Rune, d.Rune, StringComparison.Ordinal)))
                    this.Settings.RuneChainWeights.Add(
                        new RuneChainEntry { Rune = d.Rune, LootMult = d.LootMult, Avoid = d.Avoid });
        }

        private RuneChainEntry? RuneChainEntryFor(string rune) =>
            string.IsNullOrEmpty(rune) ? null
            : this.Settings.RuneChainWeights.Find(e => string.Equals(e.Rune, rune, StringComparison.Ordinal));

        // Does this monolith actually SHOW a gold frame? The panel builder
        // (Expedition2_PopulateCombinationsPanel, 0.5.4FHF) hands the row widgets the +0x40 index vector
        // ONLY when (station+0x58 − 1) < 2 — recipe-mode 1 or 2 — and when area stat 0x69dd is 0;
        // otherwise it passes an EMPTY vector, so mode 0 (standalone "additional") and mode 3
        // (anchor-less / "unique") render no frame at all even though +0x40 is populated in memory.
        // A non-empty +0x40 therefore does NOT imply a visible propagating rune.
        // The area-stat half of the gate is NOT implemented: it lives on a different stats container
        // (AreaInstance+0x130, not the +0x158/+0x160 map-mod vector we already read) whose layout we
        // haven't mapped — so a zone carrying that stat would still get a chain value here.
        private static bool RuneChainHighlightActive(MonoView v) => v.RecipeMode == 1 || v.RecipeMode == 2;

        // Effective downstream loot multiplier of propagating `rune`, with the Power empowerment applied.
        // Power multiplies the UPLIFT, not the multiplier: a 1.35 rune at powerFactor 1.5 becomes
        // 1 + 0.35×1.5 = 1.525. A rune we have no entry for is neutral (1.0).
        // `empowered` comes from the station itself (+0x5d, the same flag the panel uses to draw the
        // empowered rune art) — the manual setting only forces it on.
        private double RuneChainEffMult(string rune, bool empowered)
        {
            var e = this.RuneChainEntryFor(rune);
            if (e == null) return 1.0;
            bool power = empowered || this.Settings.RuneChainPowerInChain;
            double k = power ? Math.Max(1f, this.Settings.RuneChainPowerFactor) : 1.0;
            return 1.0 + ((e.LootMult - 1.0) * k);
        }

        // Charges not yet placed — every one of them raises ≈ one more pack that a rune propagated now
        // would still reach. Controller-authoritative when readable (see TryReadExpeditionCounts), else the
        // manual total minus entity-counted placements. 0 when we have no usable count at all (campaign
        // monolith, controller unreadable) — the chain value then rests on the recipe's own waves only.
        private int RuneChainChargesLeft()
        {
            int total = this.expCtrlResolved ? this.expTotalCharges : this.Settings.ExpTotalChargesManual;
            int placed = this.expCtrlResolved ? this.expPlacedFromCtrl : this.expPlacedFromEntities;
            if (total <= 0) return 0;
            return Math.Clamp(total - placed, 0, 64);
        }

        // Packs a rune propagated by THIS recipe would still buff: the monolith's own waves (each extra
        // runeshape in the combination adds a wave — 0.5.0 patch notes) plus one per unplaced charge.
        private double RuneChainDownstreamPacks(int recipeSize) =>
            Math.Max(1, recipeSize) + this.RuneChainChargesLeft();

        // Ex-equivalent value of the rune `recipe` would drop into the gold socket. Negative for runes
        // whose LootMult is below 1 (Oath/Wisdom) — propagating them costs value, so the recommender
        // pushes those rows down instead of pretending they are neutral.
        private double RuneChainEx(string rune, int recipeSize, bool empowered)
        {
            if (string.IsNullOrEmpty(rune)) return 0.0;
            double uplift = this.RuneChainEffMult(rune, empowered) - 1.0;
            if (Math.Abs(uplift) < 1e-9) return 0.0;
            return this.Settings.RuneChainBaseMonsterEx * this.RuneChainDownstreamPacks(recipeSize) * uplift;
        }

        // The rune a recipe would propagate on the monolith whose panel is open: runeIdx[glowSocket].
        // Several gold sockets are possible in our read of station+0x40 (officially one — the second
        // element is unverified, see the note in obsidian), so take the best-valued of them.
        private string RuneChainPropagatedRune(MonoView v, MonoRecipe rec)
        {
            if (rec.runeIdx == null || v.GlowSockets.Count == 0) return string.Empty;
            string best = string.Empty;
            double bestMult = double.NegativeInfinity;
            foreach (var g in v.GlowSockets)
            {
                if (g < 0 || g >= rec.runeIdx.Count) continue;
                var name = this.RuneNameByIndex(rec.runeIdx[g]);
                if (name == null) continue;
                double m = this.RuneChainEffMult(name, v.RunesEmpowered);
                if (m > bestMult) { bestMult = m; best = name; }
            }

            return best;
        }

        // Panel-facing: for a visible recipe row (matched to the offline catalog by Id), WHICH rune it would
        // propagate and how good that rune is. `mult` is the rune's effective loot multiplier — the row-ranking
        // key for the "best rune" frame, deliberately NOT the row's chainEx: chainEx also scales with the
        // recipe's size, so ranking by it would call a weak rune on a long combination "the best rune".
        // Returns false when the chain feature is off, no monolith panel is open, that monolith renders no
        // gold frame (mode 0/3), or the recipe puts nothing on a gold socket.
        private bool TryGetPropagatedRuneForRecipeId(string recipeId, out string rune, out double mult)
        {
            rune = string.Empty;
            mult = 1.0;
            if (!this.Settings.RuneChainEnabled || string.IsNullOrEmpty(recipeId)) return false;
            var open = this.monolithViews.Find(v => v.PanelOpen);
            if (open == null || open.GlowSockets.Count == 0) return false;
            if (!RuneChainHighlightActive(open)) return false;   // no gold frame here ⇒ nothing propagates
            var rec = this.monolithRecipes.Find(m => string.Equals(m.id, recipeId, StringComparison.Ordinal));
            if (rec == null) return false;

            rune = this.RuneChainPropagatedRune(open, rec);
            if (string.IsNullOrEmpty(rune)) return false;
            mult = this.RuneChainEffMult(rune, open.RunesEmpowered);
            return true;
        }

        // Monolith-facing: the offered recipe with the highest JOINT value (reward + chain) and that value.
        // A joint max, not the sum of two separate maxima — the player picks ONE recipe, so the expensive
        // reward and the strong rune usually cannot both be had.
        private void RuneChainResolveBest(MonoView v)
        {
            v.ChainBestEx = 0.0;
            v.ChainBestRune = string.Empty;
            v.ChainBestRecipeId = string.Empty;
            v.BestCombined = v.Best;
            if (!this.Settings.RuneChainEnabled || v.GlowSockets.Count == 0 || v.Offered.Count == 0) return;
            if (!RuneChainHighlightActive(v)) return;   // mode 0/3 render no frame ⇒ no chain value

            double bestJoint = double.NegativeInfinity;
            foreach (var rec in v.Offered)
            {
                var rune = this.RuneChainPropagatedRune(v, rec);
                double chain = string.IsNullOrEmpty(rune) ? 0.0 : this.RuneChainEx(rune, rec.size, v.RunesEmpowered);

                double reward = 0.0;
                if (rec.reward != null && !string.IsNullOrEmpty(rec.reward.name) &&
                    this.priceCache.TryGetExaltedPrice(rec.reward.name, out var unit) && unit > 0)
                    reward = unit * Math.Max(1, rec.rewardCount);

                double joint = reward + chain;
                if (joint > bestJoint)
                {
                    bestJoint = joint;
                    v.ChainBestEx = chain;
                    v.ChainBestRune = rune;
                    v.ChainBestRecipeId = rec.id;
                }
            }

            if (bestJoint > double.NegativeInfinity) v.BestCombined = bestJoint;
        }

        // ── settings UI ──────────────────────────────────────────────────────
        // Red — a rune it costs value to propagate. Vector4 (ImGui.TextColored takes RGBA floats, unlike the
        // packed uint colours the draw lists use).
        private static readonly Vector4 ColorAvoidRuneText = new(1f, 0.42f, 0.42f, 1f);

        private void DrawRuneChainSection()
        {
            // One collapsible block for the whole proliferation feature: the master toggle AND everything it
            // switches on. Collapsed, the 34-row rune weight table stops dominating the planner tab.
            if (!ImGui.CollapsingHeader(this.Loc.Title("runechain.header", "Rune settings", "rh_runechain_header")))
                return;

            ImGui.Spacing();
            ImGui.Indent();
            this.DrawRuneChainBody();
            ImGui.Unindent();
        }

        private void DrawRuneChainBody()
        {
            var s = this.Settings;

            ImGui.Checkbox(this.L("runechain.enable", "Value the rune chain (proliferation)"), ref s.RuneChainEnabled);
            if (!s.RuneChainEnabled) return;
            this.EnsureRuneChainDefaults();

            ImGui.TextDisabled(this.L("runechain.enable_hint",
                "The gold-framed socket marks the rune that propagates to every pack unearthed LATER in\n" +
                "the chain, and buffing those monsters raises their drops. In the panel the GREEN frame\n" +
                "keeps meaning \"most valuable reward\"; an AMBER frame around a rune name marks the\n" +
                "strongest rune this monolith can propagate. Two separate calls, not one merged number."));

            ImGui.InputFloat(this.L("runechain.base_ex", "Loot per monster pack (ex)"), ref s.RuneChainBaseMonsterEx, 0.5f, 2f, "%.2f");
            if (s.RuneChainBaseMonsterEx < 0f) s.RuneChainBaseMonsterEx = 0f;
            ImGui.TextDisabled(this.L("runechain.base_ex_hint",
                "Expected drop value of ONE pack of Runic monsters. The chain value scales linearly with\n" +
                "this, so it is the main calibration knob — measure it, don't trust the default."));

            ImGui.SliderFloat(this.L("runechain.power_factor", "Power empowerment factor"), ref s.RuneChainPowerFactor, 1f, 4f, "%.2f");
            ImGui.TextDisabled(this.L("runechain.power_factor_hint",
                "A propagated Power rune empowers the other runes in the chain (official 0.5.4 fix). Whether\n" +
                "Power is live is READ per monolith from the station (the same flag the game uses to draw the\n" +
                "empowered rune art), so this only says by how much it multiplies their uplift."));
            ImGui.Checkbox(this.L("runechain.power_in_chain", "Force Power empowerment (override)"), ref s.RuneChainPowerInChain);

            ImGui.Checkbox(this.L("runechain.affects_route", "Let the chain value steer the route"), ref s.RuneChainAffectsRoute);
            if (s.RuneChainAffectsRoute)
                ImGui.TextDisabled(this.L("runechain.affects_route_hint",
                    "Adds each monolith's best achievable chain value to its route weight. This is an UPPER\n" +
                    "BOUND — the real value depends on how many packs come after that monolith, which is\n" +
                    "only known once the order is fixed. Off keeps the router exactly as it is today."));

            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextDisabled(this.L("runechain.table_hint",
                "Loot multiplier per propagated rune. 1.00 = no loot effect (pure danger). Below 1.00 = a\n" +
                "net cost (Oath seeds immortal loot-less waves; Wisdom only grants experience). Magnitudes\n" +
                "are server-side, so these are estimates ordered by the community tier list."));
            this.DrawRuneChainTable();
        }

        private void DrawRuneChainTable()
        {
            var rows = this.Settings.RuneChainWeights;
            string? removeKey = null;
            if (ImGui.BeginTable("runechain", 5,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                    new Vector2(0f, Math.Min(rows.Count + 1, 12) * ImGui.GetFrameHeightWithSpacing())))
            {
                ImGui.TableSetupColumn("Rune", ImGuiTableColumnFlags.WidthFixed, 92f);
                ImGui.TableSetupColumn("Loot ×", ImGuiTableColumnFlags.WidthFixed, 76f);
                ImGui.TableSetupColumn("Avoid", ImGuiTableColumnFlags.WidthFixed, 44f);
                ImGui.TableSetupColumn("Effect", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 22f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var e in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.PushID(e.Rune);

                    ImGui.TableSetColumnIndex(0);
                    ImGui.AlignTextToFramePadding();
                    if (e.Avoid) ImGui.TextColored(ColorAvoidRuneText, e.Rune);
                    else ImGui.TextUnformatted(e.Rune);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.SetNextItemWidth(70f);
                    float m = e.LootMult;
                    if (ImGui.InputFloat("##m", ref m, 0f, 0f, "%.2f"))
                        e.LootMult = Math.Clamp(m, 0f, 10f);

                    ImGui.TableSetColumnIndex(2);
                    bool av = e.Avoid;
                    if (ImGui.Checkbox("##av", ref av)) e.Avoid = av;

                    ImGui.TableSetColumnIndex(3);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled(RuneEffects.TryGetValue(e.Rune, out var eff) ? eff : string.Empty);

                    ImGui.TableSetColumnIndex(4);
                    if (Array.FindIndex(DefaultRuneChainWeights,
                            d => string.Equals(d.Rune, e.Rune, StringComparison.Ordinal)) < 0)
                    {
                        if (ImGui.SmallButton("×")) removeKey = e.Rune;
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove from table");
                    }

                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            if (removeKey != null)
                rows.RemoveAll(e => string.Equals(e.Rune, removeKey, StringComparison.Ordinal));

            if (ImGui.BeginCombo("Add rune##rc", "+ add…", ImGuiComboFlags.HeightLarge))
            {
                foreach (var name in AllRuneNames)
                {
                    if (rows.Exists(e => string.Equals(e.Rune, name, StringComparison.Ordinal))) continue;
                    var eff = RuneEffects.TryGetValue(name, out var x) ? x : string.Empty;
                    if (ImGui.Selectable($"{name}  —  {eff}"))
                        rows.Add(new RuneChainEntry { Rune = name, LootMult = 1f });
                }

                ImGui.EndCombo();
            }

            if (ImGui.SmallButton(this.L("runechain.reset", "Reset to tier-list defaults")))
            {
                rows.Clear();
                this.EnsureRuneChainDefaults();
            }
        }
    }
}
