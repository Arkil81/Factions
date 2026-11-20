#!/usr/bin/env python3
"""
FAC-419 slice 1 gate (docs/atc-voice.md, D3): can a station name be joined from single-word clips
without it sounding like a list?

Twenty real station names, each in its real line "<Name> control. Cleared to dock.", three ways:

  A  the name and "control." rendered whole (the reference)
  B  the name joined from words rendered alone
  C  the name joined from words each rendered with a trailing comma, which keeps a continuation
     intonation instead of a sentence-final fall
  D  each word cut out of a carrier sentence at its position in a name (first, middle, last), using
     Piper's phoneme alignments; the last word is cut together with "control.", so the name runs
     into it as it does in A. Round 5 judged B and C list-like (weighting, and the pause between
     words); D keeps each word's in-phrase stress and has no pause at the joins.
  E1, E2  D with spacing, after the operator judged D's words too close and its numbers jumbled:
     a short silence between name words instead of D's crossfade, and digits read one by one
     (each rendered alone, which is how a controller reads them) with a longer silence between
     digits and before the name that follows. E1 and E2 differ only in how long those are.

"control." and "Cleared to dock." are separate fragments in all three, 45 ms apart, as in the
shipped grammar. Numbers are spoken digit by digit in every variant ("1647" -> "one six four seven").

Usage: python render_name_test.py --names names.txt [--voices voices] [--out out/nametest]
names.txt: one station name per line (the live set, e.g. every DisplayName in the brains).
"""

import argparse
import os
import random
import re

import numpy as np

import atc_audio as A

DIGITS = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"]


def spoken_words(name):
    """Words as the voice says them: digit groups become one word per digit."""
    out = []
    for tok in re.findall(r"[A-Za-z][A-Za-z']*|\d+", name):
        if tok.isdigit():
            out += [DIGITS[int(c)] for c in tok]
        else:
            out.append(tok)
    return out


CARRIERS = {
    # position -> (carrier text, index of the first word kept, keep to the end?)
    "first": ("{w} Harbor control.", 0, False),
    "mid": ("Grand {w} Harbor control.", 1, False),
    "last": ("Grand {w} control.", 1, True),
}
XFADE_S = 0.008
SPACINGS = {  # variant -> (between name words, between digits, number -> name), seconds
    "E1_spaced": (0.030, 0.080, 0.150),
    "E2_spaced_more": (0.055, 0.120, 0.220),
}
EDGE_FADE_S = 0.004


