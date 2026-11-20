#!/usr/bin/env python3
"""
Reference line builder: assembles a callout from the shipped clips the way the FactionsUI client
does (docs/atc-voice.md, "Line grammar"). The client's builder is checked against this one.

A line is a list of steps, each a clip key or a silence in milliseconds. `render()` turns the steps
into audio from the mu-law files, for listening and for tests.
"""

import json
import os
import random
import re

import numpy as np

import atc_audio as A

EVENTS = ("arrive", "depart", "station", "hostile", "advisory",
          "queued", "held", "delayed", "diverted", "cleared")
KINDS = {"haul": "kind.haul", "trade": "kind.trade", "mine": "kind.mine", "relief": "kind.relief"}


class Library:
    def __init__(self, dest):
        self.dest = dest
        self.m = json.load(open(os.path.join(dest, "manifest.json"), encoding="utf-8"))
        self.gap = self.m["spacing_ms"]

    def has(self, key):
        return key in self.m["clips"]

    def pick(self, slot, rng, speaker=0):
        return rng.choice(self.m["slots"][speaker_key(self, speaker, slot)])


def name_steps(lib, name, node_id, speaker=0):
    """The station name followed by "control.". Falls back to the node id, spelled, when any word
    of the name has no clip: a partial name sounds broken, where the id sounds like procedure. Name
    words exist in the controller's voice only, so any other speaker says the id."""
    if speaker:
        return id_steps(lib, node_id, speaker)
    tokens = re.findall(r"[A-Za-z][A-Za-z']*|\d+", name or "")
    words = [t for t in tokens if not t.isdigit()]
    positions = (["first"] + ["mid"] * (len(words) - 2) + ["last"]) if len(words) > 1 else ["last"]
    pos_of = iter(positions)
    steps, prev = [], None
    for t in tokens:
        if t.isdigit():
            for d in t:
                if prev is not None:
                    steps.append(lib.gap["spelled"] if prev == "digit" else lib.gap["spelled_to_word"])
                steps.append(f"digit.{d}")
                prev = "digit"
        else:
            key = f"word.{next(pos_of)}.{t.lower()}"
            if not lib.has(key):
                return id_steps(lib, node_id)
            if prev is not None:
                steps.append(lib.gap["name_word"] if prev == "word" else lib.gap["spelled_to_word"])
            steps.append(key)
            prev = "word"
    if not words:
        return id_steps(lib, node_id)
    return steps  # the last word's clip carries "control."


def id_steps(lib, node_id, speaker=0):
    """"N093" -> November zero niner three control."""
    steps = []
    for ch in (node_id or "").upper():
        if steps:
            steps.append(lib.gap["spelled"])
        steps.append(speaker_key(lib, speaker, f"letter.{ch}" if ch.isalpha() else f"digit.{ch}"))
    return steps + [lib.gap["phrase"], speaker_key(lib, speaker, "phrase.control.0")]


def tag_steps(lib, tag, speaker=0):
    steps = []
    for ch in tag.upper():
        if steps:
            steps.append(lib.gap["spelled"])
        steps.append(speaker_key(lib, speaker, f"letter.{ch}" if ch.isalpha() else f"digit.{ch}"))
    return steps


