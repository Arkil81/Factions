using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Factions.Wire;

namespace FactionsUI.Atc;

/// <summary>One step of a callout: a clip, or a silence in milliseconds.</summary>
internal readonly struct AtcStep
{
    public readonly string Key;
    public readonly int SilenceMs;
    private AtcStep(string key, int ms) { Key = key; SilenceMs = ms; }
    public static AtcStep Clip(string key) => new AtcStep(key, 0);
    public static AtcStep Gap(int ms) => new AtcStep(null, ms);
    public override string ToString() => Key ?? (SilenceMs + "ms");
}

/// <summary>
/// Builds a callout from library clips (docs/atc-voice.md, "Line grammar"). The C# twin of
/// scripts/atc-audio/lines.py, which is the reference. A line does not say which station is
/// speaking -- the HUD shows it while the line plays (operator, 2026-10-02: the name on every line
/// "gets very repetitive"); a station is named only where it is the content, as a divert's
/// "contact &lt;Station&gt; control". A name is joined from its words' carrier-cut clips (the last
/// word carries "control."),
/// digits and faction-tag letters are spelled one clip each, and a name with any word missing from
/// the library is called by its node id instead -- a partial name sounds broken, the id sounds like
/// procedure.
///
/// <para><b>Speakers</b> (FAC-483): a line is spoken in the station owner's voice
/// (<see cref="WireTrafficCallout.Speaker"/>). Speaker 0 is the original controller, whose keys are
/// unprefixed; speaker n's clips are <c>s&lt;n&gt;.&lt;key&gt;</c>, and any clip it lacks falls back to
/// speaker 0's. Station-name words exist for speaker 0 only, so another speaker calls a station by
/// its node id.</para>
/// </summary>
internal static class AtcLines
{
    private static readonly Regex Tokens = new Regex(@"[A-Za-z][A-Za-z']*|\d+", RegexOptions.Compiled);

    private static int Phrase => AtcLibrary.Spacing("phrase", 45);
    private static int NameWord => AtcLibrary.Spacing("name_word", 55);
    private static int Spelled => AtcLibrary.Spacing("spelled", 80);
    private static int SpelledToWord => AtcLibrary.Spacing("spelled_to_word", 150);

    /// <summary>The station's name followed by "control.", or its node id when the name cannot be
    /// built. Null only when neither can.</summary>
    public static List<AtcStep> Name(string name, string nodeId, int speaker = 0)
    {
        if (speaker > 0) return Id(nodeId, speaker);
        var steps = new List<AtcStep>();
        MatchCollection tokens = Tokens.Matches(name ?? "");
        var words = new List<string>();
        foreach (Match m in tokens) if (!char.IsDigit(m.Value[0])) words.Add(m.Value);
        if (words.Count == 0) return Id(nodeId);

        int w = 0;
        string prev = null;
        foreach (Match m in tokens)
        {
            string t = m.Value;
            if (char.IsDigit(t[0]))
            {
                foreach (char d in t)
                {
                    if (prev != null) steps.Add(AtcStep.Gap(prev == "digit" ? Spelled : SpelledToWord));
                    steps.Add(AtcStep.Clip("digit." + d));
                    prev = "digit";
                }
                continue;
            }
            string position = words.Count == 1 ? "last" : w == 0 ? "first" : w == words.Count - 1 ? "last" : "mid";
            string key = "word." + position + "." + t.ToLowerInvariant();
            if (!AtcLibrary.Has(key)) return Id(nodeId);
            if (prev != null) steps.Add(AtcStep.Gap(prev == "word" ? NameWord : SpelledToWord));
            steps.Add(AtcStep.Clip(key));
            prev = "word";
            w++;
        }
        return steps; // the last word's clip carries "control."
    }

    /// <summary>"N093" -> November zero niner three control.</summary>
    public static List<AtcStep> Id(string nodeId, int speaker = 0)
    {
        if (string.IsNullOrEmpty(nodeId)) return null;
        List<AtcStep> steps = Tag(nodeId, speaker);
        if (steps.Count == 0) return null;
        steps.Add(AtcStep.Gap(Phrase));
        steps.Add(AtcStep.Clip(Key(speaker, "phrase.control.0")));
        return steps;
    }

