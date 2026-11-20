#!/usr/bin/env python3
"""Tests for held_wants.py. Run: python3 scripts/haul-delivery/test_held_wants.py"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import held_wants as hw  # noqa: E402


def gated(t, item, dest, prefix=''):
    return '%s%s  mgr F HAUL_LOAD_GATED %s x5 src=S dest=%s loadL=5 capL=900000 fill=0 urgency=1\n' % (
        prefix, t, item, dest)


def resolved(t, dest, items, prefix=''):
    total = sum(int(kv.split('=')[-1]) for kv in items.split(','))
    return '%s%s  t1 HAUL_RESOLVED [ABC123] F -> %s delivered=%d items=%s\n' % (prefix, t, dest, total, items)


class HeldWantsTests(unittest.TestCase):

    def test_a_delivery_after_midnight_serves_a_want_held_before_it(self):
        lines = [gated('23:50:00', 'Ore:Iron', 'N1'), resolved('00:10:00', 'N1', 'Ore:Iron=5')]
        r = hw.analyse(lines, hw.parse_start('23:00:00'), 6)
        self.assertEqual(1, r['held'])
        self.assertEqual(1, r['served'])
        self.assertEqual([20.0], r['waits'])

    def test_a_want_held_past_the_horizon_across_midnight_counts_as_unserved(self):
        lines = [gated('20:00:00', 'Ore:Iron', 'N1'), gated('20:00:00', 'Ingot:Cobalt', 'N2'),
                 resolved('21:00:00', 'N2', 'Ingot:Cobalt=3'), resolved('03:00:00', 'N9', 'Ore:Stone=1')]
        r = hw.analyse(lines, hw.parse_start('19:00:00'), 6)
        self.assertEqual(2, r['eligible'])      # both held 7 h before the last line
        self.assertEqual(1, r['unserved'])      # N1's iron never arrived
        self.assertEqual(24 * 3600 + 3 * 3600, r['end'])

    def test_lines_slightly_out_of_order_do_not_start_a_new_day(self):
        lines = [gated('23:59:59', 'Ore:Iron', 'N1'), gated('23:59:58', 'Ore:Nickel', 'N1'),
                 resolved('23:59:59', 'N1', 'Ore:Iron=5')]
        r = hw.analyse(lines, hw.parse_start('23:00:00'), 6)
        self.assertEqual(23 * 3600 + 59 * 60 + 59, r['end'])
        self.assertEqual(1, r['served'])

    def test_two_midnights_count_two_days(self):
        clock = hw.Clock()
        stamps = [hw.parse_start(t) for t in ('22:00:00', '02:00:00', '14:00:00', '23:00:00', '01:00:00')]
        self.assertEqual(2 * hw.DAY + 3600, [clock.absolute(s) for s in stamps][-1])

    def test_a_findstr_path_prefix_is_ignored(self):
        lines = [gated('23:50:00', 'Ore:Iron', 'N1', prefix='d1.txt:'),
                 resolved('00:05:00', 'N1', 'Ore:Iron=5,Gas:Hydrogen=100', prefix='C:\\logs\\Factions-2026-09-26.log:')]
        r = hw.analyse(lines, hw.parse_start('23:00:00'), 6)
        self.assertEqual(1, r['served'])
        self.assertEqual([15.0], r['waits'])

    def test_lines_before_the_start_are_ignored(self):
        lines = [gated('10:00:00', 'Ore:Iron', 'N1'), resolved('11:00:00', 'N1', 'Ore:Iron=5')]
        r = hw.analyse(lines, hw.parse_start('12:00:00'), 6)
        self.assertEqual(0, r['held'])
        self.assertIsNone(r['end'])


if __name__ == '__main__':
    unittest.main()
