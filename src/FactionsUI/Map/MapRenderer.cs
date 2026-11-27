using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Map;

/// <summary>
/// Draws the map's overlay. The planets, sunlight and skybox underneath are the real world seen
/// through <see cref="MapCameraController"/> — nothing here draws them, which is the whole point of
/// the design.
///
/// Two mechanisms, deliberately:
///   * routes and rings are world-space line billboards, so they are depth-tested and pass behind
///     planets correctly;
///   * icons and labels are GUI sprites, which always composite above the world and so stay crisp,
///     constant-sized and hit-testable.
///
/// Every GUI call goes through MyGuiManager normalized space with useFullClientArea: true. That is
/// not stylistic — Projection yields full-screen normalized coordinates, and mapping them through
/// the narrower safe-GUI rectangle makes markers point short of their targets.
/// </summary>
internal sealed class MapRenderer
{
    private const float IconPixels = 18f;
    private const float StationIconPixels = 24f;

    /// <summary>
    /// A body's ring, and it is drawn at a CONSTANT size and always labelled -- neither the marker
    /// falloff nor the label cap applies to it.
    ///
    /// <para>Both exceptions come from the same fact: a body is the one thing on this map the game
    /// draws and we do not. Its marker is not a depth cue competing with its neighbours, it is the
    /// only handle the map has on an object that is a few pixels wide at system scale and fills the
    /// screen from orbit. Receding it with distance would shrink it exactly where it is the only way
    /// to find the body, and letting it lose a label to the nearest forty stations would leave the
    /// largest objects in the world the only unnamed ones.</para>
    ///
    /// <para>There are a handful of bodies, so neither exception costs anything: they do not consume
    /// the label budget and are not counted against it.</para>
    /// </summary>
    private const float BodyIconPixels = 26f;

    /// <summary>Pale and desaturated, deliberately outside the relation palette: a body is not owned
    /// by anyone, and a marker in any faction's colour would say it was.</summary>
    private static readonly Color BodyColour = new Color(170, 195, 215);

    /// <summary>
    /// Label size, and it is a CONSTANT -- labels do not recede with their marker.
    ///
    /// <para>They used to, scaled by the same distance falloff the icons use and floored at 0.62.
    /// That reads backwards on a map: the falloff is measured against the camera's own orbit
    /// distance, so zooming IN shrinks it for everything that is not the focus point itself. At full
    /// zoom-in (500 m orbit, nearest station tens of kilometres off) every label on screen sat on
    /// its floor, so the closer the player looked the smaller the names got.</para>
    ///
    /// <para>Text is not a depth cue anyway -- it is legible or it is not, and there is no reading
    /// distance at which two thirds of legible is useful. Depth is still carried -- by the alpha
    /// recession at the draw site, and by <see cref="MaxLabels"/> awarding labels to the nearest
    /// stations at all -- so size is spent on being readable instead.</para>
    /// </summary>
    private const float LabelScale = 0.55f;

    private const float LabelGapNormalized = 0.004f;

    /// <summary>
    /// Black outline behind every label, as a fraction of screen HEIGHT so it stays proportional to
    /// the text at any resolution -- the same convention the scanner sizes its icon by.
    ///
    /// <para>The map draws over the live world, and the world under a label is not a background it
    /// is a picture: a planet's ring bands run from near-white to black across a few pixels, and a
    /// mid-tone relation colour has nowhere on that range where it reads. An outline gives the
    /// glyphs their own backdrop, so legibility stops depending on what happens to be behind
    /// them.</para>
    ///
    /// <para><b>Four cardinal offsets, not one drop shadow.</b> A shadow only breaks the edge on the
    /// side it falls, which is enough over a uniform backdrop and not enough over a band that is
    /// bright above the text and dark below it. The cardinals surround the glyph for four extra
    /// draws instead of one.</para>
    ///
    /// <para><b>The offset is floored at a whole pixel, and that floor is load-bearing.</b>
    /// <c>MyGuiManager.DrawString</c> takes a normalized coordinate but converts it to screen space
    /// and rounds with <c>Math.Ceiling</c> before handing it to the renderer. Anything under one
    /// pixel of separation can therefore ceiling onto the same pixel as the glyph it is supposed to
    /// sit behind, at which point the outline is drawn and invisible. At a pixel or more,
    /// <c>ceil(x +/- d)</c> is guaranteed to differ from <c>ceil(x)</c>.</para>
    ///
    /// <para><b>The body marker takes the same offset</b>, for the same reason: its glyph is hollow
    /// and so has no fill of its own to carry it. The node icons do not — they are filled art, they
    /// bring their own backdrop, and there are thousands of them.</para>
    /// </summary>
    private const float LabelOutlineFraction = 0.0013f;

    /// <summary>Labels are capped and awarded to the nearest stations. An uncapped map at system
    /// scale is a wall of overlapping text with no information in it.</summary>
    private const int MaxLabels = 40;

    /// <summary>
    /// Markers hold full size out to the camera's own focus distance and shrink beyond it, so a
    /// node ten times further away reads as a tenth-size dot rather than competing at full size
    /// with what the player is actually looking at. Without this every one of ~2600 nodes draws the
    /// same size whatever the zoom, and the map is static.
    /// </summary>
    private const float MinMarkerScale = 0.28f;

    /// <summary>Target on-screen width of a route line. Held constant in PIXELS, not world units:
    /// a fixed world thickness reads as a hairline at system scale and as a slab up close.</summary>
    private const double RouteWidthPixels = 1.25;

    /// <summary>
    /// A quad narrower than about a pixel is sampled intermittently along its length by the
    /// rasteriser, which draws as a dashed line rather than a thin one. Below this, subtlety has to
    /// come from alpha instead of width -- width has a hard floor and that floor is the pixel grid.
    /// </summary>
    private const double MinRouteWidthPixels = 1.15;

    /// <summary>The selected fleet's route is the one the player asked to see, so it is drawn
    /// heavier and at full strength while every other route stays background texture.</summary>
    private const double SelectedRouteWidthPixels = 2.2;
    private const float SelectedRouteAlpha = 0.95f;

    /// <summary>Routes fade with distance so the near ones read and the far ones stay texture.</summary>
    private const float RouteNearAlpha = 0.34f;

    /// <summary>
    /// Floor on the distance falloff, so a far route becomes faint rather than absent. The previous
    /// band-with-cutoff model culled anything past 1.26x the camera distance, which in a world
    /// 11 760 km across viewed from 5 km out meant every route -- the map showed none at all.
    /// </summary>
    private const float MinRouteAlphaFactor = 0.15f;

    /// <summary>
    /// Below this a route contributes nothing but haze, so it is not drawn at all. With ~190 fleets
    /// live, the far ones do not fade into invisibility individually -- they overlap and sum into a
    /// grey web across the whole map. Culling is the only thing that removes them.
    /// </summary>
    private const float RouteCullAlpha = 0.02f;

    /// <summary>Segments per keep-out ring. Fine enough that one filling the screen still reads as
    /// round rather than as a polygon.</summary>
    private const int ExclusionSphereSegments = 48;

    /// <summary>Rings are capped and awarded to the nearest nodes, as labels are. The pixel culls do
    /// not bound this on their own — a 500 km ring clears the floor out to roughly 39 000 km in a
    /// world 11 760 km across — and the nearest few dozen are the ones a player siting a node is
    /// choosing between.</summary>
    private const int MaxExclusionRings = 48;

    /// <summary>A ring smaller than this on screen is a smudge rather than a boundary.</summary>
    private const double MinExclusionRingRadiusPixels = 8.0;

    /// <summary>At or above the SELECTED route's treatment (2.2 px / 0.95), not the unselected
    /// one's. A route the player did not ask about is background texture; the exclusion sphere is
    /// the answer to the question they opened the overlay to ask, so it belongs in the same tier as
    /// the convoy they clicked.</summary>
    private const double ExclusionSphereWidthPixels = 2.4;
    private const float ExclusionSiteAlpha = 1.0f;
    /// <summary>The silhouette carries the boundary; the interior circles only say "this is a
    /// volume". Subordinate to it, but still above the unselected route's 0.34 — below that they
    /// read as haze rather than as geometry.</summary>
    /// <summary>Keep-out rings are the overlay's primary information, so they are drawn at very
    /// nearly full strength. At 0.40 over the near-black of space they blended down to roughly
    /// (94,44,36) — a dull maroon that vanished against the backdrop and read as a *darker* arc
    /// wherever it crossed something bright.</summary>
    private const float ExclusionRingAlpha = 0.90f;