def edge_fade(x, s=EDGE_FADE_S):
    """A cut lands mid-word-boundary, not in silence; a 4 ms fade keeps it from clicking."""
    n = min(int(s * A.SR), len(x) // 2)
    y = x.copy()
    y[:n] *= np.linspace(0, 1, n, dtype=np.float32)
    y[-n:] *= np.linspace(1, 0, n, dtype=np.float32)
    return y


def spaced_name(ws, positions, cut, digit_clip, gaps):
    """Name words cut from carriers with a short gap between them; digits rendered alone with a
    longer gap; a longer gap still where a number meets a name word."""
    word_gap, digit_gap, group_gap = gaps
    parts = []
    for i, (w, p) in enumerate(zip(ws, positions)):
        is_digit = w in DIGITS
        clip = digit_clip(w) if is_digit else edge_fade(cut(w, p))
        if i:
            prev_digit = ws[i - 1] in DIGITS
            gap = digit_gap if (is_digit and prev_digit) else group_gap if (is_digit != prev_digit) else word_gap
            parts.append(A.silence(gap))
        parts.append(clip)
    # A name ending in a digit still needs "control." after it.
    if ws[-1] in DIGITS:
        parts += [A.silence(word_gap), CONTROL_CLIP[0]]
    return np.concatenate(parts)


CONTROL_CLIP = []


def synth_aligned(voice, text):
    """24 kHz speech plus the sample index of each word boundary (the midpoint of every
    space phoneme), from Piper's alignments."""
    from piper import SynthesisConfig
    cfg = SynthesisConfig(length_scale=A.LENGTH_SCALE, noise_scale=A.NOISE_SCALE, noise_w_scale=A.NOISE_W_SCALE)
    audio, cuts, pos = [], [], 0
    for chunk in voice.synthesize(text, syn_config=cfg, include_alignments=True):
        for a in chunk.phoneme_alignments:
            if a.phoneme == " ":
                cuts.append(pos + a.num_samples // 2)
            pos += a.num_samples
        audio.append(chunk.audio_int16_array)
        rate = chunk.sample_rate
    x = np.concatenate(audio).astype(np.float32) / 32768
    y = A.sox_fx(x, "rate", str(A.SR)) if rate != A.SR else x
    scale = A.SR / rate
    return y, [int(c * scale) for c in cuts]


def carrier_word(voice, word, position):
    text, k, to_end = CARRIERS[position]
    y, cuts = synth_aligned(voice, text.format(w=word))
    bounds = [0] + cuts + [len(y)]
    return y[bounds[k]:len(y) if to_end else bounds[k + 1]]


def crossfade_join(parts, xfade_s=XFADE_S):
    n = int(xfade_s * A.SR)
    out = parts[0].copy()
    for p in parts[1:]:
        ramp = np.linspace(0, 1, n, dtype=np.float32)
        out[-n:] = out[-n:] * (1 - ramp) + p[:n] * ramp
        out = np.concatenate([out, p[n:]])
    return out


def pick(names, n, seed):
    """A spread rather than a random draw: numbered names, apostrophes, one to four words."""
    rng = random.Random(seed)
    names = sorted(set(names))
    buckets = {
        "numbered": [x for x in names if re.search(r"\d", x)],
        "apostrophe": [x for x in names if "'" in x and not re.search(r"\d", x)],
        "one word": [x for x in names if len(spoken_words(x)) == 1],
        "two words": [x for x in names if len(spoken_words(x)) == 2 and "'" not in x],
        "three+ words": [x for x in names if len(spoken_words(x)) >= 3 and not re.search(r"\d", x) and "'" not in x],
    }
    quota = {"numbered": 4, "apostrophe": 3, "one word": 2, "two words": 6, "three+ words": 5}
    chosen = []
    for k, q in quota.items():
        pool = [x for x in buckets[k] if x not in chosen]
        chosen += rng.sample(pool, min(q, len(pool)))
    # A bucket can be empty (no live name is a single word), so top up from what is left.
    rest = [x for x in names if x not in chosen]
    chosen += rng.sample(rest, max(0, n - len(chosen)))
    return chosen[:n]


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser()
    ap.add_argument("--names", required=True)
    ap.add_argument("--voices", default=os.path.join(here, "voices"))
    ap.add_argument("--out", default=os.path.join(here, "out", "nametest"))
    ap.add_argument("--seed", type=int, default=19)
    args = ap.parse_args()

    names = [l.strip() for l in open(args.names, encoding="utf-8") if l.strip()]
    chosen = pick(names, 20, args.seed)
    alan = A.load_voice(args.voices)
    from piper import PiperVoice
    alan_aligned = PiperVoice.load(os.path.join(args.voices, A.VOICE_MODEL + ".onnx"), include_alignments=True)
    furn = A.Furniture(seed=args.seed)
    rng = np.random.default_rng(args.seed)

    control = A.synth(alan, "control.")
    CONTROL_CLIP.append(control)
    tail = A.synth(alan, "Cleared to dock.")
    word_cache = {}

    def word(w, comma):
        key = (w, comma)
        if key not in word_cache:
            word_cache[key] = A.synth(alan, w + ("," if comma else ""))
        return word_cache[key]

    reels = {"A_whole": [], "B_words": [], "C_words_comma": [], "D_carrier_cut": [],
             "E1_spaced": [], "E2_spaced_more": []}
    carrier_cache = {}

    def cut(w, position):
        if (w, position) not in carrier_cache:
            carrier_cache[(w, position)] = carrier_word(alan_aligned, w, position)
        return carrier_cache[(w, position)]
    pause = A.silence(1.0)
    for name in chosen:
        spoken = " ".join(spoken_words(name))
        whole = A.synth(alan, spoken + " control.")
        b = np.concatenate([word(w, False) for w in spoken_words(name)])
        c = np.concatenate([word(w, True) for w in spoken_words(name)])
        ws = spoken_words(name)
        positions = ["first"] + ["mid"] * (len(ws) - 2) + ["last"] if len(ws) > 1 else ["last"]
        d = A.trim_silence(crossfade_join([cut(w, p) for w, p in zip(ws, positions)]))
        lines = {
            "A_whole": A.join([whole, tail]),
            "B_words": A.join([b, control, tail]),
            "C_words_comma": A.join([c, control, tail]),
            "D_carrier_cut": A.join([d, tail]),
            **{k: A.join([spaced_name(ws, positions, cut, lambda w: word(w, False), g), tail])
               for k, g in SPACINGS.items()},
        }
        for k, v in lines.items():
            reels[k] += [A.transmission(A.radio(v), furn, rng), pause]
    os.makedirs(args.out, exist_ok=True)
    for k, parts in reels.items():
        A.save(np.concatenate(parts[:-1]), os.path.join(args.out, k + ".wav"))
    with open(os.path.join(args.out, "names.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(chosen) + "\n")
    print("\n".join(f"{i + 1:2d}. {n}" for i, n in enumerate(chosen)))


if __name__ == "__main__":
    main()
