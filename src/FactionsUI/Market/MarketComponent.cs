using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace FactionsUI.Market;

/// <summary>
/// Owns the market window's data: subscribes while the window is open, holds the decoded table.
///
/// Its own subscription, for the reason <see cref="Nodes.NodeDetailComponent"/> records — the map's
/// cache stops refreshing the moment <see cref="Map.MapScreen"/> closes, so a window reading it
/// would show whatever was true when the player last looked at the map and would look entirely
/// normal doing so. This component outlives that screen and that subscription.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class MarketComponent : MySessionComponentBase
{
    internal static MarketComponent Instance { get; private set; }

    /// <summary>Set optimistically at send time: a subscribe has no reply, and the alternative is
    /// re-sending every frame.</summary>
    internal bool Streaming { get; private set; }

    internal List<string> Items { get; private set; } = new List<string>();
    internal List<WireMarketStation> Stations { get; private set; } = new List<WireMarketStation>();
    internal List<WireMarketRow> Rows { get; private set; } = new List<WireMarketRow>();

    internal bool HasSnapshot { get; private set; }
    internal DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change, so the screen can poll rather than hold a callback into
    /// controls it may already have rebuilt.</summary>
    internal int Revision { get; private set; }

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.MarketSnapshot, OnSnapshot);
    }

    protected override void UnloadData()
    {
        Streaming = false;
        HasSnapshot = false;
        Items = new List<string>();
        Stations = new List<WireMarketStation>();
        Rows = new List<WireMarketRow>();
        Instance = null;
    }

    /// <summary>True once the server has granted the market feature. A server may withhold it while
    /// still granting the map that shows those stations exist — this is the one surface that reveals
    /// a rival's economy.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.Market);
        }
    }

    /// <summary>Subscribe or unsubscribe. Idempotent and safe to call every frame, so a window
    /// opened before the uplink was granted starts streaming as soon as it is.</summary>
    internal void SetStreaming(bool stream)
    {
        if (Streaming == stream) return;

        if (!Available)
        {
            if (!stream) Streaming = false;   // nothing to unsubscribe from; do not swallow a later subscribe
            return;
        }

        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(
                FactionsWire.ChannelId, FactionsWire.WriteMarketRequest(stream), true);
            Streaming = stream;
            if (!stream) HasSnapshot = false;
            Revision++;
        }
        catch (Exception ex)
        {
            Plugin.Log("market subscribe (" + stream + ") failed: " + ex);
        }
    }

    private void OnSnapshot(byte[] data)
    {
        List<string> items;
        List<WireMarketStation> stations;
        List<WireMarketRow> rows;
        if (!FactionsWire.TryReadMarketSnapshot(data, out items, out stations, out rows))
        {
            Plugin.Log("MARKET_SNAPSHOT unreadable");
            return;
        }

        Items = items ?? new List<string>();
        Stations = stations ?? new List<WireMarketStation>();
        Rows = rows ?? new List<WireMarketRow>();
        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;

        if (Config.Current.DebugLogging)
            Plugin.Log("MARKET_SNAPSHOT stations=" + Stations.Count + " rows=" + Rows.Count
                       + " items=" + Items.Count);
    }

    // ---- derived views the window renders ----------------------------------------------------

    /// <summary>One trading opportunity: buy an item where it is cheapest, sell where it is dearest.
    ///
    /// <para>Paired on the client on purpose. Every row it needs is already here, so this is
    /// presentation rather than a rule — and computing it server-side would put a second code path
    /// in a position to disagree with the price table it came from. Distance is <b>shown, not
    /// scored</b>, which is what keeps the ranking free of balance decisions the server owns.</para>
    /// </summary>
    internal struct Opportunity
    {
        public string Item;
        public int BuyStation;      // index into Stations: the station selling it (an Offer)
        public int SellStation;     // index into Stations: the station buying it (an Order)
        public int BuyPrice;
        public int SellPrice;
        public int MarginPerUnit;
        public int Units;           // min of the two quantities; -1 when either side is unknown
        public double DistanceKm;
        public bool Stale;          // either leg is remembered rather than live
    }

    /// <summary>Cheapest offer paired against dearest order, per item. One pass over the rows.</summary>
    internal List<Opportunity> BuildOpportunities()
    {
        var bestOffer = new Dictionary<ushort, int>();   // itemIndex -> row index
        var bestOrder = new Dictionary<ushort, int>();

        for (int i = 0; i < Rows.Count; i++)
        {
            WireMarketRow r = Rows[i];
            Dictionary<ushort, int> into = r.Side == 0 ? bestOffer : bestOrder;
            int existing;
            if (!into.TryGetValue(r.ItemIndex, out existing))
            {
                into[r.ItemIndex] = i;
                continue;
            }
            bool better = r.Side == 0
                ? r.UnitPrice < Rows[existing].UnitPrice     // buy low
                : r.UnitPrice > Rows[existing].UnitPrice;    // sell high
            if (better) into[r.ItemIndex] = i;
        }

        var result = new List<Opportunity>();
        foreach (KeyValuePair<ushort, int> pair in bestOffer)
        {
            int orderRow;
            if (!bestOrder.TryGetValue(pair.Key, out orderRow)) continue;

            WireMarketRow buy = Rows[pair.Value];
            WireMarketRow sell = Rows[orderRow];
            int margin = sell.UnitPrice - buy.UnitPrice;
            if (margin <= 0) continue;                       // not an opportunity, just a spread

            WireMarketStation a = Stations[buy.StationIndex];
            WireMarketStation b = Stations[sell.StationIndex];
            if (buy.StationIndex == sell.StationIndex) continue;   // nothing to haul

            result.Add(new Opportunity
            {
                Item = Items[pair.Key],
                BuyStation = buy.StationIndex,
                SellStation = sell.StationIndex,
                BuyPrice = buy.UnitPrice,
                SellPrice = sell.UnitPrice,
                MarginPerUnit = margin,
                Units = buy.Quantity < 0 || sell.Quantity < 0
                    ? -1
                    : Math.Min(buy.Quantity, sell.Quantity),
                DistanceKm = Distance(a, b),
                Stale = !a.Live || !b.Live,
            });
        }

        result.Sort((x, y) => y.MarginPerUnit.CompareTo(x.MarginPerUnit));
        return result;
    }

    private static double Distance(WireMarketStation a, WireMarketStation b)
    {
        double dx = a.XKm - b.XKm, dy = a.YKm - b.YKm, dz = a.ZKm - b.ZKm;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
