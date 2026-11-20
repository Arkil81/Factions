#!/usr/bin/env python3
"""
Renders the reference reels the operator approved, from atc_audio's settled values:

  out/distance_ladder.wav   one line at near / mid / far / edge (edge fades)
  out/fragments.wav         the same line rendered whole, then joined from fragments
  out/final_mix.wav         80 s of chatter bed with four callouts across the distance range
  out/fx/*.wav              each callout's clean-radio voice, before any runtime treatment

Usage:
  python -m piper.download_voices --data-dir voices en_GB-alan-medium \
      en_GB-northern_english_male-medium en_US-lessac-medium en_US-ryan-high en_GB-jenny_dioco-medium
  python render_demo.py [--voices voices] [--out out] [--seed 7]

The voice differs slightly on every run (see atc_audio's module docstring); everything else is
seeded and reproduces exactly.
"""

import argparse
import os

import numpy as np

import atc_audio as A

# Draft callout script. Faction tags and node names are real ones from the test server. The
# fragment boundaries are where the client would join a variable in.
LINES = {
    "haul_inbound": ["Wayfarer Wharf control.", "Haul convoy,", "Mike Echo Tango Charlie,",
                     "on approach.", "Cleared to dock, bay two."],
    "trade_depart": ["Tradewind Depot control.", "Trade convoy,", "India Golf Sierra Lima,",
                     "you are clear to depart.", "Safe travels."],
    "raid_warning": ["All stations, all stations.", "Hostile fleet,", "Sierra Papa Romeo Tango,",
                     "inbound", "Freeport Depot.", "Weapons free."],
    "collapsed":    ["Wayfarer Wharf control.", "Traffic advisory:", "three convoys inbound,",
                     "two departing.", "Expect delays."],
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--voices", default=os.path.join(A.__file__.rsplit(os.sep, 1)[0], "voices"))
    ap.add_argument("--out", default=os.path.join(A.__file__.rsplit(os.sep, 1)[0], "out"))
    ap.add_argument("--seed", type=int, default=7)
    args = ap.parse_args()
    rng = np.random.default_rng(args.seed)
    furn = A.Furniture(seed=args.seed)

    alan = A.load_voice(args.voices)
    whole = {k: A.radio(A.synth(alan, " ".join(f))) for k, f in LINES.items()}
    joined = {k: A.radio(A.join([A.synth(alan, s) for s in f])) for k, f in LINES.items()}
    for k, v in joined.items():
        A.save(v, os.path.join(args.out, "fx", k + ".wav"))

    pause = A.silence(1.0)
    ladder = []
    for d in A.DISTANCE_SNR_DB:
        ladder += [A.transmission(joined["trade_depart"], furn, rng, d), pause]
    A.save(np.concatenate(ladder[:-1]), os.path.join(args.out, "distance_ladder.wav"))

    A.save(np.concatenate([A.transmission(whole["haul_inbound"], furn, rng), pause,
                           A.transmission(joined["haul_inbound"], furn, rng)]),
           os.path.join(args.out, "fragments.wav"))

    pool = []
    for model in A.CHATTER_VOICES:
        v = A.load_voice(args.voices, model)
        pool += [A.synth(v, " ".join(f), length_scale=0.88) for f in LINES.values()]
    calls = [(8.0, "haul_inbound", "mid"), (26.0, "raid_warning", "edge"),
             (44.0, "trade_depart", "far"), (62.0, "collapsed", "near")]
    rendered = [(t, A.transmission(joined[k], furn, rng, d)) for t, k, d in calls]
    spans = [(t, t + len(c) / A.SR) for t, c in rendered]
    bed, chatter = A.bed(80, spans, pool, furn, rng)
    mix = A.peak_to(bed, 0.35)
    for t, c in rendered:
        a = int(t * A.SR); mix[a:a + len(c)] += c
    A.save(A.peak_to(mix, 0.7), os.path.join(args.out, "final_mix.wav"))

    leak = max(float(np.max(np.abs(chatter[int((a - A.DUCK_PAD_S) * A.SR):int((b + A.DUCK_PAD_S) * A.SR)])))
               for a, b in spans)
    print(f"chatter peak inside callout windows: {leak} (must be 0.0)")
    if leak != 0.0:
        raise SystemExit("ducking leaked chatter into a callout")


if __name__ == "__main__":
    main()