    /// <summary>Clear ground and occupied ground, matching the HUD readout's own two states so the
    /// map and the line under it never disagree about the same site.
    ///
    /// <para><b>Saturated, not tinted.</b> Space is nearly black and the map draws over the real
    /// world — planets, rings, nebula — so an infographic colour has to hold against both ends of
    /// that range. The muted (235,110,90) salmon and (120,200,120) sage these replace were chosen on
    /// a white page: over black they blend toward nothing, and over a lit planet ring they read as
    /// dirt. These are the vivid end of each hue, matched to the self-marker green that is the one
    /// thing on the map already legible against both.</para></summary>
    private static readonly Color ExclusionClearColour = new Color(110, 255, 140);
    private static readonly Color ExclusionOccupiedColour = new Color(255, 64, 64);

    /// <summary>
    /// The alarm heartbeat, in radians per second. 2.5 is the dashboard's own beat written for a
    /// clock in seconds rather than milliseconds (<c>sin(ms / 400)</c>) — about 2.5 s a cycle, slow
    /// enough to read as a pulse rather than a flicker. The two surfaces share it deliberately: one
    /// raid seen on both should not look like two mechanics.
    /// </summary>
    private const double AlarmPulseRadiansPerSecond = 2.5;

    /// <summary>A fight in progress, wherever it is drawn. Shared by an NPC raid holding its dwell
    /// and by a player siege still working on a shield: to whoever owns the node they are the same
    /// event.</summary>
    private static readonly Color AlarmAttackColour = new Color(255, 107, 74);

    /// <summary>The shield is down and the station itself is being shot — the phase in which the
    /// node can actually be lost, so it is hotter than the one that can still be denied.</summary>
    private static readonly Color AlarmBreachColour = new Color(255, 45, 45);

    /// <summary>A Reinforce holding its dwell. Far from the attack colour on the wheel, because
    /// telling a garrison from a raid at a glance is the whole point of drawing them apart.</summary>
    private static readonly Color DefenceHoldColour = new Color(93, 173, 226);

    /// <summary>A station whose safe zone is off after a raid landed, with nobody currently shooting
    /// it. Not an alarm — it is a window, and it does not beat.</summary>
    private static readonly Color UnprotectedColour = new Color(255, 190, 70);

    /// <summary>
    /// A ring's on-screen radius when the node it belongs to is too small to give it one. An alarm
    /// is a highlighter before it is geometry: at system scale a ring sized purely in world metres
    /// falls under the pixel grid and is culled, which is exactly when the player most needs to be
    /// told where the fight is. So the radius is the larger of the node's own bubble and this many
    /// pixels, and the ring stops shrinking while the marker keeps doing so.
    /// </summary>
    private const double AlarmRingPixels = 26.0;

    /// <summary>How much of the node's radius the ring spans when the node is big enough on screen
    /// to set the radius itself — outside the marker, inside anything else.</summary>
    private const double AlarmRingNodeRadii = 2.2;

    /// <summary>At the selected route's weight: an alarm is never background texture.</summary>
    private const double AlarmRingWidthPixels = 2.6;

    /// <summary>How far a pulsing ring swells. Size and brightness beat together — either alone
    /// reads as a rendering artefact rather than as an alarm.</summary>
    private const double AlarmRingSwell = 0.28;

    private const float AlarmAlphaFloor = 0.35f;
    private const float AlarmAlphaSwing = 0.45f;

    /// <summary>A ring that does not beat: a garrison, or a station waiting for its protection
    /// back. Held at the bright end of the pulse's range so it reads as deliberate rather than as
    /// an alarm caught mid-cycle.</summary>
    private const float SteadyRingAlpha = 0.5f;

    /// <summary>Reused across frames: ring selection runs every draw and would otherwise allocate
    /// per frame, sized by the world.</summary>
    private readonly List<RingCandidate> _ringCandidates = new List<RingCandidate>();

    private struct RingCandidate
    {
        public Vector3D CentreM;
        public double Distance;
    }

    private const double MinRouteThicknessM = 1.0;
    /// <summary>Runaway guard only. The pixel-derived value is the real answer.</summary>
    private const double MaxRouteThicknessM = 2.0e6;

    /// <summary>Floor on a marker's clickable radius. Icon-sized hit targets are unclickable for
    /// the small kinds, and the map is a pointing device before it is a picture.</summary>
    private const float MinPickRadiusPixels = 14f;

    /// <summary>
    /// "SquareFullColor", not "Square". Square is a PARTICLE texture with soft edges: a hairline
    /// quad samples mostly its falloff, so the line renders faint and patchy and its alpha never
    /// reads as the value asked for. square_fullcolor.dds is flat and opaque.
    /// </summary>
    private static readonly MyStringId RouteMaterial = MyStringId.GetOrCompute("SquareFullColor");

    private readonly List<Drawable> _drawables = new List<Drawable>();
    private readonly List<MyPlanet> _planets = new List<MyPlanet>();

    /// <summary>Screen positions from the last draw, so the screen can hit-test a click without
    /// re-projecting the world.</summary>
    public readonly List<PickTarget> PickTargets = new List<PickTarget>();

    public void Draw(MapWorld world, string selectedId, double cameraDistanceM)
    {
        MatrixD view;
        MatrixD projection;
        Vector3D cameraPos;
        if (!Projection.TryGetCamera(out view, out projection, out cameraPos)) return;

        RefreshPlanets();
        _drawables.Clear();
        PickTargets.Clear();

        // The exclusion volume, resolved once for the whole frame so every node is tested against
        // the same site. Radius 0 means no site quoted yet, and nothing is marked.
        Vector3D siteCentre;
        double siteRadiusM = 0;
        bool siteClear;
        ExpandComponent expand = ExpandComponent.Instance;
        if (expand == null || !Config.Current.MapShowSeparation
            || !expand.TryGetSite(out siteCentre, out siteRadiusM, out siteClear))
        {
            siteCentre = Vector3D.Zero;
            siteRadiusM = 0;
        }
        double siteRadiusSq = siteRadiusM * siteRadiusM;

        bool showEmpty = Config.Current.MapShowEmptyNodes;
        Campaigns.CampaignsComponent campaigns = Campaigns.CampaignsComponent.Instance;
        for (int i = 0; i < world.Nodes.Count; i++)
        {
            MapWorld.MapNode node = world.Nodes[i];
            // A node inside the exclusion sphere is drawn even when "empty nodes" is off: it is the
            // reason the site is refused, and hiding the cause while showing the effect is worse
            // than the clutter the toggle exists to remove.
            bool blocking = siteRadiusM > 0
                            && Vector3D.DistanceSquared(node.PositionM, siteCentre) < siteRadiusSq;
            if (node.IsEmpty && !showEmpty && !blocking) continue;
            // A node under one of the player's own directives is always labelled, and the label
            // says so: the order is the one thing on this map the player put there themselves.
            string label = node.HasStation && node.HasDistinctName ? node.LabelText : null;
            bool labelCandidate = node.HasStation && node.HasDistinctName;
            if (!node.Identified)
            {
                // Below Identity the stage is the one thing a word can say: once the name is
                // resolved the label is the name and the stage lives in the icon.
                label = node.HasStation ? NodeLabels.StageWord(node.Stage) : null;
                labelCandidate = label != null;
            }
            Factions.Wire.WireCampaign directive;
            if (campaigns != null && campaigns.TryGetDirectiveOn(node.Id, out directive))
            {
                label = (label ?? (node.LabelText ?? node.Id))
                        + (directive.Kind == 1 ? "  [CONQUEST]" : directive.Kind == 2 ? "  [DEFEND]" : "  [RAID]");
                labelCandidate = true;
            }
            NodeIcon icon = NodeIconFor(node);
            Collect(node.PositionM, cameraPos, ref view, ref projection,
                icon.Shell != null || node.HasStation ? StationIconPixels : IconPixels,
                Palette.KnowledgeColour(node.Relation, node.Detected),
                icon.Glyph,
                // Only a label that identifies the node is worth drawing -- see
                // MapWorld.MarkDistinctNames for how a shared name earns its owner tag first.
                label,
                node.Id,
                labelCandidate,
                blocking,
                echelonTexture: DefencePips(node) > 0 ? IconStore.Texture("echelon_dot") : null,
                echelonCount: DefencePips(node),
                shellTexture: icon.Shell,
                glyphScale: icon.GlyphScale,
                glyphOffset: icon.GlyphOffset,
                glyphDark: icon.GlyphDark);
        }

        for (int i = 0; i < world.Fleets.Count; i++)
        {
            MapWorld.MapFleet fleet = world.Fleets[i];
            Vector3D position = fleet.WorldPosition();
            FleetEchelon.Mark echelon = FleetEchelon.For(fleet.ConvoySize);
            Collect(position, cameraPos, ref view, ref projection,
                IconPixels, Palette.RelationColour(fleet.Relation),
                FleetTexture(fleet), null, fleet.Key, false,
                echelonTexture: IconStore.Texture(FleetIcons.TextureName(echelon.Glyph)),
                echelonCount: echelon.Count);

            if (Config.Current.MapShowRoutes && fleet.HasFlightPlan)
            {
                bool selected = selectedId != null && selectedId == fleet.Key;
                DrawRoute(fleet, position, cameraPos, cameraDistanceM, selected);
            }
        }

        DrawAlarmRings(world, cameraPos);

        if (Config.Current.MapShowSeparation) DrawExclusionZones(world, cameraPos);

        if (Config.Current.MapShowBodies) CollectBodies(cameraPos, ref view, ref projection);

        DrawOwnPosition(cameraPos, ref view, ref projection);

        // Painter's algorithm: far first, so a near marker overlaps a distant one rather than the
        // draw order deciding it arbitrarily.
        _drawables.Sort(CompareFarToNear);
        DrawCollected(selectedId, cameraDistanceM);
    }

