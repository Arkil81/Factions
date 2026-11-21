using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Factions.Core;

namespace Factions
{
    /// <summary>Published versions of a design (docs/issues/FAC-437-prefab-versions.md): what a
    /// capture under an existing name publishes, and who may publish it.</summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>What a capture will be registered as, once its name and scope have been
        /// judged.</summary>
        private sealed class CaptureTarget
        {
            /// <summary>The id the capture is registered under.</summary>
            public string PrefabId;
            /// <summary>The faction the capture is restricted to; null for every faction.</summary>
            public string OwnerTag;
            /// <summary>The design this capture is a version of; null for a new original.</summary>
            public string Family;
            public int Version;
            /// <summary>A version of a content (vanilla or mod) prefab: that faction's own line.</summary>
            public bool ContentFamily;
            /// <summary>Said to the player when the scope they typed was not the one applied.</summary>
            public string ScopeNote;
        }

        /// <summary>The runtime, faction, scope and name checks a capture makes before it looks at
        /// any grid. Split out so the chat verb can run them ahead of its raycast and the terminal's
        /// Scan button can run them at all, without either owning a copy of the rules.
        ///
        /// <para><b>A name that already names a design publishes a version of it</b>
        /// (<see cref="PrefabVersions"/>), when the caller may: a captured design only by the
        /// faction that captured its original, in that original's scope; a content prefab by any
        /// faction, as that faction's own line. The name is matched as typed first, so a mod prefab
        /// whose id has spaces resolves, then sanitised.</para>
        ///
        /// <para><b>Otherwise a name is taken if anything already answers to it</b>: a
        /// <c>Prefab/&lt;id&gt;</c> build recipe, or a station prefab the book-value walk priced.
        /// Both are ids a refit can name, and a capture sharing one would decide which design a node
        /// is rebuilt as by whichever registration ran last.</para></summary>
        private bool TryValidateCaptureRequest(FactionState owner, string scope, string name, string usage,
            out CaptureTarget target, out SePrefabCatalogue catalogue, out string refusal)
        {
            target = null;
            catalogue = null;

            if (_world == null) { refusal = "Factions runtime not ready."; return false; }
            if (owner == null) { refusal = "Your faction is not registered with Factions yet."; return false; }

            // The faction-only word is "own", not "faction": Torch slices a command's argument text
            // from the FIRST occurrence of the first argument anywhere in the line, so a word that is
            // a substring of the command path ("faction" inside "factions") hands the verb the path
            // words as its arguments. "faction" stays accepted for a client compiled before the
            // rename, which still sends it over the wire where no slicing happens.
            bool global;
            if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)) global = true;
            else if (string.Equals(scope, "own", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(scope, "faction", StringComparison.OrdinalIgnoreCase)) global = false;
            else { refusal = "Usage: " + usage; return false; }

            catalogue = _prefabCatalogue as SePrefabCatalogue;
            if (catalogue == null) { refusal = "No prefab catalogue loaded."; return false; }

            string family = ResolveFamilyName(catalogue, name);
            if (family != null)
                return TryTargetVersion(owner, global, family, catalogue, out target, out refusal);

            string prefabId = SanitizePrefabId(name);
            if (string.IsNullOrEmpty(prefabId))
            {
                refusal = "Usage: " + usage + " — a name is required (letters/digits/_/- only).";
                return false;
            }

            var nameRecipes = _recipes as SeRecipeRegistry;
            if ((nameRecipes != null && nameRecipes.HasPrefabRecipe(prefabId))
                || (_isKnownStationPrefab != null && _isKnownStationPrefab(prefabId)))
            {
                refusal = "'" + prefabId + "' is already taken — pick a different name.";
                return false;
            }

            target = new CaptureTarget { PrefabId = prefabId, OwnerTag = global ? null : owner.Tag };
            refusal = null;
            return true;
        }

        /// <summary>The design a typed name names, as its family id: the name as typed, then
        /// sanitised, against the catalogue and against captured families whose original has been
        /// deleted. Null when it names nothing.</summary>
        private string ResolveFamilyName(SePrefabCatalogue catalogue, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string[] forms = { name.Trim(), SanitizePrefabId(name) };
            foreach (string form in forms)
            {
                if (string.IsNullOrEmpty(form)) continue;
                string canonical;
                if (catalogue.TryGetCanonicalId(form, out canonical)) return catalogue.FamilyOf(canonical);
                if (catalogue.VersionsOf(form).Count > 0) return catalogue.FamilyOf(catalogue.VersionsOf(form)[0]);
            }
            return null;
        }

        private bool TryTargetVersion(FactionState owner, bool global, string family, SePrefabCatalogue catalogue,
            out CaptureTarget target, out string refusal)
        {
            target = null;
            bool contentFamily = catalogue.Contains(family) && !IsCapturedDesign(family);
            string ownerTag;
            string note = null;
            if (contentFamily)
            {
                ownerTag = owner.Tag;
                if (global)
                    note = "A version of a content prefab is your faction's own line: scoped to [" + owner.Tag + "] only.";
            }
            else
            {
                string publisher = _customPrefabStore != null ? _customPrefabStore.CapturedFamilyPublisher(family) : null;
                if (publisher == null)
                {
                    refusal = "'" + family + "' is already taken — pick a different name.";
                    return false;
                }
                if (!string.Equals(publisher, owner.Tag, StringComparison.OrdinalIgnoreCase))
                {
                    refusal = "'" + family + "' is [" + publisher + "]'s design — only they publish its versions. "
                              + "Capture yours under a new name to start your own.";
                    return false;
                }
                ownerTag = catalogue.OwnerFactionTag(FamilyReference(catalogue, family));
                if (string.IsNullOrEmpty(ownerTag)) ownerTag = null;
                if (global != (ownerTag == null))
                    note = "A version keeps its design's scope: " + (ownerTag == null ? "all factions." : "[" + ownerTag + "] only.");
            }

            var versions = new List<int>();
            foreach (string id in catalogue.VersionsOf(family)) versions.Add(catalogue.VersionOf(id));
            int version = PrefabVersions.NextVersion(versions);
            string prefabId = PrefabVersions.VersionId(family, version);
            while (catalogue.Contains(prefabId)) prefabId = PrefabVersions.VersionId(family, ++version);

            target = new CaptureTarget
            {
                PrefabId      = prefabId,
                OwnerTag      = ownerTag,
                Family        = family,
                Version       = version,
                ContentFamily = contentFamily,
                ScopeNote     = note,
            };
            refusal = null;
            return true;
        }

        /// <summary>The member of a family its kind and site are read from: the original while it
        /// is held, else any version — every version shares both.</summary>
        private static string FamilyReference(SePrefabCatalogue catalogue, string family)
        {
            if (catalogue.Contains(family)) return family;
            IReadOnlyList<string> versions = catalogue.VersionsOf(family);
            return versions.Count > 0 ? versions[0] : family;
        }

        /// <summary>The refusal when a version's kind or site does not match its design's, else
        /// null. A ship cannot be a version of a station, and a surface design's version stands only
        /// at the surface nodes the original does.</summary>
        private static string DescribeVersionMismatch(SePrefabCatalogue catalogue, CaptureTarget target,
                                                      bool isStation, string site)
        {
            if (target == null || target.Family == null) return null;
            string reference = FamilyReference(catalogue, target.Family);
            bool familyIsStation = catalogue.IsStationPrefab(reference);
            if (familyIsStation != isStation)
                return "'" + target.Family + "' is a " + (familyIsStation ? "station" : "ship")
                       + " design; a version of it must be one too.";
            if (isStation && !string.Equals(catalogue.Site(reference), site, StringComparison.OrdinalIgnoreCase))
                return "'" + target.Family + "' is a " + (catalogue.Site(reference) ?? "unsited")
                       + " design and this was scanned at a " + site + " site; scan it under a new name instead.";
            return null;
        }

        /// <summary>How a capture reply starts when it published a new version rather than a new
        /// design.</summary>
        public const string PublishedReplyPrefix = "Published '";

        /// <summary>The reply's first line for a capture: what was registered, and in whose scope.</summary>
        private static string DescribeCaptureTarget(string verb, CaptureTarget target)
        {
            string scope = target.OwnerTag == null ? "all factions" : "[" + target.OwnerTag + "] only";
            if (target.Family == null) return verb + " '" + target.PrefabId + "' (" + scope + ").";
            return PublishedReplyPrefix + target.PrefabId + "', version " + target.Version.ToString(CultureInfo.InvariantCulture)
                   + " of '" + target.Family + "' (" + scope + "). New builds of '" + target.Family
                   + "' use it from now on." + (target.ScopeNote != null ? " " + target.ScopeNote : "");
        }

        /// <summary>The log fields a capture adds when it is a version; empty for an original.</summary>
        private static string VersionLogFields(CaptureTarget target)
        {
            return target.Family == null ? ""
                : " family=" + target.Family + " version=" + target.Version.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>When the scanned group is <paramref name="owner"/>'s own node's materialised
        /// station, that node stands as the scanned design from now on, with no bill: the blocks
        /// that differ were welded there by hand (see <see cref="StationFounder.AdoptStandingDesign"/>).
        /// Returns the reply line, or null when the scan was of anything else.</summary>
        private string AdoptScannedStation(FactionState owner, string nodeId, string prefabId)
        {
            SpatialNode node;
            if (nodeId == null || !_world.Graph.Nodes.TryGetValue(nodeId, out node)
                || !string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
                return null;

            string oldPrefab = node.StationPrefabId;
            double dpsBefore = ShipPool.TotalDps(node.StationWeapons);
            long valueBefore = node.StationBookValue ?? 0L;
            string detail;
            if (!_stationFounder.AdoptStandingDesign(node, prefabId, out detail))
            {
                _log.Write(owner.Tag + " STATION_ADOPT_REFUSED @" + node.Id + " prefab=" + prefabId + " reason=" + detail);
                return nodeId + " still stands as '" + (oldPrefab ?? "(none)") + "': " + detail;
            }
            _log.Write(owner.Tag + " STATION_ADOPTED @" + node.Id + " "
                       + (oldPrefab ?? "(none)") + " -> " + prefabId
                       + " dps=" + dpsBefore.ToString("0", CultureInfo.InvariantCulture)
                       + "->" + ShipPool.TotalDps(node.StationWeapons).ToString("0", CultureInfo.InvariantCulture)
                       + " value=" + valueBefore.ToString(CultureInfo.InvariantCulture)
                       + "->" + (node.StationBookValue ?? 0L).ToString(CultureInfo.InvariantCulture));
            Flush();
            return nodeId + " now stands as '" + prefabId + "' — the station you scanned is what it respawns as.";
        }

        /// <summary>Player-facing `!factions station refit all &lt;design&gt; [force]`: queues a
        /// refit to the design's latest version at every node <paramref name="owner"/> holds that
        /// stands as an older version of it. Each node collects its own net bill and is judged by
        /// <see cref="ShipyardManager.QueueRefit"/> on its own, so a node that refuses says why and
        /// the rest still queue.</summary>
        public string QueueFamilyRefitForPlayer(FactionState owner, string design, bool force)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(design)) return "Usage: !factions station refit all <design> [force]";

            string latest = ResolveDesignForPlayer(design, owner.Tag);
            IPrefabCatalogue catalogue = _world.PrefabCatalogue;
            var stations = catalogue as IStationPrefabCatalogue;
            if (stations == null || !stations.IsStationPrefab(latest))
                return "'" + design + "' isn't a station design — see !factions station prefabs.";

            string restrictedTo = (catalogue as IOwnedPrefabCatalogue)?.OwnerFactionTag(latest);
            if (!PrefabVersions.VisibleTo(restrictedTo, owner.Tag))
                return "'" + latest + "' is a faction-restricted capture, not usable by [" + owner.Tag + "].";

            var sb = new StringBuilder();
            int queued = 0;
            var nodes = new List<SpatialNode>(_world.Graph.OwnedBy(owner.Tag));
            nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (SpatialNode node in nodes)
            {
                if (node.StableStationId == 0 || string.IsNullOrEmpty(node.StationPrefabId)) continue;
                if (!PrefabVersions.SameFamily(catalogue, node.StationPrefabId, latest)) continue;
                if (string.Equals(node.StationPrefabId, latest, StringComparison.OrdinalIgnoreCase)) continue;

                BuildQueueEntry entry;
                StationRefitResult verdict = ShipyardManager.QueueRefit(_world, _tuning, owner.Tag, node.Id,
                    latest, force, out entry, _managerLog);
                sb.Append("  ").Append(node.Id).Append(" (").Append(node.StationPrefabId).Append("): ");
                if (verdict != StationRefitResult.Ok)
                {
                    sb.AppendLine(DescribeRefitRefusal(verdict, owner, node.Id, latest));
                    continue;
                }
                queued++;
                long units = 0;
                if (entry != null)
                    foreach (KeyValuePair<string, long> kv in entry.Bom) units += kv.Value;
                sb.AppendLine("queued, " + units.ToString(CultureInfo.InvariantCulture) + " component(s) to collect");
            }
            if (queued > 0) Flush();
            if (sb.Length == 0)
                return "No node of yours stands as an older version of '" + PrefabVersions.FamilyOf(catalogue, latest)
                       + "'; the latest is '" + latest + "'.";
            sb.Insert(0, "Refit to '" + latest + "': " + queued.ToString(CultureInfo.InvariantCulture) + " queued.\n");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Player-facing `!factions fleet refit &lt;design&gt; &lt;n&gt;`: keeps up to
        /// <paramref name="cap"/> of the design's parked hulls in refit to its latest version at once
        /// (<see cref="HullRefit"/>), until none is left on an older one. 0 stops taking more; hulls
        /// already in refit finish.</summary>
        public string SetHullRefitCapForPlayer(FactionState owner, string design, int cap)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(design)) return "Usage: !factions fleet refit <design> <n>";

            IPrefabCatalogue catalogue = _world.PrefabCatalogue;
            var sePrefabs = catalogue as SePrefabCatalogue;
            string latest = ResolveDesignForPlayer(design, owner.Tag);
            if (sePrefabs == null || !sePrefabs.Contains(latest))
                return "No design named '" + design + "'.";
            if (sePrefabs.IsStationPrefab(latest))
                return "'" + design + "' is a station design — !factions station refit all " + design + " upgrades stations.";

            string family = PrefabVersions.FamilyOf(catalogue, latest);
            if (cap <= 0)
            {
                if (!owner.HullRefitCaps.Remove(family)) return "Nothing of '" + family + "' was being refit.";
                Flush();
                _log.Write("HULL_REFIT_CAP_BY_PLAYER " + owner.Tag + " family=" + family + " cap=0");
                return "Stopped refitting '" + family + "'. " + HullRefit.InRefit(_world, owner.Tag, family)
                       + " hull(s) already in refit will finish.";
            }

            owner.HullRefitCaps[family] = cap;
            Flush();
            _log.Write("HULL_REFIT_CAP_BY_PLAYER " + owner.Tag + " family=" + family + " cap=" + cap
                       + " latest=" + latest);
            return DescribeHullRefit(owner, family, cap)
                   + (string.Equals(latest, family, StringComparison.OrdinalIgnoreCase)
                       ? " Nothing newer than the original yet — refits start when a version is published."
                       : "");
        }

        /// <summary>Player-facing `!factions fleet refit`: every design with a refit cap, and how far
        /// it has come.</summary>
        public string DescribeHullRefitsForPlayer(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (owner.HullRefitCaps.Count == 0)
                return "No hull refits set. !factions fleet refit <design> <n> refits parked hulls of an older "
                       + "version to the latest, n at a time.";
            var families = new List<string>(owner.HullRefitCaps.Keys);
            families.Sort(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder("Hull refits:");
            foreach (string family in families)
                sb.AppendLine().Append("  ").Append(DescribeHullRefit(owner, family, owner.HullRefitCaps[family]));
            return sb.ToString();
        }

        private string DescribeHullRefit(FactionState owner, string family, int cap)
        {
            string latest = PrefabVersions.LatestFor(_world.PrefabCatalogue, family, owner.Tag);
            return "'" + family + "' -> '" + latest + "', up to " + cap.ToString(CultureInfo.InvariantCulture)
                   + " at a time: in_refit=" + HullRefit.InRefit(_world, owner.Tag, family).ToString(CultureInfo.InvariantCulture)
                   + " outdated_parked=" + HullRefit.Outdated(_world, owner.Tag, family, latest).ToString(CultureInfo.InvariantCulture)
                   + ". Only hulls parked at a shipyard are taken.";
        }

        /// <summary>Deletes every captured version a newer version of its own line supersedes and
        /// nothing references — no faction's brain, as it would be written now, and no recorded ship
        /// sale (<see cref="PrefabVersions.Unreferenced"/>). The latest version of each line stays,
        /// and so does every content prefab, which the capture store never holds. Runs once at boot;
        /// a version that stops being used mid-session goes at the next one. Answers how many it
        /// deleted.</summary>
        private int CollectSupersededCaptures()
        {
            var catalogue = _prefabCatalogue as SePrefabCatalogue;
            if (_customPrefabStore == null || catalogue == null || _world == null) return 0;

            var candidates = new List<string>();
            foreach (var capture in _customPrefabStore.AllEntries)
            {
                string owner = string.IsNullOrEmpty(capture.OwnerFactionTag) ? null : capture.OwnerFactionTag;
                if (!string.Equals(catalogue.LatestFor(capture.PrefabId, owner), capture.PrefabId, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(capture.PrefabId);
            }
            if (candidates.Count == 0) return 0;

            var documents = new List<string>(_states.Count + 1);
            foreach (FactionState st in _states)
                documents.Add(Newtonsoft.Json.JsonConvert.SerializeObject(
                    FactionStore.ToBrainDoc(st, _world.Graph.OwnedBy(st.Tag), FactionStore.DefaultLogCap)));
            documents.Add(Newtonsoft.Json.JsonConvert.SerializeObject(new List<string>(_customPrefabStore.SoldPrefabIds())));

            List<string> unreferenced = PrefabVersions.Unreferenced(candidates, documents);
            var recipes = _recipes as SeRecipeRegistry;
            foreach (string id in unreferenced)
            {
                string family = catalogue.FamilyOf(id);
                string owner = catalogue.OwnerFactionTag(id);
                string latest = catalogue.LatestFor(id, owner);
                catalogue.RemovePrefab(id);
                if (recipes != null) recipes.RemovePrefabRecipe(id);
                _customPrefabStore.Remove(id);
                _log.Write("PREFAB_VERSION_DELETED prefab=" + id + " family=" + family + " latest=" + latest);
            }
            if (unreferenced.Count > 0 && !string.IsNullOrEmpty(_prefabCatalogueStorageDir))
            {
                try { File.WriteAllText(Path.Combine(_prefabCatalogueStorageDir, "prefab_catalogue.json"), catalogue.ToJson()); }
                catch { /* best-effort refresh only -- live catalogue is unaffected */ }
            }
            if (candidates.Count > unreferenced.Count)
                _log.Write("PREFAB_VERSIONS_KEPT superseded=" + candidates.Count.ToString(CultureInfo.InvariantCulture)
                           + " referenced=" + (candidates.Count - unreferenced.Count).ToString(CultureInfo.InvariantCulture));
            return unreferenced.Count;
        }

        /// <summary>What scanning a grid that stands or was built as <paramref name="currentId"/>
        /// would publish for <paramref name="owner"/> — the scan dialog's "Publish as" choice. Runs
        /// the capture's own version rules (<see cref="TryTargetVersion"/>) without registering
        /// anything.</summary>
        public sealed class DesignVersionPreview
        {
            public bool Offered;
            public string Family;
            public string CurrentId;
            public string NextId;
            public bool ScopeAll;
            public string ScopeLabel;
            /// <summary>Why no version is offered; empty when the grid matched no design.</summary>
            public string Reason;
        }

        public DesignVersionPreview PreviewVersionForPlayer(FactionState owner, string currentId, bool station)
        {
            var preview = new DesignVersionPreview { CurrentId = currentId ?? "", Family = "", NextId = "", ScopeLabel = "", Reason = "" };
            var catalogue = _prefabCatalogue as SePrefabCatalogue;
            if (owner == null || catalogue == null || string.IsNullOrEmpty(currentId) || !catalogue.Contains(currentId))
                return preview;

            string family = catalogue.FamilyOf(currentId);
            preview.Family = family;
            if (catalogue.IsStationPrefab(FamilyReference(catalogue, family)) != station)
            {
                preview.Reason = "'" + family + "' is a " + (station ? "ship" : "station") + " design.";
                return preview;
            }

            CaptureTarget target;
            string refusal;
            if (!TryTargetVersion(owner, false, family, catalogue, out target, out refusal))
            {
                preview.Reason = refusal ?? "";
                return preview;
            }
            preview.Offered = true;
            preview.NextId = target.PrefabId;
            preview.ScopeAll = target.OwnerTag == null;
            preview.ScopeLabel = target.OwnerTag == null ? "Every faction" : "[" + target.OwnerTag + "] only";
            return preview;
        }

        /// <summary>The design a live ship grid was built or bought as, or null: the hull a
        /// materialised fleet entry spawned, then a recorded store sale, then a grid named exactly
        /// after a design. A grid a player has modified no longer matches any design's block
        /// fingerprint, which is why this does not ask the fingerprint.</summary>
        public string DesignOfShipGrid(Sandbox.Game.Entities.MyCubeGrid grid)
        {
            if (grid == null) return null;
            string id = _spawner != null ? _spawner.GridPrefabId(grid.EntityId) : null;
            if (!string.IsNullOrEmpty(id)) return id;

            CustomPrefabStore.SoldShipRecord sale;
            if (_customPrefabStore != null && _customPrefabStore.TryGetSoldShip(grid.EntityId, out sale))
                return sale.PrefabId;

            var catalogue = _prefabCatalogue as SePrefabCatalogue;
            string canonical;
            if (catalogue != null && !string.IsNullOrEmpty(grid.DisplayName)
                && catalogue.TryGetCanonicalId(grid.DisplayName.Trim(), out canonical))
                return canonical;
            return null;
        }

        /// <summary>The design a player's typed id means for <paramref name="factionTag"/>: its
        /// latest usable version, matched as typed first. Null when the catalogue holds nothing by
        /// that name.</summary>
        /// <summary>Player-facing `!factions fleet decommission &lt;design&gt; [count] [confirm]` and
        /// the Fleet window's Decommission button: retires the design from <paramref name="owner"/>'s
        /// fleet through <see cref="FleetDecommission.Run"/>. <paramref name="apply"/> false is the
        /// chat verb's dry run; the window has its own confirmation and applies.</summary>
        public string DecommissionForPlayer(FactionState owner, string design, int count, bool apply)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(design)) return "Usage: !factions fleet decommission <design> [count] [confirm]";
            return Decommission(owner, design, count, apply, "DECOMMISSION_BY_PLAYER",
                apply ? null : "Nothing changed yet: add 'confirm' to do it. It cannot be undone.");
        }

        /// <summary>Bridge verb `factions decommission &lt;tag&gt; &lt;design&gt; [count] [apply]` and the
        /// admin chat command: the same call for any registered faction, NPC included. An entry point,
        /// never a second implementation.</summary>
        public string DecommissionAdmin(string factionTag, string design, int count, bool apply)
        {
            if (_world == null) return "decommission: runtime not started (no session loaded).";
            FactionState owner = FindState(factionTag);
            if (owner == null) return "No faction '" + factionTag + "'.";
            if (string.IsNullOrEmpty(design)) return "Usage: factions decommission <tag> <design> [count] [apply]";
            return Decommission(owner, design, count, apply, "DECOMMISSION_BY_ADMIN",
                apply ? null : "Dry run: add 'apply' to do it.");
        }

        /// <summary>Reads the optional `[count] [&lt;applyWord&gt;]` tail every decommission grammar shares,
        /// in either order. A count of 0 or "all" means every parked hull.</summary>
        public static bool TryReadDecommissionArgs(IList<string> args, string applyWord, out int count,
            out bool apply, out string error)
        {
            count = 0;
            apply = false;
            error = null;
            if (args == null) return true;
            foreach (string a in args)
            {
                if (string.IsNullOrEmpty(a)) continue;
                if (string.Equals(a, applyWord, StringComparison.OrdinalIgnoreCase)) { apply = true; continue; }
                if (string.Equals(a, "all", StringComparison.OrdinalIgnoreCase)) { count = 0; continue; }
                int n;
                if (int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) { count = n; continue; }
                error = "'" + a + "' is neither a count nor '" + applyWord + "'.";
                return false;
            }
            return true;
        }

        private string Decommission(FactionState owner, string design, int count, bool apply, string logTag,
            string trailer)
        {
            IPrefabCatalogue catalogue = _world.PrefabCatalogue;
            string resolved = ResolveDesignForPlayer(design, owner.Tag);
            string family = PrefabVersions.FamilyOf(catalogue, resolved) ?? resolved;

            DecommissionResult r = FleetDecommission.Run(_world, owner, family, count, apply, _managerLog);
            if (r.Dismantled == 0 && r.MarkedOnReturn == 0
                && r.RemovedDoctrineLines.Count == 0 && r.RemovedShelfLines.Count == 0)
                return "[" + owner.Tag + "] has no hull of '" + family + "' to decommission"
                       + (r.SkippedFlying > 0 ? " (" + r.SkippedFlying + " flying; a decommission with a count marks none)." : ".");

            if (apply)
            {
                Flush();
                _log.Write(logTag + " " + owner.Tag + " family=" + family
                           + " dismantled=" + r.Dismantled + " delisted=" + r.Delisted + " fromYard=" + r.FromYard
                           + " markedOnReturn=" + r.MarkedOnReturn
                           + " bomCredited=" + r.BomUnitsCredited.ToString(CultureInfo.InvariantCulture)
                           + " lines=" + r.RemovedDoctrineLines.Count + " shelfLines=" + r.RemovedShelfLines.Count
                           + " skippedFlying=" + r.SkippedFlying + " skippedRepair=" + r.SkippedInRepair);
            }

            var sb = new StringBuilder();
            sb.Append(apply ? "Decommissioned " : "Would decommission ").Append(r.Dismantled)
              .Append(" hull(s) of '").Append(family).Append("'");
            if (r.Delisted > 0) sb.Append(", ").Append(r.Delisted).Append(" of them taken off the shelf");
            if (r.FromYard > 0) sb.Append(", ").Append(r.FromYard).Append(" of them out of repair or refit");
            sb.Append(": ").Append(r.BomUnitsCredited.ToString(CultureInfo.InvariantCulture))
              .Append(" component(s) credited where they were parked");
            if (r.ByNode.Count > 0)
            {
                var parts = new List<string>();
                foreach (KeyValuePair<string, int> kv in r.ByNode) parts.Add(kv.Key + " x" + kv.Value);
                sb.Append(" (").Append(string.Join(", ", parts)).Append(")");
            }
            sb.Append('.');
            if (r.RemovedDoctrineLines.Count > 0)
                sb.AppendLine().Append(apply ? "Removed" : "Would remove").Append(" doctrine line(s) ")
                  .Append(string.Join(", ", r.RemovedDoctrineLines)).Append(", so it is not rebuilt.");
            if (r.RemovedShelfLines.Count > 0)
                sb.AppendLine().Append(apply ? "Removed" : "Would remove").Append(" shelf line(s) ")
                  .Append(string.Join(", ", r.RemovedShelfLines)).Append(", so the store does not restock it.");
            if (r.MarkedOnReturn > 0)
                sb.AppendLine().Append(r.MarkedOnReturn).Append(apply ? " flying" : " flying would be")
                  .Append(" marked: each is dismantled when it comes home.");
            if (r.SkippedFlying > 0)
                sb.AppendLine().Append(r.SkippedFlying)
                  .Append(" flying left alone: a decommission with a count marks none.");
            if (r.SkippedInRepair > 0)
                sb.AppendLine().Append(r.SkippedInRepair).Append(" in repair or refit left alone: the count was reached.");
            if (r.StillQueued > 0)
                sb.AppendLine().Append(r.StillQueued)
                  .Append(" build(s) of it are still queued and will finish: cancel them in the station's Builds window.");
            if (!owner.IsPlayerFaction)
                sb.AppendLine().Append(_world.IsPrefabBanned(resolved)
                    ? "It is banned on this server, so it is not built again."
                    : "An NPC faction builds it again whenever it is the best hull for a job: 'factions prefab ban " + family + "' stops that.");
            if (trailer != null) sb.AppendLine().Append(trailer);
            return sb.ToString();
        }

        private string ResolveDesignForPlayer(string typed, string factionTag)
        {
            var catalogue = _world != null ? _world.PrefabCatalogue as SePrefabCatalogue : null;
            if (catalogue == null || string.IsNullOrEmpty(typed)) return typed;
            string canonical;
            if (!catalogue.TryGetCanonicalId(typed.Trim(), out canonical))
            {
                IReadOnlyList<string> versions = catalogue.VersionsOf(typed.Trim());
                if (versions.Count == 0) return typed;
                canonical = versions[0];
            }
            return catalogue.LatestFor(canonical, factionTag);
        }
    }
}