    public static List<AtcStep> Tag(string tag, int speaker = 0)
    {
        var steps = new List<AtcStep>();
        foreach (char ch in (tag ?? "").ToUpperInvariant())
        {
            string key = char.IsLetter(ch) ? "letter." + ch : char.IsDigit(ch) ? "digit." + ch : null;
            if (key == null) continue;
            key = Key(speaker, key);
            if (!AtcLibrary.Has(key)) continue;
            if (steps.Count > 0) steps.Add(AtcStep.Gap(Spelled));
            steps.Add(AtcStep.Clip(key));
        }
        return steps;
    }

    private static string KindSlot(WireTrafficVoice voice)
    {
        switch (voice)
        {
            case WireTrafficVoice.Haul: return "kind.haul";
            case WireTrafficVoice.Trade: return "kind.trade";
            case WireTrafficVoice.Mine: return "kind.mine";
            case WireTrafficVoice.Relief: return "kind.relief";
            case WireTrafficVoice.Hostile: return "kind.hostile";
            default: return null;
        }
    }

    /// <summary>One callout for one event. Null when the library cannot say it.</summary>
    public static List<AtcStep> Callout(WireTrafficCallout c, Random rng)
    {
        if (FactionsWire.IsHail(c.Event)) return Hail(c, rng);
        int sp = c.Speaker;
        var steps = new List<AtcStep>();
        if (c.Event == WireTrafficEvent.HostileInbound) { steps.Add(AtcStep.Clip("fx.alert")); steps.Add(AtcStep.Gap(Phrase)); }
        steps.Add(AtcStep.Clip("fx.key"));

        bool patrol = c.Voice == WireTrafficVoice.Patrol;
        switch (c.Event)
        {
            case WireTrafficEvent.HostileInbound:
                Add(steps, rng, sp, "allstations", "kind.hostile");
                AddTag(steps, c.ActorTag, sp);
                Add(steps, rng, sp, "inbound", "weaponsfree");
                break;
            case WireTrafficEvent.Arrive:
                if (patrol) { AddTag(steps, c.ActorTag, sp); Add(steps, rng, sp, "patrol", "station"); }
                else { Fleet(steps, rng, c, sp); Add(steps, rng, sp, "arrive", "arrive.close"); }
                break;
            case WireTrafficEvent.Depart:
                if (patrol) { AddTag(steps, c.ActorTag, sp); Add(steps, rng, sp, "patrol"); }
                else Fleet(steps, rng, c, sp);
                Add(steps, rng, sp, "depart", "depart.close");
                break;
            // Traffic control's queue (docs/traffic-control.md, "What the player hears").
            case WireTrafficEvent.Queued:
                Fleet(steps, rng, c, sp);
                Add(steps, rng, sp, "hold");
                AddNumber(steps, rng, sp, c.QueuePosition);
                Add(steps, rng, sp, "fordeparture");
                break;
            case WireTrafficEvent.Held:
                Fleet(steps, rng, c, sp);
                Add(steps, rng, sp, "outermarker", "expectdelay");
                break;
            case WireTrafficEvent.Delayed:
                Fleet(steps, rng, c, sp);
                if (c.Departure) { Add(steps, rng, sp, "departuredelayed"); AddNumber(steps, rng, sp, c.QueuePosition); }
                Add(steps, rng, sp, "furtherdelay");
                break;
            case WireTrafficEvent.Diverted:
            {
                List<AtcStep> to = Name(c.DivertNodeName, c.DivertNodeId, sp);
                if (to == null) return null;
                Fleet(steps, rng, c, sp);
                Add(steps, rng, sp, "divert");
                steps.Add(AtcStep.Gap(Phrase));
                steps.AddRange(to);   // "... divert, contact <Station> control."
                break;
            }
            case WireTrafficEvent.Cleared:
                Fleet(steps, rng, c, sp);
                if (c.Departure) Add(steps, rng, sp, "depart", "depart.close");
                else Add(steps, rng, sp, "clearedinbound");
                break;
            default:
                return null;
        }
        Close(steps);
        return steps;
    }

    /// <summary>"Traffic advisory: three convoys inbound, two departing. Five holding." -- what a
    /// burst at one station collapses into.</summary>
    public static List<AtcStep> Advisory(int inbound, int departing, int holding, Random rng, int sp = 0)
    {
        if (inbound + departing + holding == 0) return null;
        var steps = new List<AtcStep> { AtcStep.Clip("fx.key") };
        Add(steps, rng, sp, "advisory");
        if (inbound > 0) Add(steps, rng, sp, "count.inbound." + (inbound <= 9 ? inbound.ToString() : "many"));
        if (departing > 0) Add(steps, rng, sp, "count.departing." + (departing <= 9 ? departing.ToString() : "many"));
        if (holding > 0) Add(steps, rng, sp, "count.holding." + (holding <= 9 ? holding.ToString() : "many"));
        if (inbound + departing + holding >= 5) Add(steps, rng, sp, "advisory.close");
        Close(steps);
        return steps;
    }

