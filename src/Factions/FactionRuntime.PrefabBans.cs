using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Factions.Core;
using Newtonsoft.Json;

namespace Factions
{
    /// <summary>The server's prefab ban list (FAC-451): design families an admin has taken out of
    /// play. Held in <c>prefab_bans.json</c> in the shared data directory, so every Torch root on the
    /// box agrees, and edited at runtime by <c>factions prefab ban|unban|bans</c>. The list itself
    /// lives on the catalogue (<see cref="SePrefabCatalogue.IsBanned"/>), which every role pool and,
    /// through <see cref="WorldModel.PrefabBanned"/>, every build and refit gate reads.</summary>
    public sealed partial class FactionRuntime
    {
        public const string PrefabBansFileName = "prefab_bans.json";

        private sealed class PrefabBansFile
        {
            public List<string> Families { get; set; }
        }

        private readonly List<string> _bannedFamilies = new List<string>();

        private string PrefabBansPath
        {
            get { return _store != null ? Path.Combine(_store.BaseDir, PrefabBansFileName) : null; }
        }

        /// <summary>Reads the ban list into the catalogue and logs it beside the ceiling's boot line.
        /// An absent file is an empty list; an unreadable one is logged and treated as empty rather
        /// than stopping the boot.</summary>
        private void LoadPrefabBans()
        {
            _bannedFamilies.Clear();
            string path = PrefabBansPath;
            try
            {
                if (path != null && File.Exists(path))
                {
                    var doc = JsonConvert.DeserializeObject<PrefabBansFile>(File.ReadAllText(path));
                    if (doc != null && doc.Families != null)
                        foreach (string f in doc.Families)
                            if (!string.IsNullOrWhiteSpace(f) && !ContainsIgnoreCase(_bannedFamilies, f.Trim()))
                                _bannedFamilies.Add(f.Trim());
                }
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Prefab bans: " + path + " could not be read; nothing is banned.");
            }
            ApplyPrefabBans();
            FactionsPlugin.Log.Info("Prefab bans: " + _bannedFamilies.Count + " design(s) excluded from every role pool"
                + (_bannedFamilies.Count > 0 ? ": " + string.Join(", ", _bannedFamilies.ToArray()) : "")
                + " (" + PrefabBansFileName + ").");
        }

        private void ApplyPrefabBans()
        {
            var catalogue = _prefabCatalogue as SePrefabCatalogue;
            if (catalogue != null) catalogue.SetBannedFamilies(_bannedFamilies);
        }

