using System;
using System.Collections.Generic;
using NLog;
using ProtoBuf;
using Sandbox.ModAPI;

namespace Factions
{
    /// <summary>
    /// Reads shrapnel payloads from the Vanilla+ Framework, the one weapon property that changes a
    /// round's <b>damage</b> and is invisible to <see cref="Sandbox.Definitions.MyDefinitionManager"/>.
    ///
    /// <para><b>Why this exists at all.</b> A framework-driven weapon otherwise resolves correctly:
    /// its damage, range and rate of fire are ordinary SE definitions. What the framework adds is
    /// mostly about whether a round <i>connects</i> — homing, proximity fusing — which is a hit-rate
    /// question and deliberately not modelled here. Shrapnel is the exception: a shell that bursts
    /// into 20-35 children, each a real ammo definition carrying its own damage, delivers that damage
    /// for real, and nothing on the parent's own definition says so.</para>
    ///
    /// <para><b>Not a per-mod adapter.</b> This reads one framework's published API, and every weapon
    /// any author builds on that framework comes with it — Assault Weapons Pack and Guided Vanilla
    /// Rockets are both content inside it. Nothing here names a weapon, a mod or a subtype; the
    /// framework's own definitions name their children.</para>
    ///
    /// <para><b>The contract below is deliberately partial.</b> protobuf-net skips fields it has no
    /// member for, and the framework numbers its members explicitly, so declaring only the shrapnel
    /// payload reads correctly without copying its ~70 KB template. A framework that renumbers or
    /// drops these members yields nothing here and the scan falls back to definition-only damage —
    /// which is why every failure below is silent and non-fatal.</para>
    /// </summary>
    internal static class VpfAmmoBridge
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>The framework publishes its API on its workshop id <b>plus one</b>, once, from a
        /// session component's <c>Init</c>. A handler registered after that misses it permanently and
        /// silently, so registration happens at plugin init — see <see cref="Register"/>.</summary>
        private const long VpfApiChannel = 2915780228L;

        /// <summary><c>DefinitionType.AMMO</c> in the framework's own enum.</summary>
        private const byte AmmoDefinitionType = 0;

        /// <summary><c>DefinitionType.TURRET</c> in the framework's own enum.</summary>
        private const byte TurretDefinitionType = 2;

        // ---- the partial contract ---------------------------------------------------------------

        [ProtoContract]
        [ProtoInclude(1000, typeof(VpfAmmo))]
        [ProtoInclude(1001, typeof(VpfTurret))]
        private class VpfDefinition { }

        [ProtoContract]
        private class VpfTurret : VpfDefinition
        {
            [ProtoMember(1)] public string subtypeName;
            [ProtoMember(3)] public VpfAmmoGeneration AG_Stats;
        }

        /// <summary>The framework's <c>AmmoGeneration_Logic</c>. <c>AG_AmmoDefinitionName</c> is
        /// three-valued in its own code (<c>Weapon.cs</c>): a magazine subtype is generated; an empty
        /// string generates nothing and only gates firing on power; null is a capacitor. protobuf-net
        /// writes an empty string and omits a null one, so the default here must stay null.</summary>
        [ProtoContract]
        private class VpfAmmoGeneration
        {
            [ProtoMember(1)] public string AG_AmmoDefinitionName;
            [ProtoMember(4)] public float AG_GenerationTime;
            [ProtoMember(5)] public int AG_NumberGenerated;
        }

        [ProtoContract]
        private class VpfAmmo : VpfDefinition
        {
            [ProtoMember(1)] public string subtypeName;
            [ProtoMember(12)] public List<VpfShrapnel> SPL_Stats;
        }

        [ProtoContract]
        private class VpfShrapnel
        {
            [ProtoMember(1)] public string SPL_ShrapnelDefinitionName;
            [ProtoMember(4)] public int SPL_MinShrapnelCount;
            [ProtoMember(5)] public int SPL_MaxShrapnelCount;
            [ProtoMember(12)] public int SPL_MaxShrapnelRecursion;
        }

        // ---- what the rest of the host consumes --------------------------------------------------

        /// <summary>One spawned-child payload: which ammo, and how many of it.</summary>
        internal struct ShrapnelPayload
        {
            public string ChildAmmoSubtype;
            public int MinCount;
            public int MaxCount;
        }

