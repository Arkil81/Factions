#!/usr/bin/env python3
"""FAC-393 check: do held wants get served, and how long do they wait?

Input: DecisionLog lines holding HAUL_LOAD_GATED and HAUL_RESOLVED (v0.341.1+, which carry
items=), in time order, from one or more files. Every (destination, item) first named by a
HAUL_LOAD_GATED line at or after START is matched to the next HAUL_RESOLVED whose items= delivers
that item to that destination.

    findstr /C:HAUL_LOAD_GATED /C:HAUL_RESOLVED Factions-2026-09-25.log > d1.txt
    findstr /C:HAUL_LOAD_GATED /C:HAUL_RESOLVED Factions-2026-09-26.log > d2.txt
    python3 held_wants.py 21:47:00 d1.txt d2.txt [--horizon 6]

The log stamps box-local HH:MM:SS with no date, so a day is counted across midnight: a line more
than 12 h earlier than the latest one seen starts the next day. That holds across file boundaries
too, so the files may be passed separately or concatenated, but they must be in order. START is on
the first day. A `path:` prefix, which findstr adds when given several files, is ignored.
"""
import argparse
import re

DAY = 86400
ROLLOVER = DAY // 2

_STAMP = re.compile(r'^(?:.*?\.(?:log|txt):)?(\d{2}):(\d{2}):(\d{2})')
_GATED = re.compile(r' HAUL_LOAD_GATED (\S+) x\d+ src=\S+ dest=(\S+)')
_RESOLVED = re.compile(r' -> (\S+) delivered=\d+ items=(\S+)')


def stamp_seconds(line):
    """Seconds since midnight for a log line, or None when it carries no leading HH:MM:SS."""
    m = _STAMP.match(line)
    if not m:
        return None
    h, mi, s = int(m.group(1)), int(m.group(2)), int(m.group(3))
    return h * 3600 + mi * 60 + s


def parse_start(text):
    h, m, s = map(int, text.split(':'))
    return h * 3600 + m * 60 + s


class Clock:
    """Turns time-of-day stamps into seconds since the first day's midnight."""

    def __init__(self):
        self.day = 0
        self.latest = None

    def absolute(self, tod):
        if self.latest is not None and tod < self.latest - ROLLOVER:
            self.day += 1
            self.latest = tod
        elif self.latest is None or tod > self.latest:
            self.latest = tod
        return self.day * DAY + tod


def analyse(lines, start, horizon_hours):
    clock = Clock()
    first_held = {}
    served = {}
    last_t = None
    for line in lines:
        tod = stamp_seconds(line)
        if tod is None:
            continue
        t = clock.absolute(tod)
        if t < start:
            continue
        last_t = t
        if 'HAUL_LOAD_GATED' in line:
            m = _GATED.search(line)
            if m:
                first_held.setdefault((m.group(2), m.group(1)), t)
        elif 'HAUL_RESOLVED' in line:
            m = _RESOLVED.search(line)
            if not m:
                continue
            dest = m.group(1)
            for kv in m.group(2).split(','):
                key = (dest, kv.rsplit('=', 1)[0])
                if key in first_held and key not in served:
                    served[key] = t

    waits = sorted((served[k] - first_held[k]) / 60.0 for k in served)
    horizon = horizon_hours * 3600
    eligible = [k for k, t in first_held.items() if last_t is not None and last_t - t >= horizon]
    unserved = [k for k in eligible if k not in served]
    return {
        'start': start, 'end': last_t, 'held': len(first_held), 'served': len(served),
        'waits': waits, 'eligible': len(eligible), 'unserved': len(unserved),
    }


def fmt(seconds):
    day, rest = divmod(int(seconds), DAY)
    h, rest = divmod(rest, 3600)
    m, s = divmod(rest, 60)
    return ('+%dd ' % day if day else '') + '%02d:%02d:%02d' % (h, m, s)


def report(r, horizon_hours):
    if r['end'] is None:
        return 'no lines at or after the start time'
    out = ['window %s-%s (%.2f h); held (dest,item) pairs %d'
           % (fmt(r['start']), fmt(r['end']), (r['end'] - r['start']) / 3600.0, r['held']),
           'served at all: %d (%.0f%%)' % (r['served'], 100.0 * r['served'] / max(r['held'], 1))]
    w = r['waits']
    if w:
        out.append('wait minutes p50 %.0f  p90 %.0f  max %.0f' % (w[len(w) // 2], w[9 * len(w) // 10], w[-1]))
    if r['eligible']:
        out.append('held at least %g h before the last line: %d; never served: %d (%.0f%%)'
                   % (horizon_hours, r['eligible'], r['unserved'], 100.0 * r['unserved'] / r['eligible']))
    else:
        out.append('no pair has been held %g h yet; rerun later' % horizon_hours)
    return '\n'.join(out)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('start', help='HH:MM:SS on the first day')
    ap.add_argument('files', nargs='+', help='extracts in time order')
    ap.add_argument('--horizon', type=float, default=6.0, help='hours a want may wait (default 6)')
    args = ap.parse_args()

    def lines():
        for path in args.files:
            with open(path, errors='replace') as f:
                for line in f:
                    yield line

    print(report(analyse(lines(), parse_start(args.start), args.horizon), args.horizon))


if __name__ == '__main__':
    main()
