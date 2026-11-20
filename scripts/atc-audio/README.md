# Traffic-control radio voice

Offline half of a spoken traffic-controller channel for the `FactionsUI` client plugin, meant to
replace the fleet chat whispers (`FactionRuntime.SendNearbyFleetMessage`). Nothing in the plugin
uses it yet; whether SE lets a Pulsar-compiled plugin play its own audio is the next question.

- `atc_audio.py` — the settled values and the code that renders fragments, plus reference models
  of the runtime behaviour (distance static, fading, chatter, ducking).
- `render_demo.py` — renders the reels the operator approved, from those values.

## Settled by listening tests (operator, 2026-09-28/29)

| | Chosen | Over |
|---|---|---|
| Voice | Piper `en_GB-alan-medium`, rendered 22.05 kHz and resampled to 24 kHz for SE. Since 2026-10-17 every faction speaks in one of all five (docs/atc-voice.md, *Controller voices*); Alan is speaker 0 | Northern English male, Jenny, Lessac, Ryan as the only voice |
| Speed | model-native, `length_scale` 0.587 (1.43× the first draft) | `sox tempo 1.5` after rendering |
| Treatment | clean radio: 300 Hz–3.4 kHz, light drive, key-up click, chirp, squelch tail | dry PA with chime; rough long-range comms |
| Distance | static only while keyed, SNR none / 25 / 12 / 5 dB; the edge step also fades | — |
| Lines | fragments joined with 40–50 ms gaps; the joins were inaudible | pre-rendering every full line per node |
| Chatter | **G1** distant (forward syllable shuffle, deep fading, dropouts, 0 dB static) with **G2** formant-smeared bursts, 60/40 | reversed grains ("sounds reversed"); G3 overlapping crosstalk |
| Ducking | chatter silent from 0.3 s before to 0.3 s after each callout; static continues | — |

Phonetic alphabet for faction tags (METC → "Mike Echo Tango Charlie"); node display names spoken
as written, with the node id as the fallback ("November zero niner three").

## Playing it in the client (read from decompiled SE and Pulsar, then run live 2026-09-29)

Line numbers are in `se-dev-game-code/Data/Decompiled/` in `hermes`.

**Route: raw buffered voices, the same path SE's voice chat uses.** No mod, no `Audio.sbc`, no cue
injection.

- **Getting a voice.** `MyAudio.Static.GetSound(emitter, MySoundDimensions.D2)` returns an
  `IMySourceVoice` fixed at **24000 Hz, 16-bit, mono** (`VRage.Audio/VRage/Audio/MyXAudio2.cs:1896`).
  It returns null when no audio device is available. Fragments are therefore rendered at 24 kHz,
  and the client submits raw PCM with the RIFF header stripped.
- **Gapless lines.** `SubmitBuffer(byte[])` queues one XAudio2 buffer, and queued buffers play back
  to back with no gap. So queue every fragment of a line in the same frame.
  `SubmitBuffer` **silently drops anything past 62 queued buffers** (`MySourceVoice.cs:315`).
  Once `IsPlaying` goes false, the next submit needs `StartBuffered()` again.
- **Static and chatter.** Two more raw voices, fed a few hundred milliseconds ahead from
  `Plugin.Update()`. `SetVolume` / `VolumeMultiplier` apply immediately, so per-frame fading and
  ducking are possible (`MySourceVoice.cs:134-149`).
- **The volume slider does not apply on its own.** These voices have no output routing and go
  straight to the master mix (`MySourceVoice.cs:155-166`). The client scales every volume by
  `MyAudio.Static.VolumeHud`, which follows the Game slider and drops to 0 on alt-tab when "mute
  when not in focus" is set (`MySandboxGame.cs:758, 3119-3128`), and stays silent while
  `MyAudio.Static.Mute` is set.
- **Lifetime.** The audio engine is rebuilt on every session load and unload, which invalidates
  every voice (`MyXAudio2.cs:931-997`). Acquire voices once the session is ready, check `IsValid`
  before every submit, and hold nothing across sessions.
- **Threading.** Sequence from `Update()` on the game thread. `StoppedPlaying` callbacks fire on
  SE's parallel audio thread, and are skipped while muted.