        private static Action<object> _handler;
        private static IReadOnlyDictionary<string, Delegate> _api;
        private static Dictionary<string, List<ShrapnelPayload>> _byAmmoSubtype;
        private static Dictionary<string, MagazineGeneration> _generationByBlockSubtype;

        /// <summary>A magazine a weapon block makes for itself: which one, and how fast.</summary>
        internal struct MagazineGeneration
        {
            public string MagazineSubtype;
            public float MagazinesPerSecond;
        }

        /// <summary>The magazine this weapon block generates for itself, from power or fuel, through
        /// the framework's ammo generation. Its rate caps how fast the weapon can keep firing, and
        /// the magazine is never something to haul to it (FAC-478).</summary>
        internal static bool TryGetGeneration(string blockSubtype, out MagazineGeneration generation)
        {
            generation = default(MagazineGeneration);
            if (_generationByBlockSubtype == null || string.IsNullOrEmpty(blockSubtype)) return false;
            return _generationByBlockSubtype.TryGetValue(blockSubtype, out generation);
        }

        /// <summary>Operator balance lever on shrapnel's contribution; 1.0 counts the framework's own
        /// declared payload with no adjustment. Set from tuning at <see cref="Load"/>.</summary>
        internal static float DamageFraction = 1f;

        /// <summary>True once the framework's definitions have been read. False on any world without
        /// the framework, which is the ordinary case and not an error.</summary>
        internal static bool Loaded { get { return _byAmmoSubtype != null; } }

        /// <summary>Shrapnel this ammo subtype bursts into, or null.</summary>
        internal static List<ShrapnelPayload> PayloadFor(string ammoSubtype)
        {
            if (_byAmmoSubtype == null || string.IsNullOrEmpty(ammoSubtype)) return null;
            List<ShrapnelPayload> payload;
            return _byAmmoSubtype.TryGetValue(ammoSubtype, out payload) ? payload : null;
        }

        // ---- lifecycle ----------------------------------------------------------------------------

        /// <summary>Call at plugin init, before any world loads. Registers through
        /// <see cref="MyAPIUtilities.Static"/> rather than <c>MyAPIGateway.Utilities</c>: the former is
        /// built by its type's static constructor and exists now, the latter is only assigned from it
        /// later during session init, by which point the framework may already have published.</summary>
        internal static void Register()
        {
            if (_handler != null) return;
            try
            {
                _handler = OnApiPublished;
                MyAPIUtilities.Static.RegisterMessageHandler(VpfApiChannel, _handler);
            }
            catch (Exception ex)
            {
                _handler = null;
                Log.Info("VpfAmmoBridge: could not register: " + ex.Message);
            }
        }

        internal static void Unregister()
        {
            if (_handler == null) return;
            try { MyAPIUtilities.Static.UnregisterMessageHandler(VpfApiChannel, _handler); }
            catch { }
            _handler = null;
            _api = null;
            _byAmmoSubtype = null;
            _generationByBlockSubtype = null;
        }

        private static void OnApiPublished(object payload)
        {
            _api = payload as IReadOnlyDictionary<string, Delegate>;
        }

