using System;
using System.Collections.Generic;
using Factions.Wire;

namespace FactionsUI.Atc;

/// <summary>
/// One station's frequency (docs/atc-voice.md, "Layering"). Within it, one voice at a time: the
/// pilot calls in, the controller answers, with a short beat between. Stations are separate
/// frequencies, so different channels talk over each other -- the operator, 2026-09-29: "Sounds
/// layered on top of each other is realistic."
///
/// <para>A line that is out of reach as the listener's radio points now waits in the channel until
/// it goes stale, so turning onto the station in time still catches it. A burst of routine traffic
/// at one station still collapses into a traffic advisory.</para>
///
/// <para><b>An advisory at most every <see cref="AdvisoryIntervalSeconds"/> per station</b> (operator,
/// 2026-10-02: the queue size called out every time it changes "is pretty annoying"). A burst inside
/// that interval is dropped rather than collapsed again.</para>
///
/// <para><b>One controller at a time, across all channels</b> (operator, 2026-09-29, once layering
/// was heard: "a bit too much chatter ... mute other callouts if one is already playing back").
/// A routine controller line that comes due while another controller is speaking is dropped, not
/// queued; a hostile warning waits for it. Pilot calls still layer.</para>
/// </summary>
internal sealed class AtcChannel
{
    public const double PilotStaleSeconds = 8;
    public const double CalloutStaleSeconds = 20;
    public const double BeatSeconds = 0.4;
    public const int BurstCount = 3;
    public const double BurstWindowSeconds = 60;
    public const double AdvisoryIntervalSeconds = 120;
    private const int QueueCap = 12;

    public readonly string NodeId;
    private readonly List<AtcLine> _queue = new List<AtcLine>();
    private readonly List<DateTime> _recent = new List<DateTime>();
    private AtcLine _current;
    private DateTime _beatUntil;
    private DateTime _lastAdvisory = DateTime.MinValue;

    public AtcChannel(string nodeId) { NodeId = nodeId; }

    public bool Playing => _current != null;
    public bool PlayingController => _current is AtcControllerLine;
    public bool Empty => _current == null && _queue.Count == 0;
    public bool HasHostile => _queue.Exists(l => l is AtcControllerLine c && c.Hostile);
    public DateTime Oldest => _queue.Count > 0 ? _queue[0].QueuedAt : DateTime.MaxValue;
    public string OwnerTag { get; private set; }

    public void Add(AtcLine line, DateTime now)
    {
        line.QueuedAt = now;
        if (line is AtcControllerLine c)
        {
            OwnerTag = c.Callout.NodeOwnerTag;
            if (!c.Hostile) _recent.Add(now);
        }
        _queue.Add(line);
        if (_queue.Count > QueueCap) _queue.RemoveAt(0);
    }

    /// <summary>Plays the current line on. False when nothing is playing.</summary>
    public void TickPlaying(DateTime now, float master, float dt)
    {
        if (_current == null) return;
        if (_current.Tick(now, master, dt)) return;
        _current.Stop();
        _current = null;
        _beatUntil = now.AddSeconds(BeatSeconds);
    }

    /// <summary>Starts the next line that would be heard, if the beat has passed. False when none is.
    /// While <paramref name="controllerSpeaking"/>, a routine controller line that would be heard now
    /// is dropped and a hostile one waits; only a pilot call can start.</summary>
    public bool TryStart(DateTime now, float master, Random rng, bool controllerSpeaking)
    {
        if (_current != null || now < _beatUntil) return false;
        _queue.RemoveAll(l => (now - l.QueuedAt).TotalSeconds > (l is AtcPilotLine ? PilotStaleSeconds : CalloutStaleSeconds));
        if (controllerSpeaking)
            _queue.RemoveAll(l => l is AtcControllerLine c && !c.Hostile && l.Audible(now));
        AtcLine next = controllerSpeaking
            ? _queue.Find(l => l is AtcPilotLine && l.Audible(now))
            : _queue.Find(l => l is AtcControllerLine c && c.Hostile && l.Audible(now)) ?? _queue.Find(l => l.Audible(now));
        if (next == null) return false;

        if (next is AtcControllerLine routine && !routine.Hostile)
        {
            List<AtcLine> callouts = _queue.FindAll(l => l is AtcControllerLine c && !c.Hostile);
            if (callouts.Count >= 2 && RecentCount(now) >= BurstCount)
            {
                int inbound = 0, departing = 0, holding = 0;
                foreach (AtcLine l in callouts)
                {
                    WireTrafficCallout co = ((AtcControllerLine)l).Callout;
                    switch (co.Event)
                    {
                        case WireTrafficEvent.Arrive: inbound++; break;
                        case WireTrafficEvent.Depart: departing++; break;
                        case WireTrafficEvent.Cleared: if (co.Departure) departing++; else inbound++; break;
                        case WireTrafficEvent.Queued:
                        case WireTrafficEvent.Held:
                        case WireTrafficEvent.Delayed: holding++; break;
                    }
                }
                _queue.RemoveAll(l => l is AtcControllerLine c && !c.Hostile);
                if ((now - _lastAdvisory).TotalSeconds < AdvisoryIntervalSeconds)
                {
                    next = _queue.Find(l => l.Audible(now));
                    if (next == null) return false;
                }
                else
                {
                    _lastAdvisory = now;
                    next = new AtcControllerLine(routine.Callout, AtcLines.Advisory(inbound, departing, holding, rng, routine.Callout.Speaker), rng);
                }
            }
        }
        _queue.Remove(next);
        _current = next;
        TickPlaying(now, master, 0f);
        return _current != null;
    }

    public void Stop()
    {
        _current?.Stop();
        _current = null;
        _queue.Clear();
    }

    private int RecentCount(DateTime now)
    {
        _recent.RemoveAll(t => (now - t).TotalSeconds > BurstWindowSeconds);
        return _recent.Count;
    }
}
