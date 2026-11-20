#!/usr/bin/env python3
"""
FAC-419 slice 1: renders the traffic-control voice library into src/FactionsUI/Content/Atc/ and
writes manifest.json, the only file the client reads to find a clip.

Incremental on purpose. Piper's speech differs on every run (atc_audio's docstring), so a full
re-render would rewrite every binary in git history. A clip already in the manifest, with its file
on disk, is never rendered again; --force is the only way to redo one.

What is rendered (docs/atc-voice.md, "Vocabulary and assets"):
  names/<position>/<slug>.wav  each catalogue name word, cut from a carrier sentence at its position
                               in a name; "last" clips carry "control." with them
  spelled/digit/<d>.wav        zero .. niner, one clip each
  spelled/letter/<L>.wav       the NATO alphabet, one clip each
  phrase/<slot>/<n>.wav        the grammar's fixed phrases, with variants
  chatter/<n>.wav              pre-rendered G1/G2 garbled bursts from the four other voices
  phrase/hail.<tone>/<n>.wav   trespass hails in the controller's voice (speaker 0)
  speaker/<s>/...              speakers 1-4: the hails, the grammar phrases, the NATO letters and
                               the digits in each of the four other voices, paced to the
                               controller's tempo (station-name words are speaker 0 only)
  fx/*.wav                     key-up, chirp, squelch tail, hostile alert, seamless static loop

Everything is 24 kHz mono 8-bit mu-law (WAVE format tag 7), the format SE's raw voice gets after
the client expands it to 16-bit PCM.

Usage:
  python render_assets.py --catalogue station_name_catalogue.json [--voices voices] [--dest DIR]
                          [--force] [--limit-words N]
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import tempfile

import numpy as np

import atc_audio as A

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DEST = os.path.normpath(os.path.join(HERE, "..", "..", "src", "FactionsUI", "Content", "Atc"))

DIGITS = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"]
NATO = ["Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett",
        "Kilo", "Lima", "Mike", "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango",
        "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu"]
COUNT_WORDS = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine"]

# Fixed phrases, by slot. The client's line builder names these slots; the manifest carries the
# variants, and the client picks one at random per line so a busy station does not sound looped.
PHRASES = {
    "control": ["control."],
    "kind.haul": ["Haul convoy,"],
    "kind.trade": ["Trade convoy,"],
    "kind.mine": ["Mining detail,"],
    "kind.relief": ["Relief squadron,"],
    "kind.hostile": ["Hostile fleet,"],
    "patrol": ["patrol,"],
    "arrive": ["on approach."],
    "arrive.close": ["Cleared to dock.", "Cleared to dock, bay two.", "Proceed to docking."],
    "depart": ["cleared to depart.", "you are cleared to depart."],
    "depart.close": ["Safe travels.", "Fly safe.", "Good travels."],
    "station": ["on station."],
    "allstations": ["All stations, all stations."],
    "inbound": ["inbound."],
    "weaponsfree": ["Weapons free."],
    "advisory": ["Traffic advisory:"],
    "advisory.close": ["Expect delays."],
    **{f"count.inbound.{i + 1}": [f"{w} {'convoy' if i == 0 else 'convoys'} inbound,"] for i, w in enumerate(COUNT_WORDS)},
    "count.inbound.many": ["multiple convoys inbound,"],
    **{f"count.departing.{i + 1}": [f"{w} departing."] for i, w in enumerate(COUNT_WORDS)},
    "count.departing.many": ["multiple departing."],
    # Traffic control's station queue (docs/traffic-control.md, "What the player hears").
    "hold": ["hold position,", "hold your position,"],
    "number": ["number"],
    "fordeparture": ["for departure."],
    "outermarker": ["hold at the outer marker.", "enter the hold at the outer marker."],
    "expectdelay": ["Expect delay."],
    "departuredelayed": ["departure delayed,"],
    "furtherdelay": ["Expect further delay."],
    "divert": ["divert, contact"],
    "clearedinbound": ["cleared inbound.", "you are cleared inbound."],
    **{f"count.holding.{i + 1}": [f"{w} holding."] for i, w in enumerate(COUNT_WORDS)},
    "count.holding.many": ["multiple holding."],
}

# Trespass hails (FAC-483), spoken after "<tag, spelled> control." by the faction's own speaker.
# Rendered in every voice in A.HAIL_SPEAKERS; speaker 0's clips are ordinary phrase clips.
HAIL_PHRASES = {
    "hail.welcome": ["Welcome to our space. You are among friends here.",
                     "Good to have you with us. You are among friends here."],
    "hail.notice": ["You have entered our space. Mind your conduct and you will have no trouble from us.",
                    "You are in our space. Keep it peaceful and you will have no trouble from us."],
    "hail.warning": ["You have entered our space. You are not welcome here. Turn back.",
                     "You are in our space without leave. Turn back now."],
    "hail.intercept": ["You were told to leave our space. Our patrol is cleared to engage.",
                       "You were warned. Our patrol is cleared to engage."],
    # Occupation (a presence raid on the faction's ground): the opening strip, then every later one.
    "hail.occupation": ["You are occupying our ground. You will be treated as an aggressor.",
                        "This ground is ours. Stay on it, and you will be treated as an aggressor."],
    "hail.held": ["You are still on our ground. This will not be forgotten.",
                  "You have not left our ground. This will not be forgotten."],
}
# What each speaker's pace is measured on: a hail-shaped line, timed at length_scale 1.0.
PACE_REFERENCE = ["Mike Echo Tango Charlie control. You have entered our space. Turn back now.",
                  "Welcome to our space. You are among friends here."]

CARRIERS = {
    # position -> (carrier text, index of the word kept, keep to the end?)
    "first": ("{w} Harbor control.", 0, False),
    "mid": ("Grand {w} Harbor control.", 1, False),
    "last": ("Grand {w} control.", 1, True),
}
EDGE_FADE_S = 0.004
CHATTER_BURSTS = 40
STATIC_LOOP_S = 8.0

# Chatter source: generic traffic, spoken by the four voices that are not the controller.
# Pilot calls (FAC-419 slice 5): always garbled, so the words only need to sound like traffic. Rendered
# with the radio band and light drive but NO static or fading: the client applies the physics per call.
PILOT_TEXT = [
    "Control, haul convoy inbound, requesting docking clearance.",
    "Control, trade convoy departing, thanks for the berth.",
    "Mining detail returning, hold is full, request approach vector.",
    "Patrol checking in, sector quiet, holding at the outer marker.",
    "Control, relief squadron on final, request priority berth.",
    "Copy that control, beginning our approach now.",
    "Control, we are clear of the pad, climbing to transit.",
    "Negative, still loading cargo, give us two minutes.",
]

CHATTER_TEXT = [
    "Haul convoy inbound, requesting docking clearance.",
    "Copy that, holding at the outer marker.",
    "Negative, we are still loading cargo, give us five.",
    "Roger, proceeding to bay two.",
    "Patrol checking in, sector is quiet.",
    "Say again, your last was broken.",
    "Mining detail returning, hold is full.",
    "Traffic is heavy on the approach lane.",
    "Understood, stand by for clearance.",
    "Departing now, see you on the next run.",
]


# --------------------------------------------------------------------------
# hail speakers

def speaker_key(s, key):
    """Speaker 0 is the controller and uses the shared keys; the others are prefixed."""
    return key if s == 0 else f"s{s}.{key}"


def speaker_path(s, rel):
    return rel if s == 0 else f"speaker/{s}/{rel}"


def paced_length_scale(voice, reference_voice):
    """The length_scale at which `voice` speaks the reference lines in the time the controller does
    at A.LENGTH_SCALE: every speaker at the approved controller tempo."""
    def seconds(v):
        return sum(len(A.synth(v, t, length_scale=1.0)) for t in PACE_REFERENCE) / A.SR
    return A.LENGTH_SCALE * seconds(reference_voice) / seconds(voice)


# Voices that garble a word synthesised on its own. Their spelled clips (letters, digits) are cut
# from "Roger, <word>." instead. Measured 2026-10-17 by transcribing a shuffled NATO alphabet with
# faster-whisper medium: en_US-ryan-high 12-15/26 alone, 21/26 cut from the carrier; the other three
# scored better alone (northern male 22/26 alone, 19/26 cut).
SPELLED_FROM_CARRIER = {"en_US-ryan-high"}


def spelled_clip(voice_aligned, voice, model, word, scale):
    if model not in SPELLED_FROM_CARRIER:
        return A.radio(A.synth(voice, word, scale))
    from piper import SynthesisConfig
    cfg = SynthesisConfig(length_scale=scale, noise_scale=A.NOISE_SCALE, noise_w_scale=A.NOISE_W_SCALE)
    audio, first_gap_end, pos, rate = [], None, 0, None
    for chunk in voice_aligned.synthesize(f"Roger, {word}.", syn_config=cfg, include_alignments=True):
        for a in chunk.phoneme_alignments:
            if a.phoneme == " " and first_gap_end is None:
                first_gap_end = pos + a.num_samples
            pos += a.num_samples
        audio.append(chunk.audio_int16_array)
        rate = chunk.sample_rate
    x = np.concatenate(audio).astype(np.float32) / 32768
    x = x[first_gap_end or 0:]
    y = A.sox_fx(x, "rate", str(A.SR)) if rate != A.SR else x
    return A.radio(A.trim_silence(y))


def render_speakers(args, manifest, have, put, alan):
    """Every controller voice (FAC-483: a faction's stations and hails speak in its own speaker).
    Speaker 0 is the controller's library as shipped, plus the hails. Speakers 1-4 get the hails, the
    NATO letters, the digits, "control." and every grammar phrase. Station-name words stay speaker 0
    only, so a name in another voice falls back to the node id, spelled. Records each speaker's model
    and pace in the manifest, and reuses a recorded pace so a later run does not drift from the clips
    already shipped."""
    from piper import PiperVoice
    rendered = 0
    recorded = {sp["model"]: sp for sp in manifest.get("speakers", [])}
    speakers = []
    for s, model in enumerate(A.HAIL_SPEAKERS):
        voice = alan if s == 0 else A.load_voice(args.voices, model)
        if s == 0:
            scale = A.LENGTH_SCALE
        elif model in recorded:
            scale = recorded[model]["length_scale"]
        else:
            scale = round(paced_length_scale(voice, alan), 3)
        speakers.append({"model": model, "length_scale": scale})
        phrases = dict(HAIL_PHRASES) if s == 0 else {**PHRASES, **HAIL_PHRASES}
        if s > 0:
            aligned = (PiperVoice.load(os.path.join(args.voices, model + ".onnx"), include_alignments=True)
                       if model in SPELLED_FROM_CARRIER else None)
            for w in NATO:
                key = speaker_key(s, f"letter.{w[0]}")
                if not have(key):
                    put(key, speaker_path(s, f"spelled/letter/{w[0]}.wav"), spelled_clip(aligned, voice, model, w, scale)); rendered += 1
            for i, w in enumerate(DIGITS):
                key = speaker_key(s, f"digit.{i}")
                if not have(key):
                    put(key, speaker_path(s, f"spelled/digit/{i}.wav"), spelled_clip(aligned, voice, model, w, scale)); rendered += 1
        for slot, texts in phrases.items():
            keys = []
            for n, text in enumerate(texts):
                key = speaker_key(s, f"phrase.{slot}.{n}")
                if not have(key):
                    put(key, speaker_path(s, f"phrase/{slot}/{n}.wav"), A.radio(A.synth(voice, text, scale))); rendered += 1
                keys.append(key)
            manifest["slots"][speaker_key(s, slot)] = keys
    manifest["speakers"] = speakers
    return rendered


# --------------------------------------------------------------------------
# names
# --------------------------------------------------------------------------
def name_words(name):
    """(word, position) for the non-digit words of a name; digits are spelled separately."""
    words = [t for t in re.findall(r"[A-Za-z][A-Za-z']*|\d+", name) if not t.isdigit()]
    if not words:
        return []
    positions = ["first"] + ["mid"] * (len(words) - 2) + ["last"] if len(words) > 1 else ["last"]
    return list(zip(words, positions))


def slug(word):
    s = re.sub(r"[^a-z0-9]+", "_", word.lower()).strip("_")
    return s + "_" + hashlib.sha1(word.lower().encode()).hexdigest()[:6] if s != word.lower() else s


def catalogue_names(path):
    d = json.load(open(path, encoding="utf-8-sig"))
    return sorted({n["Name"] for e in d["Entries"] for n in e.get("Names", [])}), d.get("SourceHash")


def synth_aligned(voice, text):
    """24 kHz speech and the sample index of every word boundary (the midpoint of each space
    phoneme), from Piper's phoneme alignments."""
    from piper import SynthesisConfig
    cfg = SynthesisConfig(length_scale=A.LENGTH_SCALE, noise_scale=A.NOISE_SCALE, noise_w_scale=A.NOISE_W_SCALE)
    audio, cuts, pos, rate = [], [], 0, None
    for chunk in voice.synthesize(text, syn_config=cfg, include_alignments=True):
        for a in chunk.phoneme_alignments:
            if a.phoneme == " ":
                cuts.append(pos + a.num_samples // 2)
            pos += a.num_samples
        audio.append(chunk.audio_int16_array)
        rate = chunk.sample_rate
    x = np.concatenate(audio).astype(np.float32) / 32768
    y = A.sox_fx(x, "rate", str(A.SR)) if rate != A.SR else x
    return y, [int(c * A.SR / rate) for c in cuts]


def edge_fade(x, s=EDGE_FADE_S):
    n = min(int(s * A.SR), len(x) // 2)
    y = x.copy()
    y[:n] *= np.linspace(0, 1, n, dtype=np.float32)
    y[-n:] *= np.linspace(1, 0, n, dtype=np.float32)
    return y


def carrier_clip(voice, word, position):
    """The word cut from its carrier AFTER the radio chain runs over the whole sentence, so a
    word's level sits where it would in speech rather than being normalised on its own."""
    text, k, to_end = CARRIERS[position]
    raw, cuts = synth_aligned(voice, text.format(w=word))
    processed = A.radio(raw)
    # radio() keeps the length (no trim), so the raw boundaries apply to the processed audio.
    bounds = [0] + cuts + [len(processed)]
    clip = processed[bounds[k]:len(processed) if to_end else bounds[k + 1]]
    return A.trim_silence(edge_fade(clip)) if to_end else edge_fade(clip)


# --------------------------------------------------------------------------
# encoding
# --------------------------------------------------------------------------
def save_mulaw(x, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with tempfile.TemporaryDirectory() as d:
        pcm = os.path.join(d, "pcm.wav")
        A.save(x, pcm)
        subprocess.run(["sox", "-R", pcm, "-e", "u-law", "-b", "8", path], check=True, stderr=subprocess.DEVNULL)


def seamless_loop(x, xfade_s=0.25):
    """The tail crossfaded into the head, so the client can loop it without a seam."""
    n = int(xfade_s * A.SR)
    body = x[:-n].copy()
    ramp = np.linspace(0, 1, n, dtype=np.float32)
    body[:n] = body[:n] * ramp + x[-n:] * (1 - ramp)
    return body


def write_text_manifest(manifest, dest):
    """manifest.txt, the form the client reads: one record per line, space-separated, so FactionsUI
    needs no JSON library. clip <key> <path> | slot <name> <key>... | chatter <key>... |
    spacing <name> <ms>. Keys and paths contain no spaces."""
    lines = [f"clip {k} {p}" for k, p in sorted(manifest["clips"].items())]
    lines += [f"slot {name} {' '.join(keys)}" for name, keys in sorted(manifest["slots"].items())]
    lines.append("chatter " + " ".join(manifest.get("chatter", [])))
    lines += [f"spacing {k} {v}" for k, v in sorted(manifest["spacing_ms"].items())]
    with open(os.path.join(dest, "manifest.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--catalogue", required=True)
    ap.add_argument("--voices", default=os.path.join(HERE, "voices"))
    ap.add_argument("--dest", default=DEFAULT_DEST)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--limit-words", type=int, default=0, help="render only the first N name clips (a smoke run)")
    ap.add_argument("--text-manifest-only", action="store_true", help="rewrite manifest.txt from manifest.json, render nothing")
    args = ap.parse_args()
    if args.text_manifest_only:
        write_text_manifest(json.load(open(os.path.join(args.dest, "manifest.json"), encoding="utf-8")), args.dest)
        return

    manifest_path = os.path.join(args.dest, "manifest.json")
    manifest = json.load(open(manifest_path, encoding="utf-8")) if os.path.exists(manifest_path) and not args.force else {}
    clips = manifest.setdefault("clips", {})

    def have(key):
        return key in clips and os.path.exists(os.path.join(args.dest, clips[key]))

    def put(key, rel, audio):
        save_mulaw(audio, os.path.join(args.dest, rel))
        clips[key] = rel.replace(os.sep, "/")

    def checkpoint():
        # A crash must not cost the clips already on disk: the next run skips what is listed here.
        os.makedirs(args.dest, exist_ok=True)
        json.dump(manifest, open(manifest_path, "w", encoding="utf-8"), indent=1, sort_keys=True)

    from piper import PiperVoice
    alan = A.load_voice(args.voices)
    alan_aligned = PiperVoice.load(os.path.join(args.voices, A.VOICE_MODEL + ".onnx"), include_alignments=True)
    rendered = 0

    # Spelled things: one clip each.
    for i, w in enumerate(DIGITS):
        if not have(f"digit.{i}"):
            put(f"digit.{i}", f"spelled/digit/{i}.wav", A.radio(A.synth(alan, w))); rendered += 1
    for w in NATO:
        L = w[0]
        if not have(f"letter.{L}"):
            put(f"letter.{L}", f"spelled/letter/{L}.wav", A.radio(A.synth(alan, w))); rendered += 1

    # Fixed phrases, and the manifest's slot table the client builds lines from.
    slots = {}
    for slot, texts in PHRASES.items():
        keys = []
        for n, text in enumerate(texts):
            key = f"phrase.{slot}.{n}"
            if not have(key):
                put(key, f"phrase/{slot}/{n}.wav", A.radio(A.synth(alan, text))); rendered += 1
            keys.append(key)
        slots[slot] = keys
    manifest["slots"] = slots
    rendered += render_speakers(args, manifest, have, put, alan)
    checkpoint()

    # Name words, cut from positional carriers.
    names, source_hash = catalogue_names(args.catalogue)
    pairs = sorted({p for n in names for p in name_words(n)}, key=lambda p: (p[1], p[0].lower()))
    words = manifest.setdefault("words", {"first": {}, "mid": {}, "last": {}})
    todo = [(w, pos) for w, pos in pairs if not have(f"word.{pos}.{w.lower()}")]
    if args.limit_words:
        todo = todo[:args.limit_words]
    for i, (w, pos) in enumerate(todo):
        key = f"word.{pos}.{w.lower()}"
        put(key, f"names/{pos}/{slug(w)}.wav", carrier_clip(alan_aligned, w, pos))
        words[pos][w.lower()] = key
        rendered += 1
        if (i + 1) % 100 == 0:
            print(f"  names {i + 1}/{len(todo)}", flush=True)
            checkpoint()
    checkpoint()

    # Radio furniture and the static loop. The static is scaled so its RMS equals the mean RMS of
    # the phrases: the client then plays it at 10^(-snr/20) for a distance step.
    furn = A.Furniture(seed=7)
    phrase_rms = float(np.mean([A.rms(A.load_mulaw(os.path.join(args.dest, clips[k]))) for ks in slots.values() for k in ks]))
    fx = {"key": furn.key, "chirp": furn.chirp, "tail": furn.tail}
    for name, audio in fx.items():
        if not have(f"fx.{name}"):
            put(f"fx.{name}", f"fx/{name}.wav", audio); rendered += 1
    if not have("fx.alert"):
        alert = A.sox_fx(A.silence(0.22), "synth", "0.22", "square", "600-900", "sinc", "-2500",
                         "fade", "0.01", "0.22", "0.02", "repeat", "2", "gain", "-20")
        put("fx.alert", "fx/alert.wav", alert); rendered += 1
    if not have("fx.static"):
        st = furn.static_for(int((STATIC_LOOP_S + 0.25) * A.SR), np.random.default_rng(7)) * phrase_rms
        put("fx.static", "fx/static.wav", seamless_loop(st)); rendered += 1

    # Chatter bursts from the four other voices.
    missing = [n for n in range(CHATTER_BURSTS) if not have(f"chatter.{n}")]
    if missing:
        pool = []
        for model in A.CHATTER_VOICES:
            v = A.load_voice(args.voices, model)
            pool += [A.synth(v, t, length_scale=0.88) for t in CHATTER_TEXT]
        rng = np.random.default_rng(11)
        for n in range(CHATTER_BURSTS):
            burst = A.peak_to(A.chatter_burst(pool, furn, rng), A.db(-3))
            if n in missing:
                put(f"chatter.{n}", f"chatter/{n}.wav", burst); rendered += 1
    manifest["chatter"] = [f"chatter.{n}" for n in range(CHATTER_BURSTS)]
    checkpoint()

    # Pilot calls: every voice but the controller's, each line shuffled, and shuffled then smeared.
    pilot = {"shuffle": [], "smear": []}
    missing = [(vi, li) for vi in range(len(A.CHATTER_VOICES)) for li in range(len(PILOT_TEXT))
               if not (have(f"pilot.shuffle.{vi}.{li}") and have(f"pilot.smear.{vi}.{li}"))]
    rng = np.random.default_rng(31)
    for vi, model in enumerate(A.CHATTER_VOICES):
        v = A.load_voice(args.voices, model) if any(m[0] == vi for m in missing) else None
        for li, text in enumerate(PILOT_TEXT):
            if (vi, li) in missing:
                shuffled = A.shuffle_forward(A.synth(v, text, length_scale=0.8), rng)
                put(f"pilot.shuffle.{vi}.{li}", f"pilot/shuffle/{vi}_{li}.wav", A.peak_to(A.chatter_band(shuffled), A.db(-3)))
                put(f"pilot.smear.{vi}.{li}", f"pilot/smear/{vi}_{li}.wav", A.peak_to(A.chatter_band(A.smear(shuffled)), A.db(-3)))
                rendered += 2
            pilot["shuffle"].append(f"pilot.shuffle.{vi}.{li}")
            pilot["smear"].append(f"pilot.smear.{vi}.{li}")
    manifest["slots"]["pilot.shuffle"] = pilot["shuffle"]
    manifest["slots"]["pilot.smear"] = pilot["smear"]

    manifest["format"] = {"rate": A.SR, "channels": 1, "encoding": "mulaw"}
    manifest["spacing_ms"] = {
        "phrase": round(A.PHRASE_GAP_S * 1000), "name_word": round(A.NAME_WORD_GAP_S * 1000),
        "spelled": round(A.SPELLED_GAP_S * 1000), "spelled_to_word": round(A.SPELLED_TO_WORD_GAP_S * 1000),
    }
    manifest["distance_snr_db"] = {k: v for k, v in A.DISTANCE_SNR_DB.items()}
    manifest["fade_at"] = list(A.FADE_AT)
    manifest["catalogue_source_hash"] = source_hash
    json.dump(manifest, open(manifest_path, "w", encoding="utf-8"), indent=1, sort_keys=True)
    write_text_manifest(manifest, args.dest)
    total = sum(os.path.getsize(os.path.join(args.dest, p)) for p in clips.values())
    print(f"rendered {rendered}; manifest holds {len(clips)} clips, {total / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