    // ---- Collection -----------------------------------------------------------------------

    private void Collect(
        Vector3D worldPos, Vector3D cameraPos, ref MatrixD view, ref MatrixD projection,
        float iconPixels, Color colour, string texture, string label, string id, bool labelCandidate,
        bool blocking = false, string echelonTexture = null, int echelonCount = 0,
        string shellTexture = null, float glyphScale = 1f, float glyphOffset = 0f, bool glyphDark = false)
    {
        Vector2 normalized;
        bool behind;
        if (!Projection.Project(worldPos, ref view, ref projection, out normalized, out behind)) return;

        // Off-screen markers are dropped rather than clamped to the edge. The scanner clamps because
        // it is a heads-up display and direction is the message; a map has a camera the player can
        // move, and edge indicators would only fight the pan.
        if (behind || normalized.X < 0f || normalized.X > 1f || normalized.Y < 0f || normalized.Y > 1f) return;

        double distance = Vector3D.Distance(worldPos, cameraPos);
        if (IsOccludedByPlanet(cameraPos, worldPos)) return;

        _drawables.Add(new Drawable
        {
            Id = id,
            Normalized = normalized,
            Distance = distance,
            IconPixels = iconPixels,
            Colour = colour,
            Texture = texture,
            Label = label,
            LabelCandidate = labelCandidate,
            Blocking = blocking,
            EchelonTexture = echelonTexture,
            EchelonCount = echelonCount,
            ShellTexture = shellTexture,
            GlyphScale = glyphScale,
            GlyphOffset = glyphOffset,
            GlyphDark = glyphDark,
        });
    }

    private void DrawCollected(string selectedId, double cameraDistanceM)
    {
        // Labels go to the nearest candidates. _drawables is sorted far-to-near, so skipping the
        // first (total - MaxLabels) candidates leaves exactly the nearest MaxLabels of them.
        bool showLabels = Config.Current.MapShowLabels;

        // One outline offset for the whole frame. Derived from a PIXEL square, as the icons are, so
        // that it is the same distance in both axes on screen -- normalized space is anisotropic,
        // and an outline built from a normalized square is thicker vertically than horizontally.
        float outlinePixels = Math.Max(
            MyGuiManager.GetSafeFullscreenRectangle().Height * LabelOutlineFraction, 1f);
        Vector2 outline = MyGuiManager.GetNormalizedSizeFromScreenSize(
            new Vector2(outlinePixels, outlinePixels));
        int candidates = 0;
        for (int i = 0; i < _drawables.Count; i++)
            if (_drawables[i].LabelCandidate && _drawables[i].Label != null) candidates++;
        int skipCandidates = Math.Max(0, candidates - MaxLabels);
        int seenCandidates = 0;

        for (int i = 0; i < _drawables.Count; i++)
        {
            Drawable d = _drawables[i];
            bool selected = selectedId != null && selectedId == d.Id;

            // A body holds its size -- see BodyIconPixels.
            float scale = d.IsBody ? 1f : MarkerScale(d.Distance, cameraDistanceM);
            float px = d.IconPixels * scale;
            Vector2 iconSize = MyGuiManager.GetNormalizedSizeFromScreenSize(new Vector2(px, px));

            if (d.Blocking)
            {
                // Same ring the selection uses, in the occupied colour, and drawn UNDER a selection
                // ring so selecting a blocker does not hide why it was interesting. The icon's own
                // colour keeps meaning ownership either way.
                Vector2 blockSize = iconSize * 2.4f;
                DrawIcon(SelectionTexture, d.Normalized, blockSize, ExclusionOccupiedColour);
            }

            if (selected)
            {
                // A ring behind the icon, not a colour change: the colour is already carrying
                // ownership and must keep meaning that.
                Vector2 ringSize = iconSize * 1.9f;
                DrawIcon(SelectionTexture, d.Normalized, ringSize, Color.Yellow);
            }

            // A body's glyph is hollow by design, so it has no fill to carry it, and the backdrop
            // under it runs from a near-white planet limb to black across a few pixels -- the case
            // the note on LabelOutlineFraction describes for text. Same four cardinal offsets, same
            // reason. Bodies only: there are a handful of them, and the node icons are filled art.
            if (d.IsBody) DrawOutline(d.Texture, d.Normalized, iconSize, d.Colour, outline);

            if (d.ShellTexture != null)
            {
                // The stage shell carries the relation colour; the ore glyph sits on it, dark on a
                // filled shell and in the shell's own colour inside an outline.
                DrawIcon(d.ShellTexture, d.Normalized, iconSize, d.Colour);
                var glyphPos = new Vector2(d.Normalized.X, d.Normalized.Y - iconSize.Y * d.GlyphOffset);
                Color glyphColour = d.GlyphDark ? new Color(GlyphOnShell, d.Colour.A / 255f) : d.Colour;
                if (d.Texture != null) DrawIcon(d.Texture, glyphPos, iconSize * d.GlyphScale, glyphColour);
            }
            else
            {
                DrawIcon(d.Texture, d.Normalized, iconSize, d.Colour);
            }

            if (d.EchelonTexture != null && d.EchelonCount > 0)
                DrawEchelon(d, iconSize);

            // Hit-testing happens in PIXELS. Projection yields full-screen normalized coordinates
            // while the mouse is reported in the safe-GUI space, and the two only agree near the
            // centre of the screen -- which reads as "selection works only when you look straight
            // at the thing". Normalized space is also anisotropic, so a radius in it is an ellipse
            // on screen. Pixels are the one space where both problems go away.
            PickTargets.Add(new PickTarget
            {
                Id = d.Id,
                ScreenPx = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(
                    d.Normalized, useFullClientArea: true),
                // Hit radius from the FLOOR, not the shrunken icon: a distant node drawn as a dot
                // still has to be clickable, and MinPickRadiusPixels is what guarantees that.
                RadiusPx = Math.Max(px * 0.75f, MinPickRadiusPixels),
                Distance = d.Distance,
            });

            if (d.Label == null || !showLabels) continue;
            bool isCandidate = d.LabelCandidate;
            if (isCandidate) seenCandidates++;
            // A selected marker is always labelled, however far out it is, and so is a body.
            if (!selected && !d.IsBody && (!isCandidate || seenCandidates <= skipCandidates)) continue;

            // Constant size -- see LabelScale. Only the alpha recedes, so depth still reads without
            // the text having to give up being readable for it.
            var labelPos = new Vector2(
                d.Normalized.X, d.Normalized.Y - iconSize.Y * 0.5f - LabelGapNormalized);
            var labelColour = new Color(d.Colour, 0.55f + 0.35f * scale);
            DrawLabel(d.Label, labelPos, labelColour, outline);
        }
    }

