#!/usr/bin/env python3
"""
FAC-419 slice 1 acceptance: the shipped library covers what the grammar and the catalogue need.

  python test_manifest.py --catalogue station_name_catalogue.json [--dest DIR]

Exits non-zero on the first uncovered key, and prints what it checked so that an empty library
cannot pass silently.
"""

import argparse
import json
import os
import random
import sys

import lines as L
import render_assets as R


def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--catalogue", required=True)
    ap.add_argument("--dest", default=R.DEFAULT_DEST)
    args = ap.parse_args()
    lib = L.Library(args.dest)
    clips = lib.m["clips"]

    names, _ = R.catalogue_names(args.catalogue)
    pairs = {(w.lower(), p) for n in names for w, p in R.name_words(n)}
    missing = sorted(f"word.{p}.{w}" for w, p in pairs if f"word.{p}.{w}" not in clips)
    if missing:
        fail(f"{len(missing)} name clips missing, e.g. {missing[:5]}")
    for d in range(10):
        if f"digit.{d}" not in clips: fail(f"digit.{d}")
    for w in R.NATO:
        if f"letter.{w[0]}" not in clips: fail(f"letter.{w[0]}")
    for slot, texts in R.PHRASES.items():
        if len(lib.m["slots"].get(slot, [])) != len(texts): fail(f"slot {slot}")

    bad = []
    for key, rel in clips.items():
        path = os.path.join(args.dest, rel)
        if not os.path.exists(path):
            bad.append(f"{key}: file missing")
            continue
        with open(path, "rb") as f:
            head = f.read(64)
        fmt_tag = int.from_bytes(head[20:22], "little")
        rate = int.from_bytes(head[24:28], "little")
        chans = int.from_bytes(head[22:24], "little")
        if fmt_tag != 7 or rate != 24000 or chans != 1:
            bad.append(f"{key}: fmt={fmt_tag} rate={rate} ch={chans}")
    if bad:
        fail(f"{len(bad)} clips malformed, e.g. {bad[:5]}")

    # Every catalogue name builds without falling back to the node id.
    rng = random.Random(1)
    fallbacks = [n for n in names if L.name_steps(lib, n, "N000")[-1] == "phrase.control.0"]
    if fallbacks:
        fail(f"{len(fallbacks)} catalogue names fall back to the id, e.g. {fallbacks[:3]}")
    for ev in L.EVENTS:
        L.line(lib, ev, names[0], "N093", tag="METC", inbound=3, departing=2, holding=4, rng=rng,
               fleet_id="646CEA", position=4, departure=True, divert_name=names[-1], divert_id="N765")

    # Every hail, in every speaker's own voice: no speaker may fall back to the controller.
    speakers = lib.m.get("speakers", [])
    if [sp["model"] for sp in speakers] != list(R.A.HAIL_SPEAKERS):
        fail(f"speakers {speakers} are not {R.A.HAIL_SPEAKERS}")
    for s in range(len(speakers)):
        if s:
            for d in range(10):
                if R.speaker_key(s, f"digit.{d}") not in clips: fail(f"speaker {s} digit.{d}")
            for slot, texts in R.PHRASES.items():
                if len(lib.m["slots"].get(R.speaker_key(s, slot), [])) != len(texts): fail(f"speaker {s} slot {slot}")
            # Every controller line, in this speaker's own voice only (fx is shared).
            for ev in L.EVENTS:
                steps = L.line(lib, ev, names[0], "N093", tag="METC", inbound=3, departing=2, holding=4, rng=rng,
                               fleet_id="646CEA", position=4, departure=True, divert_name=names[-1],
                               divert_id="N765", speaker=s)
                keys = [k for k in steps if isinstance(k, str) and not k.startswith("fx.")]
                if any(not k.startswith(f"s{s}.") for k in keys) or any(k not in clips for k in keys):
                    fail(f"speaker {s} {ev}: {keys}")
        for tone in L.HAIL_TONES:
            if len(lib.m["slots"].get(R.speaker_key(s, f"hail.{tone}"), [])) != len(R.HAIL_PHRASES[f"hail.{tone}"]):
                fail(f"speaker {s} slot hail.{tone}")
            steps = L.hail(lib, tone, "METC", s, rng)
            keys = [k for k in steps if isinstance(k, str) and not k.startswith("fx.")]
            if s and any(not k.startswith(f"s{s}.") for k in keys):
                fail(f"speaker {s} {tone} borrows the controller: {keys}")
            missing = [k for k in keys if k not in clips]
            if missing:
                fail(f"speaker {s} {tone} misses {missing}")

    size = sum(os.path.getsize(os.path.join(args.dest, p)) for p in clips.values())
    print(f"PASS: {len(names)} catalogue names, {len(pairs)} word/position clips, 10 digits, "
          f"{len(R.NATO)} letters, {len(R.PHRASES)} phrase slots, {len(speakers)} hail speakers x "
          f"{len(L.HAIL_TONES)} tones, {len(clips)} clips on disk, "
          f"{size / 1e6:.1f} MB, all 24 kHz mono mu-law")


if __name__ == "__main__":
    main()
