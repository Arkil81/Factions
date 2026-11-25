using System;
using Factions.Core;
using NLog;
using Sandbox.ModAPI;
using VRage;

namespace Factions
{
    /// <summary>Whether Digi's Aerodynamic Wings mod is applying wing force, so the analogue thrust
    /// law does not counter a force another mod has switched off.
    ///
    /// <para>The mod publishes a getter on its workshop-id channel about a second into the session,
    /// and any mod may switch it with a <c>(bool, string)</c> message on the same channel. Both are
    /// heard here. With neither heard the mod's own default, enabled, stands: a world without the mod
    /// has no wing blocks, so the answer is never consulted there.</para></summary>
    internal static class AeroWingsBridge
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static Action<object> _handler;
        private static Func<bool> _getter;
        private static bool _lastSet = true;

        internal static bool Enabled
        {
            get
            {
                Func<bool> getter = _getter;
                if (getter == null) return _lastSet;
                try { return getter(); }
                catch { return _lastSet; }
            }
        }

        /// <summary>Call at plugin init, before any world loads, for the reason
        /// <see cref="VpfAmmoBridge.Register"/> records.</summary>
        internal static void Register()
        {
            if (_handler != null) return;
            try
            {
                _handler = OnMessage;
                MyAPIUtilities.Static.RegisterMessageHandler(WingLift.ModId, _handler);
            }
            catch (Exception ex)
            {
                _handler = null;
                Log.Info("AeroWingsBridge: could not register: " + ex.Message);
            }
        }

        internal static void Unregister()
        {
            if (_handler == null) return;
            try { MyAPIUtilities.Static.UnregisterMessageHandler(WingLift.ModId, _handler); }
            catch { }
            _handler = null;
            _getter = null;
            _lastSet = true;
        }

        private static void OnMessage(object payload)
        {
            if (payload is MyTuple<Func<bool>, Action<bool, string>>)
                _getter = ((MyTuple<Func<bool>, Action<bool, string>>)payload).Item1;
            else if (payload is MyTuple<bool, string>)
                _lastSet = ((MyTuple<bool, string>)payload).Item1;
        }
    }
}
