namespace RunecraftHelper.Sim
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Numerics;
    using SimMono = RunecraftHelperCore.SimMono;
    using SimOffer = RunecraftHelperCore.SimOffer;
    using SimResult = RunecraftHelperCore.SimResult;

    // Offline harness for the question "does the rune chain actually change the route?".
    //
    // Each scenario lays 12-15 monoliths on ONE flat, fully walkable Grand-Expedition map and plans it TWICE
    // with byte-identical geometry: once with the chain valuation off (reward price only, i.e. the shipped
    // behaviour) and once with it on. Only Settings.RuneChainAffectsRoute differs between the two plans, so
    // any difference in the anchor tour is caused by the rune valuation and nothing else.
    //
    // Everything that decides anything is the plugin's own code (see Sim/SimHooks.cs); this file only invents
    // monoliths, recipes and prices — the things that normally come out of client memory.
    internal static class Program
    {
        private const int Grid = 500;
        private const int Charges = 15;

        // Reward tiers, in Exalted. Deliberately coarse: the point is the ORDER of magnitude against a rune's
        // chain value (~200 ex for a top rune at the shipped 30 ex/pack), not price realism.
        private const string RewardJunk = "SimJunk";
        private const string RewardMid = "SimMid";
        private const string RewardRich = "SimRich";
        private const string RewardRich200 = "SimRich200";
        private const string RewardRich300 = "SimRich300";

        private static void Main(string[] args)
        {
            bool verbose = Array.Exists(args, a => a is "-v" or "--verbose");

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine($"Grand Expedition · {Charges} charges · flat {Grid}×{Grid} walkable grid · " +
                              "Grand physics (108 grid placement / 37 grid blast)");

            RunScenario("A · rich rewards, nothing worth propagating", ScenarioA(), verbose);
            RunScenario("B · junk rewards, strong runes on the junk", ScenarioB(), verbose);
            RunScenario("C · one jackpot reward far away vs. a cluster of strong runes", ScenarioC(), verbose);
            RunScenario("D · junk rewards, strong runes, 60 ex monolith gate", ScenarioD(), verbose, minEx: 60f);
            RunScenario("E · junk rewards, strong runes far away, only 6 charges", ScenarioB(), verbose, charges: 6);
            RunScenario("F · rich cluster vs. rune cluster on opposite sides, 60 ex gate",
                        ScenarioF(), verbose, minEx: 60f);

            Compare("G · 20 monoliths, only two rich recipes (200 + 300 ex), some strong runes",
                    ScenarioG(), charges: 15);
            Compare("H · 20 monoliths, no strong runes, several mid recipes",
                    ScenarioH(), charges: 15);
            Compare("G/scarce · same 20 monoliths, only 8 charges", ScenarioG(), charges: 8);
            Compare("H/scarce · same 20 monoliths, only 8 charges", ScenarioH(), charges: 8);
            PowerPlacement("I · does WHEN Power is detonated matter?", ScenarioI(97), charges: 15);
            PowerSweep(charges: 15);
            ChainAudit("J · shipped chain value vs. the order-aware one", ScenarioI(97), charges: 15);
        }

        // ── scenarios ────────────────────────────────────────────────────────────────────────────────────
        // Rune vocabulary used below (weights are the plugin's shipped defaults):
        //   Opulent 1.35 · Power 1.30 · Bond 1.25 · Time 1.18   → worth propagating
        //   Soul / Toxic / Arcane / Moon / Tidal / Celestial 1.00 → pure danger, no loot effect
        //   Oath 0.75                                            → a net cost

        // Rewards carry the value, runes are all neutral. The chain must NOT move the route.
        private static List<SimMono> ScenarioA()
        {
            var m = Ring(13, seed: 11);
            for (int i = 0; i < m.Count; i++)
            {
                string reward = i % 3 == 0 ? RewardRich : RewardMid;
                m[i].Offers.Add(Offer(reward, 1, "Soul", "Toxic", "Arcane", "Moon"));
                m[i].Offers.Add(Offer(RewardMid, 1, "Tidal", "Celestial", "Soul"));
                m[i].GlowSocket = i % 3;
            }

            return m;
        }

        // Rewards are junk everywhere, but four monoliths — the FAR ones — can propagate a top rune. If the
        // valuation works, chain-on must pull the tour onto exactly those.
        private static List<SimMono> ScenarioB()
        {
            var m = Ring(14, seed: 23);
            for (int i = 0; i < m.Count; i++)
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, "Soul", "Toxic", "Arcane", "Moon"));
                m[i].Offers.Add(Offer(RewardJunk, 2, "Tidal", "Celestial", "Soul"));
                m[i].GlowSocket = 1;
            }

            // The four monoliths furthest from the detonator get a good rune ON the gold socket (index 1).
            foreach (int i in FarthestFour(m))
            {
                string good = i % 2 == 0 ? "Opulent" : "Bond";
                m[i].Offers.Add(Offer(RewardJunk, 1, "Soul", good, "Arcane", "Moon"));
            }

            return m;
        }

        // The adversarial case: one 260 ex reward on the far side, a cluster of Opulent/Power near the middle.
        // Chain-on should visit the rune cluster EARLY (a rune only buffs what comes after it) and still take
        // the jackpot — the interesting output is the ORDER, not the set.
        private static List<SimMono> ScenarioC()
        {
            var m = Ring(12, seed: 37);
            for (int i = 0; i < m.Count; i++)
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, "Soul", "Toxic", "Arcane"));
                m[i].GlowSocket = 0;
            }

            m[0].Offers.Add(Offer(RewardRich, 1, "Toxic", "Soul", "Arcane", "Moon"));   // jackpot, neutral rune
            m[0].Name = "M00-jackpot";
            foreach (int i in new[] { 5, 6, 7 })
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, i == 6 ? "Power" : "Opulent", "Soul", "Arcane"));
                m[i].Name = m[i].Name + (i == 6 ? "-power" : "-opulent");
            }

            return m;
        }

        // Same as B but with the 60 ex monolith price gate the user runs with. Chain-off gates every junk
        // monolith out; chain-on must let the rune seeds back through, because the gate is applied to
        // ExpMonolithRouteValue and that now includes the chain.
        private static List<SimMono> ScenarioD() => ScenarioB();

        // The priority test proper: four 260 ex monoliths on one side, four junk-but-Opulent/Bond monoliths on
        // the OTHER side, everything else junk, with the 60 ex gate on. Chain-off can only see the rich four;
        // chain-on must also admit the rune four — and the two clusters are far apart, so admitting them has a
        // real routing cost. This is where "changes priority" either shows up or does not.
        private static List<SimMono> ScenarioF()
        {
            var m = Ring(14, seed: 51);
            for (int i = 0; i < m.Count; i++)
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, "Soul", "Toxic", "Arcane", "Moon"));
                m[i].GlowSocket = 0;
            }

            foreach (int i in new[] { 1, 2, 3, 4 })
            {
                m[i].Offers.Add(Offer(RewardRich, 1, "Toxic", "Soul", "Arcane"));   // rich, neutral rune
                m[i].Name += "-rich";
            }

            foreach (int i in new[] { 8, 9, 10, 11 })
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, i % 2 == 0 ? "Opulent" : "Bond", "Soul", "Arcane"));
                m[i].Name += i % 2 == 0 ? "-opulent" : "-bond";
            }

            return m;
        }

        // The user's case: 20 monoliths, 15 charges, only two of them carry a real recipe (200 and 300 ex).
        // Four others can propagate a top rune off junk recipes.
        private static List<SimMono> ScenarioG()
        {
            var m = Ring(20, seed: 71);
            for (int i = 0; i < m.Count; i++)
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, "Soul", "Toxic", "Arcane", "Moon"));
                m[i].Offers.Add(Offer(RewardJunk, 1, "Tidal", "Celestial", "Soul"));
                m[i].GlowSocket = 0;
            }

            m[3].Offers.Add(Offer(RewardRich200, 1, "Toxic", "Soul", "Arcane"));
            m[3].Name += "-200ex";
            m[14].Offers.Add(Offer(RewardRich300, 1, "Toxic", "Soul", "Arcane", "Moon"));
            m[14].Name += "-300ex";

            foreach (int i in new[] { 6, 9, 12, 17 })
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, i % 3 == 0 ? "Opulent" : "Bond", "Soul", "Arcane", "Moon"));
                m[i].Name += i % 3 == 0 ? "-opulent" : "-bond";
            }

            return m;
        }

        // The mirror case: nothing worth propagating anywhere, but half a dozen mid-priced recipes.
        private static List<SimMono> ScenarioH()
        {
            var m = Ring(20, seed: 83);
            for (int i = 0; i < m.Count; i++)
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, "Soul", "Toxic", "Arcane", "Moon"));
                m[i].Offers.Add(Offer(RewardJunk, 1, "Tidal", "Celestial", "Soul"));
                m[i].GlowSocket = 0;
            }

            foreach (int i in new[] { 2, 5, 8, 11, 15, 18 })
            {
                m[i].Offers.Add(Offer(RewardMid, 2, "Toxic", "Soul", "Arcane"));   // 2 x 25 ex
                m[i].Name += "-mid";
            }

            return m;
        }

        // One Power monolith plus five other strong runes, all on junk recipes: the case where the order of
        // detonation is the only thing that differs.
        private static List<SimMono> ScenarioI(int seed)
        {
            var m = Ring(20, seed: seed);
            for (int i = 0; i < m.Count; i++)
            {
                m[i].Offers.Add(Offer(RewardJunk, 1, "Soul", "Toxic", "Arcane", "Moon"));
                m[i].GlowSocket = 0;
            }

            m[10].Offers.Add(Offer(RewardJunk, 1, "Power", "Soul", "Arcane", "Moon"));
            m[10].Name += "-POWER";
            string[] good = { "Opulent", "Bond", "Time", "Death", "Rebirth" };
            for (int g = 0; g < good.Length; g++)
            {
                int i = 2 + (g * 3);
                m[i].Offers.Add(Offer(RewardJunk, 1, good[g], "Soul", "Arcane", "Moon"));
                m[i].Name += "-" + good[g].ToLowerInvariant();
            }

            return m;
        }

        // Prices the difference between the two ways of counting "how many packs will this rune still buff".
        //
        //   SHIPPED   downstreamPacks = size(recipe) + chargesLeft          (RuneChainDownstreamPacks)
        //   TRUE      downstreamPacks = size(recipe) + SUM of the wave counts of the monoliths that come
        //                              after this one on the planned route
        //   SOCKETS   the same, but each downstream monolith contributes its HOLE COUNT instead of the
        //             recipe we expect to be chosen there -- the "if you play every remaining monolith at
        //             full length" upper bound, which is what a player is really deciding against.
        //
        // The shipped count is wrong in both directions at once: it credits one pack per remaining CHARGE
        // (most charges only extend the chain and unearth nothing), while a single monolith with 8 holes is
        // 8 waves, not one. The two errors do not cancel.
        private static void ChainAudit(string title, List<SimMono> monos, int charges)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 108));
            Console.WriteLine(title + $"   [{charges} charges]");
            Console.WriteLine(new string('=', 108));

            var res = Plan(monos, chain: true, minEx: 0f, verbose: false, charges: charges);
            var order = Strategy.Reached(monos, res, GrandBlastRadius);
            var e = Strategy.Evaluate(monos, order, BaseMonsterEx, PowerFactor);

            // Marginal value of a rune = what the WHOLE plan loses if this monolith is played on a neutral
            // recipe instead. It is the only figure that prices Power correctly: Power's own uplift is small,
            // but removing it also un-empowers every rune after it, and that shows up here and nowhere else.
            double baseTotal = e.Total;
            double Marginal(int mi)
            {
                var keep = monos[mi].OfferViews;
                var neutral = keep.FindAll(o => o.EffMult <= 1.0);
                if (neutral.Count == 0) return double.NaN;      // nothing neutral on offer -> not a free choice
                monos[mi].OfferViews = neutral;
                double without = Strategy.Evaluate(monos, order, BaseMonsterEx, PowerFactor).Total;
                monos[mi].OfferViews = keep;
                return baseTotal - without;
            }

            Console.WriteLine("{0,-16} {1,4} {2,8} {3,7} {4,7} {5,9} {6,9} {7,9} {8,8} {9,9}",
                "monolith", "pos", "rune", "runeEx", "own", "Y chosen", "Y sockets", "true ex", "shipped",
                "marginal");
            Console.WriteLine(new string('-', 108));

            for (int j = 0; j < order.Count; j++)
            {
                int mi = order[j];
                if (e.Uplifts[j] <= 0) continue;   // only the monoliths that actually propagate something

                int yChosen = 0;
                for (int i = j + 1; i < order.Count; i++) yChosen += e.Sizes[i];
                int ySockets = 0;
                for (int i = j + 1; i < order.Count; i++) ySockets += monos[order[i]].Holes;

                // Per-pack ex value of this rune -- the user-facing reparameterisation: runeEx = baseEx * uplift.
                double runeEx = BaseMonsterEx * e.Uplifts[j];
                double trueEx = runeEx * (e.Sizes[j] + yChosen);
                double marg = Marginal(mi);
                Console.WriteLine("{0,-16} {1,4} {2,8} {3,7:F1} {4,7} {5,9} {6,9} {7,9:F0} {8,8:F0} {9,9}",
                    monos[mi].Name, j + 1, monos[mi].BestRune, runeEx, e.Sizes[j], yChosen, ySockets,
                    trueEx, monos[mi].ChainEx, double.IsNaN(marg) ? "-" : marg.ToString("F0"));
            }

            Console.WriteLine(new string('-', 108));
            Console.WriteLine("runeEx = baseMonsterEx x uplift (ex added per buffed wave) · own = this recipe's waves");
            Console.WriteLine("Y chosen/sockets = waves still ahead, by expected recipe vs. by hole count (upper bound)");
            Console.WriteLine("shipped = MonoView.ChainBestEx, i.e. baseEx x (size + chargesLeft) x uplift");
            Console.WriteLine("marginal = what the whole plan loses if this monolith is played neutral instead " +
                              "(Power's cross-term lives here)");
        }

        // Scenario I on one map answers "does order matter HERE"; it cannot answer "should the router be
        // changed", because where the geometric tour happens to put the Power monolith is luck of the layout.
        // So repeat over several layouts and see whether the shipped geometric order is systematically leaving
        // value behind, or only occasionally.
        private static void PowerSweep(int charges)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 108));
            Console.WriteLine($"I/sweep · same setup over 6 layouts   [{charges} charges, budget-truncated totals]");
            Console.WriteLine(new string('=', 108));
            Console.WriteLine("{0,6} {1,10} {2,12} {3,12} {4,12} {5,12} {6,16}",
                "seed", "Power at", "as planned", "Power #1", "Power last", "runes first", "as-planned vs best");
            Console.WriteLine(new string('-', 108));

            int wins = 0, total = 0;
            foreach (int seed in new[] { 97, 101, 103, 107, 109, 113 })
            {
                var monos = ScenarioI(seed);
                var res = Plan(monos, chain: true, minEx: 0f, verbose: false, charges: charges);
                var order = Strategy.Reached(monos, res, GrandBlastRadius);
                int power = order.FindIndex(i => monos[i].Name.Contains("POWER", StringComparison.Ordinal));
                if (power < 0)
                {
                    Console.WriteLine("{0,6} {1,10}", seed, "unreached");
                    continue;
                }

                var variants = OrderVariants(monos, order, power);
                var totals = new double[variants.Length];
                for (int v = 0; v < variants.Length; v++)
                {
                    var cut = TruncateTour(monos, variants[v], charges);
                    totals[v] = Strategy.Evaluate(monos, cut, BaseMonsterEx, PowerFactor).Total;
                }

                double best = totals[0];
                foreach (var t in totals) best = Math.Max(best, t);
                total++;
                if (totals[0] >= best - 0.5) wins++;
                Console.WriteLine("{0,6} {1,10} {2,12:F0} {3,12:F0} {4,12:F0} {5,12:F0} {6,16}",
                    seed, $"#{power + 1}/{order.Count}", totals[0], totals[1], totals[2], totals[3],
                    totals[0] >= best - 0.5 ? "already best" : $"-{best - totals[0]:F0} ex ({(best - totals[0]) / best * 100:F0}%)");
            }

            Console.WriteLine(new string('-', 108));
            Console.WriteLine($"the shipped geometric order was already the best of the four on {wins}/{total} layouts");
        }

        private static List<int>[] OrderVariants(List<SimMono> monos, List<int> order, int power)
        {
            var asPlanned = new List<int>(order);

            var powerFirst = new List<int>(order);
            powerFirst.RemoveAt(power);
            powerFirst.Insert(0, order[power]);

            var powerLast = new List<int>(order);
            powerLast.RemoveAt(power);
            powerLast.Add(order[power]);

            var runesFirst = new List<int>(order);
            runesFirst.Sort((a, b) => RuneRank(monos, b).CompareTo(RuneRank(monos, a)));

            return new[] { asPlanned, powerFirst, powerLast, runesFirst };
        }

        private static int RuneRank(List<SimMono> monos, int i) =>
            monos[i].Name.Contains("POWER", StringComparison.Ordinal) ? 2
            : Strategy.RuneUplift(monos[i]) > 0 ? 1 : 0;

        private static List<int> TruncateTour(List<SimMono> monos, List<int> ord, int charges)
        {
            float cap = charges * GrandPlacementDist;
            var keep = new List<int>();
            float len = 0;
            var prev = Detonator;
            foreach (var i in ord)
            {
                len += Vector2.Distance(prev, monos[i].Pos);
                if (len > cap) break;
                keep.Add(i);
                prev = monos[i].Pos;
            }

            return keep;
        }

        // Same anchors, same charges, same recipes on offer -- only the DETONATION ORDER changes. This is the
        // one axis the shipped planner has no notion of, so it is worth pricing on its own.
        private static void PowerPlacement(string title, List<SimMono> monos, int charges)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 108));
            Console.WriteLine(title + $"   [{charges} charges]");
            Console.WriteLine(new string('=', 108));

            var res = Plan(monos, chain: true, minEx: 0f, verbose: false, charges: charges);
            var order = Strategy.Reached(monos, res, GrandBlastRadius);
            int power = order.FindIndex(i => monos[i].Name.Contains("POWER", StringComparison.Ordinal));
            if (power < 0) { Console.WriteLine("Power monolith not reached by the plan."); return; }

            var asPlanned = new List<int>(order);
            var powerFirst = new List<int>(order);
            powerFirst.RemoveAt(power);
            powerFirst.Insert(0, order[power]);
            var powerLast = new List<int>(order);
            powerLast.RemoveAt(power);
            powerLast.Add(order[power]);

            // The full re-order: every rune-bearing monolith to the front, Power at the very head.
            var runesFirst = new List<int>(order);
            runesFirst.Sort((a, b) =>
            {
                int ka = monos[a].Name.Contains("POWER", StringComparison.Ordinal) ? 2
                    : Strategy.RuneUplift(monos[a]) > 0 ? 1 : 0;
                int kb = monos[b].Name.Contains("POWER", StringComparison.Ordinal) ? 2
                    : Strategy.RuneUplift(monos[b]) > 0 ? 1 : 0;
                return kb.CompareTo(ka);
            });

            // A re-ordered tour is a LONGER walk, and the walk is paid for in charges. Reporting the value of
            // a re-ordering without that cost would be fiction, so price it: on this flat all-walkable map the
            // straight line IS the walkable path, and the planner's own estimate of a hop is
            // ceil(hop / effDist) charges.
            float PathLen(List<int> ord)
            {
                float len = 0;
                var prev = Detonator;
                foreach (var i in ord) { len += Vector2.Distance(prev, monos[i].Pos); prev = monos[i].Pos; }
                return len;
            }

            // Charges for a whole tour, NOT the sum of per-hop ceilings: the Placer sweeps forward along one
            // continuous spine, so a charge that lands between two nearby anchors serves both. Per-hop
            // ceiling charges at least one per anchor and badly overestimates (it claimed 20 for the tour the
            // real planner laid in 15). Whole-polyline length over the placement distance matches it.
            int ChargeEst(List<int> ord) =>
                Math.Max(1, (int)Math.Ceiling(PathLen(ord) / GrandPlacementDist));

            // The prefix of `ord` the charge budget actually reaches. This is what makes the comparison fair:
            // a re-ordering that puts the rune monoliths first does not get the rest of the map for free.
            List<int> Truncate(List<int> ord, int budget)
            {
                float cap = budget * GrandPlacementDist;
                var keep = new List<int>();
                float len = 0;
                var prev = Detonator;
                foreach (var i in ord)
                {
                    len += Vector2.Distance(prev, monos[i].Pos);
                    if (len > cap) break;
                    keep.Add(i);
                    prev = monos[i].Pos;
                }

                return keep;
            }

            Console.WriteLine("{0,-34} {1,7} {2,8} {3,7} {4,8} {5,9} {6,9}",
                "detonation order", "walk", "charges", "kept", "rewardEx", "runeEx", "TOTAL");
            Console.WriteLine(new string('-', 108));
            foreach (var (name, ord) in new (string, List<int>)[]
            {
                ($"as planned (Power at #{power + 1} of {order.Count})", asPlanned),
                ("Power moved to #1", powerFirst),
                ("Power moved last", powerLast),
                ("all rune monoliths first, Power #1", runesFirst),
            })
            {
                var cut = Truncate(ord, charges);
                var e = Strategy.Evaluate(monos, cut, BaseMonsterEx, PowerFactor);
                Console.WriteLine("{0,-34} {1,7:F0} {2,8} {3,7} {4,8:F0} {5,9:F0} {6,9:F0}",
                    name, PathLen(cut), ChargeEst(cut), $"{cut.Count}/{ord.Count}",
                    e.RewardEx, e.MonsterExUplift, e.Total);
            }

            Console.WriteLine("(walk/charges/TOTAL are for the prefix the 15-charge budget reaches, " +
                              "so a re-ordering pays for its own detour)");

            Console.WriteLine(new string('-', 108));
            Console.WriteLine("picks for the best-ordered variant:");
            foreach (var line in Strategy.Evaluate(monos, Truncate(runesFirst, charges), BaseMonsterEx, PowerFactor).Picks)
                Console.WriteLine("      " + line);
        }

        // -- strategy comparison ------------------------------------------------------------------------
        // Three candidate anchor sets, each planned by the REAL router/placer, each scored by the SAME
        // order-aware evaluator (Sim/Strategy.cs). "Best" is simply the argmax -- which is the whole point:
        // three strategies are three candidates under one objective, not three modes to choose between.
        private static void Compare(string title, List<SimMono> monos, int charges)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 108));
            Console.WriteLine(title + $"   [{charges} charges]");
            Console.WriteLine(new string('=', 108));

            // One warm-up plan to fill OfferViews (the candidate sets are built from that valuation).
            Plan(monos, chain: true, minEx: 0f, verbose: false, charges: charges);

            int take = Math.Max(4, charges - 2);
            var candidates = new (string Name, HashSet<int> Anchors)[]
            {
                ("recipes only", Strategy.TopBy(monos, Strategy.RecipeValue, take, 0)),
                ("runes only", Strategy.TopBy(monos, Strategy.RuneUplift, take, 0)),
                ("runes + rich recipes", Union(
                    Strategy.TopBy(monos, Strategy.RuneUplift, take / 2, 0),
                    Strategy.TopBy(monos, Strategy.RecipeValue, take / 2, 20))),
                ("everything (shipped, gate 0)", All(monos.Count)),
            };

            Console.WriteLine("{0,-30} {1,7} {2,8} {3,10} {4,7} {5,9} {6,9}",
                "strategy", "anchors", "charges", "rewardEx", "packs", "runeEx", "TOTAL");
            Console.WriteLine(new string('-', 108));

            string bestName = string.Empty;
            double bestTotal = double.NegativeInfinity;
            var evals = new List<(string Name, Strategy.Eval E)>();
            foreach (var (name, anchors) in candidates)
            {
                var res = Plan(monos, chain: true, minEx: 0f, verbose: false, charges: charges, onlyAnchors: anchors);
                var order = Strategy.Reached(monos, res, GrandBlastRadius);
                var e = Strategy.Evaluate(monos, order, BaseMonsterEx, PowerFactor);
                evals.Add((name, e));
                Console.WriteLine("{0,-30} {1,7} {2,8} {3,10:F0} {4,7} {5,9:F0} {6,9:F0}",
                    name, order.Count, res.Charges, e.RewardEx, e.Packs, e.MonsterExUplift, e.Total);
                if (e.Total > bestTotal) { bestTotal = e.Total; bestName = name; }
            }

            Console.WriteLine(new string('-', 108));
            Console.WriteLine($"BEST  {bestName}  ({bestTotal:F0} ex)");
            foreach (var (name, e) in evals)
                Console.WriteLine($"      {name,-30} recipes {e.RewardEx,6:F0} + monster floor {e.MonsterExBase,7:F0} " +
                                  $"+ runes {e.MonsterExUplift,6:F0}");

            // Sensitivity: baseMonsterEx is the one number in the whole model that is calibrated rather than
            // read, and the monster floor scales linearly with it. If the winner changed across this sweep the
            // conclusion would rest on a guess, so sweep it -- including 0, i.e. "monsters drop nothing and
            // only recipes count".
            Console.WriteLine();
            Console.WriteLine("sensitivity to baseMonsterEx (same plans, rescored):");
            Console.WriteLine("{0,-30} {1,10} {2,10} {3,10} {4,10}", "strategy", "0 ex", "10 ex", "30 ex", "60 ex");
            foreach (var (name, anchors) in candidates)
            {
                var res = Plan(monos, chain: true, minEx: 0f, verbose: false, charges: charges, onlyAnchors: anchors);
                var order = Strategy.Reached(monos, res, GrandBlastRadius);
                Console.WriteLine("{0,-30} {1,10:F0} {2,10:F0} {3,10:F0} {4,10:F0}", name,
                    Strategy.Evaluate(monos, order, 0, PowerFactor).Total,
                    Strategy.Evaluate(monos, order, 10, PowerFactor).Total,
                    Strategy.Evaluate(monos, order, 30, PowerFactor).Total,
                    Strategy.Evaluate(monos, order, 60, PowerFactor).Total);
            }
        }

        private const float GrandBlastRadius = 37f;
        private const double BaseMonsterEx = 30.0;   // the plugin's shipped default (measured; see settings)
        private const double PowerFactor = 1.5;      // Settings.RuneChainPowerFactor default
        private const float GrandPlacementDist = 108f;

        private static HashSet<int> Union(HashSet<int> a, HashSet<int> b)
        {
            var u = new HashSet<int>(a);
            u.UnionWith(b);
            return u;
        }

        private static HashSet<int> All(int n)
        {
            var a = new HashSet<int>();
            for (int i = 0; i < n; i++) a.Add(i);
            return a;
        }

        // ── scenario helpers ─────────────────────────────────────────────────────────────────────────────
        private static SimOffer Offer(string reward, int count, params string[] runes) =>
            new() { Reward = reward, Count = count, Runes = runes };

        // Monoliths scattered on a jittered ring + a couple of inner ones, so distances differ enough for the
        // tour order to be meaningful. Deterministic per seed.
        private static List<SimMono> Ring(int n, int seed)
        {
            var rng = new Random(seed);
            var list = new List<SimMono>(n);
            for (int i = 0; i < n; i++)
            {
                double a = (2 * Math.PI * i / n) + (rng.NextDouble() * 0.25);
                double r = 90 + (rng.NextDouble() * 140) + (i % 4 == 0 ? 60 : 0);
                list.Add(new SimMono
                {
                    Name = "M" + i.ToString("00", CultureInfo.InvariantCulture),
                    Pos = new Vector2(
                        (float)Math.Round((Grid / 2) + (r * Math.Cos(a))),
                        (float)Math.Round((Grid / 2) + (r * Math.Sin(a)))),
                    Holes = 4 + (i % 5),
                    RecipeMode = 1,
                });
            }

            return list;
        }

        private static readonly Vector2 Detonator = new(Grid / 2, Grid / 2);

        private static List<int> FarthestFour(List<SimMono> m)
        {
            var idx = new List<int>();
            for (int i = 0; i < m.Count; i++) idx.Add(i);
            idx.Sort((x, y) => Vector2.Distance(m[y].Pos, Detonator).CompareTo(Vector2.Distance(m[x].Pos, Detonator)));
            return idx.GetRange(0, Math.Min(4, idx.Count));
        }

        // ── run + report ─────────────────────────────────────────────────────────────────────────────────
        private static void RunScenario(string title, List<SimMono> monos, bool verbose, float minEx = 0f,
                                        int charges = Charges)
        {
            Console.WriteLine();
            Console.WriteLine(new string('═', 108));
            Console.WriteLine(title + $"   [gate {minEx:F0} ex · {charges} charges]");
            Console.WriteLine(new string('═', 108));

            var off = Plan(monos, chain: false, minEx: minEx, verbose: verbose, charges: charges);
            var offRows = Snapshot(monos);
            var on = Plan(monos, chain: true, minEx: minEx, verbose: verbose, charges: charges);
            var onRows = Snapshot(monos);

            Console.WriteLine("{0,-16} {1,8} {2,10} {3,8} {4,10} {5,7} {6,10} {7,7}",
                "monolith", "rewardEx", "bestRune", "chainEx", "wOFF", "ordOFF", "wON", "ordON");
            Console.WriteLine(new string('─', 108));
            for (int i = 0; i < monos.Count; i++)
            {
                var a = offRows[i];
                var b = onRows[i];
                Console.WriteLine("{0,-16} {1,8:F0} {2,10} {3,8:F0} {4,10:F0} {5,7} {6,10:F0} {7,7}",
                    monos[i].Name, b.RewardEx, string.IsNullOrEmpty(b.BestRune) ? "-" : b.BestRune, b.ChainEx,
                    a.RouteValue, Ord(a.VisitOrder), b.RouteValue, Ord(b.VisitOrder));
            }

            Console.WriteLine(new string('─', 108));
            Console.WriteLine($"OFF  anchors {off.AnchorOrder.Count,2} · charges {off.Charges,2} · " +
                              $"weight {off.Weight,8:F0} · covered {off.Covered}/{off.Targets} · {off.ComputeMs:F0} ms");
            Console.WriteLine($"     tour  {string.Join(" -> ", off.AnchorOrder)}");
            Console.WriteLine($"ON   anchors {on.AnchorOrder.Count,2} · charges {on.Charges,2} · " +
                              $"weight {on.Weight,8:F0} · covered {on.Covered}/{on.Targets} · {on.ComputeMs:F0} ms");
            Console.WriteLine($"     tour  {string.Join(" -> ", on.AnchorOrder)}");

            var added = new List<string>();
            var dropped = new List<string>();
            foreach (var x in on.AnchorOrder) if (!off.AnchorOrder.Contains(x)) added.Add(x);
            foreach (var x in off.AnchorOrder) if (!on.AnchorOrder.Contains(x)) dropped.Add(x);
            bool sameSet = added.Count == 0 && dropped.Count == 0;
            bool sameOrder = sameSet && string.Join(",", off.AnchorOrder) == string.Join(",", on.AnchorOrder);

            Console.WriteLine();
            Console.WriteLine(sameOrder ? "VERDICT  identical plan — the chain changed nothing"
                : sameSet ? "VERDICT  same anchors, DIFFERENT order"
                : $"VERDICT  anchor set CHANGED — added [{string.Join(", ", added)}] dropped [{string.Join(", ", dropped)}]");
        }

        private static SimResult Plan(List<SimMono> monos, bool chain, float minEx, bool verbose, int charges,
                                      HashSet<int>? onlyAnchors = null)
        {
            var settings = new RunecraftHelperSettings
            {
                ExpTotalChargesManual = charges,
                ExpMonolithMinEx = minEx,
                RuneChainEnabled = chain,
                RuneChainAffectsRoute = chain,
                ExpLogPlanner = verbose,
            };
            var core = RunecraftHelperCore.SimCreate(settings);
            core.SimSeedPrice(RewardJunk, 5);
            core.SimSeedPrice(RewardMid, 25);
            core.SimSeedPrice(RewardRich, 260);
            core.SimSeedPrice(RewardRich200, 200);
            core.SimSeedPrice(RewardRich300, 300);
            core.SimSetCharges(charges, 0);

            var res = core.SimRun(monos, Detonator, Grid, log: verbose, onlyAnchors: onlyAnchors);
            if (verbose && res.Log != null)
                foreach (var line in res.Log) Console.WriteLine("      | " + line);
            return res;
        }

        private static List<(double RewardEx, double ChainEx, string BestRune, double RouteValue, int VisitOrder)>
            Snapshot(List<SimMono> monos)
        {
            var rows = new List<(double, double, string, double, int)>(monos.Count);
            foreach (var m in monos) rows.Add((m.RewardEx, m.ChainEx, m.BestRune, m.RouteValue, m.VisitOrder));
            return rows;
        }

        private static string Ord(int o) => o > 0 ? o.ToString(CultureInfo.InvariantCulture) : "-";
    }
}
