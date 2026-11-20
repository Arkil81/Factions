using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>
    /// Parses the authored <c>world_features.json</c>. In Core rather than the host because it is
    /// pure JSON over pure data — and because a silently misparsed feature file means deep space
    /// silently holds no ore, which is exactly the failure a unit test should be able to catch.
    ///
    /// <para>Never throws and never returns null. A malformed file is reported and treated as blank:
    /// deep space going quiet is recoverable and visible in the log, whereas a plugin that fails to
    /// load is neither.</para>
    /// </summary>
    public static class WorldFeatureFile
    {
        public const string FileName = "world_features.json";

        private sealed class Document
        {
            public List<WorldFeature> Features { get; set; }
        }

        /// <summary>Parses <paramref name="json"/>. <paramref name="status"/> gets one line for the
        /// boot log describing what happened, including why any feature was rejected.</summary>
        public static List<WorldFeature> Parse(string json, out string status)
        {
            var empty = new List<WorldFeature>();
            if (string.IsNullOrEmpty(json))
            {
                status = FileName + ": empty -- deep space holds no minerals.";
                return empty;
            }

            Document doc;
            try
            {
                doc = JsonConvert.DeserializeObject<Document>(json);
            }
            catch (Exception ex)
            {
                status = FileName + ": unreadable, treating deep space as empty -- " + ex.Message;
                return empty;
            }

            if (doc == null || doc.Features == null || doc.Features.Count == 0)
            {
                status = FileName + ": no features defined -- deep space holds no minerals.";
                return empty;
            }

            var valid = new List<WorldFeature>();
            var rejected = new List<string>();
            foreach (WorldFeature f in doc.Features)
            {
                string why;
                if (Validate(f, out why)) valid.Add(f);
                else rejected.Add((f == null || string.IsNullOrEmpty(f.Name) ? "(unnamed)" : f.Name) + ": " + why);
            }

            status = FileName + ": " + valid.Count + " feature(s) loaded"
                   + (rejected.Count > 0
                        ? ", " + rejected.Count + " REJECTED -- " + string.Join("; ", rejected.ToArray())
                        : ".");
            return valid;
        }

        /// <summary>Rejects a feature that cannot describe a real volume. A silently empty region is
        /// worse than a named rejection: it is indistinguishable from a deliberately barren one.</summary>
        public static bool Validate(WorldFeature f, out string why)
        {
            if (f == null) { why = "null entry"; return false; }
            if (string.IsNullOrEmpty(f.Name)) { why = "no Name"; return false; }
            if (f.OuterRadiusKm <= 0) { why = "needs OuterRadiusKm > 0"; return false; }

            if (f.Shape != WorldFeatureShape.Sphere)
            {
                if (f.InnerRadiusKm < 0) { why = "InnerRadiusKm cannot be negative"; return false; }
                if (f.InnerRadiusKm >= f.OuterRadiusKm) { why = "InnerRadiusKm must be less than OuterRadiusKm"; return false; }
                if (f.Shape == WorldFeatureShape.Ring && f.HalfThicknessKm <= 0)
                {
                    why = "Ring needs HalfThicknessKm > 0";
                    return false;
                }
            }

            if (f.Density < 0 || f.Density > 1)
            {
                why = "Density must be 0..1 -- it scales against FeatureMaxDensityPer1000Km3, the same shape as SE's ProceduralDensity";
                return false;
            }

            if (f.Bands != null)
            {
                foreach (FeatureBand b in f.Bands)
                {
                    if (b == null) continue;
                    if (b.Density.HasValue && (b.Density.Value < 0 || b.Density.Value > 1))
                    {
                        why = "band " + b.InnerRadiusKm + "-" + b.OuterRadiusKm + " km: Density must be 0..1";
                        return false;
                    }
                    if (!ValidateSizeRanges(b.Materials, out why)) return false;
                }
            }

            // Materials may legitimately be empty: that authors a deliberately barren region, which
            // still shadows the planet reader beneath it.
            if (!ValidateSizeRanges(f.Materials, out why)) return false;

            why = null;
            return true;
        }

        /// <summary>Rejects a size range that cannot describe a real object. A null range is not a
        /// failure — it means the default, which is how an author declines to have an opinion.</summary>
        private static bool ValidateSizeRanges(List<FeatureMaterial> materials, out string why)
        {
            why = null;
            if (materials == null) return true;

            foreach (FeatureMaterial m in materials)
            {
                if (m == null || m.SizeRange == null) continue;
                string id = string.IsNullOrEmpty(m.SubTypeId) ? "(unnamed material)" : m.SubTypeId;
                if (m.SizeRange.MinM <= 0)
                {
                    why = id + ": SizeRange.MinM must be greater than 0";
                    return false;
                }
                if (m.SizeRange.MaxM < m.SizeRange.MinM)
                {
                    why = id + ": SizeRange.MaxM cannot be less than MinM";
                    return false;
                }
            }
            return true;
        }
    }
}
