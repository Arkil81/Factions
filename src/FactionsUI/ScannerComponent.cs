using System;
using System.Collections.Generic;
using Factions.Wire;
using FactionsUI.Atc;
using Sandbox.Graphics;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace FactionsUI;

/// <summary>
/// Client-side scanner HUD. Holds the contact set the server sends over the uplink and draws it
/// directly, rather than handing it to SE's GPS marker renderer: that path costs a slot in the
/// player's GPS list per contact and gives no control over icon, alpha or placement.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class ScannerComponent : MySessionComponentBase
{
    // Fades. In is quick — a contact appearing should read as an acquisition, not a slow bloom.
    // Out is slower so a contact leaving the sphere is noticed rather than simply gone.
    private const double FadeInSeconds = 0.30;
    private const double FadeOutSeconds = 0.80;

    // Distance falloff: full strength close in, MinRangeAlpha at the edge of the sphere. Reads as
    // signal strength and keeps a crowded far edge from competing with what is actually near.
    private const float MinRangeAlpha = 0.35f;

    private const float IconScreenFraction = 0.016f;  // of screen HEIGHT; drawn square
    private const float EdgeMargin = 0.06f;           // clamp box inset, normalized
    private const float LabelScale = 0.605f;
    private const float LabelGap = 0.004f;            // normalized, icon top to label baseline

    /// <summary>km/min on the wire to m/s here -- the one place that boundary is crossed.</summary>
    private const double KmPerMinToMPerSec = 1000.0 / 60.0;

    public static ScannerComponent Instance { get; private set; }

    private readonly Dictionary<string, Contact> _contacts = new Dictionary<string, Contact>(StringComparer.Ordinal);

    private readonly List<IMyGps> _gpsList = new List<IMyGps>();
    private readonly List<string> _gpsTexts = new List<string>();
    private double _nextDiagnosticAt;
    private double _smallRangeKm = 50.0;
    private double _largeRangeKm = 100.0;
    private int _level;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.ScannerConfig, OnConfig);
        UplinkComponent.RegisterHandler(WireMessageKind.ScannerContacts, OnContacts);
    }

    protected override void UnloadData()
    {
        _contacts.Clear();
        Instance = null;
    }

    private void OnConfig(byte[] data)
    {
        int level;
        double smallKm, largeKm;
        if (!FactionsWire.TryReadScannerConfig(data, out level, out smallKm, out largeKm)) return;
        _level = level;
        _smallRangeKm = smallKm;
        _largeRangeKm = largeKm;
        Plugin.Log("SCANNER_CONFIG level=" + level + " smallKm=" + smallKm + " largeKm=" + largeKm);
    }

    private void OnContacts(byte[] data)
    {
        List<WireContact> present;
        List<string> removedKeys;
        bool reset;
        if (!FactionsWire.TryReadScannerContacts(data, out present, out removedKeys, out reset)) return;

        double now = NowSeconds();
        // The server has dropped its record of what we hold, so this message is the whole truth.
        // Anything not in it is gone for good and would otherwise never be named for removal.
        if (reset) _contacts.Clear();
        foreach (WireContact w in present)
        {
            Contact c;
            if (_contacts.TryGetValue(w.Key, out c))
            {
                // Already known: refresh in place. Re-acquiring a contact that was fading out
                // cancels the fade rather than restarting the acquisition flash.
                c.Apply(w);
                c.LostAt = 0;
            }
            else
            {
                c = new Contact(w) { AcquiredAt = now };
                _contacts[w.Key] = c;
            }
        }

        foreach (string key in removedKeys)
        {
            Contact c;
            // Marked, not deleted: the contact keeps drawing while it fades out.
            if (_contacts.TryGetValue(key, out c) && c.LostAt == 0) c.LostAt = now;
        }

        if (Config.Current.DebugLogging && now >= _nextDiagnosticAt)
        {
            _nextDiagnosticAt = now + 10.0;
            Plugin.Log("SCANNER_RECV present=" + present.Count + " removed=" + removedKeys.Count
                       + (reset ? " reset" : "")
                       + " held=" + _contacts.Count + " byKind=" + KindHistogram(present));
        }
    }

    /// <summary>Per-kind tally of one wire message, so a kind that never arrives can be told apart
    /// from one that arrives and then fails to draw.</summary>
    private static string KindHistogram(List<WireContact> contacts)
    {
        var counts = new Dictionary<byte, int>();
        foreach (WireContact c in contacts)
        {
            int n;
            counts.TryGetValue(c.Kind, out n);
            counts[c.Kind] = n + 1;
        }
        var sb = new System.Text.StringBuilder();
        foreach (KeyValuePair<byte, int> pair in counts)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(KindName(pair.Key)).Append('=').Append(pair.Value);
        }
        return sb.Length > 0 ? sb.ToString() : "(none)";
    }

    /// <summary>Separator <c>TacticHint.ContactLabel</c> puts between a label and its phrase.</summary>
    private const string PhraseSeparator = " \u00B7 ";

    /// <summary>
    /// The server's label with its leading "Station" or "Fleet" dropped: the icon already says which
    /// of the two a marker is, so the word repeats it. Whatever follows it -- "(free)", a tactic
    /// phrase -- is kept. Done here rather than in <c>PlayerScanner</c> because the GPS fallback for
    /// an unlinked player shows that same label as a marker name, and an empty one there is no name.
    /// </summary>
    private static string WithoutClassWord(byte kind, string label)
    {
        string word = kind == 0 ? "Station" : kind == 7 ? "Fleet" : null;
        if (word == null || !label.StartsWith(word, StringComparison.Ordinal)) return label;
        string rest = label.Substring(word.Length);
        if (rest.StartsWith(PhraseSeparator, StringComparison.Ordinal))
            rest = rest.Substring(PhraseSeparator.Length);
        return rest;
    }

    private static string KindName(byte kind)
    {
        switch (kind)
        {
            case 0: return "Station";
            case 1: return "Outpost";
            case 2: return "OreField";
            case 3: return "RareOreField";
            case 4: return "IceField";
            case 5: return "Refinery";
            case 6: return "FuelDepot";
            case 7: return "Fleet";
            case 8: return "RaidEngagement";
            case 9: return "Grid";
            default: return "Kind" + kind;
        }
    }

    /// <summary>
    /// Called from a postfix on <c>MyGuiScreenHudSpace.Draw</c> rather than from this component's
    /// own Draw override. Sprites queued outside the GUI draw pass land in a different batch and
    /// scissor state than the HUD's, so they flickered whenever another UI element was compositing
    /// over them. Drawing inside that pass puts them in the same state vanilla HUD markers use.
    /// </summary>
    public void DrawMarkers()
    {
        if (_contacts.Count == 0) return;
        if (!Config.Current.Enabled) return;

        UplinkComponent uplink = UplinkComponent.Instance;
        if (uplink == null || !uplink.HasFeature(WireFeatures.Scanner)) return;

        // Names are detail, and SE's gesture for detail is holding Alt; without it a marker draws
        // only its class remainder (a grid: its owner's tag) and distance. The GPS list is read only while Alt is held.
        List<string> gpsTexts = MyInput.Static != null && MyInput.Static.IsAnyAltKeyPressed()
            ? HudGpsTexts()
            : null;

        MatrixD view;
        MatrixD projection;
        Vector3D cameraPos;
        if (!Projection.TryGetCamera(out view, out projection, out cameraPos)) return;

        double now = NowSeconds();
        double maxRangeM = _largeRangeKm * 1000.0;

        // One square icon size in MyGuiManager's normalized space. Deriving it from a pixel square
        // is what keeps the icon square on any aspect ratio, and keeps it in the SAME space the
        // label is positioned in — see DrawContact.
        float iconPixels = MyGuiManager.GetSafeFullscreenRectangle().Height * IconScreenFraction;
        Vector2 iconSize = MyGuiManager.GetNormalizedSizeFromScreenSize(new Vector2(iconPixels, iconPixels));

        bool diagnose = Config.Current.DebugLogging && now >= _nextDiagnosticAt;
        var drawn = new Dictionary<byte, int>();
        var skippedFade = new Dictionary<byte, int>();
        var skippedAlpha = new Dictionary<byte, int>();

        List<string> expired = null;
        foreach (KeyValuePair<string, Contact> pair in _contacts)
        {
            Contact c = pair.Value;

            float fade = c.FadeAlpha(now, FadeInSeconds, FadeOutSeconds);
            if (fade <= 0f)
            {
                if (expired == null) expired = new List<string>();
                expired.Add(pair.Key);
                if (diagnose) Bump(skippedFade, c.Kind);
                continue;
            }

            Vector3D world = c.WorldPosition();
            double distance = Vector3D.Distance(cameraPos, world);
            float alpha = fade * RangeAlpha(distance, maxRangeM);
            if (alpha <= 0.01f)
            {
                if (diagnose) Bump(skippedAlpha, c.Kind);
                continue;
            }

            if (diagnose) Bump(drawn, c.Kind);
            Vector2 normalized;
            bool behind;
            if (Projection.Project(world, ref view, ref projection, out normalized, out behind)
                && !behind && !IsOffScreen(normalized))
            {
                _placed.Add(new Placed
                {
                    Key = pair.Key, Contact = c, World = world, Distance = distance, Alpha = alpha,
                    Normalized = normalized,
                });
                continue;
            }
            DrawContact(pair.Key, c, world, distance, alpha, ref view, ref projection, iconSize, now, gpsTexts);
        }
        DrawPlaced(cameraPos, ref view, ref projection, iconSize, now, gpsTexts);

        if (diagnose)
        {
            _nextDiagnosticAt = now + 10.0;
            Plugin.Log("SCANNER_DRAW held=" + _contacts.Count
                       + " drawn=" + Histogram(drawn)
                       + " fadedOut=" + Histogram(skippedFade)
                       + " tooFaint=" + Histogram(skippedAlpha));
        }

        if (expired != null)
            foreach (string key in expired) _contacts.Remove(key);
    }

    private void DrawContact(
        string key, Contact c, Vector3D world, double distance, float alpha,
        ref MatrixD view, ref MatrixD projection, Vector2 iconSize, double now, List<string> gpsTexts)
    {
        Vector2 normalized;
        bool behind;
        if (!Projection.Project(world, ref view, ref projection, out normalized, out behind)) return;

        Color colour = Palette.RelationColour(c.Relation);
        colour.A = (byte)(alpha * 255f);

        bool offScreen = behind || IsOffScreen(normalized);

        float rotation = 0f;
        if (offScreen)
        {
            Vector2 dir = normalized - new Vector2(0.5f, 0.5f);
            // Behind the camera the projection mirrors, so the raw direction points the wrong way.
            if (behind) dir = -dir;
            if (dir.LengthSquared() < 1e-8f) dir = new Vector2(1f, 0f);
            dir.Normalize();

            // Clamp into the margin box rather than onto a circle: a circle in normalized space is
            // an ellipse on a wide screen, which bunches indicators at the left and right edges.
            float scale = Math.Min(
                (0.5f - EdgeMargin) / Math.Max(Math.Abs(dir.X), 1e-6f),
                (0.5f - EdgeMargin) / Math.Max(Math.Abs(dir.Y), 1e-6f));
            normalized = new Vector2(0.5f, 0.5f) + dir * scale;
            rotation = (float)Math.Atan2(dir.Y, dir.X);
        }

        // Icon and label are both placed in MyGuiManager's normalized space, from the same anchor
        // and with the same useFullClientArea. Positioning the icon in raw screen pixels while the
        // label went through DrawString's normalized space put them in two different coordinate
        // systems, so they drifted apart by an amount that varied with screen position.
        string texture = offScreen ? OffScreenTexture : ContactTexture(c);
        MyGuiManager.DrawSpriteBatch(texture, normalized, iconSize, colour,
            MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
            useFullClientArea: true, waitTillLoaded: false, maskTexture: null,
            rotation: rotation, rotSpeed: 0f, ignoreBounds: true);

        // Off-screen contacts get neither marks nor a label: the indicator says only "that way",
        // and a stack of either along the screen edge is unreadable at any realistic contact count.
        if (offScreen) return;

        bool hostileLine;
        if (key.StartsWith(NodeKeyPrefix, StringComparison.Ordinal)
            && AtcComponent.TrySpeaking(key.Substring(NodeKeyPrefix.Length), out hostileLine))
            DrawSpeakerRing(normalized, iconSize, alpha, hostileLine, now);

        // Strength, between the marker and its label. The map puts the marks directly above the
        // marker and nothing else competes for that space; here the label already lives there, so
        // something has to give -- and it is the LABEL that moves up, not the marks that move down.
        // An echelon mark annotates the symbol and has to stay next to it; pushed below the icon it
        // would read as a separate contact, and on the far side of the label it would annotate the
        // text. Sizing and spacing come from FleetIcons so the two surfaces cannot drift apart.
        int markCount = DrawEchelon(c, normalized, iconSize, colour);

        if (string.IsNullOrEmpty(c.Label)) return;

        string text = LabelText(c, gpsTexts);
        var labelColour = new Color(colour, alpha);
        MyGuiManager.DrawString("Blue",
            text.Length > 0 ? text + "  " + FormatDistance(distance) : FormatDistance(distance),
            new Vector2(normalized.X,
                        normalized.Y - iconSize.Y * FleetIcons.EchelonBandFraction(markCount) - LabelGap),
            LabelScale, labelColour,
            MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_BOTTOM,
            useFullClientArea: true, maxTextWidth: float.PositiveInfinity, ignoreBounds: true);
    }

    /// <summary>A marker's text before its distance. A grid's label is a sentence (owner, class,
    /// blocks, armament); without Alt the marker keeps only the owner's tag, as a fleet keeps only
    /// its phrase. With Alt the name goes in front.</summary>
    private static string LabelText(Contact c, List<string> gpsTexts)
    {
        if (string.IsNullOrEmpty(c.Label)) return "";
        string text = c.Kind == FactionsWire.ContactKindGrid && gpsTexts == null
            ? ScannerNames.GridOwnerTag(c.Label)
            : WithoutClassWord(c.Kind, c.Label);
        if (gpsTexts != null && ShowName(c.DisplayName, gpsTexts))
            text = text.Length > 0 ? c.DisplayName + PhraseSeparator + text : c.DisplayName;
        return text;
    }

    private static bool IsOffScreen(Vector2 normalized)
    {
        return normalized.X < EdgeMargin || normalized.X > 1f - EdgeMargin
            || normalized.Y < EdgeMargin || normalized.Y > 1f - EdgeMargin;
    }

    /// <summary>An on-screen contact, projected, waiting to be bundled.</summary>
    private struct Placed
    {
        public string Key;
        public Contact Contact;
        public Vector3D World;
        public double Distance;
        public float Alpha;
        public Vector2 Normalized;
    }

    private readonly List<Placed> _placed = new List<Placed>();
    private readonly List<Placed> _bundle = new List<Placed>();

    /// <summary>Contacts within SE's cluster angle of each other draw as one bundle.</summary>
    private const float BundleIconScale = 0.75f;      // SE draws a group's icons at 0.75
    private const int BundleGlyphIcons = 4;           // SE's group shows at most four
    private const int BundleListRows = 8;

    /// <summary>
    /// Draws this frame's on-screen contacts, bundling the ones close together the way SE's HUD
    /// bundles its own markers (<see cref="HudClusters"/>): a single contact draws as before; a
    /// bundle draws as up to four small icons in a square, with its count and its nearest
    /// distance. Holding Alt opens a bundle into a list, one row per contact with the text and
    /// distance that contact would show on its own. SE opens a group when the crosshair is on it;
    /// here Alt does, the gesture that already means "more detail" on this HUD.
    ///
    /// <para>Hostile contacts sort first, then nearer ones, so a bundle's seed and its first icons
    /// are the ones that matter most. A station whose traffic control is speaking is never
    /// bundled: its ring is drawn on its own icon.</para>
    /// </summary>
    private void DrawPlaced(Vector3D cameraPos, ref MatrixD view, ref MatrixD projection, Vector2 iconSize,
                            double now, List<string> gpsTexts)
    {
        if (_placed.Count == 0) return;
        _placed.Sort((a, b) =>
        {
            bool ha = a.Contact.Relation == 3, hb = b.Contact.Relation == 3;
            if (ha != hb) return ha ? -1 : 1;
            return a.Distance.CompareTo(b.Distance);
        });
        int n = _placed.Count;
        var xyz = new double[3 * n];
        var clusters = new bool[n];
        for (int i = 0; i < n; i++)
        {
            Placed p = _placed[i];
            xyz[3 * i] = p.World.X; xyz[3 * i + 1] = p.World.Y; xyz[3 * i + 2] = p.World.Z;
            bool speaking;
            clusters[i] = !(p.Key.StartsWith(NodeKeyPrefix, StringComparison.Ordinal)
                            && AtcComponent.TrySpeaking(p.Key.Substring(NodeKeyPrefix.Length), out speaking));
        }
        int[] seedOf = HudClusters.Seeds(xyz, clusters, cameraPos.X, cameraPos.Y, cameraPos.Z,
                                         HudClusters.ClusterAngleDeg);
        for (int i = 0; i < n; i++)
        {
            if (seedOf[i] != i) continue;
            _bundle.Clear();
            for (int j = i; j < n; j++) if (seedOf[j] == i) _bundle.Add(_placed[j]);
            if (_bundle.Count == 1)
            {
                Placed p = _bundle[0];
                DrawContact(p.Key, p.Contact, p.World, p.Distance, p.Alpha, ref view, ref projection,
                            iconSize, now, gpsTexts);
            }
            else DrawBundle(_bundle, iconSize, gpsTexts);
        }
        _placed.Clear();
        _bundle.Clear();
    }

    private void DrawBundle(List<Placed> members, Vector2 iconSize, List<string> gpsTexts)
    {
        Vector2 anchor = Vector2.Zero;
        float alpha = 0f;
        double nearest = double.MaxValue;
        for (int i = 0; i < members.Count; i++)
        {
            anchor += members[i].Normalized;
            alpha = Math.Max(alpha, members[i].Alpha);
            nearest = Math.Min(nearest, members[i].Distance);
        }
        anchor /= members.Count;
        Vector2 mini = iconSize * BundleIconScale;
        Color lead = Palette.RelationColour(members[0].Contact.Relation);

        if (gpsTexts == null)
        {
            int icons = Math.Min(BundleGlyphIcons, members.Count);
            for (int k = 0; k < icons; k++)
            {
                // Two by two, filled left to right and top to bottom; three leaves the corner empty.
                var offset = new Vector2((k % 2 == 0 ? -0.5f : 0.5f) * mini.X, (k < 2 ? -0.5f : 0.5f) * mini.Y);
                DrawMini(members[k], anchor + offset, mini);
            }
            MyGuiManager.DrawString("Blue",
                members.Count + " contacts  " + FormatDistance(nearest),
                new Vector2(anchor.X, anchor.Y - mini.Y - LabelGap),
                LabelScale, new Color(lead, alpha),
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_BOTTOM,
                useFullClientArea: true, maxTextWidth: float.PositiveInfinity, ignoreBounds: true);
            return;
        }

        // Alt: a list, one row per contact, below and to the right of the bundle's point.
        float row = Math.Max(mini.Y, MyGuiManager.MeasureString("Blue", "Xg", LabelScale).Y) * 1.1f;
        int rows = members.Count > BundleListRows ? BundleListRows - 1 : members.Count;
        float y = anchor.Y;
        for (int k = 0; k < rows; k++)
        {
            Placed m = members[k];
            DrawMini(m, new Vector2(anchor.X, y), mini);
            string text = LabelText(m.Contact, gpsTexts);
            MyGuiManager.DrawString("Blue",
                text.Length > 0 ? text + "  " + FormatDistance(m.Distance) : FormatDistance(m.Distance),
                new Vector2(anchor.X + mini.X, y),
                LabelScale, new Color(Palette.RelationColour(m.Contact.Relation), m.Alpha),
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
                useFullClientArea: true, maxTextWidth: float.PositiveInfinity, ignoreBounds: true);
            y += row;
        }
        if (rows < members.Count)
            MyGuiManager.DrawString("Blue", "+" + (members.Count - rows) + " more",
                new Vector2(anchor.X + mini.X, y),
                LabelScale, new Color(lead, alpha),
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
                useFullClientArea: true, maxTextWidth: float.PositiveInfinity, ignoreBounds: true);
    }

    private static void DrawMini(Placed m, Vector2 at, Vector2 size)
    {
        Color colour = Palette.RelationColour(m.Contact.Relation);
        colour.A = (byte)(m.Alpha * 255f);
        MyGuiManager.DrawSpriteBatch(ContactTexture(m.Contact), at, size, colour,
            MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
            useFullClientArea: true, waitTillLoaded: false, maskTexture: null,
            rotation: 0f, rotSpeed: 0f, ignoreBounds: true);
    }

    /// <summary>The text SE's HUD writes beside each of the local player's GPSes that is shown on
    /// the HUD. Refilled per frame, and only while Alt is held; the list is the player's own and
    /// a few dozen entries at most.</summary>
    private List<string> HudGpsTexts()
    {
        _gpsTexts.Clear();
        if (MySession.Static == null || MySession.Static.Gpss == null) return _gpsTexts;
        long identityId = MySession.Static.LocalPlayerId;
        if (identityId == 0L) return _gpsTexts;

        _gpsList.Clear();
        MySession.Static.Gpss.GetGpsList(identityId, _gpsList);
        foreach (IMyGps gps in _gpsList)
        {
            if (gps == null || !gps.ShowOnHud) continue;
            var full = gps as MyGps;
            _gpsTexts.Add(ScannerNames.HudText(gps.Name, full != null ? full.DisplayName : null));
        }
        return _gpsTexts;
    }

    /// <summary>A node's name is drawn unless a GPS on the HUD already says it, which SE draws at
    /// the same spot -- two copies of one name overprint each other.</summary>
    private static bool ShowName(string name, List<string> gpsTexts)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < gpsTexts.Count; i++)
            if (ScannerNames.GpsShowsName(gpsTexts[i], name)) return false;
        return true;
    }

    /// <summary>
    /// How many hulls the convoy is, as marks above its marker, in the marker's own colour.
    /// Returns how many were drawn, which is what the label needs in order to clear them.
    ///
    /// <para>Zero for everything that is not a fleet, and for a one-hull convoy: most fleets are
    /// one hull, and a mark on every marker carries no signal. The ladder is
    /// <see cref="FleetEchelon"/>'s, shared with the map and with the web dashboard.</para>
    ///
    /// <para>Falls back to drawing nothing rather than to a stock texture. A missing mark costs a
    /// detail; a wrong one would be counted.</para>
    /// </summary>
    private static int DrawEchelon(Contact c, Vector2 normalized, Vector2 iconSize, Color colour)
    {
        if (!FactionsWire.ContactCarriesFleetFields(c.Kind)) return 0;

        FleetEchelon.Mark mark = FleetEchelon.For(c.ConvoySize);
        if (mark.Count <= 0) return 0;

        string texture = IconStore.Texture(FleetIcons.TextureName(mark.Glyph));
        if (texture == null) return 0;

        Vector2 markSize = iconSize * FleetIcons.EchelonMarkFraction;
        float spacing = iconSize.X * FleetIcons.EchelonSpacingFraction;
        float first = normalized.X - spacing * (mark.Count - 1) * 0.5f;
        // Normalized Y runs downward, so clearing the top of the marker is a subtraction.
        float y = normalized.Y - iconSize.Y * (0.5f + FleetIcons.EchelonGapFraction) - markSize.Y * 0.5f;

        for (int i = 0; i < mark.Count; i++)
        {
            MyGuiManager.DrawSpriteBatch(texture, new Vector2(first + spacing * i, y), markSize, colour,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                useFullClientArea: true, waitTillLoaded: false, maskTexture: null,
                rotation: 0f, rotSpeed: 0f, ignoreBounds: true);
        }
        return mark.Count;
    }

    private static void Bump(Dictionary<byte, int> into, byte kind)
    {
        int n;
        into.TryGetValue(kind, out n);
        into[kind] = n + 1;
    }

    private static string Histogram(Dictionary<byte, int> counts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (KeyValuePair<byte, int> pair in counts)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(KindName(pair.Key)).Append('=').Append(pair.Value);
        }
        return sb.Length > 0 ? sb.ToString() : "(none)";
    }

    /// <summary>Full strength up close, <see cref="MinRangeAlpha"/> at the edge of the sphere.</summary>
    private static float RangeAlpha(double distance, double maxRange)
    {
        if (maxRange <= 0) return 1f;
        float t = (float)MathHelper.Clamp(distance / maxRange, 0.0, 1.0);
        return MathHelper.Lerp(1f, MinRangeAlpha, t);
    }

    private static string FormatDistance(double metres)
    {
        return metres >= 1000.0
            ? (metres / 1000.0).ToString("0.#") + " km"
            : metres.ToString("0") + " m";
    }

    /// <summary>Wall clock rather than session time: fades are presentation, and should not
    /// stretch or freeze with sim speed or a paused session.</summary>
    private const string NodeKeyPrefix = "node:";
    private static readonly Color SpeakerColour = new Color(110, 170, 255);
    private const float SpeakerRingScale = 2.0f;     // of the icon, at the trough of the pulse
    private const float SpeakerPulse = 0.35f;        // added at the crest
    private const double SpeakerPulseHz = 1.2;
    private const double SpeakerSpinRadPerSec = 0.6;

    /// <summary>
    /// A dashed ring pulsing around a station's icon while its controller speaks: the radio line no
    /// longer names the station (operator, 2026-10-02: the name on every callout "gets very
    /// repetitive"; the marker belongs "at the station's HUD icon ... a dashed pulsating circle").
    /// Red for a hostile warning. Nothing is drawn when the art did not extract.
    /// </summary>
    private static void DrawSpeakerRing(Vector2 centre, Vector2 iconSize, float alpha, bool hostile, double now)
    {
        string ring = IconStore.Texture("radio_ring");
        if (ring == null) return;
        float crest = (float)(0.5 + 0.5 * Math.Sin(now * SpeakerPulseHz * 2.0 * Math.PI));
        Color colour = hostile ? Color.Red : SpeakerColour;
        colour.A = (byte)(alpha * (0.55f + 0.45f * crest) * 255f);
        MyGuiManager.DrawSpriteBatch(ring, centre, iconSize * (SpeakerRingScale + SpeakerPulse * crest), colour,
            MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
            useFullClientArea: true, waitTillLoaded: false, maskTexture: null,
            rotation: (float)(now * SpeakerSpinRadPerSec % (2.0 * Math.PI)), rotSpeed: 0f, ignoreBounds: true);
    }

    private static double NowSeconds()
    {
        return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
    }

    // ---- Presentation tables ------------------------------------------------------------------
    // Nodes are stock SE HUD/GUI textures tinted per kind. Convoys are this plugin's own art,
    // shared with the map window — see FleetIcons and IconStore.

    private const string OffScreenTexture = "Textures\\HUD\\marker_gps.dds";

    /// <summary>A grid the sim does not own: SE's own grid marker, tinted by relation like every
    /// other contact. The map draws the same texture for the same grid.</summary>
    internal const string GridTexture = "Textures\\HUD\\marker_friendly.dds";

    /// <summary>What a convoy draws when this client could not write its own icon art to disk.
    /// Stock SE, so it cannot fail for the same reason the art did.</summary>
    private const string FleetFallbackTexture = "Textures\\HUD\\marker_alert.dds";

    /// <summary>
    /// The marker for one contact. A node is named by its <c>ScannerContactKind</c>; a convoy is
    /// named by its <b>mission</b>, out of the same <see cref="FleetIcons"/> table the map marker
    /// reads, so one fleet does not draw as two different things on two surfaces.
    ///
    /// <para><b>A fight does not override the mission here either.</b> This is where a
    /// <c>RaidEngagement</c> contact used to swap to <c>marker_enemy.dds</c> — the only way to say
    /// "fighting" when every convoy drew the same exclamation triangle. The HUD still says it, and
    /// more plainly than the map can: the contact's label is already <c>"Raid: &lt;place&gt;"</c>
    /// rather than <c>"Fleet"</c>, and it names the node being fought over. The map gave this up
    /// for a pulsing ring; the HUD gives it up for a label that was already there.</para>
    /// </summary>
    private static string ContactTexture(Contact c)
    {
        if (FactionsWire.ContactCarriesFleetFields(c.Kind))
        {
            string art = IconStore.Texture(FleetIcons.TextureName(FleetIcons.GlyphFor(c.FleetKind)));
            return art ?? FleetFallbackTexture;
        }

        switch (c.Kind)
        {
            case 0: return "Textures\\HUD\\marker_scenario.dds";             // Station
            case 1: return "Textures\\HUD\\marker_neutral.dds";              // Outpost
            case 2: return "Textures\\GUI\\Icons\\filter_ore.dds";           // OreField
            case 3: return "Textures\\GUI\\Icons\\ore_Pt_platinum.dds";      // RareOreField
            case 4: return "Textures\\GUI\\Icons\\ore_H2O_ice.dds";          // IceField
            case 5: return "Textures\\GUI\\Icons\\filter_ingot.dds";         // Refinery (ingots = its output)
            case 6: return "Textures\\GUI\\Icons\\filter_hydrogen.dds";      // FuelDepot
            case FactionsWire.ContactKindGrid: return GridTexture;
            default: return "Textures\\HUD\\marker_gps.dds";
        }
    }

    private sealed class Contact
    {
        public byte Kind;
        /// <summary>The convoy's mission, as a Core ActionKind ordinal; zero unless this contact
        /// is a fleet. Drives the silhouette through the table the map marker also reads.</summary>
        public byte FleetKind;
        /// <summary>Hulls in the convoy; zero unless this contact is a fleet. Carried for the
        /// strength mark, which the HUD does not draw yet.</summary>
        public byte ConvoySize;
        public string Label;
        /// <summary>The node's own name, or empty; drawn only while Alt is held.</summary>
        public string DisplayName;
        public Vector3D PositionM;
        public long EntityId;
        public bool Unclaimed;
        public byte Relation;
        public double AcquiredAt;
        /// <summary>Non-zero once the server says the contact left the sphere; it keeps drawing
        /// until the fade-out completes.</summary>
        public double LostAt;

        private bool _moving;
        /// <summary>Whether the destination was offered. Without it the contact coasts on its
        /// velocity for a pass instead of stopping at a target it was never told.</summary>
        private bool _hasTarget;
        private Vector3D _velocityMPerSec;
        private Vector3D _targetM;
        private double _sampledAtSeconds;

        public Contact(WireContact w) { Apply(w); }

        public void Apply(WireContact w)
        {
            Kind = w.Kind;
            FleetKind = w.FleetKind;
            ConvoySize = w.ConvoySize;
            Label = w.Label;
            DisplayName = w.DisplayName;
            PositionM = new Vector3D(w.XKm * 1000.0, w.YKm * 1000.0, w.ZKm * 1000.0);
            EntityId = w.EntityId;
            Unclaimed = w.Unclaimed;
            Relation = w.Relation;

            _moving = w.Moving;
            _velocityMPerSec = _moving
                ? new Vector3D(
                    w.VelXKmPerMin * KmPerMinToMPerSec,
                    w.VelYKmPerMin * KmPerMinToMPerSec,
                    w.VelZKmPerMin * KmPerMinToMPerSec)
                : Vector3D.Zero;
            _hasTarget = _moving && w.HasTarget;
            _targetM = _hasTarget
                ? new Vector3D(w.TargetXKm * 1000.0, w.TargetYKm * 1000.0, w.TargetZKm * 1000.0)
                : Vector3D.Zero;
            _sampledAtSeconds = NowSeconds();
        }

        /// <summary>
        /// Where the contact is right now. A contact whose entity is streamed to this client is
        /// drawn at the grid's real position; a moving one without a streamed grid is dead-reckoned
        /// from the last sample, so it tracks at frame rate rather than stepping once per scanner
        /// pass. Anchored contacts are simply where the server put them.
        ///
        /// Extrapolation is <see cref="DeadReckon"/>'s, which is also what the map draws fleets by.
        /// </summary>
        public Vector3D WorldPosition()
        {
            if (EntityId != 0)
            {
                IMyEntity entity;
                if (MyAPIGateway.Entities.TryGetEntityById(EntityId, out entity)
                    && entity != null && !entity.MarkedForClose)
                    return entity.GetPosition();
            }
            if (!_moving) return PositionM;

            double x, y, z;
            if (!_hasTarget)
            {
                DeadReckon.ExtrapolateOpen(
                    PositionM.X, PositionM.Y, PositionM.Z,
                    _velocityMPerSec.X, _velocityMPerSec.Y, _velocityMPerSec.Z,
                    NowSeconds() - _sampledAtSeconds,
                    out x, out y, out z);
                return new Vector3D(x, y, z);
            }
            DeadReckon.Extrapolate(
                PositionM.X, PositionM.Y, PositionM.Z,
                _velocityMPerSec.X, _velocityMPerSec.Y, _velocityMPerSec.Z,
                _targetM.X, _targetM.Y, _targetM.Z,
                NowSeconds() - _sampledAtSeconds,
                out x, out y, out z);
            return new Vector3D(x, y, z);
        }

        public float FadeAlpha(double now, double fadeIn, double fadeOut)
        {
            if (LostAt > 0)
            {
                double remaining = 1.0 - (now - LostAt) / fadeOut;
                return remaining <= 0 ? 0f : (float)MathHelper.Clamp(remaining, 0.0, 1.0);
            }
            double age = now - AcquiredAt;
            return age >= fadeIn ? 1f : (float)MathHelper.Clamp(age / fadeIn, 0.0, 1.0);
        }
    }
}
