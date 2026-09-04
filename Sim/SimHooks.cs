namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Numerics;
    using System.Reflection;

    // ── OFFLINE SIMULATOR SEAM ───────────────────────────────────────────────────────────────────────────
    // Compiled ONLY into Sim/ExpeditionSim.csproj; the plugin csproj excludes Sim\**, so none of this ships
    // to GameHelper. It exists so the simulator can drive the plugin's REAL code — RuneChainResolveBest,
    // ExpMonolithRouteValue, ExpFillRouteTargets, ExpComputeRoute — over synthetic monoliths. Anything the
    // simulator reimplemented instead would only prove the simulator right, so the ONLY things faked here
    // are the inputs that normally come from client memory: monolith placement, offered recipes, prices,
    // charge counts and a flat walkable grid.
    //
    // Being a partial of RunecraftHelperCore is what makes that possible: the valuation entry points and the
    // MonoView/MonoRecipe shapes are private, and a partial sees them. The public surface below therefore
    // speaks only in simulator-owned DTOs (a public method may not expose a private nested type).
    public sealed partial class RunecraftHelperCore
    {
        // ── simulator-facing DTOs ────────────────────────────────────────────────────────────────────────
        public sealed class SimOffer
        {
            public string Reward = string.Empty;   // priced by name through the (seeded) PriceCache
            public int Count = 1;
            public string[] Runes = Array.Empty<string>();   // the combination, in SOCKET ORDER

            public int Size => this.Runes.Length;
        }

        // One offered recipe, valued through the plugin's own functions: what it pays, what it would
        // propagate from THIS monolith's gold socket, and how strong that rune is.
        public sealed class SimOfferView
        {
            public string Id = string.Empty;
            public double RewardEx;
            public string Rune = string.Empty;

            // EffMult = what the plugin's RuneChainEffMult says for THIS station (its own +0x5d empowered
            // flag already applied). LootMult = the raw table weight, before any Power empowerment — the
            // only one a chain-wide model can use, since whether Power is active depends on the ORDER.
            public double EffMult = 1.0;
            public double LootMult = 1.0;
            public int Size;
        }

        public sealed class SimMono
        {
            public string Name = string.Empty;
            public Vector2 Pos;
            public int Holes = 8;
            public int GlowSocket;             // station+0x40: the gold-framed SOCKET INDEX (0-based)
            public bool Empowered;             // station+0x5d: a Power rune already in effect here
            public int RecipeMode = 1;         // station+0x58: 1/2 render a gold frame, 0/3 do not
            public int SealedOffer = -1;       // index into Offers that is LOCKED IN (station+0x60), -1 = none
            public bool PanelOpen;             // its Runeshape Combinations panel is the open one
            public List<SimOffer> Offers = new();

            // Filled by the run (all read back out of the plugin's own MonoView).
            public double RewardEx;            // MonoView.Best        — priciest reward on offer
            public int TourPos = -1;           // 1-based detonation position the plugin planned for it
            public double ChainEx;             // MonoView.ChainBestEx — chain part of the joint best
            public string BestRune = string.Empty;
            public double RouteValue;          // ExpMonolithRouteValue — what the router actually sees
            public bool Routed;
            public int VisitOrder = -1;        // 1-based position in the anchor tour, -1 = not an anchor
            public List<SimOfferView> OfferViews = new();
        }

        public sealed class SimResult
        {
            public double Weight;
            public int Covered;
            public int Targets;
            public int Charges;
            public double ComputeMs;
            public List<string> AnchorOrder = new();
            public List<Vector2> ChargePts = new();
            public List<string>? Log;
        }

        // All 34 rune names, index-aligned with Expedition2Runes — the scenario writer spells runes by name.
        public static IReadOnlyList<string> SimRuneNames => AllRuneNames;

        public static RunecraftHelperCore SimCreate(RunecraftHelperSettings settings)
        {
            var core = new RunecraftHelperCore { Settings = settings };
            core.EnsureRuneChainDefaults();
            return core;
        }

        // Prices normally arrive from poe.ninja through PriceCache's private table. Poking it by reflection
        // keeps the shipped class untouched and, more importantly, keeps the sim OFF the network so a run is
        // reproducible.
        public void SimSeedPrice(string item, double exalted)
        {
            var f = typeof(PriceCache).GetField("pricesExalted", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("PriceCache.pricesExalted moved — update SimSeedPrice.");
            var dict = (Dictionary<string, double>)f.GetValue(this.priceCache)!;
            var key = PriceCache.Normalize(item);
            lock (dict) { dict[key] = exalted; }
        }

        // Stands in for TryReadExpeditionCounts (controller-authoritative in game). Drives
        // RuneChainChargesLeft, i.e. how many packs a rune propagated now would still reach.
        public void SimSetCharges(int total, int placed)
        {
            this.expCtrlResolved = true;
            this.expTotalCharges = total;
            this.expPlacedFromCtrl = placed;
        }

        // One full pass: value every monolith, gate + weight it, then plan. Returns the plan; the per-monolith
        // numbers are written back into the SimMono objects so the caller can table them.
        // `passes` mirrors what the live plugin does over successive frames: it values the monoliths, plans,
        // and the NEXT scan values them again against that plan. The chain value depends on the detonation
        // order and the order depends on the values, so one pass alone would report the chain value of a
        // monolith whose position on the route was not yet known. Two passes is what the game reaches.
        public SimResult SimRun(List<SimMono> monos, Vector2 detonator, int gridSize, bool log = false,
                                HashSet<int>? onlyAnchors = null, int passes = 2)
        {
            SimResult outp = new();
            for (int pass = 0; pass < Math.Max(1, passes); pass++)
                outp = this.SimRunOnce(monos, detonator, gridSize, log && pass == Math.Max(1, passes) - 1, onlyAnchors);
            return outp;
        }

        private SimResult SimRunOnce(List<SimMono> monos, Vector2 detonator, int gridSize, bool log,
                                     HashSet<int>? onlyAnchors)
        {
            // 0) The waves-ahead map the chain valuation reads, rebuilt from the previous pass's plan and
            //    the previous pass's VIEWS -- before SimBuildViews replaces them, exactly as the live scan
            //    rebuilds it before EnumerateMonoliths. Doing it after would hand the rebuild fresh views
            //    whose ExpectedWaves is still 0, silently dropping it back to the socket-count fallback.
            this.RuneChainRebuildWavesAhead();

            var views = this.SimBuildViews(monos);

            // 1) VALUATION — the real one.
            for (int i = 0; i < views.Count; i++)
            {
                var v = views[i];
                this.RuneChainResolveBest(v);
                monos[i].RewardEx = v.Best;
                monos[i].ChainEx = v.ChainBestEx;
                monos[i].BestRune = v.ChainBestRune;
                monos[i].RouteValue = this.ExpMonolithRouteValue(v);
                monos[i].Routed = false;
                monos[i].VisitOrder = -1;
                monos[i].OfferViews = this.SimValueOffers(v);
            }

            // 2) TARGET CACHE — what the scan would have produced from those monoliths.
            this.expTargetCache.Clear();
            for (int i = 0; i < views.Count; i++)
            {
                if (onlyAnchors != null && !onlyAnchors.Contains(i)) continue;
                var v = views[i];

                // Same two facts the live scan caches next to the value: waves + the best uplift this monolith
                // could propagate. Without them ExpChainReorder sees a chainless map and never reorders.
                this.RuneChainRouteUplift(v, out var runeId, out var uplift);
                this.expTargetCache[v.EntityId] = new ExpCachedTarget(
                    monos[i].Pos, ExpGridToWorld(monos[i].Pos, 0f), ExpKind.Monolith, "monolith", monos[i].RouteValue,
                    0f, RuneChainWavesOf(v), uplift, runeId);
            }

            // 3) PLANNER INPUTS — mirrors BuildRouteInputs for a GRAND expedition (the mechanic only exists
            //    there), with a flat all-walkable grid so geometry never masks a valuation difference.
            int bpr = ((gridSize + 1) / 2) + 1;
            var walk = new byte[bpr * gridSize];
            Array.Fill(walk, (byte)0xFF);

            var s = this.Settings;
            int budget = Math.Max(0, this.expTotalCharges - this.expPlacedFromCtrl);
            float effDist = ExpBasePlacementDistanceGrand * (1f + (s.ExpPlacementDistancePct / 100f));
            float effRadius = ExpBaseBlastRadiusGrand * (1f + (s.ExpBlastRadiusPct / 100f));
            var inp = new ExpRouteInputs
            {
                WalkData = walk,
                Bpr = bpr,
                Doors = null,
                HasDetonator = true,
                DetonatorPos = detonator,
                DetonatorWorld = ExpGridToWorld(detonator, 0f),
                Budget = budget,
                EffDist = effDist,
                EffRadius = effRadius,
                StepDist = Math.Max(1f, effDist - ExpStepMarginGrid),
                MarkerCoverageMode = false,                                  // Grand
                MinMarkers = Math.Max(1, s.ExpMinMarkersPerSpareCharge),
                ChainOrder = s.RuneChainEnabled && s.RuneChainAffectsRoute,
                ChainBaseEx = s.RuneChainBaseMonsterEx,
                Log = log ? new List<string>() : null,
            };

            // 4) GATING + WEIGHTING + 5) ROUTING — both the plugin's own.
            this.ExpFillRouteTargets(inp, float.NaN, false);
            var res = ExpComputeRoute(inp);

            // Publish the plan into the same fields ApplyPendingRouteResult writes, so the next pass's
            // RuneChainRebuildWavesAhead sees a real detonation order.
            this.expSpinePts.Clear();
            this.expSpinePts.AddRange(res.SpinePts);
            this.expSpineAnchorIdx.Clear();
            this.expSpineAnchorIdx.AddRange(res.SpineAnchorIdx);

            var outp = new SimResult
            {
                Weight = res.Weight,
                Covered = res.Covered,
                Targets = res.Targets,
                Charges = res.Route.Count,
                ComputeMs = res.ComputeMs,
                Log = res.Log,
            };
            foreach (var rp in res.Route) outp.ChargePts.Add(rp.Grid);

            // Anchor tour → monolith names, in visit order. SpineAnchorIdx points into SpinePts, so each
            // anchor is a grid cell; the nearest monolith to it is that anchor (they ARE monolith cells).
            for (int k = 0; k < res.SpineAnchorIdx.Count; k++)
            {
                int si = res.SpineAnchorIdx[k];
                if (si < 0 || si >= res.SpinePts.Count) continue;
                var p = res.SpinePts[si];
                int bestI = -1;
                float bestD = float.MaxValue;
                for (int i = 0; i < monos.Count; i++)
                {
                    float d = Vector2.DistanceSquared(monos[i].Pos, p);
                    if (d < bestD) { bestD = d; bestI = i; }
                }

                if (bestI < 0 || bestD > 4f * 4f) continue;   // not a monolith cell → skip
                if (monos[bestI].VisitOrder > 0) continue;     // already recorded
                monos[bestI].Routed = true;
                monos[bestI].VisitOrder = outp.AnchorOrder.Count + 1;
                outp.AnchorOrder.Add(monos[bestI].Name);
            }

            return outp;
        }

        // Values every offered recipe with the plugin's own primitives: RuneChainPropagatedRune decides WHICH
        // rune the gold socket would take, RuneChainEffMult how strong it is, PriceCache what the reward pays.
        private List<SimOfferView> SimValueOffers(MonoView v)
        {
            var list = new List<SimOfferView>(v.Offered.Count);
            bool frame = RuneChainHighlightActive(v);
            foreach (var rec in v.Offered)
            {
                double reward = 0;
                if (rec.reward != null && this.priceCache.TryGetExaltedPrice(rec.reward.name, out var unit) && unit > 0)
                    reward = unit * Math.Max(1, rec.rewardCount);

                var rune = frame ? this.RuneChainPropagatedRune(v, rec) : string.Empty;
                list.Add(new SimOfferView
                {
                    Id = rec.id,
                    RewardEx = reward,
                    Rune = rune,
                    EffMult = string.IsNullOrEmpty(rune) ? 1.0 : this.RuneChainEffMult(rune, v.RunesEmpowered),
                    LootMult = string.IsNullOrEmpty(rune) ? 1.0 : (this.RuneChainEntryFor(rune)?.LootMult ?? 1.0),
                    Size = rec.size,
                });
            }

            return list;
        }

        // What the Combinations panel would draw for each row of the open monolith: the rune the row would
        // propagate, its deduped multiplier, whether it is already taken, and which row wins the amber ring.
        public List<(string Id, string Rune, double Mult, bool Taken, bool Amber)> SimPanelRows()
        {
            var rows = new List<(string, string, double, bool, bool)>();
            var open = this.monolithViews.Find(v => v.PanelOpen);
            if (open == null) return rows;

            double bestMult = 1.0;
            foreach (var rec in open.Offered)
                if (this.TryGetPropagatedRuneForRecipeId(rec.id, out _, out var m, out _) && m > bestMult)
                    bestMult = m;

            foreach (var rec in open.Offered)
            {
                if (!this.TryGetPropagatedRuneForRecipeId(rec.id, out var rune, out var mult, out var taken))
                {
                    rows.Add((rec.id, "-", 1.0, false, false));
                    continue;
                }

                rows.Add((rec.id, rune, mult, taken, bestMult > 1.0 && mult >= bestMult));
            }

            return rows;
        }

        // Synthetic MonoViews: exactly the fields the valuation reads, filled the way the live scan fills them.
        private List<MonoView> SimBuildViews(List<SimMono> monos)
        {
            this.monolithViews = new List<MonoView>();
            this.monolithRecipes = new List<MonoRecipe>();
            var nameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < AllRuneNames.Length; i++) nameToIdx[AllRuneNames[i]] = i;

            long id = 1;
            foreach (var m in monos)
            {
                var v = new MonoView
                {
                    EntityId = id++,
                    HoleCount = m.Holes,
                    RecipeMode = m.RecipeMode,
                    RunesEmpowered = m.Empowered,
                    GridPos = m.Pos,
                    HasPos = true,
                    AreaLevel = 80,
                    PanelOpen = m.PanelOpen,
                };
                v.GlowSockets.Add(m.GlowSocket);

                int r = 0;
                foreach (var o in m.Offers)
                {
                    var idxs = new List<int>(o.Runes.Length);
                    foreach (var rn in o.Runes)
                    {
                        if (!nameToIdx.TryGetValue(rn, out var ri))
                            throw new ArgumentException($"unknown rune \"{rn}\" in {m.Name}");
                        idxs.Add(ri);
                    }

                    var rec = new MonoRecipe
                    {
                        row = r,
                        id = string.Create(CultureInfo.InvariantCulture, $"{m.Name}_r{r++}"),
                        size = o.Size,
                        runeIdx = idxs,
                        runes = new List<string>(o.Runes),
                        reward = new MonoReward { name = o.Reward, id = o.Reward },
                        rewardCount = o.Count,
                    };
                    v.Offered.Add(rec);
                    this.monolithRecipes.Add(rec);
                }

                // MonoView.Best as the live scan computes it: the priciest reward TOTAL on offer.
                double best = 0;
                foreach (var rec in v.Offered)
                {
                    if (rec.reward == null) continue;
                    if (this.priceCache.TryGetExaltedPrice(rec.reward.name, out var unit) && unit > 0)
                        best = Math.Max(best, unit * Math.Max(1, rec.rewardCount));
                }

                v.Best = best;

                // A locked-in recipe: what the game exposes as sealed (is_rerolled) + station+0x60.
                if (m.SealedOffer >= 0 && m.SealedOffer < v.Offered.Count)
                {
                    v.IsRerolled = true;
                    v.SelectedRecipeId = v.Offered[m.SealedOffer].id;
                }

                this.monolithViews.Add(v);
            }

            return this.monolithViews;
        }
    }
}