- **Shipping the WAVs.** Pulsar supports `<Asset Name="AssetFolder" Path="..."/>` in
  `FactionsUI.xml`, and calls the plugin's `LoadAssets(string folder)` before `Init` (Pulsar
  `Legacy/Loader/PluginInstance.cs:123-137`, checked at `7db817d`). The fallback is base64 in
  source, as `FleetIconArt` does, at roughly 11 MB for 200 fragments.

**Runner-up:** a `MySoundData` cue injected by reflection into SE's cue bank with `Category="HUD"`
and an absolute `Waves[0].Start`. The HUD slider then applies automatically, but cue-to-cue playback
is not gapless and the cue has to be re-injected after every engine reload. It would suit the static
and chatter loops, not speech.

**Run live on the operator's client, 2026-09-29** (Pulsar 2.4.2, dev folder, throwaway probe
`feat/atc-probe` driven by `/atc` chat commands; read by ear and from `ATC_PROBE` lines in the client
log):

| Question | Result |
|---|---|
| Is a D2 raw voice audible, and centred? | Yes: 1 s of a 24 kHz sine, one buffer |
| Are buffer joins click-free? | Yes: a 3 s sine split into 8 buffers at off-period sample counts is indistinguishable from the same sine in one buffer |
| Does a per-frame volume change crackle? | No: a noise sweep 0 → 1 → 0 over 4 s is smooth, and so is live fading on an edge-distance callout |
| Does the volume slider apply? | Yes, once scaled by `VolumeHud`, which tracks the "Sound volume" slider (0.298 → 0.548 → 0.298); alt-tab takes it to 0 and back |
| Do voices recover after a reconnect? | Old voices read `valid:False` after leaving; new ones acquired after rejoining play normally |
| Does Pulsar pass the asset folder? | Yes, before `Init` (`LoadAssets folder=... wav=15`), **but only when the dev folder has its XML attached and the XML's `<Runtimes>` uses Pulsar's tokens**. Both were broken by the Pulsar 2.4.0 update; see `docs/client-plugin.md` |

Callouts built at runtime from fragments (key-up, 5-6 fragments with 45 ms silences, chirp, tail: 13-15
buffers in one frame) sound like the approved offline reels at every distance.

Not tested: a PluginHub install. It resolves assets through a different Pulsar path
(`ResolveGithubAsset`), so check it when the plugin is first published.

## Running it

Needs `sox` 14.4 on PATH, and Python 3.10+:

```bash
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
.venv/bin/python -m piper.download_voices --data-dir voices en_GB-alan-medium \
    en_GB-northern_english_male-medium en_US-lessac-medium en_US-ryan-high en_GB-jenny_dioco-medium
.venv/bin/python render_demo.py          # writes out/
```

About 35 s on a 4-core NUC. The four non-Alan voices are only used as chatter.

## Speakers 1-4 (FAC-483)

`render_assets.py` renders, for each of the four other voices, every grammar phrase, the hails, the
NATO letters and the digits under `speaker/<n>/`, at a `length_scale` measured to Alan's tempo and
recorded in the manifest's `speakers`. Ryan's letters and digits are cut from a `Roger, <word>.`
carrier (`SPELLED_FROM_CARRIER`): rendered alone they transcribe at 12-15/26. `speaker/1/spelled/letter/E.wav`
is a hand-picked take of `Echo.`, checked by transcription, because the renderer's take read as
"nepho". `test_manifest.py` fails when any speaker's line borrows a clip from another.

## Reproducibility

Every noise source and random choice is seeded, and sox runs with `-R` so its dither is too; those
parts reproduce bit for bit. **Piper's speech does not**: its ONNX graph carries unseeded noise ops,
and `onnxruntime.set_seed` does not reach them. Two renders of one fragment differ slightly. Once
there is a fragment set, the committed files are the source of truth, not a re-render.

## Checking a change by measurement

This work was tuned by ear, but the defects found along the way were all visible without one:

- `sox f.wav -n spectrogram -o f.png` — the radio band must stop at 3.4 kHz. Energy above it means
  a filter sits before the distortion instead of after it. Full-height vertical lines are clicks
  from clipping between sox effects.
- `sox f.wav -n stats` — peak and RMS; every reel here peaks at -3.1 dBFS.
- `render_demo.py` exits non-zero if any chatter leaks into a callout window.
