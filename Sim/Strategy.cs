namespace RunecraftHelper.Sim
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using SimMono = RunecraftHelperCore.SimMono;
    using SimResult = RunecraftHelperCore.SimResult;

    // ── ORDER-AWARE PLAN EVALUATOR (PROTOTYPE — NOT plugin code) ─────────────────────────────────────────
    // Everything else in Sim/ drives the plugin's real code. This file does NOT: it is the piece the plugin
    // is currently missing, prototyped here so its worth can be measured before it is built for real.
    //
    // WHY IT IS NEEDED. The plugin's shipped chain value is position-blind: RuneChainDownstreamPacks =
    // size + chargesLeft, i.e. the same "everything still ahead" for every monolith. That is fine for
    // ranking recipes inside ONE open panel, but it cannot compare two whole ROUTES, because a rune only
    // buffs what is detonated AFTER it — a top rune at the end of the chain is worth nothing. Without a
    // number that knows the order, "recipes vs. runes vs. both" cannot be compared at all.
    //
    // THE MODEL. For a plan that detonates monoliths in order 1..k, with recipe r_j chosen at each:
    //
    //     packs_j   = size(r_j)                      -- each extra runeshape adds a wave (0.5.0 notes)
    //     uplift_j  = effMult(propagated(r_j)) - 1   -- 0 for a neutral rune, negative for Oath/Wisdom
    //     M_j       = 1 + SUM over i<=j of uplift_i   -- accumulated buff acting on the packs at j
    //     monsterEx = baseMonsterEx * SUM_j packs_j * M_j
    //     rewardEx  = SUM_j reward(r_j)
    //     total     = rewardEx + monsterEx
    //
    // Two model choices worth naming, because they are assumptions and not facts:
    //   * uplifts ADD (1.35 and 1.25 together give ×1.60), rather than multiply (×1.69). Additive is the
    //     conservative reading and matches how the shipped per-monolith value is written. The magnitudes are
    //     server-side either way, so this is calibration, not truth.
    //   * a rune already in the chain contributes nothing a second time ("same runes do not stack",
    //     patch notes). Enforced by de-duplicating rune names along the chain.
    //
    // POWER IS NOT JUST ANOTHER RUNE. Power empowers the OTHER runes in the chain: an empowered rune's
    // uplift is multiplied by powerFactor (that is exactly what the plugin's RuneChainEffMult does with the
    // station's +0x5d flag). So a Power taken EARLY is worth (powerFactor - 1) x the uplift of everything
    // that comes after it, and a Power taken last is worth only its own 1.30. Nothing else in the model has
    // that shape -- every other rune's value depends on the order only through how many packs follow it.
    // Conservative reading used here: Power empowers runes entering the chain AFTER it, not retroactively
    // the ones already propagating. Unverified either way.
    //
    // RECIPE CHOICE is a backward pass over the tour: with "packs still ahead" and "uplift-weight still
    // ahead" both known, each monolith's best recipe is
    //     argmax(reward + baseEx*size + baseEx*(size + packsAhead)*uplift + powerBonus)
    // where powerBonus = (powerFactor - 1) * (uplift-weight ahead) when this recipe propagates Power. That
    // bonus makes the choice non-separable (a later monolith's best recipe depends on whether Power lands
    // earlier), so backward selection and the forward Power scan are iterated to a fixed point (<= 4 passes).
    // Duplicate-rune suppression stays a greedy forward correction.
    internal static class Strategy
    {
        internal sealed class Eval
        {
            public double RewardEx;
            public double MonsterExBase;    // monster loot with NO runes at all — the floor any plan gets
            public double MonsterExUplift;  // what the propagated runes add on top
            public double Total => this.RewardEx + this.MonsterExBase + this.MonsterExUplift;
            public int Packs;
            public List<string> Picks = new();   // "M07 Opulent ×1.35 (size 4, 25 ex)" per visited monolith

            // Per detonation position, parallel to the tour: the chosen recipe's wave count and the uplift
            // its propagated rune actually contributes (0 for a neutral rune or a suppressed duplicate).
            public List<int> Sizes = new();
            public List<double> Uplifts = new();
        }

        private const string PowerRune = "Power";

        private static bool IsPower(RunecraftHelperCore.SimOfferView? o) =>
            o != null && string.Equals(o.Rune, PowerRune, StringComparison.Ordinal);

        // `order` = monoliths in detonation order (indices into `monos`), already filtered to the ones the
        // charge budget actually reaches.
        internal static Eval Evaluate(List<SimMono> monos, List<int> order, double baseMonsterEx,
                                      double powerFactor)
        {
            var e = new Eval();
            int k = order.Count;
            if (k == 0) return e;

            // powerOn[j] = a Power rune is already propagating by the time monolith j is detonated. Unknown
            // until the recipes are chosen, and the choice depends on it, so iterate to a fixed point.
            var powerOn = new bool[k];
            var chosen = new RunecraftHelperCore.SimOfferView?[k];
            for (int iter = 0; iter < 4; iter++)
            {
                chosen = SelectRecipes(monos, order, baseMonsterEx, powerFactor, powerOn);
                var next = new bool[k];
                bool on = false;
                for (int j = 0; j < k; j++) { next[j] = on; on |= IsPower(chosen[j]); }

                bool same = true;
                for (int j = 0; j < k; j++) if (next[j] != powerOn[j]) { same = false; break; }
                powerOn = next;
                if (same) break;
            }

            // Forward pass: accumulate the buff, suppressing a rune already propagating, and empowering the
            // runes that enter after a Power.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            double m = 1.0;
            for (int j = 0; j < k; j++)
            {
                var o = chosen[j];
                if (o == null) continue;
                bool dup = !string.IsNullOrEmpty(o.Rune) && !seen.Add(o.Rune);
                double uplift = dup || string.IsNullOrEmpty(o.Rune)
                    ? 0.0
                    : (o.LootMult - 1.0) * (powerOn[j] ? powerFactor : 1.0);
                m += uplift;

                e.RewardEx += o.RewardEx;
                e.Packs += o.Size;
                e.Sizes.Add(o.Size);
                e.Uplifts.Add(uplift);
                e.MonsterExBase += baseMonsterEx * o.Size;
                e.MonsterExUplift += baseMonsterEx * o.Size * (m - 1.0);
                e.Picks.Add($"{monos[order[j]].Name} {(string.IsNullOrEmpty(o.Rune) ? "-" : o.Rune)}" +
                            $"{(dup ? " dup" : string.Empty)}{(powerOn[j] && uplift != 0 ? " empowered" : string.Empty)}" +
                            $" +{uplift:F2} size {o.Size} · {o.RewardEx:F0} ex");
            }

            return e;
        }

        private static RunecraftHelperCore.SimOfferView?[] SelectRecipes(
            List<SimMono> monos, List<int> order, double baseMonsterEx, double powerFactor, bool[] powerOn)
        {
            int k = order.Count;
            var chosen = new RunecraftHelperCore.SimOfferView?[k];
            double packsAhead = 0;      // packs at positions after the one being decided
            double upliftAhead = 0;     // SUM over later positions of uplift_i * packs from i onward * baseEx

            for (int j = k - 1; j >= 0; j--)
            {
                var offers = monos[order[j]].OfferViews;
                RunecraftHelperCore.SimOfferView? best = null;
                double bestVal = double.NegativeInfinity;
                foreach (var o in offers)
                {
                    // Three terms, and the middle one is the easy one to forget: SIZE is the wave count, so a
                    // longer combination is monster loot in itself, whatever it pays or propagates.
                    double uplift = (o.LootMult - 1.0) * (powerOn[j] ? powerFactor : 1.0);
                    double val = o.RewardEx
                               + (baseMonsterEx * o.Size)
                               + (baseMonsterEx * (o.Size + packsAhead) * uplift);

                    // ...and the Power cross-term: taking Power HERE empowers every rune after it.
                    if (IsPower(o) && !powerOn[j]) val += (powerFactor - 1.0) * upliftAhead;

                    if (val > bestVal) { bestVal = val; best = o; }
                }

                chosen[j] = best;
                if (best != null)
                {
                    double packsFromHere = best.Size + packsAhead;
                    double up = (best.LootMult - 1.0) * (powerOn[j] ? powerFactor : 1.0);
                    upliftAhead += up * packsFromHere * baseMonsterEx;
                    packsAhead = packsFromHere;
                }
            }

            return chosen;
        }

        // ── candidate anchor sets ────────────────────────────────────────────────────────────────────────
        // Best-recipe value per monolith, ignoring runes entirely.
        internal static double RecipeValue(SimMono m)
        {
            double best = 0;
            foreach (var o in m.OfferViews) best = Math.Max(best, o.RewardEx);
            return best;
        }

        // Strongest uplift this monolith could propagate (0 when it has nothing worth propagating).
        internal static double RuneUplift(SimMono m)
        {
            double best = 0;
            foreach (var o in m.OfferViews) best = Math.Max(best, o.EffMult - 1.0);
            return best;
        }

        internal static HashSet<int> TopBy(List<SimMono> monos, Func<SimMono, double> key, int take, double minKey)
        {
            var idx = new List<int>();
            for (int i = 0; i < monos.Count; i++) if (key(monos[i]) > minKey) idx.Add(i);
            idx.Sort((a, b) => key(monos[b]).CompareTo(key(monos[a])));
            if (idx.Count > take) idx.RemoveRange(take, idx.Count - take);
            return new HashSet<int>(idx);
        }

        // Which of the routed anchors the charges actually reached, in tour order. The criterion is the
        // planner's own: an anchor is collected when some charge sits within the blast radius of it.
        internal static List<int> Reached(List<SimMono> monos, SimResult res, float effRadius)
        {
            var order = new List<int>();
            float r2 = effRadius * effRadius;
            foreach (var name in res.AnchorOrder)
            {
                int i = monos.FindIndex(m => string.Equals(m.Name, name, StringComparison.Ordinal));
                if (i < 0) continue;
                foreach (var c in res.ChargePts)
                {
                    if (Vector2.DistanceSquared(c, monos[i].Pos) <= r2) { order.Add(i); break; }
                }
            }

            return order;
        }
    }
}
