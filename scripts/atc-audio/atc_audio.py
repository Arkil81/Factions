#!/usr/bin/env python3
"""
Traffic-control radio voice for the Factions client plugin: the offline half.

Settled by operator listening tests over four rounds (2026-09-28/29); the values below are the
approved ones, and README.md records what each was chosen over.

Two kinds of thing live here:

  * RENDER steps that produce assets the client ships: `synth` (Piper, Alan, model-native 1.43x)
    followed by `radio` (the clean-radio chain). One call per fragment; the client joins fragments
    at runtime.
  * REFERENCE MODELS of what the client does at runtime: `transmission` (key-up, chirp, squelch
    tail, distance static and fading), the chatter garblers, and `bed` (chatter ducked around
    callouts). Rendering them here is how the operator approved them; the client implementation
    should match these, and render_demo.py is the thing to A/B it against.

Determinism: every noise source and random choice is seeded here and reproduces exactly. Piper's
own output does not: its ONNX graph carries unseeded noise ops, and neither onnxruntime.set_seed
nor a fixed session makes it repeatable. So the committed fragment set, not a re-render, is the
source of truth for the voice.

External tools: sox 14.4 on PATH. Python: see requirements.txt.
"""

import io
import os
import subprocess
import tempfile
import wave

import numpy as np

# SE's raw voice (IMyAudio.GetSound) is fixed at 24000 Hz 16-bit mono, and PCM is submitted without
# a header, so a 22050 Hz fragment would play 8.8% fast. Piper renders 22050; synth() resamples.
SR = 24000

VOICE_MODEL = "en_GB-alan-medium"
LENGTH_SCALE = 0.587          # model-native speed; 0.88 was round 1, 0.88/1.5 asked for
NOISE_SCALE = 0.6
NOISE_W_SCALE = 0.7

# The clean-radio chain. The steep band-limit must come AFTER the overdrive (drive regenerates
# everything above 3.4 kHz), and needs the -6 dB of headroom before it or the filter's overshoot
# clips between sox effects and clicks.
RADIO_CHAIN = ["highpass", "300", "lowpass", "3400",
               "compand", "0.005,0.12", "6:-60,-60,-30,-14,-20,-9,0,-6", "-4",
               "overdrive", "4", "gain", "-6", "sinc", "300-3400", "gain", "-n", "-3"]

# Distance ladder: static SNR in dB under the voice. None = no static. Fading from EDGE outward.
DISTANCE_SNR_DB = {"near": None, "mid": 25.0, "far": 12.0, "edge": 5.0}
FADE_AT = ("edge",)

# Spacing inside a callout, settled by ear 2026-09-29 (FAC-419 round 5c). Name words are cut from
# positional carrier sentences; spelled things (digits, faction-tag letters) are rendered one per
# clip and read with a pause, as a controller reads them.
PHRASE_GAP_S = 0.045          # between fragments of a line
NAME_WORD_GAP_S = 0.055       # between words of a station name
SPELLED_GAP_S = 0.080         # between digits, and between letters of a faction tag
SPELLED_TO_WORD_GAP_S = 0.150 # where a number meets a name word

# Chatter bed. Other people's traffic, so never the controller's voice.
CHATTER_VOICES = ("en_GB-northern_english_male-medium", "en_US-lessac-medium",
                  "en_US-ryan-high", "en_GB-jenny_dioco-medium")
# Controller speakers (FAC-483): a faction's stations and hails speak in one of these, picked from
# its tag (ControllerVoice.SpeakerFor); the index here is that speaker. Alan is speaker 0.
HAIL_SPEAKERS = (VOICE_MODEL,) + CHATTER_VOICES
CHATTER_G1_SHARE = 0.6        # remainder is G2
CHATTER_PEAK_DB = -24.0       # per burst, before the bed is normalised
BED_STATIC_DB = -44.0         # continuous static under the bed, re unit-RMS noise
DUCK_PAD_S = 0.3              # chatter silent this long before and after every callout
DUCK_RAMP_S = 0.15


# --------------------------------------------------------------------------
# io
# --------------------------------------------------------------------------
def load(path):
    with wave.open(path) as w:
        if w.getframerate() != SR or w.getnchannels() != 1 or w.getsampwidth() != 2:
            raise ValueError(f"{path}: need {SR} Hz mono 16-bit")
        return np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(np.float32) / 32768


def load_mulaw(path):
    """Any WAV sox can read (the shipped clips are 8-bit mu-law), as float32 at SR."""
    with tempfile.TemporaryDirectory() as d:
        b = os.path.join(d, "b.wav")
        subprocess.run(["sox", "-R", path, "-e", "signed-integer", "-b", "16", "-r", str(SR), "-c", "1", b],
                       check=True, stderr=subprocess.DEVNULL)
        return load(b)


def save(x, path):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype(np.int16).tobytes())


def sox_fx(x, *effects):
    with tempfile.TemporaryDirectory() as d:
        a, b = os.path.join(d, "a.wav"), os.path.join(d, "b.wav")
        save(x, a)
        subprocess.run(["sox", "-R", a, b, *effects], check=True, stderr=subprocess.DEVNULL)
        return load(b)