    /// <summary>
    /// How many hulls a convoy is, as marks centred above its marker — the tactical convention in
    /// which strength is an annotation on the symbol rather than a bigger symbol. Repeating a mark
    /// is what keeps a 20-hull squadron the same number of pixels as a lone trader, which is the
    /// whole reason the marker itself is a constant size.
    ///
    /// <para><b>In the marker's own colour, which means ownership.</b> The mark is part of that
    /// contact and has to read as part of it; a mark in some other colour reads as a second thing
    /// floating above the first.</para>
    ///
    /// <para>The marks shrink with the marker — they annotate it, and an annotation that held its
    /// size would swamp a distant marker it is supposed to qualify. There is deliberately no floor
    /// on that: below a couple of pixels the mark stops being countable, and a countable-looking
    /// smudge is worse than nothing.</para>
    /// </summary>
    private static void DrawEchelon(Drawable d, Vector2 iconSize)
    {
        Vector2 markSize = iconSize * FleetIcons.EchelonMarkFraction;
        float spacing = iconSize.X * FleetIcons.EchelonSpacingFraction;

        // Centred as a group: the first mark sits half the group's width left of centre.
        float first = d.Normalized.X - spacing * (d.EchelonCount - 1) * 0.5f;
        // Normalized Y runs downward, so clearing the top of the marker is a subtraction.
        float y = d.Normalized.Y
                  - iconSize.Y * (0.5f + FleetIcons.EchelonGapFraction)
                  - markSize.Y * 0.5f;

        for (int i = 0; i < d.EchelonCount; i++)
            DrawIcon(d.EchelonTexture, new Vector2(first + spacing * i, y), markSize, d.Colour);
    }

    /// <summary>
    /// One label, outlined. The outline is drawn in the label's OWN alpha rather than at full
    /// strength: a far label recedes as a whole that way, instead of fading to a bright-black
    /// stencil of itself while the near ones stay solid.
    /// </summary>
    private static void DrawLabel(string text, Vector2 position, Color colour, Vector2 outline)
    {
        var shadow = new Color(Color.Black, colour.A / 255f);
        DrawText(text, new Vector2(position.X - outline.X, position.Y), shadow);
        DrawText(text, new Vector2(position.X + outline.X, position.Y), shadow);
        DrawText(text, new Vector2(position.X, position.Y - outline.Y), shadow);
        DrawText(text, new Vector2(position.X, position.Y + outline.Y), shadow);
        DrawText(text, position, colour);
    }

    private static void DrawText(string text, Vector2 position, Color colour)
    {
        MyGuiManager.DrawString("Blue", text, position, LabelScale, colour,
            MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_BOTTOM,
            useFullClientArea: true, maxTextWidth: float.PositiveInfinity, ignoreBounds: true);
    }

    /// <summary>
    /// One sprite, outlined in its own alpha — <see cref="DrawLabel"/> for text, and the same
    /// reasoning: a far marker recedes as a whole rather than fading to a bright-black stencil of
    /// itself.
    ///
    /// <para>Four cardinal offsets rather than one drop shadow. A shadow breaks the edge only on the
    /// side it falls, which is enough over a uniform backdrop and not enough over a ring band that
    /// is bright above the glyph and dark below it.</para>
    /// </summary>
    private static void DrawOutline(string texture, Vector2 position, Vector2 size, Color colour,
                                    Vector2 offset)
    {
        var shadow = new Color(Color.Black, colour.A / 255f);
        DrawIcon(texture, new Vector2(position.X - offset.X, position.Y), size, shadow);
        DrawIcon(texture, new Vector2(position.X + offset.X, position.Y), size, shadow);
        DrawIcon(texture, new Vector2(position.X, position.Y - offset.Y), size, shadow);
        DrawIcon(texture, new Vector2(position.X, position.Y + offset.Y), size, shadow);
    }

    /// <summary>One sprite, in the one convention every marker on this map is drawn with.
    /// <c>useFullClientArea: true</c> is not stylistic — see the note at the top of this class.</summary>
    private static void DrawIcon(string texture, Vector2 position, Vector2 size, Color colour)
    {
        MyGuiManager.DrawSpriteBatch(texture, position, size, colour,
            MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
            useFullClientArea: true, waitTillLoaded: false, maskTexture: null,
            rotation: 0f, rotSpeed: 0f, ignoreBounds: true);
    }

    // ---- World-space geometry ---------------------------------------------------------------

    /// <summary>
    /// The routed itinerary, as depth-tested world lines. Thickness scales with distance from the
    /// camera so a route stays visible when zoomed out to system scale instead of thinning to
    /// nothing.
    ///
    /// <para>The line ends where the route does. <see cref="MapWorld.MapFleet.StopsM"/> carries only
    /// the clearance polyline the server has actually planned, so a later leg — which has no
    /// waypoints yet and would be drawn as a chord straight through a planet — is simply not
    /// there.</para>
    /// </summary>
    private void DrawRoute(
        MapWorld.MapFleet fleet, Vector3D from, Vector3D cameraPos, double cameraDistanceM, bool selected)
    {
        Color c = Palette.RelationColour(fleet.Relation);
        var rgb = new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);