    /// <summary>The fleet as the controller names it: kind, id, owner -- "Haul convoy, six four six
    /// Charlie Echo Alpha, Echo X-ray Papa" (T9). The id is spelled like a tag.</summary>
    private static void Fleet(List<AtcStep> steps, Random rng, WireTrafficCallout c, int sp)
    {
        Add(steps, rng, sp, KindSlot(c.Voice));
        AddTag(steps, c.FleetId, sp);
        AddTag(steps, c.ActorTag, sp);
    }

    /// <summary>"number four", "number one two".</summary>
    private static void AddNumber(List<AtcStep> steps, Random rng, int sp, int n)
    {
        if (n <= 0) return;
        Add(steps, rng, sp, "number");
        AddTag(steps, n.ToString(), sp);
    }

    private static void Add(List<AtcStep> steps, Random rng, int sp, params string[] slots)
    {
        foreach (string slot in slots)
        {
            string[] keys = slot == null ? null : Slot(sp, slot);
            if (keys == null || keys.Length == 0) continue;
            steps.Add(AtcStep.Gap(Phrase));
            steps.Add(AtcStep.Clip(keys[rng.Next(keys.Length)]));
        }
    }

    private static void AddTag(List<AtcStep> steps, string tag, int sp)
    {
        List<AtcStep> t = Tag(tag, sp);
        if (t.Count == 0) return;
        steps.Add(AtcStep.Gap(Phrase));
        steps.AddRange(t);
    }

    /// <summary>"Mike Echo Tango Charlie control. You have entered our space. Turn back." -- a trespass
    /// hail (FAC-483) to the one pilot it addresses, in the faction's speaker. The intercept warning
    /// and both occupation hails open with the hostile alert.</summary>
    public static List<AtcStep> Hail(WireTrafficCallout c, Random rng)
    {
        string slot = HailSlot(c.Event);
        if (slot == null) return null;
        int sp = c.Speaker;
        string[] keys = Slot(sp, slot);
        if (keys == null || keys.Length == 0) return null;
        var steps = new List<AtcStep>();
        if (OpensWithAlert(c.Event)) { steps.Add(AtcStep.Clip("fx.alert")); steps.Add(AtcStep.Gap(Phrase)); }
        steps.Add(AtcStep.Clip("fx.key"));
        AddTag(steps, c.ActorTag, sp);
        steps.Add(AtcStep.Gap(Phrase));
        steps.Add(AtcStep.Clip(Key(sp, "phrase.control.0")));
        Add(steps, rng, sp, slot);
        Close(steps);
        return steps;
    }

    private static string HailSlot(WireTrafficEvent e)
    {
        switch (e)
        {
            case WireTrafficEvent.HailWelcome: return "hail.welcome";
            case WireTrafficEvent.HailNotice: return "hail.notice";
            case WireTrafficEvent.HailWarning: return "hail.warning";
            case WireTrafficEvent.HailIntercept: return "hail.intercept";
            case WireTrafficEvent.HailOccupation: return "hail.occupation";
            case WireTrafficEvent.HailOccupationHeld: return "hail.held";
            default: return null;
        }
    }

    private static bool OpensWithAlert(WireTrafficEvent e)
    {
        return e == WireTrafficEvent.HailIntercept || e == WireTrafficEvent.HailOccupation
            || e == WireTrafficEvent.HailOccupationHeld;
    }

    /// <summary>Speaker <paramref name="sp"/>'s clip for <paramref name="key"/>, or speaker 0's when it has none.</summary>
    internal static string Key(int sp, string key)
    {
        if (sp <= 0) return key;
        string own = "s" + sp + "." + key;
        return AtcLibrary.Has(own) ? own : key;
    }

    private static string[] Slot(int sp, string slot)
    {
        if (sp > 0)
        {
            string[] own = AtcLibrary.Slot("s" + sp + "." + slot);
            if (own != null && own.Length > 0) return own;
        }
        return AtcLibrary.Slot(slot);
    }

    private static void Close(List<AtcStep> steps)
    {
        steps.Add(AtcStep.Gap(120));
        steps.Add(AtcStep.Clip("fx.chirp"));
        steps.Add(AtcStep.Clip("fx.tail"));
    }
}