def line(lib, event, name, node_id, tag=None, kind="haul", inbound=0, departing=0, rng=None,
         fleet_id=None, position=0, departure=False, divert_name=None, divert_id=None, holding=0,
         speaker=0):
    """One controller line, in the station owner's speaker (FAC-483; 0 is the controller's voice)."""
    rng = rng or random.Random()
    sp = speaker
    g = lib.gap["phrase"]
    steps = []
    if event == "hostile":
        steps += ["fx.alert", g]
    # The speaking station is shown on the HUD, not said (operator, 2026-10-02); a station is named
    # only where it is the content, as a divert's "contact <Station> control".
    steps += ["fx.key"]

    def add(*parts):
        for p in parts:
            steps.extend([g] + (p if isinstance(p, list) else [p]))

    def fleet():
        # kind, id, owner -- the id is spelled like a tag (docs/traffic-control.md, T9)
        add(lib.pick(KINDS[kind], rng, sp))
        if fleet_id:
            add(tag_steps(lib, fleet_id, sp))
        add(tag_steps(lib, tag, sp))

    def number(n):
        if n > 0:
            add(lib.pick("number", rng, sp), tag_steps(lib, str(n), sp))

    if event == "arrive":
        fleet()
        add(lib.pick("arrive", rng, sp), lib.pick("arrive.close", rng, sp))
    elif event == "depart" and kind == "patrol":
        add(tag_steps(lib, tag, sp), lib.pick("patrol", rng, sp), lib.pick("depart", rng, sp), lib.pick("depart.close", rng, sp))
    elif event == "depart":
        fleet()
        add(lib.pick("depart", rng, sp), lib.pick("depart.close", rng, sp))
    elif event == "queued":
        fleet()
        add(lib.pick("hold", rng, sp))
        number(position)
        add(lib.pick("fordeparture", rng, sp))
    elif event == "held":
        fleet()
        add(lib.pick("outermarker", rng, sp), lib.pick("expectdelay", rng, sp))
    elif event == "delayed":
        fleet()
        if departure:
            add(lib.pick("departuredelayed", rng, sp))
            number(position)
        add(lib.pick("furtherdelay", rng, sp))
    elif event == "diverted":
        fleet()
        add(lib.pick("divert", rng, sp), name_steps(lib, divert_name, divert_id, sp))
    elif event == "cleared":
        fleet()
        if departure:
            add(lib.pick("depart", rng, sp), lib.pick("depart.close", rng, sp))
        else:
            add(lib.pick("clearedinbound", rng, sp))
    elif event == "station":
        add(tag_steps(lib, tag, sp), lib.pick("patrol", rng, sp), lib.pick("station", rng, sp))
    elif event == "hostile":
        add(lib.pick("allstations", rng, sp), lib.pick("kind.hostile", rng, sp), tag_steps(lib, tag, sp),
            lib.pick("inbound", rng, sp), lib.pick("weaponsfree", rng, sp))
    elif event == "advisory":
        add(lib.pick("advisory", rng, sp))
        if inbound:
            add(lib.pick(f"count.inbound.{inbound if inbound <= 9 else 'many'}", rng, sp))
        if departing:
            add(lib.pick(f"count.departing.{departing if departing <= 9 else 'many'}", rng, sp))
        if holding:
            add(lib.pick(f"count.holding.{holding if holding <= 9 else 'many'}", rng, sp))
        if inbound + departing + holding >= 5:
            add(lib.pick("advisory.close", rng, sp))
    else:
        raise ValueError(event)
    steps += [120, "fx.chirp", "fx.tail"]
    return steps


HAIL_TONES = ("welcome", "notice", "warning", "intercept", "occupation", "held")
# Tones that open with the hostile alert, as the client's AtcLines.OpensWithAlert.
ALERT_TONES = ("intercept", "occupation", "held")


def speaker_key(lib, speaker, key):
    """A key in one hail speaker's voice. Speaker 0 is the controller's shared clips; a speaker the
    library lacks a clip for falls back to the controller, as the client does."""
    if speaker:
        k = f"s{speaker}.{key}"
        if k in lib.m["clips"] or k in lib.m["slots"]:
            return k
    return key


def hail(lib, tone, tag, speaker, rng=None):
    """A trespass hail (FAC-483): "<tag, spelled> control. <hail>", all in the faction's speaker.
    An intercept warning and both occupation hails open with the hostile alert."""
    rng = rng or random.Random()
    g = lib.gap["phrase"]
    steps = ["fx.alert", g] if tone in ALERT_TONES else []
    steps.append("fx.key")
    steps += [g] + tag_steps(lib, tag, speaker)
    steps += [g, speaker_key(lib, speaker, "phrase.control.0")]
    steps += [g, lib.pick(f"hail.{tone}", rng, speaker)]
    steps += [120, "fx.chirp", "fx.tail"]
    return steps


EDGE_FADE_MS = 4


def edge_fade(x, ms=EDGE_FADE_MS):
    """Every clip is faded in and out over 4 ms as it is joined. Most clips begin and end a little
    above zero (trimmed at a threshold, not at a zero crossing), and next to a silent gap that step
    is a broadband tick. The client does the same, so the shipped files need no re-render."""
    n = min(int(ms / 1000 * A.SR), len(x) // 2)
    y = x.copy()
    y[:n] *= np.linspace(0, 1, n, dtype=np.float32)
    y[-n:] *= np.linspace(1, 0, n, dtype=np.float32)
    return y


def render(lib, steps, cache=None):
    cache = {} if cache is None else cache
    parts = []
    for s in steps:
        if isinstance(s, int):
            parts.append(A.silence(s / 1000))
        else:
            if s not in cache:
                cache[s] = edge_fade(A.load_mulaw(os.path.join(lib.dest, lib.m["clips"][s])))
            parts.append(cache[s])
    return np.concatenate(parts)