        Vector3D previous = from;
        DrawLine(previous, fleet.TargetM, rgb, cameraPos, cameraDistanceM, selected);
        previous = fleet.TargetM;
        for (int i = 0; i < fleet.StopsM.Length; i++)
        {
            DrawLine(previous, fleet.StopsM[i], rgb, cameraPos, cameraDistanceM, selected);
            previous = fleet.StopsM[i];
        }
    }

    /// <summary>
    /// Everything happening right now, as rings on the world.
    ///
    /// <para>Three sources, one beat. A raid holding its dwell is drawn on the fleet, because that
    /// is where the attacker is; a player siege is drawn on the <b>node</b>, because a player
    /// shooting a station produces no fleet at all and a fleet-anchored alarm would leave the map
    /// looking peaceful through a station being taken (FAC-258, FAC-365). A Reinforce holding the
    /// same dwell gets the same ring shape so the two read as one mechanic, and no beat, so it
    /// never competes for attention with the thing that actually needs answering.</para>
    ///
    /// <para>Every pulsing ring on the frame shares one <see cref="AlarmPulse"/> value: two alarms
    /// out of phase read as two different things.</para>
    /// </summary>
    private void DrawAlarmRings(MapWorld world, Vector3D cameraPos)
    {
        float pulse = AlarmPulse();

        for (int i = 0; i < world.Fleets.Count; i++)
        {
            MapWorld.MapFleet fleet = world.Fleets[i];
            if (fleet.IsEngaging)
            {
                DrawAlarmRing(fleet.WorldPosition(), 0.0, cameraPos, AlarmAttackColour, pulse);
            }
            else if (fleet.IsHoldingDefence)
            {
                DrawAlarmRing(fleet.WorldPosition(), 0.0, cameraPos, DefenceHoldColour, null);
            }
        }

        if (world.Alarms.Count == 0) return;
        for (int i = 0; i < world.Nodes.Count; i++)
        {
            MapWorld.MapNode node = world.Nodes[i];
            MapWorld.MapAlarm alarm = world.AlarmFor(node.Id);
            if (alarm == null) continue;
            DrawAlarmRing(node.PositionM, node.RadiusM, cameraPos, AlarmColour(alarm),
                          alarm.IsAttack ? pulse : (float?)null);
        }
    }

    /// <summary>The phase decides the colour: a shield being worn down can still be denied, a
    /// breached or already-raided station is a node about to change hands, and a zone merely off is
    /// not an attack at all.</summary>
    private static Color AlarmColour(MapWorld.MapAlarm alarm)
    {
        if (alarm.State == WireZoneState.Besieged) return AlarmAttackColour;
        if (alarm.IsAttack) return AlarmBreachColour;
        return UnprotectedColour;
    }

    /// <summary>
    /// One alarm ring. <paramref name="pulse"/> null draws it steady.
    ///
    /// <para><b>The radius has a floor in pixels, unlike the keep-out rings.</b> Those describe a
    /// volume and are culled when they get too small to be a boundary; this one exists to be seen.
    /// A node's own bubble sets the radius while the node is close enough for that to be bigger
    /// than <see cref="AlarmRingPixels"/>, and past that the ring holds its on-screen size while
    /// the marker inside it keeps shrinking.</para>
    /// </summary>
    private static void DrawAlarmRing(
        Vector3D centre, double nodeRadiusM, Vector3D cameraPos, Color colour, float? pulse)
    {
        double distance = Vector3D.Distance(cameraPos, centre);
        if (distance <= 1.0) return;

        double radius = Math.Max(nodeRadiusM * AlarmRingNodeRadii,
                                 MetresPerPixelAt(distance) * AlarmRingPixels);
        float alpha = SteadyRingAlpha;
        if (pulse.HasValue)
        {
            radius *= 1.0 + AlarmRingSwell * pulse.Value;
            alpha = AlarmAlphaFloor + AlarmAlphaSwing * pulse.Value;
        }
        DrawFacingRing(centre, radius, cameraPos, colour, alpha, AlarmRingWidthPixels);
    }

    /// <summary>0..1 on the shared heartbeat, off the wall clock. Wall clock and not the sim's:
    /// a pulse must not stretch with sim speed or freeze with a paused session.</summary>
    private static float AlarmPulse()
    {
        double seconds = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        return (float)(0.5 + 0.5 * Math.Sin(seconds * AlarmPulseRadiansPerSecond));
    }

    /// <summary>
    /// The radius inside which no new node may be created, drawn around one node as a camera-facing
    /// ring. "Stay out of the red" is the whole instruction, and this is literally the rule the
    /// server enforces on an Expand rather than a proxy for it (FAC-197).
    ///
    /// <para>A ring rather than a shaded sphere. A sphere would need depth sorting against every
    /// other one and against the planets, for a boundary whose only interesting part is its edge;
    /// hundreds of overlapping translucent shells would also sum into a fog with no readable edge
    /// anywhere. The ring is drawn in the plane facing the camera, so from any angle it outlines the
    /// sphere's silhouette exactly.</para>
    ///
    /// <para>Drawn for rivals' nodes as much as the viewer's own: a bubble is a statement about
    /// ground, not about its owner, and the ground a player most needs to know is blocked is
    /// somebody else's.</para>
    /// </summary>
    /// <summary>
    /// The keep-out zones: one ring per known node, all at the separation in force at the site the
    /// player is standing on. Outside every ring a node may be created; inside any one it may not.
    /// That demarcation is the whole job of the overlay (FAC-197).
    ///
    /// <para><b>All at ONE radius — the viewer's, not each node's.</b> The server evaluates
    /// <c>nearest &lt; SeparationAtKm(placed)</c>, so the radius that excludes you is yours, measured
    /// at your candidate site. Drawing each node at its own separation is a different and wrong
    /// picture: near a planet a 30 km orbit node still refuses a deep-space site 400 km away, while
    /// its own 30 km ring would show that ground clear. At the viewer's radius the two agree
    /// exactly — you are inside a node's ring iff that node is within your radius of you — and
    /// unlike a single sphere drawn on the player, a field of rings says where you may go rather
    /// than only whether you may stay.</para>
    ///
    /// <para>The rings resize together as the player descends toward a planet, because the rule
    /// itself relaxes: 500 km in deep space, 30 in orbit, 15 on a surface.</para>
    ///
    /// <para>Only nodes the viewer knows about can be ringed, while the server tests against all of
    /// them, so this is a floor and never the whole truth. The readout stays authoritative and says
    /// so when it refuses a site with nothing visible inside it.</para>
    /// </summary>
    private void DrawExclusionZones(MapWorld world, Vector3D cameraPos)
    {
        ExpandComponent expand = ExpandComponent.Instance;
        if (expand == null) return;

        Vector3D site;
        double radius;
        bool clear;
        if (!expand.TryGetSite(out site, out radius, out clear)) return;

        _ringCandidates.Clear();
        for (int i = 0; i < world.Nodes.Count; i++)
        {
            Vector3D centre = world.Nodes[i].PositionM;
            double distance = Vector3D.Distance(cameraPos, centre);
            if (distance <= 1.0) continue;
            if (radius / MetresPerPixelAt(distance) < MinExclusionRingRadiusPixels) continue;
            _ringCandidates.Add(new RingCandidate { CentreM = centre, Distance = distance });
        }
        _ringCandidates.Sort(CompareNearToFar);

        // Keep-out is keep-out whoever owns the ground, so one colour rather than the relation
        // palette: the node markers already carry ownership, and colouring a boundary by faction
        // would make it read as territory instead of as a rule.
        int count = Math.Min(_ringCandidates.Count, MaxExclusionRings);
        for (int i = 0; i < count; i++)
        {
            DrawFacingRing(_ringCandidates[i].CentreM, radius, cameraPos,
                ExclusionOccupiedColour, ExclusionRingAlpha, ExclusionSphereWidthPixels);
        }

        // The site itself, in the verdict's own colour. Small and bright rather than another ring at
        // the same radius, which would be indistinguishable from the keep-out field around it.
        DrawFacingRing(site, radius * SiteRingFraction, cameraPos,
            clear ? ExclusionClearColour : ExclusionOccupiedColour, ExclusionSiteAlpha,
            ExclusionSphereWidthPixels);
    }

    private static int CompareNearToFar(RingCandidate a, RingCandidate b)
    {
        return a.Distance.CompareTo(b.Distance);
    }

    /// <summary>How much of the keep-out radius the site marker spans. Small enough to read as a
    /// point rather than as another boundary, large enough to find at system scale.</summary>
    private const double SiteRingFraction = 0.06;

    /// <summary>A circle of <paramref name="radius"/> about <paramref name="centre"/> drawn in the
    /// plane facing the camera, so from any angle it outlines the sphere's true silhouette rather
    /// than a great circle seen edge-on.</summary>
    private static void DrawFacingRing(Vector3D centre, double radius, Vector3D cameraPos,
                                       Color colour, float alpha, double widthPixels)
    {
        double distance = Vector3D.Distance(cameraPos, centre);
        if (distance <= 1.0 || radius <= 0) return;

        Vector3D toCamera = (cameraPos - centre) / distance;
        Vector3D seed = Math.Abs(toCamera.Y) > 0.99 ? Vector3D.Forward : Vector3D.Up;
        Vector3D axisA = Vector3D.Normalize(Vector3D.Cross(toCamera, seed));
        DrawCircle(centre, radius, axisA, Vector3D.Cross(toCamera, axisA), colour, alpha, cameraPos,
                   widthPixels);
    }

    /// <summary>
    /// One circle of radius <paramref name="radius"/> about <paramref name="centre"/>, in the plane
    /// spanned by two unit axes.
    ///
    /// <para><b>Thickness is per segment, from that segment's own distance to the camera</b> — the
    /// same rule <see cref="DrawLine"/> applies to a route leg. One thickness for the whole circle,
    /// taken at its centre, is wrong by the ratio of the centre's distance to the ring's: a 500 km
    /// ring viewed from 30 km inside it renders every segment at a sixteenth of the width asked for,
    /// which is under the pixel grid and draws as a dashed smear rather than a line.</para>
    /// </summary>
    private static void DrawCircle(Vector3D centre, double radius, Vector3D axisA, Vector3D axisB,
                                   Color colour, float alpha, Vector3D cameraPos, double widthPixels)
    {
        var rgba = new Vector4(colour.R / 255f, colour.G / 255f, colour.B / 255f, alpha);
        Vector3D previous = centre + axisA * radius;
        for (int i = 1; i <= ExclusionSphereSegments; i++)
        {
            double angle = i * (2.0 * Math.PI / ExclusionSphereSegments);
            Vector3D next = centre + axisA * (radius * Math.Cos(angle)) + axisB * (radius * Math.Sin(angle));
            Vector3D delta = next - previous;
            double length = delta.Length();
            if (length > 1.0)
            {
                // The nearer end, matching DrawLine: a segment is then never wider than it should be
                // anywhere along itself, and the far end thins the way perspective does.
                double nearest = Math.Min(Vector3D.Distance(cameraPos, previous),
                                          Vector3D.Distance(cameraPos, next));
                MyTransparentGeometry.AddLineBillboard(
                    RouteMaterial, rgba, previous, (Vector3)(delta / length), (float)length,
                    ThicknessAt(nearest, widthPixels));
            }
            previous = next;
        }
    }

    /// <summary>World metres that project to one screen pixel at this distance, from the live FOV
    /// and viewport. The scale factor behind both route thickness and ring culling.</summary>
    private static double MetresPerPixelAt(double distanceM)
    {
        double fov = MySector.MainCamera != null ? MySector.MainCamera.FovWithZoom : 1.22;
        double viewportPx = Math.Max(MyGuiManager.GetSafeFullscreenRectangle().Height, 1);
        return 2.0 * distanceM * Math.Tan(fov * 0.5) / viewportPx;
    }

    /// <summary>
    /// One route leg, as a SINGLE billboard.
    ///
    /// This was split into up to 24 pieces so each could be sized for its own distance, and that
    /// produced dashes instead of a line. A polyline billboard is a parallelogram:
    /// <c>MyUtils.GetPolyLineQuad</c> derives its camera-facing offset from Point0 alone and applies
    /// that same offset to both ends. Neighbouring pieces therefore disagree at every joint -- in
    /// facing, in width (each sized for its own distance) and in alpha -- and two dozen such joints
    /// read as a dashed line. Overlapping them would not help either: two translucent quads over one
    /// another are brighter than one, so the dashes would just become beads.
    ///
    /// One piece, sized by the leg's NEAREST end. Midpoint sizing was the original slab bug; the
    /// near end means the leg is never wider than it should be anywhere along its length, and the
    /// far end thins on its own, which is what perspective on a fixed-width ribbon does for free.
    /// </summary>
    private static void DrawLine(
        Vector3D from, Vector3D to, Vector3 rgb, Vector3D cameraPos, double cameraDistanceM, bool selected)
    {
        Vector3D delta = to - from;
        double length = delta.Length();
        if (length <= 1.0) return;
        Vector3D direction = delta / length;

        double widthPixels = selected ? SelectedRouteWidthPixels : RouteWidthPixels;
        double nearest = Math.Min(
            Vector3D.Distance(cameraPos, from), Vector3D.Distance(cameraPos, to));
        double representative = Vector3D.Distance(cameraPos, (from + to) * 0.5);

        // The selection is what the player asked to trace, so it does not fade -- following a route
        // to its destination is the whole reason to select a convoy.
        float alpha = selected ? SelectedRouteAlpha : FadeAlphaAt(representative, cameraDistanceM);
        if (!selected && alpha < RouteCullAlpha) return;

        MyTransparentGeometry.AddLineBillboard(
            RouteMaterial, new Vector4(rgb.X, rgb.Y, rgb.Z, alpha), from, (Vector3)direction,
            (float)length, ThicknessAt(nearest, widthPixels));
    }

    /// <summary>
    /// Fades a route out with distance, measured against the camera's own orbit distance rather
    /// than an absolute range. That keeps the effect identical at every zoom: whatever the player
    /// has framed reads clearly, and whatever is well behind it recedes.
    /// </summary>
    private static float FadeAlphaAt(double distanceM, double cameraDistanceM)
    {
        return RouteNearAlpha * Falloff(distanceM, cameraDistanceM, MinRouteAlphaFactor);
    }

    /// <summary>World thickness that lands on <paramref name="widthPixels"/> on screen at this
    /// distance, derived from the live FOV and viewport rather than a tuned constant.</summary>
    private static float ThicknessAt(double distanceM, double widthPixels)
    {
        double metresPerPixel = MetresPerPixelAt(distanceM);
        // HALF-width, not width: MyUtils.GetPolyLineQuad builds the quad as Point +/- Thickness,
        // so the drawn line is twice this. Without the halving every route renders at double the
        // width asked for, which reads as "scaling is right but everything is too heavy".
        if (widthPixels < MinRouteWidthPixels) widthPixels = MinRouteWidthPixels;
        double thickness = widthPixels * metresPerPixel * 0.5;
        if (thickness < MinRouteThicknessM) thickness = MinRouteThicknessM;
        if (thickness > MaxRouteThicknessM) thickness = MaxRouteThicknessM;
        return (float)thickness;
    }

    // ---- Occlusion ---------------------------------------------------------------------------

    private void RefreshPlanets()
    {
        _planets.Clear();
        try
        {
            List<MyPlanet> planets = MyPlanets.GetPlanets();
            if (planets != null) _planets.AddRange(planets);
        }
        catch (Exception ex)
        {
            Plugin.Log("map planet enumeration failed: " + ex);
        }
    }

    /// <summary>
    /// Whether a planet stands between the camera and a marker. Without this, markers on the far
    /// side of a body float on top of it and the map reads as flat -- the one place where drawing
    /// GUI sprites over a real 3D scene needs help.
    /// </summary>
    private bool IsOccludedByPlanet(Vector3D from, Vector3D to, long ignoreEntityId = 0L)
    {
        Vector3D delta = to - from;
        double length = delta.Length();
        if (length <= 0.0) return false;
        Vector3D direction = delta / length;

        for (int i = 0; i < _planets.Count; i++)
        {
            MyPlanet planet = _planets[i];
            if (planet == null || planet.MarkedForClose) continue;
            if (planet.EntityId == ignoreEntityId) continue;

            Vector3D centre = planet.PositionComp.GetPosition();
            double radius = planet.AverageRadius;

            Vector3D m = from - centre;
            double c = m.LengthSquared() - radius * radius;
            if (c < 0.0) continue;                       // camera inside the body: do not occlude

            double b = Vector3D.Dot(m, direction);
            if (b > 0.0) continue;                       // sphere is behind the camera

            double discriminant = b * b - c;
            if (discriminant < 0.0) continue;            // ray misses

            double hit = -b - Math.Sqrt(discriminant);
            if (hit > 0.0 && hit < length) return true;  // hit before reaching the marker
        }
        return false;
    }

    // ---- Bodies -------------------------------------------------------------------------------

    /// <summary>
    /// A ring and a name on every planet and moon, at its centre.
    ///
    /// <para>Deliberately not routed through <see cref="Collect"/>: a body is the one marker whose
    /// occlusion test has to ignore the body it belongs to. Every other marker is a point in space
    /// that a planet can stand in front of; a planet's own centre is always behind its own surface,
    /// so the shared test culls it every single frame.</para>
    ///
    /// <para>A camera inside the sphere gets no marker at all. Standing on a world, its centre is
    /// under the player's feet, and a name floating in the ground is not a locator for something
    /// they are already on.</para>
    /// </summary>
    private void CollectBodies(Vector3D cameraPos, ref MatrixD view, ref MatrixD projection)
    {
        for (int i = 0; i < _planets.Count; i++)
        {
            MyPlanet planet = _planets[i];
            if (planet == null || planet.MarkedForClose) continue;

            Vector3D centre = planet.PositionComp.GetPosition();
            double radius = planet.AverageRadius;
            double distance = Vector3D.Distance(centre, cameraPos);
            if (distance <= radius) continue;

            Vector2 normalized;
            bool behind;
            if (!Projection.Project(centre, ref view, ref projection, out normalized, out behind)) continue;
            if (behind || normalized.X < 0f || normalized.X > 1f
                || normalized.Y < 0f || normalized.Y > 1f) continue;

            // Another body in the way still hides this one; its own surface does not.
            if (IsOccludedByPlanet(cameraPos, centre, planet.EntityId)) continue;

            _drawables.Add(new Drawable
            {
                Id = PlanetId(planet),
                Normalized = normalized,
                Distance = distance,
                IconPixels = BodyIconPixels,
                Colour = BodyColour,
                Texture = BodyTexture,
                Label = PlanetName(planet),
                LabelCandidate = false,
                IsBody = true,
            });
        }
    }

    // ---- Planet picking -----------------------------------------------------------------------

    /// <summary>
    /// The nearest planet the cursor ray enters, or false. Planets are the one thing on this map
    /// with no marker of its own -- they are the real rendered world -- so they are picked against
    /// their actual spheres rather than against a projected icon, which is also what keeps a body
    /// filling the screen clickable anywhere on it.
    ///
    /// <para>A camera already inside the sphere is not a hit: at that point the player is looking
    /// at the surface, not at a body they could ask to be taken to.</para>
    /// </summary>
    public bool TryPickPlanet(Vector3D from, Vector3D direction, out PlanetPick pick)
    {
        pick = default(PlanetPick);
        double nearest = double.MaxValue;
        for (int i = 0; i < _planets.Count; i++)
        {
            MyPlanet planet = _planets[i];
            if (planet == null || planet.MarkedForClose) continue;

            Vector3D centre = planet.PositionComp.GetPosition();
            double radius = planet.AverageRadius;

            Vector3D m = from - centre;
            double c = m.LengthSquared() - radius * radius;
            if (c < 0.0) continue;                       // camera inside the body

            double b = Vector3D.Dot(m, direction);
            if (b > 0.0) continue;                       // sphere is behind the camera

            double discriminant = b * b - c;
            if (discriminant < 0.0) continue;            // ray misses

            double hit = -b - Math.Sqrt(discriminant);
            if (hit <= 0.0 || hit >= nearest) continue;
            nearest = hit;
            pick = Describe(planet, centre, radius);
        }
        return nearest < double.MaxValue;
    }

    /// <summary>The planet behind a selection id, or false once it is gone -- the selection outlives
    /// nothing else on this map either.</summary>
    public bool TryGetPlanet(string id, out PlanetPick pick)
    {
        pick = default(PlanetPick);
        if (string.IsNullOrEmpty(id) || !IsPlanetId(id)) return false;
        for (int i = 0; i < _planets.Count; i++)
        {
            MyPlanet planet = _planets[i];
            if (planet == null || planet.MarkedForClose) continue;
            if (PlanetId(planet) != id) continue;
            pick = Describe(planet, planet.PositionComp.GetPosition(), planet.AverageRadius);
            return true;
        }
        return false;
    }

    public static bool IsPlanetId(string id)
    {
        return id != null && id.StartsWith(PlanetIdPrefix, StringComparison.Ordinal);
    }

    private static PlanetPick Describe(MyPlanet planet, Vector3D centre, double radius)
    {
        return new PlanetPick
        {
            Id = PlanetId(planet),
            Name = PlanetName(planet),
            CentreM = centre,
            RadiusM = radius,
        };
    }

    private static string PlanetId(MyPlanet planet)
    {
        return PlanetIdPrefix + planet.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>What to call a body. The entity name is what the world's author gave it and what a
    /// player's GPS says; the generator subtype is the fallback, since an unnamed body still has a
    /// kind.</summary>
    private static string PlanetName(MyPlanet planet)
    {
        if (!string.IsNullOrEmpty(planet.DisplayName)) return planet.DisplayName;
        if (planet.Generator != null && !string.IsNullOrEmpty(planet.Generator.Id.SubtypeName))
            return planet.Generator.Id.SubtypeName;
        return "Planet";
    }

    // ---- Own position -------------------------------------------------------------------------

    private void DrawOwnPosition(Vector3D cameraPos, ref MatrixD view, ref MatrixD projection)
    {
        Vector3D self = MapComponent.LocalPlayerPosition();
        if (self == Vector3D.Zero) return;
        Collect(self, cameraPos, ref view, ref projection,
            IconPixels, new Color(120, 255, 140), SelfTexture, "You", SelfId, true);
    }

    /// <summary>Full size at or inside the focus distance, shrinking in proportion beyond it, with
    /// a floor so a far node stays a visible dot rather than disappearing.</summary>
    private static float MarkerScale(double distanceM, double cameraDistanceM)
    {
        return Falloff(distanceM, cameraDistanceM, MinMarkerScale);
    }

    /// <summary>Full strength at or inside the focus distance, falling off in proportion beyond it,
    /// floored at <paramref name="floor"/>. Shared by marker size and route alpha so the two recede
    /// together rather than on two different curves.</summary>
    private static float Falloff(double distanceM, double cameraDistanceM, float floor)
    {
        if (cameraDistanceM <= 0 || distanceM <= 0) return 1f;
        var f = (float)(cameraDistanceM / distanceM);
        if (f > 1f) return 1f;
        return f < floor ? floor : f;
    }

    private static int CompareFarToNear(Drawable a, Drawable b)
    {
        return b.Distance.CompareTo(a.Distance);
    }

    // ---- Presentation tables ------------------------------------------------------------------
    // Placeholder art, same convention as the scanner: stock SE textures tinted per relation, so
    // bespoke icons later are a change to this table and nothing else.

    internal const string SelfId = "__self";

    /// <summary>Selection ids are one namespace across nodes, fleets, the player and now bodies,
    /// so a planet's carries a prefix a node id cannot collide with.</summary>
    internal const string PlanetIdPrefix = "planet:";

    private const string SelfTexture = "Textures\\HUD\\marker_self.dds";

    /// <summary>
    /// The fallback fleet marker: what draws when this client could not write its own icon art to
    /// disk. Stock SE, so it cannot fail for the same reason the art did.
    ///
    /// <para>It is an exclamation triangle, which is the wrong thing to say about a convoy — every
    /// fleet on the map is not an alert — and that is what <see cref="FleetIcons"/> replaces. It is
    /// kept only because a map of invisible markers is worse than a map of wrong ones.</para>
    /// </summary>
    private const string FleetMarkerFallback = "Textures\\HUD\\marker_alert.dds";
    /// <summary>
    /// The ring drawn around a selected marker, and around one blocking a candidate site.
    ///
    /// <para><b>A dashed reticle, because the hairline circle could not be drawn at this size.</b>
    /// <c>RadialOuterCircle.dds</c> is 1024 px square carrying a stroke about 6.3 px wide — area over
    /// circumference — which is 0.018 of its texels. Drawn at 1.9x an 18 px marker it lands at
    /// <b>0.21 px</b>, so the rasteriser samples a fraction of a pixel and the ring resolves as a
    /// ghost at roughly a fifth strength. That is not a colour or alpha problem and no alpha fixes
    /// it: the stroke scales with the sprite, and a ring thick enough to read would have to be drawn
    /// some 370 px across.</para>
    ///
    /// <para><c>TargetingPredictionMarker.dds</c> is 128 px carrying an 8.6 px dashed stroke, so the
    /// same 34 px sprite lands at <b>2.3 px</b> — above the pixel grid, which is the floor
    /// <see cref="MinRouteWidthPixels"/> records for the line billboards on the same grounds. Dashed
    /// also keeps it distinct from the solid keep-out circles <see cref="DrawExclusionZones"/> draws
    /// in world space, which are a different statement (FAC-367).</para>
    /// </summary>
    private const string SelectionTexture = "Textures\\GUI\\TargetingPredictionMarker.dds";

    /// <summary>
    /// The same dashed circle <see cref="SelectionTexture"/> draws, at icon size and in
    /// <see cref="BodyColour"/>. Hollow, so it reads as an outline around something rather than as a
    /// contact, which is what a body is — the seven <c>marker_*</c> HUD sprites are all filled
    /// contact glyphs.
    ///
    /// <para><b>A GUI sprite here has to satisfy two properties, and SE ships exactly one closed
    /// hollow circle that does.</b> The first is stroke width: <c>RadialOuterCircle.dds</c> carries
    /// about 6.3 px in a 1024 px source, so at 26 px it lands at <b>0.16 px</b> and resolves as a
    /// ghost — see the note on <see cref="SelectionTexture"/>. The second is the one that cost a
    /// live round trip. <c>GravityHudGlobe.dds</c> cleared the first (7.2 px in 128, so 1.46 px at
    /// 26) and failed the second: it is the only texture this map draws that is <b>legacy DXT5</b>
    /// rather than <b>BC7/DX10</b>, and the only one whose fully transparent texels carry a colour
    /// — <c>(178,201,214)</c> instead of black. It drew as a flat opaque rectangle. The arithmetic
    /// is exact: <c>(178,201,214) x BodyColour(170,195,215) / 255 = (118,153,180)</c>, which is the
    /// pixel measured off the operator's screenshot, so the whole quad drew at the surround's own
    /// colour (FAC-369).</para>
    ///
    /// <para><b>Check both before changing this string.</b> Every SE texture the map draws is
    /// BC7/DX10 with black transparent texels; all 51 square textures under <c>Textures\\GUI</c> and
    /// <c>Textures\\HUD</c> and all 361 under <c>GUI\\Controls</c> were measured, and
    /// <c>TargetingPredictionMarker.dds</c> is the only closed hollow circle among them that passes.
    /// Sharing it with the selection was the cost of that, and colour, size (26 px against 34) and
    /// transience carry the difference: a body's ring is smaller, permanent, pale and empty, while a
    /// selection ring is larger, yellow, and encircles a marker only while it is picked.</para>
    ///
    /// <para><b>The format half of that rule is a correlate, not the requirement</b>, and the
    /// distinction matters now that the fleet markers are our own PNGs rather than SE's DDS. What
    /// the sprite pass actually needs is <b>premultiplied</b> alpha — it draws with
    /// <c>premultipliedAlpha: true</c>, which selects the shader compiled WITHOUT
    /// <c>PREMULTIPLY_ALPHA</c>, so a straight-alpha texture composites its surround at full
    /// strength. Black transparent texels are what premultiplication produces, which is why every
    /// texture that works has them; BC7/DX10 merely came along with them across SE's own art. See
    /// <see cref="IconStore"/>, which premultiplies by construction. When choosing an SE texture the
    /// measured rule above is still the cheap way to predict this.</para>
    /// </summary>
    private const string BodyTexture = "Textures\\GUI\\TargetingPredictionMarker.dds";

    /// <summary>
    /// The marker for a convoy: its mission, as a silhouette.
    ///
    /// <para><b>The glyph does not change while the fleet is fighting, and it used to.</b> An
    /// engaging raid swapped to <c>marker_enemy.dds</c>, which was the only way to say "fighting"
    /// when every fleet drew the same exclamation triangle. It is now the one thing that must not
    /// happen: the mission is exactly what a player needs while a fight is running, and a swap
    /// throws it away at that moment. <see cref="DrawAlarmRings"/> already says "fighting" — a
    /// pulsing ring on the fleet for an engagement, a steady one for a defensive hold — which is
    /// also how the web dashboard says it, over an unchanged marker.</para>
    /// </summary>
    private static string FleetTexture(MapWorld.MapFleet fleet)
    {
        if (fleet.IsGrid) return ScannerComponent.GridTexture;
        string art = IconStore.Texture(FleetIcons.TextureName(FleetIcons.GlyphFor(fleet.Kind)));
        return art ?? FleetMarkerFallback;
    }

    /// <summary>Pips over a node marker for its scanned defence: the band less one, none when it has
    /// not been read. Drawn with the fleet strength marks' art and placement, so strength reads the
    /// same way on both.</summary>
    private static int DefencePips(MapWorld.MapNode node)
    {
        return node.Defence == (byte)WireDefenceBand.Unknown ? 0 : node.Defence - 1;
    }

    /// <summary>The ore glyph drawn dark on a filled stage shell.</summary>
    private static readonly Color GlyphOnShell = new Color(12, 12, 16);

    private struct NodeIcon
    {
        public string Glyph;
        public string Shell;
        public float GlyphScale;
        public float GlyphOffset;
        public bool GlyphDark;
    }

    /// <summary>
    /// The node marker (docs/fog-of-war.md → Economic stage): the ore glyph, with the stage built
    /// around it. Field is the bare glyph; Outpost puts it on a platform; Settlement, Hub and
    /// Capital put it inside a station shell that grows with the stage. Filled once identified,
    /// an outline below that. Below Class there is no stage and no ore to draw, so the marker is the
    /// plain contact blip. Missing art falls back to the bare glyph, as the map drew before stages.
    /// </summary>
    private static NodeIcon NodeIconFor(MapWorld.MapNode node)
    {
        var icon = new NodeIcon { Glyph = NodeTexture(node), GlyphScale = 1f };
        if (!node.Classed)
        {
            icon.Glyph = IconStore.Texture(FleetIcons.TextureName(FleetIcons.Glyph.Generic)) ?? icon.Glyph;
            return icon;
        }
        string name;
        float scale;
        switch ((WireNodeStage)node.Stage)
        {
            case WireNodeStage.Outpost: name = "stage_outpost"; scale = 0.62f; break;
            case WireNodeStage.Settlement: name = "stage_settlement"; scale = 0.52f; break;
            case WireNodeStage.Hub: name = "stage_hub"; scale = 0.42f; break;
            case WireNodeStage.Capital: name = "stage_capital"; scale = 0.34f; break;
            default: return icon;
        }
        string shell = IconStore.Texture(node.Identified ? name : name + "_outline");
        if (shell == null) return icon;
        icon.Shell = shell;
        icon.GlyphScale = scale;
        bool outpost = node.Stage == (byte)WireNodeStage.Outpost;
        // The platform sits under the glyph, so the glyph rides above centre and keeps its colour.
        icon.GlyphOffset = outpost ? 0.14f : 0f;
        icon.GlyphDark = node.Identified && !outpost;
        return icon;
    }

    private static string NodeTexture(MapWorld.MapNode node)
    {
        // Ore first, because the ore is why a player flies there. Shared with every screen that
        // names a node so the marker and the label cannot disagree about what it is.
        return NodeLabels.Texture(node.DominantOre, node.Surveyed, node.CapabilityMask, node.HasStation);
    }

    private struct Drawable
    {
        public string Id;
        public Vector2 Normalized;
        public double Distance;
        public float IconPixels;
        public Color Colour;
        public string Texture;
        public string Label;
        public bool LabelCandidate;
        /// <summary>This node is inside the exclusion sphere, i.e. it is one of the nodes actually
        /// refusing the site. Only ever set for nodes, never fleets.</summary>
        public bool Blocking;
        /// <summary>A planet or moon. Bodies do not recede with distance the way nodes do and are
        /// always labelled -- see the note on BodyIconPixels.</summary>
        public bool IsBody;
        /// <summary>The strength mark to draw above this marker, or null for none. Only ever set
        /// for fleets -- a node has no hull count.</summary>
        public string EchelonTexture;
        /// <summary>How many times to repeat <see cref="EchelonTexture"/>.</summary>
        public int EchelonCount;
        /// <summary>A node's stage shell, drawn under <see cref="Texture"/>; null for none.</summary>
        public string ShellTexture;
        /// <summary>The glyph's size against the marker when a shell is drawn.</summary>
        public float GlyphScale;
        /// <summary>How far above centre the glyph sits, as a fraction of the marker.</summary>
        public float GlyphOffset;
        /// <summary>Draw the glyph dark, on a filled shell.</summary>
        public bool GlyphDark;
    }

    /// <summary>A planet the cursor found. Carries its own radius: what frames a body is its size,
    /// not a standoff picked for stations.</summary>
    public struct PlanetPick
    {
        public string Id;
        public string Name;
        public Vector3D CentreM;
        public double RadiusM;
    }

    /// <summary>A marker as it landed on screen this frame, for click hit-testing. Screen pixels,
    /// deliberately -- see the note at the call site.</summary>
    public struct PickTarget
    {
        public string Id;
        public Vector2 ScreenPx;
        public float RadiusPx;
        public double Distance;
    }
}
