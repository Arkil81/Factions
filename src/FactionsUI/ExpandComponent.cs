using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Wire;
using Sandbox.Game.Gui;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;

namespace FactionsUI;

/// <summary>
/// The in-flight placement readout: whether a node may be created where the player is right now,
/// what it would cost, and where it would actually land (FAC-197; docs/economy-territory.md →
/// "Expanding — creating a node where there is none").
///
/// <para><b>Everything on it is the server's answer, not a local guess.</b> Separation is authored
/// per region and per body, and the client has neither the feature catalogue nor the resolved planet
/// anchors, so nothing here is derived — the component subscribes, draws what arrives, and sends a
/// commit that carries no position of its own.</para>
///
/// <para><b>Occupied says only "occupied".</b> Never by whom, never where. A ship scanner reaches
/// 50/100 km against 500 km of deep-space separation, so a player on a legal-looking deep-space site
/// routinely cannot see what blocks them; that blind case is structural, and flying is what resolves
/// it. Hedging the wording would only restore the trial-and-error this readout exists to remove.</para>
///
/// <para>The purchase is behind a modal confirmation rather than the readout's own key, because it
/// is the largest single amount a player spends and it cannot be undone.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class ExpandComponent : MySessionComponentBase
{
    internal static ExpandComponent Instance { get; private set; }

    private const float Scale = 0.65f;

    /// <summary>The same two colours the map overlay uses, so a ring and the line describing it are
    /// never two different reds. Saturated deliberately: this draws over the game world, which runs
    /// from near-black space to a lit planet, and a muted tint holds against neither.</summary>
    private static readonly Color ClearColour = new Color(110, 255, 140);
    private static readonly Color OccupiedColour = new Color(255, 64, 64);
    private static readonly Color WaitingColour = new Color(200, 200, 200);

    /// <summary>Top centre. The bottom-left column belongs to the signature readout and to vanilla's
    /// own furniture, and this line was drawing over both — it is queued in the HUD's draw pass, so
    /// it composites above anything sharing that corner rather than yielding to it. The top edge is
    /// empty in gameplay and it is where the eye already is while flying.</summary>
    private static readonly Vector2 Anchor = new Vector2(0.5f, 0.020f);

    /// <summary>The server's last word. Null until the first status arrives after subscribing.</summary>
    /// <summary>The server answered that the caller is in no faction; cleared by any readout.</summary>
    private bool _noFaction;
    private WireExpandStatus? _status;

    /// <summary>Why the client currently wants a status stream. Two independent surfaces need it —
    /// the HUD readout and the map's exclusion sphere — and either alone is reason enough to keep the
    /// server sending. A single bool here meant closing one silently unsubscribed the other.</summary>
    private readonly HashSet<string> _wants = new HashSet<string>(StringComparer.Ordinal);

    internal const string WantReadout = "readout";
    internal const string WantMap = "map";

    /// <summary>What the server is actually sending, as opposed to what has been asked for.</summary>
    private bool _subscribed;

    /// <summary>One confirmation at a time. Without this, holding the key queues a message box per
    /// frame and the player confirms whichever one happens to surface.</summary>
    private bool _confirming;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.ExpandStatus, OnStatus);
    }

    protected override void UnloadData()
    {
        Instance = null;
        _status = null;
        _noFaction = false;
        _wants.Clear();
        _subscribed = false;
        _confirming = false;
    }

    /// <summary>True when the server has granted the Expand feature to this client.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Expand);
        }
    }

    /// <summary>Whether the HUD readout itself is showing, as opposed to whether anything is
    /// subscribed. The map can hold the stream open with the readout hidden.</summary>
    internal bool IsOpen { get { return _wants.Contains(WantReadout); } }

    /// <summary>Register or drop one reason to want the status stream. The server is subscribed
    /// while any reason stands and unsubscribed when the last one goes, so a client that stops
    /// looking stops costing anything.</summary>
    internal void SetWant(string reason, bool wanted)
    {
        bool changed = wanted ? _wants.Add(reason) : _wants.Remove(reason);
        if (!changed) return;

        bool shouldSubscribe = _wants.Count > 0;
        if (_subscribed == shouldSubscribe) return;

        if (!Available)
        {
            // Nothing to unsubscribe from if the link is gone; drop the flag so a later subscribe is
            // not swallowed as a no-op.
            if (!shouldSubscribe) _subscribed = false;
            return;
        }

        if (Send(shouldSubscribe ? WireExpandAction.Subscribe : WireExpandAction.Unsubscribe, 0))
        {
            _subscribed = shouldSubscribe;
            if (!shouldSubscribe) { _status = null; _noFaction = false; }
        }
    }

    /// <summary>
    /// The site the server last quoted: where a node would actually come to rest, the radius nothing
    /// may sit inside of it, and whether it is currently clear. False before the first status.
    ///
    /// <para><b>The centre is the PLACED point, not the player.</b> The server evaluates
    /// <c>NearestNodeDistance</c> at the snapped position, so over a planet — where the site drops to
    /// terrain far below the ship — a sphere drawn on the player would be centred somewhere the rule
    /// was never evaluated.</para>
    /// </summary>
    internal bool TryGetSite(out Vector3D placedM, out double radiusM, out bool clear)
    {
        placedM = Vector3D.Zero;
        radiusM = 0;
        clear = false;
        if (_status == null) return false;

        WireExpandStatus s = _status.Value;
        if (s.SeparationKm <= 0) return false;
        placedM = new Vector3D(s.PlacedXKm * 1000.0, s.PlacedYKm * 1000.0, s.PlacedZKm * 1000.0);
        radiusM = s.SeparationKm * 1000.0;
        clear = s.Clear;
        return true;
    }

    /// <summary>Ask to create the node the last status quoted, after the player confirms. The quote
    /// id is echoed rather than a position: the server re-derives the site from the player's live
    /// position, and refuses a commit against a status it has since replaced.</summary>
    internal void RequestCommit()
    {
        if (_confirming || !IsOpen || _status == null) return;
        WireExpandStatus s = _status.Value;
        if (!s.Clear) return;

        _confirming = true;
        // Leading blank line: the caption sits hard against the body otherwise. The question and
        // the cost then run together, which is what fits the box -- the last line was landing under
        // the buttons.
        var body = new StringBuilder()
            .AppendLine()
            .Append("Create a node for your faction here?").AppendLine()
            .Append("Cost: ").Append(s.CostCredits.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" credits.").AppendLine().AppendLine()
            .Append(LandingSentence(s));

        MyGuiScreenBase box = MyGuiSandbox.CreateMessageBox(
            MyMessageBoxStyleEnum.Info,
            buttonType: MyMessageBoxButtonsType.YES_NO,
            messageText: body,
            messageCaption: new StringBuilder("EXPAND"),
            yesButtonText: MyStringId.GetOrCompute("Create"),
            noButtonText: MyStringId.GetOrCompute("Cancel"),
            callback: OnConfirmed);
        MyGuiSandbox.AddScreen(box);
    }

    private void OnConfirmed(MyGuiScreenMessageBox.ResultEnum result)
    {
        _confirming = false;
        if (result != MyGuiScreenMessageBox.ResultEnum.YES) return;
        if (_status == null) return;
        Send(WireExpandAction.Commit, _status.Value.QuoteId);
    }

    private static bool Send(WireExpandAction action, int quoteId)
    {
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteExpandRequest(action, quoteId), true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log("expand request (" + action + ") failed: " + ex);
            return false;
        }
    }

    private void OnStatus(byte[] data)
    {
        WireExpandStatus status;
        if (!FactionsWire.TryReadExpandStatus(data, out status))
        {
            Plugin.Log("EXPAND_STATUS unreadable");
            return;
        }

        // A commit's answer is announced once and then replaced by the readout tick that follows it,
        // so the outcome is never left standing as the live verdict at a position it no longer
        // describes.
        if (status.Committed)
        {
            MyAPIGateway.Utilities.ShowNotification(CommitNotice(status), 8000);
            // The server's answer to a readout it cannot quote: the caller is in no faction, and
            // nothing will follow until they join one and reopen it.
            if (status.Verdict == WireExpandVerdict.NoFaction && _status == null) _noFaction = true;
        }
        else
        {
            _status = status;
            _noFaction = false;
        }
    }

    private static string CommitNotice(WireExpandStatus s)
    {
        switch (s.Verdict)
        {
            case WireExpandVerdict.Ok:
                return "Expanded: " + s.NodeId + " is yours. Stay near it to build hold.";
            case WireExpandVerdict.Occupied:
                return "Occupied — nothing was charged. Fly further out and try again.";
            case WireExpandVerdict.ChargeRefused:
                return "Not enough in the faction account — nothing was charged. It needs "
                       + s.CostCredits.ToString("N0", CultureInfo.InvariantCulture)
                       + " credits: deposit at K > Factions > your faction > Deposit.";
            case WireExpandVerdict.EconomyUnavailable:
                return "The economy is unavailable — nothing was charged or created.";
            case WireExpandVerdict.NoFaction:
                return "You are not in a faction.";
            case WireExpandVerdict.NotLeader:
                return "Only a leader of your faction can expand it — nothing was charged.";
            default:
                return "Expand failed — nothing was charged.";
        }
    }

    /// <summary>Called from the HUD screen's own draw pass — see
    /// <see cref="Patches.HudMarkerPatch"/>. Sprites queued outside it land in a different batch and
    /// flicker when other UI composites over them.</summary>
    public void Draw()
    {
        if (!IsOpen) return;

        string text;
        Color colour;
        if (_status == null && _noFaction)
        {
            text = "SITE  you are not in a faction";
            colour = OccupiedColour;
        }
        else if (_status == null)
        {
            text = "SITE  checking...";
            colour = WaitingColour;
        }
        else
        {
            WireExpandStatus s = _status.Value;
            if (!s.Clear)
            {
                text = "SITE  OCCUPIED  (" + Km(s.SeparationKm) + " separation here)" + BlockerNote(s);
                colour = OccupiedColour;
            }
            else
            {
                text = "SITE  CLEAR  (" + Km(s.SeparationKm) + " separation, " + ZoneName(s.Zone) + ")"
                       + LandingShort(s)
                       + "  " + s.CostCredits.ToString("N0", CultureInfo.InvariantCulture) + " cr from faction";
                colour = ClearColour;
            }
        }

        MyGuiManager.DrawString("Blue", text, Anchor, Scale, colour,
            MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP,
            useFullClientArea: true, maxTextWidth: float.PositiveInfinity, ignoreBounds: true);
    }

    /// <summary>
    /// Whether anything the player knows about is inside the exclusion sphere, and how much.
    ///
    /// <para>Nothing visible is the <b>expected</b> answer in deep space, not a fault: a ship scanner
    /// reaches 50/100 km against 500 km of separation, so the node refusing a deep-space site is
    /// usually one nobody has ever seen. Saying so turns "OCCUPIED and the map looks empty" from a
    /// bug report into the structural blind case it is.</para>
    ///
    /// <para>Silent when the map has never been opened — the client has no node list then, and
    /// "nothing visible" would be a statement about the client rather than about the world.</para>
    /// </summary>
    private static string BlockerNote(WireExpandStatus s)
    {
        Map.MapComponent map = Map.MapComponent.Instance;
        if (map == null || !map.World.HasWorld) return "";

        var centre = new Vector3D(s.PlacedXKm * 1000.0, s.PlacedYKm * 1000.0, s.PlacedZKm * 1000.0);
        double radiusSq = s.SeparationKm * 1000.0 * s.SeparationKm * 1000.0;
        int inside = 0;
        for (int i = 0; i < map.World.Nodes.Count; i++)
            if (Vector3D.DistanceSquared(map.World.Nodes[i].PositionM, centre) < radiusSq) inside++;

        if (inside == 0) return "  by nothing you can see \u2014 fly on";
        return inside == 1 ? "  by 1 known node" : "  by " + inside + " known nodes";
    }

    /// <summary>The same fact as <see cref="LandingSentence"/>, clipped for a single HUD line that
    /// now sits across the top of the screen. Empty when the node lands where the player is, which is
    /// the deep-space case and needs no words at all.</summary>
    private static string LandingShort(WireExpandStatus s)
    {
        if (s.SnapDistanceKm < 1.0) return "";
        string away = Km(s.SnapDistanceKm);
        switch (s.Zone)
        {
            case 2:  return "  drops to ground " + away + " below";
            case 1:  return "  pulled " + away + " out to the gravity edge";
            default: return "  drawn " + away + " into the field";
        }
    }

    /// <summary>
    /// Where the node comes to rest, and <b>why it is not where you are standing</b>.
    ///
    /// <para>A bare distance does not explain itself. Over a planet the site drops to terrain, which
    /// reads naturally enough; in space it does not — an Orbit site is pulled radially onto the edge
    /// of the planet's gravity well (<c>SnapToOrbitBand</c> lands it exactly on the gravity limit),
    /// and a site inside an authored region is drawn into that region's own volume. Both can move a
    /// node tens of kilometres for reasons nothing on screen would otherwise give.</para>
    ///
    /// <para>So the zone names the mechanism. Deep space has no snap at all, which is why its
    /// sentence is the short one.</para>
    ///
    /// <para><b>Kept under ~50 characters.</b> The message box does not wrap: a longer line is drawn
    /// at its natural width straight through the dialog frame and out the other side. The first cut
    /// ran to 120 characters and rendered across the whole screen.</para>
    /// </summary>
    private static string LandingSentence(WireExpandStatus s)
    {
        if (s.SnapDistanceKm < 1.0) return "Your node settles exactly here.";

        string away = Km(s.SnapDistanceKm);
        switch (s.Zone)
        {
            case 2:  // Surface
                return "Your node settles " + away + " below, on solid ground.";
            case 1:  // Orbit
                return "Your node settles " + away + " out, at the gravity edge.";
            default: // DeepSpace, moved only by an authored region claiming the point
                return "Your node settles " + away + " away, inside the field.";
        }
    }

    private static string Km(double km)
    {
        return km.ToString(km < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " km";
    }

    /// <summary>Matches the server's NodeZone ordinal. An ordinal this build does not know is named
    /// rather than guessed at — a wrong zone name would misdescribe the rule in force.</summary>
    private static string ZoneName(byte zone)
    {
        switch (zone)
        {
            case 0:  return "deep space";
            case 1:  return "orbit";
            case 2:  return "surface";
            default: return "zone " + zone;
        }
    }
}
