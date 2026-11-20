using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>One station in a market snapshot. Carries its own name and position because the
    /// market window must not read <c>MapComponent.World</c> — that cache stops refreshing when the
    /// map closes, so a window reading it would silently show whatever was true when the player last
    /// looked at the map.</summary>
    public struct WireMarketStation
    {
        public string NodeId;
        public string DisplayName;
        public string OwnerTag;
        /// <summary>Matches <see cref="WireRelation"/>, resolved for the receiving player. Never
        /// <see cref="WireRelation.Enemy"/> — a hostile faction's stations are omitted from the
        /// payload entirely rather than sent with prices withheld.</summary>
        public byte Relation;
        public double XKm, YKm, ZKm;
        /// <summary>False when the viewer is out of contact with this station's faction and the rows
        /// are last-known rather than current. Liveness is scoped to the FACTION, not the node:
        /// while any of that faction's stations detects the player, every station of theirs the
        /// player has discovered reports live.</summary>
        public bool Live;
        /// <summary><see cref="DateTime.Ticks"/>, UTC, of the observation these rows came from.
        /// Equal to now for a live station; the age of the belief for a frozen one.</summary>
        public long AsOfUtcTicks;
    }

    /// <summary>One price row. Indices into the snapshot's station and item tables.</summary>
    public struct WireMarketRow
    {
        public ushort StationIndex;
        public ushort ItemIndex;
        /// <summary>Matches <c>Factions.Core.StationListingSide</c>: 0 = the station sells, 1 = buys.</summary>
        public byte Side;
        public int UnitPrice;
        /// <summary>Units available at that price, or -1 when the quantity is not known — a frozen
        /// station remembers what a faction wanted, not how much of it. Distinguishing "none" from
        /// "unknown" is the whole point of carrying it as a signed value.</summary>
        public int Quantity;
    }

    /// <summary>Message kinds carried on <see cref="FactionsWire.ChannelId"/>.</summary>
    public enum WireMessageKind : ushort
    {
        // ---- Uplink: the generic client/server link. Feature-agnostic on purpose. ----

        /// <summary>Client -> server. A FactionsUI client announcing itself.</summary>
        UplinkRequest = 1,

        /// <summary>Server -> client. The link is accepted; carries what the server supports.</summary>
        UplinkGranted = 2,

        /// <summary>Server -> client. The link is rejected; the client stays passive and the player
        /// keeps whatever the server-only path gives them.</summary>
        UplinkRefused = 3,

        // ---- Scanner feature. Only meaningful once WireFeatures.Scanner is granted. ----

        /// <summary>Server -> client. The player's scanner parameters, sent once on grant.</summary>
        ScannerConfig = 16,

        /// <summary>Server -> client. Changes to the player's scanner sphere since the last pass:
        /// contacts that entered or moved, and the keys of those that left.</summary>
        ScannerContacts = 17,

        // ---- Map feature. Only meaningful once WireFeatures.Map is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from map updates; sent when the
        /// map window opens and closes. The only client-originated feature message so far.</summary>
        MapRequest = 32,

        /// <summary>Server -> client. The slow-changing half of the map: nodes and factions.</summary>
        MapWorld = 33,

        /// <summary>Server -> client. The fast-changing half: fleets in transit and their
        /// remaining itinerary.</summary>
        MapFleets = 34,

        // ---- Assets feature. Only meaningful once WireFeatures.Assets is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the player's own asset
        /// register; sent when the K-menu Assets tab is entered and left.</summary>
        AssetsRequest = 48,

        /// <summary>Server -> client. Everything the viewing player and their faction own:
        /// territory, convoys in transit, and grids. Full state, never a delta.</summary>
        AssetsSnapshot = 49,

        // ---- Node detail. Only meaningful once WireFeatures.NodeDetail is granted. ----

        /// <summary>Client -> server. Watch one node, by id; an empty id stops watching. Sent when
        /// the node window opens, changes node, and closes. One node per client at a time — the
        /// window is the only thing that asks, and only one is ever open.</summary>
        NodeDetailRequest = 64,

        /// <summary>Server -> client. Everything the window shows for one node. Identity-only for a
        /// node the viewer's faction does not own — the stock, queue and ship rows are never sent
        /// for someone else's territory, rather than sent and hidden.</summary>
        NodeDetailSnapshot = 65,

        /// <summary>Client -> server. One shipyard-queue action against a watched node. The only
        /// client-originated message that changes server state; every one is re-authorised against
        /// the caller's own faction server-side, exactly as the chat verb it mirrors.</summary>
        NodeCommandRequest = 66,

        /// <summary>Server -> client. What the command did, in the words the chat verb would have
        /// used. Advisory: the authoritative answer is the snapshot that follows it.</summary>
        NodeCommandResult = 67,

        // ---- Events feature. Only meaningful once WireFeatures.Events is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the viewing player's faction
        /// history; sent when the K-menu Events tab is entered and left.</summary>
        EventsRequest = 80,

        /// <summary>Server -> client. What has happened to the viewer's faction lately, newest
        /// first. Full state, never a delta — the ring is small and bounded by definition.</summary>
        EventsSnapshot = 81,

        // ---- Standing orders. Only meaningful once WireFeatures.StandingOrders is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the viewing player's own
        /// faction standing-order list; sent when the window opens and closes.</summary>
        StandingOrdersRequest = 96,

        /// <summary>Server -> client. The whole list, in the order the managers evaluate it. Full
        /// state, never a delta — the list is bounded by <c>FactionTuning.StandingOrderCap</c>.</summary>
        StandingOrdersSnapshot = 97,

        /// <summary>Client -> server. One action against one standing order. Re-authorised against
        /// the caller's own faction server-side, exactly as the chat verb it mirrors.</summary>
        StandingOrderCommandRequest = 98,

        /// <summary>Server -> client. What the command did, in the words the chat verb would have
        /// used. Advisory: the snapshot that follows it is the authoritative answer.</summary>
        StandingOrderCommandResult = 99,

        /// <summary>Server -> client. The pick-lists the create dialog needs: which nodes can be a
        /// hub or host a build, and which prefabs each role accepts. Sent ONCE per subscribe rather
        /// than folded into the snapshot — it is the largest payload of the feature and the one thing
        /// that does not change when a player edits their list.</summary>
        StandingOrderOptions = 100,

        /// <summary>Client -> server. Create one standing order. Separate from
        /// <see cref="StandingOrderCommandRequest"/> because it carries a whole order's worth of
        /// fields rather than one id and one number.</summary>
        StandingOrderCreateRequest = 101,

        // ---- Feature rock. Only meaningful once WireFeatures.FeatureRock is granted. ----

        /// <summary>Server -> client. The pristine feature rock near this player, named rather than
        /// sent: a storage name, a shape seed and a size, from which the client builds the identical
        /// voxel locally. Replaces streaming each rock's octree, which is orders of magnitude larger
        /// for geometry both ends can derive.
        ///
        /// <para>The server names the rock rather than the client deriving it from the world recipe,
        /// which is the one design choice here worth stating. A client that walked the cell grid
        /// itself would have to agree with the server about the world seed, the cell size, the
        /// density ceiling, every authored feature's geometry, the founded-station bubbles that
        /// suppress an object, the per-tick spawn budget that decides build ORDER, and the
        /// pristine-only despawn vetoes -- eight things that must never drift, each of which fails
        /// as rock one side has and the other does not. Naming it costs ~60 bytes per rock and makes
        /// every one of those disagreements structurally impossible: the client shapes exactly what
        /// the server has already spawned, so collision always exists before geometry does.</para></summary>
        FeatureRockObjects = 112,

        /// <summary>Client -> server. Whether this client can actually derive rock, and whether its
        /// voxel material control took. The server enrols a client only on this message, never on
        /// the granted feature bit alone.
        ///
        /// <para>Two reasons it is a message rather than an assumption. The server withholds rock
        /// from an enrolled client, so enrolling one that turns out unable to build it produces
        /// space where rock should be — the client has to be the one to say it is ready. And
        /// material control is a Harmony patch on a reflected private member on BOTH ends: if one
        /// side's binding fails and the other's holds, the same rock is ice on one and gold on the
        /// other, with nothing at all to signal it. The server compares this against its own state
        /// and refuses to enrol on a mismatch, which is the "refuse rather than degrade" rule the
        /// rest of this feature is built on.</para></summary>
        FeatureRockRequest = 113,

        // ---- Grid speed. Only meaningful once WireFeatures.GridSpeed is granted. ----

        /// <summary>Server -> client. The world's large- and small-grid speed limits in m/s, sent on
        /// grant and again whenever an admin changes them. SE reads these from
        /// <c>MySector.EnvironmentDefinition</c>, which each machine resolves from its OWN local
        /// definitions — the sector save carries only the definition's Id — so a server-side change
        /// reaches a client through this message or not at all.</summary>
        GridSpeedConfig = 128,

        /// <summary>Server -> client. The multiplier the server has applied to every missile-class
        /// ammo definition's speed, sent on grant and again whenever an admin changes it. Rides the
        /// same <see cref="WireFeatures.GridSpeed"/> grant as the limits above because it is the
        /// same contract — a world speed the client must mirror onto its own definitions or predict
        /// wrongly. A missile replicates as an object builder and every client then simulates its
        /// own copy, clamped at its own <c>DesiredSpeed</c>, so a client that is not told this sees
        /// rounds fly slower than the server's.</summary>
        MunitionSpeedConfig = 129,

        // ---- Signature readout. Only meaningful once WireFeatures.Signature is granted. ----

        /// <summary>Server -> client. How loud this player's own grid currently is, on the scanner
        /// cadence. One number: what the player radiates right now, base and activity already folded
        /// together. The breakdown is deliberately not sent — it is legible from what the ship is
        /// doing, and sending it would put the whole emission model on the wire.</summary>
        SignatureStatus = 144,

        // ---- Market. Only meaningful once WireFeatures.Market is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the market table; sent when
        /// the market window opens and closes.</summary>
        MarketRequest = 160,

        /// <summary>Server -> client. Prices at every station the viewer may see, as a string table
        /// plus fixed-width rows. Full state, never a delta.
        ///
        /// <para>The table exists because a row repeating "MyObjectBuilder_Ingot/Iron" as text costs
        /// more than the price it carries. It is not load-bearing at today's world size — measured
        /// ~20 KB/min for a viewer who knows all 68 stations either way — but it bounds the ceiling
        /// rather than letting it grow with the item catalogue.</para></summary>
        MarketSnapshot = 161,

        // ---- Expand. Only meaningful once WireFeatures.Expand is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the in-flight placement
        /// readout, or commit the Expand the last status quoted. The readout streams because the
        /// answer changes as the player flies; the commit is the one client-originated message here
        /// and carries no position of its own.</summary>
        ExpandRequest = 176,

        /// <summary>Server -> client. The truthful verdict at the player's own position: clear or
        /// occupied, the separation in force, the zone, where the node would actually settle, what it
        /// costs and whose wallet pays. Doubles as the commit result — a commit is answered with the
        /// status it produced, so the client never has to reconcile two shapes.
        ///
        /// <para>What it deliberately omits is which node blocks an occupied site, and how far away
        /// it is. A ship scanner reaches 50/100 km against 500 km deep-space separation, so a player
        /// on a legal-looking deep-space site routinely cannot see what blocks them — that blind case
        /// is structural at these numbers, and telling them more here would put the whole node graph
        /// on the wire one probe at a time.</para></summary>
        ExpandStatus = 177,

        // ---- Node inventory. Only meaningful once WireFeatures.NodeInventory is granted. ----

        /// <summary>Client -> server. What may I move at this node, and may I move anything at all?
        /// A request/response pair rather than a subscription, unlike every other node message: the
        /// store screen asks when it opens and again after each transfer, and it must not take the
        /// node window's single <see cref="NodeDetailRequest"/> watch away from it.
        ///
        /// <para>Carries a station id OR a node id, whichever the asking surface has. The store
        /// block knows the station it belongs to and nothing about the node graph; the node window
        /// knows the node id and there may be no station at all. Resolving the one to the other is
        /// server work either way.</para></summary>
        NodeStockRequest = 192,

        /// <summary>Server -> client. The rows this viewer may act on, with the reserves already
        /// deducted, plus whether transfer is permitted here and why not when it is not. Sent only
        /// in answer to a request.</summary>
        NodeStockSnapshot = 193,

        /// <summary>Client -> server. Move one item key between the node's abstract stock and one
        /// physical inventory. Re-authorised server-side against the caller's own faction, their
        /// distance from the node and the ownership of the target grid — the client's claim about
        /// any of the three is never trusted.</summary>
        NodeTransferRequest = 194,

        /// <summary>Server -> client. What actually moved. The amount is carried separately from the
        /// verdict because a partial transfer is the normal case, not an error: a hold fills up and a
        /// node runs short.</summary>
        NodeTransferResult = 195,
        // ---- Ship assets. Only meaningful once WireFeatures.ShipAssets is granted. ----

        /// <summary>Client -> server. What ships does my faction hold that I may withdraw at this
        /// Services Terminal? A request/response pair rather than a subscription: the terminal asks
        /// when its screen opens and again after each withdrawal, and it must not take the node
        /// window's single <see cref="NodeDetailRequest"/> watch away from it.
        ///
        /// <para>The terminal's entity id is the only argument, and it is a claim to VERIFY rather
        /// than a fact: the server resolves the block, its station and the caller's distance from it
        /// before answering, exactly as the node messages re-authorise their own node id.</para></summary>
        TerminalAssetsRequest = 196,

        /// <summary>Server -> client. One row per (node, prefab) the caller may take a hull from,
        /// across every node the faction owns rather than only the terminal's own. Carries whether
        /// the surface may be used at all, and why not when it may not — a refusal with no reason
        /// reads as a faction that owns no ships.</summary>
        TerminalAssetsSnapshot = 197,

        /// <summary>Client -> server. Convert one unit of one prefab at one node into a vanilla
        /// stored grid. Re-authorised server-side against the caller's own faction, the terminal it
        /// names and the node's stock; the client's claim about any of the three is never trusted.</summary>
        ShipWithdrawRequest = 198,

        /// <summary>Server -> client. The guid of the stored grid the withdrawal produced, which is
        /// what the client hands to vanilla's own retrieval queue. The guid travels separately from
        /// the verdict because it is the whole point of a success: a client that read only
        /// <c>ok</c> would have nothing to pull.</summary>
        ShipWithdrawResult = 199,

        /// <summary>Client -> server. Despawn one live grid near the terminal and credit it to the
        /// node as a ship asset plus its cargo. The grid is named by entity id; the server checks
        /// ownership, distance, repair state and the prefab match itself.</summary>
        ShipSendRequest = 200,

        /// <summary>Server -> client. Which prefab the grid was matched to, and what happened. The
        /// prefab id is carried on success because it is the thing the player did not know — the
        /// match is a block fingerprint, not the grid's name.</summary>
        ShipSendResult = 201,

        /// <summary>Client -> server. Scan one live grid into a new prefab, the terminal's form of
        /// the chat capture verb: an entity id instead of a raycast, plus the name and scope the
        /// dialog collected.</summary>
        ShipScanRequest = 202,

        /// <summary>Server -> client. What the scan did, in the words the chat verb would have
        /// used.</summary>
        ShipScanResult = 203,

        // ---- Campaigns. Only meaningful once WireFeatures.Campaigns is granted. ----

        /// <summary>Client -> server. Subscribe (true) or unsubscribe (false) to the viewing
        /// player's own faction's campaign list: the hand-placed directives and the objectives its
        /// managers are serving.</summary>
        CampaignsRequest = 204,
        /// <summary>Server -> client. The whole list, sent when it changes. Bounded by the focus
        /// capacity plus the player cap, so a snapshot is small.</summary>
        CampaignsSnapshot = 205,
        /// <summary>Client -> server. Place a raid or conquest directive on a node (or its region),
        /// or cancel one by focus id. The server re-resolves the caller's faction and refuses on
        /// its own rules; nothing in the request is trusted.</summary>
        CampaignCommandRequest = 206,
        /// <summary>Server -> client. Whether the command was accepted, and the focus id it made
        /// or removed.</summary>
        CampaignCommandResult = 207,

        // ---- Station refit. Only meaningful once WireFeatures.StationRefit is granted. ----

        /// <summary>Client -> server. What could this founded node be rebuilt as, and what would
        /// each design cost from where it stands? A request/response pair rather than a
        /// subscription, for the reason <see cref="NodeStockRequest"/> is one: the refit panel opens
        /// over the node window, which holds this client's single <see cref="NodeDetailRequest"/>
        /// watch, and a second subscription would take that watch away from it.</summary>
        StationRefitRequest = 208,

        /// <summary>Server -> client. Every station design this node may be rebuilt as, each with
        /// the net bill against the design it currently stands as, plus what is already pending or
        /// collecting here. Sent only in answer to a request.</summary>
        StationRefitSnapshot = 209,

        /// <summary>Client -> server. Queue a rebuild, displace what is collecting, stand a finished
        /// one up now, or scan the station the caller is standing in as a new design. Every one is
        /// re-authorised against the caller's own faction server-side, exactly as the chat verbs
        /// they mirror; the scan additionally re-resolves the Services Terminal it names, the same
        /// way <see cref="ShipScanRequest"/> does.</summary>
        StationRefitCommandRequest = 210,

        /// <summary>Server -> client. What the command did, in the words the chat verb would have
        /// used. Advisory: the snapshot that follows it is the authoritative answer.</summary>
        StationRefitCommandResult = 211,

        // ---- Build button. Only meaningful once WireFeatures.Build is granted. ----

        /// <summary>Client -> server. What may I build at this shipyard, and what would each hull
        /// be? A request/response pair rather than a subscription, for the reason
        /// <see cref="NodeStockRequest"/> is one: the dialog opens over the node window, which holds
        /// this client's single <see cref="NodeDetailRequest"/> watch, and a second subscription
        /// would take that watch away from it.</summary>
        BuildOptionsRequest = 212,

        /// <summary>Server -> client. The roles a player may queue and the hull pick-list for THIS
        /// yard, each hull carrying the numbers a player needs to choose between them — cargo, Dps,
        /// grid size, propulsion, drill yield, BOM units — and whether it could actually leave this
        /// yard. Sent only in answer to a request.</summary>
        BuildOptions = 213,

        /// <summary>Client -> server. Queue one hull at one shipyard, once. Re-authorised
        /// server-side through the same <c>FactionRuntime</c> entry point the chat verb reaches:
        /// the pick-list is a convenience, never an authorisation.</summary>
        BuildRequest = 214,

        /// <summary>Server -> client. What the request did, in the words the chat verb would have
        /// used.</summary>
        BuildResult = 215,

        // ---- Fleet doctrine. Only meaningful once WireFeatures.FleetDoctrine is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the viewing player's own
        /// faction fleet doctrine; sent when the Fleet window opens and closes.</summary>
        FleetDoctrineRequest = 216,

        /// <summary>Server -> client. The whole doctrine in evaluation order, each line with the
        /// counts the manager itself evaluates against, plus the work waiting on hulls no line
        /// covers and a census of the hulls the faction actually owns. Full state, never a delta —
        /// the list is bounded by <c>FactionTuning.FleetDoctrineCap</c>.</summary>
        FleetDoctrineSnapshot = 217,

        /// <summary>Client -> server. One action against one doctrine line. Re-authorised against
        /// the caller's own faction server-side, exactly as the chat verb it mirrors.</summary>
        FleetDoctrineCommandRequest = 218,

        /// <summary>Server -> client. What the command did, in the words the chat verb would have
        /// used. Advisory: the snapshot that follows it is the authoritative answer.</summary>
        FleetDoctrineCommandResult = 219,

        /// <summary>Server -> client. The pick-lists the new-line dialog needs: the build roles, the
        /// owned yards a line may be scoped to, and the hull pick-list with the numbers a player
        /// chooses on. Sent ONCE per subscribe rather than folded into the snapshot — it is the
        /// largest payload of the feature and the one thing that does not change when a player edits
        /// their doctrine.</summary>
        FleetDoctrineOptions = 220,

        /// <summary>Client -> server. Create one doctrine line. Separate from
        /// <see cref="FleetDoctrineCommandRequest"/> because it carries a whole line's worth of
        /// fields rather than one id and one number.</summary>
        FleetDoctrineCreateRequest = 221,

        // ---- The store shelf. Only meaningful once WireFeatures.PlayerShelf is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the viewing player's own
        /// faction shelf; sent when the Store window opens and closes.</summary>
        PlayerShelfRequest = 222,

        /// <summary>Server -> client. The whole shelf in list order, each line with the counts the
        /// store manager itself evaluates against, plus a census of every hull actually on sale --
        /// including the ones no line named, which no other window shows. Full state, never a delta;
        /// the list is bounded by <c>FactionTuning.PlayerShelfCap</c>.</summary>
        PlayerShelfSnapshot = 223,

        /// <summary>Client -> server. One action against one shelf line, or the shop-wide restocking
        /// switch. Re-authorised against the caller's own faction server-side, exactly as the chat
        /// verb it mirrors.</summary>
        PlayerShelfCommandRequest = 224,

        /// <summary>Server -> client. What the command did, in the words the chat verb would have
        /// used. Advisory: the snapshot that follows it is the authoritative answer.</summary>
        PlayerShelfCommandResult = 225,

        /// <summary>Client -> server. Create one shelf line. Separate from
        /// <see cref="PlayerShelfCommandRequest"/> because it carries a prefab and a count rather
        /// than one id and one number.
        ///
        /// <para>There is no <c>PlayerShelfOptions</c> twin of
        /// <see cref="FleetDoctrineOptions"/>: a shelf line names no role and no scope node, so the
        /// only pick-list it needs is the hull list the Fleet window's own options message already
        /// carries. The Store window reads that one rather than asking for a second copy of the
        /// largest payload in the protocol.</para></summary>
        PlayerShelfCreateRequest = 226,

        // ---- Paused blocks. Only meaningful once WireFeatures.BlockPause is granted. ----

        /// <summary>Server -> client. Every block of the recipient's that the load governor's player
        /// toolbox has paused, in full; an empty list clears. Sent when that set changes and on link.
        /// The client appends a notice to each block's terminal detail text, which SE lets only the
        /// client write for itself (docs/load-governor.md).</summary>
        BlockPauseStatus = 227,

        // ---- Dock supply: refuel and rearm at a Services Terminal. Only meaningful once
        // WireFeatures.DockSupply is granted. ----

        /// <summary>Client -> server. Fill one gas's tanks on the ship selected on the terminal's
        /// Active Grids tab. Sent twice: first as a quote (<c>commit</c> false), which answers what
        /// the fill would move and cost, then — once the player confirms — as a commit carrying the
        /// quoted total as a ceiling the server will not charge past.</summary>
        DockRefuelRequest = 228,

        /// <summary>Server -> client. Answers a <see cref="DockRefuelRequest"/>: the quote to confirm,
        /// or what was delivered, or why nothing was. The message is shown verbatim.</summary>
        DockRefuelResult = 229,

        /// <summary>Client -> server. What could be loaded into the selected ship's weapons and cargo
        /// here: its gun groups, the magazines each takes, and what the station sells of them.</summary>
        RearmSurveyRequest = 230,

        /// <summary>Server -> client. Answers a <see cref="RearmSurveyRequest"/>. The client builds
        /// the rearm dialog from it and sends its choices back as a <see cref="RearmRequest"/>.</summary>
        RearmSurvey = 231,

        /// <summary>Client -> server. Load the chosen gun groups with the chosen magazines and put the
        /// chosen reserve counts in the ship's cargo, charging no more than the total the player
        /// confirmed. The server re-plans against the ship and the station as they stand.</summary>
        RearmRequest = 232,

        /// <summary>Server -> client. What a <see cref="RearmRequest"/> delivered, or why it
        /// delivered nothing. Shown verbatim.</summary>
        RearmResult = 233,

        // ---- Player diplomacy. Only meaningful once WireFeatures.Diplomacy is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the viewing player's own
        /// faction's diplomacy readout; sent while the terminal's Factions page or the Diplomacy
        /// window is up.</summary>
        DiplomacyRequest = 234,

        /// <summary>Server -> client. The viewer's faction's stance toward every NPC faction it can
        /// deal with, and theirs toward it. Full state; sent on subscribe, after a command, and when
        /// any row changes (checked on the scanner cadence).</summary>
        DiplomacySnapshot = 235,

        /// <summary>Client -> server. One leader act toward one NPC faction. Re-authorised
        /// server-side exactly as the <c>!factions diplomacy</c> chat verb is: the caller's own
        /// faction, then the leader check, then the act.</summary>
        DiplomacyCommandRequest = 236,

        /// <summary>Server -> client. What the act did, in the words the chat verb would have used.
        /// Advisory: the snapshot that follows is the authoritative answer.</summary>
        DiplomacyCommandResult = 237,

        // ---- The Tactics window. Only meaningful once WireFeatures.Tactics is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the viewing player's own
        /// faction's learned tactics; sent while the Tactics window is up.</summary>
        TacticsRequest = 238,

        /// <summary>Server -> client. The faction's tactic memory as the window shows it: per
        /// opponent the preferred tactic and the context buckets behind it, and the last reviews.
        /// Full state; sent on subscribe and on the scanner cadence while subscribed.</summary>
        TacticsSnapshot = 239,

        /// <summary>Client -> server. A wing action pressed on an antenna's toolbar slot: the
        /// antenna, the command, and the grid under the presser's crosshair. Behind
        /// <see cref="WireFeatures.WingCommand"/>; the server re-validates the antenna, the
        /// presser and the target every time (docs/fleet-tactics.md, Package 2).</summary>
        WingCommandRequest = 240,

        /// <summary>Server -> client. Whether the command was taken, in words for the HUD.</summary>
        WingCommandResult = 241,

        // ---- Combat outcomes. Only meaningful once WireFeatures.CombatOutcome is granted. ----

        /// <summary>Server -> client. A fight within the viewer's observation range has resolved:
        /// one sentence for the HUD, and whether the viewer's faction won, lost or was not in it
        /// (<see cref="WireCombatTone"/>). Sent once per resolution; nothing is kept.</summary>
        CombatOutcomeNotice = 242,

        // ---- Restoration at a Services Terminal. Only meaningful once
        // WireFeatures.Restoration is granted. ----

        /// <summary>Client -> server. What restoring the ship selected on the terminal's Repair tab
        /// would take: the bill and the time, or — for a whole ship — the fee to register its
        /// design, or the state of a restore already running. Grid 0 is the probe a terminal sends
        /// when it opens: is this station's Repair tab Restore's?</summary>
        RestoreQuoteRequest = 243,

        /// <summary>Server -> client. Answers a <see cref="RestoreQuoteRequest"/>, at once or — when
        /// the ship's design has to be found by matching — a few frames later.</summary>
        RestoreQuote = 244,

        /// <summary>Client -> server. Start the quoted restore (charging no more than the confirmed
        /// total), register the ship's design for the quoted fee, or stop a running restore.</summary>
        RestoreRequest = 245,

        /// <summary>Server -> client. What a <see cref="RestoreRequest"/> did, or — flagged as a
        /// notice — a running restore pausing, resuming or ending. Shown verbatim.</summary>
        RestoreResult = 246,

        // ---- Traffic control voice. Only meaningful once WireFeatures.TrafficVoice is granted. ----

        /// <summary>Server -> client. Something a station's traffic controller would say, for a
        /// player within the station's radio range (FAC-419, docs/atc-voice.md). The client decides
        /// what is actually spoken: one line at a time, hostile first, bursts collapsed.</summary>
        TrafficCallout = 247,

        /// <summary>Server -> client. A pilot calling a station, heard over the radio physics between
        /// the pilot's ship and this listener: Doppler, flutter, signal-to-noise, muffle. Always
        /// garbled; the client picks the words (FAC-419 slice 5).</summary>
        TrafficPilotCall = 248,

        /// <summary>Client -> server. Whether this player wants the traffic voice. Off gives them back
        /// the fleet chat whispers the voice replaces.</summary>
        TrafficVoicePreference = 249,
        /// <summary>Server → client, once per Orders-window subscribe: every item a Stock order may name,
        /// with who could supply it. See <see cref="WireStockItem"/>.</summary>
        StockItemOptions = 250,

        // ---- Design versions. Only meaningful once WireFeatures.DesignVersions is granted. ----

        /// <summary>Client -> server. Which design is this grid, and what would scanning it publish?
        /// Sent when a Scan or Scan station dialog opens, naming the selected hull or the terminal
        /// the station scan is taken at.</summary>
        DesignVersionQuery = 251,

        /// <summary>Server -> client. The answer: the design the grid stands as or was built as, and
        /// the version a scan under that name would publish — or why it would not.</summary>
        DesignVersionInfo = 252,

        // ---- Crew orders. Only meaningful once WireFeatures.CrewOrders is granted. ----

        /// <summary>Client -> server. A crew order pressed on a seat's toolbar slot: the seat and the
        /// order. The server re-validates the seat's hull and the presser's right to command it
        /// (docs/ground-operations.md).</summary>
        CrewOrderRequest = 253,

        /// <summary>Server -> client. Whether the order was taken, in words for the HUD.</summary>
        CrewOrderResult = 254,

        // ---- Bank sales: which items the central bank may buy. Behind WireFeatures.PlayerShelf, ----
        // ---- because the window opens from the Store window.                                   ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the viewing player's own
        /// faction's bank-sale list; sent when the Bank sales window opens and closes.</summary>
        BankSalesRequest = 255,

        /// <summary>Server -> client. Every item the central bank buys, with what the faction holds
        /// of it and the rule it set. Full state, never a delta.</summary>
        BankSalesSnapshot = 256,

        /// <summary>Client -> server. Set one rule on a set of items. Re-authorised against the
        /// caller's own faction server-side, exactly as the chat verb it mirrors.</summary>
        BankSalesCommandRequest = 257,

        /// <summary>Server -> client. What the command did, in the chat verb's words. Advisory: the
        /// snapshot that follows it is the authoritative answer.</summary>
        BankSalesCommandResult = 258,

        // ---- Node spawn. Only meaningful once WireFeatures.NodeSpawn is granted. ----

        /// <summary>Client -> server. The respawn screen is open or refreshing: send the caller's own
        /// founded nodes and whether each can take a spawn. No payload.</summary>
        NodeSpawnListRequest = 259,

        /// <summary>Server -> client. The caller's founded nodes, each with its block reason. The
        /// spawn itself is not a Factions message: the client sends SE's own respawn request naming
        /// <see cref="NodeSpawnSentinel"/>.</summary>
        NodeSpawnList = 260,

        // ---- Faction demand. Only meaningful once WireFeatures.FactionDemand is granted. ----

        /// <summary>Client -> server. Subscribe to or unsubscribe from the viewing player's own
        /// faction's demand; sent when the Assets tab's Demand window opens and closes.</summary>
        FactionDemandRequest = 261,

        /// <summary>Server -> client. Every live <c>Item:</c> and <c>Ship:</c> demand the faction
        /// has, and what ended lately. Full state, never a delta.</summary>
        FactionDemandSnapshot = 262,

        // ---- Diagnostics. No feature grant: sent only to the admin who asked, and an older client
        // ---- simply has no handler for it.                                                       ----

        /// <summary>Server -> client. Record this client's prediction corrections for one grid over
        /// the next frames, alongside the server's own trace of it (<c>factions dampprobe trace</c>).</summary>
        NetTraceStart = 263,

        // ---- Status panel. Only meaningful once WireFeatures.StatusPanel is granted. ----

        /// <summary>Server -> client. Everything the player's HUD status panel shows, whole: an empty
        /// list clears it. Sent when what it shows changes, and again every few seconds while it is
        /// not empty, so a client can drop a panel the server stopped refreshing. Countdowns travel as
        /// seconds left at send time and are counted down by the client.</summary>
        StatusPanel = 264,
    }

    /// <summary>Which side of the server's two relation thresholds a stance sits on. Resolved
    /// server-side, so the client needs no copy of the tuning to colour a value.</summary>
    public enum WireStanceBand : byte
    {
        Hostile = 0,
        Neutral = 1,
        Friendly = 2,
    }

    /// <summary>One leader act in a <see cref="WireMessageKind.DiplomacyCommandRequest"/>. The
    /// ordinals are Core's <c>LeaderAct</c>'s; the host maps them by name all the same, so a
    /// renumbering on either side is a compile-visible change rather than a silent one. A proposal's
    /// kind is its own act, so a command needs no separate term field.</summary>
    public enum WireDiplomacyAct : byte
    {
        Status = 0,
        War = 1,
        Embargo = 2,
        Lift = 3,
        Gift = 4,
        Peace = 5,
        NonAggression = 6,
        Alliance = 7,
        Withdraw = 8,
    }

    /// <summary>One NPC faction as the viewer's faction sees it. Mirrors Core's
    /// <c>DiplomacyReadoutRow</c>, which the chat <c>status</c> verb formats too. Times are UTC
    /// ticks, 0 for none; the snapshot's <c>serverNowTicks</c> is what the client measures them
    /// against, so the client's own clock never enters a countdown.</summary>
    public struct WireDiplomacyRow
    {
        public string NpcTag;
        /// <summary>The NPC's view of the viewer's faction, -1..1.</summary>
        public double TheirView;
        public WireStanceBand TheirBand;
        /// <summary>The viewer's faction's own stance, set by its leader, -1..1.</summary>
        public double YourStance;
        public WireStanceBand YourBand;
        public bool EmbargoByYou;
        public bool EmbargoByThem;
        public bool TradeBarred;
        public bool Allied;
        /// <summary>The NPC takes no gifts and makes no agreements.</summary>
        public bool Implacable;
        public long TruceUntilTicks;
        /// <summary>The viewer's faction's outstanding offer: Core's term names joined by '+'
        /// (<c>Truce</c>, <c>NonAggression</c>, <c>Alliance</c>), or empty.</summary>
        public string YouOffered;
        public long YouOfferedExpiresTicks;
        /// <summary>The NPC's outstanding offer to the viewer's faction, or empty.</summary>
        public string TheyOffered;
        public long TheyOfferedExpiresTicks;
        /// <summary>When the next gift may go; 0 when one may go now.</summary>
        public long GiftReadyTicks;
    }

    /// <summary>One line of a <see cref="WireStatusPanelItem"/>. <see cref="ClockToken"/> in the text
    /// is replaced by m:ss counting down from <see cref="CountdownSeconds"/> at receipt.</summary>
    public struct WireStatusPanelLine
    {
        public const string ClockToken = "{clock}";

        public string Text;
        /// <summary>Seconds left when the server sent it; negative when the line has no countdown.</summary>
        public float CountdownSeconds;
    }

    /// <summary>One situation in a <see cref="WireMessageKind.StatusPanel"/>: the first line is the
    /// heading. <see cref="Tone"/>: 0 info, 1 under way, 2 act now, 3 about to be lost; a client that
    /// predates a value shows it as info.</summary>
    public struct WireStatusPanelItem
    {
        public string Key;
        public byte Tone;
        public WireStatusPanelLine[] Lines;
    }

    /// <summary>One paused block in a <see cref="WireMessageKind.BlockPauseStatus"/>.</summary>
    public struct WirePausedBlock
    {
        public long EntityId;
        /// <summary>0 refinery, 1 assembler, 2 welder (Build and Repair included), all paused;
        /// <see cref="ScriptSlowed"/> and <see cref="ScriptStopped"/> for a programmable block the
        /// limiter holds. A client that predates a value shows it as paused.</summary>
        public byte Class;

        public const byte ScriptSlowed = 3;
        public const byte ScriptStopped = 4;
        /// <summary>Paused by an admin rather than by the governor.</summary>
        public bool ByAdmin;
    }

    /// <summary>One choice in the create dialog's node or prefab list. <see cref="Flags"/> says what
    /// it may be used for, so one list serves both pickers without the client re-deriving
    /// eligibility from data it does not have.</summary>
    public struct WireOrderOption
    {
        public string Id;
        /// <summary>Display name, or empty to show the id. For a prefab this is the role it belongs
        /// to, so the dialog can filter one flat list by the selected role.</summary>
        public string Label;
        public int Flags;
    }

    /// <summary>Bits on <see cref="WireOrderOption.Flags"/> for a node option.</summary>
    [Flags]
    public enum WireNodeOptionFlags
    {
        None = 0,
        /// <summary>Owned and able to anchor a Mine order: something mineable is within reach.</summary>
        MineHub = 1 << 0,
        /// <summary>Owned and Shipyard-capable, so a Build goal may be scoped to it.</summary>
        Shipyard = 1 << 1,
    }

    /// <summary>What a <see cref="WireMessageKind.StandingOrderCommandRequest"/> asks for.
    /// Deliberately only the actions that operate on a row the window is already showing — creating
    /// an order needs a prefab or hub picker the window does not have, and stays a chat verb.</summary>
    public enum WireStandingOrderCommand : byte
    {
        Remove = 0,
        Priority = 1,
        Pause = 2,
        Resume = 3,
    }

    /// <summary>One row of the standing-order list: a Mine order (<see cref="Kind"/> 0) or a Stock
    /// order (1). Both are anchored at a node, carried in <see cref="HubNodeId"/>; each kind reads
    /// only its own fields and leaves the other kind's at their defaults.
    ///
    /// <para><see cref="NextScanUtcTicks"/> is a timestamp rather than a rendered countdown so the
    /// client can tick it down locally. Sending the countdown would mean re-sending the whole list
    /// every scan for a number the viewer can derive.</para></summary>
    public struct WireStandingOrder
    {
        public string Id;
        /// <summary>0 = Mine, 1 = Stock. Matches <c>Factions.Core.StandingOrderKind</c>.</summary>
        public byte Kind;
        public int Priority;
        public bool Paused;

        public string HubNodeId;
        /// <summary>Resolved server-side: the client has no graph to look a name up in.</summary>
        public string HubNodeName;
        public double PeriodSeconds;
        /// <summary>Pre-rendered ore mix ("Iron:50, Nickel:50"), empty for a node's natural mix.
        /// A display string rather than a map: the window shows it and never computes with it.</summary>
        public string OreWeights;
        public long NextScanUtcTicks;

        /// <summary>Stock: the inventory key kept at the node.</summary>
        public string ItemKey;
        /// <summary>Stock: the item's display name, resolved server-side.</summary>
        public string ItemName;
        public long TargetQuantity;
        /// <summary>Stock: what the node holds of the item now.</summary>
        public long Have;

        /// <summary>Stock: the order covers every founded station the faction owns, and
        /// <see cref="HubNodeId"/> is empty.</summary>
        public bool AllStations;
        /// <summary>Stock, all stations: the target at each station in <see cref="StationIds"/> — the
        /// item's all-stations orders summed.</summary>
        public long StationTarget;
        /// <summary>Stock, all stations: the stations it sets the target at (a per-node order for the
        /// item overrides it elsewhere), parallel to <see cref="StationHaves"/>. Null otherwise.</summary>
        public string[] StationIds;
        public long[] StationHaves;
    }

    /// <summary>Bits of <see cref="WireStockItem.Flags"/>: where a Stock order for the item could be
    /// filled from today. None set means no source is known; the order is still allowed.</summary>
    [Flags]
    public enum WireStockItemFlags
    {
        /// <summary>One of the faction's own stations has a recipe that makes it.</summary>
        OwnProduction = 1 << 0,
        /// <summary>The faction's own stations hold some.</summary>
        OwnStock = 1 << 1,
        /// <summary>Another faction's stations hold some.</summary>
        OtherStock = 1 << 2,
    }

    /// <summary>One entry of the Stock-order item pick-list.</summary>
    public struct WireStockItem
    {
        /// <summary>Inventory key, the value a create request sends back.</summary>
        public string Key;
        public string Name;
        /// <summary>Grouping for the list: Ore, Ingot, Component, Ammo, Gas or Other.</summary>
        public string Category;
        /// <summary>Pre-rendered source text ("you make it", "IGSL, METC hold it").</summary>
        public string Source;
        public int Flags;
    }

    /// <summary>What a <see cref="WireMessageKind.CampaignCommandRequest"/> asks for.</summary>
    public enum WireCampaignCommand : byte
    {
        /// <summary>Place a directive: <c>NodeId</c>, <c>Kind</c> (0 raid, 1 conquest, 2 defend) and
        /// <c>RegionScope</c> (ignored for defend) are read.</summary>
        Place = 0,
        /// <summary>Cancel a standing player directive: <c>FocusId</c> is read.</summary>
        Cancel = 1,
    }

    /// <summary>One campaign objective as the list window and the map see it. Every name is resolved
    /// server-side; the client has no graph.</summary>
    public struct WireCampaign
    {
        /// <summary>The focus id. Stable for the campaign's life and what a cancel names.</summary>
        public string FocusId;
        /// <summary>0 = raid, 1 = conquest, 2 = defend (a player-issued defensive focus: the
        /// target is its centre, <see cref="CandidateCount"/> its region's membership). A new value
        /// in an existing byte, so no <see cref="FactionsWire.ProtocolVersion"/> move.</summary>
        public byte Kind;
        /// <summary>Matches <c>Factions.Core.CampaignCause</c> by ordinal: 0 Predation, 1 Grudge,
        /// 2 Retaliation, 3 Terror, 4 Directive.</summary>
        public byte Cause;
        /// <summary>Empty for a region-scoped objective.</summary>
        public string TargetNodeId;
        public string TargetNodeName;
        /// <summary>The region key ("body:&lt;planet&gt;:Surface", a feature name), or empty for a
        /// node-scoped objective.</summary>
        public string RegionKey;
        public string StagingNodeId;
        public long ExpiresUtcTicks;
        /// <summary>0 when nothing has been dispatched under it yet.</summary>
        public long LastDispatchUtcTicks;
        /// <summary>How many targets the objective currently ranks. 0 means nothing to attack —
        /// the ground is taken, or nothing in reach is known.</summary>
        public int CandidateCount;
        /// <summary>True for a directive the player placed; false for one the brain nominated (an
        /// NPC's, or a leftover on a player faction).</summary>
        public bool PlayerIssued;

        /// <summary>What came back from the last attempt, matching <c>Factions.Core.CampaignOutcome</c>
        /// by ordinal: 0 nothing yet, 1 refused, 2 intercepted, 3 landed.
        ///
        /// <para><see cref="LastDispatchUtcTicks"/> is stamped by a refusal exactly as it is by a
        /// dispatch — deliberately, since a refusal decides — so it alone cannot say whether anything
        /// flew. This is the field that separates the three.</para></summary>
        public byte LastOutcome;
        /// <summary>When that outcome was decided. Later than <see cref="LastDispatchUtcTicks"/> for
        /// a fleet that flew and then lost; equal to it for a refusal. 0 when there is none.</summary>
        public long LastOutcomeUtcTicks;
        /// <summary>The duel range in metres that decided the outcome, and both sides' output there
        /// — the actionable half, because it names the reach a different hull would have to bring.
        /// All three are 0 for <see cref="LastOutcome"/> 3 (landed), which turns on no bracket.</summary>
        public int LastOutcomeRangeM;
        public int LastOutcomeOwnDps;
        public int LastOutcomeOpposedDps;
        /// <summary>Hulls the last attempt committed and lost; both 0 for a refusal, which spends
        /// none.</summary>
        public int LastOutcomeHullsLost;
        public int LastOutcomeHullsTotal;
    }

    /// <summary>One pristine feature-rock object as it crosses the wire: everything the client
    /// needs to build the identical voxel and nothing it could not act on.
    ///
    /// <para><see cref="EntityId"/> is carried rather than re-derived client-side. Both ends must
    /// agree on it exactly -- it is what lets SE's own <c>MyVoxelReplicable.OnLoad</c> swap an
    /// edited storage into the entity the client already has instead of stacking a second rock on
    /// top of it -- and a hash re-implemented on the client is a second place for that agreement to
    /// break silently.</para></summary>
    public struct WireRockObject
    {
        /// <summary>SE voxel storage name; also the delta key.</summary>
        public string StorageName;
        public long EntityId;
        public double XKm, YKm, ZKm;
        public double SizeM;
        /// <summary>Bare SE voxel-material subtype, resolved to a definition client-side.</summary>
        public string MaterialSubTypeId;
        /// <summary>Seed for SE's own asteroid shape generator -- the whole of the geometry.</summary>
        public int ShapeSeed;

        /// <summary>
        /// The rock's authored ore-volume overrides, or null to inherit the world's own settings.
        ///
        /// <para>These are SE's <c>DepositsCountCoefficient</c> and <c>DepositSizeDenominator</c>,
        /// which SE reads off the session while building a rock and which are therefore per-WORLD.
        /// The server makes them per-material by scoping the session setting across one generation
        /// call; the client has to do exactly the same or the two ends build the same seed into
        /// rock with different amounts of ore in it.</para>
        ///
        /// <para>They are carried per rock rather than once at link time precisely because they are
        /// per material: two rocks in one cell can be authored differently, so a single per-session
        /// value could not express them. Absent means "leave the world's setting alone" and is not
        /// the same as zero -- a zero denominator is what SE treats as "unset" internally, and a
        /// zero count would mean a rock with no ore at all.</para>
        /// </summary>
        public bool HasDepositCount;
        public double DepositCountCoefficient;
        public bool HasDepositSize;
        public double DepositSizeDenominator;
    }

    /// <summary>What a server offers a linked client. Granted per uplink so a client can tell an
    /// old server from a new one without inspecting version strings, and so a feature can be
    /// withheld (disabled, or not permitted for that player) without refusing the link.</summary>
    [Flags]
    public enum WireFeatures : long
    {
        None = 0,
        Scanner = 1L << 0,
        Map = 1L << 1,
        Assets = 1L << 2,
        /// <summary>The per-node window: the detail stream and the queue actions it hosts. A bit of
        /// its own rather than a corner of <see cref="Assets"/> — it is the one feature that carries
        /// a client-originated mutation, so a server must be able to withhold it while still handing
        /// out the read-only register.</summary>
        NodeDetail = 1L << 3,
        /// <summary>The faction history feed behind the Events tab. Read-only, and separate from
        /// <see cref="Assets"/> so a server can offer the register without the history.</summary>
        Events = 1L << 4,
        /// <summary>The standing-order window and the actions it hosts. A bit of its own for the
        /// same reason <see cref="NodeDetail"/> has one: it carries client-originated mutations of
        /// a faction's recurring intent, so a server must be able to withhold it while still
        /// granting the read-only surfaces.</summary>
        StandingOrders = 1L << 5,
        /// <summary>Pristine feature rock is named to this client instead of streamed to it. Its own
        /// bit because it changes what the SERVER withholds, not what the client may look at: a
        /// server that grants it stops replicating pristine rock to that client, so it must be able
        /// to withhold the bit -- and start replicating again -- without touching any other feature.</summary>
        FeatureRock = 1L << 6,
        /// <summary>The server's speed settings are pushed to this client, which applies them to its
        /// own definitions: the grid speed limits onto the environment definition, and the missile
        /// munition speed scale onto the ammo definitions. Its own bit because it changes what the
        /// CLIENT enforces rather than what it may look at: SE resolves both from each machine's
        /// local content, so a server-side change is invisible to a client until it is sent one —
        /// and a server must be able to withhold that without touching any other feature. The two
        /// share one bit deliberately: granting half of it would leave the client predicting one
        /// speed correctly and the other wrongly, which is the desync the feature exists to avoid.</summary>
        GridSpeed = 1L << 7,
        /// <summary>The player's own signature, pushed on the scanner cadence. A bit of its own
        /// rather than a corner of <see cref="Scanner"/>: the scanner is about what the player can
        /// see, this is about what can see <i>them</i>, and a server may reasonably offer one without
        /// the other.</summary>
        Signature = 1L << 8,
        /// <summary>The market window: prices and trading opportunities across every station the
        /// viewer's faction knows. Read-only, and its own bit because it is the one surface that
        /// reveals a *rival's* economy — a server must be able to withhold it while still granting
        /// the map that shows those stations exist.</summary>
        Market = 1L << 9,
        /// <summary>The placement readout and the Expand it commits. Its own bit because it is the
        /// only client-originated message that SPENDS a wallet — a server must be able to withhold
        /// the purchase while still granting the map that shows where one would be legal.</summary>
        Expand = 1L << 10,
        /// <summary>Withdraw and Deposit against a node's own stock. Its own bit for the reason
        /// <see cref="NodeDetail"/> has one, one step further: this one moves goods rather than
        /// intent, so a server must be able to withhold the hand while still granting the window
        /// that shows what the node holds.</summary>
        NodeInventory = 1L << 11,
        /// <summary>The Services Terminal's ship surfaces: the faction's withdrawable hulls, Send to
        /// Node, and Scan. Its own bit for the reason <see cref="Expand"/> has one, one step further:
        /// this one SPAWNS and DESPAWNS hulls rather than reading a register, so a server must be
        /// able to withhold it while still granting <see cref="NodeDetail"/> — the window that shows
        /// those same ships as a count.</summary>
        ShipAssets = 1L << 12,
        /// <summary>The campaign list and the two directives a player may place on the map — raid
        /// this, conquer this — which their faction's FleetManager then serves. Its own bit for the
        /// reason <see cref="StandingOrders"/> has one: it carries a client-originated mutation of
        /// a faction's intent, and this one starts fights, so a server must be able to withhold it
        /// while still granting the map that shows the targets.</summary>
        Campaigns = 1L << 13,
        /// <summary>The refit panel on the node window and the Scan-station button on the Services
        /// Terminal. Its own bit for the reason <see cref="ShipAssets"/> has one: a refit closes and
        /// rebuilds the live station grid a player may be standing in, and a scan reads one, so a
        /// server must be able to withhold it while still granting <see cref="NodeDetail"/> — the
        /// window the panel opens from, which only reads the same node.</summary>
        StationRefit = 1L << 14,
        /// <summary>The station window's <c>Build...</c> button and the dialog behind it: the hull
        /// pick-list for one yard, and the one-shot build it queues. Its own bit for the reason
        /// <see cref="StandingOrders"/> has one — it carries a client-originated mutation, this one
        /// spending a station's components — so a server must be able to withhold it while still
        /// granting <see cref="NodeDetail"/>, the window the button sits on. Withheld, the button
        /// is drawn and disabled rather than absent, so the surface does not silently vanish.</summary>
        Build = 1L << 15,
        /// <summary>The Fleet window: the faction's doctrine, what it is waiting on, the hulls it
        /// owns, and the lines a player adds or edits. Its own bit for the reason
        /// <see cref="StandingOrders"/> has one — it carries a client-originated mutation of a
        /// faction's standing intent, and this one decides everything the faction builds — so a
        /// server must be able to withhold it while still granting the read-only surfaces.</summary>
        FleetDoctrine = 1L << 16,
        /// <summary>The Store window: what this faction's shop sells, what is on sale now, and the
        /// lines a player adds or edits. Its own bit for the reason <see cref="FleetDoctrine"/> has
        /// one — it carries a client-originated mutation of standing intent, and this one decides
        /// what the faction's yards spend components on for the shop. Withheld, the Store button is
        /// drawn and disabled rather than absent.</summary>
        PlayerShelf = 1L << 17,
        /// <summary>The recipient is told which of their blocks the load governor has paused, so
        /// the client can say so in each block's terminal. Local to the server that owns the
        /// blocks, like <see cref="GridSpeed"/>.</summary>
        BlockPause = 1L << 18,
        /// <summary>Refuel and rearm at a Services Terminal: a docked ship buys gas into its tanks
        /// and magazines into its guns and cargo from the station's own stock. Local to the server
        /// that owns the terminal, like <see cref="ShipAssets"/> — every step is an entity in the
        /// caller's own sector.</summary>
        DockSupply = 1L << 19,
        /// <summary>The player-diplomacy readout on the terminal's Factions page and the Diplomacy
        /// window's acts: embargo, lift, gift, peace, non-aggression, alliance, withdraw. Its own bit
        /// for the reason <see cref="StandingOrders"/> has one — it carries client-originated
        /// mutations, and these change what the faction trades and who it is at war with — so a
        /// server must be able to withhold it. Withheld, the page is exactly vanilla's; the chat verb
        /// stays.</summary>
        Diplomacy = 1L << 20,
        /// <summary>The Tactics window off the Fleet window: the faction's learned tactics per
        /// opponent. Read-only; its own bit so a server can withhold what its faction has learned
        /// about fighting whom while still granting the doctrine beside it.</summary>
        Tactics = 1L << 21,
        /// <summary>Wing actions on antennas: a player's toolbar press commands their own faction's
        /// materialised NPC hulls. Its own bit because it is a client-originated command on live
        /// grids in the caller's own sector, so a server withholds it from a relayed client.</summary>
        WingCommand = 1L << 22,
        /// <summary>A HUD notice when a fight the player could see resolves, coloured by whether
        /// their faction won or lost it. Local to the server where the fight is, like
        /// <see cref="WingCommand"/>: the viewers are the players in that server's sectors.</summary>
        CombatOutcome = 1L << 23,
        /// <summary>Register a design and Restore at a Services Terminal: a ship docked beside the
        /// terminal is welded and its missing blocks placed back, paid from its cargo, the station's
        /// stock and the bank. Local to the server that owns the terminal, like
        /// <see cref="DockSupply"/>: every step is an entity in the caller's own sector.</summary>
        Restoration = 1L << 24,
        /// <summary>Traffic control voice: station callouts in radio range replace the fleet
        /// chat whispers. Local to the server where the station is, like <see cref="CombatOutcome"/>:
        /// the listeners are the players in that server's sectors.</summary>
        TrafficVoice = 1L << 25,
        /// <summary>The scan dialogs' "Publish as" choice: the server says which design a selected
        /// hull or this terminal's station is, and the version scanning it would publish. Local to
        /// the server that owns the grid, like <see cref="ShipAssets"/>: the question names an entity
        /// in the caller's own sector.</summary>
        DesignVersions = 1L << 26,
        /// <summary>Crew orders on seats: a player's toolbar press tells the crew of a hull they
        /// command to mount up, dismount, stand at ease or follow the automatic default. Local to the
        /// server that owns the hull, like <see cref="WingCommand"/>: the seat and the crew are
        /// entities in the caller's own sector. Additive, so no <see cref="FactionsWire.ProtocolVersion"/>
        /// move: an older client is never granted the bit, and an older server never grants it.</summary>
        CrewOrders = 1L << 27,
        /// <summary>Respawning at one of the player's own founded nodes, from SE's respawn screen.
        /// Local to the server the player is on, like <see cref="CrewOrders"/>: the respawn request
        /// is handled by this process, which places the character. Additive, so no
        /// <see cref="FactionsWire.ProtocolVersion"/> move: an older client is never granted the bit,
        /// and an older server never grants it.</summary>
        NodeSpawn = 1L << 28,
        /// <summary>The Demand window off the Assets tab: everything the viewer's own faction is
        /// asking for, station-pinned and faction-wide, and what ended lately. Read-only. Additive, so
        /// no <see cref="FactionsWire.ProtocolVersion"/> move: an older client is never granted the
        /// bit, and an older server never grants it.</summary>
        FactionDemand = 1L << 29,
        /// <summary>The HUD status panel: sieges and occupations the player is running, drawn by the
        /// client in one fixed place instead of as repeated centre-screen notifications. A granted
        /// client is sent no siege status notification, only the panel and the chat lines, so it
        /// moved <see cref="FactionsWire.ProtocolVersion"/>: an older client granted it would show
        /// nothing at all.</summary>
        StatusPanel = 1L << 30,
    }

    /// <summary>How a contact's owner stands towards the viewing player. Resolved server-side:
    /// the client must never be handed the standings table to work this out for itself.</summary>
    public enum WireRelation : byte
    {
        /// <summary>Unowned / claimable — the neutral "graveyard" faction.</summary>
        Unknown = 0,
        /// <summary>Owned by the viewing player's own faction.</summary>
        Own = 1,
        /// <summary>Owned by another faction at neutral standing.</summary>
        Neutral = 2,
        /// <summary>Owned by a faction hostile to the viewing player.</summary>
        Enemy = 3,
    }

    /// <summary>One scanner contact as it crosses the wire. Mirrors the server-side
    /// <c>ScannerContact</c> without depending on it — this assembly stays free of Factions.Core.</summary>
    public struct WireContact
    {
        /// <summary>Stable identity for this contact, used to match updates and removals.</summary>
        public string Key;
        /// <summary>Matches the server's <c>ScannerContactKind</c> ordinal.</summary>
        public byte Kind;
        public string Label;
        /// <summary>The node's own name, or empty. Absent from the wire for a fleet, which has
        /// none. The HUD draws it only while Alt is held, and not when a GPS on the HUD already
        /// shows it (<see cref="ScannerNames"/>).</summary>
        public string DisplayName;
        public double XKm;
        public double YKm;
        public double ZKm;
        /// <summary>Non-zero for contacts backed by a live entity, so a client that has the entity
        /// streamed can track it at frame rate instead of at the server's scanner cadence.</summary>
        public long EntityId;
        public bool Unclaimed;
        /// <summary>Matches <see cref="WireRelation"/>. Drives the marker colour; the icon carries
        /// the kind, so the two axes stay independent.</summary>
        public byte Relation;
        /// <summary>Whether this contact moves. False for everything anchored to the ground, and
        /// the three vector fields below are then absent from the wire rather than sent as
        /// zeroes -- a scanner sphere is mostly nodes, and they pay nothing for this.</summary>
        public bool Moving;
        /// <summary>Instantaneous velocity along the current leg, km per minute. Sampled from the
        /// server's own acceleration profile so the client can dead-reckon between scanner passes
        /// without a second copy of the easing curve. Zero unless <see cref="Moving"/>.</summary>
        public double VelXKmPerMin;
        public double VelYKmPerMin;
        public double VelZKmPerMin;
        /// <summary>Where this contact is heading, so dead reckoning can be clamped there instead
        /// of sailing past it. Zero unless <see cref="Moving"/>.</summary>
        public double TargetXKm;
        public double TargetYKm;
        public double TargetZKm;
        /// <summary>
        /// The convoy's mission, as a Core <c>ActionKind</c> ordinal — the same byte
        /// <see cref="WireMapFleet.Kind"/> carries, deliberately, so one client table turns either
        /// into an icon and the HUD and the map cannot disagree about what a convoy is doing.
        ///
        /// <para>Absent from the wire, and zero, unless
        /// <see cref="FactionsWire.ContactCarriesFleetFields"/>. <see cref="Kind"/> stays a
        /// <c>ScannerContactKind</c> — what sort of contact this is — and this says which mission,
        /// so the two axes stay independent the way relation and icon already are.</para>
        /// </summary>
        public byte FleetKind;
        /// <summary>Hulls in the convoy, for the strength mark. A byte: the echelon ladder's top
        /// band opens at 18 and <c>RaidSquadronMaxHulls</c> is 24, so anything past 255 would draw
        /// identically. Absent and zero on the same condition as <see cref="FleetKind"/>; zero also
        /// means "a legacy fleet that never wrote a size", which reads as one hull.</summary>
        public byte ConvoySize;
    }

    /// <summary>One world node as the map sees it. Positions are km, matching Core; the client
    /// scales to SE metres on ingest.</summary>
    public struct WireMapNode
    {
        public string Id;
        public string DisplayName;
        /// <summary>The bare subtype of the ore that dominates this node's deposit ("Iron", "Ice",
        /// "Platinum"), or empty when the node has no ore under it. Replaces the retired
        /// <c>NodeKind</c> ordinal: the ground is what a node is, so the client is told what is
        /// actually there rather than which of eight labels a die once picked. See FAC-102.</summary>
        public string DominantOre;
        /// <summary>Whether this node's ground has been sampled at all. Distinguishes the two states
        /// an empty <see cref="DominantOre"/> otherwise collapses: sampled and barren (there is
        /// nothing here) versus never sampled (nobody has looked). Since FAC-101 a deposit is surveyed
        /// across many ticks, so "not yet sampled" is a common transient rather than an edge case, and
        /// the two drive opposite decisions — do not bother, versus go and look.</summary>
        public bool Surveyed;
        /// <summary>Which capabilities stand on this node, one bit per <c>NodeCapabilityKind</c>
        /// ordinal. A bitmask rather than the named list the assets and detail payloads carry: the
        /// map sends every node the viewer knows, and it only needs enough to badge a marker.
        /// Sixteen bits since protocol 50, when the food chain took ordinals 9 and 10.</summary>
        public ushort CapabilityMask;
        /// <summary>Matches the server's <c>NodeZone</c> ordinal (DeepSpace/Orbit/Surface).</summary>
        public byte Zone;
        public string OwnerTag;
        /// <summary>Matches <see cref="WireRelation"/>, resolved for the receiving player.</summary>
        public byte Relation;
        public bool HasStation;
        public bool HasInfrastructure;
        public double XKm;
        public double YKm;
        public double ZKm;
        public double RadiusKm;
        public double HoldStrength;
        /// <summary>Matches <see cref="WireKnowledge"/>. A node the viewer does not know is never
        /// sent at all, so this only ever distinguishes remembered from currently-detected.</summary>
        public byte Knowledge;
    }

    /// <summary>What the client is asking the Expand feature to do. Subscribe and commit share one
    /// message because they share one subject — the player's own position — and a commit that carried
    /// its own point would be a second, client-supplied source of truth for it.</summary>
    public enum WireExpandAction : byte
    {
        /// <summary>Stop sending the readout.</summary>
        Unsubscribe = 0,
        /// <summary>Start sending the readout on the server's own cadence.</summary>
        Subscribe = 1,
        /// <summary>Create the node. The server re-derives everything from the player's live
        /// position; the request's quote id only proves the client acted on a status it was actually
        /// sent.</summary>
        Commit = 2,
    }

    /// <summary>Why an Expand did not happen. Mirrors the server's own <c>PlayerExpandRefusal</c>;
    /// <see cref="Ok"/> is the success case, and the only one on a readout tick.</summary>
    public enum WireExpandVerdict : byte
    {
        Ok = 0,
        Occupied = 1,
        EconomyUnavailable = 2,
        ChargeRefused = 3,
        NoFaction = 4,
        /// <summary>The caller is not a leader of their faction; expanding spends its treasury.</summary>
        NotLeader = 5,
    }

    /// <summary>The placement verdict at one player's own position — a readout tick, or the answer
    /// to a commit. One shape for both, so the client never reconciles two.</summary>
    public struct WireExpandStatus
    {
        /// <summary>Identifies this quote. The client echoes it on a commit and the server refuses a
        /// commit that does not match the status it last sent, so a window left open across a flight
        /// cannot buy the verdict it is still showing.</summary>
        public int QuoteId;
        /// <summary>Nothing sits inside <see cref="SeparationKm"/> of where the node would land.</summary>
        public bool Clear;
        /// <summary>Matches the server's <c>NodeZone</c> ordinal, resolved where the node would land
        /// rather than where the ship is.</summary>
        public byte Zone;
        /// <summary>The separation in force at the landing point, km.</summary>
        public double SeparationKm;
        public double PlacedXKm;
        public double PlacedYKm;
        public double PlacedZKm;
        /// <summary>How far the landing point is from the player, km. Over a planet the node lands on
        /// terrain, which can be a long way down, and that has to be visible before payment.</summary>
        public double SnapDistanceKm;
        public long CostCredits;
        /// <summary>Always false: every Expand is charged to the faction's own account. Kept on the
        /// wire so the protocol does not move; an older client reads it as "faction" too.</summary>
        public bool ChargedToPlayer;
        /// <summary>This is the answer to a commit, not a readout tick.</summary>
        public bool Committed;
        public WireExpandVerdict Verdict;
        /// <summary>The node created, on a successful commit; empty otherwise.</summary>
        public string NodeId;
    }

    /// <summary>How the viewer knows a node. Unknown has no wire value on purpose: it is expressed
    /// by the node's absence, so the client is never handed something it must remember not to draw.</summary>
    public enum WireKnowledge : byte
    {
        /// <summary>Seen before, not seen now. Identity and position are current (a node does not
        /// move); owner and standing are last-known and may be stale. Draw it dim.</summary>
        Discovered = 0,
        /// <summary>Inside sensor coverage right now. Every field is live.</summary>
        Detected = 1,
    }

    /// <summary>A faction the map knows about, for the legend and colour assignment.</summary>
    public struct WireMapFaction
    {
        public string Tag;
        /// <summary>Matches <see cref="WireRelation"/>, resolved for the receiving player.</summary>
        public byte Relation;
        public int NodeCount;
    }

    /// <summary>One routed leg of a fleet's itinerary.</summary>
    public struct WireMapStop
    {
        public double XKm;
        public double YKm;
        public double ZKm;
        /// <summary>Matches the server's <c>FleetStopKind</c> ordinal.</summary>
        public byte Kind;
    }

    /// <summary>
    /// A fleet in transit. Carries velocity as well as position so the client can dead-reckon
    /// between the server's ~5 s updates: the easing curve that produces the position lives in
    /// <c>FactionTuning.TransitProgressRatio</c> on the server, and duplicating it here would put
    /// one rule in two places. Missing an update degrades to a straight line, not a jump.
    /// </summary>
    public struct WireMapFleet
    {
        public string Key;
        /// <summary>Matches the server's <c>ActionKind</c> ordinal.</summary>
        public byte Kind;
        public string OwnerTag;
        /// <summary>Matches <see cref="WireRelation"/>, resolved for the receiving player.</summary>
        public byte Relation;
        public double XKm;
        public double YKm;
        public double ZKm;
        public double VelXKmPerMin;
        public double VelYKmPerMin;
        public double VelZKmPerMin;
        public double TargetXKm;
        public double TargetYKm;
        public double TargetZKm;
        /// <summary><see cref="DateTime.Ticks"/>, UTC.</summary>
        public long EtaUtcTicks;
        public int ConvoySize;
        /// <summary>Non-zero once a physical grid exists, so a client with the entity streamed can
        /// track it at frame rate instead of at the server's cadence.</summary>
        public long EntityId;
        /// <summary>The clearance polyline still ahead of the current destination. Null or empty
        /// for a fleet flying a direct leg, and for a one-way mission. Legs the server has not
        /// routed yet are deliberately absent: they have no waypoints, so the only line drawable
        /// for them is a straight one through whatever body lies between.</summary>
        public WireMapStop[] RoutedStops;
    }

    /// <summary>
    /// What a station's protection is doing. Mirrors <c>Factions.Core.PlayerSiege.ZoneState</c> by
    /// ordinal — the host casts one onto the other — so neither may be renumbered.
    /// </summary>
    public enum WireZoneState : byte
    {
        /// <summary>Nothing founded here, so there is no protection to have a state.</summary>
        NoStation = 0,
        /// <summary>Intact.</summary>
        Protected = 1,
        /// <summary>A siege is running and the shield is still up.</summary>
        Besieged = 2,
        /// <summary>The shield is down and the station itself is being shot.</summary>
        Breached = 3,
        /// <summary>The raid has met its criterion; the conquer clock is running.</summary>
        Raided = 4,
        /// <summary>No siege, but the zone is off after a raid landed.</summary>
        Down = 5,
    }

    /// <summary>
    /// One node that is under attack or unprotected right now.
    ///
    /// <para><b>Why this rides the fleet message and not the node one.</b> A player shooting a
    /// station produces no fleet, so nothing in <see cref="WireMapFleet"/> can carry it; and
    /// <see cref="WireMapNode"/> is a full-state broadcast of every node the viewer knows, sent on
    /// a lazy cadence roughly twelve times slower than fleets. Fields there would be paid for by
    /// ~2600 nodes to describe a state that is usually true of none, and a breach countdown would
    /// arrive up to a minute stale. This list is normally empty and moves at the fleet cadence,
    /// which is the rate the thing it describes actually changes at.</para>
    /// </summary>
    public struct WireMapAlarm
    {
        public string NodeId;
        /// <summary>A <see cref="WireZoneState"/>. Never <c>NoStation</c> or <c>Protected</c>:
        /// those states send no row at all.</summary>
        public byte State;
        /// <summary>Shield remaining, 0..1. Frozen once breached — phase 2 is a fight with the
        /// station, not with the shield.</summary>
        public double ZoneIntegrity;
        /// <summary>Block damage as a fraction of what the raid needs, 0..1. Stays 0 before the
        /// breach, when nothing can be damaged.</summary>
        public double RaidProgress;
        /// <summary><see cref="DateTime.Ticks"/>, UTC: the earliest the shield can break while
        /// <c>Besieged</c>, the moment protection returns while <c>Down</c>, 0 otherwise. A breach
        /// also needs the pool empty at that moment, so a countdown reaching zero is not the same
        /// as the zone having dropped.</summary>
        public long UntilUtcTicks;
    }

    /// <summary>One installed capability on an owned node, and its level.</summary>
    public struct WireAssetCapability
    {
        /// <summary>Matches the server's <c>NodeCapabilityKind</c> name, not its ordinal: the tab
        /// shows this text, and a name survives a reordered enum where an ordinal would not.</summary>
        public string Cap;
        public int Level;
    }

    /// <summary>
    /// One node the viewing player's faction holds. Deliberately not <see cref="WireMapNode"/>:
    /// the map needs every node at low detail and a viewer-relative standing, this needs the few
    /// the player owns at the detail the chat command `!factions station status` already prints.
    /// </summary>
    public struct WireAssetNode
    {
        public string Id;
        public string DisplayName;
        /// <summary>The bare subtype of the ore that dominates this node's deposit ("Iron", "Ice",
        /// "Platinum"), or empty when the node has no ore under it. Replaces the retired
        /// <c>NodeKind</c> ordinal: the ground is what a node is, so the client is told what is
        /// actually there rather than which of eight labels a die once picked. See FAC-102.</summary>
        public string DominantOre;
        /// <summary>Whether this node's ground has been sampled at all. Distinguishes the two states
        /// an empty <see cref="DominantOre"/> otherwise collapses: sampled and barren (there is
        /// nothing here) versus never sampled (nobody has looked). Since FAC-101 a deposit is surveyed
        /// across many ticks, so "not yet sampled" is a common transient rather than an edge case, and
        /// the two drive opposite decisions — do not bother, versus go and look.</summary>
        public bool Surveyed;
        /// <summary>Matches the server's <c>NodeZone</c> ordinal (DeepSpace/Orbit/Surface).</summary>
        public byte Zone;
        /// <summary>False while the node is claimed but not yet founded — there is no station
        /// there to fly to, only a claim.</summary>
        public bool HasStation;
        public double XKm;
        public double YKm;
        public double ZKm;
        public double HoldStrength;
        public double ResourceValue;
        /// <summary>Measured ore density, 0..1 — what the node's mint and drill rate actually scale
        /// by. Sent beside <see cref="ResourceValue"/> rather than folded into it because the two
        /// answer different questions and, live, disagree: a thick ice seam is worth little and
        /// mines fast. See docs/issues/FAC-219.</summary>
        public double Richness;
        /// <summary>Credits founding a station here would cost. Zero once
        /// <see cref="HasStation"/> is true — there is nothing left to buy.</summary>
        public long FoundCost;
        /// <summary>Ships built here and not yet dispatched or sold.</summary>
        public int ShipsReady;
        /// <summary>Builds currently on this node's shipyard queue.</summary>
        public int BuildsQueued;
        /// <summary>Damaged hulls waiting here for repair (<c>SpatialNode.DamagedHulls</c>).</summary>
        public int ShipsInRepair;
        public bool ShipyardPaused;
        /// <summary>Null or empty for a node with nothing installed.</summary>
        public WireAssetCapability[] Capabilities;
    }

    /// <summary>One of the viewing faction's convoys in transit. A trimmed
    /// <see cref="WireMapFleet"/>: the tab lists where it is going and when it lands, and has no
    /// use for the dead-reckoning terms a moving map marker needs.</summary>
    /// <summary>One entry in the Events feed. Structured rather than a rendered sentence: the
    /// client filters and sorts on these fields, and only it knows how much room the row has.
    /// Node names are resolved server-side because the client has no graph of its own.</summary>
    public struct WireFactionEvent
    {
        /// <summary><see cref="DateTime.Ticks"/>, UTC.</summary>
        public long UtcTicks;
        /// <summary>Matches the server's <c>FactionEventKind</c> byte value. An unknown value is a
        /// newer server, not a corrupt feed — render it as unrecognised rather than dropping it.</summary>
        public byte Kind;
        public string ActorTag;
        /// <summary>The counterparty. Empty for a one-sided event such as a build.</summary>
        public string OtherTag;
        public string NodeId;
        /// <summary>Display name for <see cref="NodeId"/>, or empty when the node is gone or
        /// unnamed. The id is always sent; the name is a courtesy.</summary>
        public string NodeName;
        public long Amount;
        public double Ratio;
        /// <summary>True when the viewer's own faction is the one that acted. Resolved server-side
        /// so the client never has to compare tags to work out which end of a raid it is on.</summary>
        public bool ViewerIsActor;
    }

    public struct WireAssetFleet
    {
        public string Key;
        /// <summary>Matches the server's <c>ActionKind</c> ordinal.</summary>
        public byte Kind;
        public double XKm;
        public double YKm;
        public double ZKm;
        /// <summary>Node the convoy left, and the one it is heading for, already resolved to
        /// display names server-side. Either may be empty when the leg is a bare coordinate.</summary>
        public string OriginName;
        public string DestinationName;
        public double TargetXKm;
        public double TargetYKm;
        public double TargetZKm;
        /// <summary><see cref="DateTime.Ticks"/>, UTC.</summary>
        public long EtaUtcTicks;
        public int ConvoySize;
        /// <summary>Non-zero once a physical grid exists.</summary>
        public long EntityId;
    }

    /// <summary>One grid the viewing player, or a faction-mate, owns. Enumerated from the live
    /// world rather than the faction graph: a player-built ship exists in SE and nowhere in the
    /// world model.</summary>
    public struct WireAssetGrid
    {
        public long EntityId;
        public string Name;
        public bool IsStatic;
        /// <summary>0 = large grid, 1 = small grid — matching SE's own <c>MyCubeSize</c>.</summary>
        public byte GridSize;
        public int BlockCount;
        public double XKm;
        public double YKm;
        public double ZKm;
        /// <summary>True when the viewing player owns it personally; false when a faction-mate
        /// does. Both are listed — the tab distinguishes them rather than hiding either.</summary>
        public bool OwnedPersonally;
        /// <summary>Display name of the owning identity. Empty when it is the viewer's own.</summary>
        public string OwnerName;
    }

    /// <summary>One ore of a node's deposit as the station window's Ground panel shows it — the
    /// server's <c>OreBreakdown</c>, carried as measured.</summary>
    public struct WireNodeOre
    {
        /// <summary>Bare SE ore subtype ("Iron", "Ice").</summary>
        public string SubTypeId;
        /// <summary>Share of the deposit, 0..1.</summary>
        public double Share;
        /// <summary>Worth of a unit of this ore in multiples of the world's ordinary ore; 0 unpriced.</summary>
        public double PriceRatio;
        /// <summary>Share of the ground's worth that comes from this ore, 0..1; 0 unpriced.</summary>
        public double WorthShare;
        /// <summary>False while the share is still under the survey's noise floor.</summary>
        public bool Resolved;
    }

    /// <summary>One item in a node's own stock: what is routable, and what has been moved aside for
    /// a capability install. The two are separate numbers server-side and are kept separate here —
    /// a player looking at 400 Superconductor needs to know none of it is spendable.</summary>
    public struct WireNodeStockItem
    {
        /// <summary>SE SubTypeId, e.g. "SteelPlate".</summary>
        public string Key;
        /// <summary>Routable stock.</summary>
        public long Quantity;
        /// <summary>Earmarked for a capability install; not part of <see cref="Quantity"/>.</summary>
        public long Reserved;
        /// <summary>What this station's store sells of <see cref="Quantity"/> right now, in inventory
        /// units — the same row a refuel or rearm at its terminal draws on. 0 when
        /// <see cref="WireNodeDetail.HasStore"/> is false.</summary>
        public long ForSale;
        /// <summary>What the station keeps back from sale for its own needs, in inventory units; 0
        /// when it keeps nothing back. Not its whole stock target, which also counts what it sells.</summary>
        public long Target;
        /// <summary>A <see cref="WireStockKeptFor"/>: why <c>Quantity - ForSale</c> is withheld.</summary>
        public byte KeptFor;
    }

    /// <summary>Why a station withholds part of its stock from sale. Mirrors
    /// <c>Factions.Core.StockKeptFor</c>.</summary>
    public enum WireStockKeptFor : byte
    {
        /// <summary>Nothing withheld.</summary>
        None = 0,
        /// <summary>Ship and install bills owed at this station.</summary>
        Owed = 1,
        /// <summary>What the station itself consumed recently — production, and fuel for the hulls
        /// that depart from it.</summary>
        Use = 2,
        /// <summary>No longer sent: a store does not hold stock back for its own sales.</summary>
        Sales = 3,
        /// <summary>The minimum every gas holder keeps.</summary>
        GasFloor = 4,
        /// <summary>The minimum kept of an item something here wants.</summary>
        Floor = 5,
        /// <summary>More than one store row may carry.</summary>
        ListingCap = 6,
        /// <summary>Not an item this store trades.</summary>
        NotTraded = 7,
        /// <summary>The station has no store.</summary>
        NoStore = 8,
    }

    /// <summary>One line of the Withdraw/Deposit listing. Distinct from <see cref="WireNodeStockItem"/>
    /// because the two answer different questions: the node window asks what a node HOLDS, this asks
    /// what a player standing at it may TAKE. <see cref="Available"/> therefore has every reserve
    /// already deducted server-side, so a client can never offer an amount the server would refuse.
    ///
    /// <para><see cref="Reserved"/> is carried anyway, and shown: stock a build has claimed is
    /// exactly what a player wondering where their ore went needs to see, and hiding it would read
    /// as the node being empty.</para></summary>
    public struct WireNodeTransferRow
    {
        /// <summary>Inventory key, e.g. "Ore:Iron" or "Comp:SteelPlate".</summary>
        public string Key;
        /// <summary>Display name; empty means show the key. Resolved server-side — the client has no
        /// item registry to look one up in.</summary>
        public string Label;
        /// <summary>Withdrawable right now, reserves already removed.</summary>
        public long Available;
        /// <summary>Held back by an active build or a capability install. Not part of
        /// <see cref="Available"/> and never withdrawable.</summary>
        public long Reserved;
    }

    /// <summary>One BOM component of one build, with the places its shortfall could be answered
    /// from. Mirrors <c>Factions.Core.BuildSupply.ComponentRow</c>, which computes all of it; the
    /// server sends the rows already ordered, outstanding first.</summary>
    public struct WireBuildComponent
    {
        /// <summary>Inventory key, e.g. "Comp:SteelPlate".</summary>
        public string Key;
        /// <summary>Units the BOM calls for, and how many the build has absorbed.</summary>
        public long Need;
        public long Have;
        /// <summary>Routable stock of this key standing at the build's own node.</summary>
        public long AtNode;
        /// <summary>Routable stock of this key across the faction's other nodes.</summary>
        public long Elsewhere;
        /// <summary>Aboard a convoy bound for this node, or booked into one of its later pickups.</summary>
        public long EnRoute;
        /// <summary>The faction's last Operations pass found no internal source for this key here.</summary>
        public bool Unserved;
    }

    /// <summary>One raw or intermediate input a build has earmarked toward producing a component it
    /// is still short of — <c>BuildQueueEntry.ReservedInputs</c>. Not a BOM key: it is what the
    /// recipe eats, which is why it cannot be folded into <see cref="WireBuildComponent"/>.</summary>
    public struct WireBuildReservedInput
    {
        public string Key;
        public long Quantity;
    }

    /// <summary>One entry on a node's shipyard queue: the summary numbers the queue table shows,
    /// and the per-component breakdown behind them. Only the first <c>SpatialNode.BuildSlotCount()</c> of
    /// a yard's queue are actually worked, so <see cref="Working"/> is what separates a build that
    /// is collecting nothing from one that is merely next in line.</summary>
    public struct WireNodeBuild
    {
        /// <summary>Entry id, e.g. "Trade:001" — the same token the chat verbs take.</summary>
        public string Id;
        public string Role;
        /// <summary>Empty for a free build (no BOM).</summary>
        public string PrefabId;
        /// <summary>Summed BOM, and how much of it the entry has absorbed.</summary>
        public long BomTotal;
        public long Collected;
        public int Priority;
        /// <summary><see cref="DateTime.Ticks"/>, UTC.</summary>
        public long ExpiresUtcTicks;
        /// <summary>How long the entry has gone without absorbing anything.</summary>
        public double StalledSeconds;
        /// <summary><see cref="DateTime.Ticks"/>, UTC, when the entry was queued.</summary>
        public long CreatedUtcTicks;
        /// <summary>This entry holds one of the yard's working slots. A waiting entry absorbs
        /// nothing and reserves nothing, however short of components it looks.</summary>
        public bool Working;
        /// <summary>How many entries are ahead of it in the yard's claim order. 0 for a working
        /// build.</summary>
        public int WaitingBehind;
        /// <summary>Every BOM component, outstanding rows first. Null or empty for a free build.</summary>
        public WireBuildComponent[] Bom;
        /// <summary>Null or empty when nothing is staged toward this build.</summary>
        public WireBuildReservedInput[] ReservedInputs;
    }

    /// <summary>Where a row of a node's repair queue stands. Mirrors the server's
    /// <c>RepairState</c>.</summary>
    public enum WireRepairState : byte
    {
        /// <summary>The yard is still collecting the repair bill.</summary>
        WaitingForParts = 0,
        /// <summary>The parts are in; the hull rejoins stock at
        /// <see cref="WireNodeRepair.DoneUtcTicks"/>.</summary>
        Repairing = 1,
        /// <summary>The node has no Shipyard, so nothing here is repaired.</summary>
        NoShipyard = 2,
        /// <summary>The node's yard is paused.</summary>
        YardPaused = 3,
    }

    /// <summary>One row of a node's repair queue (<c>SpatialNode.DamagedHulls</c>): one damaged
    /// hull, or <see cref="Count"/> identical ones, in the order the yard serves them -- smallest
    /// outstanding bill first. The bill rows reuse <see cref="WireBuildComponent"/>, so the Builds
    /// window's component panel reads a repair exactly as it reads a build.</summary>
    public struct WireNodeRepair
    {
        public string PrefabId;
        /// <summary>Identical hulls folded into this row.</summary>
        public int Count;
        /// <summary>1-based served position of the row's first hull.</summary>
        public int Position;
        /// <summary>0..1, what the hull came home with.</summary>
        public double Integrity;
        /// <summary>A <see cref="WireRepairState"/> ordinal.</summary>
        public byte State;
        /// <summary>Per hull: the whole repair bill, and how much of it is absorbed.</summary>
        public long BillTotal;
        public long Collected;
        /// <summary><see cref="DateTime.Ticks"/>, UTC.</summary>
        public long QueuedUtcTicks;
        /// <summary>When the parts came in, and when the hull rejoins stock; 0 while it waits for
        /// parts.</summary>
        public long PartsCompleteUtcTicks;
        public long DoneUtcTicks;
        /// <summary>Every line of the bill, outstanding first. Null or empty for a whole bill.</summary>
        public WireBuildComponent[] Bill;
    }

    /// <summary>Ships standing at a node, by prefab. <see cref="ListedForSale"/> is a slice of
    /// <see cref="Ready"/>, not a separate pile — matching <c>SpatialNode.ListedForSale</c>'s own
    /// invariant.</summary>
    public struct WireNodeShip
    {
        public string PrefabId;
        public int Ready;
        public int ListedForSale;
    }

    /// <summary>Why an install cannot happen right now. Mirrors the server's
    /// <c>DevelopStationResult</c>, minus the two structural refusals a window can never present
    /// (a node that does not exist, and a node the viewer does not own — neither reaches this
    /// payload at all, because the whole develop menu sits past the ownership seam).</summary>
    public enum WireDevelopBlock : byte
    {
        /// <summary>Nothing is in the way; the install would go through.</summary>
        None = 0,
        /// <summary>Already at this capability's level ceiling.</summary>
        AtMaxLevel = 1,
        /// <summary>The prerequisite capability is installed nowhere in the faction's network.
        /// <see cref="WireNodeCapabilityOption.BlockKey"/> names it.</summary>
        PrerequisiteMissing = 2,
        /// <summary>Treasury is short. <see cref="WireNodeCapabilityOption.BlockHave"/> and
        /// <see cref="WireNodeCapabilityOption.BlockNeed"/> are credits.</summary>
        InsufficientTreasury = 3,
        /// <summary>A component or bulk item is short. <see cref="WireNodeCapabilityOption.BlockKey"/>
        /// names it.</summary>
        InsufficientStock = 4,
        /// <summary>The node is claimed but has no founded station yet.</summary>
        NotFounded = 5,
    }

    /// <summary>Why a station cannot be founded on this claim right now. Mirrors the server's
    /// <c>FoundStationResult</c>, minus the two structural refusals a window can never present (a
    /// node that does not exist, and a node the viewer does not own — neither reaches this payload
    /// at all, because founding sits past the same ownership seam the develop menu does).</summary>
    public enum WireFoundBlock : byte
    {
        /// <summary>Nothing is in the way; the purchase would go through.</summary>
        None = 0,
        /// <summary>The node already carries a station.</summary>
        AlreadyFounded = 1,
        /// <summary>Treasury is short. <see cref="WireNodeDetail.FoundHave"/> against
        /// <see cref="WireNodeDetail.FoundCost"/>, both credits.</summary>
        InsufficientTreasury = 2,
    }

    /// <summary>
    /// One row of the develop menu: a capability, where it stands at this node, what installing the
    /// next level would cost, and what is stopping it. Sent for every capability rather than only
    /// the installable ones — "you cannot build a Shipyard until a Manufactory exists somewhere" is
    /// the answer a player is asking the menu for, and omitting the row would not give it.
    ///
    /// <para><see cref="Reserved"/> and <see cref="Priority"/> are faction-wide, not per-node: they
    /// live on <c>FactionState</c> and apply to every install of that capability anywhere in the
    /// faction. They ride this per-node row because the capability is what they are keyed on, and
    /// this is the only place the client sees one.</para>
    /// </summary>
    public struct WireNodeCapabilityOption
    {
        /// <summary>Capability enum name, e.g. "RefineryII" — a name rather than an ordinal, for the
        /// reason <see cref="WireAssetCapability.Cap"/> is one.</summary>
        public string Cap;
        /// <summary>Level installed at this node, and this capability's ceiling.</summary>
        public int Level;
        public int MaxLevel;
        /// <summary>Credits the next install would cost, or 0 when the gate stops before the
        /// price.</summary>
        public long Cost;
        /// <summary>A <see cref="WireDevelopBlock"/> ordinal.</summary>
        public byte Block;
        /// <summary>Names the missing prerequisite or the short item, per <see cref="Block"/>. Empty
        /// when the block is named by its numbers alone, or when there is no block.</summary>
        public string BlockKey;
        public long BlockHave;
        public long BlockNeed;
        /// <summary>Faction-wide: whether <c>ProductionManager</c> may stage materials toward this
        /// capability on the faction's behalf (<c>FactionState.ReservationOptIn</c>).</summary>
        public bool Reserved;
        /// <summary>Faction-wide: how this capability's install ranks against ship builds competing
        /// for the same components (<c>FactionState.InstallPriority</c>). 0 is the default, where
        /// builds win.</summary>
        public int Priority;
    }

    /// <summary>
    /// One node, at the detail a window can show and neither list wants to carry. Sent only to a
    /// client that asked for this node by id, and only while its window is open.
    ///
    /// <see cref="Owned"/> is the privacy seam: everything above it is what the map already shows
    /// for any node, and everything below it is sent only when the viewer's own faction holds the
    /// node. Filtering happens server-side, so a client is never handed detail it must be trusted
    /// not to display.
    /// </summary>
    public struct WireNodeDetail
    {
        /// <summary>False when the server has no such node — a window opened on a node that was
        /// abandoned or renamed away says so rather than sitting blank.</summary>
        public bool Exists;

        public string Id;
        public string DisplayName;
        /// <summary>The bare subtype of the ore that dominates this node's deposit ("Iron", "Ice",
        /// "Platinum"), or empty when the node has no ore under it. Replaces the retired
        /// <c>NodeKind</c> ordinal: the ground is what a node is, so the client is told what is
        /// actually there rather than which of eight labels a die once picked. See FAC-102.</summary>
        public string DominantOre;
        /// <summary>Whether this node's ground has been sampled at all. Distinguishes the two states
        /// an empty <see cref="DominantOre"/> otherwise collapses: sampled and barren (there is
        /// nothing here) versus never sampled (nobody has looked). Since FAC-101 a deposit is surveyed
        /// across many ticks, so "not yet sampled" is a common transient rather than an edge case, and
        /// the two drive opposite decisions — do not bother, versus go and look.</summary>
        public bool Surveyed;
        /// <summary>Matches the server's <c>NodeZone</c> ordinal (DeepSpace/Orbit/Surface).</summary>
        public byte Zone;
        /// <summary>Empty for an unclaimed node.</summary>
        public string OwnerTag;
        /// <summary>Matches <see cref="WireRelation"/>, resolved for the receiving player.</summary>
        public byte Relation;
        public bool HasStation;
        public double XKm;
        public double YKm;
        public double ZKm;
        public double HoldStrength;

        /// <summary>A <see cref="WireZoneState"/>: what this station's protection is doing.
        ///
        /// <para><b>Above the ownership seam on purpose.</b> A safe zone is a bubble the player can
        /// see from outside it, so withholding its state from a non-owner hides nothing anyone
        /// standing there does not already know — while a defender reading their own window during
        /// a raid needs it most.</para></summary>
        public byte ZoneState;
        /// <summary>Shield remaining, 0..1. 1 for a station nobody is shooting.</summary>
        public double ZoneIntegrity;
        /// <summary>Block damage as a fraction of what the raid needs, 0..1.</summary>
        public double ZoneRaidProgress;
        /// <summary><see cref="DateTime.Ticks"/>, UTC: the earliest the shield can break while
        /// besieged, the moment protection returns while down, 0 otherwise.</summary>
        public long ZoneUntilUtcTicks;

        /// <summary>True when the viewing player's faction holds this node. Everything below is
        /// default or empty when it is false.</summary>
        public bool Owned;

        public double ResourceValue;
        /// <summary>Measured ore density, 0..1. See <see cref="WireAssetNode.Richness"/>.</summary>
        public double Richness;
        /// <summary>What founding a station on this claim costs, and the treasury that would pay
        /// for it. Both zero once <see cref="HasStation"/> is true.</summary>
        public long FoundCost;
        public long FoundHave;
        /// <summary>A <see cref="WireFoundBlock"/> ordinal.</summary>
        public byte FoundBlock;
        public bool ShipyardPaused;
        /// <summary>True when this station runs a player store, so <see cref="WireNodeStockItem.ForSale"/>
        /// means something.</summary>
        public bool HasStore;
        /// <summary>Whole people living here, and the beds the Habitat has for them. Both zero
        /// without a Habitat. FAC-493.</summary>
        public int Workers;
        public int WorkerBeds;
        /// <summary>What one recruit costs here, or 0 when this node's body has no civilian pool to
        /// recruit from — the window offers the Recruit button only when it is above zero.</summary>
        public long RecruitPrice;
        /// <summary>Hours the node's food keeps its workers, and hours its oxygen does; each below
        /// zero when it is not needed — nobody to feed, or air the workers breathe without a
        /// bottle.</summary>
        public double WorkerFoodHours;
        public double WorkerAirHours;
        /// <summary>How far the survey has got, 0..1: the term that moves <see cref="ResourceValue"/>
        /// and <see cref="Richness"/> from their floors toward <see cref="ResourceValueTarget"/> and
        /// <see cref="RichnessTarget"/>. Zero for a node with no ore under it.</summary>
        public double SurveyConfidence;
        /// <summary>What the two readings converge on once the survey completes — what the ground
        /// has measured so far.</summary>
        public double ResourceValueTarget;
        public double RichnessTarget;
        /// <summary>Smallest share the survey can tell from noise so far, 0..1; 1 before it starts.</summary>
        public double ResolvedShareFloor;
        /// <summary>The deposit ore by ore, heaviest first. Null or empty when there is no ore.</summary>
        public WireNodeOre[] Ores;
        /// <summary>Null or empty when nothing is installed.</summary>
        public WireAssetCapability[] Capabilities;
        /// <summary>Null or empty when the node holds nothing.</summary>
        public WireNodeStockItem[] Stock;
        /// <summary>Null or empty when nothing is queued.</summary>
        public WireNodeBuild[] Builds;
        /// <summary>Null or empty when no ships are standing here.</summary>
        public WireNodeShip[] Ships;
        /// <summary>Every capability as a develop-menu row, installed or not. Null or empty only
        /// when the node has no founded station to install into.</summary>
        public WireNodeCapabilityOption[] Develop;

        /// <summary>True when the owner has asked for this node's hold to be maintained while they
        /// are away — <c>FactionState.ReinforceOptIn</c>. False for an NPC-owned node and for one
        /// nobody has opted in.</summary>
        public bool HoldOptIn;
        /// <summary>Rank this node is served at against the faction's other held nodes; higher
        /// first. Meaningless when <see cref="HoldOptIn"/> is false.</summary>
        public int HoldRank;
        /// <summary>What one top-up costs, so the window can price the button it is offering.</summary>
        public long HoldCost;
        /// <summary>The faction's three reinforce spend knobs as they RESOLVE — its own where it set
        /// one, the world's otherwise. Display only: they are faction-wide, and setting them needs a
        /// fractional value the command request's int cannot carry, so the chat verb owns writing
        /// them.</summary>
        public double HoldBudgetIncomeFraction;
        public double HoldBudgetTreasuryFraction;
        public long HoldBudgetFloor;
        /// <summary>Which of the three above the faction set itself, so the window can mark them:
        /// bit 0 income, bit 1 treasury, bit 2 floor.</summary>
        public byte HoldBudgetOverrideMask;

        /// <summary>The two trade switches — <c>FactionState.TradeBuyOptIn</c> and
        /// <c>TradeSellOptIn</c>. Faction-wide, carried on the node detail because that is the window
        /// the player is standing in. Both false for an NPC-owned node.</summary>
        public bool TradeBuyOptIn;
        public bool TradeSellOptIn;
        /// <summary>How many partners the projection ranked, and whether the faction owns a hull that
        /// can carry a trade.
        ///
        /// <para>Both are here because both are otherwise invisible: an opted-in faction that ranked
        /// nobody dispatches nothing and logs nothing, and a buy pass with no suitable hull returns
        /// without a counter. A window that showed only the switches would say "on" over a feature
        /// doing nothing.</para></summary>
        public int TradePartners;
        public bool TradeHasHull;
        /// <summary>Unserved rows this faction is publishing for a neighbour to serve. Zero while
        /// buying is off, which is the point: that is the flag that makes the shortage visible.</summary>
        public int TradeUnserved;
        /// <summary>In-flight trade convoys against the world's cap, so the window can show the other
        /// gate that silently ends a pass.</summary>
        public int TradeInFlight;
        public int TradeMaxFleets;
        /// <summary>The three trade spend knobs as they RESOLVE, and which the faction set itself,
        /// on the same terms as the hold block above.</summary>
        public double TradeBudgetIncomeFraction;
        public double TradeBudgetTreasuryFraction;
        public long TradeBudgetFloor;
        public byte TradeBudgetOverrideMask;

        /// <summary>The node's repair queue in served order, folded
        /// (<see cref="WireNodeRepair.Count"/>). Null or empty when no hull is in repair here.</summary>
        public WireNodeRepair[] Repairs;
        /// <summary>Hulls in repair here, every one -- the rows can be capped, this is not.</summary>
        public int RepairHulls;

        /// <summary>What this node actually refined and built recently, most recent first — the
        /// dashboard's production history for this one node. Null or empty when nothing ran in the
        /// window.</summary>
        public WireNodeProductionSpan[] Production;
        /// <summary>How far back <see cref="Production"/> reaches, in seconds. Sent rather than
        /// restated by the window so the caption cannot drift from the server's retention.</summary>
        public int ProductionWindowSeconds;

        /// <summary>What the owning faction wants at this node now — its <c>Item:</c> and
        /// <c>Ship:</c> demands pinned here, most urgent first. Null or empty when nothing is
        /// wanted.</summary>
        public WireNodeDemand[] Demand;
        /// <summary>Demands pinned here that ended inside <see cref="DemandWindowSeconds"/>, most
        /// recently ended first.</summary>
        public WireNodeEndedDemand[] EndedDemand;
        /// <summary>How far back <see cref="EndedDemand"/> reaches, in seconds; the server's
        /// retention, sent for the same reason as <see cref="ProductionWindowSeconds"/>.</summary>
        public int DemandWindowSeconds;
    }

    /// <summary>Why a demand was raised. Mirrors <c>Factions.Core.DemandClass</c> ordinal for
    /// ordinal, weakest to strongest.</summary>
    public enum WireDemandClass : byte
    {
        Unclassified = 0,
        Catalog = 1,
        StoreStock = 2,
        RecipeInput = 3,
        Fuel = 4,
        Install = 5,
        ShipBuild = 6,
        Upkeep = 7,
    }

    /// <summary>One live demand at a node.</summary>
    public struct WireNodeDemand
    {
        /// <summary>The register kind, e.g. "Item:Ingot:Iron" or "Ship:Trade".</summary>
        public string Kind;
        /// <summary>The node the demand is pinned to; empty for a faction-wide one. Carried only by
        /// the faction demand snapshot — in the node detail snapshot the node is the window's.</summary>
        public string AtNodeId;
        /// <summary>Where a <c>Ship:</c> demand's hull is bound; empty otherwise.</summary>
        public string DestNodeId;
        /// <summary>Units wanted; 0 is "unspecified", which every <c>Ship:</c> demand is.</summary>
        public long Quantity;
        /// <summary>True when the row names an item, so <see cref="Have"/> is a reading and not a
        /// default.</summary>
        public bool HasHave;
        /// <summary>What the node holds of the item.</summary>
        public long Have;
        /// <summary>Units of the item this node has used per hour, smoothed —
        /// <c>SpatialNode.DemandHistory</c>. 0 when it has used none.</summary>
        public double UsedPerHour;
        /// <summary>A <see cref="WireDemandClass"/>.</summary>
        public byte Class;
        /// <summary>Wanted to stock a store listing rather than for the faction's own use.</summary>
        public bool DestinedForSale;
        /// <summary><see cref="DateTime.Ticks"/>, UTC: when this demand was first posted. 0 when
        /// unknown.</summary>
        public long FirstPostedUtcTicks;
    }

    /// <summary>One demand that ended at a node.</summary>
    public struct WireNodeEndedDemand
    {
        public string Kind;
        /// <summary>As <see cref="WireNodeDemand.AtNodeId"/>.</summary>
        public string AtNodeId;
        public string DestNodeId;
        /// <summary>The largest quantity it was posted at.</summary>
        public long PeakQuantity;
        /// <summary>A <see cref="WireDemandClass"/>.</summary>
        public byte Class;
        public bool DestinedForSale;
        /// <summary>True when a manager retired it as met; false when it lapsed because nothing
        /// re-posted it.</summary>
        public bool Consumed;
        /// <summary><see cref="DateTime.Ticks"/>, UTC.</summary>
        public long FirstPostedUtcTicks;
        /// <summary><see cref="DateTime.Ticks"/>, UTC.</summary>
        public long EndedUtcTicks;
    }

    /// <summary>One span of real production at a node: a recipe consuming or producing one item,
    /// coalesced across consecutive batches so a recipe holding its slot for minutes is one growing
    /// row. Mirrors <c>Factions.Core.ProductionManager.ProductionHistoryEntry</c>, less the faction and
    /// node the snapshot already names.</summary>
    public struct WireNodeProductionSpan
    {
        /// <summary>The recipe's blueprint id, e.g. "SteelPlate".</summary>
        public string RecipeId;
        /// <summary>Inventory key of the item moved, e.g. "Ingot:Iron".</summary>
        public string Key;
        /// <summary>True for what the recipe produced, false for what it consumed.</summary>
        public bool IsOutput;
        /// <summary>Units moved across the whole span.</summary>
        public long Quantity;
        /// <summary><see cref="DateTime.Ticks"/>, UTC: the span's first batch.</summary>
        public long StartUtcTicks;
        /// <summary><see cref="DateTime.Ticks"/>, UTC: its most recent batch.</summary>
        public long EndUtcTicks;
    }

    /// <summary>What a <see cref="WireMessageKind.NodeCommandRequest"/> asks for. Every verb here
    /// acts on something the window is already showing and has a server-side entry point that
    /// re-checks ownership for itself. Rename and mine stay chat verbs: both need a free-text or
    /// spatial target the window has nowhere to take.
    ///
    /// <para>The request's target token is an entry id for the queue verbs and a capability name for
    /// the develop verbs — one field, because a command never needs both, and the server switches on
    /// the verb before it reads it.</para></summary>
    public enum WireNodeCommand : byte
    {
        None = 0,
        /// <summary>Stop this shipyard claiming components.</summary>
        QueuePause = 1,
        /// <summary>Let it collect again.</summary>
        QueueResume = 2,
        /// <summary>Cancel one entry, refunding what it holds. Uses the entry id.</summary>
        QueueCancel = 3,
        /// <summary>Rank one entry's claim. Uses the entry id and the value.</summary>
        QueuePriority = 4,
        /// <summary>Install the next level of one capability at this node, paying its treasury cost.
        /// Uses the capability name.</summary>
        Develop = 5,
        /// <summary>Flip whether ProductionManager may stage materials toward one capability on this
        /// faction's behalf. Faction-wide despite arriving with a node id. Uses the capability
        /// name.</summary>
        ReserveToggle = 6,
        /// <summary>Rank one capability's install against the ship builds competing for the same
        /// components. Faction-wide. Uses the capability name and the value.</summary>
        InstallPriority = 7,
        /// <summary>Flip whether this node's hold is maintained while nobody is online. Uses
        /// neither the target nor the value — the node id is the whole request.</summary>
        HoldToggle = 8,
        /// <summary>Rank this node against the faction's other held nodes. Uses the value.</summary>
        HoldRank = 9,
        /// <summary>Set one of the faction's three reinforce spend knobs. Faction-wide despite
        /// arriving with a node id, like the two develop verbs above. Uses the knob name in the
        /// target ("income", "treasury", "floor") and the value.
        ///
        /// <para><b>The two fractions travel as WHOLE PERCENT</b> — 30 means 0.30 — because the
        /// request carries an int and a budget share is a percentage in every place a player meets
        /// one. "floor" is plain credits. <b>-1 clears the knob back to the world's tuning</b>, which
        /// is also the only way to reach a default finer than a percent: the shipped treasury
        /// fraction is 0.001, and whole percent cannot express it.</para></summary>
        HoldBudget = 10,
        /// <summary>Found a station on this claim, paying its treasury cost. Uses the style name in
        /// the target ("standard" or "fortified"); an unrecognised or empty target means
        /// standard.</summary>
        Found = 11,
        /// <summary>Flip one direction of automated trade for this faction. Faction-wide despite
        /// arriving with a node id, like the two develop verbs and the three hold ones. Uses the
        /// direction name in the target ("buy", "sell" or "both") and the value as the new state:
        /// 1 on, 0 off.</summary>
        TradeToggle = 12,
        /// <summary>Set one of the faction's three trade spend knobs. Faction-wide. Uses the knob
        /// name in the target ("income", "treasury", "floor") and the value, on exactly the terms
        /// <see cref="HoldBudget"/> documents: <b>the two fractions travel as WHOLE PERCENT</b> and
        /// <b>-1 clears the knob back to the world's tuning</b>.</summary>
        TradeBudget = 13,
        /// <summary>Buy workers for this node's Habitat out of its body's civilian pool. Uses the
        /// value as the number asked for; the server trims it to the beds left and what the treasury
        /// buys. FAC-493.</summary>
        Recruit = 14,
    }

    /// <summary>One withdrawable ship asset: a (node, prefab) pair the caller's faction holds
    /// somewhere and may take a hull from at the terminal that asked.
    ///
    /// <para>The node's name and distance are resolved server-side and carried per row because a
    /// row is only meaningful with them: an asset at another station costs vanilla's transit time
    /// for that distance, so the row has to say where it is before the player pulls it. The client
    /// must not look either up in <c>MapComponent.World</c> — that cache stops refreshing when the
    /// map closes.</para>
    ///
    /// <para><see cref="Count"/> is already net of copies committed elsewhere: stock a store listing
    /// has claimed is not withdrawable and is never sent as a row.</para></summary>
    public struct WireShipAssetRow
    {
        public string NodeId;
        /// <summary>Display name; empty means show the id.</summary>
        public string NodeName;
        /// <summary>Catalogue prefab id — also the argument of the withdrawal that follows.</summary>
        public string PrefabId;
        /// <summary>Withdrawable hulls of this prefab at this node, listings already deducted.</summary>
        public int Count;
        /// <summary>Block and PCU counts of one hull, for the columns vanilla's stored-grid list
        /// already shows. Zero when the catalogue does not know them.</summary>
        public int Blocks;
        public int Pcu;
        /// <summary>"Large" or "Small"; empty when unknown.</summary>
        public string GridSize;
        /// <summary>From the terminal to the holding node. What vanilla's retrieval formula charges
        /// time for, so it is shown rather than left for the countdown to reveal.</summary>
        public double DistanceKm;
    }

    /// <summary>What a <see cref="WireMessageKind.DockRefuelRequest"/> fills. Hydrogen and oxygen
    /// are the two gases SE's store sells into a tank.</summary>
    public enum WireDockGas : byte
    {
        Hydrogen = 0,
        Oxygen = 1,
    }

    /// <summary>One magazine type the station sells and at least one gun on the ship takes, in a
    /// <see cref="WireMessageKind.RearmSurvey"/>.</summary>
    public struct WireRearmMagazine
    {
        /// <summary>Inventory key — for a magazine, its bare subtype.</summary>
        public string Key;
        public string Name;
        /// <summary>Credits per magazine, with the buyer's friendly discount already applied.</summary>
        public long UnitPrice;
        /// <summary>Magazines the station will sell: its store row's quantity.</summary>
        public long Available;
        /// <summary>Litres one magazine occupies in cargo.</summary>
        public double UnitLitres;
    }

    /// <summary>One magazine a gun group can load, and how many of it the group has room for.</summary>
    public struct WireRearmOption
    {
        public string Key;
        public long Fits;
    }

    /// <summary>The ship's guns of one block definition, in a <see cref="WireMessageKind.RearmSurvey"/>.
    /// Grouped rather than listed block by block because a ship carries its guns in batteries of one
    /// type, and a toggle per turret would be a list of twenty identical rows.</summary>
    public struct WireRearmGroup
    {
        /// <summary>The block definition id; the handle a <see cref="WireRearmPick"/> names.</summary>
        public string GroupId;
        public string Name;
        public int Blocks;
        /// <summary>The magazine most of the group has selected; empty when none has.</summary>
        public string CurrentKey;
        /// <summary>Every magazine the group takes that the station sells.</summary>
        public WireRearmOption[] Options;
    }

    /// <summary>Load this gun group with this magazine, in a <see cref="WireMessageKind.RearmRequest"/>.</summary>
    public struct WireRearmPick
    {
        public string GroupId;
        public string Key;
    }

    /// <summary>Put this many magazines of this key in the ship's cargo, in a
    /// <see cref="WireMessageKind.RearmRequest"/>.</summary>
    public struct WireRearmReserve
    {
        public string Key;
        public long Count;
    }

    /// <summary>One item on a dock-supply receipt: what landed where, and what it cost. Carried on
    /// the tail of <see cref="WireMessageKind.DockRefuelResult"/> and
    /// <see cref="WireMessageKind.RearmResult"/>.</summary>
    public struct WireReceiptLine
    {
        public string Key;
        public string Name;
        /// <summary>Into the ship's guns, or for gas into its tanks — litres then.</summary>
        public long Loaded;
        /// <summary>Into its cargo containers. Always 0 for gas.</summary>
        public long Cargo;
        /// <summary>Per priced unit: per magazine, or per 1,000 L of gas.</summary>
        public long UnitPrice;
        public long Credits;
    }

    /// <summary>What a <see cref="WireMessageKind.StationRefitCommandRequest"/> asks for. The four
    /// verbs the chat surface already has, in the same order a player meets them.</summary>
    public enum WireStationRefitCommand : byte
    {
        /// <summary>Queue the rebuild. Refused if something is already collecting here.</summary>
        Queue = 0,
        /// <summary>Queue it anyway, displacing whatever holds the node's working slot.</summary>
        Force = 1,
        /// <summary>Stand a finished rebuild up now rather than waiting for the station to
        /// empty.</summary>
        Apply = 2,
        /// <summary>Scan the station the caller is standing in as a new design. The only verb that
        /// reads the request's terminal, scope and name fields, and the only one that names no
        /// prefab.</summary>
        Scan = 3,
    }

    /// <summary>One station design this node could be rebuilt as. Every number is resolved
    /// server-side from the catalogue and the recipe registry — the client has neither.
    ///
    /// <para><see cref="NetBom"/> is the difference from the design the node currently stands as,
    /// not the target's gross bill, so the panel shows what the upgrade costs from here. It reuses
    /// <see cref="WireBuildComponent"/> rather than a shape of its own: the refit is an ordinary
    /// build once it is queued, and the window that then shows it reads exactly these rows.</para></summary>
    public struct WireStationRefitCandidate
    {
        /// <summary>Catalogue prefab id — also the argument of the refit that follows.</summary>
        public string Id;
        /// <summary>"vanilla" for a design from the content walk, "scanned" for a player
        /// capture.</summary>
        public string Kind;
        /// <summary>"Surface" or "Space". The one property a refit can never cross, so it is shown
        /// even though every candidate sent already matches the node.</summary>
        public string Site;
        public int BlockCount;
        /// <summary>Summed weapon-band DPS of the design, which is what the duel resolver reads once
        /// the rebuild lands.</summary>
        public float Dps;
        /// <summary>Book value of the design; becomes the node's collateral after the swap.</summary>
        public long Cost;
        /// <summary>Summed units of <see cref="NetBom"/>, or -1 when the design has no registered
        /// recipe at all. -1 is not zero: the first is a design nothing can price, the second one
        /// the node already stands as everything of.</summary>
        public long NetBomTotal;
        /// <summary>The design this node stands as right now.</summary>
        public bool Current;
        /// <summary>Every component the rebuild would have to collect. Null or empty when the net
        /// bill is empty or unpriceable. <c>Have</c> is filled in only for the design this node is
        /// already collecting toward; it is zero on every other row.</summary>
        public WireBuildComponent[] NetBom;
    }

    /// <summary>Everything the refit panel shows for one node: what it stands as, what it is
    /// becoming, and what it could become.</summary>
    public struct WireStationRefit
    {
        public string NodeId;
        /// <summary>Whether this viewer may refit this node at all. False for a node that does not
        /// exist, is not theirs, or has no founded station — with <see cref="Message"/> saying
        /// which, because an empty candidate list on its own reads as a world with no designs in
        /// it.</summary>
        public bool MayRefit;
        /// <summary>Why not, or a sentence about an empty list. Empty when there is nothing to
        /// say.</summary>
        public string Message;
        public string CurrentPrefabId;
        /// <summary>A rebuild that is paid for and waiting to be stood up, or empty.</summary>
        public string PendingPrefabId;
        /// <summary>"Surface" or "Space", measured for the node.</summary>
        public string Site;
        /// <summary>A siege is running here, so every refit is refused until it ends. Carried
        /// separately from <see cref="MayRefit"/> because the panel is still worth reading: the
        /// designs and their bills do not change while the walls are being shot at.</summary>
        public bool SiegeBlocked;
        /// <summary>Somebody is standing at this station, so a finished rebuild is being held rather
        /// than applied. What decides whether the panel offers Apply now as the next step or as a
        /// way to skip the wait.</summary>
        public bool Observed;
        /// <summary>The design a queued rebuild is collecting toward, or empty.</summary>
        public string CollectingPrefabId;
        public long CollectedUnits;
        public long CollectingBomUnits;
        /// <summary>Null or empty when this faction has no design for this node's site yet.</summary>
        public WireStationRefitCandidate[] Candidates;
    }

    /// <summary>One hull the Build dialog may offer at one yard, with the numbers a player needs to
    /// choose between them. Every field is resolved server-side against the prefab catalogue and the
    /// recipe registry — the client has neither, so it must not be left to derive any of this.
    ///
    /// <para>The row is per PREFAB, not per (prefab, role): the stats are a property of the hull and
    /// repeating them per role would send the same numbers four times. <see cref="Roles"/> carries
    /// which roles it fits, so the dialog narrows the hull combo by the chosen role from one
    /// list.</para></summary>
    public struct WireBuildPrefab
    {
        public string PrefabId;
        /// <summary>Display name, or empty to show the id.</summary>
        public string Label;
        /// <summary>Every queueable role this hull passes <c>ShipPool.IsSuitableForRole</c> for.
        /// Never empty — a hull that fits no role is not sent at all.</summary>
        public string[] Roles;
        public long CargoLiters;
        /// <summary>Summed across the hull's weapon bands. 0 for an unarmed hull.</summary>
        public double Dps;
        /// <summary>"Small" or "Large". Station designs are never sent.</summary>
        public string GridSize;
        /// <summary>Which thrust the hull actually mounts, pre-rendered ("atmo+hydro"), or "none"
        /// for a hull with no thrusters at all. A display string: the dialog shows it and never
        /// computes with it, and rendering it here keeps one spelling of the thruster types.</summary>
        public string Propulsion;
        /// <summary>Summed drill cross-section. 0 for a hull with no drills — the real gate on
        /// whether it can mine, which cargo capacity alone does not say.</summary>
        public double DrillYieldScore;
        /// <summary>Total units across the hull's BOM. 0 for a hull the recipe registry has no
        /// bill for, which is a real answer rather than a missing one.</summary>
        public long BomUnits;
        /// <summary>Whether this hull could actually leave THIS yard — the same degenerate
        /// same-point route check the doctrine's placement gate makes. False means a stranded grid
        /// and a wasted BOM, so the dialog says so rather than letting the player find out.
        ///
        /// <para>In <see cref="WireMessageKind.FleetDoctrineOptions"/> the list is not about one
        /// yard, so the field means <b>at least one owned yard</b>: a doctrine line is placed per
        /// yard and skips the ones this hull cannot leave, so "no yard at all" is the only case the
        /// new-line dialog can usefully warn about. Which yards those are is reported afterwards, on
        /// the line itself, by <see cref="WireDoctrineLine.RouteBlocked"/>.</para></summary>
        public bool CanFlyFromHere;
    }

    /// <summary>What a <see cref="WireMessageKind.FleetDoctrineCommandRequest"/> asks for. The two
    /// priority commands are relative on purpose, unlike <see cref="WireStandingOrderCommand.Priority"/>:
    /// the server reads the line's current rank and steps it, so a click against a row the snapshot
    /// has since moved cannot write back a rank the player never saw.</summary>
    public enum WireDoctrineCommand : byte
    {
        Remove = 0,
        PriorityUp = 1,
        PriorityDown = 2,
        Pause = 3,
        Resume = 4,
        /// <summary>Value is the new <c>TargetCount</c>.</summary>
        SetTarget = 5,
        /// <summary>Value is the new <c>MaxConcurrent</c>.</summary>
        SetConcurrency = 6,
        /// <summary>Not a line: the id field carries a design family and the value is how many of
        /// its hulls may be in refit at once (0 stops). See <see cref="WireHullRefitRow"/>.</summary>
        SetRefitCap = 7,
        /// <summary>Not a line: the id field carries a design (any version of it) and the value is
        /// how many parked hulls to dismantle, 0 for all of them. Applied at once; the client asks
        /// for confirmation first. A server that predates it refuses it as an unknown command.</summary>
        Decommission = 8,
    }

    /// <summary>One row of the Fleet window: a doctrine line and the live counts the shipyard
    /// manager itself evaluates it against. Every count is resolved server-side through the same
    /// Core helper the manager uses, so the window can never disagree with the manager about whether
    /// a line is satisfied.</summary>
    public struct WireDoctrineLine
    {
        public string Id;
        public int Priority;
        public bool Paused;
        /// <summary>The roles the line's hull is suitable for, "/"-joined ("Raid/Patrol") -- which
        /// demand pressure it answers. Display only: a line carries no role.</summary>
        public string Fits;
        public string PrefabId;
        public int TargetCount;
        public int MaxConcurrent;
        /// <summary>Empty for "any owned Shipyard node".</summary>
        public string ScopeNodeId;
        /// <summary>Resolved server-side: the client has no graph to look a name up in.</summary>
        public string ScopeNodeName;
        /// <summary>Parked in scope (less the store-listed slice), flying home to it, and in repair
        /// in it — what the line compares against <see cref="TargetCount"/>.</summary>
        public long Have;
        /// <summary>The flying half of <see cref="Have"/>, sent separately because "two of my four
        /// are out" is the thing a player cannot see anywhere else.</summary>
        public long Flying;
        /// <summary>Queue entries for this hull in scope, whoever queued them.</summary>
        public int Building;
        /// <summary>Live <c>Ship:{role}</c> demands in scope this line is the highest-ranked answer
        /// for and has decided not to build: work waiting on hulls. Zero is the normal case.</summary>
        public int Pressure;
        /// <summary>No yard in scope this hull can fly out of — the line's own placement gate said
        /// so on the manager's last pass. Empty until that first pass after a restart.</summary>
        public bool RouteBlocked;
        /// <summary>The hull is over this server's performance ceiling for ship prefabs, so the line
        /// builds nothing.</summary>
        public bool OverCeiling;
        /// <summary>The design is on this server's ban list, so the line builds nothing.</summary>
        public bool Banned;
        /// <summary>Hulls of this prefab in repair in scope, not counted in <see cref="Have"/>:
        /// still short of parts, and with their parts in.</summary>
        public int RepairWaiting;
        public int Repairing;
    }

    /// <summary>Work waiting on hulls that no unpaused line covers: a <c>Ship:{role}</c> demand at a
    /// node, with no doctrine line for that role whose scope reaches it. The silent case
    /// (docs/fleet-doctrine-plan.md: a role with no line is not built) made visible.</summary>
    public struct WireDoctrinePressureRow
    {
        public string Role;
        public string NodeId;
        /// <summary>Resolved server-side, empty for a node that was never named.</summary>
        public string NodeName;
        public int Demands;
    }

    /// <summary>One hull the faction actually owns, counted across every node it holds. Sent beside
    /// the doctrine so a hull nothing names — the drone stream the doctrine was written against — is
    /// visible as stock rather than only as an absence from the list.</summary>
    /// <summary>One arm × break-off cell of a faction's tactic memory. <see cref="Arm"/> and
    /// <see cref="BreakOff"/> are the server's <c>TacticArm</c> / <c>BreakOffLevel</c> ordinals;
    /// <see cref="WireTactics"/> names them.</summary>
    public struct WireTacticCell
    {
        public byte Arm;
        public byte BreakOff;
        /// <summary>Posterior mean of the reward share, prior included.</summary>
        public float Mean;
        /// <summary>Evidence behind it, in fights' worth, prior excluded. Zero: the prior alone.</summary>
        public float Evidence;
    }

    /// <summary>One context bucket — the server's <c>TacticContext.Key</c>, three characters
    /// range, mobility, force, each <c>+ = -</c> — and its cells, best mean first.</summary>
    public struct WireTacticContext
    {
        public string Context;
        public float Evidence;
        public WireTacticCell[] Cells;
    }

    /// <summary>What the viewer's faction has learned against one opponent.</summary>
    public struct WireTacticOpponent
    {
        public string Tag;
        /// <summary>When it last fought this opponent, UTC ticks.</summary>
        public long LastUtcTicks;
        public WireTacticCell Preferred;
        public float Evidence;
        /// <summary>Most evidence first.</summary>
        public WireTacticContext[] Contexts;
    }

    /// <summary>One recorded fight outcome — what the server's <c>TACTIC_REVIEW</c> line states.</summary>
    public struct WireTacticReview
    {
        public long UtcTicks;
        public string Opponent;
        public string Context;
        public byte Arm;
        public byte BreakOff;
        /// <summary>Predicted damage share; NaN when there was no prediction.</summary>
        public float Predicted;
        public float Actual;
        public float Reward;
        public bool Mistake;
        public byte HullsLost;
        public byte HullsBrought;
    }

    /// <summary>The Tactics window's header: whose memory, its personality default and whether real
    /// fights are teaching it.</summary>
    public struct WireTacticsHeader
    {
        public string FactionTag;
        public byte PriorArm;
        public byte PriorBreakOff;
        public long Updates;
        /// <summary>Real duels pick from and write to the memory. False: only bench scenarios do.</summary>
        public bool Learning;
    }

    /// <summary>Names for the tactic ordinals the Tactics snapshot carries. Must match the server's
    /// <c>TacticArm</c> and <c>BreakOffLevel</c>; a test holds them together.</summary>
    public static class WireTactics
    {
        public static readonly string[] ArmNames = { "Brawl", "Standoff", "Kite", "AttackRun" };
        public static readonly string[] BreakOffNames = { "Low", "Mid", "High" };

        public static string Arm(byte arm) { return arm < ArmNames.Length ? ArmNames[arm] : "?"; }
        public static string BreakOff(byte level) { return level < BreakOffNames.Length ? BreakOffNames[level] : "?"; }
    }

    public struct WireFleetCensusRow
    {
        public string PrefabId;
        /// <summary>Parked across owned nodes, less the store-listed slice — the same rule a line's
        /// <see cref="WireDoctrineLine.Have"/> counts by.</summary>
        public long Parked;
        public long Flying;
        /// <summary>In repair across the faction's nodes: still short of parts, and with their
        /// parts in.</summary>
        public int RepairWaiting;
        public int Repairing;
    }

    /// <summary>One design the faction has hulls of on an older version, or a refit set for — a row
    /// of the Fleet window's Refits list. Carried at the end of
    /// <see cref="WireMessageKind.FleetDoctrineSnapshot"/>.</summary>
    public struct WireHullRefitRow
    {
        public string Family;
        /// <summary>The newest version the faction may use.</summary>
        public string Latest;
        /// <summary>Hulls in refit at once; 0 when none is set.</summary>
        public int Cap;
        public int InRefit;
        /// <summary>Hulls on an older version parked anywhere.</summary>
        public int OutdatedParked;
        /// <summary>Of those, the ones at a yard, which a refit can take now.</summary>
        public int OutdatedAtYards;
        /// <summary>Why nothing is refit to <see cref="Latest"/>: "banned", "over_ceiling", or empty
        /// when refits may proceed.</summary>
        public string Blocked;
    }

    /// <summary>What <see cref="WireMessageKind.DesignVersionInfo"/> says about one grid.</summary>
    public struct WireDesignVersionInfo
    {
        /// <summary>The hull the query named, or the terminal for a station scan, echoed so a late
        /// answer for a dialog already closed is recognisable.</summary>
        public long EntityId;
        public bool Station;
        /// <summary>A version can be published from this grid.</summary>
        public bool Offered;
        /// <summary>The design's name — what the dialog sends as the scan name.</summary>
        public string Family;
        /// <summary>The design the grid stands as or was built as.</summary>
        public string CurrentId;
        /// <summary>The id the scan would be registered under.</summary>
        public string NextId;
        /// <summary>True when the version would be usable by every faction.</summary>
        public bool ScopeAll;
        /// <summary>Who the version would be usable by, as the player reads it.</summary>
        public string ScopeLabel;
        /// <summary>Why no version is offered, when one is not; empty when the grid matches no
        /// design at all.</summary>
        public string Reason;
    }

    /// <summary>What a <see cref="WireMessageKind.PlayerShelfCommandRequest"/> asks for. No relative
    /// priority pair, unlike <see cref="WireDoctrineCommand"/>: a shelf has no rank, because its rows
    /// do not compete for a scarce yard the way doctrine lines do — a shop hull ranks below every
    /// fleet role whatever order the lines are in.</summary>
    public enum WireShelfCommand : byte
    {
        Remove = 0,
        Pause = 1,
        Resume = 2,
        /// <summary>Value is the new <c>TargetCount</c>.</summary>
        SetTarget = 3,
        /// <summary>Shop-wide, not per line: stop restocking entirely. Carries no line id, and the
        /// server ignores whatever one is sent. It is a command rather than a field on the create
        /// request because a player switches it with no line selected.</summary>
        StockingOff = 4,
        /// <summary>Shop-wide: resume restocking.</summary>
        StockingOn = 5,
    }

    /// <summary>One item in the Bank sales window. Mirrors Core's <c>BankSalesRow</c>.</summary>
    public struct WireBankSalesRow
    {
        /// <summary>Inventory key, <c>Comp:SteelPlate</c> -- what a command names.</summary>
        public string Key;
        /// <summary>Display name resolved server-side; empty means show the key.</summary>
        public string Name;
        /// <summary>Core's <c>ProcessedGoodCategory</c> ordinal: 0 component, 1 weapon, 2 ammunition,
        /// 3 consumable.</summary>
        public byte Category;
        /// <summary>Held across every node the faction owns.</summary>
        public long Held;
        /// <summary>Book value per unit.</summary>
        public long UnitValue;
        /// <summary>Core's <c>BankSaleMode</c> ordinal: 0 sell oversupply, 1 sell oversupply above
        /// <see cref="Target"/>, 2 don't sell.</summary>
        public byte Mode;
        /// <summary>The faction-wide target under mode 1; zero otherwise.</summary>
        public long Target;
    }

    /// <summary>One row of the Store window: a shelf line and the live counts the store manager
    /// itself evaluates it against. Every count is resolved server-side through the same Core helper
    /// the manager uses, so the window can never disagree with the manager about whether a line is
    /// satisfied.</summary>
    public struct WireShelfLine
    {
        public string Id;
        public bool Paused;
        public string PrefabId;
        public int TargetCount;
        /// <summary>Copies reserved for sale across the estate — what the line compares against
        /// <see cref="TargetCount"/>. Deliberately not ship stock: a hull the fleet owns is not on
        /// sale, which is what keeps this window and the Fleet window from double-counting one
        /// hull.</summary>
        public long OnSale;
        /// <summary>Shelf builds of this hull in flight. Fleet builds of the same hull are excluded,
        /// because their output never reaches the shelf.</summary>
        public int Building;
        /// <summary>The capability class the shop files this hull under, resolved server-side.
        /// Empty when the catalogue can class it as nothing, which is also what
        /// <see cref="BlockedReason"/> reports.</summary>
        public string Role;
        /// <summary>Why the manager could not act on this line on its last pass, or empty: one of
        /// <c>unclassed</c>, <c>route</c>, <c>unsellable</c>. Empty until the first store pass after
        /// a restart, for the reason the Core report's own field records.</summary>
        public string BlockedReason;
    }

    /// <summary>One hull actually on sale, counted across every node the faction holds.
    ///
    /// <para><b>The reason this window exists.</b> A faction can hold shelf stock nothing in its list
    /// asked for — everything the managers stocked before the player wrote a line, and anything left
    /// over after one is removed — and the Fleet window cannot show it: that census counts
    /// <c>UnlistedStockAt</c>, which subtracts exactly this stock out. <see cref="Unlined"/> is what
    /// makes it legible rather than merely present.</para></summary>
    public struct WireShelfCensusRow
    {
        public string PrefabId;
        public long OnSale;
        /// <summary>No shelf line names this hull.</summary>
        public bool Unlined;
    }

    /// <summary>
    /// Wire contract shared verbatim by the Factions Torch plugin (server) and FactionsUI (client).
    ///
    /// This folder is compiled into BOTH projects by source include, and the Torch host pins
    /// LangVersion 7.3 — keep everything here inside that ceiling even though FactionsUI itself
    /// is not bound by it. It also deliberately depends on nothing beyond the BCL: no SE types, no
    /// ProtoBuf, no Factions.Core. That is what lets the client plugin move to its own repo later
    /// with this folder duplicated rather than untangled.
    ///
    /// Layering: the uplink messages are the connection, and know nothing about any feature. Each
    /// feature adds its own kinds and its own bit in <see cref="WireFeatures"/>. Adding one must
    /// never require touching the handshake.
    /// </summary>
    public static partial class FactionsWire
    {
        /// <summary>Mod-message channel id. Both ends must agree; see docs/client-plugin.md.</summary>
        public const ushort ChannelId = 0xFAC7;

        /// <summary>Bumped on any breaking change to the payload shapes. A client whose version the
        /// server does not recognise is refused and left on the server-only path.</summary>
        public const int ProtocolVersion = 59;  // 59: a Stock order can cover every station; 58: node detail splits worker supply into food hours and air hours; 57: occupation hails (traffic events 13, 14); 56: traffic callouts carry the speaker, and trespass hails are callouts; 55: node detail no longer carries the Found bar

        /// <summary><c>ScannerContactKind.Fleet</c>. Named here because the contact codec branches
        /// on it; see <see cref="ContactCarriesFleetFields"/>.</summary>
        public const byte ContactKindFleet = 7;

        /// <summary><c>ScannerContactKind.RaidEngagement</c> — a fleet holding at its target and
        /// fighting. Still a fleet, so it carries the fleet fields.</summary>
        public const byte ContactKindRaidEngagement = 8;

        /// <summary>
        /// Whether a contact of this kind carries <see cref="WireContact.FleetKind"/> and
        /// <see cref="WireContact.ConvoySize"/>. A scanner sphere is mostly nodes and they pay
        /// nothing for a field that cannot apply to them — the same reason the velocity block is
        /// conditional on <see cref="WireContact.Moving"/>.
        ///
        /// <para><b>Writer and reader must call this, never inline the test.</b> The discriminator
        /// is <see cref="WireContact.Kind"/>, which the reader has already consumed by the time it
        /// reaches this branch, so both ends evaluate the same predicate over the same value. A
        /// future fleet-ish contact kind that is added here but not there — or the reverse — would
        /// desynchronise the whole rest of the message, not just its own row.</para>
        /// </summary>
        public static bool ContactCarriesFleetFields(byte contactKind)
        {
            return contactKind == ContactKindFleet || contactKind == ContactKindRaidEngagement;
        }

        /// <summary>Sanity bound on a decoded count, so a corrupt length cannot make the reader
        /// pre-allocate an enormous list before it fails.</summary>
        private const int MaxContactsPerMessage = 4096;
        private const int MaxNodesPerMessage = 8192;
        private const int MaxFactionsPerMessage = 512;
        private const int MaxFleetsPerMessage = 2048;
        private const int MaxStopsPerFleet = 64;
        private const int MaxAlarmsPerMessage = 512;
        private const int MaxStandingOrdersPerMessage = 256;
        private const int MaxStationsPerStockOrder = 512;
        private const int MaxCampaignsPerMessage = 64;
        private const int MaxPausedBlocksPerMessage = 16384;
        private const int MaxOrderOptionsPerMessage = 4096;
        private const int MaxStockItemsPerMessage = 8192;
        private const int MaxAssetNodesPerMessage = 1024;
        private const int MaxAssetFleetsPerMessage = 1024;
        private const int MaxAssetGridsPerMessage = 4096;
        private const int MaxCapabilitiesPerNode = 64;
        /// <summary>Bound on a deposit's ore list. SE ships ten ores; this leaves room for mods.</summary>
        private const int MaxOresPerNode = 64;
        /// <summary>Bound on the develop menu. One row per capability, so this is the enum's length
        /// with room to grow, not a payload-shaped guess.</summary>
        private const int MaxDevelopOptionsPerNode = 64;
        private const int MaxStockItemsPerNode = 1024;
        /// <summary>Bound on one transfer listing. Deliberately the same as the node window's stock
        /// list: it is the same stock, read through a different surface.</summary>
        private const int MaxTransferRowsPerNode = 1024;
        private const int MaxBuildsPerNode = 256;
        /// <summary>A ship BOM runs to about nineteen components; a capability install's
        /// is shorter. Anything past this is a corrupt length, not a hull.</summary>
        private const int MaxBomRowsPerBuild = 256;
        private const int MaxShipPrefabsPerNode = 256;
        /// <summary>Bound on one node's repair rows after folding. The writer stops here; the
        /// window's status line carries the uncapped count.</summary>
        public const int MaxRepairRowsPerNode = 128;
        /// <summary>Bound on one node's production-history rows. The host sends the most recent
        /// first, so the writer's cut drops the oldest spans.</summary>
        public const int MaxProductionRowsPerNode = 128;
        /// <summary>Bound on one node's live demand rows. Most urgent first on the wire.</summary>
        public const int MaxDemandRowsPerNode = 128;
        /// <summary>Bound on one node's ended-demand rows. Most recently ended first on the wire.</summary>
        public const int MaxEndedDemandRowsPerNode = 128;
        private const int MaxEventsPerMessage = 512;
        /// <summary>Bound on one market snapshot. The measured whole-world table is ~1,400 rows
        /// across 68 stations; this leaves room for the catalogue to grow without letting a corrupt
        /// length preallocate.</summary>
        private const int MaxMarketRowsPerMessage = 16384;
        private const int MaxMarketStationsPerMessage = 2048;
        private const int MaxMarketItemsPerMessage = 2048;
        /// <summary>Bound on one rock delta. Generous against the ~150 objects a 15 km view sphere
        /// holds at the density now live, so a jump into a dense belt still fits one message.</summary>
        private const int MaxRockObjectsPerMessage = 4096;
        /// <summary>Bound on one terminal ship-asset listing. One row per (node, prefab), so the
        /// ceiling is the faction's node count times its catalogue breadth; 512 is well past both at
        /// any world size the graph supports.</summary>
        private const int MaxShipAssetRows = 512;
        /// <summary>Bounds on one rearm survey. A ship's guns come in a handful of block types and
        /// each takes a handful of magazines; anything past these is a corrupt length.</summary>
        private const int MaxRearmGroups = 128;
        private const int MaxRearmOptionsPerGroup = 64;
        private const int MaxRearmMagazines = 256;
        private const int MaxReceiptLines = 256;
        /// <summary>Longest command text accepted from a client. Node and entry ids are short
        /// tokens; anything longer is a malformed or hostile payload, not a typo.</summary>
        private const int MaxCommandTokenLength = 128;
        /// <summary>Bound on one refit listing. One row per station design the faction may use at
        /// this node's site, so the ceiling is the vanilla station pool plus its own captures;
        /// deliberately the same as the node window's prefab bound, which counts the same
        /// catalogue through a different surface.</summary>
        private const int MaxStationRefitCandidates = 256;
        private const int MaxDoctrineLinesPerMessage = 256;
        private const int MaxDoctrinePressureRowsPerMessage = 512;
        private const int MaxFleetCensusRowsPerMessage = 512;
        /// <summary>The server keeps at most 16 opponents, 27 contexts and 12 cells each, and 8
        /// reviews; these bound a hostile payload, not the report.</summary>
        private const int MaxTacticOpponentsPerMessage = 64;
        private const int MaxTacticContextsPerOpponent = 27;
        private const int MaxTacticCellsPerContext = 12;
        private const int MaxTacticReviewsPerMessage = 64;
        private const int MaxShelfLinesPerMessage = 256;
        /// <summary>Every bank-buyable item a heavily modded world defines, with room to spare. A
        /// command naming more keys than this is refused rather than truncated: a "keep every shown
        /// item" that silently dropped the tail would read as having worked.</summary>
        public const int MaxBankSalesRows = 2048;
        private const int MaxShelfCensusRowsPerMessage = 512;
        /// <summary>One row per NPC faction on the server's roster.</summary>
        private const int MaxDiplomacyRowsPerMessage = 256;
        private const int MaxBuildPrefabsPerMessage = 512;
        private const int MaxBuildRolesPerMessage = 32;
        private const int MaxRolesPerBuildPrefab = 16;

        /// <summary>Every payload starts with these two fields, so a receiver can always read the
        /// kind and version even when it cannot decode the rest.</summary>
        public static bool TryReadHeader(byte[] data, out WireMessageKind kind, out int protocolVersion)
        {
            kind = default(WireMessageKind);
            protocolVersion = 0;
            if (data == null || data.Length < 6) return false;
            kind = (WireMessageKind)BitConverter.ToUInt16(data, 0);
            protocolVersion = BitConverter.ToInt32(data, 2);
            return true;
        }

        // ---- Uplink ---------------------------------------------------------------------------

        public static byte[] WriteUplinkRequest(string clientVersion)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.UplinkRequest);
                w.Write(clientVersion ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadUplinkRequest(byte[] data, out string clientVersion)
        {
            clientVersion = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.UplinkRequest) return false;
                    clientVersion = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteUplinkGranted(string serverVersion, WireFeatures features)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.UplinkGranted);
                w.Write(serverVersion ?? "");
                w.Write((long)features);
                return ms.ToArray();
            }
        }

        public static bool TryReadUplinkGranted(byte[] data, out string serverVersion, out WireFeatures features)
        {
            serverVersion = null;
            features = WireFeatures.None;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.UplinkGranted) return false;
                    serverVersion = r.ReadString();
                    features = (WireFeatures)r.ReadInt64();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteUplinkRefused(string reason)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.UplinkRefused);
                w.Write(reason ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadUplinkRefused(byte[] data, out string reason)
        {
            reason = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.UplinkRefused) return false;
                    reason = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Whether <paramref name="data"/> is a refusal from a server speaking another
        /// protocol, and which. Read from the header alone: <see cref="TryReadUplinkRefused"/>
        /// version-checks the header and so cannot read a refusal across exactly this mismatch, but the
        /// header's own layout — kind, then version — is the one part of a message every build reads
        /// the same way.</summary>
        public static bool TryReadRefusalProtocol(byte[] data, out int serverVersion)
        {
            WireMessageKind kind;
            if (!TryReadHeader(data, out kind, out serverVersion)) return false;
            return kind == WireMessageKind.UplinkRefused && serverVersion != ProtocolVersion;
        }

        /// <summary>What a player refused for a protocol mismatch is told, on screen. Only a mismatch
        /// gets one: without the link every Factions panel is simply missing, which reads as the client
        /// plugin being broken, and the fix differs by which side is behind.</summary>
        public static string ProtocolMismatchNotice(int clientVersion, int serverVersion)
        {
            string fix = clientVersion < serverVersion
                ? "Restart Space Engineers once the Factions client plugin has updated."
                : "The server is behind this plugin; reconnect once it has been updated.";
            return "Factions: this client plugin speaks protocol " + clientVersion + ", the server "
                   + serverVersion + ", so its panels are off. " + fix;
        }

        // ---- Scanner --------------------------------------------------------------------------

        /// <summary>The player's current signature. A single value on purpose — see
        /// <see cref="WireMessageKind.SignatureStatus"/>.</summary>
        public static byte[] WriteSignatureStatus(double signature)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.SignatureStatus);
                w.Write(signature);
                return ms.ToArray();
            }
        }

        public static bool TryReadSignatureStatus(byte[] data, out double signature)
        {
            signature = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.SignatureStatus) return false;
                    signature = r.ReadDouble();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteGridSpeedConfig(double largeGridMps, double smallGridMps)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.GridSpeedConfig);
                w.Write(largeGridMps);
                w.Write(smallGridMps);
                return ms.ToArray();
            }
        }

        public static bool TryReadGridSpeedConfig(byte[] data, out double largeGridMps, out double smallGridMps)
        {
            largeGridMps = 0;
            smallGridMps = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.GridSpeedConfig) return false;
                    largeGridMps = r.ReadDouble();
                    smallGridMps = r.ReadDouble();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteNetTraceStart(long gridEntityId, int frames, string stamp)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NetTraceStart);
                w.Write(gridEntityId);
                w.Write(frames);
                w.Write(stamp ?? string.Empty);
                return ms.ToArray();
            }
        }

        public static bool TryReadNetTraceStart(byte[] data, out long gridEntityId, out int frames, out string stamp)
        {
            gridEntityId = 0;
            frames = 0;
            stamp = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NetTraceStart) return false;
                    gridEntityId = r.ReadInt64();
                    frames = r.ReadInt32();
                    stamp = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteMunitionSpeedConfig(double missileSpeedScale)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.MunitionSpeedConfig);
                w.Write(missileSpeedScale);
                return ms.ToArray();
            }
        }

        public static bool TryReadMunitionSpeedConfig(byte[] data, out double missileSpeedScale)
        {
            missileSpeedScale = 1.0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.MunitionSpeedConfig) return false;
                    missileSpeedScale = r.ReadDouble();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteScannerConfig(int level, double smallGridRangeKm, double largeGridRangeKm)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ScannerConfig);
                w.Write(level);
                w.Write(smallGridRangeKm);
                w.Write(largeGridRangeKm);
                return ms.ToArray();
            }
        }

        public static bool TryReadScannerConfig(
            byte[] data, out int level, out double smallGridRangeKm, out double largeGridRangeKm)
        {
            level = 0;
            smallGridRangeKm = 0;
            largeGridRangeKm = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ScannerConfig) return false;
                    level = r.ReadInt32();
                    smallGridRangeKm = r.ReadDouble();
                    largeGridRangeKm = r.ReadDouble();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Contacts that entered or moved, plus the keys of those that left.
        ///
        /// <paramref name="reset"/> means "forget everything you hold, this message is the whole
        /// truth". The server tracks each player's contact set to compute deltas, and whenever it
        /// drops that state -- a client linking, a grid change, the scanner going inactive -- it can
        /// no longer name the keys the client still holds, so it could never remove them. Without a
        /// reset those contacts are stranded on the client forever.
        /// </summary>
        public static byte[] WriteScannerContacts(IList<WireContact> present, IList<string> removedKeys, bool reset)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ScannerContacts);
                w.Write(reset);
                int presentCount = present == null ? 0 : present.Count;
                w.Write(presentCount);
                for (int i = 0; i < presentCount; i++)
                {
                    WireContact c = present[i];
                    w.Write(c.Key ?? "");
                    w.Write(c.Kind);
                    w.Write(c.Label ?? "");
                    w.Write(c.XKm);
                    w.Write(c.YKm);
                    w.Write(c.ZKm);
                    w.Write(c.EntityId);
                    w.Write(c.Unclaimed);
                    w.Write(c.Relation);
                    w.Write(c.Moving);
                    if (c.Moving)
                    {
                        w.Write(c.VelXKmPerMin);
                        w.Write(c.VelYKmPerMin);
                        w.Write(c.VelZKmPerMin);
                        w.Write(c.TargetXKm);
                        w.Write(c.TargetYKm);
                        w.Write(c.TargetZKm);
                    }
                    if (ContactCarriesFleetFields(c.Kind))
                    {
                        w.Write(c.FleetKind);
                        w.Write(c.ConvoySize);
                    }
                    else
                    {
                        w.Write(c.DisplayName ?? "");
                    }
                }
                int removedCount = removedKeys == null ? 0 : removedKeys.Count;
                w.Write(removedCount);
                for (int i = 0; i < removedCount; i++) w.Write(removedKeys[i] ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadScannerContacts(
            byte[] data, out List<WireContact> present, out List<string> removedKeys, out bool reset)
        {
            present = null;
            removedKeys = null;
            reset = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ScannerContacts) return false;
                    reset = r.ReadBoolean();

                    int presentCount = r.ReadInt32();
                    if (presentCount < 0 || presentCount > MaxContactsPerMessage) return false;
                    var readPresent = new List<WireContact>(presentCount);
                    for (int i = 0; i < presentCount; i++)
                    {
                        var c = new WireContact();
                        c.Key = r.ReadString();
                        c.Kind = r.ReadByte();
                        c.Label = r.ReadString();
                        c.XKm = r.ReadDouble();
                        c.YKm = r.ReadDouble();
                        c.ZKm = r.ReadDouble();
                        c.EntityId = r.ReadInt64();
                        c.Unclaimed = r.ReadBoolean();
                        c.Relation = r.ReadByte();
                        c.Moving = r.ReadBoolean();
                        if (c.Moving)
                        {
                            c.VelXKmPerMin = r.ReadDouble();
                            c.VelYKmPerMin = r.ReadDouble();
                            c.VelZKmPerMin = r.ReadDouble();
                            c.TargetXKm = r.ReadDouble();
                            c.TargetYKm = r.ReadDouble();
                            c.TargetZKm = r.ReadDouble();
                        }
                        if (ContactCarriesFleetFields(c.Kind))
                        {
                            c.FleetKind = r.ReadByte();
                            c.ConvoySize = r.ReadByte();
                        }
                        else
                        {
                            c.DisplayName = r.ReadString();
                        }
                        readPresent.Add(c);
                    }

                    int removedCount = r.ReadInt32();
                    if (removedCount < 0 || removedCount > MaxContactsPerMessage) return false;
                    var readRemoved = new List<string>(removedCount);
                    for (int i = 0; i < removedCount; i++) readRemoved.Add(r.ReadString());

                    present = readPresent;
                    removedKeys = readRemoved;
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Map ------------------------------------------------------------------------------

        /// <summary>Subscribe to or unsubscribe from map updates. Sent when the map window opens
        /// and closes, so an idle client costs the server nothing.</summary>
        public static byte[] WriteMapRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.MapRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadMapRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.MapRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The slow-changing half of the map. Always the whole truth, never a delta:
        /// nodes change rarely enough that a full send on a slow cadence costs less than the
        /// per-client tracking state a delta would need.</summary>
        public static byte[] WriteMapWorld(IList<WireMapNode> nodes, IList<WireMapFaction> factions)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.MapWorld);
                int nodeCount = nodes == null ? 0 : nodes.Count;
                w.Write(nodeCount);
                for (int i = 0; i < nodeCount; i++)
                {
                    WireMapNode n = nodes[i];
                    w.Write(n.Id ?? "");
                    w.Write(n.DisplayName ?? "");
                    w.Write(n.DominantOre ?? "");
                    w.Write(n.Surveyed);
                    w.Write(n.CapabilityMask);
                    w.Write(n.Zone);
                    w.Write(n.OwnerTag ?? "");
                    w.Write(n.Relation);
                    w.Write(n.HasStation);
                    w.Write(n.HasInfrastructure);
                    w.Write(n.XKm);
                    w.Write(n.YKm);
                    w.Write(n.ZKm);
                    w.Write(n.RadiusKm);
                    w.Write(n.HoldStrength);
                    w.Write(n.Knowledge);
                }
                int factionCount = factions == null ? 0 : factions.Count;
                w.Write(factionCount);
                for (int i = 0; i < factionCount; i++)
                {
                    WireMapFaction f = factions[i];
                    w.Write(f.Tag ?? "");
                    w.Write(f.Relation);
                    w.Write(f.NodeCount);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadMapWorld(
            byte[] data, out List<WireMapNode> nodes, out List<WireMapFaction> factions)
        {
            nodes = null;
            factions = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.MapWorld) return false;

                    int nodeCount = r.ReadInt32();
                    if (nodeCount < 0 || nodeCount > MaxNodesPerMessage) return false;
                    var readNodes = new List<WireMapNode>(nodeCount);
                    for (int i = 0; i < nodeCount; i++)
                    {
                        var n = new WireMapNode();
                        n.Id = r.ReadString();
                        n.DisplayName = r.ReadString();
                        n.DominantOre = r.ReadString();
                        n.Surveyed = r.ReadBoolean();
                        n.CapabilityMask = r.ReadUInt16();
                        n.Zone = r.ReadByte();
                        n.OwnerTag = r.ReadString();
                        n.Relation = r.ReadByte();
                        n.HasStation = r.ReadBoolean();
                        n.HasInfrastructure = r.ReadBoolean();
                        n.XKm = r.ReadDouble();
                        n.YKm = r.ReadDouble();
                        n.ZKm = r.ReadDouble();
                        n.RadiusKm = r.ReadDouble();
                        n.HoldStrength = r.ReadDouble();
                        n.Knowledge = r.ReadByte();
                        readNodes.Add(n);
                    }

                    int factionCount = r.ReadInt32();
                    if (factionCount < 0 || factionCount > MaxFactionsPerMessage) return false;
                    var readFactions = new List<WireMapFaction>(factionCount);
                    for (int i = 0; i < factionCount; i++)
                    {
                        var f = new WireMapFaction();
                        f.Tag = r.ReadString();
                        f.Relation = r.ReadByte();
                        f.NodeCount = r.ReadInt32();
                        readFactions.Add(f);
                    }

                    nodes = readNodes;
                    factions = readFactions;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Every fleet currently in transit. Full state each time for the same reason
        /// <see cref="WriteMapWorld"/> is: the set is small and churns fast, so a delta would cost
        /// more bookkeeping than it saves -- and the stranded-contact class of bug the scanner's
        /// reset flag exists to fix cannot arise at all.</summary>
        public static byte[] WriteMapFleets(IList<WireMapFleet> fleets, IList<WireMapAlarm> alarms)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.MapFleets);
                int fleetCount = fleets == null ? 0 : fleets.Count;
                w.Write(fleetCount);
                for (int i = 0; i < fleetCount; i++)
                {
                    WireMapFleet f = fleets[i];
                    w.Write(f.Key ?? "");
                    w.Write(f.Kind);
                    w.Write(f.OwnerTag ?? "");
                    w.Write(f.Relation);
                    w.Write(f.XKm);
                    w.Write(f.YKm);
                    w.Write(f.ZKm);
                    w.Write(f.VelXKmPerMin);
                    w.Write(f.VelYKmPerMin);
                    w.Write(f.VelZKmPerMin);
                    w.Write(f.TargetXKm);
                    w.Write(f.TargetYKm);
                    w.Write(f.TargetZKm);
                    w.Write(f.EtaUtcTicks);
                    w.Write(f.ConvoySize);
                    w.Write(f.EntityId);

                    int stopCount = f.RoutedStops == null ? 0 : f.RoutedStops.Length;
                    if (stopCount > MaxStopsPerFleet) stopCount = MaxStopsPerFleet;
                    w.Write(stopCount);
                    for (int s = 0; s < stopCount; s++)
                    {
                        WireMapStop stop = f.RoutedStops[s];
                        w.Write(stop.XKm);
                        w.Write(stop.YKm);
                        w.Write(stop.ZKm);
                        w.Write(stop.Kind);
                    }
                }

                int alarmCount = alarms == null ? 0 : alarms.Count;
                if (alarmCount > MaxAlarmsPerMessage) alarmCount = MaxAlarmsPerMessage;
                w.Write(alarmCount);
                for (int i = 0; i < alarmCount; i++)
                {
                    WireMapAlarm a = alarms[i];
                    w.Write(a.NodeId ?? "");
                    w.Write(a.State);
                    w.Write(a.ZoneIntegrity);
                    w.Write(a.RaidProgress);
                    w.Write(a.UntilUtcTicks);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadMapFleets(
            byte[] data, out List<WireMapFleet> fleets, out List<WireMapAlarm> alarms)
        {
            fleets = null;
            alarms = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.MapFleets) return false;

                    int fleetCount = r.ReadInt32();
                    if (fleetCount < 0 || fleetCount > MaxFleetsPerMessage) return false;
                    var readFleets = new List<WireMapFleet>(fleetCount);
                    for (int i = 0; i < fleetCount; i++)
                    {
                        var f = new WireMapFleet();
                        f.Key = r.ReadString();
                        f.Kind = r.ReadByte();
                        f.OwnerTag = r.ReadString();
                        f.Relation = r.ReadByte();
                        f.XKm = r.ReadDouble();
                        f.YKm = r.ReadDouble();
                        f.ZKm = r.ReadDouble();
                        f.VelXKmPerMin = r.ReadDouble();
                        f.VelYKmPerMin = r.ReadDouble();
                        f.VelZKmPerMin = r.ReadDouble();
                        f.TargetXKm = r.ReadDouble();
                        f.TargetYKm = r.ReadDouble();
                        f.TargetZKm = r.ReadDouble();
                        f.EtaUtcTicks = r.ReadInt64();
                        f.ConvoySize = r.ReadInt32();
                        f.EntityId = r.ReadInt64();

                        int stopCount = r.ReadInt32();
                        if (stopCount < 0 || stopCount > MaxStopsPerFleet) return false;
                        var stops = new WireMapStop[stopCount];
                        for (int s = 0; s < stopCount; s++)
                        {
                            var stop = new WireMapStop();
                            stop.XKm = r.ReadDouble();
                            stop.YKm = r.ReadDouble();
                            stop.ZKm = r.ReadDouble();
                            stop.Kind = r.ReadByte();
                            stops[s] = stop;
                        }
                        f.RoutedStops = stops;
                        readFleets.Add(f);
                    }

                    int alarmCount = r.ReadInt32();
                    if (alarmCount < 0 || alarmCount > MaxAlarmsPerMessage) return false;
                    var readAlarms = new List<WireMapAlarm>(alarmCount);
                    for (int i = 0; i < alarmCount; i++)
                    {
                        var a = new WireMapAlarm();
                        a.NodeId = r.ReadString();
                        a.State = r.ReadByte();
                        a.ZoneIntegrity = r.ReadDouble();
                        a.RaidProgress = r.ReadDouble();
                        a.UntilUtcTicks = r.ReadInt64();
                        readAlarms.Add(a);
                    }

                    fleets = readFleets;
                    alarms = readAlarms;
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Assets ---------------------------------------------------------------------------

        /// <summary>Subscribe to or unsubscribe from the player's asset register. Sent when the
        /// Assets tab is entered and left, so a player who never opens it costs the server the
        /// grid sweep it would otherwise pay for.</summary>
        public static byte[] WriteAssetsRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.AssetsRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadAssetsRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.AssetsRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Everything the viewing player and their faction own. Full state each time, for the same
        /// reason the map's payloads are: one player's holdings are a small set, and a delta would
        /// need per-client tracking state to fall out of sync.
        ///
        /// <paramref name="factionTag"/> is empty for a player in no faction — which is a real
        /// state, not an error: they can still own grids.
        /// </summary>
        public static byte[] WriteAssetsSnapshot(
            string factionTag, string factionName,
            IList<WireAssetNode> nodes, IList<WireAssetFleet> fleets, IList<WireAssetGrid> grids)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.AssetsSnapshot);
                w.Write(factionTag ?? "");
                w.Write(factionName ?? "");

                int nodeCount = nodes == null ? 0 : nodes.Count;
                w.Write(nodeCount);
                for (int i = 0; i < nodeCount; i++)
                {
                    WireAssetNode n = nodes[i];
                    w.Write(n.Id ?? "");
                    w.Write(n.DisplayName ?? "");
                    w.Write(n.DominantOre ?? "");
                    w.Write(n.Surveyed);
                    w.Write(n.Zone);
                    w.Write(n.HasStation);
                    w.Write(n.XKm);
                    w.Write(n.YKm);
                    w.Write(n.ZKm);
                    w.Write(n.HoldStrength);
                    w.Write(n.ResourceValue);
                    w.Write(n.Richness);
                    w.Write(n.FoundCost);
                    w.Write(n.ShipsReady);
                    w.Write(n.BuildsQueued);
                    w.Write(n.ShipsInRepair);
                    w.Write(n.ShipyardPaused);
                    int capCount = n.Capabilities == null ? 0 : n.Capabilities.Length;
                    w.Write(capCount);
                    for (int c = 0; c < capCount; c++)
                    {
                        w.Write(n.Capabilities[c].Cap ?? "");
                        w.Write(n.Capabilities[c].Level);
                    }
                }

                int fleetCount = fleets == null ? 0 : fleets.Count;
                w.Write(fleetCount);
                for (int i = 0; i < fleetCount; i++)
                {
                    WireAssetFleet f = fleets[i];
                    w.Write(f.Key ?? "");
                    w.Write(f.Kind);
                    w.Write(f.XKm);
                    w.Write(f.YKm);
                    w.Write(f.ZKm);
                    w.Write(f.OriginName ?? "");
                    w.Write(f.DestinationName ?? "");
                    w.Write(f.TargetXKm);
                    w.Write(f.TargetYKm);
                    w.Write(f.TargetZKm);
                    w.Write(f.EtaUtcTicks);
                    w.Write(f.ConvoySize);
                    w.Write(f.EntityId);
                }

                int gridCount = grids == null ? 0 : grids.Count;
                w.Write(gridCount);
                for (int i = 0; i < gridCount; i++)
                {
                    WireAssetGrid g = grids[i];
                    w.Write(g.EntityId);
                    w.Write(g.Name ?? "");
                    w.Write(g.IsStatic);
                    w.Write(g.GridSize);
                    w.Write(g.BlockCount);
                    w.Write(g.XKm);
                    w.Write(g.YKm);
                    w.Write(g.ZKm);
                    w.Write(g.OwnedPersonally);
                    w.Write(g.OwnerName ?? "");
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadAssetsSnapshot(
            byte[] data, out string factionTag, out string factionName,
            out List<WireAssetNode> nodes, out List<WireAssetFleet> fleets, out List<WireAssetGrid> grids)
        {
            factionTag = null;
            factionName = null;
            nodes = null;
            fleets = null;
            grids = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.AssetsSnapshot) return false;

                    string readTag = r.ReadString();
                    string readName = r.ReadString();

                    int nodeCount = r.ReadInt32();
                    if (nodeCount < 0 || nodeCount > MaxAssetNodesPerMessage) return false;
                    var readNodes = new List<WireAssetNode>(nodeCount);
                    for (int i = 0; i < nodeCount; i++)
                    {
                        var n = new WireAssetNode();
                        n.Id = r.ReadString();
                        n.DisplayName = r.ReadString();
                        n.DominantOre = r.ReadString();
                        n.Surveyed = r.ReadBoolean();
                        n.Zone = r.ReadByte();
                        n.HasStation = r.ReadBoolean();
                        n.XKm = r.ReadDouble();
                        n.YKm = r.ReadDouble();
                        n.ZKm = r.ReadDouble();
                        n.HoldStrength = r.ReadDouble();
                        n.ResourceValue = r.ReadDouble();
                        n.Richness = r.ReadDouble();
                        n.FoundCost = r.ReadInt64();
                        n.ShipsReady = r.ReadInt32();
                        n.BuildsQueued = r.ReadInt32();
                        n.ShipsInRepair = r.ReadInt32();
                        n.ShipyardPaused = r.ReadBoolean();
                        int capCount = r.ReadInt32();
                        if (capCount < 0 || capCount > MaxCapabilitiesPerNode) return false;
                        var caps = new WireAssetCapability[capCount];
                        for (int c = 0; c < capCount; c++)
                        {
                            caps[c].Cap = r.ReadString();
                            caps[c].Level = r.ReadInt32();
                        }
                        n.Capabilities = caps;
                        readNodes.Add(n);
                    }

                    int fleetCount = r.ReadInt32();
                    if (fleetCount < 0 || fleetCount > MaxAssetFleetsPerMessage) return false;
                    var readFleets = new List<WireAssetFleet>(fleetCount);
                    for (int i = 0; i < fleetCount; i++)
                    {
                        var f = new WireAssetFleet();
                        f.Key = r.ReadString();
                        f.Kind = r.ReadByte();
                        f.XKm = r.ReadDouble();
                        f.YKm = r.ReadDouble();
                        f.ZKm = r.ReadDouble();
                        f.OriginName = r.ReadString();
                        f.DestinationName = r.ReadString();
                        f.TargetXKm = r.ReadDouble();
                        f.TargetYKm = r.ReadDouble();
                        f.TargetZKm = r.ReadDouble();
                        f.EtaUtcTicks = r.ReadInt64();
                        f.ConvoySize = r.ReadInt32();
                        f.EntityId = r.ReadInt64();
                        readFleets.Add(f);
                    }

                    int gridCount = r.ReadInt32();
                    if (gridCount < 0 || gridCount > MaxAssetGridsPerMessage) return false;
                    var readGrids = new List<WireAssetGrid>(gridCount);
                    for (int i = 0; i < gridCount; i++)
                    {
                        var g = new WireAssetGrid();
                        g.EntityId = r.ReadInt64();
                        g.Name = r.ReadString();
                        g.IsStatic = r.ReadBoolean();
                        g.GridSize = r.ReadByte();
                        g.BlockCount = r.ReadInt32();
                        g.XKm = r.ReadDouble();
                        g.YKm = r.ReadDouble();
                        g.ZKm = r.ReadDouble();
                        g.OwnedPersonally = r.ReadBoolean();
                        g.OwnerName = r.ReadString();
                        readGrids.Add(g);
                    }

                    factionTag = readTag;
                    factionName = readName;
                    nodes = readNodes;
                    fleets = readFleets;
                    grids = readGrids;
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Node detail ----------------------------------------------------------------------

        /// <summary>Watch one node, or stop watching. An empty or null
        /// <paramref name="nodeId"/> is the unsubscribe — there is no separate verb, because a
        /// client only ever watches one node and "watch nothing" is the same statement.</summary>
        public static byte[] WriteNodeDetailRequest(string nodeId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeDetailRequest);
                w.Write(nodeId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeDetailRequest(byte[] data, out string nodeId)
        {
            nodeId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeDetailRequest) return false;
                    string read = r.ReadString();
                    if (read.Length > MaxCommandTokenLength) return false;
                    nodeId = read;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// One node at window detail. Full state each time, on the same reasoning as the other two
        /// feature payloads: it is one node, and a delta would need per-client tracking to fall out
        /// of sync over.
        ///
        /// The privacy rule lives in the writer, not the reader: everything past
        /// <see cref="WireNodeDetail.Owned"/> is written as empty when the caller says the viewer
        /// does not own the node, so unowned detail never reaches the wire at all.
        /// </summary>
        public static byte[] WriteNodeDetailSnapshot(WireNodeDetail detail)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeDetailSnapshot);
                w.Write(detail.Exists);
                w.Write(detail.Id ?? "");
                w.Write(detail.DisplayName ?? "");
                w.Write(detail.DominantOre ?? "");
                w.Write(detail.Surveyed);
                w.Write(detail.Zone);
                w.Write(detail.OwnerTag ?? "");
                w.Write(detail.Relation);
                w.Write(detail.HasStation);
                w.Write(detail.XKm);
                w.Write(detail.YKm);
                w.Write(detail.ZKm);
                w.Write(detail.HoldStrength);
                w.Write(detail.ZoneState);
                w.Write(detail.ZoneIntegrity);
                w.Write(detail.ZoneRaidProgress);
                w.Write(detail.ZoneUntilUtcTicks);

                w.Write(detail.Owned);
                if (!detail.Owned)
                {
                    return ms.ToArray();
                }

                w.Write(detail.ResourceValue);
                w.Write(detail.Richness);
                w.Write(detail.FoundCost);
                w.Write(detail.FoundHave);
                w.Write(detail.FoundBlock);
                w.Write(detail.ShipyardPaused);
                w.Write(detail.HasStore);
                w.Write(detail.Workers);
                w.Write(detail.WorkerBeds);
                w.Write(detail.RecruitPrice);
                w.Write(detail.WorkerFoodHours);
                w.Write(detail.WorkerAirHours);
                w.Write(detail.SurveyConfidence);
                w.Write(detail.ResourceValueTarget);
                w.Write(detail.RichnessTarget);
                w.Write(detail.ResolvedShareFloor);

                int oreCount = detail.Ores == null ? 0 : Math.Min(detail.Ores.Length, MaxOresPerNode);
                w.Write(oreCount);
                for (int i = 0; i < oreCount; i++)
                {
                    w.Write(detail.Ores[i].SubTypeId ?? "");
                    w.Write(detail.Ores[i].Share);
                    w.Write(detail.Ores[i].PriceRatio);
                    w.Write(detail.Ores[i].WorthShare);
                    w.Write(detail.Ores[i].Resolved);
                }

                int capCount = detail.Capabilities == null ? 0 : detail.Capabilities.Length;
                w.Write(capCount);
                for (int i = 0; i < capCount; i++)
                {
                    w.Write(detail.Capabilities[i].Cap ?? "");
                    w.Write(detail.Capabilities[i].Level);
                }

                int stockCount = detail.Stock == null ? 0 : detail.Stock.Length;
                w.Write(stockCount);
                for (int i = 0; i < stockCount; i++)
                {
                    w.Write(detail.Stock[i].Key ?? "");
                    w.Write(detail.Stock[i].Quantity);
                    w.Write(detail.Stock[i].Reserved);
                    w.Write(detail.Stock[i].ForSale);
                    w.Write(detail.Stock[i].Target);
                    w.Write(detail.Stock[i].KeptFor);
                }

                int buildCount = detail.Builds == null ? 0 : detail.Builds.Length;
                w.Write(buildCount);
                for (int i = 0; i < buildCount; i++)
                {
                    WireNodeBuild b = detail.Builds[i];
                    w.Write(b.Id ?? "");
                    w.Write(b.Role ?? "");
                    w.Write(b.PrefabId ?? "");
                    w.Write(b.BomTotal);
                    w.Write(b.Collected);
                    w.Write(b.Priority);
                    w.Write(b.ExpiresUtcTicks);
                    w.Write(b.StalledSeconds);
                    w.Write(b.CreatedUtcTicks);
                    w.Write(b.Working);
                    w.Write(b.WaitingBehind);

                    int bomCount = b.Bom == null ? 0 : b.Bom.Length;
                    w.Write(bomCount);
                    for (int c = 0; c < bomCount; c++)
                    {
                        w.Write(b.Bom[c].Key ?? "");
                        w.Write(b.Bom[c].Need);
                        w.Write(b.Bom[c].Have);
                        w.Write(b.Bom[c].AtNode);
                        w.Write(b.Bom[c].Elsewhere);
                        w.Write(b.Bom[c].EnRoute);
                        w.Write(b.Bom[c].Unserved);
                    }

                    int reservedCount = b.ReservedInputs == null ? 0 : b.ReservedInputs.Length;
                    w.Write(reservedCount);
                    for (int c = 0; c < reservedCount; c++)
                    {
                        w.Write(b.ReservedInputs[c].Key ?? "");
                        w.Write(b.ReservedInputs[c].Quantity);
                    }
                }

                int shipCount = detail.Ships == null ? 0 : detail.Ships.Length;
                w.Write(shipCount);
                for (int i = 0; i < shipCount; i++)
                {
                    w.Write(detail.Ships[i].PrefabId ?? "");
                    w.Write(detail.Ships[i].Ready);
                    w.Write(detail.Ships[i].ListedForSale);
                }

                int developCount = detail.Develop == null ? 0 : detail.Develop.Length;
                w.Write(developCount);
                for (int i = 0; i < developCount; i++)
                {
                    WireNodeCapabilityOption o = detail.Develop[i];
                    w.Write(o.Cap ?? "");
                    w.Write(o.Level);
                    w.Write(o.MaxLevel);
                    w.Write(o.Cost);
                    w.Write(o.Block);
                    w.Write(o.BlockKey ?? "");
                    w.Write(o.BlockHave);
                    w.Write(o.BlockNeed);
                    w.Write(o.Reserved);
                    w.Write(o.Priority);
                }

                // Hold upkeep. Written last so the block is a pure append against protocol 15's
                // layout, which keeps this diff readable next to the reader below.
                w.Write(detail.HoldOptIn);
                w.Write(detail.HoldRank);
                w.Write(detail.HoldCost);
                w.Write(detail.HoldBudgetIncomeFraction);
                w.Write(detail.HoldBudgetTreasuryFraction);
                w.Write(detail.HoldBudgetFloor);
                w.Write(detail.HoldBudgetOverrideMask);

                // Trade. Appended after the hold block for the same reason it was: protocol 19's
                // layout is untouched up to here.
                w.Write(detail.TradeBuyOptIn);
                w.Write(detail.TradeSellOptIn);
                w.Write(detail.TradePartners);
                w.Write(detail.TradeHasHull);
                w.Write(detail.TradeUnserved);
                w.Write(detail.TradeInFlight);
                w.Write(detail.TradeMaxFleets);
                w.Write(detail.TradeBudgetIncomeFraction);
                w.Write(detail.TradeBudgetTreasuryFraction);
                w.Write(detail.TradeBudgetFloor);
                w.Write(detail.TradeBudgetOverrideMask);

                // Repair queue. Appended after the trade block, as that block was after hold.
                int repairCount = detail.Repairs == null ? 0 : detail.Repairs.Length;
                if (repairCount > MaxRepairRowsPerNode) repairCount = MaxRepairRowsPerNode;
                w.Write(repairCount);
                for (int i = 0; i < repairCount; i++)
                {
                    WireNodeRepair rp = detail.Repairs[i];
                    w.Write(rp.PrefabId ?? "");
                    w.Write(rp.Count);
                    w.Write(rp.Position);
                    w.Write(rp.Integrity);
                    w.Write(rp.State);
                    w.Write(rp.BillTotal);
                    w.Write(rp.Collected);
                    w.Write(rp.QueuedUtcTicks);
                    w.Write(rp.PartsCompleteUtcTicks);
                    w.Write(rp.DoneUtcTicks);
                    int billCount = rp.Bill == null ? 0 : rp.Bill.Length;
                    if (billCount > MaxBomRowsPerBuild) billCount = MaxBomRowsPerBuild;
                    w.Write(billCount);
                    for (int c = 0; c < billCount; c++) WriteBuildComponent(w, rp.Bill[c]);
                }
                w.Write(detail.RepairHulls);

                // Production history. Appended after the repair block, as that block was after trade.
                int productionCount = detail.Production == null ? 0 : detail.Production.Length;
                if (productionCount > MaxProductionRowsPerNode) productionCount = MaxProductionRowsPerNode;
                w.Write(productionCount);
                for (int i = 0; i < productionCount; i++)
                {
                    WireNodeProductionSpan span = detail.Production[i];
                    w.Write(span.RecipeId ?? "");
                    w.Write(span.Key ?? "");
                    w.Write(span.IsOutput);
                    w.Write(span.Quantity);
                    w.Write(span.StartUtcTicks);
                    w.Write(span.EndUtcTicks);
                }
                w.Write(detail.ProductionWindowSeconds);

                // Demand, then what ended. Appended after the production block.
                int demandCount = detail.Demand == null ? 0 : detail.Demand.Length;
                if (demandCount > MaxDemandRowsPerNode) demandCount = MaxDemandRowsPerNode;
                w.Write(demandCount);
                for (int i = 0; i < demandCount; i++)
                {
                    WireNodeDemand d = detail.Demand[i];
                    w.Write(d.Kind ?? "");
                    w.Write(d.DestNodeId ?? "");
                    w.Write(d.Quantity);
                    w.Write(d.HasHave);
                    w.Write(d.Have);
                    w.Write(d.UsedPerHour);
                    w.Write(d.Class);
                    w.Write(d.DestinedForSale);
                    w.Write(d.FirstPostedUtcTicks);
                }
                int endedCount = detail.EndedDemand == null ? 0 : detail.EndedDemand.Length;
                if (endedCount > MaxEndedDemandRowsPerNode) endedCount = MaxEndedDemandRowsPerNode;
                w.Write(endedCount);
                for (int i = 0; i < endedCount; i++)
                {
                    WireNodeEndedDemand e = detail.EndedDemand[i];
                    w.Write(e.Kind ?? "");
                    w.Write(e.DestNodeId ?? "");
                    w.Write(e.PeakQuantity);
                    w.Write(e.Class);
                    w.Write(e.DestinedForSale);
                    w.Write(e.Consumed);
                    w.Write(e.FirstPostedUtcTicks);
                    w.Write(e.EndedUtcTicks);
                }
                w.Write(detail.DemandWindowSeconds);

                return ms.ToArray();
            }
        }

        public static bool TryReadNodeDetailSnapshot(byte[] data, out WireNodeDetail detail)
        {
            detail = new WireNodeDetail();
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeDetailSnapshot) return false;

                    var read = new WireNodeDetail();
                    read.Exists = r.ReadBoolean();
                    read.Id = r.ReadString();
                    read.DisplayName = r.ReadString();
                    read.DominantOre = r.ReadString();
                    read.Surveyed = r.ReadBoolean();
                    read.Zone = r.ReadByte();
                    read.OwnerTag = r.ReadString();
                    read.Relation = r.ReadByte();
                    read.HasStation = r.ReadBoolean();
                    read.XKm = r.ReadDouble();
                    read.YKm = r.ReadDouble();
                    read.ZKm = r.ReadDouble();
                    read.HoldStrength = r.ReadDouble();
                    read.ZoneState = r.ReadByte();
                    read.ZoneIntegrity = r.ReadDouble();
                    read.ZoneRaidProgress = r.ReadDouble();
                    read.ZoneUntilUtcTicks = r.ReadInt64();

                    read.Owned = r.ReadBoolean();
                    // Empty arrays rather than null throughout, so a caller never has to tell
                    // "not owned" apart from "owned and holding nothing" by a null check.
                    read.Capabilities = new WireAssetCapability[0];
                    read.Ores = new WireNodeOre[0];
                    read.Stock = new WireNodeStockItem[0];
                    read.Builds = new WireNodeBuild[0];
                    read.Ships = new WireNodeShip[0];
                    read.Develop = new WireNodeCapabilityOption[0];
                    read.Repairs = new WireNodeRepair[0];
                    read.Production = new WireNodeProductionSpan[0];
                    read.Demand = new WireNodeDemand[0];
                    read.EndedDemand = new WireNodeEndedDemand[0];
                    if (!read.Owned)
                    {
                        detail = read;
                        return true;
                    }

                    read.ResourceValue = r.ReadDouble();
                    read.Richness = r.ReadDouble();
                    read.FoundCost = r.ReadInt64();
                    read.FoundHave = r.ReadInt64();
                    read.FoundBlock = r.ReadByte();
                    read.ShipyardPaused = r.ReadBoolean();
                    read.HasStore = r.ReadBoolean();
                    read.Workers = r.ReadInt32();
                    read.WorkerBeds = r.ReadInt32();
                    read.RecruitPrice = r.ReadInt64();
                    read.WorkerFoodHours = r.ReadDouble();
                    read.WorkerAirHours = r.ReadDouble();
                    read.SurveyConfidence = r.ReadDouble();
                    read.ResourceValueTarget = r.ReadDouble();
                    read.RichnessTarget = r.ReadDouble();
                    read.ResolvedShareFloor = r.ReadDouble();

                    int oreCount = r.ReadInt32();
                    if (oreCount < 0 || oreCount > MaxOresPerNode) return false;
                    var ores = new WireNodeOre[oreCount];
                    for (int i = 0; i < oreCount; i++)
                    {
                        ores[i].SubTypeId = r.ReadString();
                        ores[i].Share = r.ReadDouble();
                        ores[i].PriceRatio = r.ReadDouble();
                        ores[i].WorthShare = r.ReadDouble();
                        ores[i].Resolved = r.ReadBoolean();
                    }
                    read.Ores = ores;

                    int capCount = r.ReadInt32();
                    if (capCount < 0 || capCount > MaxCapabilitiesPerNode) return false;
                    var caps = new WireAssetCapability[capCount];
                    for (int i = 0; i < capCount; i++)
                    {
                        caps[i].Cap = r.ReadString();
                        caps[i].Level = r.ReadInt32();
                    }
                    read.Capabilities = caps;

                    int stockCount = r.ReadInt32();
                    if (stockCount < 0 || stockCount > MaxStockItemsPerNode) return false;
                    var stock = new WireNodeStockItem[stockCount];
                    for (int i = 0; i < stockCount; i++)
                    {
                        stock[i].Key = r.ReadString();
                        stock[i].Quantity = r.ReadInt64();
                        stock[i].Reserved = r.ReadInt64();
                        stock[i].ForSale = r.ReadInt64();
                        stock[i].Target = r.ReadInt64();
                        stock[i].KeptFor = r.ReadByte();
                    }
                    read.Stock = stock;

                    int buildCount = r.ReadInt32();
                    if (buildCount < 0 || buildCount > MaxBuildsPerNode) return false;
                    var builds = new WireNodeBuild[buildCount];
                    for (int i = 0; i < buildCount; i++)
                    {
                        builds[i].Id = r.ReadString();
                        builds[i].Role = r.ReadString();
                        builds[i].PrefabId = r.ReadString();
                        builds[i].BomTotal = r.ReadInt64();
                        builds[i].Collected = r.ReadInt64();
                        builds[i].Priority = r.ReadInt32();
                        builds[i].ExpiresUtcTicks = r.ReadInt64();
                        builds[i].StalledSeconds = r.ReadDouble();
                        builds[i].CreatedUtcTicks = r.ReadInt64();
                        builds[i].Working = r.ReadBoolean();
                        builds[i].WaitingBehind = r.ReadInt32();

                        int bomCount = r.ReadInt32();
                        if (bomCount < 0 || bomCount > MaxBomRowsPerBuild) return false;
                        var bom = new WireBuildComponent[bomCount];
                        for (int c = 0; c < bomCount; c++)
                        {
                            bom[c].Key = r.ReadString();
                            bom[c].Need = r.ReadInt64();
                            bom[c].Have = r.ReadInt64();
                            bom[c].AtNode = r.ReadInt64();
                            bom[c].Elsewhere = r.ReadInt64();
                            bom[c].EnRoute = r.ReadInt64();
                            bom[c].Unserved = r.ReadBoolean();
                        }
                        builds[i].Bom = bom;

                        int reservedCount = r.ReadInt32();
                        if (reservedCount < 0 || reservedCount > MaxBomRowsPerBuild) return false;
                        var reserved = new WireBuildReservedInput[reservedCount];
                        for (int c = 0; c < reservedCount; c++)
                        {
                            reserved[c].Key = r.ReadString();
                            reserved[c].Quantity = r.ReadInt64();
                        }
                        builds[i].ReservedInputs = reserved;
                    }
                    read.Builds = builds;

                    int shipCount = r.ReadInt32();
                    if (shipCount < 0 || shipCount > MaxShipPrefabsPerNode) return false;
                    var ships = new WireNodeShip[shipCount];
                    for (int i = 0; i < shipCount; i++)
                    {
                        ships[i].PrefabId = r.ReadString();
                        ships[i].Ready = r.ReadInt32();
                        ships[i].ListedForSale = r.ReadInt32();
                    }
                    read.Ships = ships;

                    int developCount = r.ReadInt32();
                    if (developCount < 0 || developCount > MaxDevelopOptionsPerNode) return false;
                    var develop = new WireNodeCapabilityOption[developCount];
                    for (int i = 0; i < developCount; i++)
                    {
                        develop[i].Cap = r.ReadString();
                        develop[i].Level = r.ReadInt32();
                        develop[i].MaxLevel = r.ReadInt32();
                        develop[i].Cost = r.ReadInt64();
                        develop[i].Block = r.ReadByte();
                        develop[i].BlockKey = r.ReadString();
                        develop[i].BlockHave = r.ReadInt64();
                        develop[i].BlockNeed = r.ReadInt64();
                        develop[i].Reserved = r.ReadBoolean();
                        develop[i].Priority = r.ReadInt32();
                    }
                    read.Develop = develop;

                    read.HoldOptIn = r.ReadBoolean();
                    read.HoldRank = r.ReadInt32();
                    read.HoldCost = r.ReadInt64();
                    read.HoldBudgetIncomeFraction = r.ReadDouble();
                    read.HoldBudgetTreasuryFraction = r.ReadDouble();
                    read.HoldBudgetFloor = r.ReadInt64();
                    read.HoldBudgetOverrideMask = r.ReadByte();

                    read.TradeBuyOptIn = r.ReadBoolean();
                    read.TradeSellOptIn = r.ReadBoolean();
                    read.TradePartners = r.ReadInt32();
                    read.TradeHasHull = r.ReadBoolean();
                    read.TradeUnserved = r.ReadInt32();
                    read.TradeInFlight = r.ReadInt32();
                    read.TradeMaxFleets = r.ReadInt32();
                    read.TradeBudgetIncomeFraction = r.ReadDouble();
                    read.TradeBudgetTreasuryFraction = r.ReadDouble();
                    read.TradeBudgetFloor = r.ReadInt64();
                    read.TradeBudgetOverrideMask = r.ReadByte();

                    int repairCount = r.ReadInt32();
                    if (repairCount < 0 || repairCount > MaxRepairRowsPerNode) return false;
                    var repairs = new WireNodeRepair[repairCount];
                    for (int i = 0; i < repairCount; i++)
                    {
                        repairs[i].PrefabId = r.ReadString();
                        repairs[i].Count = r.ReadInt32();
                        repairs[i].Position = r.ReadInt32();
                        repairs[i].Integrity = r.ReadDouble();
                        repairs[i].State = r.ReadByte();
                        repairs[i].BillTotal = r.ReadInt64();
                        repairs[i].Collected = r.ReadInt64();
                        repairs[i].QueuedUtcTicks = r.ReadInt64();
                        repairs[i].PartsCompleteUtcTicks = r.ReadInt64();
                        repairs[i].DoneUtcTicks = r.ReadInt64();
                        int billCount = r.ReadInt32();
                        if (billCount < 0 || billCount > MaxBomRowsPerBuild) return false;
                        var bill = new WireBuildComponent[billCount];
                        for (int c = 0; c < billCount; c++) bill[c] = ReadBuildComponent(r);
                        repairs[i].Bill = bill;
                    }
                    read.Repairs = repairs;
                    read.RepairHulls = r.ReadInt32();

                    int productionCount = r.ReadInt32();
                    if (productionCount < 0 || productionCount > MaxProductionRowsPerNode) return false;
                    var production = new WireNodeProductionSpan[productionCount];
                    for (int i = 0; i < productionCount; i++)
                    {
                        production[i].RecipeId = r.ReadString();
                        production[i].Key = r.ReadString();
                        production[i].IsOutput = r.ReadBoolean();
                        production[i].Quantity = r.ReadInt64();
                        production[i].StartUtcTicks = r.ReadInt64();
                        production[i].EndUtcTicks = r.ReadInt64();
                    }
                    read.Production = production;
                    read.ProductionWindowSeconds = r.ReadInt32();

                    int demandCount = r.ReadInt32();
                    if (demandCount < 0 || demandCount > MaxDemandRowsPerNode) return false;
                    var demand = new WireNodeDemand[demandCount];
                    for (int i = 0; i < demandCount; i++)
                    {
                        demand[i].Kind = r.ReadString();
                        demand[i].DestNodeId = r.ReadString();
                        demand[i].Quantity = r.ReadInt64();
                        demand[i].HasHave = r.ReadBoolean();
                        demand[i].Have = r.ReadInt64();
                        demand[i].UsedPerHour = r.ReadDouble();
                        demand[i].Class = r.ReadByte();
                        demand[i].DestinedForSale = r.ReadBoolean();
                        demand[i].FirstPostedUtcTicks = r.ReadInt64();
                    }
                    read.Demand = demand;
                    int endedCount = r.ReadInt32();
                    if (endedCount < 0 || endedCount > MaxEndedDemandRowsPerNode) return false;
                    var ended = new WireNodeEndedDemand[endedCount];
                    for (int i = 0; i < endedCount; i++)
                    {
                        ended[i].Kind = r.ReadString();
                        ended[i].DestNodeId = r.ReadString();
                        ended[i].PeakQuantity = r.ReadInt64();
                        ended[i].Class = r.ReadByte();
                        ended[i].DestinedForSale = r.ReadBoolean();
                        ended[i].Consumed = r.ReadBoolean();
                        ended[i].FirstPostedUtcTicks = r.ReadInt64();
                        ended[i].EndedUtcTicks = r.ReadInt64();
                    }
                    read.EndedDemand = ended;
                    read.DemandWindowSeconds = r.ReadInt32();

                    detail = read;
                    return true;
                }
            }
            catch { detail = new WireNodeDetail(); return false; }
        }

        private static void WriteBuildComponent(BinaryWriter w, WireBuildComponent c)
        {
            w.Write(c.Key ?? "");
            w.Write(c.Need);
            w.Write(c.Have);
            w.Write(c.AtNode);
            w.Write(c.Elsewhere);
            w.Write(c.EnRoute);
            w.Write(c.Unserved);
        }

        private static WireBuildComponent ReadBuildComponent(BinaryReader r)
        {
            var c = new WireBuildComponent();
            c.Key = r.ReadString();
            c.Need = r.ReadInt64();
            c.Have = r.ReadInt64();
            c.AtNode = r.ReadInt64();
            c.Elsewhere = r.ReadInt64();
            c.EnRoute = r.ReadInt64();
            c.Unserved = r.ReadBoolean();
            return c;
        }

        /// <summary>One action against one node. <paramref name="target"/> is the build-entry id for
        /// the queue verbs and the capability name for the develop verbs — see
        /// <see cref="WireNodeCommand"/> for why it is one field — and is empty for the verbs that
        /// act on the shipyard itself. <paramref name="value"/> is meaningful only for the two
        /// priority verbs.</summary>
        public static byte[] WriteNodeCommandRequest(
            string nodeId, WireNodeCommand command, string target, int value)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeCommandRequest);
                w.Write(nodeId ?? "");
                w.Write((byte)command);
                w.Write(target ?? "");
                w.Write(value);
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeCommandRequest(
            byte[] data, out string nodeId, out WireNodeCommand command, out string target, out int value)
        {
            nodeId = null;
            command = WireNodeCommand.None;
            target = null;
            value = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeCommandRequest) return false;
                    string readNode = r.ReadString();
                    byte readCommand = r.ReadByte();
                    string readTarget = r.ReadString();
                    int readValue = r.ReadInt32();
                    if (readNode.Length > MaxCommandTokenLength) return false;
                    if (readTarget.Length > MaxCommandTokenLength) return false;
                    nodeId = readNode;
                    command = (WireNodeCommand)readCommand;
                    target = readTarget;
                    value = readValue;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteNodeCommandResult(bool ok, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeCommandResult);
                w.Write(ok);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeCommandResult(byte[] data, out bool ok, out string message)
        {
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeCommandResult) return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Node inventory -------------------------------------------------------------------

        /// <summary>Ask what may be moved at one node. Exactly one identifier is meaningful:
        /// <paramref name="stationId"/> for the store block, which knows the station it belongs to
        /// and nothing about the node graph, or <paramref name="nodeId"/> for the node window, which
        /// knows the node and may be looking at a claim with no station at all. Pass 0 and "" for
        /// the one that does not apply.</summary>
        public static byte[] WriteNodeStockRequest(long stationId, string nodeId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeStockRequest);
                w.Write(stationId);
                w.Write(nodeId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeStockRequest(byte[] data, out long stationId, out string nodeId)
        {
            stationId = 0;
            nodeId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeStockRequest) return false;
                    long readStation = r.ReadInt64();
                    string readNode = r.ReadString();
                    if (readNode.Length > MaxCommandTokenLength) return false;
                    stationId = readStation;
                    nodeId = readNode;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Answer one <see cref="WireMessageKind.NodeStockRequest"/>. A refusal carries no
        /// rows and says why in <paramref name="message"/> — the client shows the reason rather than
        /// an empty list, which reads as a station with nothing in it.</summary>
        public static byte[] WriteNodeStockSnapshot(
            string nodeId, string nodeName, bool mayTransfer, string message, WireNodeTransferRow[] rows)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeStockSnapshot);
                w.Write(nodeId ?? "");
                w.Write(nodeName ?? "");
                w.Write(mayTransfer);
                w.Write(message ?? "");
                int count = rows == null ? 0 : Math.Min(rows.Length, MaxTransferRowsPerNode);
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    w.Write(rows[i].Key ?? "");
                    w.Write(rows[i].Label ?? "");
                    w.Write(rows[i].Available);
                    w.Write(rows[i].Reserved);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeStockSnapshot(
            byte[] data, out string nodeId, out string nodeName, out bool mayTransfer,
            out string message, out WireNodeTransferRow[] rows)
        {
            nodeId = null;
            nodeName = null;
            mayTransfer = false;
            message = null;
            rows = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeStockSnapshot) return false;
                    string readNode = r.ReadString();
                    string readName = r.ReadString();
                    bool readMay = r.ReadBoolean();
                    string readMessage = r.ReadString();
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxTransferRowsPerNode) return false;
                    var read = new WireNodeTransferRow[count];
                    for (int i = 0; i < count; i++)
                    {
                        read[i].Key = r.ReadString();
                        read[i].Label = r.ReadString();
                        read[i].Available = r.ReadInt64();
                        read[i].Reserved = r.ReadInt64();
                    }
                    nodeId = readNode;
                    nodeName = readName;
                    mayTransfer = readMay;
                    message = readMessage;
                    rows = read;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Move one key between the node's stock and one physical inventory.
        /// <paramref name="deposit"/> false withdraws from the node into
        /// <paramref name="targetEntityId"/>; true takes from that entity and credits the node.
        /// <paramref name="amount"/> is what the player asked for, not a promise: the server moves
        /// what actually fits and what actually exists, and says so in the result.</summary>
        public static byte[] WriteNodeTransferRequest(
            string nodeId, bool deposit, string invKey, long amount, long targetEntityId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeTransferRequest);
                w.Write(nodeId ?? "");
                w.Write(deposit);
                w.Write(invKey ?? "");
                w.Write(amount);
                w.Write(targetEntityId);
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeTransferRequest(
            byte[] data, out string nodeId, out bool deposit, out string invKey,
            out long amount, out long targetEntityId)
        {
            nodeId = null;
            deposit = false;
            invKey = null;
            amount = 0;
            targetEntityId = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeTransferRequest) return false;
                    string readNode = r.ReadString();
                    bool readDeposit = r.ReadBoolean();
                    string readKey = r.ReadString();
                    long readAmount = r.ReadInt64();
                    long readTarget = r.ReadInt64();
                    if (readNode.Length > MaxCommandTokenLength) return false;
                    if (readKey.Length > MaxCommandTokenLength) return false;
                    nodeId = readNode;
                    deposit = readDeposit;
                    invKey = readKey;
                    amount = readAmount;
                    targetEntityId = readTarget;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>What the transfer did. <paramref name="moved"/> is separate from
        /// <paramref name="ok"/> because a partial move is the ordinary outcome — a hold fills, a
        /// node runs short — and a client that read only the verdict would report a half-filled
        /// container as a success with no number, or a refusal with goods already moved.</summary>
        public static byte[] WriteNodeTransferResult(bool ok, long moved, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeTransferResult);
                w.Write(ok);
                w.Write(moved);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeTransferResult(byte[] data, out bool ok, out long moved, out string message)
        {
            ok = false;
            moved = 0;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeTransferResult) return false;
                    ok = r.ReadBoolean();
                    moved = r.ReadInt64();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Events ---------------------------------------------------------------------------

        /// <summary>Subscribe to or unsubscribe from the viewer's faction history. Sent when the
        /// Events tab is entered and left; nothing is assembled server-side while nobody is looking.</summary>
        public static byte[] WriteEventsRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.EventsRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadEventsRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.EventsRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// The viewer's faction history, newest first. Full state each time: the ring is capped
        /// server-side, so the whole of it is cheaper to send than the bookkeeping a delta needs.
        ///
        /// <paramref name="factionTag"/> is empty for a player in no faction — a real state the tab
        /// explains rather than an error.
        /// </summary>
        public static byte[] WriteEventsSnapshot(string factionTag, IList<WireFactionEvent> events)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.EventsSnapshot);
                w.Write(factionTag ?? "");

                int count = events == null ? 0 : events.Count;
                if (count > MaxEventsPerMessage) count = MaxEventsPerMessage;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WireFactionEvent e = events[i];
                    w.Write(e.UtcTicks);
                    w.Write(e.Kind);
                    w.Write(e.ActorTag ?? "");
                    w.Write(e.OtherTag ?? "");
                    w.Write(e.NodeId ?? "");
                    w.Write(e.NodeName ?? "");
                    w.Write(e.Amount);
                    w.Write(e.Ratio);
                    w.Write(e.ViewerIsActor);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadEventsSnapshot(byte[] data, out string factionTag,
                                                 out List<WireFactionEvent> events)
        {
            factionTag = null;
            events = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.EventsSnapshot) return false;

                    string readTag = r.ReadString();

                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxEventsPerMessage) return false;
                    var readEvents = new List<WireFactionEvent>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var e = new WireFactionEvent();
                        e.UtcTicks = r.ReadInt64();
                        e.Kind = r.ReadByte();
                        e.ActorTag = r.ReadString();
                        e.OtherTag = r.ReadString();
                        e.NodeId = r.ReadString();
                        e.NodeName = r.ReadString();
                        e.Amount = r.ReadInt64();
                        e.Ratio = r.ReadDouble();
                        e.ViewerIsActor = r.ReadBoolean();
                        readEvents.Add(e);
                    }

                    factionTag = readTag;
                    events = readEvents;
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Campaigns ------------------------------------------------------------------------

        public static byte[] WriteCampaignsRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.CampaignsRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadCampaignsRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.CampaignsRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The costs ride along so the map's confirm can name what a directive will charge
        /// per dispatch without a second request; they are tuning, not per-campaign state.</summary>
        public static byte[] WriteCampaignsSnapshot(string factionTag, int cap, long raidCost, long conquerCost,
                                                    IList<WireCampaign> campaigns)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.CampaignsSnapshot);
                w.Write(factionTag ?? "");
                w.Write(cap);
                w.Write(raidCost);
                w.Write(conquerCost);
                int count = campaigns == null ? 0 : campaigns.Count;
                if (count > MaxCampaignsPerMessage) count = MaxCampaignsPerMessage;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WireCampaign c = campaigns[i];
                    w.Write(c.FocusId ?? "");
                    w.Write(c.Kind);
                    w.Write(c.Cause);
                    w.Write(c.TargetNodeId ?? "");
                    w.Write(c.TargetNodeName ?? "");
                    w.Write(c.RegionKey ?? "");
                    w.Write(c.StagingNodeId ?? "");
                    w.Write(c.ExpiresUtcTicks);
                    w.Write(c.LastDispatchUtcTicks);
                    w.Write(c.CandidateCount);
                    w.Write(c.PlayerIssued);
                    w.Write(c.LastOutcome);
                    w.Write(c.LastOutcomeUtcTicks);
                    w.Write(c.LastOutcomeRangeM);
                    w.Write(c.LastOutcomeOwnDps);
                    w.Write(c.LastOutcomeOpposedDps);
                    w.Write(c.LastOutcomeHullsLost);
                    w.Write(c.LastOutcomeHullsTotal);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadCampaignsSnapshot(byte[] data, out string factionTag, out int cap,
                                                    out long raidCost, out long conquerCost,
                                                    out List<WireCampaign> campaigns)
        {
            factionTag = null;
            cap = 0;
            raidCost = 0;
            conquerCost = 0;
            campaigns = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.CampaignsSnapshot) return false;
                    string readTag = r.ReadString();
                    int readCap = r.ReadInt32();
                    long readRaid = r.ReadInt64();
                    long readConquer = r.ReadInt64();
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxCampaignsPerMessage) return false;
                    var list = new List<WireCampaign>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var c = new WireCampaign();
                        c.FocusId = r.ReadString();
                        c.Kind = r.ReadByte();
                        c.Cause = r.ReadByte();
                        c.TargetNodeId = r.ReadString();
                        c.TargetNodeName = r.ReadString();
                        c.RegionKey = r.ReadString();
                        c.StagingNodeId = r.ReadString();
                        c.ExpiresUtcTicks = r.ReadInt64();
                        c.LastDispatchUtcTicks = r.ReadInt64();
                        c.CandidateCount = r.ReadInt32();
                        c.PlayerIssued = r.ReadBoolean();
                        c.LastOutcome = r.ReadByte();
                        c.LastOutcomeUtcTicks = r.ReadInt64();
                        c.LastOutcomeRangeM = r.ReadInt32();
                        c.LastOutcomeOwnDps = r.ReadInt32();
                        c.LastOutcomeOpposedDps = r.ReadInt32();
                        c.LastOutcomeHullsLost = r.ReadInt32();
                        c.LastOutcomeHullsTotal = r.ReadInt32();
                        list.Add(c);
                    }
                    factionTag = readTag;
                    cap = readCap;
                    raidCost = readRaid;
                    conquerCost = readConquer;
                    campaigns = list;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteCampaignCommandRequest(WireCampaignCommand command, string nodeId, byte kind,
                                                         bool regionScope, string focusId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.CampaignCommandRequest);
                w.Write((byte)command);
                w.Write(nodeId ?? "");
                w.Write(kind);
                w.Write(regionScope);
                w.Write(focusId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadCampaignCommandRequest(byte[] data, out WireCampaignCommand command,
                                                         out string nodeId, out byte kind, out bool regionScope,
                                                         out string focusId)
        {
            command = WireCampaignCommand.Place;
            nodeId = null;
            kind = 0;
            regionScope = false;
            focusId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind k;
                    if (!ReadHeader(r, out k) || k != WireMessageKind.CampaignCommandRequest) return false;
                    command = (WireCampaignCommand)r.ReadByte();
                    nodeId = r.ReadString();
                    kind = r.ReadByte();
                    regionScope = r.ReadBoolean();
                    focusId = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteCampaignCommandResult(bool ok, string message, string focusId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.CampaignCommandResult);
                w.Write(ok);
                w.Write(message ?? "");
                w.Write(focusId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadCampaignCommandResult(byte[] data, out bool ok, out string message, out string focusId)
        {
            ok = false;
            message = null;
            focusId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.CampaignCommandResult) return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    focusId = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Standing orders ------------------------------------------------------------------

        public static byte[] WriteStandingOrdersRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StandingOrdersRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadStandingOrdersRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StandingOrdersRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteStandingOrdersSnapshot(string factionTag, int cap, IList<WireStandingOrder> orders)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StandingOrdersSnapshot);
                w.Write(factionTag ?? "");
                w.Write(cap);

                int count = orders == null ? 0 : orders.Count;
                if (count > MaxStandingOrdersPerMessage) count = MaxStandingOrdersPerMessage;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WireStandingOrder o = orders[i];
                    w.Write(o.Id ?? "");
                    w.Write(o.Kind);
                    w.Write(o.Priority);
                    w.Write(o.Paused);
                    w.Write(o.HubNodeId ?? "");
                    w.Write(o.HubNodeName ?? "");
                    w.Write(o.PeriodSeconds);
                    w.Write(o.OreWeights ?? "");
                    w.Write(o.NextScanUtcTicks);
                    w.Write(o.ItemKey ?? "");
                    w.Write(o.ItemName ?? "");
                    w.Write(o.TargetQuantity);
                    w.Write(o.Have);
                    w.Write(o.AllStations);
                    w.Write(o.StationTarget);
                    int stations = o.StationIds == null || o.StationHaves == null
                        ? 0 : Math.Min(o.StationIds.Length, o.StationHaves.Length);
                    if (stations > MaxStationsPerStockOrder) stations = MaxStationsPerStockOrder;
                    w.Write(stations);
                    for (int s = 0; s < stations; s++)
                    {
                        w.Write(o.StationIds[s] ?? "");
                        w.Write(o.StationHaves[s]);
                    }
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadStandingOrdersSnapshot(byte[] data, out string factionTag,
                                                         out int cap, out List<WireStandingOrder> orders)
        {
            factionTag = null;
            cap = 0;
            orders = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StandingOrdersSnapshot) return false;

                    string readTag = r.ReadString();
                    int readCap = r.ReadInt32();

                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxStandingOrdersPerMessage) return false;
                    var readOrders = new List<WireStandingOrder>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var o = new WireStandingOrder();
                        o.Id = r.ReadString();
                        o.Kind = r.ReadByte();
                        o.Priority = r.ReadInt32();
                        o.Paused = r.ReadBoolean();
                        o.HubNodeId = r.ReadString();
                        o.HubNodeName = r.ReadString();
                        o.PeriodSeconds = r.ReadDouble();
                        o.OreWeights = r.ReadString();
                        o.NextScanUtcTicks = r.ReadInt64();
                        o.ItemKey = r.ReadString();
                        o.ItemName = r.ReadString();
                        o.TargetQuantity = r.ReadInt64();
                        o.Have = r.ReadInt64();
                        o.AllStations = r.ReadBoolean();
                        o.StationTarget = r.ReadInt64();
                        int stations = r.ReadInt32();
                        if (stations < 0 || stations > MaxStationsPerStockOrder) return false;
                        if (stations > 0)
                        {
                            o.StationIds = new string[stations];
                            o.StationHaves = new long[stations];
                            for (int s = 0; s < stations; s++)
                            {
                                o.StationIds[s] = r.ReadString();
                                o.StationHaves[s] = r.ReadInt64();
                            }
                        }
                        readOrders.Add(o);
                    }

                    factionTag = readTag;
                    cap = readCap;
                    orders = readOrders;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteStandingOrderCommandRequest(string orderId, WireStandingOrderCommand command, int value)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StandingOrderCommandRequest);
                w.Write(orderId ?? "");
                w.Write((byte)command);
                w.Write(value);
                return ms.ToArray();
            }
        }

        public static bool TryReadStandingOrderCommandRequest(byte[] data, out string orderId,
                                                              out WireStandingOrderCommand command, out int value)
        {
            orderId = null;
            command = WireStandingOrderCommand.Remove;
            value = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StandingOrderCommandRequest) return false;
                    orderId = r.ReadString();
                    command = (WireStandingOrderCommand)r.ReadByte();
                    value = r.ReadInt32();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteStandingOrderCommandResult(bool ok, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StandingOrderCommandResult);
                w.Write(ok);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadStandingOrderCommandResult(byte[] data, out bool ok, out string message)
        {
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StandingOrderCommandResult) return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The hub pick-list for a new Mine order. The prefab list this message carried
        /// until ProtocolVersion 23 went with the Build goal it was for: choosing a hull is the Fleet
        /// window's job now, and it sends <see cref="WireBuildPrefab"/> rows with the numbers the
        /// choice is actually made on rather than bare ids.</summary>
        public static byte[] WriteStandingOrderOptions(IList<WireOrderOption> nodes)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StandingOrderOptions);
                WriteOptionList(w, nodes);
                return ms.ToArray();
            }
        }

        public static bool TryReadStandingOrderOptions(byte[] data, out List<WireOrderOption> nodes)
        {
            nodes = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StandingOrderOptions) return false;
                    List<WireOrderOption> readNodes;
                    if (!TryReadOptionList(r, out readNodes)) return false;
                    nodes = readNodes;
                    return true;
                }
            }
            catch { return false; }
        }

        private static void WriteOptionList(BinaryWriter w, IList<WireOrderOption> options)
        {
            int count = options == null ? 0 : options.Count;
            if (count > MaxOrderOptionsPerMessage) count = MaxOrderOptionsPerMessage;
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                WireOrderOption o = options[i];
                w.Write(o.Id ?? "");
                w.Write(o.Label ?? "");
                w.Write(o.Flags);
            }
        }

        private static bool TryReadOptionList(BinaryReader r, out List<WireOrderOption> options)
        {
            options = null;
            int count = r.ReadInt32();
            if (count < 0 || count > MaxOrderOptionsPerMessage) return false;
            var read = new List<WireOrderOption>(count);
            for (int i = 0; i < count; i++)
            {
                var o = new WireOrderOption();
                o.Id = r.ReadString();
                o.Label = r.ReadString();
                o.Flags = r.ReadInt32();
                read.Add(o);
            }
            options = read;
            return true;
        }

        /// <summary>Creates one standing order at <paramref name="nodeId"/>. A Mine order (kind 0)
        /// reads the period and ore weights, a Stock order (kind 1) the item key and quantity. The
        /// server validates every field regardless — this is the same entry point the chat verb
        /// reaches.</summary>
        public static byte[] WriteStandingOrderCreateRequest(
            byte kind, string nodeId, double periodSeconds, string oreWeights,
            string itemKey = null, long quantity = 0)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StandingOrderCreateRequest);
                w.Write(kind);
                w.Write(nodeId ?? "");
                w.Write(periodSeconds);
                w.Write(oreWeights ?? "");
                w.Write(itemKey ?? "");
                w.Write(quantity);
                return ms.ToArray();
            }
        }

        public static bool TryReadStandingOrderCreateRequest(
            byte[] data, out byte kind, out string nodeId, out double periodSeconds, out string oreWeights,
            out string itemKey, out long quantity)
        {
            kind = 0;
            nodeId = null;
            periodSeconds = 0;
            oreWeights = null;
            itemKey = null;
            quantity = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind messageKind;
                    if (!ReadHeader(r, out messageKind)
                        || messageKind != WireMessageKind.StandingOrderCreateRequest) return false;
                    kind = r.ReadByte();
                    nodeId = r.ReadString();
                    periodSeconds = r.ReadDouble();
                    oreWeights = r.ReadString();
                    itemKey = r.ReadString();
                    quantity = r.ReadInt64();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteStockItemOptions(IList<WireStockItem> items)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StockItemOptions);
                int count = items == null ? 0 : items.Count;
                if (count > MaxStockItemsPerMessage) count = MaxStockItemsPerMessage;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WireStockItem it = items[i];
                    w.Write(it.Key ?? "");
                    w.Write(it.Name ?? "");
                    w.Write(it.Category ?? "");
                    w.Write(it.Source ?? "");
                    w.Write(it.Flags);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadStockItemOptions(byte[] data, out List<WireStockItem> items)
        {
            items = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StockItemOptions) return false;
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxStockItemsPerMessage) return false;
                    var read = new List<WireStockItem>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var it = new WireStockItem();
                        it.Key = r.ReadString();
                        it.Name = r.ReadString();
                        it.Category = r.ReadString();
                        it.Source = r.ReadString();
                        it.Flags = r.ReadInt32();
                        read.Add(it);
                    }
                    items = read;
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Feature rock ---------------------------------------------------------------------

        /// <summary>
        /// The rock that entered this player's neighbourhood, and the storage names of the rock that
        /// left. Same delta shape as <see cref="WriteScannerContacts"/>, and <paramref name="reset"/>
        /// means the same thing: forget everything you hold, this message is the whole truth.
        ///
        /// <para>A reset is what heals divergence. The server refuses to replicate pristine rock to a
        /// client holding this feature, so a client whose set has drifted is not merely showing a
        /// stale list -- it is standing in geometry nobody will ever send it. Periodically restating
        /// the whole set costs one message and removes that as a permanent failure.</para>
        ///
        /// <para><paramref name="removed"/> carries storage names rather than entity ids so a client
        /// that never managed to build a rock can still forget it.</para>
        ///
        /// <para><paramref name="generatorVersion"/> is a property of the world, not of a rock, and
        /// rides on every message so a client can only ever use the server's answer. It selects which
        /// of SE's asteroid generators builds the shape: two ends disagreeing about it do not produce
        /// slightly different rock, they produce entirely different rock from the same seed. It is
        /// operator-tunable server-side (<c>FeatureRockGeneratorVersion</c>), which is exactly why the
        /// client must never carry a default of its own.</para>
        /// </summary>
        // ---- Market ----------------------------------------------------------------------------

        public static byte[] WriteMarketRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.MarketRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadMarketRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.MarketRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The whole market table the viewer may see. Items and stations are interned once
        /// and referenced by index from every row.</summary>
        public static byte[] WriteMarketSnapshot(
            IList<string> items, IList<WireMarketStation> stations, IList<WireMarketRow> rows)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.MarketSnapshot);

                int itemCount = items == null ? 0 : items.Count;
                w.Write(itemCount);
                for (int i = 0; i < itemCount; i++) w.Write(items[i] ?? "");

                int stationCount = stations == null ? 0 : stations.Count;
                w.Write(stationCount);
                for (int i = 0; i < stationCount; i++)
                {
                    WireMarketStation st = stations[i];
                    w.Write(st.NodeId ?? "");
                    w.Write(st.DisplayName ?? "");
                    w.Write(st.OwnerTag ?? "");
                    w.Write(st.Relation);
                    w.Write(st.XKm);
                    w.Write(st.YKm);
                    w.Write(st.ZKm);
                    w.Write(st.Live);
                    w.Write(st.AsOfUtcTicks);
                }

                int rowCount = rows == null ? 0 : rows.Count;
                w.Write(rowCount);
                for (int i = 0; i < rowCount; i++)
                {
                    WireMarketRow row = rows[i];
                    w.Write(row.StationIndex);
                    w.Write(row.ItemIndex);
                    w.Write(row.Side);
                    w.Write(row.UnitPrice);
                    w.Write(row.Quantity);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadMarketSnapshot(
            byte[] data, out List<string> items, out List<WireMarketStation> stations,
            out List<WireMarketRow> rows)
        {
            items = null;
            stations = null;
            rows = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.MarketSnapshot) return false;

                    int itemCount = r.ReadInt32();
                    if (itemCount < 0 || itemCount > MaxMarketItemsPerMessage) return false;
                    var itemList = new List<string>(itemCount);
                    for (int i = 0; i < itemCount; i++) itemList.Add(r.ReadString());

                    int stationCount = r.ReadInt32();
                    if (stationCount < 0 || stationCount > MaxMarketStationsPerMessage) return false;
                    var stationList = new List<WireMarketStation>(stationCount);
                    for (int i = 0; i < stationCount; i++)
                    {
                        stationList.Add(new WireMarketStation
                        {
                            NodeId       = r.ReadString(),
                            DisplayName  = r.ReadString(),
                            OwnerTag     = r.ReadString(),
                            Relation     = r.ReadByte(),
                            XKm          = r.ReadDouble(),
                            YKm          = r.ReadDouble(),
                            ZKm          = r.ReadDouble(),
                            Live         = r.ReadBoolean(),
                            AsOfUtcTicks = r.ReadInt64(),
                        });
                    }

                    int rowCount = r.ReadInt32();
                    if (rowCount < 0 || rowCount > MaxMarketRowsPerMessage) return false;
                    var rowList = new List<WireMarketRow>(rowCount);
                    for (int i = 0; i < rowCount; i++)
                    {
                        var row = new WireMarketRow
                        {
                            StationIndex = r.ReadUInt16(),
                            ItemIndex    = r.ReadUInt16(),
                            Side         = r.ReadByte(),
                            UnitPrice    = r.ReadInt32(),
                            Quantity     = r.ReadInt32(),
                        };
                        // An index past its table is a corrupt or mismatched payload, not a row to
                        // drop quietly -- a reader that skipped it would render a partial market and
                        // look correct doing it.
                        if (row.StationIndex >= stationList.Count || row.ItemIndex >= itemList.Count)
                            return false;
                        rowList.Add(row);
                    }

                    items = itemList;
                    stations = stationList;
                    rows = rowList;
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Expand ---------------------------------------------------------------------------

        /// <summary>Subscribe, unsubscribe, or commit. <paramref name="quoteId"/> is the id of the
        /// status the client is acting on; the server rejects a commit that does not match the status
        /// it last sent, so a stale window cannot spend a wallet against a verdict that has since
        /// changed. Ignored for the other two actions.</summary>
        public static byte[] WriteExpandRequest(WireExpandAction action, int quoteId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ExpandRequest);
                w.Write((byte)action);
                w.Write(quoteId);
                return ms.ToArray();
            }
        }

        public static bool TryReadExpandRequest(byte[] data, out WireExpandAction action, out int quoteId)
        {
            action = WireExpandAction.Unsubscribe;
            quoteId = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ExpandRequest) return false;
                    byte raw = r.ReadByte();
                    // An action this build does not recognise is refused, not coerced: every value
                    // this enum could grow into is a state change, and running the wrong one is worse
                    // than dropping the message.
                    if (raw > (byte)WireExpandAction.Commit) return false;
                    action = (WireExpandAction)raw;
                    quoteId = r.ReadInt32();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The verdict at the player's own position, and — after a commit — what that
        /// commit did. <paramref name="committed"/> separates "this is what you would get" from
        /// "this is what you got", which is the difference between a readout tick and a purchase.</summary>
        public static byte[] WriteExpandStatus(int quoteId, bool clear, byte zone, double separationKm,
                                               double placedXKm, double placedYKm, double placedZKm,
                                               double snapDistanceKm, long costCredits,
                                               bool chargedToPlayer, bool committed,
                                               WireExpandVerdict verdict, string nodeId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ExpandStatus);
                w.Write(quoteId);
                w.Write(clear);
                w.Write(zone);
                w.Write(separationKm);
                w.Write(placedXKm);
                w.Write(placedYKm);
                w.Write(placedZKm);
                w.Write(snapDistanceKm);
                w.Write(costCredits);
                w.Write(chargedToPlayer);
                w.Write(committed);
                w.Write((byte)verdict);
                w.Write(nodeId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadExpandStatus(byte[] data, out WireExpandStatus status)
        {
            status = new WireExpandStatus();
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ExpandStatus) return false;
                    status.QuoteId         = r.ReadInt32();
                    status.Clear           = r.ReadBoolean();
                    status.Zone            = r.ReadByte();
                    status.SeparationKm    = r.ReadDouble();
                    status.PlacedXKm       = r.ReadDouble();
                    status.PlacedYKm       = r.ReadDouble();
                    status.PlacedZKm       = r.ReadDouble();
                    status.SnapDistanceKm  = r.ReadDouble();
                    status.CostCredits     = r.ReadInt64();
                    status.ChargedToPlayer = r.ReadBoolean();
                    status.Committed       = r.ReadBoolean();
                    status.Verdict         = (WireExpandVerdict)r.ReadByte();
                    status.NodeId          = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Ship assets ------------------------------------------------------------------------

        /// <summary>Ask what ships the caller's faction may withdraw at one Services Terminal.
        /// <paramref name="terminalEntityId"/> is the block the player is standing at; the server
        /// resolves it to a station, a node and an owner before answering, so this is a claim to be
        /// checked and not an identity the client has established.</summary>
        public static byte[] WriteTerminalAssetsRequest(long terminalEntityId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.TerminalAssetsRequest);
                w.Write(terminalEntityId);
                return ms.ToArray();
            }
        }

        public static bool TryReadTerminalAssetsRequest(byte[] data, out long terminalEntityId)
        {
            terminalEntityId = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.TerminalAssetsRequest) return false;
                    terminalEntityId = r.ReadInt64();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Answer one <see cref="WireMessageKind.TerminalAssetsRequest"/>. The terminal id
        /// is echoed so a client that has moved between blocks can drop an answer to a question it
        /// no longer has open. A refusal carries no rows and says why in
        /// <paramref name="message"/> — an empty list alone reads as a faction with no ships.</summary>
        public static byte[] WriteTerminalAssetsSnapshot(
            long terminalEntityId, string nodeId, string nodeName, bool mayUse, string message,
            WireShipAssetRow[] rows)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.TerminalAssetsSnapshot);
                w.Write(terminalEntityId);
                w.Write(nodeId ?? "");
                w.Write(nodeName ?? "");
                w.Write(mayUse);
                w.Write(message ?? "");
                int count = rows == null ? 0 : Math.Min(rows.Length, MaxShipAssetRows);
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    w.Write(rows[i].NodeId ?? "");
                    w.Write(rows[i].NodeName ?? "");
                    w.Write(rows[i].PrefabId ?? "");
                    w.Write(rows[i].Count);
                    w.Write(rows[i].Blocks);
                    w.Write(rows[i].Pcu);
                    w.Write(rows[i].GridSize ?? "");
                    w.Write(rows[i].DistanceKm);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadTerminalAssetsSnapshot(
            byte[] data, out long terminalEntityId, out string nodeId, out string nodeName,
            out bool mayUse, out string message, out WireShipAssetRow[] rows)
        {
            terminalEntityId = 0;
            nodeId = null;
            nodeName = null;
            mayUse = false;
            message = null;
            rows = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.TerminalAssetsSnapshot) return false;
                    long readTerminal = r.ReadInt64();
                    string readNode = r.ReadString();
                    string readName = r.ReadString();
                    bool readMay = r.ReadBoolean();
                    string readMessage = r.ReadString();
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxShipAssetRows) return false;
                    var read = new WireShipAssetRow[count];
                    for (int i = 0; i < count; i++)
                    {
                        read[i].NodeId = r.ReadString();
                        read[i].NodeName = r.ReadString();
                        read[i].PrefabId = r.ReadString();
                        read[i].Count = r.ReadInt32();
                        read[i].Blocks = r.ReadInt32();
                        read[i].Pcu = r.ReadInt32();
                        read[i].GridSize = r.ReadString();
                        read[i].DistanceKm = r.ReadDouble();
                    }
                    terminalEntityId = readTerminal;
                    nodeId = readNode;
                    nodeName = readName;
                    mayUse = readMay;
                    message = readMessage;
                    rows = read;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Take one hull of <paramref name="prefabId"/> out of the node's stock at
        /// <paramref name="nodeId"/>, which need not be the terminal's own node — that is what makes
        /// a withdrawal cost vanilla's transit time. One unit per request: the count is a row's
        /// display, not a quantity the player may batch.</summary>
        public static byte[] WriteShipWithdrawRequest(long terminalEntityId, string nodeId, string prefabId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ShipWithdrawRequest);
                w.Write(terminalEntityId);
                w.Write(nodeId ?? "");
                w.Write(prefabId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadShipWithdrawRequest(
            byte[] data, out long terminalEntityId, out string nodeId, out string prefabId)
        {
            terminalEntityId = 0;
            nodeId = null;
            prefabId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ShipWithdrawRequest) return false;
                    long readTerminal = r.ReadInt64();
                    string readNode = r.ReadString();
                    string readPrefab = r.ReadString();
                    if (readNode.Length > MaxCommandTokenLength) return false;
                    if (readPrefab.Length > MaxCommandTokenLength) return false;
                    terminalEntityId = readTerminal;
                    nodeId = readNode;
                    prefabId = readPrefab;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>What the withdrawal produced. <paramref name="guid"/> is the vanilla stored-grid
        /// entry the client then hands to its own retrieval proxy, and is written as the raw 16
        /// bytes rather than as text: it is SE's own key, and a string round trip is one more place
        /// for the two ends to disagree about a value neither of them may reformat.
        /// <see cref="Guid.Empty"/> on a refusal.</summary>
        public static byte[] WriteShipWithdrawResult(bool ok, Guid guid, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ShipWithdrawResult);
                w.Write(ok);
                w.Write(guid.ToByteArray());
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadShipWithdrawResult(byte[] data, out bool ok, out Guid guid, out string message)
        {
            ok = false;
            guid = Guid.Empty;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ShipWithdrawResult) return false;
                    bool readOk = r.ReadBoolean();
                    byte[] raw = r.ReadBytes(16);
                    // ReadBytes returns short rather than throwing at the end of the stream, so the
                    // length is the truncation check for this field.
                    if (raw == null || raw.Length != 16) return false;
                    string readMessage = r.ReadString();
                    ok = readOk;
                    guid = new Guid(raw);
                    message = readMessage;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Send one live grid to the node: despawn it and credit the hull and its cargo to
        /// the terminal's node. The grid is named by entity id and everything else — ownership,
        /// distance, repair state, the prefab match, the cargo keys — is the server's to decide.</summary>
        public static byte[] WriteShipSendRequest(long terminalEntityId, long gridEntityId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ShipSendRequest);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                return ms.ToArray();
            }
        }

        public static bool TryReadShipSendRequest(byte[] data, out long terminalEntityId, out long gridEntityId)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ShipSendRequest) return false;
                    terminalEntityId = r.ReadInt64();
                    gridEntityId = r.ReadInt64();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>What the send did. <paramref name="prefabId"/> is carried separately from the
        /// verdict because the match is a block fingerprint: on success it names the prefab the hull
        /// became, which is the one thing the player could not have known, and it is empty on a
        /// refusal — including the refusal that says no prefab matched at all.</summary>
        public static byte[] WriteShipSendResult(bool ok, string prefabId, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ShipSendResult);
                w.Write(ok);
                w.Write(prefabId ?? "");
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadShipSendResult(byte[] data, out bool ok, out string prefabId, out string message)
        {
            ok = false;
            prefabId = null;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ShipSendResult) return false;
                    ok = r.ReadBoolean();
                    prefabId = r.ReadString();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Scan one live grid into a new prefab — the terminal's form of the chat capture
        /// verb. <paramref name="scope"/> is the capture's ownership scope ("own" or "all") and
        /// <paramref name="name"/> the name the dialog collected; both are sanitised server-side by
        /// the same code the chat verb uses, so they cross as the player typed them.</summary>
        public static byte[] WriteShipScanRequest(long terminalEntityId, long gridEntityId, string scope, string name)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ShipScanRequest);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                w.Write(scope ?? "");
                w.Write(name ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadShipScanRequest(
            byte[] data, out long terminalEntityId, out long gridEntityId, out string scope, out string name)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            scope = null;
            name = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ShipScanRequest) return false;
                    long readTerminal = r.ReadInt64();
                    long readGrid = r.ReadInt64();
                    string readScope = r.ReadString();
                    string readName = r.ReadString();
                    if (readScope.Length > MaxCommandTokenLength) return false;
                    if (readName.Length > MaxCommandTokenLength) return false;
                    terminalEntityId = readTerminal;
                    gridEntityId = readGrid;
                    scope = readScope;
                    name = readName;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>What the scan did, in the words the chat verb would have used. Advisory: the
        /// snapshot the client re-requests afterwards is the authoritative answer.</summary>
        public static byte[] WriteShipScanResult(bool ok, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.ShipScanResult);
                w.Write(ok);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadShipScanResult(byte[] data, out bool ok, out string message)
        {
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.ShipScanResult) return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Dock supply ----------------------------------------------------------------------

        /// <summary>Quote (<paramref name="commit"/> false) or commit one gas fill of the ship
        /// <paramref name="gridEntityId"/> docked at the terminal. <paramref name="maxCredits"/> is
        /// the quoted total on a commit — the server charges no more — and ignored on a quote.</summary>
        public static byte[] WriteDockRefuelRequest(long terminalEntityId, long gridEntityId,
            WireDockGas gas, bool commit, long maxCredits)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.DockRefuelRequest);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                w.Write((byte)gas);
                w.Write(commit);
                w.Write(maxCredits);
                return ms.ToArray();
            }
        }

        public static bool TryReadDockRefuelRequest(byte[] data, out long terminalEntityId,
            out long gridEntityId, out WireDockGas gas, out bool commit, out long maxCredits)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            gas = WireDockGas.Hydrogen;
            commit = false;
            maxCredits = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.DockRefuelRequest) return false;
                    long readTerminal = r.ReadInt64();
                    long readGrid = r.ReadInt64();
                    byte readGas = r.ReadByte();
                    if (readGas > (byte)WireDockGas.Oxygen) return false;
                    bool readCommit = r.ReadBoolean();
                    long readMax = r.ReadInt64();
                    terminalEntityId = readTerminal;
                    gridEntityId = readGrid;
                    gas = (WireDockGas)readGas;
                    commit = readCommit;
                    maxCredits = readMax;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary><paramref name="quote"/> true means nothing moved yet and the client should
        /// confirm <paramref name="credits"/>; false with <paramref name="ok"/> is a delivery.</summary>
        public static byte[] WriteDockRefuelResult(WireDockGas gas, bool ok, bool quote, long litres,
            long credits, string message)
        {
            return WriteDockRefuelResult(gas, ok, quote, litres, credits, message, 0, null);
        }

        /// <summary>With a receipt: <paramref name="balanceAfter"/> and one line per item delivered.
        /// Appended after the message so a reader that stops there — the first client to carry this
        /// kind — reads the same message it always did.</summary>
        public static byte[] WriteDockRefuelResult(WireDockGas gas, bool ok, bool quote, long litres,
            long credits, string message, long balanceAfter, WireReceiptLine[] receipt)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.DockRefuelResult);
                w.Write((byte)gas);
                w.Write(ok);
                w.Write(quote);
                w.Write(litres);
                w.Write(credits);
                w.Write(message ?? "");
                WriteReceiptTail(w, balanceAfter, receipt);
                return ms.ToArray();
            }
        }

        public static bool TryReadDockRefuelResult(byte[] data, out WireDockGas gas, out bool ok,
            out bool quote, out long litres, out long credits, out string message)
        {
            long balanceAfter;
            WireReceiptLine[] receipt;
            return TryReadDockRefuelResult(data, out gas, out ok, out quote, out litres, out credits,
                out message, out balanceAfter, out receipt);
        }

        /// <summary>The receipt tail is optional: a result from a server that predates it reads with
        /// <paramref name="receipt"/> empty.</summary>
        public static bool TryReadDockRefuelResult(byte[] data, out WireDockGas gas, out bool ok,
            out bool quote, out long litres, out long credits, out string message,
            out long balanceAfter, out WireReceiptLine[] receipt)
        {
            balanceAfter = 0;
            receipt = new WireReceiptLine[0];
            gas = WireDockGas.Hydrogen;
            ok = false;
            quote = false;
            litres = 0;
            credits = 0;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.DockRefuelResult) return false;
                    byte readGas = r.ReadByte();
                    if (readGas > (byte)WireDockGas.Oxygen) return false;
                    bool readOk = r.ReadBoolean();
                    bool readQuote = r.ReadBoolean();
                    long readLitres = r.ReadInt64();
                    long readCredits = r.ReadInt64();
                    string readMessage = r.ReadString();
                    long readBalance;
                    WireReceiptLine[] readReceipt;
                    if (!TryReadReceiptTail(ms, r, out readBalance, out readReceipt)) return false;
                    gas = (WireDockGas)readGas;
                    ok = readOk;
                    quote = readQuote;
                    litres = readLitres;
                    credits = readCredits;
                    message = readMessage;
                    balanceAfter = readBalance;
                    receipt = readReceipt;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteRearmSurveyRequest(long terminalEntityId, long gridEntityId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.RearmSurveyRequest);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                return ms.ToArray();
            }
        }

        public static bool TryReadRearmSurveyRequest(byte[] data, out long terminalEntityId, out long gridEntityId)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.RearmSurveyRequest) return false;
                    long readTerminal = r.ReadInt64();
                    long readGrid = r.ReadInt64();
                    terminalEntityId = readTerminal;
                    gridEntityId = readGrid;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary><paramref name="ok"/> false carries only the refusal in
        /// <paramref name="message"/>; the arrays are then empty.</summary>
        public static byte[] WriteRearmSurvey(long terminalEntityId, long gridEntityId, bool ok,
            string message, long balance, double cargoFreeLitres,
            WireRearmMagazine[] magazines, WireRearmGroup[] groups)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.RearmSurvey);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                w.Write(ok);
                w.Write(message ?? "");
                w.Write(balance);
                w.Write(cargoFreeLitres);
                int magCount = magazines == null ? 0 : Math.Min(magazines.Length, MaxRearmMagazines);
                w.Write(magCount);
                for (int i = 0; i < magCount; i++)
                {
                    w.Write(magazines[i].Key ?? "");
                    w.Write(magazines[i].Name ?? "");
                    w.Write(magazines[i].UnitPrice);
                    w.Write(magazines[i].Available);
                    w.Write(magazines[i].UnitLitres);
                }
                int groupCount = groups == null ? 0 : Math.Min(groups.Length, MaxRearmGroups);
                w.Write(groupCount);
                for (int i = 0; i < groupCount; i++)
                {
                    w.Write(groups[i].GroupId ?? "");
                    w.Write(groups[i].Name ?? "");
                    w.Write(groups[i].Blocks);
                    w.Write(groups[i].CurrentKey ?? "");
                    WireRearmOption[] options = groups[i].Options;
                    int optionCount = options == null ? 0 : Math.Min(options.Length, MaxRearmOptionsPerGroup);
                    w.Write(optionCount);
                    for (int j = 0; j < optionCount; j++)
                    {
                        w.Write(options[j].Key ?? "");
                        w.Write(options[j].Fits);
                    }
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadRearmSurvey(byte[] data, out long terminalEntityId, out long gridEntityId,
            out bool ok, out string message, out long balance, out double cargoFreeLitres,
            out WireRearmMagazine[] magazines, out WireRearmGroup[] groups)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            ok = false;
            message = null;
            balance = 0;
            cargoFreeLitres = 0;
            magazines = null;
            groups = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.RearmSurvey) return false;
                    long readTerminal = r.ReadInt64();
                    long readGrid = r.ReadInt64();
                    bool readOk = r.ReadBoolean();
                    string readMessage = r.ReadString();
                    long readBalance = r.ReadInt64();
                    double readCargo = r.ReadDouble();

                    int magCount = r.ReadInt32();
                    if (magCount < 0 || magCount > MaxRearmMagazines) return false;
                    var readMags = new WireRearmMagazine[magCount];
                    for (int i = 0; i < magCount; i++)
                    {
                        readMags[i].Key = r.ReadString();
                        readMags[i].Name = r.ReadString();
                        readMags[i].UnitPrice = r.ReadInt64();
                        readMags[i].Available = r.ReadInt64();
                        readMags[i].UnitLitres = r.ReadDouble();
                    }

                    int groupCount = r.ReadInt32();
                    if (groupCount < 0 || groupCount > MaxRearmGroups) return false;
                    var readGroups = new WireRearmGroup[groupCount];
                    for (int i = 0; i < groupCount; i++)
                    {
                        readGroups[i].GroupId = r.ReadString();
                        readGroups[i].Name = r.ReadString();
                        readGroups[i].Blocks = r.ReadInt32();
                        readGroups[i].CurrentKey = r.ReadString();
                        int optionCount = r.ReadInt32();
                        if (optionCount < 0 || optionCount > MaxRearmOptionsPerGroup) return false;
                        var options = new WireRearmOption[optionCount];
                        for (int j = 0; j < optionCount; j++)
                        {
                            options[j].Key = r.ReadString();
                            options[j].Fits = r.ReadInt64();
                        }
                        readGroups[i].Options = options;
                    }

                    terminalEntityId = readTerminal;
                    gridEntityId = readGrid;
                    ok = readOk;
                    message = readMessage;
                    balance = readBalance;
                    cargoFreeLitres = readCargo;
                    magazines = readMags;
                    groups = readGroups;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteRearmRequest(long terminalEntityId, long gridEntityId, long maxCredits,
            WireRearmPick[] picks, WireRearmReserve[] reserves)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.RearmRequest);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                w.Write(maxCredits);
                int pickCount = picks == null ? 0 : Math.Min(picks.Length, MaxRearmGroups);
                w.Write(pickCount);
                for (int i = 0; i < pickCount; i++)
                {
                    w.Write(picks[i].GroupId ?? "");
                    w.Write(picks[i].Key ?? "");
                }
                int reserveCount = reserves == null ? 0 : Math.Min(reserves.Length, MaxRearmMagazines);
                w.Write(reserveCount);
                for (int i = 0; i < reserveCount; i++)
                {
                    w.Write(reserves[i].Key ?? "");
                    w.Write(reserves[i].Count);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Every string is a client claim bounded by <see cref="MaxCommandTokenLength"/>;
        /// the server then matches each against the survey it recomputes, never against the text
        /// alone.</summary>
        public static bool TryReadRearmRequest(byte[] data, out long terminalEntityId, out long gridEntityId,
            out long maxCredits, out WireRearmPick[] picks, out WireRearmReserve[] reserves)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            maxCredits = 0;
            picks = null;
            reserves = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.RearmRequest) return false;
                    long readTerminal = r.ReadInt64();
                    long readGrid = r.ReadInt64();
                    long readMax = r.ReadInt64();

                    int pickCount = r.ReadInt32();
                    if (pickCount < 0 || pickCount > MaxRearmGroups) return false;
                    var readPicks = new WireRearmPick[pickCount];
                    for (int i = 0; i < pickCount; i++)
                    {
                        readPicks[i].GroupId = r.ReadString();
                        readPicks[i].Key = r.ReadString();
                        if (readPicks[i].GroupId.Length > MaxCommandTokenLength) return false;
                        if (readPicks[i].Key.Length > MaxCommandTokenLength) return false;
                    }

                    int reserveCount = r.ReadInt32();
                    if (reserveCount < 0 || reserveCount > MaxRearmMagazines) return false;
                    var readReserves = new WireRearmReserve[reserveCount];
                    for (int i = 0; i < reserveCount; i++)
                    {
                        readReserves[i].Key = r.ReadString();
                        readReserves[i].Count = r.ReadInt64();
                        if (readReserves[i].Key.Length > MaxCommandTokenLength) return false;
                    }

                    terminalEntityId = readTerminal;
                    gridEntityId = readGrid;
                    maxCredits = readMax;
                    picks = readPicks;
                    reserves = readReserves;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteRearmResult(bool ok, string message)
        {
            return WriteRearmResult(ok, message, 0, null);
        }

        /// <summary>With a receipt, appended after the message for the reason
        /// <see cref="WriteDockRefuelResult(WireDockGas,bool,bool,long,long,string,long,WireReceiptLine[])"/>
        /// gives.</summary>
        public static byte[] WriteRearmResult(bool ok, string message, long balanceAfter, WireReceiptLine[] receipt)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.RearmResult);
                w.Write(ok);
                w.Write(message ?? "");
                WriteReceiptTail(w, balanceAfter, receipt);
                return ms.ToArray();
            }
        }

        public static bool TryReadRearmResult(byte[] data, out bool ok, out string message)
        {
            long balanceAfter;
            WireReceiptLine[] receipt;
            return TryReadRearmResult(data, out ok, out message, out balanceAfter, out receipt);
        }

        public static bool TryReadRearmResult(byte[] data, out bool ok, out string message,
            out long balanceAfter, out WireReceiptLine[] receipt)
        {
            balanceAfter = 0;
            receipt = new WireReceiptLine[0];
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.RearmResult) return false;
                    bool readOk = r.ReadBoolean();
                    string readMessage = r.ReadString();
                    long readBalance;
                    WireReceiptLine[] readReceipt;
                    if (!TryReadReceiptTail(ms, r, out readBalance, out readReceipt)) return false;
                    ok = readOk;
                    message = readMessage;
                    balanceAfter = readBalance;
                    receipt = readReceipt;
                    return true;
                }
            }
            catch { return false; }
        }

        private static void WriteReceiptTail(BinaryWriter w, long balanceAfter, WireReceiptLine[] receipt)
        {
            w.Write(balanceAfter);
            int count = receipt == null ? 0 : Math.Min(receipt.Length, MaxReceiptLines);
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                w.Write(receipt[i].Key ?? "");
                w.Write(receipt[i].Name ?? "");
                w.Write(receipt[i].Loaded);
                w.Write(receipt[i].Cargo);
                w.Write(receipt[i].UnitPrice);
                w.Write(receipt[i].Credits);
            }
        }

        /// <summary>Reads the receipt tail when the payload carries one; a payload that ends after the
        /// message is a result without a receipt, not a malformed one.</summary>
        private static bool TryReadReceiptTail(MemoryStream ms, BinaryReader r, out long balanceAfter,
            out WireReceiptLine[] receipt)
        {
            balanceAfter = 0;
            receipt = new WireReceiptLine[0];
            if (ms.Position >= ms.Length) return true;
            balanceAfter = r.ReadInt64();
            int count = r.ReadInt32();
            if (count < 0 || count > MaxReceiptLines) return false;
            var lines = new WireReceiptLine[count];
            for (int i = 0; i < count; i++)
            {
                lines[i].Key = r.ReadString();
                lines[i].Name = r.ReadString();
                lines[i].Loaded = r.ReadInt64();
                lines[i].Cargo = r.ReadInt64();
                lines[i].UnitPrice = r.ReadInt64();
                lines[i].Credits = r.ReadInt64();
            }
            receipt = lines;
            return true;
        }

        // ---- Station refit --------------------------------------------------------------------

        /// <summary>Ask what one founded node could be rebuilt as. The node id is a claim to VERIFY
        /// rather than a fact: the server re-resolves the caller's faction and the node's ownership
        /// before it answers, exactly as the node messages do.</summary>
        public static byte[] WriteStationRefitRequest(string nodeId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StationRefitRequest);
                w.Write(nodeId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadStationRefitRequest(byte[] data, out string nodeId)
        {
            nodeId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StationRefitRequest) return false;
                    string readNode = r.ReadString();
                    if (readNode.Length > MaxCommandTokenLength) return false;
                    nodeId = readNode;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteStationRefitSnapshot(WireStationRefit refit)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StationRefitSnapshot);
                w.Write(refit.NodeId ?? "");
                w.Write(refit.MayRefit);
                w.Write(refit.Message ?? "");
                w.Write(refit.CurrentPrefabId ?? "");
                w.Write(refit.PendingPrefabId ?? "");
                w.Write(refit.Site ?? "");
                w.Write(refit.SiegeBlocked);
                w.Write(refit.Observed);
                w.Write(refit.CollectingPrefabId ?? "");
                w.Write(refit.CollectedUnits);
                w.Write(refit.CollectingBomUnits);

                int count = refit.Candidates == null ? 0 : refit.Candidates.Length;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WireStationRefitCandidate c = refit.Candidates[i];
                    w.Write(c.Id ?? "");
                    w.Write(c.Kind ?? "");
                    w.Write(c.Site ?? "");
                    w.Write(c.BlockCount);
                    w.Write(c.Dps);
                    w.Write(c.Cost);
                    w.Write(c.NetBomTotal);
                    w.Write(c.Current);

                    int bomCount = c.NetBom == null ? 0 : c.NetBom.Length;
                    w.Write(bomCount);
                    for (int b = 0; b < bomCount; b++)
                    {
                        w.Write(c.NetBom[b].Key ?? "");
                        w.Write(c.NetBom[b].Need);
                        w.Write(c.NetBom[b].Have);
                        w.Write(c.NetBom[b].AtNode);
                        w.Write(c.NetBom[b].Elsewhere);
                        w.Write(c.NetBom[b].EnRoute);
                        w.Write(c.NetBom[b].Unserved);
                    }
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadStationRefitSnapshot(byte[] data, out WireStationRefit refit)
        {
            refit = new WireStationRefit();
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StationRefitSnapshot) return false;

                    var read = new WireStationRefit();
                    read.NodeId = r.ReadString();
                    read.MayRefit = r.ReadBoolean();
                    read.Message = r.ReadString();
                    read.CurrentPrefabId = r.ReadString();
                    read.PendingPrefabId = r.ReadString();
                    read.Site = r.ReadString();
                    read.SiegeBlocked = r.ReadBoolean();
                    read.Observed = r.ReadBoolean();
                    read.CollectingPrefabId = r.ReadString();
                    read.CollectedUnits = r.ReadInt64();
                    read.CollectingBomUnits = r.ReadInt64();

                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxStationRefitCandidates) return false;
                    var candidates = new WireStationRefitCandidate[count];
                    for (int i = 0; i < count; i++)
                    {
                        var c = new WireStationRefitCandidate();
                        c.Id = r.ReadString();
                        c.Kind = r.ReadString();
                        c.Site = r.ReadString();
                        c.BlockCount = r.ReadInt32();
                        c.Dps = r.ReadSingle();
                        c.Cost = r.ReadInt64();
                        c.NetBomTotal = r.ReadInt64();
                        c.Current = r.ReadBoolean();

                        int bomCount = r.ReadInt32();
                        if (bomCount < 0 || bomCount > MaxBomRowsPerBuild) return false;
                        var bom = new WireBuildComponent[bomCount];
                        for (int b = 0; b < bomCount; b++)
                        {
                            bom[b] = new WireBuildComponent
                            {
                                Key = r.ReadString(),
                                Need = r.ReadInt64(),
                                Have = r.ReadInt64(),
                                AtNode = r.ReadInt64(),
                                Elsewhere = r.ReadInt64(),
                                EnRoute = r.ReadInt64(),
                                Unserved = r.ReadBoolean(),
                            };
                        }
                        c.NetBom = bom;
                        candidates[i] = c;
                    }
                    read.Candidates = candidates;
                    refit = read;
                    return true;
                }
            }
            catch { refit = new WireStationRefit(); return false; }
        }

        /// <summary>One refit action. <paramref name="prefabId"/> names the design for
        /// <see cref="WireStationRefitCommand.Queue"/> and <see cref="WireStationRefitCommand.Force"/>
        /// and is empty for the other two; <paramref name="terminalEntityId"/>,
        /// <paramref name="scope"/> and <paramref name="name"/> are read only by
        /// <see cref="WireStationRefitCommand.Scan"/>, which names the Services Terminal the caller
        /// is standing at instead of a node.
        ///
        /// <para>One request for all four rather than a second pair for the scan: the scan produces
        /// a design the same panel then lists, and the answer is the same prose either way.</para></summary>
        public static byte[] WriteStationRefitCommandRequest(
            string nodeId, WireStationRefitCommand command, string prefabId,
            long terminalEntityId, string scope, string name)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StationRefitCommandRequest);
                w.Write(nodeId ?? "");
                w.Write((byte)command);
                w.Write(prefabId ?? "");
                w.Write(terminalEntityId);
                w.Write(scope ?? "");
                w.Write(name ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadStationRefitCommandRequest(
            byte[] data, out string nodeId, out WireStationRefitCommand command, out string prefabId,
            out long terminalEntityId, out string scope, out string name)
        {
            nodeId = null;
            command = WireStationRefitCommand.Queue;
            prefabId = null;
            terminalEntityId = 0;
            scope = null;
            name = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StationRefitCommandRequest)
                        return false;
                    string readNode = r.ReadString();
                    byte readCommand = r.ReadByte();
                    string readPrefab = r.ReadString();
                    long readTerminal = r.ReadInt64();
                    string readScope = r.ReadString();
                    string readName = r.ReadString();
                    if (readNode.Length > MaxCommandTokenLength) return false;
                    if (readPrefab.Length > MaxCommandTokenLength) return false;
                    if (readScope.Length > MaxCommandTokenLength) return false;
                    if (readName.Length > MaxCommandTokenLength) return false;
                    nodeId = readNode;
                    command = (WireStationRefitCommand)readCommand;
                    prefabId = readPrefab;
                    terminalEntityId = readTerminal;
                    scope = readScope;
                    name = readName;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteStationRefitCommandResult(bool ok, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StationRefitCommandResult);
                w.Write(ok);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadStationRefitCommandResult(byte[] data, out bool ok, out string message)
        {
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StationRefitCommandResult)
                        return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Build button ---------------------------------------------------------------------

        public static byte[] WriteBuildOptionsRequest(string nodeId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BuildOptionsRequest);
                w.Write(nodeId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadBuildOptionsRequest(byte[] data, out string nodeId)
        {
            nodeId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BuildOptionsRequest) return false;
                    nodeId = r.ReadString();
                    return true;
                }
            }
            catch { nodeId = null; return false; }
        }

        /// <summary>The node id is echoed so a reply in flight when the player changed station is
        /// dropped rather than shown under the new one, exactly as the refit snapshot does.</summary>
        public static byte[] WriteBuildOptions(string nodeId, IList<string> roles, IList<WireBuildPrefab> prefabs)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BuildOptions);
                w.Write(nodeId ?? "");

                int roleCount = roles == null ? 0 : roles.Count;
                if (roleCount > MaxBuildRolesPerMessage) roleCount = MaxBuildRolesPerMessage;
                w.Write(roleCount);
                for (int i = 0; i < roleCount; i++) w.Write(roles[i] ?? "");

                WriteBuildPrefabList(w, prefabs);
                return ms.ToArray();
            }
        }

        public static bool TryReadBuildOptions(byte[] data, out string nodeId,
                                               out List<string> roles, out List<WireBuildPrefab> prefabs)
        {
            nodeId = null;
            roles = null;
            prefabs = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BuildOptions) return false;

                    string readNodeId = r.ReadString();

                    int roleCount = r.ReadInt32();
                    if (roleCount < 0 || roleCount > MaxBuildRolesPerMessage) return false;
                    var readRoles = new List<string>(roleCount);
                    for (int i = 0; i < roleCount; i++) readRoles.Add(r.ReadString());

                    List<WireBuildPrefab> readPrefabs;
                    if (!TryReadBuildPrefabList(r, out readPrefabs)) return false;

                    nodeId = readNodeId;
                    roles = readRoles;
                    prefabs = readPrefabs;
                    return true;
                }
            }
            catch { nodeId = null; roles = null; prefabs = null; return false; }
        }

        /// <summary>The hull pick-list block, shared verbatim by <see cref="WriteBuildOptions"/> and
        /// <see cref="WriteFleetDoctrineOptions"/>. One writer and one reader rather than two: the row
        /// is a variable-length block (the per-hull role list) inside a repeated one, which is exactly
        /// the shape where two copies drift into reading one hull's id out of the previous hull's
        /// roles.</summary>
        private static void WriteBuildPrefabList(BinaryWriter w, IList<WireBuildPrefab> prefabs)
        {
            int count = prefabs == null ? 0 : prefabs.Count;
            if (count > MaxBuildPrefabsPerMessage) count = MaxBuildPrefabsPerMessage;
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                WireBuildPrefab p = prefabs[i];
                w.Write(p.PrefabId ?? "");
                w.Write(p.Label ?? "");
                string[] prefabRoles = p.Roles ?? new string[0];
                int prefabRoleCount = prefabRoles.Length;
                if (prefabRoleCount > MaxRolesPerBuildPrefab) prefabRoleCount = MaxRolesPerBuildPrefab;
                w.Write(prefabRoleCount);
                for (int j = 0; j < prefabRoleCount; j++) w.Write(prefabRoles[j] ?? "");
                w.Write(p.CargoLiters);
                w.Write(p.Dps);
                w.Write(p.GridSize ?? "");
                w.Write(p.Propulsion ?? "");
                w.Write(p.DrillYieldScore);
                w.Write(p.BomUnits);
                w.Write(p.CanFlyFromHere);
            }
        }

        private static bool TryReadBuildPrefabList(BinaryReader r, out List<WireBuildPrefab> prefabs)
        {
            prefabs = null;
            int count = r.ReadInt32();
            if (count < 0 || count > MaxBuildPrefabsPerMessage) return false;
            var read = new List<WireBuildPrefab>(count);
            for (int i = 0; i < count; i++)
            {
                var p = new WireBuildPrefab();
                p.PrefabId = r.ReadString();
                p.Label = r.ReadString();
                int prefabRoleCount = r.ReadInt32();
                if (prefabRoleCount < 0 || prefabRoleCount > MaxRolesPerBuildPrefab) return false;
                var prefabRoles = new string[prefabRoleCount];
                for (int j = 0; j < prefabRoleCount; j++) prefabRoles[j] = r.ReadString();
                p.Roles = prefabRoles;
                p.CargoLiters = r.ReadInt64();
                p.Dps = r.ReadDouble();
                p.GridSize = r.ReadString();
                p.Propulsion = r.ReadString();
                p.DrillYieldScore = r.ReadDouble();
                p.BomUnits = r.ReadInt64();
                p.CanFlyFromHere = r.ReadBoolean();
                read.Add(p);
            }
            prefabs = read;
            return true;
        }

        /// <summary>Queues one hull at a yard. It names the hull and no role: what the hull answers is
        /// read off the hull server-side.</summary>
        public static byte[] WriteBuildRequest(string nodeId, string prefabId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BuildRequest);
                w.Write(nodeId ?? "");
                w.Write(prefabId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadBuildRequest(byte[] data, out string nodeId, out string prefabId)
        {
            nodeId = null;
            prefabId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BuildRequest) return false;
                    nodeId = r.ReadString();
                    prefabId = r.ReadString();
                    return true;
                }
            }
            catch { nodeId = null; prefabId = null; return false; }
        }

        public static byte[] WriteBuildResult(bool ok, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BuildResult);
                w.Write(ok);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadBuildResult(byte[] data, out bool ok, out string message)
        {
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BuildResult) return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { ok = false; message = null; return false; }
        }

        // ---- Fleet doctrine -------------------------------------------------------------------

        public static byte[] WriteFleetDoctrineRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FleetDoctrineRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadFleetDoctrineRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FleetDoctrineRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The whole Fleet window in one message: the doctrine in evaluation order, the
        /// work waiting on hulls no line covers, and what the faction actually owns. Three lists
        /// rather than three messages because they are read together and are each bounded — a
        /// window showing a line's shortfall beside a stale census would be worse than one that
        /// waits for all three.</summary>
        public static byte[] WriteFleetDoctrineSnapshot(string factionTag, int cap, int revision,
            IList<WireDoctrineLine> lines, IList<WireDoctrinePressureRow> unlined,
            IList<WireFleetCensusRow> census, IList<WireHullRefitRow> refits = null)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FleetDoctrineSnapshot);
                w.Write(factionTag ?? "");
                w.Write(cap);
                w.Write(revision);

                int lineCount = lines == null ? 0 : lines.Count;
                if (lineCount > MaxDoctrineLinesPerMessage) lineCount = MaxDoctrineLinesPerMessage;
                w.Write(lineCount);
                for (int i = 0; i < lineCount; i++)
                {
                    WireDoctrineLine l = lines[i];
                    w.Write(l.Id ?? "");
                    w.Write(l.Priority);
                    w.Write(l.Paused);
                    w.Write(l.Fits ?? "");
                    w.Write(l.PrefabId ?? "");
                    w.Write(l.TargetCount);
                    w.Write(l.MaxConcurrent);
                    w.Write(l.ScopeNodeId ?? "");
                    w.Write(l.ScopeNodeName ?? "");
                    w.Write(l.Have);
                    w.Write(l.Flying);
                    w.Write(l.Building);
                    w.Write(l.Pressure);
                    w.Write(l.RouteBlocked);
                    w.Write(l.OverCeiling);
                    w.Write(l.Banned);
                    w.Write(l.RepairWaiting);
                    w.Write(l.Repairing);
                }

                int unlinedCount = unlined == null ? 0 : unlined.Count;
                if (unlinedCount > MaxDoctrinePressureRowsPerMessage) unlinedCount = MaxDoctrinePressureRowsPerMessage;
                w.Write(unlinedCount);
                for (int i = 0; i < unlinedCount; i++)
                {
                    WireDoctrinePressureRow p = unlined[i];
                    w.Write(p.Role ?? "");
                    w.Write(p.NodeId ?? "");
                    w.Write(p.NodeName ?? "");
                    w.Write(p.Demands);
                }

                int censusCount = census == null ? 0 : census.Count;
                if (censusCount > MaxFleetCensusRowsPerMessage) censusCount = MaxFleetCensusRowsPerMessage;
                w.Write(censusCount);
                for (int i = 0; i < censusCount; i++)
                {
                    WireFleetCensusRow c = census[i];
                    w.Write(c.PrefabId ?? "");
                    w.Write(c.Parked);
                    w.Write(c.Flying);
                    w.Write(c.RepairWaiting);
                    w.Write(c.Repairing);
                }

                // Appended last: a reader that predates it stops before it, and a reader that
                // knows it finds nothing here from a server that predates it.
                int refitCount = refits == null ? 0 : refits.Count;
                if (refitCount > MaxFleetCensusRowsPerMessage) refitCount = MaxFleetCensusRowsPerMessage;
                w.Write(refitCount);
                for (int i = 0; i < refitCount; i++)
                {
                    WireHullRefitRow f = refits[i];
                    w.Write(f.Family ?? "");
                    w.Write(f.Latest ?? "");
                    w.Write(f.Cap);
                    w.Write(f.InRefit);
                    w.Write(f.OutdatedParked);
                    w.Write(f.OutdatedAtYards);
                    w.Write(f.Blocked ?? "");
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadFleetDoctrineSnapshot(byte[] data, out string factionTag, out int cap,
            out int revision, out List<WireDoctrineLine> lines, out List<WireDoctrinePressureRow> unlined,
            out List<WireFleetCensusRow> census)
        {
            List<WireHullRefitRow> refits;
            return TryReadFleetDoctrineSnapshot(data, out factionTag, out cap, out revision, out lines,
                out unlined, out census, out refits);
        }

        /// <summary>The same, with the Refits list, which is empty from a server that predates it.</summary>
        public static bool TryReadFleetDoctrineSnapshot(byte[] data, out string factionTag, out int cap,
            out int revision, out List<WireDoctrineLine> lines, out List<WireDoctrinePressureRow> unlined,
            out List<WireFleetCensusRow> census, out List<WireHullRefitRow> refits)
        {
            refits = null;
            factionTag = null;
            cap = 0;
            revision = 0;
            lines = null;
            unlined = null;
            census = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FleetDoctrineSnapshot) return false;

                    string readTag = r.ReadString();
                    int readCap = r.ReadInt32();
                    int readRevision = r.ReadInt32();

                    int lineCount = r.ReadInt32();
                    if (lineCount < 0 || lineCount > MaxDoctrineLinesPerMessage) return false;
                    var readLines = new List<WireDoctrineLine>(lineCount);
                    for (int i = 0; i < lineCount; i++)
                    {
                        var l = new WireDoctrineLine();
                        l.Id = r.ReadString();
                        l.Priority = r.ReadInt32();
                        l.Paused = r.ReadBoolean();
                        l.Fits = r.ReadString();
                        l.PrefabId = r.ReadString();
                        l.TargetCount = r.ReadInt32();
                        l.MaxConcurrent = r.ReadInt32();
                        l.ScopeNodeId = r.ReadString();
                        l.ScopeNodeName = r.ReadString();
                        l.Have = r.ReadInt64();
                        l.Flying = r.ReadInt64();
                        l.Building = r.ReadInt32();
                        l.Pressure = r.ReadInt32();
                        l.RouteBlocked = r.ReadBoolean();
                        l.OverCeiling = r.ReadBoolean();
                        l.Banned = r.ReadBoolean();
                        l.RepairWaiting = r.ReadInt32();
                        l.Repairing = r.ReadInt32();
                        readLines.Add(l);
                    }

                    int unlinedCount = r.ReadInt32();
                    if (unlinedCount < 0 || unlinedCount > MaxDoctrinePressureRowsPerMessage) return false;
                    var readUnlined = new List<WireDoctrinePressureRow>(unlinedCount);
                    for (int i = 0; i < unlinedCount; i++)
                    {
                        var p = new WireDoctrinePressureRow();
                        p.Role = r.ReadString();
                        p.NodeId = r.ReadString();
                        p.NodeName = r.ReadString();
                        p.Demands = r.ReadInt32();
                        readUnlined.Add(p);
                    }

                    int censusCount = r.ReadInt32();
                    if (censusCount < 0 || censusCount > MaxFleetCensusRowsPerMessage) return false;
                    var readCensus = new List<WireFleetCensusRow>(censusCount);
                    for (int i = 0; i < censusCount; i++)
                    {
                        var c = new WireFleetCensusRow();
                        c.PrefabId = r.ReadString();
                        c.Parked = r.ReadInt64();
                        c.Flying = r.ReadInt64();
                        c.RepairWaiting = r.ReadInt32();
                        c.Repairing = r.ReadInt32();
                        readCensus.Add(c);
                    }

                    var readRefits = new List<WireHullRefitRow>();
                    if (ms.Position < ms.Length)
                    {
                        int refitCount = r.ReadInt32();
                        if (refitCount < 0 || refitCount > MaxFleetCensusRowsPerMessage) return false;
                        for (int i = 0; i < refitCount; i++)
                        {
                            var f = new WireHullRefitRow();
                            f.Family = r.ReadString();
                            f.Latest = r.ReadString();
                            f.Cap = r.ReadInt32();
                            f.InRefit = r.ReadInt32();
                            f.OutdatedParked = r.ReadInt32();
                            f.OutdatedAtYards = r.ReadInt32();
                            f.Blocked = r.ReadString();
                            readRefits.Add(f);
                        }
                    }

                    refits = readRefits;
                    factionTag = readTag;
                    cap = readCap;
                    revision = readRevision;
                    lines = readLines;
                    unlined = readUnlined;
                    census = readCensus;
                    return true;
                }
            }
            catch
            {
                factionTag = null; lines = null; unlined = null; census = null;
                return false;
            }
        }

        // ---- The Tactics window ----------------------------------------------------------------

        public static byte[] WriteTacticsRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.TacticsRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadTacticsRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.TacticsRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The whole Tactics window in one message: the header, every opponent with its
        /// context buckets, and the recent reviews. Each list is trimmed to its own bound.</summary>
        public static byte[] WriteTacticsSnapshot(WireTacticsHeader header, IList<WireTacticOpponent> opponents,
            IList<WireTacticReview> reviews)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.TacticsSnapshot);
                w.Write(header.FactionTag ?? "");
                w.Write(header.PriorArm);
                w.Write(header.PriorBreakOff);
                w.Write(header.Updates);
                w.Write(header.Learning);

                int oppCount = opponents == null ? 0 : Math.Min(opponents.Count, MaxTacticOpponentsPerMessage);
                w.Write(oppCount);
                for (int i = 0; i < oppCount; i++)
                {
                    WireTacticOpponent o = opponents[i];
                    w.Write(o.Tag ?? "");
                    w.Write(o.LastUtcTicks);
                    WriteTacticCell(w, o.Preferred);
                    w.Write(o.Evidence);
                    int ctxCount = o.Contexts == null ? 0 : Math.Min(o.Contexts.Length, MaxTacticContextsPerOpponent);
                    w.Write(ctxCount);
                    for (int c = 0; c < ctxCount; c++)
                    {
                        WireTacticContext ctx = o.Contexts[c];
                        w.Write(ctx.Context ?? "");
                        w.Write(ctx.Evidence);
                        int cellCount = ctx.Cells == null ? 0 : Math.Min(ctx.Cells.Length, MaxTacticCellsPerContext);
                        w.Write(cellCount);
                        for (int k = 0; k < cellCount; k++) WriteTacticCell(w, ctx.Cells[k]);
                    }
                }

                int reviewCount = reviews == null ? 0 : Math.Min(reviews.Count, MaxTacticReviewsPerMessage);
                w.Write(reviewCount);
                for (int i = 0; i < reviewCount; i++)
                {
                    WireTacticReview rv = reviews[i];
                    w.Write(rv.UtcTicks);
                    w.Write(rv.Opponent ?? "");
                    w.Write(rv.Context ?? "");
                    w.Write(rv.Arm);
                    w.Write(rv.BreakOff);
                    w.Write(rv.Predicted);
                    w.Write(rv.Actual);
                    w.Write(rv.Reward);
                    w.Write(rv.Mistake);
                    w.Write(rv.HullsLost);
                    w.Write(rv.HullsBrought);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadTacticsSnapshot(byte[] data, out WireTacticsHeader header,
            out List<WireTacticOpponent> opponents, out List<WireTacticReview> reviews)
        {
            header = new WireTacticsHeader();
            opponents = null;
            reviews = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.TacticsSnapshot) return false;

                    var h = new WireTacticsHeader();
                    h.FactionTag = r.ReadString();
                    h.PriorArm = r.ReadByte();
                    h.PriorBreakOff = r.ReadByte();
                    h.Updates = r.ReadInt64();
                    h.Learning = r.ReadBoolean();

                    int oppCount = r.ReadInt32();
                    if (oppCount < 0 || oppCount > MaxTacticOpponentsPerMessage) return false;
                    var readOpponents = new List<WireTacticOpponent>(oppCount);
                    for (int i = 0; i < oppCount; i++)
                    {
                        var o = new WireTacticOpponent();
                        o.Tag = r.ReadString();
                        o.LastUtcTicks = r.ReadInt64();
                        o.Preferred = ReadTacticCell(r);
                        o.Evidence = r.ReadSingle();
                        int ctxCount = r.ReadInt32();
                        if (ctxCount < 0 || ctxCount > MaxTacticContextsPerOpponent) return false;
                        o.Contexts = new WireTacticContext[ctxCount];
                        for (int c = 0; c < ctxCount; c++)
                        {
                            var ctx = new WireTacticContext();
                            ctx.Context = r.ReadString();
                            ctx.Evidence = r.ReadSingle();
                            int cellCount = r.ReadInt32();
                            if (cellCount < 0 || cellCount > MaxTacticCellsPerContext) return false;
                            ctx.Cells = new WireTacticCell[cellCount];
                            for (int k = 0; k < cellCount; k++) ctx.Cells[k] = ReadTacticCell(r);
                            o.Contexts[c] = ctx;
                        }
                        readOpponents.Add(o);
                    }

                    int reviewCount = r.ReadInt32();
                    if (reviewCount < 0 || reviewCount > MaxTacticReviewsPerMessage) return false;
                    var readReviews = new List<WireTacticReview>(reviewCount);
                    for (int i = 0; i < reviewCount; i++)
                    {
                        var rv = new WireTacticReview();
                        rv.UtcTicks = r.ReadInt64();
                        rv.Opponent = r.ReadString();
                        rv.Context = r.ReadString();
                        rv.Arm = r.ReadByte();
                        rv.BreakOff = r.ReadByte();
                        rv.Predicted = r.ReadSingle();
                        rv.Actual = r.ReadSingle();
                        rv.Reward = r.ReadSingle();
                        rv.Mistake = r.ReadBoolean();
                        rv.HullsLost = r.ReadByte();
                        rv.HullsBrought = r.ReadByte();
                        readReviews.Add(rv);
                    }

                    header = h;
                    opponents = readOpponents;
                    reviews = readReviews;
                    return true;
                }
            }
            catch
            {
                header = new WireTacticsHeader();
                opponents = null;
                reviews = null;
                return false;
            }
        }

        private static void WriteTacticCell(BinaryWriter w, WireTacticCell c)
        {
            w.Write(c.Arm);
            w.Write(c.BreakOff);
            w.Write(c.Mean);
            w.Write(c.Evidence);
        }

        private static WireTacticCell ReadTacticCell(BinaryReader r)
        {
            var c = new WireTacticCell();
            c.Arm = r.ReadByte();
            c.BreakOff = r.ReadByte();
            c.Mean = r.ReadSingle();
            c.Evidence = r.ReadSingle();
            return c;
        }

        // ---- Bank sales ---------------------------------------------------------------------------

        public static byte[] WriteBankSalesRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BankSalesRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadBankSalesRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BankSalesRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The whole list. <paramref name="factionTag"/> is empty when the viewer is in no
        /// registered player faction, which the window says rather than showing an empty table.</summary>
        public static byte[] WriteBankSalesSnapshot(string factionTag, IList<WireBankSalesRow> rows)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BankSalesSnapshot);
                w.Write(factionTag ?? "");
                int count = rows == null ? 0 : rows.Count;
                if (count > MaxBankSalesRows) count = MaxBankSalesRows;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WireBankSalesRow row = rows[i];
                    w.Write(row.Key ?? "");
                    w.Write(row.Name ?? "");
                    w.Write(row.Category);
                    w.Write(row.Held);
                    w.Write(row.UnitValue);
                    w.Write(row.Mode);
                    w.Write(row.Target);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadBankSalesSnapshot(byte[] data, out string factionTag, out List<WireBankSalesRow> rows)
        {
            factionTag = null;
            rows = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BankSalesSnapshot) return false;
                    string readTag = r.ReadString();
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxBankSalesRows) return false;
                    var readRows = new List<WireBankSalesRow>(count);
                    for (int i = 0; i < count; i++)
                        readRows.Add(new WireBankSalesRow
                        {
                            Key = r.ReadString(),
                            Name = r.ReadString(),
                            Category = r.ReadByte(),
                            Held = r.ReadInt64(),
                            UnitValue = r.ReadInt64(),
                            Mode = r.ReadByte(),
                            Target = r.ReadInt64(),
                        });
                    factionTag = readTag;
                    rows = readRows;
                    return true;
                }
            }
            catch { factionTag = null; rows = null; return false; }
        }

        /// <summary>Sets one rule -- <paramref name="mode"/> as in <see cref="WireBankSalesRow.Mode"/>,
        /// <paramref name="target"/> read only for mode 1 -- on every one of <paramref name="keys"/>.
        /// One message for a whole filtered list, so "every shown item" is one request rather than
        /// one per row.</summary>
        public static byte[] WriteBankSalesCommandRequest(byte mode, long target, IList<string> keys)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BankSalesCommandRequest);
                w.Write(mode);
                w.Write(target);
                int count = keys == null ? 0 : keys.Count;
                w.Write(count);
                for (int i = 0; i < count; i++) w.Write(keys[i] ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadBankSalesCommandRequest(byte[] data, out byte mode, out long target,
            out List<string> keys)
        {
            mode = 0;
            target = 0;
            keys = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BankSalesCommandRequest) return false;
                    byte readMode = r.ReadByte();
                    long readTarget = r.ReadInt64();
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxBankSalesRows) return false;
                    var readKeys = new List<string>(count);
                    for (int i = 0; i < count; i++)
                    {
                        string key = r.ReadString();
                        if (key.Length > MaxCommandTokenLength) return false;
                        readKeys.Add(key);
                    }
                    mode = readMode;
                    target = readTarget;
                    keys = readKeys;
                    return true;
                }
            }
            catch { keys = null; return false; }
        }

        public static byte[] WriteBankSalesCommandResult(bool ok, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BankSalesCommandResult);
                w.Write(ok);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadBankSalesCommandResult(byte[] data, out bool ok, out string message)
        {
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BankSalesCommandResult) return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { ok = false; message = null; return false; }
        }

        // ---- The store shelf ---------------------------------------------------------------------

        public static byte[] WritePlayerShelfRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.PlayerShelfRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadPlayerShelfRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.PlayerShelfRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The whole Store window in one message: the shelf in list order and every hull
        /// actually on sale. Two lists rather than two messages for the reason the doctrine snapshot
        /// carries three — they are read together, and a window showing a line's shortfall beside a
        /// stale census would be worse than one that showed neither.
        ///
        /// <para><paramref name="stockingOff"/> rides here rather than being inferred from an empty
        /// list: a shop that is switched off and a shop with nothing to do are the same picture, and
        /// the difference is the only thing a player can act on.</para></summary>
        public static byte[] WritePlayerShelfSnapshot(string factionTag, int cap, bool stockingOff,
            IList<WireShelfLine> lines, IList<WireShelfCensusRow> census)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.PlayerShelfSnapshot);
                w.Write(factionTag ?? "");
                w.Write(cap);
                w.Write(stockingOff);

                int lineCount = lines == null ? 0 : lines.Count;
                if (lineCount > MaxShelfLinesPerMessage) lineCount = MaxShelfLinesPerMessage;
                w.Write(lineCount);
                for (int i = 0; i < lineCount; i++)
                {
                    WireShelfLine l = lines[i];
                    w.Write(l.Id ?? "");
                    w.Write(l.Paused);
                    w.Write(l.PrefabId ?? "");
                    w.Write(l.TargetCount);
                    w.Write(l.OnSale);
                    w.Write(l.Building);
                    w.Write(l.Role ?? "");
                    w.Write(l.BlockedReason ?? "");
                }

                int censusCount = census == null ? 0 : census.Count;
                if (censusCount > MaxShelfCensusRowsPerMessage) censusCount = MaxShelfCensusRowsPerMessage;
                w.Write(censusCount);
                for (int i = 0; i < censusCount; i++)
                {
                    WireShelfCensusRow c = census[i];
                    w.Write(c.PrefabId ?? "");
                    w.Write(c.OnSale);
                    w.Write(c.Unlined);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadPlayerShelfSnapshot(byte[] data, out string factionTag, out int cap,
            out bool stockingOff, out List<WireShelfLine> lines, out List<WireShelfCensusRow> census)
        {
            factionTag = null;
            cap = 0;
            stockingOff = false;
            lines = null;
            census = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.PlayerShelfSnapshot) return false;

                    string readTag = r.ReadString();
                    int readCap = r.ReadInt32();
                    bool readOff = r.ReadBoolean();

                    int lineCount = r.ReadInt32();
                    if (lineCount < 0 || lineCount > MaxShelfLinesPerMessage) return false;
                    var readLines = new List<WireShelfLine>(lineCount);
                    for (int i = 0; i < lineCount; i++)
                        readLines.Add(new WireShelfLine
                        {
                            Id = r.ReadString(),
                            Paused = r.ReadBoolean(),
                            PrefabId = r.ReadString(),
                            TargetCount = r.ReadInt32(),
                            OnSale = r.ReadInt64(),
                            Building = r.ReadInt32(),
                            Role = r.ReadString(),
                            BlockedReason = r.ReadString(),
                        });

                    int censusCount = r.ReadInt32();
                    if (censusCount < 0 || censusCount > MaxShelfCensusRowsPerMessage) return false;
                    var readCensus = new List<WireShelfCensusRow>(censusCount);
                    for (int i = 0; i < censusCount; i++)
                        readCensus.Add(new WireShelfCensusRow
                        {
                            PrefabId = r.ReadString(),
                            OnSale = r.ReadInt64(),
                            Unlined = r.ReadBoolean(),
                        });

                    factionTag = readTag;
                    cap = readCap;
                    stockingOff = readOff;
                    lines = readLines;
                    census = readCensus;
                    return true;
                }
            }
            catch
            {
                factionTag = null;
                lines = null;
                census = null;
                return false;
            }
        }

        public static byte[] WritePlayerShelfCommandRequest(string lineId, WireShelfCommand command, int value)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.PlayerShelfCommandRequest);
                w.Write(lineId ?? "");
                w.Write((byte)command);
                w.Write(value);
                return ms.ToArray();
            }
        }

        public static bool TryReadPlayerShelfCommandRequest(byte[] data, out string lineId,
                                                            out WireShelfCommand command, out int value)
        {
            lineId = null;
            command = WireShelfCommand.Remove;
            value = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.PlayerShelfCommandRequest) return false;
                    lineId = r.ReadString();
                    command = (WireShelfCommand)r.ReadByte();
                    value = r.ReadInt32();
                    return true;
                }
            }
            catch { lineId = null; return false; }
        }

        public static byte[] WritePlayerShelfCommandResult(bool ok, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.PlayerShelfCommandResult);
                w.Write(ok);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadPlayerShelfCommandResult(byte[] data, out bool ok, out string message)
        {
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.PlayerShelfCommandResult) return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { ok = false; message = null; return false; }
        }

        public static byte[] WritePlayerShelfCreateRequest(string prefabId, int targetCount)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.PlayerShelfCreateRequest);
                w.Write(prefabId ?? "");
                w.Write(targetCount);
                return ms.ToArray();
            }
        }

        public static bool TryReadPlayerShelfCreateRequest(byte[] data, out string prefabId, out int targetCount)
        {
            prefabId = null;
            targetCount = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.PlayerShelfCreateRequest) return false;
                    prefabId = r.ReadString();
                    targetCount = r.ReadInt32();
                    return true;
                }
            }
            catch { prefabId = null; return false; }
        }

        public static byte[] WriteFleetDoctrineCommandRequest(string lineId, WireDoctrineCommand command, int value)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FleetDoctrineCommandRequest);
                w.Write(lineId ?? "");
                w.Write((byte)command);
                w.Write(value);
                return ms.ToArray();
            }
        }

        public static bool TryReadFleetDoctrineCommandRequest(byte[] data, out string lineId,
                                                              out WireDoctrineCommand command, out int value)
        {
            lineId = null;
            command = WireDoctrineCommand.Remove;
            value = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FleetDoctrineCommandRequest) return false;
                    lineId = r.ReadString();
                    command = (WireDoctrineCommand)r.ReadByte();
                    value = r.ReadInt32();
                    return true;
                }
            }
            catch { lineId = null; return false; }
        }

        /// <summary><paramref name="terminalEntityId"/> is the Services Terminal the dialog opened
        /// at, which every terminal request is authorised against; <paramref name="gridEntityId"/>
        /// is the selected hull, and 0 for a station scan, whose grid is the terminal's own.</summary>
        public static byte[] WriteDesignVersionQuery(long terminalEntityId, long gridEntityId, bool station)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.DesignVersionQuery);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                w.Write(station);
                return ms.ToArray();
            }
        }

        public static bool TryReadDesignVersionQuery(byte[] data, out long terminalEntityId, out long gridEntityId,
                                                     out bool station)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            station = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.DesignVersionQuery) return false;
                    terminalEntityId = r.ReadInt64();
                    gridEntityId = r.ReadInt64();
                    station = r.ReadBoolean();
                    return true;
                }
            }
            catch { terminalEntityId = 0; gridEntityId = 0; station = false; return false; }
        }

        public static byte[] WriteDesignVersionInfo(WireDesignVersionInfo info)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.DesignVersionInfo);
                w.Write(info.EntityId);
                w.Write(info.Station);
                w.Write(info.Offered);
                w.Write(info.Family ?? "");
                w.Write(info.CurrentId ?? "");
                w.Write(info.NextId ?? "");
                w.Write(info.ScopeAll);
                w.Write(info.ScopeLabel ?? "");
                w.Write(info.Reason ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadDesignVersionInfo(byte[] data, out WireDesignVersionInfo info)
        {
            info = new WireDesignVersionInfo();
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.DesignVersionInfo) return false;
                    info.EntityId = r.ReadInt64();
                    info.Station = r.ReadBoolean();
                    info.Offered = r.ReadBoolean();
                    info.Family = r.ReadString();
                    info.CurrentId = r.ReadString();
                    info.NextId = r.ReadString();
                    info.ScopeAll = r.ReadBoolean();
                    info.ScopeLabel = r.ReadString();
                    info.Reason = r.ReadString();
                    return true;
                }
            }
            catch { info = new WireDesignVersionInfo(); return false; }
        }

        public static byte[] WriteFleetDoctrineCommandResult(bool ok, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FleetDoctrineCommandResult);
                w.Write(ok);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadFleetDoctrineCommandResult(byte[] data, out bool ok, out string message)
        {
            ok = false;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FleetDoctrineCommandResult) return false;
                    ok = r.ReadBoolean();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { ok = false; message = null; return false; }
        }

        /// <summary>The new-line dialog's pick-lists. The hull rows are
        /// <see cref="WireBuildPrefab"/>, the same struct and the same writer the Build dialog uses
        /// — a doctrine line is chosen on the same numbers a one-shot build is, and a second row
        /// shape would be a second set of numbers to keep in step. <see cref="WireBuildPrefab.CanFlyFromHere"/>
        /// means "from at least one owned yard" here; see that field.</summary>
        public static byte[] WriteFleetDoctrineOptions(IList<string> roles, IList<WireOrderOption> scopeNodes,
            IList<WireBuildPrefab> prefabs)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FleetDoctrineOptions);
                int roleCount = roles == null ? 0 : roles.Count;
                if (roleCount > MaxBuildRolesPerMessage) roleCount = MaxBuildRolesPerMessage;
                w.Write(roleCount);
                for (int i = 0; i < roleCount; i++) w.Write(roles[i] ?? "");
                WriteOptionList(w, scopeNodes);
                WriteBuildPrefabList(w, prefabs);
                return ms.ToArray();
            }
        }

        public static bool TryReadFleetDoctrineOptions(byte[] data, out List<string> roles,
            out List<WireOrderOption> scopeNodes, out List<WireBuildPrefab> prefabs)
        {
            roles = null;
            scopeNodes = null;
            prefabs = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FleetDoctrineOptions) return false;

                    int roleCount = r.ReadInt32();
                    if (roleCount < 0 || roleCount > MaxBuildRolesPerMessage) return false;
                    var readRoles = new List<string>(roleCount);
                    for (int i = 0; i < roleCount; i++) readRoles.Add(r.ReadString());

                    List<WireOrderOption> readNodes;
                    if (!TryReadOptionList(r, out readNodes)) return false;
                    List<WireBuildPrefab> readPrefabs;
                    if (!TryReadBuildPrefabList(r, out readPrefabs)) return false;

                    roles = readRoles;
                    scopeNodes = readNodes;
                    prefabs = readPrefabs;
                    return true;
                }
            }
            catch { roles = null; scopeNodes = null; prefabs = null; return false; }
        }

        /// <summary>Creates one doctrine line for a hull. Every field is re-validated through the same
        /// <c>FactionRuntime</c> entry point the chat verb reaches; the pick-lists this was built
        /// from are a convenience, never an authorisation.</summary>
        public static byte[] WriteFleetDoctrineCreateRequest(string prefabId, int targetCount,
            int maxConcurrent, string scopeNodeId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FleetDoctrineCreateRequest);
                w.Write(prefabId ?? "");
                w.Write(targetCount);
                w.Write(maxConcurrent);
                w.Write(scopeNodeId ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadFleetDoctrineCreateRequest(byte[] data, out string prefabId,
            out int targetCount, out int maxConcurrent, out string scopeNodeId)
        {
            prefabId = null;
            targetCount = 0;
            maxConcurrent = 0;
            scopeNodeId = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FleetDoctrineCreateRequest) return false;
                    prefabId = r.ReadString();
                    targetCount = r.ReadInt32();
                    maxConcurrent = r.ReadInt32();
                    scopeNodeId = r.ReadString();
                    return true;
                }
            }
            catch { prefabId = null; scopeNodeId = null; return false; }
        }

        public static byte[] WriteFeatureRockObjects(
            IList<WireRockObject> present, IList<string> removed, bool reset, int generatorVersion)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FeatureRockObjects);
                w.Write(reset);
                w.Write(generatorVersion);
                int presentCount = present == null ? 0 : present.Count;
                w.Write(presentCount);
                for (int i = 0; i < presentCount; i++)
                {
                    WireRockObject o = present[i];
                    w.Write(o.StorageName ?? "");
                    w.Write(o.EntityId);
                    w.Write(o.XKm);
                    w.Write(o.YKm);
                    w.Write(o.ZKm);
                    w.Write(o.SizeM);
                    w.Write(o.MaterialSubTypeId ?? "");
                    w.Write(o.ShapeSeed);
                    w.Write(o.HasDepositCount);
                    w.Write(o.DepositCountCoefficient);
                    w.Write(o.HasDepositSize);
                    w.Write(o.DepositSizeDenominator);
                }
                int removedCount = removed == null ? 0 : removed.Count;
                w.Write(removedCount);
                for (int i = 0; i < removedCount; i++) w.Write(removed[i] ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadFeatureRockObjects(
            byte[] data, out List<WireRockObject> present, out List<string> removed, out bool reset,
            out int generatorVersion)
        {
            present = null;
            removed = null;
            reset = false;
            generatorVersion = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FeatureRockObjects) return false;
                    reset = r.ReadBoolean();
                    generatorVersion = r.ReadInt32();

                    int presentCount = r.ReadInt32();
                    if (presentCount < 0 || presentCount > MaxRockObjectsPerMessage) return false;
                    var readPresent = new List<WireRockObject>(presentCount);
                    for (int i = 0; i < presentCount; i++)
                    {
                        var o = new WireRockObject();
                        o.StorageName = r.ReadString();
                        o.EntityId = r.ReadInt64();
                        o.XKm = r.ReadDouble();
                        o.YKm = r.ReadDouble();
                        o.ZKm = r.ReadDouble();
                        o.SizeM = r.ReadDouble();
                        o.MaterialSubTypeId = r.ReadString();
                        o.ShapeSeed = r.ReadInt32();
                        o.HasDepositCount = r.ReadBoolean();
                        o.DepositCountCoefficient = r.ReadDouble();
                        o.HasDepositSize = r.ReadBoolean();
                        o.DepositSizeDenominator = r.ReadDouble();
                        readPresent.Add(o);
                    }

                    int removedCount = r.ReadInt32();
                    if (removedCount < 0 || removedCount > MaxRockObjectsPerMessage) return false;
                    var readRemoved = new List<string>(removedCount);
                    for (int i = 0; i < removedCount; i++) readRemoved.Add(r.ReadString());

                    present = readPresent;
                    removed = readRemoved;
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Client -> server. See <see cref="WireMessageKind.FeatureRockRequest"/>.
        /// <paramref name="canDerive"/> false withdraws a client that had been enrolled — the server
        /// resumes replicating rock to it — so this is also how a client backs out mid-session.</summary>
        public static byte[] WriteFeatureRockRequest(bool canDerive, bool materialControlActive)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FeatureRockRequest);
                w.Write(canDerive);
                w.Write(materialControlActive);
                return ms.ToArray();
            }
        }

        public static bool TryReadFeatureRockRequest(byte[] data, out bool canDerive, out bool materialControlActive)
        {
            canDerive = false;
            materialControlActive = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FeatureRockRequest) return false;
                    canDerive = r.ReadBoolean();
                    materialControlActive = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Paused blocks ----------------------------------------------------------------------

        public static byte[] WriteBlockPauseStatus(IList<WirePausedBlock> blocks)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.BlockPauseStatus);
                int n = blocks != null ? Math.Min(blocks.Count, MaxPausedBlocksPerMessage) : 0;
                w.Write(n);
                for (int i = 0; i < n; i++)
                {
                    w.Write(blocks[i].EntityId);
                    w.Write(blocks[i].Class);
                    w.Write(blocks[i].ByAdmin);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadBlockPauseStatus(byte[] data, out List<WirePausedBlock> blocks)
        {
            blocks = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.BlockPauseStatus) return false;
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxPausedBlocksPerMessage) return false;
                    var list = new List<WirePausedBlock>(n);
                    for (int i = 0; i < n; i++)
                        list.Add(new WirePausedBlock { EntityId = r.ReadInt64(), Class = r.ReadByte(), ByAdmin = r.ReadBoolean() });
                    blocks = list;
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Status panel -----------------------------------------------------------------------

        public const int MaxStatusPanelItems = 8;
        public const int MaxStatusPanelLines = 8;

        public static byte[] WriteStatusPanel(IList<WireStatusPanelItem> items)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.StatusPanel);
                int n = items != null ? Math.Min(items.Count, MaxStatusPanelItems) : 0;
                w.Write(n);
                for (int i = 0; i < n; i++)
                {
                    WireStatusPanelItem item = items[i];
                    w.Write(item.Key ?? "");
                    w.Write(item.Tone);
                    int lines = item.Lines != null ? Math.Min(item.Lines.Length, MaxStatusPanelLines) : 0;
                    w.Write(lines);
                    for (int j = 0; j < lines; j++)
                    {
                        w.Write(item.Lines[j].Text ?? "");
                        w.Write(item.Lines[j].CountdownSeconds);
                    }
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadStatusPanel(byte[] data, out List<WireStatusPanelItem> items)
        {
            items = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.StatusPanel) return false;
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxStatusPanelItems) return false;
                    var list = new List<WireStatusPanelItem>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var item = new WireStatusPanelItem { Key = r.ReadString(), Tone = r.ReadByte() };
                        int lines = r.ReadInt32();
                        if (lines < 0 || lines > MaxStatusPanelLines) return false;
                        item.Lines = new WireStatusPanelLine[lines];
                        for (int j = 0; j < lines; j++)
                            item.Lines[j] = new WireStatusPanelLine { Text = r.ReadString(), CountdownSeconds = r.ReadSingle() };
                        list.Add(item);
                    }
                    items = list;
                    return true;
                }
            }
            catch { return false; }
        }

        // ---- Player diplomacy -------------------------------------------------------------------

        public static byte[] WriteDiplomacyRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.DiplomacyRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadDiplomacyRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.DiplomacyRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>The viewer's faction's whole diplomacy readout. <paramref name="factionTag"/> is
        /// empty when the viewer is in no registered player faction, and the rows are then empty:
        /// that is an answer, not a missing one.
        ///
        /// <para><paramref name="isLeader"/> is the server's own leader check for this viewer, so
        /// the window disables what the server would refuse. It is a courtesy; the command is
        /// re-checked on arrival.</para></summary>
        public static byte[] WriteDiplomacySnapshot(string factionTag, bool isLeader, long giftCost, long treasury,
            long serverNowTicks, IList<WireDiplomacyRow> rows)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.DiplomacySnapshot);
                w.Write(factionTag ?? "");
                w.Write(isLeader);
                w.Write(giftCost);
                w.Write(treasury);
                w.Write(serverNowTicks);

                int count = rows == null ? 0 : rows.Count;
                if (count > MaxDiplomacyRowsPerMessage) count = MaxDiplomacyRowsPerMessage;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WireDiplomacyRow d = rows[i];
                    w.Write(d.NpcTag ?? "");
                    w.Write(d.TheirView);
                    w.Write((byte)d.TheirBand);
                    w.Write(d.YourStance);
                    w.Write((byte)d.YourBand);
                    w.Write(d.EmbargoByYou);
                    w.Write(d.EmbargoByThem);
                    w.Write(d.TradeBarred);
                    w.Write(d.Allied);
                    w.Write(d.Implacable);
                    w.Write(d.TruceUntilTicks);
                    w.Write(d.YouOffered ?? "");
                    w.Write(d.YouOfferedExpiresTicks);
                    w.Write(d.TheyOffered ?? "");
                    w.Write(d.TheyOfferedExpiresTicks);
                    w.Write(d.GiftReadyTicks);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadDiplomacySnapshot(byte[] data, out string factionTag, out bool isLeader,
            out long giftCost, out long treasury, out long serverNowTicks, out List<WireDiplomacyRow> rows)
        {
            factionTag = null;
            isLeader = false;
            giftCost = 0;
            treasury = 0;
            serverNowTicks = 0;
            rows = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.DiplomacySnapshot) return false;

                    string readTag = r.ReadString();
                    bool readLeader = r.ReadBoolean();
                    long readCost = r.ReadInt64();
                    long readTreasury = r.ReadInt64();
                    long readNow = r.ReadInt64();

                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxDiplomacyRowsPerMessage) return false;
                    var list = new List<WireDiplomacyRow>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var d = new WireDiplomacyRow();
                        d.NpcTag = r.ReadString();
                        d.TheirView = r.ReadDouble();
                        d.TheirBand = ReadBand(r);
                        d.YourStance = r.ReadDouble();
                        d.YourBand = ReadBand(r);
                        d.EmbargoByYou = r.ReadBoolean();
                        d.EmbargoByThem = r.ReadBoolean();
                        d.TradeBarred = r.ReadBoolean();
                        d.Allied = r.ReadBoolean();
                        d.Implacable = r.ReadBoolean();
                        d.TruceUntilTicks = r.ReadInt64();
                        d.YouOffered = r.ReadString();
                        d.YouOfferedExpiresTicks = r.ReadInt64();
                        d.TheyOffered = r.ReadString();
                        d.TheyOfferedExpiresTicks = r.ReadInt64();
                        d.GiftReadyTicks = r.ReadInt64();
                        list.Add(d);
                    }

                    factionTag = readTag;
                    isLeader = readLeader;
                    giftCost = readCost;
                    treasury = readTreasury;
                    serverNowTicks = readNow;
                    rows = list;
                    return true;
                }
            }
            catch
            {
                factionTag = null;
                rows = null;
                return false;
            }
        }

        /// <summary>A band byte this build does not know reads as neutral rather than failing the
        /// whole snapshot: a later server adding a band must not blank an older client's page.</summary>
        private static WireStanceBand ReadBand(BinaryReader r)
        {
            byte b = r.ReadByte();
            return b <= (byte)WireStanceBand.Friendly ? (WireStanceBand)b : WireStanceBand.Neutral;
        }

        public static byte[] WriteDiplomacyCommandRequest(WireDiplomacyAct act, string npcTag)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.DiplomacyCommandRequest);
                w.Write((byte)act);
                w.Write(npcTag ?? "");
                return ms.ToArray();
            }
        }

        /// <summary>An act byte this build does not know is refused here, not passed on as a number
        /// the host would have to guess at.</summary>
        public static bool TryReadDiplomacyCommandRequest(byte[] data, out WireDiplomacyAct act, out string npcTag)
        {
            act = WireDiplomacyAct.Status;
            npcTag = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.DiplomacyCommandRequest) return false;
                    byte a = r.ReadByte();
                    if (a > (byte)WireDiplomacyAct.Withdraw) return false;
                    string tag = r.ReadString();
                    act = (WireDiplomacyAct)a;
                    npcTag = tag;
                    return true;
                }
            }
            catch { npcTag = null; return false; }
        }

        public static byte[] WriteDiplomacyCommandResult(bool done, string npcTag, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.DiplomacyCommandResult);
                w.Write(done);
                w.Write(npcTag ?? "");
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadDiplomacyCommandResult(byte[] data, out bool done, out string npcTag, out string message)
        {
            done = false;
            npcTag = null;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.DiplomacyCommandResult) return false;
                    done = r.ReadBoolean();
                    npcTag = r.ReadString();
                    message = r.ReadString();
                    return true;
                }
            }
            catch { done = false; npcTag = null; message = null; return false; }
        }

        // ---- Header ---------------------------------------------------------------------------

        private static void WriteHeader(BinaryWriter w, WireMessageKind kind)
        {
            w.Write((ushort)kind);
            w.Write(ProtocolVersion);
        }

        /// <summary>Reads and version-checks the header. A mismatched version fails here, so no
        /// payload written by a different protocol is ever interpreted.</summary>
        private static bool ReadHeader(BinaryReader r, out WireMessageKind kind)
        {
            kind = (WireMessageKind)r.ReadUInt16();
            return r.ReadInt32() == ProtocolVersion;
        }
    }
}