        /// <summary>Reads every framework ammo definition and keeps the shrapnel payloads. Call once
        /// the session is loaded, after the framework's content mods have published their
        /// definitions.</summary>
        internal static void Load(float damageFraction)
        {
            DamageFraction = damageFraction > 0f ? damageFraction : 0f;

            if (_api == null) return;

            Delegate rawNames, rawDef;
            if (!_api.TryGetValue("GetVPFDefinitionNames", out rawNames)
                || !_api.TryGetValue("GetSerializedVPFDefinition", out rawDef))
            {
                Log.Info("VpfAmmoBridge: framework API present but the definition accessors are not.");
                return;
            }

            var getNames = rawNames as Func<byte, string[]>;
            var getDefinition = rawDef as Func<byte, string, byte[]>;
            if (getNames == null || getDefinition == null)
            {
                Log.Info("VpfAmmoBridge: definition accessors have an unexpected signature; skipping.");
                return;
            }

            string[] names;
            try { names = getNames(AmmoDefinitionType); }
            catch (Exception ex) { Log.Info("VpfAmmoBridge: listing definitions threw: " + ex.Message); return; }
            if (names == null) return;

            var byAmmo = new Dictionary<string, List<ShrapnelPayload>>(StringComparer.OrdinalIgnoreCase);
            int read = 0, withShrapnel = 0;

            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;

                VpfAmmo ammo;
                try
                {
                    byte[] bytes = getDefinition(AmmoDefinitionType, name);
                    if (bytes == null || bytes.Length == 0) continue;
                    using (var stream = new System.IO.MemoryStream(bytes))
                        ammo = Serializer.Deserialize<VpfDefinition>(stream) as VpfAmmo;
                }
                catch
                {
                    // A member this contract does not model, or a shape it no longer matches. Skipping
                    // one definition must not cost the rest.
                    continue;
                }

                if (ammo == null) continue;
                read++;

                if (ammo.SPL_Stats == null || ammo.SPL_Stats.Count == 0) continue;

                string parent = string.IsNullOrEmpty(ammo.subtypeName) ? name : ammo.subtypeName;
                List<ShrapnelPayload> payloads = null;
                foreach (VpfShrapnel spl in ammo.SPL_Stats)
                {
                    if (spl == null || string.IsNullOrEmpty(spl.SPL_ShrapnelDefinitionName)) continue;
                    if (spl.SPL_MaxShrapnelCount <= 0 && spl.SPL_MinShrapnelCount <= 0) continue;

                    if (payloads == null) payloads = new List<ShrapnelPayload>();
                    payloads.Add(new ShrapnelPayload
                    {
                        ChildAmmoSubtype = spl.SPL_ShrapnelDefinitionName,
                        MinCount = spl.SPL_MinShrapnelCount,
                        MaxCount = spl.SPL_MaxShrapnelCount,
                    });
                }

                if (payloads == null) continue;
                byAmmo[parent] = payloads;
                withShrapnel++;
            }

            _byAmmoSubtype = byAmmo;
            Log.Info(string.Format(
                "VpfAmmoBridge: read {0} of {1} framework ammo definition(s); {2} burst into shrapnel"
                + " (damage fraction {3:0.##}).",
                read, names.Length, withShrapnel, DamageFraction));

            LoadGeneration(getNames, getDefinition);
        }

        /// <summary>Reads every framework turret definition and keeps the ones that generate a
        /// magazine.</summary>
        private static void LoadGeneration(Func<byte, string[]> getNames, Func<byte, string, byte[]> getDefinition)
        {
            string[] names;
            try { names = getNames(TurretDefinitionType); }
            catch (Exception ex) { Log.Info("VpfAmmoBridge: listing turret definitions threw: " + ex.Message); return; }
            if (names == null) return;

            var byBlock = new Dictionary<string, MagazineGeneration>(StringComparer.Ordinal);
            int read = 0;
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                VpfTurret turret;
                try
                {
                    byte[] bytes = getDefinition(TurretDefinitionType, name);
                    if (bytes == null || bytes.Length == 0) continue;
                    using (var stream = new System.IO.MemoryStream(bytes))
                        turret = Serializer.Deserialize<VpfDefinition>(stream) as VpfTurret;
                }
                catch
                {
                    continue;
                }
                if (turret == null) continue;
                read++;

                VpfAmmoGeneration ag = turret.AG_Stats;
                if (ag == null || string.IsNullOrEmpty(ag.AG_AmmoDefinitionName)) continue;
                if (string.Equals(ag.AG_AmmoDefinitionName, "null", StringComparison.OrdinalIgnoreCase)) continue;
                if (ag.AG_GenerationTime <= 0f || ag.AG_NumberGenerated <= 0) continue;

                string block = string.IsNullOrEmpty(turret.subtypeName) ? name : turret.subtypeName;
                byBlock[block] = new MagazineGeneration
                {
                    MagazineSubtype = ag.AG_AmmoDefinitionName,
                    MagazinesPerSecond = ag.AG_NumberGenerated / ag.AG_GenerationTime,
                };
            }

            _generationByBlockSubtype = byBlock;
            Log.Info(string.Format(
                "VpfAmmoBridge: read {0} of {1} framework turret definition(s); {2} generate their own magazine.",
                read, names.Length, byBlock.Count));
        }
    }
}
