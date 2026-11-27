using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using FactionsUI.Campaigns;
using FactionsUI.Nodes;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.ModAPI.Interfaces;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Map;

/// <summary>
/// The map window.
///
/// It paints no background. The world is still being rendered behind it — SE keeps drawing the
/// gameplay screen whatever has focus — so pointing the game camera at the map's orbit position
/// makes the map show real planets, real atmospheres and real light, and this screen only has to
/// draw markers on top.
///
/// Three things are borrowed on open and MUST be given back on every close path: the camera
/// controller, the camera's far planes, and the HUD. Restoring is done in <see cref="OnRemoved"/>
/// rather than only in the close handler, because that is the one hook every path goes through.
/// </summary>
internal sealed class MapScreen : MyGuiScreenBase
{
    /// <summary>
    /// SE clips the world at 20 km (far) / 1 000 km (far-far) by default, far short of a
    /// system-scale view. This covers the camera at full pull-out plus the world's diameter beyond
    /// it, and no more: see MapCameraController.MaxDistanceM for why the tight value matters.
    /// </summary>
    private const float MapFarPlaneM = 1.0e8f;   // 100 000 km

    private const double OrbitRadiansPerPixel = 0.006;
    private const double PanPixelsPerRadian = 900.0;
    private const double DoubleClickSeconds = 0.35;

    /// <summary>Standoff used when framing a selection, as a multiple of nothing in particular —
    /// it is simply a distance at which a station and its neighbours are both legible.</summary>
    private const double FrameDistanceM = 30_000.0;

    /// <summary>Standoff used when framing a planet, as a multiple of its own radius. A body is
    /// framed by its size, not by the distance that suits a station: SE's vertical field of view is
    /// 70 degrees by default, so a sphere just fits the screen at about 1.7 radii and this leaves
    /// it some sky around it. Earthlike's 60 km becomes a 150 km standoff, a moon's 9 km becomes
    /// 22 km.</summary>
    private const double PlanetFrameRadii = 2.5;

    /// <summary>Total travel below which a press counts as a click rather than a drag, in screen
    /// pixels accumulated over the whole gesture.</summary>
    private const float ClickSlopPixels = 5f;

    /// <summary>Enough for identity, what is happening here, and range. The panel grows UPWARD
    /// from a fixed bottom edge: the button row beneath it already ends at y 0.470 against a screen
    /// that stops at 0.5, so a line added below would draw off the edge.</summary>
    private const int SelectionLineCount = 8;
    private const float SelectionLineHeight = 0.028f;

    /// <summary>Where the selection block ends and the button row begins. Fixed, so adding an event
    /// line moves the text up rather than moving the buttons down.</summary>
    private const float SelectionBottom = 0.434f;

    /// <summary>The GPS button's slot: after Conquer for a node, at the row's start for a planet,
    /// which has none of the node buttons before it.</summary>
    private const float GpsNodeX = -0.17f;
    private const float GpsPlanetX = -0.62f;

    /// <summary>How close a fleet's target has to be to a node's position to be counted as a fight
    /// AT that node. Both are the same Core value in km scaled to metres, so the match is exact and
    /// this is slack for the conversion, not a proximity search: no second node is ever within
    /// 100 m of the first.</summary>
    private const double NodeMatchMetresSq = 100.0 * 100.0;

    internal static MapScreen Instance { get; private set; }

    private readonly MapRenderer _renderer = new MapRenderer();
    private readonly MapCameraController _camera = new MapCameraController();

    private IMyCameraController _previousCamera;
    private float _previousFarPlane;
    private float _previousFarFarPlane;
    private bool _previousMinimalHud;
    private bool _borrowed;

    private MyGuiControlLabel _statusLabel;
    /// <summary>MyGuiControlLabel is single-line, so the selection panel is a stack of them rather
    /// than one label with newlines in it.</summary>
    private MyGuiControlLabel[] _selectionLines;
    /// <summary>Opens the selected node in <see cref="NodeDetailScreen"/>. The panel above it stays
    /// identity-only; this is the way through to everything it deliberately does not carry.</summary>
    private MyGuiControlButton _openNode;
    /// <summary>Place a raid or conquest directive on the selected node, or cancel the one already
    /// on it. The map is where a target is, so this is where the order is given; the campaigns
    /// window lists what stands.</summary>
    private MyGuiControlButton _raid;
    private MyGuiControlButton _conquer;
    private MyGuiControlButton _defend;
    /// <summary>Adds the selected node or planet to the player's own GPS list, so the map is somewhere
    /// a route can start from and not only somewhere one is looked at.</summary>
    private MyGuiControlButton _gps;
    /// <summary>The selection a GPS was last added for; the button reads "Added" while it stays
    /// selected. The map runs under a minimal HUD, which is where a notification would have
    /// gone.</summary>
    private string _gpsAddedFor;