def rms(x):
    return float(np.sqrt(np.mean(x ** 2)) + 1e-12)


def db(d):
    return 10 ** (d / 20)


def silence(seconds):
    return np.zeros(int(seconds * SR), np.float32)


def peak_to(x, peak):
    return x * (peak / (np.max(np.abs(x)) + 1e-9))


# --------------------------------------------------------------------------
# render: voice
# --------------------------------------------------------------------------
def load_voice(voices_dir, model=VOICE_MODEL):
    from piper import PiperVoice
    return PiperVoice.load(os.path.join(voices_dir, model + ".onnx"))


def trim_silence(x, threshold=0.01):
    idx = np.nonzero(np.abs(x) > threshold)[0]
    return x[max(idx[0] - 200, 0): idx[-1] + 400] if len(idx) else x


def synth(voice, text, length_scale=LENGTH_SCALE):
    """Dry Piper speech, resampled to SR mono and trimmed of leading/trailing silence."""
    from piper import SynthesisConfig
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        voice.synthesize_wav(text, wf, syn_config=SynthesisConfig(
            length_scale=length_scale, noise_scale=NOISE_SCALE, noise_w_scale=NOISE_W_SCALE))
    with tempfile.TemporaryDirectory() as d:
        a, b = os.path.join(d, "a.wav"), os.path.join(d, "b.wav")
        open(a, "wb").write(buf.getvalue())
        subprocess.run(["sox", "-R", a, "-r", str(SR), "-c", "1", "-b", "16", b], check=True, stderr=subprocess.DEVNULL)
        return trim_silence(load(b))


def radio(x):
    return sox_fx(x, *RADIO_CHAIN)


def join(fragments, gap_s=PHRASE_GAP_S):
    """How the client builds a line. Operator could not hear the joins at 40-50 ms."""
    out = []
    for f in fragments:
        out += [f, silence(gap_s)]
    return np.concatenate(out[:-1])


# --------------------------------------------------------------------------
# radio furniture and static (seeded)
# --------------------------------------------------------------------------
def noise(n, rng, colour="pink"):
    w = rng.standard_normal(n).astype(np.float32)
    if colour == "white":
        return w
    f = np.fft.rfft(w)
    f[1:] /= np.sqrt(np.arange(1, len(f)))
    return np.fft.irfft(f, n).astype(np.float32)


def band_noise(n, lo, hi, rng, colour="pink"):
    return sox_fx(peak_to(noise(n, rng, colour), 0.5), "sinc", f"{lo}-{hi}")


class Furniture:
    """Key-up click, chirp, squelch tail and a unit-RMS radio-band static loop."""

    def __init__(self, seed=1):
        rng = np.random.default_rng(seed)
        k = band_noise(int(.07 * SR), 800, 4000, rng, "white") * np.linspace(1, 0, int(.07 * SR)) ** 2
        self.key = peak_to(k, 0.12)
        t = band_noise(int(.18 * SR), 600, 5000, rng) * np.linspace(1, 0, int(.18 * SR)) ** 1.5
        self.tail = peak_to(t, 0.2)
        s = np.arange(int(.05 * SR)) / SR
        c = np.concatenate([np.sin(2 * np.pi * 1800 * s), np.sin(2 * np.pi * 2400 * s)]).astype(np.float32) * 0.16
        c[:110] *= np.linspace(0, 1, 110); c[-220:] *= np.linspace(1, 0, 220)
        self.chirp = c
        st = band_noise(SR * 30, 300, 3400, rng)
        self.static = st / rms(st)

    def static_for(self, n, rng):
        start = int(rng.integers(0, len(self.static)))
        return np.roll(self.static, -start)[:n] if n <= len(self.static) else np.resize(np.roll(self.static, -start), n)


def fading_envelope(n, rng, lo, points=None, points_per_s=None, dropouts_per_s=0.0):
    """Slow random gain wander between `lo` and 1, plus optional short near-silent dropouts."""
    k = points if points is not None else max(int(n / SR * points_per_s), 4)
    env = np.interp(np.arange(n), np.linspace(0, n, k), rng.uniform(lo, 1, k)).astype(np.float32)
    for _ in range(int(n / SR * dropouts_per_s)):
        a = int(rng.uniform(0, n)); w = int(rng.uniform(0.05, 0.18) * SR)
        env[a:a + w] *= 0.05
    return env


def transmission(voice_fx, furn, rng, distance="near"):
    """REFERENCE MODEL. Key-up, voice, chirp, tail. Static runs only while keyed (squelch open)."""
    snr = DISTANCE_SNR_DB[distance]
    v = voice_fx.copy()
    if distance in FADE_AT:
        v *= fading_envelope(len(v), rng, lo=0.35, points=9)
    body = np.concatenate([furn.key, v, silence(.12), furn.chirp, furn.tail])
    if snr is not None:
        body = body + furn.static_for(len(body), rng) * rms(voice_fx) * db(-snr)
    return peak_to(body, 0.7)


