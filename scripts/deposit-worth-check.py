#!/usr/bin/env python3
"""Recompute every sampled node's ResourceValue from the brain files and say which price scale the
running server used: ore (FAC-191, v0.504.0) or ingot (before it).

Read-only. Point it at a directory holding copies of the live brains
(C:\\Torch\\awl-shared\\Factions\\brains\\*.json):

    python3 -I scripts/deposit-worth-check.py <brains-dir>

Mirrors NodeContent.ResourceValueFor for a mineable node: floor 0.2, cap 1.0, confidence =
points / SurveyMath.PointsToResolve(0.0008), log10(1 + vpg) / log10(1 + saturation). Ore prices are
the resolver's (ingot price x SE refine yield); ingot prices are the collapsed item_prices.json entries.
"""
import glob
import json
import math
import os
import sys

INGOT = {'Iron': 143, 'Nickel': 250, 'Silicon': 143, 'Cobalt': 1000, 'Magnesium': 28571,
         'Silver': 2000, 'Gold': 20000, 'Uranium': 35000, 'Platinum': 70000, 'Stone': 10, 'Ice': 50}
ORE = {'Iron': 100, 'Nickel': 100, 'Silicon': 100, 'Cobalt': 300, 'Magnesium': 200,
       'Silver': 200, 'Gold': 200, 'Uranium': 350, 'Platinum': 350, 'Stone': 10, 'Ice': 50}
NEED = math.ceil((1.645 / 0.10) ** 2 * (1 - 0.0008) / 0.0008)


def worth(deposit, points, prices, saturation):
    vpg = sum(e['R'] / points * prices.get(e['O'], 0) / prices['Iron'] for e in deposit)
    proven = 0.0 if vpg <= 0 else min(1.0, math.log10(1 + vpg) / math.log10(1 + saturation))
    return 0.2 + (proven - 0.2) * min(1.0, points / NEED)


def rare(deposit, prices):
    # NodeDeposit.ShareOf: by weight (S), not richness.
    total = sum(e['S'] for e in deposit)
    if total <= 0:
        return 0.0
    return sum(e['S'] / total * prices.get(e['O'], 0) for e in deposit) / prices['Iron']


def main(path):
    nodes, seen = [], set()
    for f in sorted(glob.glob(os.path.join(path, '*.json'))):
        with open(f, encoding='utf-8-sig') as fh:
            doc = json.load(fh)
        if not isinstance(doc, dict):
            continue
        for s in doc.get('Sources') or []:
            dep, pts = s.get('Deposit'), s.get('DepositPoints') or 0
            if dep and pts > 0 and s['Id'] not in seen:
                seen.add(s['Id'])
                nodes.append((dep, pts, s.get('ResourceValue') or 0.0))
    if not nodes:
        print('no sampled nodes found under ' + path)
        return 2
    n = len(nodes)
    ore_err = sum(abs(worth(d, p, ORE, 140.0) - rv) for d, p, rv in nodes) / n
    ing_err = sum(abs(worth(d, p, INGOT, 9000.0) - rv) for d, p, rv in nodes) / n
    print('sampled nodes: %d' % n)
    print('mean |persisted - recomputed|: ore-priced (sat 140) %.4f   ingot-priced (sat 9000) %.4f'
          % (ore_err, ing_err))
    print('foundable (>= 0.30) persisted: %d' % sum(rv >= 0.30 for _, _, rv in nodes))
    print('rare at 2.0 (ore scale): %d' % sum(rare(d, ORE) >= 2.0 for d, _, _ in nodes))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else '.'))
