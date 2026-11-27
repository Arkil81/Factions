using System;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Diplomacy;

/// <summary>
/// The Factions page Overview's link to the server: asks for the viewer's estimate of the selected
/// faction and keeps the last answer (docs/fog-of-war.md → The Factions page: Overview and Members).
/// The server holds no subscription, so the page asks again on a slow beat while a faction stays
/// selected.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class FactionIntelComponent : MySessionComponentBase
{
    internal const double RefreshSeconds = 15.0;

    internal static FactionIntelComponent Instance { get; private set; }

    internal int Revision { get; private set; }

    private WireFactionIntel _last;
    private bool _has;
    private string _asked;
    private DateTime _askedUtc;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.FactionIntelSnapshot, OnSnapshot);
    }

    protected override void UnloadData()
    {
        _has = false;
        _asked = null;
        Instance = null;
    }

    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.FactionIntel);
        }
    }

    /// <summary>The held answer for <paramref name="tag"/>; false while none has arrived for it.</summary>
    internal bool TryGet(string tag, out WireFactionIntel intel)
    {
        intel = _last;
        return _has && !string.IsNullOrEmpty(tag) && string.Equals(_last.Tag, tag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks for <paramref name="tag"/> when it is new, or when the last ask is older than
    /// <see cref="RefreshSeconds"/>.</summary>
    internal void Want(string tag)
    {
        if (string.IsNullOrEmpty(tag) || !Available) return;
        bool same = string.Equals(_asked, tag, StringComparison.OrdinalIgnoreCase);
        if (same && (DateTime.UtcNow - _askedUtc).TotalSeconds < RefreshSeconds) return;
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteFactionIntelRequest(tag), true);
            _asked = tag;
            _askedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Plugin.Log("faction intel request failed: " + ex);
        }
    }

    private void OnSnapshot(byte[] data)
    {
        WireFactionIntel intel;
        if (!FactionsWire.TryReadFactionIntelSnapshot(data, out intel)) return;
        _last = intel;
        _has = true;
        Revision++;
    }
}