# --------------------------------------------------------------------------
# chatter garblers (REFERENCE MODELS; forward only — reversed grains were rejected)
# --------------------------------------------------------------------------
def shuffle_forward(x, rng, lo_ms=110, hi_ms=200, window=4, xfade_s=0.015):
    """Syllable-sized grains, shuffled within windows of `window`, all played forwards."""
    grains, i = [], 0
    while i < len(x):
        n = int(rng.integers(lo_ms, hi_ms) * SR / 1000); grains.append(x[i:i + n].copy()); i += n
    order = np.arange(len(grains))
    for k in range(0, len(order), window):
        order[k:k + window] = rng.permutation(order[k:k + window])
    xf = int(xfade_s * SR)
    out = np.zeros(len(x) + xf * len(grains), np.float32); p = 0
    for j in order:
        g = grains[j]
        if len(g) > 2 * xf:
            g[:xf] *= np.linspace(0, 1, xf); g[-xf:] *= np.linspace(1, 0, xf)
        out[p:p + len(g)] += g; p += max(len(g) - xf, 1)
    return out[:p]


def smear(x, keep=4, detail=30, nfft=1024, hop=256):
    """G2: swap each frame's formant envelope (first `detail` cepstral coefficients) for a far
    smoother one (first `keep`). Pitch and loudness contour survive; vowel identity does not."""
    win = np.hanning(nfft).astype(np.float32)
    pad = np.concatenate([np.zeros(nfft), x, np.zeros(nfft)]).astype(np.float32)
    out = np.zeros_like(pad); norm = np.zeros_like(pad)
    for s in range(0, len(pad) - nfft, hop):
        spec = np.fft.rfft(pad[s:s + nfft] * win)
        mag, ph = np.abs(spec) + 1e-7, np.angle(spec)
        cep = np.fft.irfft(np.log(mag))

        def envelope(n):
            lift = np.zeros_like(cep); lift[:n] = cep[:n]; lift[-n + 1:] = cep[-n + 1:]
            return np.fft.rfft(lift).real

        logm = np.log(mag) - envelope(detail) + envelope(keep)
        out[s:s + nfft] += np.fft.irfft(np.exp(logm) * np.exp(1j * ph)) * win
        norm[s:s + nfft] += win ** 2
    return (out / np.maximum(norm, 1e-3))[nfft:nfft + len(x)]


def chatter_band(x, lo=500, hi=2400, drive=10):
    return sox_fx(x, "sinc", f"{lo}-{hi}", "overdrive", str(drive), "gain", "-6", "sinc", f"{lo}-{hi}")


def distant(x, furn, rng, snr_db=0.0):
    """G1: the edge-distance treatment pushed further — deep fading, dropouts, static at 0 dB."""
    y = x * fading_envelope(len(x), rng, lo=0.1, points_per_s=3, dropouts_per_s=1)
    return y + furn.static_for(len(x), rng) * rms(x) * db(-snr_db)


def chatter_burst(pool, furn, rng):
    """One garbled transmission from someone else: G1 with CHATTER_G1_SHARE, else G2."""
    src = pool[int(rng.integers(len(pool)))]
    cut = rng.uniform(1.6, 3.2)
    s0 = int(rng.uniform(0, max(len(src) / SR - cut, 0.01)) * SR)
    g = shuffle_forward(src[s0:s0 + int(cut * SR)], rng)
    g = sox_fx(g, "pitch", str(int(rng.integers(-250, 150))))
    if rng.random() < CHATTER_G1_SHARE:
        g = distant(chatter_band(g), furn, rng)
    else:
        g = chatter_band(smear(g))
    return np.concatenate([furn.key * 0.5, g, furn.tail * 0.6])


def duck_envelope(n, spans):
    env = np.ones(n, np.float32); r = int(DUCK_RAMP_S * SR)
    for a, b in spans:
        a = max(int((a - DUCK_PAD_S) * SR), 0); b = min(int((b + DUCK_PAD_S) * SR), n)
        env[a:b] = 0
        lo = max(a - r, 0); env[lo:a] = np.minimum(env[lo:a], np.linspace(1, 0, a - lo))
        hi = min(b + r, n); env[b:hi] = np.minimum(env[b:hi], np.linspace(0, 1, hi - b))
    return env


def bed(seconds, callout_spans, pool, furn, rng):
    """REFERENCE MODEL. Chatter bursts with random gaps, ducked around callouts, over static.
    Returns (bed, chatter_after_ducking) so a caller can assert nothing leaks into a callout."""
    n = int(seconds * SR)
    chatter = np.zeros(n, np.float32); t = 1.0
    while t < seconds - 3:
        g = peak_to(chatter_burst(pool, furn, rng), db(CHATTER_PEAK_DB))
        a = int(t * SR); b = min(a + len(g), n); chatter[a:b] += g[:b - a]
        t += len(g) / SR + rng.uniform(0.6, 3.0)
    chatter *= duck_envelope(n, callout_spans)
    return chatter + furn.static_for(n, rng) * db(BED_STATIC_DB), chatter
