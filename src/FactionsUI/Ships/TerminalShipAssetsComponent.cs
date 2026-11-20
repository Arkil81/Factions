using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmptyKeys.UserInterface.Media.Imaging;
using Factions.Wire;
using Sandbox.Game.EntityComponents.Interfaces;
using Sandbox.Game.Screens.ViewModels.ServicesTerminal;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace FactionsUI.Ships;

/// <summary>
/// Owns the Services Terminal's ship-asset surface: the snapshot for the terminal that is currently
/// open, the vanilla grid-storage proxy that screen was built with, and the three requests the two
/// injected surfaces send.
///
/// <para><b>Nothing here is a subscription.</b> The terminal asks once when its view model is
/// constructed and again after every mutation, and drops the answer if it names a terminal the
/// player is no longer standing at. That is deliberate: the node window's single
/// <see cref="WireMessageKind.NodeDetailRequest"/> watch is the client's only watch, and a second
/// one taken from a block screen would take it away from the window that owns it.</para>
///
/// <para><b>Nothing is derived locally either.</b> Whether the surface may be used at all is
/// <c>mayUse</c> from the server, per terminal — not an ownership test against
/// <see cref="Map.MapComponent"/>'s world, which stops refreshing the moment the map closes. A
/// client-side hide is a convenience; the server refuses every request on its own account.</para>
///
/// <para>State is per open screen and is cleared on close: the proxy, the rows, the guid map and
/// the view model all belong to one terminal, and the next one builds its own.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class TerminalShipAssetsComponent : MySessionComponentBase
{
    /// <summary>Salt for <see cref="RowId"/>. Only has to be stable and unlikely to collide with a
    /// real stored-grid guid; the dictionary below is what actually recognises a row.</summary>
    private const string RowIdSalt = "FactionsUI.ShipAsset|";

    internal static TerminalShipAssetsComponent Instance { get; private set; }

    /// <summary>The terminal whose screen is open, or 0. Every reply is matched against it before
    /// it is shown — a snapshot in flight when the player walked to another block would otherwise
    /// be drawn under the wrong station's name.</summary>
    internal long TerminalEntityId { get; private set; }

    /// <summary>The vanilla proxy the open screen was constructed with. Kept for exactly one call:
    /// <see cref="IMyGridStorageProxy.TryRetrieveGrid"/> once the server has converted an asset into
    /// a real stored grid. Cleared on close so a stale screen's proxy is never used.</summary>
    private IMyGridStorageProxy _proxy;

    private MyServicesTerminalViewModel _viewModel;

    /// <summary>Server's verdict for this terminal. False means show nothing at all.</summary>
    internal bool MayUse { get; private set; }

    internal bool HasSnapshot { get; private set; }
    internal string NodeName { get; private set; }

    /// <summary>Synthetic rows by their deterministic id, in arrival order. The dictionary is the
    /// recognition test — a selected row is ours because it is in here, not because its guid looks
    /// a certain way.</summary>
    private readonly Dictionary<Guid, WireShipAssetRow> _rows = new Dictionary<Guid, WireShipAssetRow>();
    private readonly List<Guid> _rowOrder = new List<Guid>();

    /// <summary>Raised when a snapshot lands, so the injected buttons can re-evaluate their own
    /// visibility without polling. Cleared with the rest of the per-screen state.</summary>
    internal Action SnapshotChanged;

    public override void LoadData()
    {
        Instance = this;
        UplinkComponent.RegisterHandler(WireMessageKind.TerminalAssetsSnapshot, OnSnapshot);
        UplinkComponent.RegisterHandler(WireMessageKind.ShipWithdrawResult, OnWithdrawResult);
        UplinkComponent.RegisterHandler(WireMessageKind.ShipSendResult, OnSendResult);
        UplinkComponent.RegisterHandler(WireMessageKind.ShipScanResult, OnScanResult);
    }

    protected override void UnloadData()
    {
        Clear();
        Instance = null;
    }

    /// <summary>True when the server has granted the ship-asset feature to this client. Its own bit
    /// rather than NodeDetail's: this one spawns and despawns hulls, so a server must be able to
    /// withhold it while still granting the node window.</summary>
    internal static bool Available
    {
        get
        {
            UplinkComponent uplink = UplinkComponent.Instance;
            return uplink != null && uplink.HasFeature(WireFeatures.ShipAssets);
        }
    }

    /// <summary>True when the server granted either of the two Services Terminal surfaces. The
    /// snapshot answers the one question they share — whether the caller is standing at a terminal
    /// on a node their faction owns — so it is asked for whichever bit arrived, and each button
    /// then gates on its own feature.</summary>
    internal static bool AnyTerminalSurface
    {
        get { return Available || Nodes.StationRefitComponent.Available; }
    }

    // ---- Screen lifecycle ---------------------------------------------------------------------

    /// <summary>A Services Terminal screen is being built. Called from the view model's constructor,
    /// which is the one place that has both the block and the proxy.</summary>
    internal void OnTerminalOpened(MyServicesTerminalViewModel viewModel, long blockEntityId,
        IMyGridStorageProxy proxy)
    {
        Clear();
        _viewModel = viewModel;
        _proxy = proxy;
        TerminalEntityId = blockEntityId;
        RequestSnapshot();
    }

    /// <summary>The screen closed. Everything above belongs to it.</summary>
    internal void OnTerminalClosed()
    {
        Clear();
    }

    private void Clear()
    {
        TerminalEntityId = 0;
        _proxy = null;
        _viewModel = null;
        MayUse = false;
        HasSnapshot = false;
        NodeName = null;
        _rows.Clear();
        _rowOrder.Clear();
        SnapshotChanged = null;
    }

    // ---- Requests -----------------------------------------------------------------------------

    /// <summary>Ask what this faction holds that may be withdrawn here. Sent on open and again after
    /// every mutation, because a withdrawal, a send and a scan all move the answer. Asked for either
    /// terminal feature, not just this one: <see cref="MayUse"/> is the shared answer both surfaces
    /// gate on, and a server that grants only station refit must still be able to reach it.</summary>
    internal void RequestSnapshot()
    {
        if (!AnyTerminalSurface || TerminalEntityId == 0) return;
        Send(FactionsWire.WriteTerminalAssetsRequest(TerminalEntityId), "terminal assets request");
    }

    internal void RequestWithdraw(WireShipAssetRow row)
    {
        if (!Available || TerminalEntityId == 0) return;
        Send(FactionsWire.WriteShipWithdrawRequest(TerminalEntityId, row.NodeId ?? "", row.PrefabId ?? ""),
            "ship withdraw request");
    }

    internal void RequestSend(long gridEntityId)
    {
        if (!Available || TerminalEntityId == 0) return;
        Send(FactionsWire.WriteShipSendRequest(TerminalEntityId, gridEntityId), "ship send request");
    }

    internal void RequestScan(long gridEntityId, string scope, string name)
    {
        if (!Available || TerminalEntityId == 0) return;
        Send(FactionsWire.WriteShipScanRequest(TerminalEntityId, gridEntityId, scope ?? "", name ?? ""),
            "ship scan request");
    }

    private static void Send(byte[] payload, string what)
    {
        try
        {
            MyAPIGateway.Multiplayer.SendMessageToServer(FactionsWire.ChannelId, payload, true);
        }
        catch (Exception ex)
        {
            Plugin.Log(what + " failed: " + ex);
        }
    }

    // ---- Replies ------------------------------------------------------------------------------

    private void OnSnapshot(byte[] data)
    {
        long terminalEntityId;
        string nodeId;
        string nodeName;
        bool mayUse;
        string message;
        WireShipAssetRow[] rows;
        if (!FactionsWire.TryReadTerminalAssetsSnapshot(
                data, out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out rows))
        {
            Plugin.Log("TERMINAL_ASSETS unreadable");
            return;
        }

        // A snapshot for a terminal this client no longer has open. Dropped rather than shown: the
        // entity id is the only thing that pairs a reply with the screen that asked.
        if (terminalEntityId != TerminalEntityId || TerminalEntityId == 0) return;

        _rows.Clear();
        _rowOrder.Clear();
        MayUse = mayUse;
        NodeName = nodeName;
        HasSnapshot = true;

        if (mayUse && rows != null)
        {
            foreach (WireShipAssetRow row in rows)
            {
                if (row.Count <= 0 || string.IsNullOrEmpty(row.PrefabId)) continue;
                Guid id = RowId(row.NodeId, row.PrefabId);
                if (_rows.ContainsKey(id)) continue;
                _rows[id] = row;
                _rowOrder.Add(id);
            }
        }

        if (Config.Current.DebugLogging)
        {
            Plugin.Log("TERMINAL_ASSETS terminal=" + terminalEntityId + " node=" + (nodeId ?? "")
                       + " mayUse=" + mayUse + " rows=" + _rowOrder.Count
                       + (string.IsNullOrEmpty(message) ? "" : " message=" + message));
        }

        // A refusal that says why is worth showing once; an empty list with no reason reads as a
        // faction that simply owns no ships, which is a legitimate answer and needs no dialog.
        if (!mayUse && !string.IsNullOrEmpty(message)) Plugin.Log("TERMINAL_ASSETS refused: " + message);

        RefreshStoredGrids();
        Action changed = SnapshotChanged;
        if (changed != null)
        {
            try { changed(); }
            catch (Exception ex) { Plugin.Log("terminal ship-asset snapshot handler failed: " + ex); }
        }
    }

    private void OnWithdrawResult(byte[] data)
    {
        bool ok;
        Guid guid;
        string message;
        if (!FactionsWire.TryReadShipWithdrawResult(data, out ok, out guid, out message))
        {
            Plugin.Log("SHIP_WITHDRAW_RESULT unreadable");
            return;
        }

        if (ok && guid != Guid.Empty)
        {
            IMyGridStorageProxy proxy = _proxy;
            if (proxy == null)
            {
                // The screen closed between the request and the answer. The stored grid exists and
                // is the player's; it is simply pulled from the terminal on the next visit.
                Plugin.Log("SHIP_WITHDRAW landed with no open terminal; grid " + guid + " is stored");
            }
            else
            {
                try
                {
                    // From here it is vanilla: the retrieval queue, its countdown, its fee and its
                    // Enroute panel. Nothing of ours runs again until the next snapshot.
                    proxy.TryRetrieveGrid(guid, delayed: true);
                }
                catch (Exception ex)
                {
                    Plugin.Log("ship withdrawal retrieval failed: " + ex);
                }
            }
        }

        ShowMessage(ok ? "WITHDRAW" : "WITHDRAW REFUSED", message);
        RefreshStoredGrids(requestVanillaList: true);
        RequestSnapshot();
    }

    private void OnSendResult(byte[] data)
    {
        bool ok;
        string prefabId;
        string message;
        if (!FactionsWire.TryReadShipSendResult(data, out ok, out prefabId, out message))
        {
            Plugin.Log("SHIP_SEND_RESULT unreadable");
            return;
        }
        // The prefab is named separately because it is the half the player did not know: the match
        // is a block fingerprint, not the grid's name, so "it went in as X" is the useful part.
        string body = message ?? "";
        if (ok && !string.IsNullOrEmpty(prefabId)
            && body.IndexOf(prefabId, StringComparison.OrdinalIgnoreCase) < 0)
        {
            body = (body.Length == 0 ? "" : body + Environment.NewLine + Environment.NewLine)
                   + "Matched prefab: " + prefabId;
        }
        ShowMessage(ok ? "SEND TO NODE" : "SEND REFUSED", body);
        RefreshActiveGrids();
        RequestSnapshot();
    }

    private void OnScanResult(byte[] data)
    {
        bool ok;
        string message;
        if (!FactionsWire.TryReadShipScanResult(data, out ok, out message))
        {
            Plugin.Log("SHIP_SCAN_RESULT unreadable");
            return;
        }
        ShowMessage(ok ? "SCAN" : "SCAN REFUSED", message);
        RefreshActiveGrids();
        RequestSnapshot();
    }

    // ---- Rows ---------------------------------------------------------------------------------

    /// <summary>Deterministic id for one (node, prefab) pair, so vanilla's selection — which is
    /// tracked by guid — survives the list being rebuilt under it. Recognition is
    /// <see cref="TryGetRow"/>, not the shape of the guid: a marker prefix alone would claim a real
    /// stored grid that happened to collide.</summary>
    internal static Guid RowId(string nodeId, string prefabId)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] hash = md5.ComputeHash(
                Encoding.UTF8.GetBytes(RowIdSalt + (nodeId ?? "") + "|" + (prefabId ?? "")));
            return new Guid(hash);
        }
    }

    internal bool TryGetRow(Guid id, out WireShipAssetRow row)
    {
        return _rows.TryGetValue(id, out row);
    }

    internal bool IsOurs(Guid id)
    {
        return _rows.ContainsKey(id);
    }

    /// <summary>Vanilla's own stored-grid icons, by the same asset paths and with the same lazy
    /// construction: they cannot be built before <c>ImageManager.LoadImages</c> has run, which is
    /// why vanilla builds its pair inside the list callback rather than in a constructor.</summary>
    private static readonly Lazy<BitmapImage> IconLarge = new Lazy<BitmapImage>(
        () => new BitmapImage { TextureAsset = "Textures\\GUI\\Icons\\HUD 2017\\GridSizeLargeFit.png" });
    private static readonly Lazy<BitmapImage> IconSmall = new Lazy<BitmapImage>(
        () => new BitmapImage { TextureAsset = "Textures\\GUI\\Icons\\HUD 2017\\GridSizeSmallFit.png" });

    /// <summary>
    /// Appends one row per withdrawable (node, prefab) to the Stored Grids list.
    ///
    /// <para>Called from a prefix on <c>RefreshViewItems</c>, which runs <b>after</b> vanilla's own
    /// <c>m_dataItems.Clear()</c> in the list callback — so the rows are re-added on every refresh
    /// by design, and the dedupe below is what keeps a refresh that did not clear (paging, sorting,
    /// a search keystroke) from doubling them.</para>
    /// </summary>
    internal void AppendRows(List<MyGridStorageItemModel> dataItems)
    {
        if (dataItems == null || !MayUse || _rowOrder.Count == 0) return;
        for (int i = 0; i < _rowOrder.Count; i++)
        {
            Guid id = _rowOrder[i];
            WireShipAssetRow row;
            if (!_rows.TryGetValue(id, out row)) continue;
            if (Contains(dataItems, id)) continue;
            dataItems.Add(BuildModel(id, row));
        }
    }

    private static bool Contains(List<MyGridStorageItemModel> dataItems, Guid id)
    {
        for (int i = 0; i < dataItems.Count; i++)
        {
            MyGridStorageItemModel existing = dataItems[i];
            if (existing != null && existing.Id == id) return true;
        }
        return false;
    }

    /// <summary>One synthetic row. It is not a stored grid and never becomes one until the server
    /// converts it, so it carries no <c>DeleteCommand</c>, no storage position, and a sharing
    /// dropdown that is populated (the cell template reads it) but not changeable.</summary>
    private static MyGridStorageItemModel BuildModel(Guid id, WireShipAssetRow row)
    {
        var model = new MyGridStorageItemModel
        {
            Icon = IsSmallGrid(row.GridSize) ? IconSmall.Value : IconLarge.Value,
            Name = (row.PrefabId ?? "?") + " x" + row.Count.ToString(CultureInfo.InvariantCulture),
            Blocks = row.Blocks,
            PCU = row.Pcu,
            Owner = OwnerText(row),
            Id = id,
            OwnershipChangeAllowed = false,
            OwnershipTooltip = "A faction ship asset. Its sharing follows the node that holds it.",
        };

        var kinds = new ObservableCollection<OwnershipKindItemModel>();
        foreach (object value in Enum.GetValues(typeof(MyStoredGridOwnershipKind)))
        {
            var kind = new OwnershipKindItemModel
            {
                OwnershipKind = (MyStoredGridOwnershipKind)value,
                Label = value.ToString(),
            };
            kinds.Add(kind);
            if (kind.OwnershipKind == MyStoredGridOwnershipKind.FactionShare) model.SelectedOwnershipKind = kind;
        }
        model.OwnershipKinds = kinds;
        // Vanilla's own index for a faction-shared row. No PropertyChanged handler is attached, so
        // writing it cannot reach the proxy's SetOwnershipKind with a guid the server never stored.
        model.SelectedOwnershipKindIndex = 1;
        return model;
    }

    private static bool IsSmallGrid(string gridSize)
    {
        return !string.IsNullOrEmpty(gridSize)
               && gridSize.StartsWith("Small", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the Owner column reads. The faction comes from the player's own SE faction —
    /// the snapshot carries the node, not the tag, and the map's cache is not a source.</summary>
    internal static string OwnerText(WireShipAssetRow row)
    {
        string tag = LocalFactionTag();
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(tag)) sb.Append('[').Append(tag).Append("] · ");
        sb.Append(string.IsNullOrEmpty(row.NodeName) ? (row.NodeId ?? "?") : row.NodeName);
        sb.Append(" · ").Append(row.DistanceKm.ToString("0.#", CultureInfo.InvariantCulture)).Append(" km");
        return sb.ToString();
    }

    private static string LocalFactionTag()
    {
        try
        {
            var player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            if (player == null || MyAPIGateway.Session.Factions == null) return null;
            var faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(player.IdentityId);
            return faction == null ? null : faction.Tag;
        }
        catch (Exception ex)
        {
            Plugin.Log("local faction tag lookup failed: " + ex);
            return null;
        }
    }

    // ---- Driving the vanilla lists ------------------------------------------------------------

    /// <summary>Re-runs vanilla's own refresh so the appending prefix gets another pass.
    /// <paramref name="requestVanillaList"/> also re-asks the server for the real stored grids,
    /// which is what makes a just-converted asset appear as a vanilla row.</summary>
    private void RefreshStoredGrids(bool requestVanillaList = false)
    {
        try
        {
            MyServicesTerminalViewModel vm = _viewModel;
            if (vm == null) return;
            MyGridStorageViewModel storage = vm.GridStorageViewModel;
            if (storage == null) return;
            MyStoredGridsViewModel stored = storage.StoredGridsViewModel;
            if (stored == null) return;
            if (requestVanillaList) stored.RequestStoredGrids();
            else stored.RefreshViewItems();
        }
        catch (Exception ex)
        {
            Plugin.Log("stored-grid list refresh failed: " + ex);
        }
    }

    private void RefreshActiveGrids()
    {
        try
        {
            MyServicesTerminalViewModel vm = _viewModel;
            if (vm == null) return;
            MyGridStorageViewModel storage = vm.GridStorageViewModel;
            if (storage == null || storage.ActiveGridsViewModel == null) return;
            storage.ActiveGridsViewModel.RequestNearbyGrids();
        }
        catch (Exception ex)
        {
            Plugin.Log("active-grid list refresh failed: " + ex);
        }
    }

    internal MyGridStorageViewModel Storage
    {
        get
        {
            MyServicesTerminalViewModel vm = _viewModel;
            return vm == null ? null : vm.GridStorageViewModel;
        }
    }

    // ---- Dialogs ------------------------------------------------------------------------------

    /// <summary>The server's words, verbatim. Every refusal reason on this path is written to be
    /// read by a player, so paraphrasing here would only lose detail.</summary>
    internal static void ShowMessage(string caption, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        try
        {
            MyGuiScreenBase box = MyGuiSandbox.CreateMessageBox(
                MyMessageBoxStyleEnum.Info,
                buttonType: MyMessageBoxButtonsType.OK,
                messageText: new StringBuilder(message),
                messageCaption: new StringBuilder(caption),
                // Vanilla's own view-model dialogs open this way over an MVVM screen: the terminal
                // behind must keep drawing, and its opacity is already applied by the screen.
                canHideOthers: false,
                useOpacity: false);
            MyGuiSandbox.AddScreen(box);
        }
        catch (Exception ex)
        {
            Plugin.Log("terminal ship-asset message box failed: " + ex);
        }
    }

    /// <param name="yesLabel">What the confirming button says; each caller names its own action.</param>
    internal static void ConfirmYesNo(string caption, string body, Action onYes, string yesLabel = "Send")
    {
        try
        {
            MyGuiScreenBase box = MyGuiSandbox.CreateMessageBox(
                MyMessageBoxStyleEnum.Info,
                buttonType: MyMessageBoxButtonsType.YES_NO,
                messageText: new StringBuilder(body),
                messageCaption: new StringBuilder(caption),
                yesButtonText: MyStringId.GetOrCompute(string.IsNullOrEmpty(yesLabel) ? "Send" : yesLabel),
                noButtonText: MyStringId.GetOrCompute("Cancel"),
                callback: delegate(MyGuiScreenMessageBox.ResultEnum result)
                {
                    if (result != MyGuiScreenMessageBox.ResultEnum.YES) return;
                    try { onYes(); }
                    catch (Exception ex) { Plugin.Log("terminal ship-asset confirmation failed: " + ex); }
                },
                canHideOthers: false,
                useOpacity: false);
            MyGuiSandbox.AddScreen(box);
        }
        catch (Exception ex)
        {
            Plugin.Log("terminal ship-asset confirmation box failed: " + ex);
        }
    }
}