        private bool SavePrefabBans(out string error)
        {
            error = null;
            string path = PrefabBansPath;
            if (path == null) { error = "no data directory"; return false; }
            try
            {
                var sorted = new List<string>(_bannedFamilies);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(path, JsonConvert.SerializeObject(new PrefabBansFile { Families = sorted }, Formatting.Indented));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>A player's refusal for a banned design, naming the ban rather than the ceiling.</summary>
        private string BannedRefusal(string prefabId)
        {
            return "'" + prefabId + "' is banned on this server and cannot be built.";
        }

        /// <summary>Bridge and admin chat `factions prefab ban &lt;design&gt;`: bans every version of the
        /// design, then cancels and refunds its queued builds, takes it off every shelf and cancels
        /// refits to it (<see cref="PrefabBanEnforcement"/>). Existing hulls stay; the reply counts
        /// them so they can be decommissioned.</summary>
        public string BanPrefab(string design)
        {
            if (_world == null) return "prefab: runtime not started (no session loaded).";
            if (string.IsNullOrEmpty(design)) return "Usage: factions prefab ban <design>";
            string family;
            string error = ResolveShipFamily(design, out family);
            if (error != null) return error;
            if (ContainsIgnoreCase(_bannedFamilies, family)) return "'" + family + "' is already banned.";

            _bannedFamilies.Add(family);
            if (!SavePrefabBans(out error))
            {
                _bannedFamilies.Remove(family);
                return "Could not write " + PrefabBansFileName + ": " + error + ". Nothing was banned.";
            }
            ApplyPrefabBans();

            PrefabBanResult r = PrefabBanEnforcement.Apply(_world, _states, family, _managerLog);
            Flush();
            _log.Write("PREFAB_BANNED family=" + family + " buildsCancelled=" + r.BuildsCancelled
                       + " delisted=" + r.Delisted + " refitsCancelled=" + r.RefitsCancelled
                       + " parked=" + r.Parked + " flying=" + r.Flying);

            var sb = new StringBuilder();
            sb.Append("Banned '").Append(family).Append("' (every version). Cancelled ")
              .Append(r.BuildsCancelled).Append(" queued build(s) with their components refunded, took ")
              .Append(r.Delisted).Append(" hull(s) off the shelves, cancelled ")
              .Append(r.RefitsCancelled).Append(" refit(s) to it.");
            sb.AppendLine().Append("Still in the world: ").Append(r.Parked).Append(" parked, ")
              .Append(r.Flying).Append(" flying.");
            if (r.ParkedByFaction.Count > 0)
            {
                var parts = new List<string>();
                foreach (KeyValuePair<string, int> kv in r.ParkedByFaction) parts.Add(kv.Key + " " + kv.Value);
                sb.Append(" Parked by faction: ").Append(string.Join(", ", parts.ToArray()))
                  .Append(". 'factions decommission <tag> ").Append(family).Append(" apply' removes them.");
            }
            return sb.ToString();
        }

        /// <summary>Bridge and admin chat `factions prefab unban &lt;design&gt;`. Takes effect at once;
        /// nothing that the ban cancelled comes back.</summary>
        public string UnbanPrefab(string design)
        {
            if (_world == null) return "prefab: runtime not started (no session loaded).";
            if (string.IsNullOrEmpty(design)) return "Usage: factions prefab unban <design>";
            string match = null;
            foreach (string f in _bannedFamilies)
                if (string.Equals(f, design.Trim(), StringComparison.OrdinalIgnoreCase)) { match = f; break; }
            if (match == null)
            {
                string family;
                if (ResolveShipFamily(design, out family) == null)
                    foreach (string f in _bannedFamilies)
                        if (string.Equals(f, family, StringComparison.OrdinalIgnoreCase)) { match = f; break; }
            }
            if (match == null) return "'" + design + "' is not banned.";

            _bannedFamilies.Remove(match);
            string error;
            if (!SavePrefabBans(out error))
            {
                _bannedFamilies.Add(match);
                return "Could not write " + PrefabBansFileName + ": " + error + ". It is still banned.";
            }
            ApplyPrefabBans();
            _log.Write("PREFAB_UNBANNED family=" + match);
            return "Unbanned '" + match + "'. It is back in the role pools and can be built again.";
        }

        /// <summary>Bridge and admin chat `factions prefab bans`.</summary>
        public string DescribePrefabBans()
        {
            if (_bannedFamilies.Count == 0) return "No prefab is banned (" + PrefabBansFileName + ").";
            var sorted = new List<string>(_bannedFamilies);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return "Banned designs (" + sorted.Count + "): " + string.Join(", ", sorted.ToArray());
        }

        /// <summary>The ship design family <paramref name="design"/> names, or an error. Any version's
        /// id or the family's own name resolves, ignoring case; a station design is refused, since a
        /// ban is about what fleets fly.</summary>
        private string ResolveShipFamily(string design, out string family)
        {
            family = null;
            var catalogue = _prefabCatalogue as SePrefabCatalogue;
            if (catalogue == null) return "No prefab catalogue is loaded.";
            string typed = design.Trim();
            string canonical;
            if (!catalogue.TryGetCanonicalId(typed, out canonical))
            {
                IReadOnlyList<string> versions = catalogue.VersionsOf(typed);
                if (versions.Count == 0) return "No design named '" + design + "'.";
                canonical = versions[0];
            }
            if (catalogue.IsStationPrefab(canonical))
                return "'" + design + "' is a station design; a ban covers ship designs.";
            family = catalogue.FamilyOf(canonical) ?? canonical;
            return null;
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
