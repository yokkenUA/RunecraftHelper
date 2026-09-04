# -*- coding: utf-8 -*-
"""Was the geometric order wrong on Scorched Cay?

Real data, read live out of the plugin (2026-09-04, ExpeditionLogBook_Wastes, 18 charges).
Straight-line distance is used as the walk metric: the planner's own log shows path == straight
line on all 8 spine hops of this map (132/122/162/113/52/207/62/181), so the proxy is exact here.
"""
import itertools
import math

DET = (688.0, 568.0)
BASE_EX = 30.0        # Settings.RuneChainBaseMonsterEx
EFF_DIST = 108.0      # Grand placement distance
BUDGET = 18

# name, pos, waves, reward_ex, uplift, locked
# uplift: what the monolith's gold-socket rune adds to the loot multiplier.
#   A3/A5 both offer Rebirth (+0.10) -- only the FIRST one visited counts (duplicates don't stack).
#   A8 has Opulent (+0.35) and its recipe is already COMMITTED, so it propagates no matter what.
M = [
    ("A1 (556,568) Divine",   (556.0, 568.0), 4, 488.20, 0.00, "Life (locked)"),
    ("A2 (554,690) Prism",    (554.0, 690.0), 3,  27.85, 0.00, "Prismatic (locked)"),
    ("A3 (666,806) Chaos",    (666.0, 806.0), 5, 145.58, 0.10, "Rebirth"),
    ("A4 (776,832) SpiritGem",(776.0, 832.0), 5,  40.23, 0.00, "Life"),
    ("A5 (794,881) Alloy",    (794.0, 881.0), 4,   4.33, 0.10, "Rebirth (dup of A3)"),
    ("A6 (588,898) Chaos",    (588.0, 898.0), 4,  97.05, 0.00, "Fire"),
    ("A7 (592,960) Prism",    (592.0, 960.0), 3,  27.85, 0.00, "Prismatic"),
    ("A8 (416,1002) Opulent", (416.0, 1002.0), 5, 34.21, 0.35, "Opulent (locked)"),
]
REBIRTH = {2, 4}      # indices that share the Rebirth rune

# What the 8 spare charges actually bought in the shipped plan, in ex per charge (from the log's
# cluster list: 25,25,22,22,3,2,2 and one unused). Extra spine walking is paid out of this tail,
# cheapest cluster first -- so the first extra charges are nearly free.
SPARE_LADDER = [0.0, 2.0, 2.0, 3.0, 22.0, 22.0, 25.0, 25.0]


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def path_len(order):
    total, prev = 0.0, DET
    for j in order:
        total += dist(prev, M[j][1])
        prev = M[j][1]
    return total


def uplift_ex(order):
    """BASE_EX * sum(waves_j * cumulative uplift at j). Own waves included (a rune buffs its own
    monolith's packs too), duplicates suppressed."""
    cum, total, seen_rebirth = 0.0, 0.0, False
    for j in order:
        up = M[j][4]
        if j in REBIRTH:
            if seen_rebirth:
                up = 0.0
            elif up > 0:
                seen_rebirth = True
        cum += up
        total += M[j][2] * cum
    return BASE_EX * total


def spare_loss(charges):
    """Cost of a longer spine: each charge past the shipped 10 eats a spare cluster."""
    extra = max(0, charges - 10)
    return sum(SPARE_LADDER[:extra]) if extra <= len(SPARE_LADDER) else float('inf')


def score(order):
    p = path_len(order)
    ch = math.ceil(p / EFF_DIST)
    rew = sum(M[j][3] for j in order)
    up = uplift_ex(order)
    return dict(path=p, charges=ch, reward=rew, uplift=up,
                loss=spare_loss(ch), net=rew + up - spare_loss(ch))


SHIPPED = (0, 1, 2, 3, 4, 5, 6, 7)          # the order the planner actually laid
OPULENT_FIRST = (7, 6, 5, 2, 4, 3, 1, 0)    # go to A8 first, then walk back up the chain


def show(tag, order):
    r = score(order)
    print("{0:<22} path {1:6.0f}  {2:2d} chg  reward {3:7.1f}  uplift {4:7.1f}  "
          "spare -{5:5.1f}  NET {6:8.1f}   {7}".format(
              tag, r['path'], r['charges'], r['reward'], r['uplift'], r['loss'], r['net'],
              " -> ".join(M[j][0].split()[0] for j in order)))
    return r


print("=" * 132)
print("Scorched Cay, 8 anchors, base {0:.0f} ex/wave, budget {1} charges".format(BASE_EX, BUDGET))
print("=" * 132)
base = show("shipped (geometric)", SHIPPED)
opu = show("Opulent first", OPULENT_FIRST)

best = max(itertools.permutations(range(8)), key=lambda o: score(o)['net'])
bst = show("optimal (brute 8!)", best)

print()
print("Opulent first vs shipped : {0:+.1f} ex".format(opu['net'] - base['net']))
print("optimal      vs shipped : {0:+.1f} ex".format(bst['net'] - base['net']))
print()

# Where does the Opulent monolith sit in each order, and what is its rune worth there?
for tag, order in (("shipped", SHIPPED), ("Opulent first", OPULENT_FIRST), ("optimal", best)):
    pos = order.index(7)
    ahead = sum(M[j][2] for j in order[pos + 1:])
    own = M[7][2]
    print("{0:<14} Opulent at stop #{1}  waves own {2} + ahead {3:2d} = {4:2d}  "
          "-> rune worth {5:6.1f} ex".format(
              tag, pos + 1, own, ahead, own + ahead, BASE_EX * 0.35 * (own + ahead)))

print()
print("Sensitivity: does the answer flip with base ex/wave?")
print("{0:>8}  {1:>12}  {2:>12}  {3:>12}".format("base", "shipped", "Opulent 1st", "optimal"))
for b in (5, 10, 15, 20, 30, 45, 60):
    BASE_EX = b
    globals()['BASE_EX'] = b
    a, o = score(SHIPPED), score(OPULENT_FIRST)
    bb = max(itertools.permutations(range(8)), key=lambda x: score(x)['net'])
    print("{0:>8}  {1:>12.1f}  {2:>12.1f}  {3:>12.1f}  {4}".format(
        b, a['net'], o['net'], score(bb)['net'],
        " -> ".join(M[j][0].split()[0] for j in bb)))
