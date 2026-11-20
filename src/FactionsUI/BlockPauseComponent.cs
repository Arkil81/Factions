using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI;

/// <summary>
/// Says, in a block's own terminal, that the server's load governor has paused it
/// (docs/load-governor.md, player toolbox step 3).
///
/// <para><b>Why the client writes it.</b> A block's custom info is local to each machine:
/// <c>MyTerminalBlock.RefreshCustomInfo</c> clears it and raises <c>AppendingCustomInfo</c> on the
/// machine that calls it, and nothing replicates the result. Text the server wrote would exist only in
/// the server's copy of the block. So the server sends the ids and this appends the notice, the way
/// the BuildInfo mod writes its lines.</para>
///
/// <para><b>Full state.</b> Each <see cref="WireMessageKind.BlockPauseStatus"/> replaces the last; an
/// empty one clears. A paused block this client has not streamed yet is picked up when it arrives,
/// and one that is closed and streamed back is attached again.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class BlockPauseComponent : MySessionComponentBase
{
    /// <summary>How often blocks not yet attached are looked for, ~2 s.</summary>
    private const int AttachIntervalTicks = 120;

    private static readonly Dictionary<long, WirePausedBlock> Paused = new Dictionary<long, WirePausedBlock>();
    private static readonly Dictionary<long, IMyTerminalBlock> Attached = new Dictionary<long, IMyTerminalBlock>();
    private int _tick;

    public override void LoadData()
    {
        UplinkComponent.RegisterHandler(WireMessageKind.BlockPauseStatus, OnStatus);
    }

    protected override void UnloadData()
    {
        DetachAll();
        Paused.Clear();
    }

    private static void OnStatus(byte[] data)
    {
        if (!FactionsWire.TryReadBlockPauseStatus(data, out List<WirePausedBlock> blocks)) return;

        var next = new Dictionary<long, WirePausedBlock>(blocks.Count);
        foreach (WirePausedBlock b in blocks) next[b.EntityId] = b;

        foreach (long id in new List<long>(Attached.Keys))
            if (!next.ContainsKey(id)) Detach(id);

        Paused.Clear();
        foreach (KeyValuePair<long, WirePausedBlock> kv in next) Paused[kv.Key] = kv.Value;

        // A block still paused may have changed cause (admin to governor or back): redraw it.
        foreach (IMyTerminalBlock block in Attached.Values) Refresh(block);
        AttachPending();
        Plugin.Log("BLOCK_PAUSE blocks=" + Paused.Count + " attached=" + Attached.Count);
    }

    public override void UpdateAfterSimulation()
    {
        if (++_tick < AttachIntervalTicks) return;
        _tick = 0;

        UplinkComponent uplink = UplinkComponent.Instance;
        if (uplink == null || !uplink.Linked)
        {
            // Without the link nothing will ever clear the notice, so it must not outlive it.
            if (Paused.Count > 0 || Attached.Count > 0)
            {
                DetachAll();
                Paused.Clear();
            }
            return;
        }
        if (Paused.Count > Attached.Count) AttachPending();
    }

    private static void AttachPending()
    {
        foreach (long id in Paused.Keys)
        {
            if (Attached.TryGetValue(id, out IMyTerminalBlock held))
            {
                if (!held.Closed) continue;
                held.AppendingCustomInfo -= Append;
                Attached.Remove(id);
            }
            if (!(MyAPIGateway.Entities.GetEntityById(id) is IMyTerminalBlock block) || block.Closed) continue;
            block.AppendingCustomInfo += Append;
            Attached[id] = block;
            Refresh(block);
        }
    }

    private static void Detach(long id)
    {
        if (!Attached.TryGetValue(id, out IMyTerminalBlock block)) return;
        block.AppendingCustomInfo -= Append;
        Attached.Remove(id);
        if (!block.Closed) Refresh(block);
    }

    private static void DetachAll()
    {
        foreach (long id in new List<long>(Attached.Keys)) Detach(id);
    }

    private static void Refresh(IMyTerminalBlock block)
    {
        try
        {
            block.RefreshCustomInfo();
            block.SetDetailedInfoDirty();
        }
        catch { /* a block torn down between the check and the call */ }
    }

    private static void Append(IMyTerminalBlock block, StringBuilder text)
    {
        if (block == null || !Paused.TryGetValue(block.EntityId, out WirePausedBlock paused)) return;
        if (text.Length > 0) text.AppendLine();
        if (paused.Class == WirePausedBlock.ScriptSlowed)
        {
            text.AppendLine("SLOWED - server load");
            text.AppendLine("This script uses more of the server's frame than one block may, so its scheduled runs are spaced out. It sees the gap in Runtime.TimeSinceLastRun and misses no update flags. Commands sent to it still run.");
            return;
        }
        if (paused.Class == WirePausedBlock.ScriptStopped)
        {
            text.AppendLine("STOPPED - server load");
            text.AppendLine("Each run of this script takes long enough to stall the server, so it is stopped for a while and then retried. Recompiling it retries at once. Commands sent to it meanwhile are dropped.");
            return;
        }
        text.AppendLine("PAUSED - server load");
        text.AppendLine(paused.ByAdmin
            ? "An admin has paused this block to reduce server load."
            : "The server is over capacity and your grids are using more than your share of it, so the server has paused this block.");
        text.AppendLine("It stays powered and resumes by itself when the load drops. Work it would have done meanwhile is skipped, not queued.");
    }
}
