using System;
using System.Collections.Generic;
using System.Diagnostics;    // Stopwatch -- frame probes
using System.Reflection;
using Factions.Wire;
using HarmonyLib;                       // AccessTools
using Sandbox.Engine.Voxels;            // MyStorageBase, MyOctreeStorage
using Sandbox.Game.Entities;            // MyEntities, MyVoxelBase
using Sandbox.Game.World;               // MyWorldGenerator, MySession
using Sandbox.ModAPI;                   // MyAPIGateway
using VRage.Game;                       // MyObjectBuilder_SessionSettings
using VRage.Game.Components;
using VRage.Game.Entity;                // MyEntity
using VRage.Voxels;                     // IMyStorageDataProvider
using VRageMath;

namespace FactionsUI.Rock;

/// <summary>
/// Builds the server's pristine feature rock locally instead of receiving it.
///
/// <para><b>The exchange.</b> The server names each rock — a storage name, a shape seed, a size, a
/// position and the entity id both ends must agree on — and this shapes it from SE's own asteroid
/// generator. What stops crossing the wire is each rock's serialized octree, which is the large
/// part; the entity, the octree, the physics body and the clipmap all still exist here exactly as
/// they do today, derived rather than delivered. That is the whole of the saving and it is worth
/// being honest about: this is a bandwidth feature, not a client-performance one.</para>
///
/// <para><b>This is vanilla's own model.</b> <c>MyProceduralAsteroidCellGenerator</c> ends its spawn
/// path with <c>if (!Sync.IsServer) break;</c> — it runs here too and regenerates the same rock from
/// the same seed, which is why vanilla asteroids can be <c>Save = false</c> and never replicate. The
/// only thing this adds is that the server, rather than a duplicated world recipe, decides what
/// exists.</para>
///
/// <para><b>Never build what the server has not named.</b> Every rock here comes from a
/// <see cref="WireMessageKind.FeatureRockObjects"/> message, and the server sends one only after the
/// rock actually exists server-side. Collision therefore always precedes geometry — a client can
/// never render rock it would fall through, which is the mirror of the failure the server half hit
/// (rock that collided and rendered for nobody).</para>
///
/// <para><b>Nothing edited is ever ours.</b> The first write to a rock's storage — a drill, a
/// bomb — hands it to SE: the server stops withholding it, replicates the edited storage, and
/// <c>MyVoxelReplicable.OnLoad</c> swaps that storage into this very entity rather than making a
/// second one. From that moment this component stops tracking it and must never close it.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class RockComponent : MySessionComponentBase
{
    /// <summary>Rocks built per frame. A jump into a dense belt can name a hundred and more at once,
    /// and <c>AddVoxelMap</c> is synchronous — an unbounded burst is a visible client stall, the
    /// same reason the server caps its own spawns per tick.</summary>
    private const int BuildsPerFrame = 4;

    /// <summary>Closes drained per frame, and the reason this exists is the reason
    /// <see cref="BuildsPerFrame"/> does. Closing a voxel map is the same class of work as building
    /// one — SE releases the physics body, the clipmap and the render mesh — and a server diff can
    /// name fifty at once when a player crosses the keep boundary at speed. Unbudgeted, that whole
    /// set lands in one frame; the handler returns in a fraction of a millisecond and SE pays for it
    /// afterwards, off this stack, which is why it reads as a stall nothing here appears to cause.</summary>
    private const int ClosesPerFrame = 4;

    /// <summary>Names waiting to be closed. The set is the authority, not the queue: a name the
    /// server re-states before its close is reached is dropped from the set and dequeues to a
    /// no-op, so a rock is never closed out from under a diff that just asked for it.</summary>
    private readonly Queue<string> _closing = new Queue<string>();
    private readonly HashSet<string> _closingSet = new HashSet<string>(StringComparer.Ordinal);

    internal static RockComponent Instance { get; private set; }

    private static readonly MethodInfo CreateAsteroidShapeM = ResolveAsteroidShapeFactory();

    /// <summary>Rock this component built and still owns, by storage name. Dropped the moment
    /// anything edits it, at which point the entity lives on and belongs to SE.</summary>
    private readonly Dictionary<string, long> _built = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>The entity ids in <see cref="_built"/>, for <see cref="OwnsPristine"/> on SE's physics path.</summary>
    private readonly HashSet<long> _ownedIds = new HashSet<long>();

    /// <summary>Rock this component built and still names: the only rock <see cref="RockContactPhysics"/>
    /// may hold back or release.
    ///
    /// <para>Not <c>ContentChanged</c>: when the server replicates a rock this client already built,
    /// <c>MyVoxelReplicable.OnLoad</c> assigns its storage into our entity, and <c>MyVoxelMap.Storage</c>'s setter
    /// notifies the whole range, which sets <c>ContentChanged</c> on rock nobody edited. An edit made after such
    /// a swap goes to the server's storage, which our edit hook is not on, so the rock stays here until the
    /// server's next diff drops it and <see cref="Close"/> vetoes; until then its grid may be released, and is
    /// rebuilt from its current storage on contact.</para></summary>
    internal bool OwnsPristine(MyVoxelBase voxel)
    {
        return voxel != null && _ownedIds.Contains(voxel.EntityId) && voxel.IsSeedOpen == true;
    }

    internal IEnumerable<long> OwnedIds => _ownedIds;

    /// <summary>Named by the server and not yet built. Drained a few per frame.</summary>
    private readonly Queue<WireRockObject> _pending = new Queue<WireRockObject>();

    /// <summary>Storage names in <see cref="_pending"/>, so a restatement of the whole set does not
    /// queue the same rock a second time.</summary>
    private readonly HashSet<string> _queued = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Total built and closed this session, for the log line on unload — the only
    /// after-the-fact evidence that the feature did anything.</summary>
    private int _totalBuilt;
    private int _totalClosed;

    /// <summary>Set once the server has been told what this client can do. Cleared whenever the link
    /// drops, so a re-established link re-announces rather than leaving the server holding a stale
    /// answer about a client it has stopped sending rock to.</summary>
    private bool _announced;

    /// <summary>Which of SE's asteroid generators the server builds rock with. Sentinel until the
    /// first message says: a client that guessed would build the same seed into a different rock.</summary>
    private const int UnknownGeneratorVersion = -1;
    private int _generatorVersion = UnknownGeneratorVersion;

    /// <summary>
    /// One first-edited rock, watched until we can say what SE did with it.
    ///
    /// <para>This is the check the whole design rests on. A derived rock and a replicated one have
    /// to be the SAME entity: when a player drills, the server stops withholding and replicates the
    /// edited storage, and <c>MyVoxelReplicable.OnLoad</c> is supposed to find our entity by id and
    /// assign into its <c>Storage</c> property rather than construct a second voxel. If that
    /// handover does not happen, the player is standing next to two overlapping asteroids — and
    /// nothing else in this feature would say so.</para>
    ///
    /// <para>The storage REFERENCE is what makes the answer unambiguous. OnLoad's in-place path is
    /// literally <c>((MyVoxelMap)entity).Storage = storage;</c>, so a swap replaces the object we
    /// built with the server's. Comparing references distinguishes "handed over" from "the server
    /// has not sent yet", which a position or block count cannot.</para>
    /// </summary>
    private struct HandoverWatch
    {
        public string StorageName;
        public long EntityId;
        /// <summary>The storage this client built. Identity comparison only — never dereferenced.</summary>
        public object OurStorage;
        /// <summary>Consecutive polls in which our entity id resolved to nothing.</summary>
        public int MissedPolls;
    }

    /// <summary>How long the entity may be absent before that is a verdict rather than the blink of
    /// a rebuild. Polls run every frame, so this is a fraction of a second of tolerance.</summary>
    private const int MissesBeforeGone = 120;

    private readonly List<HandoverWatch> _handovers = new List<HandoverWatch>();

    /// <summary>Verdicts reported per session. This is diagnostic evidence for one specific
    /// question, not telemetry: the first few answer it, and a belt full of drilling players must
    /// not turn the client log into a firehose.</summary>
    private const int MaxHandoverReports = 5;
    private int _handoverReports;

    /// <summary>Watches held at once. The set is a sample, not a census: <see cref="PollHandovers"/>
    /// walks every entry once per frame and <see cref="DropHandoverWatch"/> scans it on every close,
    /// so its length is a per-frame cost paid by a client that is doing nothing wrong. A rock nobody
    /// touches never reaches a verdict and would otherwise sit in here for the whole session, one
    /// entry per rock ever built -- thousands, in a belt.</summary>
    private const int MaxHandoverWatches = 16;
    private int _frame;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.FeatureRockObjects, OnRockObjects);
        if (CreateAsteroidShapeM == null)
            Plugin.Log("ROCK: MyCompositeShapeProvider.CreateAsteroidShape not found -- no rock will "
                       + "be derived locally. The server keeps replicating it, so this degrades to "
                       + "today's behaviour rather than to empty space.");
    }

    protected override void UnloadData()
    {
        // Deliberately does NOT close what it built. Session teardown closes every entity anyway,
        // and reaching into the entity list while SE is doing that is how a shutdown turns into a
        // crash report.
        Plugin.Log("ROCK: session end -- derived " + _totalBuilt + " rock, closed " + _totalClosed + ".");
        _built.Clear();
        _ownedIds.Clear();
        RockContactPhysics.Reset();
        _pending.Clear();
        _queued.Clear();
        _closing.Clear();
        _closingSet.Clear();
        _handovers.Clear();
        _announced = false;
        Instance = null;
    }

    /// <summary>True when the server has granted this client the right to derive its own rock. While
    /// false, the server is replicating rock normally and this component must build nothing.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.FeatureRock);
        }
    }

    /// <summary>Live counts, for the settings screen and for asking "is this actually on".</summary>
    internal int BuiltCount { get { return _built.Count; } }
    internal int PendingCount { get { return _pending.Count; } }

    /// <summary>Times one server message. A full restatement arrives here as a single frame's work —
    /// decode, the stale sweep, and the re-queue — and then pays again over the following frames as
    /// whatever it queued is built, which is what <c>pending</c> in the other probes tracks.</summary>
    private void OnRockObjects(byte[] data)
    {
        long ingestStart = Stopwatch.GetTimestamp();
        int pendingBefore = _pending.Count;
        int heldBefore = _built.Count;
        OnRockObjectsCore(data);

        double ingestMs = Ms(Stopwatch.GetTimestamp() - ingestStart);
        _windowIngests++;
        _windowIngestMs += ingestMs;
        if (ingestMs > _worstIngestMs) _worstIngestMs = ingestMs;
        if (Config.Current.DebugLogging)
            Plugin.Log("ROCK_INGEST " + ingestMs.ToString("F1") + "ms"
                       + " bytes=" + (data == null ? 0 : data.Length)
                       + " queued=" + pendingBefore + "->" + _pending.Count
                       + " closing=" + _closing.Count
                       + " held=" + heldBefore + "->" + _built.Count);
    }

    private void OnRockObjectsCore(byte[] data)
    {
        List<WireRockObject> present;
        List<string> removed;
        bool reset;
        int generatorVersion;
        if (!FactionsWire.TryReadFeatureRockObjects(data, out present, out removed, out reset, out generatorVersion))
        {
            Plugin.Log("ROCK: unreadable FeatureRockObjects payload.");
            return;
        }

        // Taken from the server on every message, never defaulted here. It selects WHICH of SE's
        // asteroid generators builds the shape, so a client carrying its own idea of it would not
        // produce slightly different rock -- it would produce entirely different rock from the same
        // seed. It is operator-tunable server-side, so there is no safe constant to hold.
        if (_generatorVersion != generatorVersion)
        {
            if (_generatorVersion != UnknownGeneratorVersion)
                Plugin.Log("ROCK: server generator version changed " + _generatorVersion + " -> "
                           + generatorVersion + "; everything built under the old one is wrong.");
            _generatorVersion = generatorVersion;
        }

        if (reset)
        {
            // The server is restating the whole set. Anything it no longer names is rock this client
            // should not be holding -- most often because the two drifted, which is exactly what a
            // reset exists to repair.
            var named = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < present.Count; i++) named.Add(present[i].StorageName ?? "");

            List<string> stale = null;
            foreach (string name in _built.Keys)
            {
                if (named.Contains(name)) continue;
                if (stale == null) stale = new List<string>();
                stale.Add(name);
            }
            if (stale != null) for (int i = 0; i < stale.Count; i++) EnqueueClose(stale[i]);

            _pending.Clear();
            _queued.Clear();
        }

        if (removed != null)
            for (int i = 0; i < removed.Count; i++) EnqueueClose(removed[i]);

        for (int i = 0; i < present.Count; i++)
        {
            WireRockObject o = present[i];
            string name = o.StorageName ?? "";
            if (name.Length == 0) continue;
            // Named again before its close came up: it stays.
            _closingSet.Remove(name);
            // Registered on arrival, not at build time, and never removed. The build scope below
            // covers this client's own generate call; the registration is what lets the ore be
            // re-imposed when SE regenerates the rock from the server's storage after the first
            // drill -- the path that has no scope of ours anywhere on the stack.
            Factions.FeatureRockMaterialControl.Register(o.ShapeSeed, o.MaterialSubTypeId);

            if (_built.ContainsKey(name) || !_queued.Add(name)) continue;
            _pending.Enqueue(o);
        }
    }

    public override void UpdateAfterSimulation()
    {
        _frame++;

        long frameStart = Stopwatch.GetTimestamp();
        ProbeFrameGap(frameStart);

        Announce();

        long pollTicks = 0;
        if (_handovers.Count > 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            PollHandovers();
            pollTicks = Stopwatch.GetTimestamp() - t0;
        }

        long closeTicks = 0;
        int closed = 0;
        if (_closing.Count > 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < ClosesPerFrame && _closing.Count > 0; i++)
            {
                string name = _closing.Dequeue();
                if (!_closingSet.Remove(name)) continue;   // re-stated since; leave it alone
                Close(name);
                closed++;
            }
            closeTicks = Stopwatch.GetTimestamp() - t0;
        }

        // Same three conditions as before, folded into one guard so the probe below still runs on a
        // frame that builds nothing -- a frame with no builds is evidence too.
        long buildTicks = 0;
        int built = 0;
        if (_pending.Count > 0 && CreateAsteroidShapeM != null
            && _generatorVersion != UnknownGeneratorVersion)   // no server answer yet; do not guess
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < BuildsPerFrame && _pending.Count > 0; i++)
            {
                WireRockObject o = _pending.Dequeue();
                _queued.Remove(o.StorageName ?? "");
                Build(o);
                built++;
            }
            buildTicks = Stopwatch.GetTimestamp() - t0;
        }

        ProbeWork(built, buildTicks, pollTicks, closed, closeTicks);

        RockContactPhysics.Tick(this);
    }

    // ---- Frame probes -----------------------------------------------------------------------
    //
    // Four readings, all gated on Config.Current.DebugLogging at the call site, all built to
    // separate one question from its neighbour: when the client hitches, was it this component's
    // work, or something outside it that this component merely happened to be running during.
    //
    // ROCK_STALL      the gap between two frames -- everything the game did, not only us.
    // ROCK_SLOW_FRAME our own cost in one frame, so it can be compared against that gap.
    // ROCK_INGEST     the cost of one server message, which is where a full restatement lands.
    // ROCK_WINDOW     a periodic total, because many cheap frames can cost more than one dear one
    //                 and no per-frame threshold would ever fire on them.

    /// <summary>A frame slower than this is a visible hitch rather than a busy frame.</summary>
    private const double FrameGapWarnMs = 50.0;

    /// <summary>Our own per-frame cost worth naming: a quarter of a 60 fps budget in one
    /// component.</summary>
    private const double WorkWarnMs = 4.0;

    private const double SummaryMs = 30000.0;

    private long _lastFrameStamp;
    private long _windowStart;
    private int _windowFrames, _windowBuilt, _windowStalls;
    private double _windowBuildMs, _windowPollMs, _windowIngestMs, _windowCloseMs;
    private int _windowClosed;
    private double _worstWorkMs, _worstGapMs, _worstIngestMs;
    private int _windowIngests;

    private static double Ms(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Time since the previous call. SE calls this once per simulation frame, so a gap far
    /// over the frame budget is a stall — and one measured here covers the whole frame, not just
    /// this component. Reported next to what we were holding at the time so a stall can be read
    /// against a build backlog rather than guessed at.</summary>
    private void ProbeFrameGap(long now)
    {
        if (_lastFrameStamp != 0)
        {
            double gapMs = Ms(now - _lastFrameStamp);
            if (gapMs > _worstGapMs) _worstGapMs = gapMs;
            if (gapMs >= FrameGapWarnMs)
            {
                _windowStalls++;
                if (Config.Current.DebugLogging)
                    Plugin.Log("ROCK_STALL gap=" + gapMs.ToString("F1") + "ms"
                               + " pending=" + _pending.Count + " held=" + _built.Count
                               + " watches=" + _handovers.Count + " built=" + _totalBuilt);
            }
        }
        _lastFrameStamp = now;
    }

    /// <summary>What this component actually spent. A ROCK_STALL with no ROCK_SLOW_FRAME beside it
    /// is a stall this component did not cause.</summary>
    private void ProbeWork(int built, long buildTicks, long pollTicks, int closed, long closeTicks)
    {
        double buildMs = Ms(buildTicks);
        double pollMs = Ms(pollTicks);
        double closeMs = Ms(closeTicks);
        double workMs = buildMs + pollMs + closeMs;

        _windowFrames++;
        _windowBuilt += built;
        _windowClosed += closed;
        _windowBuildMs += buildMs;
        _windowPollMs += pollMs;
        _windowCloseMs += closeMs;
        if (workMs > _worstWorkMs) _worstWorkMs = workMs;

        if (workMs >= WorkWarnMs && Config.Current.DebugLogging)
            Plugin.Log("ROCK_SLOW_FRAME work=" + workMs.ToString("F1") + "ms"
                       + " build=" + buildMs.ToString("F1") + "ms x" + built
                       + " close=" + closeMs.ToString("F1") + "ms x" + closed
                       + " poll=" + pollMs.ToString("F1") + "ms x" + _handovers.Count
                       + " pending=" + _pending.Count + " closing=" + _closing.Count);

        long now = Stopwatch.GetTimestamp();
        if (_windowStart == 0) { _windowStart = now; return; }
        if (Ms(now - _windowStart) < SummaryMs) return;

        if (Config.Current.DebugLogging)
            Plugin.Log("ROCK_WINDOW " + (Ms(now - _windowStart) / 1000.0).ToString("F0") + "s"
                       + " frames=" + _windowFrames
                       + " built=" + _windowBuilt
                       + " closed=" + _windowClosed
                       + " buildMs=" + _windowBuildMs.ToString("F0")
                       + " closeMs=" + _windowCloseMs.ToString("F0")
                       + " pollMs=" + _windowPollMs.ToString("F0")
                       + " ingest=" + _windowIngests + "/" + _windowIngestMs.ToString("F0") + "ms"
                       + " worstWork=" + _worstWorkMs.ToString("F1")
                       + " worstIngest=" + _worstIngestMs.ToString("F1")
                       + " worstGap=" + _worstGapMs.ToString("F1")
                       + " stalls=" + _windowStalls
                       + " pending=" + _pending.Count + " closing=" + _closing.Count
                       + " held=" + _built.Count);

        _windowStart = now;
        _windowFrames = _windowBuilt = _windowClosed = _windowStalls = _windowIngests = 0;
        _windowBuildMs = _windowPollMs = _windowIngestMs = _windowCloseMs = 0;
        _worstWorkMs = _worstGapMs = _worstIngestMs = 0;
    }

    /// <summary>
    /// Tells the server, once per link, whether this client can actually derive rock.
    ///
    /// <para>The server enrols on this and on nothing else, which is deliberate: while enrolled it
    /// stops sending rock, so a client enrolled on the strength of a granted feature bit it cannot
    /// honour would stand in empty space. Saying "no" is a complete, safe answer — the server keeps
    /// replicating, exactly as it does for a vanilla client.</para>
    ///
    /// <para>It reports the material patch separately from readiness because the server has to
    /// COMPARE that one rather than trust it. Both ends narrow SE's material pool through a patch on
    /// a reflected private member; if one binding holds and the other does not, the same rock is
    /// ice on one side and gold on the other and nothing says so.</para>
    /// </summary>
    private void Announce()
    {
        if (!Available) { _announced = false; return; }   // not linked, or the feature was withheld
        if (_announced) return;
        _announced = true;

        bool canDerive = CreateAsteroidShapeM != null;
        bool material = Factions.FeatureRockMaterialControl.Active;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteFeatureRockRequest(canDerive, material), true);
            Plugin.Log("ROCK: announced canDerive=" + canDerive + " materialControl=" + material
                       + (canDerive ? "" : " -- the server will keep sending rock."));
        }
        catch (Exception ex)
        {
            _announced = false;   // retried next frame; without it this client silently never enrols
            Plugin.Log("ROCK: announce failed, will retry: " + ex);
        }
    }

    private void DropHandoverWatch(long entityId)
    {
        for (int i = _handovers.Count - 1; i >= 0; i--)
            if (_handovers[i].EntityId == entityId) _handovers.RemoveAt(i);
    }

    private void WatchHandover(string storageName, long entityId, object ourStorage)
    {
        if (_handoverReports >= MaxHandoverReports) return;
        if (_handovers.Count >= MaxHandoverWatches) return;
        _handovers.Add(new HandoverWatch
        {
            StorageName = storageName,
            EntityId = entityId,
            OurStorage = ourStorage,
        });
    }

    /// <summary>
    /// Answers, for each rock a player has just drilled, what SE actually did with it. Three
    /// outcomes, and only one of them is the design working:
    ///
    /// <list type="bullet">
    /// <item><c>OK</c> — our entity survived and its storage was replaced by the server's. That is
    /// the in-place swap the whole feature depends on.</item>
    /// <item><c>STACKED</c> — a second voxel map now overlaps ours. The client is showing derived
    /// geometry underneath replicated geometry, which is the failure this design exists to make
    /// impossible; if this ever prints, the entity ids are not agreeing.</item>
    /// <item><c>GONE</c> — our entity was closed. Not necessarily wrong (SE's reverter may have
    /// reclaimed it) but it means the player's edit did not land on the rock we built.</item>
    /// </list>
    ///
    /// <para>A watch that reaches its deadline with the storage still ours reports INCONCLUSIVE
    /// rather than passing quietly: it means the server never sent the edited rock, which is its own
    /// bug and would otherwise look exactly like success.</para>
    /// </summary>
    private void PollHandovers()
    {
        for (int i = _handovers.Count - 1; i >= 0; i--)
        {
            HandoverWatch w = _handovers[i];
            string verdict = null;
            try
            {
                MyEntity entity;
                if (!MyEntities.TryGetEntityById(w.EntityId, out entity) || entity.Closed)
                {
                    // The rock genuinely vanishes for a moment mid-handover: OnLoad's non-swap path
                    // does TryRemoveExistingEntity and then rebuilds the entity, and the player sees
                    // it blink out and come back (observed live, 2026-08-25). Reporting GONE on the
                    // first miss would turn the most interesting case into the least informative
                    // verdict, so absence has to persist before it means anything.
                    w.MissedPolls++;
                    _handovers[i] = w;
                    if (w.MissedPolls < MissesBeforeGone) continue;
                    verdict = "GONE -- our entity has been absent for "
                            + MissesBeforeGone + " polls; it was not rebuilt under our id";
                }
                else
                {
                    if (w.MissedPolls != 0) { w.MissedPolls = 0; _handovers[i] = w; }

                    var voxel = entity as MyVoxelBase;
                    if (voxel == null)
                    {
                        verdict = "GONE -- our id no longer resolves to a voxel";
                    }
                    else if (!ReferenceEquals(voxel.Storage, w.OurStorage))
                    {
                        int twins = CountTwins(voxel);
                        verdict = twins > 0
                            ? "STACKED -- storage swapped BUT " + twins + " other voxel map(s) share this "
                              + "rock's identity; the client is showing two asteroids in one place"
                            : "OK -- server storage swapped into our entity in place, no duplicate";
                    }
                    // Still the storage we built, and nothing has touched the rock. That is the
                    // ordinary resting state of every rock a player has walked past, so it is not a
                    // verdict and must never time out into one -- the watch simply waits.

                }
            }
            catch (Exception ex)
            {
                verdict = "unreadable: " + ex.Message;
            }

            if (verdict == null) continue;   // untouched; keep watching

            _handovers.RemoveAt(i);
            _handoverReports++;

            bool last = _handoverReports >= MaxHandoverReports;
            Plugin.Log("ROCK_HANDOVER " + w.StorageName + " id=" + w.EntityId + " -- " + verdict
                       + (last ? " (last handover report this session)" : ""));

            // The cap is on reports, so it has to retire the watches that would produce them. The
            // remaining entries can only report after the line above has said none will, and they
            // are polled every frame until they do.
            if (last)
            {
                _handovers.Clear();
                return;
            }
        }
    }

    /// <summary>
    /// Other voxel maps that are duplicates of THIS rock -- same storage name, or sitting on its
    /// centre. Not merely maps whose bounding box overlaps.
    ///
    /// <para>The first version counted AABB intersections and reported STACKED on a rock that was
    /// fine (live, 2026-08-25). A 305 m rock in a belt has a 512 m storage cube for an AABB and 176
    /// rocks around it, so intersecting a neighbour is the normal case, not a defect. The failure
    /// this is actually looking for is one rock existing twice — which shows up as a shared storage
    /// name or two maps on the same point, and never as a bounding box that merely touches.</para>
    /// </summary>
    private static int CountTwins(MyVoxelBase voxel)
    {
        BoundingBoxD box = voxel.PositionComp.WorldAABB;
        Vector3D centre = box.Center;
        var found = new List<MyVoxelBase>();
        MyGamePruningStructure.GetAllVoxelMapsInBox(ref box, found);

        int twins = 0;
        for (int i = 0; i < found.Count; i++)
        {
            MyVoxelBase other = found[i];
            if (other == null || other == voxel || ((MyEntity)other).Closed) continue;

            bool sameName = other.StorageName != null
                         && string.Equals(other.StorageName, voxel.StorageName, StringComparison.Ordinal);
            bool sameSpot = Vector3D.DistanceSquared(other.PositionComp.WorldAABB.Center, centre)
                            < TwinCentreToleranceM * TwinCentreToleranceM;
            if (sameName || sameSpot) twins++;
        }
        return twins;
    }

    /// <summary>How close two voxel centres must be to be the same rock twice. Metres, not the
    /// hundreds a belt's neighbours sit apart: a genuine duplicate is built from the same position,
    /// so it lands on the same point rather than merely nearby.</summary>
    private const double TwinCentreToleranceM = 5.0;

    private void Build(WireRockObject o)
    {
        string name = o.StorageName ?? "";
        if (name.Length == 0 || _built.ContainsKey(name)) return;

        try
        {
            // Already in the world: the server replicated it before this client was enrolled, or SE
            // has it from an edit. Adopting rather than adding is what stops a second rock stacking
            // on the first -- AddVoxelMap with a taken id is an error, not a merge.
            if (MyEntities.EntityExists(o.EntityId)) return;

            // generator 0 explicitly, exactly as the server passes it. Left to its default this
            // resolves to MySession.Static.Settings.VoxelGeneratorVersion, and the two ends agreeing
            // about a world setting is precisely the drift this design exists to avoid.
            //
            // The authored ore is imposed here, in the same scope, for the same reason the server
            // does it there: SE picks an asteroid's material while the shape is being built, and
            // there is no affordable way to repaint it afterwards. It is shape-neutral -- the whole
            // material setup runs inside a PushSeed/Dispose pair that restores the RNG -- so the
            // geometry still comes from the seed alone, which is what makes this whole feature
            // possible. Cleared in a finally: a leaked value repaints the next rock.
            // The authored ore VOLUME is imposed the same way the server imposes it: SE reads
            // DepositsCountCoefficient and DepositSizeDenominator off the session while it builds
            // the rock, so a per-material value is applied by scoping the session setting across
            // this one call. Both ends must do it -- the server alone leaves the client generating
            // against the world default, which builds the same seed into a rock holding a different
            // amount of ore. Identity would agree while volume diverged, and it would be silent in
            // exactly the way the identity divergence was.
            MyObjectBuilder_SessionSettings settings = MySession.Static != null ? MySession.Static.Settings : null;
            float countBefore = settings != null ? settings.DepositsCountCoefficient : 0f;
            float sizeBefore = settings != null ? settings.DepositSizeDenominator : 0f;

            object shape;
            Factions.FeatureRockMaterialControl.Want(o.MaterialSubTypeId);
            try
            {
                if (settings != null)
                {
                    if (o.HasDepositCount) settings.DepositsCountCoefficient = (float)o.DepositCountCoefficient;
                    if (o.HasDepositSize && o.DepositSizeDenominator > 0)
                        settings.DepositSizeDenominator = (float)o.DepositSizeDenominator;
                }
                shape = CreateAsteroidShapeM.Invoke(
                    null, new object[] { o.ShapeSeed, (float)o.SizeM, 0, (int?)_generatorVersion });
            }
            finally
            {
                // Restored unconditionally, for the server's reason and one of our own: a leaked
                // setting here would retune every asteroid this CLIENT draws afterwards, including
                // vanilla rock the server never told us about.
                if (settings != null)
                {
                    settings.DepositsCountCoefficient = countBefore;
                    settings.DepositSizeDenominator = sizeBefore;
                }
                Factions.FeatureRockMaterialControl.Clear();
            }

            var provider = shape as IMyStorageDataProvider;
            if (provider == null) return;

            var voxelSize = new Vector3I(Math.Max(64, (int)Math.Ceiling(o.SizeM)));
            MyStorageBase storage = new MyOctreeStorage(provider, voxelSize);

            var centerM = new Vector3D(o.XKm * 1000.0, o.YKm * 1000.0, o.ZKm * 1000.0);
            Vector3D minCorner = centerM - new Vector3D(MathHelper.GetNearestBiggerPowerOfTwo((float)o.SizeM) / 2.0);

            MyVoxelBase voxel;
            RockContactPhysics.SpawningPristine = true;
            try { voxel = MyWorldGenerator.AddVoxelMap(name, storage, minCorner, o.EntityId); }
            finally { RockContactPhysics.SpawningPristine = false; }
            if (voxel == null) return;

            // Mirrors the server: the open seed marks rock nobody has touched, and SE's own trash
            // reverter skips it while it stands.
            voxel.IsSeedOpen = true;

            string captured = name;
            MyVoxelBase capturedVoxel = voxel;
            MyStorageBase storageRef = storage;
            storageRef.RangeChanged += (min, max, flags) =>
            {
                // First edit of any cause. The rock stops being derived and becomes replicated: the
                // server starts sending it, OnLoad swaps the authoritative storage into this same
                // entity, and closing it from here would delete a player's work.
                if (capturedVoxel.IsSeedOpen.HasValue) capturedVoxel.IsSeedOpen = null;
                if (_built.TryGetValue(captured, out long id)) _ownedIds.Remove(id);
                _built.Remove(captured);
            };

            _built[name] = voxel.EntityId;
            _ownedIds.Add(voxel.EntityId);
            _totalBuilt++;

            // Armed here, at build time, and NOT from RangeChanged.
            //
            // The first version watched the storage's RangeChanged and never fired once, on a rock
            // the client had definitely built and the player had definitely drilled (live,
            // 2026-08-25). The handover does not MODIFY our storage -- MyVoxelReplicable.OnLoad
            // assigns a new one over it -- so the object the handler was attached to is discarded
            // without ever raising anything. Watching from the moment the rock exists cannot miss
            // that, and the verdict logic was always the reference comparison rather than the event.
            WatchHandover(name, voxel.EntityId, storage);
        }
        catch (Exception ex)
        {
            Plugin.Log("ROCK: building '" + name + "' failed: " + ex);
        }
    }

    /// <summary>Closes one derived rock. Refuses anything this component does not still own, which
    /// is the whole safety property: an edited rock has already dropped out of <see cref="_built"/>
    /// and is never reachable from here.</summary>
    private void EnqueueClose(string storageName)
    {
        if (string.IsNullOrEmpty(storageName)) return;
        if (_closingSet.Add(storageName)) _closing.Enqueue(storageName);
    }

    private void Close(string storageName)
    {
        if (string.IsNullOrEmpty(storageName)) return;
        _queued.Remove(storageName);

        long entityId;
        if (!_built.TryGetValue(storageName, out entityId)) return;
        _built.Remove(storageName);
        _ownedIds.Remove(entityId);

        // Deliberately NOT dropping the handover watch here yet -- see below. The server removes a
        // rock from this client's set on the very first diff AFTER an edit, so Close() is reached
        // for precisely the rock whose handover we are trying to observe. Dropping the watch here
        // is why no verdict ever printed (live, 2026-08-25): the watch was destroyed moments before
        // it could speak. The watch is dropped only when the voxel is actually closed, below.

        try
        {
            MyEntity entity;
            if (!MyEntities.TryGetEntityById(entityId, out entity)) return;
            var voxel = entity as MyVoxelBase;
            if (voxel == null) return;

            // A last veto in case the edit hook did not run: an edited rock is the server's to send,
            // not ours to delete.
            if (!voxel.IsSeedOpen.HasValue || !voxel.IsSeedOpen.Value) return;
            if (voxel.ContentChanged) return;

            // Genuinely ours and untouched, so this is an ordinary despawn and not a handover.
            // Dropping the watch here (and only here) stops a fly-past reporting GONE.
            DropHandoverWatch(entityId);
            voxel.Close();
            _totalClosed++;
        }
        catch (Exception ex)
        {
            Plugin.Log("ROCK: closing '" + storageName + "' failed: " + ex);
        }
    }

    /// <summary>SE's asteroid shape factory. Internal to Sandbox.Game, so it is reached by name —
    /// the same reflected call the server makes, with the same signature, because identical
    /// arguments to a different overload would be identical seeds producing different rock.</summary>
    private static MethodInfo ResolveAsteroidShapeFactory()
    {
        try
        {
            Type provider = AccessTools.TypeByName("Sandbox.Game.World.Generator.MyCompositeShapeProvider");
            if (provider == null) return null;
            return AccessTools.Method(provider, "CreateAsteroidShape",
                new[] { typeof(int), typeof(float), typeof(int), typeof(int?) });
        }
        catch { return null; }
    }
}
