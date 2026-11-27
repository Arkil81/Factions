using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// One in-transit fleet as the map window needs it: richer than the scanner's
    /// <see cref="ScannerFleetCandidate"/>, which only carries what a HUD marker needs.
    ///
    /// A host type rather than a Core one — the map is a presentation feature, and Core has no
    /// business gaining a type per client window. Positions are km, matching Core throughout.
    /// </summary>
    internal struct MapFleetInfo
    {
        /// <summary>Same key the scanner uses, so the two features name the same fleet
        /// identically.</summary>
        public string Key;
        public ActionKind Kind;
        public string OwnerTag;
        /// <summary>Graph nodes this leg runs between. Either may be null — a computed navigation
        /// leg is a bare coordinate with no node behind it.</summary>
        public string OriginNodeId;
        public string TargetNodeId;
        public Vec3 PositionKm;
        /// <summary>Instantaneous velocity along the current leg, km per minute. Sampled from the
        /// live acceleration profile rather than derived from a duplicate of it, so the client can
        /// dead-reckon between server updates without knowing the easing curve.</summary>
        public Vec3 VelocityKmPerMin;
        public Vec3 TargetPosKm;
        public DateTime EstimatedArrivalUtc;
        public int ConvoySize;
        /// <summary>How loud this convoy is, from hull, armament and load
        /// (<see cref="FleetSensing.SignatureOf"/>). Computed once per fleet per tick because every
        /// subscriber tests the same fleet against their own sensors.</summary>
        public double Signature;
        /// <summary>Radius the materialised hull is announcing itself over, km; 0 for an abstract
        /// fleet, which has no grid for SE's own HUD to draw (FAC-140).</summary>
        public double BroadcastRadiusKm;
        /// <summary>Non-zero once a physical grid exists.</summary>
        public long MaterialisedEntityId;
        /// <summary>The node this fleet is fighting at, while it fights; null otherwise. Its owner
        /// identifies the attacker at any margin.</summary>
        public string EngagementTargetNodeId;
        /// <summary>The fleet's weapon DPS, for the viewer's fleet-sightings record. Server-side only.</summary>
        public double WeaponDps;
        /// <summary>The clearance polyline still ahead of the current destination, already resolved
        /// to world positions — <see cref="FleetItinerary.RoutedStopCount"/> of the fleet's remaining
        /// stops. Legs the resolver has not routed yet are left out: they are bare destinations with
        /// no waypoints, so a map that drew them would draw a straight line through any body between.
        /// Empty for a fleet flying a direct leg.</summary>
        public List<MapFleetStopInfo> RoutedStops;
    }

    internal struct MapFleetStopInfo
    {
        public Vec3 PositionKm;
        public FleetStopKind Kind;
    }
}
