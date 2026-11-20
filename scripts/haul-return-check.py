#!/usr/bin/env python3
"""FAC-211 acceptance: how often a haul comes back, split by whether the sender's own demand row jumped.

Reads one DecisionLog (C:\\Torch\\awl-shared\\Factions\\logs\\Factions-<date>.log, or a findstr of it for
PROD_INPUT_DEMAND and HAUL_DISPATCHED) and counts dispatches that START inside [--from, --to - 1h], box-local,
so every dispatch has a whole hour in the file for its return. A dispatch is "returned" when the same faction
dispatches the same key dst -> src within the hour. --within shortens both, for a window too short for an hour. Run it once on a keep-own-want=off window and once on an
=on window of the same day.

Usage: haul-return-check.py <log> [--from HH:MM] [--to HH:MM] [--within MINUTES]
"""
import argparse, bisect, collections, re

DEMAND = re.compile(r'^(\d\d):(\d\d):(\d\d)\s+mgr (\S+) PROD_INPUT_DEMAND (\S+) need=(\d+) .*?(?:prev=(\d+) )?node=(\S+)')
HAUL = re.compile(r'^(\d\d):(\d\d):(\d\d)\s+mgr \[\w+\] (\S+) HAUL_DISPATCHED (\S+) x(\d+) (\S+) -> (\S+)')


def secs(h, m, s):
    return int(h) * 3600 + int(m) * 60 + int(s)


def hhmm(text):
    h, m = text.split(':')
    return int(h) * 3600 + int(m) * 60


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('log')
    ap.add_argument('--from', dest='start', default='00:00')
    ap.add_argument('--to', dest='end', default='24:00')
    ap.add_argument('--within', type=int, default=60, help='return window, minutes')
    a = ap.parse_args()
    within = a.within * 60
    lo, hi = hhmm(a.start), hhmm(a.end) - within

    rows, jumps, hauls = set(), collections.defaultdict(list), []
    with open(a.log, errors='replace') as f:
        for line in f:
            m = DEMAND.match(line)
            if m:
                h, mi, s, fac, key, need, prev, node = m.groups()
                rows.add((fac, node, key))
                if prev and int(need) >= 2 * int(prev):
                    jumps[(fac, node, key)].append(secs(h, mi, s))
                continue
            m = HAUL.match(line)
            if m:
                h, mi, s, fac, key, qty, src, dst = m.groups()
                hauls.append((secs(h, mi, s), fac, key, int(qty), src, dst))

    by_route = collections.defaultdict(list)
    for t, fac, key, _, src, dst in hauls:
        by_route[(fac, key, src, dst)].append(t)
    for times in by_route.values():
        times.sort()

    def after(times, t, within):
        i = bisect.bisect_right(times, t)
        return i < len(times) and times[i] - t <= within

    groups = collections.Counter()
    for t, fac, key, qty, src, dst in hauls:
        if not lo <= t <= hi:
            continue
        back = after(by_route.get((fac, key, dst, src), []), t, within)
        if key.startswith('Gas:'):
            group = key
        elif (fac, src, key) not in rows:
            group = 'item, sender has no input row (control)'
        elif after(jumps.get((fac, src, key), []), t - 1, within):
            group = 'item, sender row jumped up within window'
        else:
            group = 'item, sender row steady'
        groups[(group, back)] += 1

    span_h = max(1, hi - lo) / 3600.0
    print('window %s..%s, dispatches counted from the first %.1f h' % (a.start, a.end, span_h))
    for group in sorted({g for g, _ in groups}):
        n = groups[(group, True)] + groups[(group, False)]
        print('  %-44s %7d dispatches (%7.1f/h), returned within window %6d (%5.1f%%)'
              % (group, n, n / span_h, groups[(group, True)], 100.0 * groups[(group, True)] / n))


if __name__ == '__main__':
    main()