    private string _selectedId;
    private Vector2 _dragAnchor;
    private bool _orbiting;
    private bool _panning;
    /// <summary>Distance travelled across the whole press, not since the last frame. Comparing the
    /// last frame's delta made any drag that paused before release read as a click, so moving the
    /// camera and letting go cleared the selection.</summary>
    private float _dragTravelPx;
    private double _lastClickAt;
    /// <summary>Wall clock between frames, for the reframe glide. Draw runs at frame rate, not at
    /// the sim tick, so the step has to be measured rather than assumed.</summary>
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();

    public MapScreen()
        : base(new Vector2(0.5f, 0.5f), null, null, isTopMostScreen: false)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = true;
        CloseButtonEnabled = false;
        EnabledBackgroundFade = false;
        RecreateControls(constructor: true);
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.MapScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        Borrow();
        MapComponent component = MapComponent.Instance;
        if (component != null)
        {
            component.SetStreaming(true);
            _camera.FrameOn(MapComponent.LocalPlayerPosition(), FrameDistanceM);
        }
    }

    public override void OnRemoved()
    {
        Restore();
        MapComponent component = MapComponent.Instance;
        if (component != null) component.SetStreaming(false);
        // The exclusion sphere is the map's only reason to hold the status stream; the HUD readout
        // keeps its own, so dropping this one never closes a readout the player still has open.
        ExpandComponent expand = ExpandComponent.Instance;
        if (expand != null) expand.SetWant(ExpandComponent.WantMap, false);
        CampaignsComponent campaigns = CampaignsComponent.Instance;
        if (campaigns != null) campaigns.SetWant(CampaignsComponent.WantMap, false);
        Events.EventsComponent events = Events.EventsComponent.Instance;
        if (events != null) events.SetWant(Events.EventsComponent.WantMap, false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Borrowed global state ----------------------------------------------------------------

    private void Borrow()
    {
        if (_borrowed) return;
        try
        {
            if (MySession.Static != null)
            {
                _previousCamera = MySession.Static.CameraController;
                MySession.Static.CameraController = _camera;
            }
            if (MySector.MainCamera != null)
            {
                _previousFarPlane = MySector.MainCamera.FarPlaneDistance;
                _previousFarFarPlane = MySector.MainCamera.FarFarPlaneDistance;
                MySector.MainCamera.FarPlaneDistance = MapFarPlaneM;
                MySector.MainCamera.FarFarPlaneDistance = MapFarPlaneM;
            }
            _previousMinimalHud = MyHud.MinimalHud;
            MyHud.MinimalHud = true;
            _borrowed = true;
        }
        catch (Exception ex)
        {
            Plugin.Log("map open failed while taking over the camera: " + ex);
            // Anything already taken must go back, or the player is left in a detached camera.
            Restore();
        }
    }

    private void Restore()
    {
        if (!_borrowed) return;
        _borrowed = false;
        try
        {
            if (MySession.Static != null && MySession.Static.CameraController == _camera)
            {
                MySession.Static.CameraController = _previousCamera;
            }
            if (MySector.MainCamera != null)
            {
                MySector.MainCamera.FarPlaneDistance = _previousFarPlane;
                MySector.MainCamera.FarFarPlaneDistance = _previousFarFarPlane;
            }
            MyHud.MinimalHud = _previousMinimalHud;
        }
        catch (Exception ex)
        {
            Plugin.Log("map close failed while restoring the camera: " + ex);
        }
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);

        _statusLabel = new MyGuiControlLabel(
            position: new Vector2(-0.62f, -0.42f),
            text: "",
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_statusLabel);

        _selectionLines = new MyGuiControlLabel[SelectionLineCount];
        for (int i = 0; i < _selectionLines.Length; i++)
        {
            _selectionLines[i] = new MyGuiControlLabel(
                position: new Vector2(
                    -0.62f, SelectionBottom - 0.01f - (SelectionLineCount - i) * SelectionLineHeight),
                text: "",
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
            Controls.Add(_selectionLines[i]);
        }

        _openNode = new MyGuiControlButton(
            position: new Vector2(-0.62f, SelectionBottom),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.2f, 0.036f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Open this station's own window.",
            text: new StringBuilder("Open station"),
            textScale: 0.7f,
            onButtonClick: _ => OpenSelectedNode())
        {
            Visible = false,
        };
        Controls.Add(_openNode);

        // Same row as Open station, to its right: that row ends at y 0.470 and a row beneath it
        // clips at the screen's bottom edge (0.5), which is where the first version put these.
        float campaignTop = SelectionBottom;
        _raid = new MyGuiControlButton(
            position: new Vector2(-0.41f, campaignTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.11f, 0.036f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Order your faction to raid this station: strikes for loot and to wear its hold down.",
            text: new StringBuilder("Raid"),
            textScale: 0.7f,
            onButtonClick: _ => PlaceOrCancel(0))
        {
            Visible = false,
        };
        Controls.Add(_raid);
        _conquer = new MyGuiControlButton(
            position: new Vector2(-0.29f, campaignTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.11f, 0.036f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Order your faction to take this station: raids until its hold is low, then a Conquer fleet.",
            text: new StringBuilder("Conquer"),
            textScale: 0.7f,
            onButtonClick: _ => PlaceOrCancel(1))
        {
            Visible = false,
        };
        Controls.Add(_conquer);
        // The defensive order takes the Raid slot: the two are never offered together, since one
        // needs the node to be somebody else's and the other needs it to be ours.
        _defend = new MyGuiControlButton(
            position: new Vector2(-0.41f, campaignTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.11f, 0.036f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Order your faction to keep a patrol circuit around this station and your stations near it.",
            text: new StringBuilder("Defend"),
            textScale: 0.7f,
            onButtonClick: _ => PlaceOrCancel(2))
        {
            Visible = false,
        };
        Controls.Add(_defend);

        _gps = new MyGuiControlButton(
            position: new Vector2(GpsNodeX, campaignTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.08f, 0.036f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Add a GPS marker for this location to your GPS list.",
            text: new StringBuilder("GPS"),
            textScale: 0.7f,
            onButtonClick: _ => AddSelectionGps())
        {
            Visible = false,
        };
        Controls.Add(_gps);
    }

    /// <summary>The selected node as the map knows it, or null for a fleet, the self marker or
    /// nothing.</summary>
    private MapWorld.MapNode SelectedNode(MapComponent component)
    {
        if (component == null || _selectedId == null || _selectedId == MapRenderer.SelfId) return null;
        for (int i = 0; i < component.World.Nodes.Count; i++)
        {
            if (component.World.Nodes[i].Id == _selectedId) return component.World.Nodes[i];
        }
        return null;
    }

    /// <summary>Places a directive on the selected node after a confirm that names the cost, or
    /// cancels the one already on it. <paramref name="kind"/> is the wire's: 0 raid, 1 conquest,
    /// 2 defend. Everything is re-checked server-side; the confirm exists so a mis-click cannot
    /// start a war.</summary>
    private void PlaceOrCancel(byte kind)
    {
        MapComponent map = MapComponent.Instance;
        CampaignsComponent campaigns = CampaignsComponent.Instance;
        MapWorld.MapNode node = SelectedNode(map);
        if (campaigns == null || node == null) return;

        WireCampaign standing;
        if (campaigns.TryGetDirectiveOn(node.Id, out standing))
        {
            campaigns.SendCancel(standing.FocusId);
            return;
        }

        string name = string.IsNullOrEmpty(node.DisplayName) ? node.Id : node.DisplayName + " (" + node.Id + ")";
        string nodeId = node.Id;
        if (kind == 2)
        {
            string defendBody = "Defend " + name + "?\n\n"
                                + "Your faction will keep a patrol circuit over this station and your "
                                + "stations near it, flown from wherever it has combat hulls, at the "
                                + "faction's patrol cost per circuit. A patrol that finds a station "
                                + "under threat stays to defend it.\n\nThe order stands for six hours "
                                + "unless you cancel it.";
            Ships.TerminalShipAssetsComponent.ConfirmYesNo("Defend " + node.Id, defendBody,
                () => campaigns.SendPlace(nodeId, 2));
            return;
        }
        bool conquest = kind == 1;
        string what = conquest ? "Conquer" : "Raid";
        long cost = conquest ? campaigns.ConquerCost : campaigns.RaidCost;
        string owner = string.IsNullOrEmpty(node.OwnerTag) ? "nobody" : "[" + node.OwnerTag + "]";
        string body = what + " " + name + ", held by " + owner + "?\n\n"
                      + "Your faction's combat hulls will be sent against it whenever it can field "
                      + "them and the treasury covers " + cost + " per strike"
                      + (conquest ? ", and a Conquer fleet once its hold is low enough." : ".")
                      + "\n\nThis is an act of war against its owner.";
        Ships.TerminalShipAssetsComponent.ConfirmYesNo(what + " " + node.Id, body,
            () => campaigns.SendPlace(nodeId, kind));
    }

    private bool IsNodeSelected(MapComponent component)
    {
        if (_selectedId == null || _selectedId == MapRenderer.SelfId) return false;
        for (int i = 0; i < component.World.Nodes.Count; i++)
        {
            if (component.World.Nodes[i].Id == _selectedId) return true;
        }
        return false;
    }

    /// <summary>A node's GPS sits at the node, a planet's at its centre, as SE's own planet markers
    /// do. Sent through the same request the terminal's GPS panel uses, so the server persists it
    /// under the player's identity like any GPS they made by hand; an identical name and position
    /// is refused by SE's own hash, so a second click adds nothing.</summary>
    private void AddSelectionGps()
    {
        if (MySession.Static == null || MySession.Static.Gpss == null) return;
        long identityId = MySession.Static.LocalPlayerId;
        if (identityId == 0L) return;

        string name;
        string description;
        Vector3D coords;
        MapRenderer.PlanetPick planet;
        MapWorld.MapNode node = SelectedNode(MapComponent.Instance);
        if (node != null)
        {
            // The disambiguated label where there is one: 166 station names are shared, and two
            // identical entries in a GPS list cannot be told apart.
            name = node.LabelText ?? node.DisplayName;
            description = "Factions node " + node.Id;
            coords = node.PositionM;
        }
        else if (_renderer.TryGetPlanet(_selectedId, out planet))
        {
            name = planet.Name;
            description = "Planet";
            coords = planet.CentreM;
        }
        else
        {
            return;
        }

        var gps = new MyGps
        {
            Name = name,
            DisplayName = name,
            Description = description,
            Coords = coords,
            ShowOnHud = true,
            AlwaysVisible = false,
            DiscardAt = null,
        };
        gps.UpdateHash();
        MySession.Static.Gpss.SendAddGpsRequest(identityId, ref gps);
        _gpsAddedFor = _selectedId;
    }

    /// <summary>Nodes only. The map also selects fleets and the player's own marker, and neither is
    /// a node the server can be asked about.</summary>
    private void OpenSelectedNode()
    {
        if (string.IsNullOrEmpty(_selectedId) || _selectedId == MapRenderer.SelfId) return;
        NodeDetailScreen.Open(_selectedId);
    }

    // ---- Frame --------------------------------------------------------------------------------

    public override bool Draw()
    {
        MapComponent component = MapComponent.Instance;
        if (component != null)
        {
            // Retried every frame rather than only on open: a map opened before the uplink was
            // granted would otherwise stay blank until the player closed and reopened it.
            component.SetStreaming(true);
            // Asked for every frame, like SetStreaming above and for the same reason: the toggle can
            // be flipped while the map is open, and a map opened before the uplink was granted must
            // start streaming as soon as it is.
            ExpandComponent expand = ExpandComponent.Instance;
            if (expand != null)
            {
                expand.SetWant(ExpandComponent.WantMap,
                    Config.Current.MapShowSeparation && ExpandComponent.Available);
            }
            // The campaign list rides with the map for the same reason: the markers and the Raid/
            // Conquer buttons read it, and a map opened before the grant starts streaming once it lands.
            CampaignsComponent campaigns = CampaignsComponent.Instance;
            if (campaigns != null) campaigns.SetWant(CampaignsComponent.WantMap, CampaignsComponent.Available);
            // The selection panel names what recently happened at the selected node, which is the
            // events feed. A want rather than a plain subscribe: the Events tab reads the same feed
            // and closing the map must not cut it off.
            Events.EventsComponent events = Events.EventsComponent.Instance;
            if (events != null) events.SetWant(Events.EventsComponent.WantMap, Events.EventsComponent.Available);
            _camera.Advance(_frameClock.Elapsed.TotalSeconds);
            _frameClock.Restart();
            _renderer.Draw(component.World, _selectedId, _camera.DistanceM);
            UpdateStatusText(component);
        }
        // Base last: the chrome labels belong above the markers, and the markers above the world.
        return base.Draw();
    }

    private void UpdateStatusText(MapComponent component)
    {
        if (_statusLabel == null) return;

        var status = new StringBuilder();
        if (!MapComponent.Available) status.Append("No map uplink to this server");
        else if (component.AwaitingFirstWorld) status.Append("Waiting for the world...");
        else status.Append(component.World.Nodes.Count).Append(" nodes, ")
                   .Append(component.World.Fleets.Count).Append(" fleets");
        status.Append("   |   ").Append(FormatDistance(_camera.DistanceM));
        _statusLabel.Text = status.ToString();

        SetSelectionLines(DescribeSelection(component));
        RefreshOpenButton(component);
    }

    /// <summary>Shown only for a selected node, and only while the server offers the window at all —
    /// an older server grants Map without NodeDetail, and a dead button would be worse than none.</summary>
    private void RefreshOpenButton(MapComponent component)
    {
        if (_openNode == null) return;
        _openNode.Visible = NodeDetailComponent.Available && IsNodeSelected(component);
        RefreshCampaignButtons(component);
        RefreshGpsButton(component);
    }

    /// <summary>For a selected node or planet; fleets move and the self marker is where the player
    /// already is.</summary>
    private void RefreshGpsButton(MapComponent component)
    {
        if (_gps == null) return;
        MapRenderer.PlanetPick planet;
        bool node = IsNodeSelected(component);
        bool body = !node && _renderer.TryGetPlanet(_selectedId, out planet);
        _gps.Visible = node || body;
        if (!_gps.Visible) return;
        _gps.PositionX = node ? GpsNodeX : GpsPlanetX;
        _gps.Text = _selectedId == _gpsAddedFor ? "Added" : "GPS";
    }

    /// <summary>Raid and Conquer for a selected node the viewer's faction does not own, Defend for
    /// one it does, while the server offers campaigns. A node already under one of the player's
    /// directives shows Cancel in that kind's slot and hides the others.</summary>
    private void RefreshCampaignButtons(MapComponent component)
    {
        if (_raid == null || _conquer == null || _defend == null) return;
        CampaignsComponent campaigns = CampaignsComponent.Instance;
        MapWorld.MapNode node = SelectedNode(component);
        bool offer = campaigns != null && CampaignsComponent.Available && node != null;
        if (!offer)
        {
            _raid.Visible = false;
            _conquer.Visible = false;
            _defend.Visible = false;
            return;
        }
        bool own = node.Relation == (byte)WireRelation.Own;
        WireCampaign standing;
        if (campaigns.TryGetDirectiveOn(node.Id, out standing))
        {
            bool conquest = standing.Kind == 1;
            bool defend = standing.Kind == 2;
            _raid.Visible = !conquest && !defend;
            _conquer.Visible = conquest;
            _defend.Visible = defend;
            // One word: the button is 0.11 wide, and only one of the three is visible in this state.
            _raid.Text = "Cancel";
            _conquer.Text = "Cancel";
            _defend.Text = "Cancel";
            return;
        }
        _raid.Visible = !own;
        _conquer.Visible = !own;
        _defend.Visible = own;
        _raid.Text = "Raid";
        _conquer.Text = "Conquer";
        _defend.Text = "Defend";
    }

    /// <summary>Writes the block bottom-up, so it grows away from the buttons and an identity-only
    /// selection sits exactly where it always has. A selection with more lines than slots loses its
    /// first ones, which are the identity — never the event or the range beneath it.</summary>
    private void SetSelectionLines(string[] lines)
    {
        if (_selectionLines == null) return;
        int count = lines == null ? 0 : lines.Length;
        int offset = _selectionLines.Length - count;
        for (int i = 0; i < _selectionLines.Length; i++)
        {
            int index = i - offset;
            _selectionLines[i].Text = index >= 0 && index < count ? lines[index] : "";
        }
    }

    /// <summary>Identity only, by design: name, kind, owner, standing and range. Stock, prices and
    /// build queues are deliberately absent until it is decided what a player may see.</summary>
    private string[] DescribeSelection(MapComponent component)
    {
        if (_selectedId == null) return null;
        if (_selectedId == MapRenderer.SelfId) return new[] { "You" };

        MapRenderer.PlanetPick planet;
        if (_renderer.TryGetPlanet(_selectedId, out planet))
        {
            return new[]
            {
                planet.Name,
                "Radius: " + FormatDistance(planet.RadiusM),
                // To the surface, not to the centre: a range that counts 60 km of rock is not a
                // range anyone flies.
                "Range: " + FormatDistance(Math.Max(0.0,
                    Vector3D.Distance(planet.CentreM, MapComponent.LocalPlayerPosition())
                    - planet.RadiusM)),
            };
        }

        for (int i = 0; i < component.World.Nodes.Count; i++)
        {
            MapWorld.MapNode node = component.World.Nodes[i];
            if (node.Id != _selectedId) continue;
            var lines = new List<string>(SelectionLineCount);
            if (!node.Classed)
            {
                lines.Add("Unidentified contact");
            }
            else if (!node.Identified)
            {
                // No name and no capabilities below Identity: the stage and the ground are all a
                // sensor has resolved.
                string stage = NodeLabels.StageWord(node.Stage) ?? "Site";
                lines.Add(stage + "  [" + NodeLabels.Describe(node.DominantOre, node.Surveyed, 0, node.HasStation) + "]");
                lines.Add("Owner: not identified");
            }
            else
            {
                lines.Add(node.DisplayName + "  [" + NodeLabels.Describe(
                    node.DominantOre, node.Surveyed, node.CapabilityMask, node.HasStation) + "]");
                lines.Add("Owner: " + (node.OwnerTag ?? "unclaimed") + "  (" + RelationName(node.Relation) + ")");
            }
            string defence = NodeLabels.Defence(node.Defence, 0);
            if (defence != null) lines.Add("Defence: " + defence);
            AppendEventLines(component, node, lines);
            lines.Add("Range: " + FormatDistance(
                Vector3D.Distance(node.PositionM, MapComponent.LocalPlayerPosition())));
            return lines.ToArray();
        }

        for (int i = 0; i < component.World.Fleets.Count; i++)
        {
            MapWorld.MapFleet fleet = component.World.Fleets[i];
            if (fleet.Key != _selectedId) continue;
            return DescribeFleet(fleet);
        }

        // The selection outlived the snapshot it came from.
        _selectedId = null;
        return null;
    }

    // ---- Input --------------------------------------------------------------------------------

    public override void HandleInput(bool receivedFocusInThisUpdate)
    {
        // Base first: GUI widgets get first refusal, so a click on the chrome is not also a
        // click on the map behind it.
        base.HandleInput(receivedFocusInThisUpdate);
        if (!IsOpened) return;

        if (Config.Current.MapKey.HasPressed(MyInput.Static))
        {
            CloseScreen();
            return;
        }

        _camera.Zoom(Math.Sign(MyInput.Static.DeltaMouseScrollWheelValue()));

        // Screen pixels throughout: drag deltas, click hit-testing and the marker positions the
        // renderer records all live in one space, so none of them can drift apart.
        Vector2 cursor = MyInput.Static.GetMousePosition();
        bool overControl = IsMouseOverAnyControl();

        if (MyInput.Static.IsNewLeftMousePressed() && !overControl)
        {
            _dragAnchor = cursor;
            _orbiting = true;
            _dragTravelPx = 0f;
        }
        if (MyInput.Static.IsNewRightMousePressed() && !overControl)
        {
            _dragAnchor = cursor;
            _panning = true;
        }

        Vector2 pixels = cursor - _dragAnchor;
        if (_orbiting && MyInput.Static.IsLeftMousePressed())
        {
            _dragTravelPx += pixels.Length();
            _camera.Orbit(pixels.X * OrbitRadiansPerPixel, -pixels.Y * OrbitRadiansPerPixel);
            _dragAnchor = cursor;
        }
        else if (_orbiting)
        {
            _orbiting = false;
            // Only a press that never went anywhere is a click. Accumulated travel, not the last
            // frame's delta: a drag that comes to rest before the button is released has a delta of
            // zero, which is exactly the gesture that was silently clearing the selection.
            if (_dragTravelPx < ClickSlopPixels) HandleClick(cursor);
        }

        if (_panning && MyInput.Static.IsRightMousePressed())
        {
            _camera.Pan(pixels.X, pixels.Y, PanPixelsPerRadian);
            _dragAnchor = cursor;
        }
        else if (_panning)
        {
            _panning = false;
        }
    }

    /// <summary>
    /// Nearest marker under the cursor wins, in screen pixels. A second click inside the
    /// double-click window reframes the camera on it.
    ///
    /// Ties are broken by cursor proximity rather than by depth, so a near marker does not
    /// automatically win over a far one the cursor is actually pointing at -- which is what makes
    /// picking usable where markers are stacked.
    /// </summary>
    private void HandleClick(Vector2 cursorPx)
    {
        string hit = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < _renderer.PickTargets.Count; i++)
        {
            MapRenderer.PickTarget target = _renderer.PickTargets[i];
            float distance = Vector2.Distance(target.ScreenPx, cursorPx);
            if (distance > target.RadiusPx || distance >= bestDistance) continue;
            bestDistance = distance;
            hit = target.Id;
        }

        // A click that found no marker is tested against the bodies themselves. Markers occluded by
        // a planet are already culled before they reach PickTargets, so anything the cursor did hit
        // is in front of the body behind it and keeps the click.
        if (hit == null) hit = PickPlanet(cursorPx);

        double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        bool doubleClick = hit != null && hit == _selectedId && (now - _lastClickAt) < DoubleClickSeconds;
        _lastClickAt = now;
        _selectedId = hit;

        if (doubleClick) FrameOnSelection();
    }

    /// <summary>The body under the cursor, as a selection id, or null.</summary>
    private string PickPlanet(Vector2 cursorPx)
    {
        Vector3D from;
        Vector3D direction;
        if (!Projection.TryCursorRay(cursorPx, out from, out direction)) return null;
        MapRenderer.PlanetPick planet;
        return _renderer.TryPickPlanet(from, direction, out planet) ? planet.Id : null;
    }

    /// <summary>
    /// Take the camera to whatever is selected. The travel is a glide rather than a cut -- see
    /// MapCameraController.GlideTo -- and a body is framed by its own radius while everything else
    /// gets the station standoff.
    /// </summary>
    private void FrameOnSelection()
    {
        MapComponent component = MapComponent.Instance;
        if (component == null || _selectedId == null) return;

        if (_selectedId == MapRenderer.SelfId)
        {
            _camera.GlideTo(MapComponent.LocalPlayerPosition(), FrameDistanceM);
            return;
        }
        MapRenderer.PlanetPick planet;
        if (_renderer.TryGetPlanet(_selectedId, out planet))
        {
            _camera.GlideTo(planet.CentreM, planet.RadiusM * PlanetFrameRadii);
            return;
        }
        for (int i = 0; i < component.World.Nodes.Count; i++)
        {
            if (component.World.Nodes[i].Id != _selectedId) continue;
            _camera.GlideTo(component.World.Nodes[i].PositionM, FrameDistanceM);
            return;
        }
        for (int i = 0; i < component.World.Fleets.Count; i++)
        {
            if (component.World.Fleets[i].Key != _selectedId) continue;
            _camera.GlideTo(component.World.Fleets[i].WorldPosition(), FrameDistanceM);
            return;
        }
    }

    // ---- Formatting ---------------------------------------------------------------------------

    /// <summary>
    /// What is happening at this node right now, and what happened here recently — the lines the
    /// pulsing ring on the map is an invitation to read.
    ///
    /// <para>Three sources, in the order a defender needs them: the siege on the station itself,
    /// the fleets fighting or standing at it, and then the history. Each is at most one line: the
    /// panel is four lines of room, not a report.</para>
    /// </summary>
    private void AppendEventLines(
        MapComponent component, MapWorld.MapNode node, List<string> lines)
    {
        MapWorld.MapAlarm alarm = component.World.AlarmFor(node.Id);
        if (alarm != null) lines.Add(DescribeAlarm(alarm));

        int attackers = 0, attackerHulls = 0, defenders = 0, defenderHulls = 0;
        string attackerTag = null, defenderTag = null;
        DateTime attackEnd = DateTime.MaxValue, defenceEnd = DateTime.MaxValue;
        for (int i = 0; i < component.World.Fleets.Count; i++)
        {
            MapWorld.MapFleet fleet = component.World.Fleets[i];
            if (!fleet.IsEngaging && !fleet.IsHoldingDefence) continue;
            // A dwell holds at its target, so its position is the node it is at whether or not
            // the destination was offered.
            Vector3D at = fleet.HasFlightPlan ? fleet.TargetM : fleet.WorldPosition();
            if (Vector3D.DistanceSquared(at, node.PositionM) > NodeMatchMetresSq) continue;

            if (fleet.IsEngaging)
            {
                attackers++;
                attackerHulls += fleet.ConvoySize;
                if (attackerTag == null) attackerTag = fleet.OwnerTag;
                if (fleet.HasFlightPlan && fleet.EtaUtc < attackEnd) attackEnd = fleet.EtaUtc;
            }
            else
            {
                defenders++;
                defenderHulls += fleet.ConvoySize;
                if (defenderTag == null) defenderTag = fleet.OwnerTag;
                if (fleet.HasFlightPlan && fleet.EtaUtc < defenceEnd) defenceEnd = fleet.EtaUtc;
            }
        }

        if (attackers > 0)
        {
            lines.Add("! Under attack: " + Attribution(attackerTag, attackers) + " x" + attackerHulls
                      + (attackEnd != DateTime.MaxValue
                          ? ", resolves in " + FormatRemaining(attackEnd - DateTime.UtcNow) : ""));
        }
        if (defenders > 0)
        {
            lines.Add("Defended: " + Attribution(defenderTag, defenders) + " x" + defenderHulls
                      + (defenceEnd != DateTime.MaxValue
                          ? ", relieves in " + FormatRemaining(defenceEnd - DateTime.UtcNow) : ""));
        }

        string recent = RecentEventAt(node.Id);
        if (recent != null) lines.Add(recent);
    }

    /// <summary>The siege on this station, in the phase's own words: a shield being worn down can
    /// still be denied, a breached one is a fight for the station itself, and a zone merely off is a
    /// window rather than an attack.</summary>
    private static string DescribeAlarm(MapWorld.MapAlarm alarm)
    {
        switch (alarm.State)
        {
            case WireZoneState.Besieged:
            {
                string line = "! Under siege: shield " + Percent(alarm.ZoneIntegrity);
                TimeSpan toBreach = alarm.UntilUtc - DateTime.UtcNow;
                // Only half the breach condition -- the pool has to be empty at that moment too --
                // so this is the earliest it can go, not a promise that it will.
                if (alarm.UntilUtc != DateTime.MinValue && toBreach.TotalSeconds > 0)
                    line += ", breach in " + FormatDuration(toBreach);
                return line;
            }
            case WireZoneState.Breached:
                return "! Safe zone breached: raid " + Percent(alarm.RaidProgress);
            case WireZoneState.Raided:
                return "! Raid succeeded here";
            default:
            {
                TimeSpan back = alarm.UntilUtc - DateTime.UtcNow;
                return alarm.UntilUtc != DateTime.MinValue && back.TotalSeconds > 0
                    ? "Safe zone down, back in " + FormatDuration(back)
                    : "Safe zone down";
            }
        }
    }

    /// <summary>The newest thing the viewer's faction has on record at this node. The feed is
    /// faction-scoped server-side, so this is silent about a fight between two other factions —
    /// which the alarm and fleet lines above still report, because those are what a sensor sees
    /// rather than what our own books remember.</summary>
    private static string RecentEventAt(string nodeId)
    {
        Events.EventsComponent events = Events.EventsComponent.Instance;
        if (events == null || !Events.EventsComponent.Available) return null;
        List<Events.EventsWorld.FactionEventRow> rows = events.World.Events;
        for (int i = 0; i < rows.Count; i++)
        {
            // Newest first, as the server sends it, so the first match is the latest.
            if (!string.Equals(rows[i].NodeId, nodeId, StringComparison.Ordinal)) continue;
            return Events.EventLabels.Headline(rows[i]) + "  (" + Events.EventLabels.Age(rows[i].Utc) + " ago)";
        }
        return null;
    }

    /// <summary>Who is doing it, when several factions are at one node.</summary>
    /// <summary>
    /// A fleet's panel, saying only what the viewer has learned (docs/fog-of-war.md → Intelligence
    /// grades). The server has already zeroed what lies above the tier, so each line is gated on the
    /// tier rather than on a zero, which would read a legacy one-hull convoy as unknown.
    /// </summary>
    private static string[] DescribeFleet(MapWorld.MapFleet fleet)
    {
        if (fleet.IsGrid) return DescribeGrid(fleet);
        var lines = new List<string>(3);
        bool classed = fleet.Tier >= (byte)WireIntelTier.Class;
        bool identified = fleet.Tier >= (byte)WireIntelTier.Identity;

        lines.Add(classed ? "Convoy  x" + Math.Max(1, fleet.ConvoySize) : "Unidentified contact");
        lines.Add(identified
            ? "Owner: " + (fleet.OwnerTag ?? "unknown") + "  (" + RelationName(fleet.Relation) + ")"
            : "Owner: not identified");

        // A dwelling fleet's ETA is the moment its strike RESOLVES, not an arrival: it is
        // already there. Labelling it "arrives in" describes a commute and hides the one window
        // in which the fight can still be answered.
        TimeSpan remaining = fleet.EtaUtc - DateTime.UtcNow;
        if (fleet.IsEngaging)
            lines.Add(fleet.HasFlightPlan ? "Fighting here: resolves in " + FormatRemaining(remaining) : "Fighting here");
        else if (fleet.IsHoldingDefence)
            lines.Add(fleet.HasFlightPlan ? "Holding station: relieves in " + FormatRemaining(remaining) : "Holding station");
        else if (fleet.HasFlightPlan)
            lines.Add("Arrives in: " + FormatRemaining(remaining));
        return lines.ToArray();
    }

    /// <summary>A grid's panel: the server's label for the viewer's tier, the grid's name once
    /// identified, and its range.</summary>
    private static string[] DescribeGrid(MapWorld.MapFleet fleet)
    {
        var lines = new List<string>(3);
        lines.Add(fleet.Name ?? (fleet.Tier >= (byte)WireIntelTier.Class ? "Grid" : "Unidentified contact"));
        if (!string.IsNullOrEmpty(fleet.Label)) lines.Add(fleet.Label);
        lines.Add("Range: " + FormatDistance(
            Vector3D.Distance(fleet.WorldPosition(), MapComponent.LocalPlayerPosition())));
        return lines.ToArray();
    }

    private static string Attribution(string tag, int fleets)
    {
        string who = string.IsNullOrEmpty(tag) ? "unknown" : tag;
        return fleets > 1 ? who + " +" + (fleets - 1) : who;
    }

    private static string Percent(double fraction)
    {
        return (fraction * 100.0).ToString("0") + "%";
    }

    private static string FormatRemaining(TimeSpan span)
    {
        return span.TotalSeconds <= 0 ? "now" : FormatDuration(span);
    }

    private static string RelationName(byte relation)
    {
        switch (relation)
        {
            case 1: return "own";
            case 2: return "neutral";
            case 3: return "hostile";
            case 4: return "friendly";
            default: return "unclaimed";
        }
    }

    private static string FormatDistance(double metres)
    {
        if (metres >= 1.0e9) return (metres / 1.0e9).ToString("0.##") + " Mm";
        if (metres >= 1000.0) return (metres / 1000.0).ToString("0.#") + " km";
        return metres.ToString("0") + " m";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1.0) return ((int)span.TotalHours) + "h " + span.Minutes + "m";
        if (span.TotalMinutes >= 1.0) return ((int)span.TotalMinutes) + "m " + span.Seconds + "s";
        return ((int)span.TotalSeconds) + "s";
    }
}
