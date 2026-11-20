using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Factions.Core;

namespace Factions
{
    /// <summary>Writes a small, API-safe catalogue of live planets from the SE game thread.</summary>
    internal sealed class PlanetSnapshotWriter
    {
        private const double RefreshSeconds = 30.0;
        private readonly string _path;
        private readonly IPlanetCatalogue _planets;
        private DateTime _nextWriteUtc = DateTime.MinValue;

        // Reads the host's own catalogue rather than scanning for planets itself: two live scans of
        // the same bodies is exactly the divergence docs/planet-navigation asked us not to create,
        // and the scan this used to do was a walk of every entity in the world (FAC-265).
        public PlanetSnapshotWriter(string storageDir, IPlanetCatalogue planets)
        {
            _path = Path.Combine(storageDir, "planets.json");
            _planets = planets ?? NullPlanetCatalogue.Instance;
        }

        public void Tick(DateTime now)
        {
            if (now < _nextWriteUtc) return;
            _nextWriteUtc = now.AddSeconds(RefreshSeconds);
            try
            {
                var snapshot = new PlanetSnapshotFile { At = now };
                foreach (PlanetRecord planet in _planets.Planets)
                {
                    snapshot.Planets.Add(new PlanetSnapshotEntry
                    {
                        Id = planet.Id,
                        Name = planet.Name,
                        Generator = planet.Generator,
                        X = planet.Center.X,
                        Y = planet.Center.Y,
                        Z = planet.Center.Z,
                        SurfaceRadius = planet.SurfaceRadius,
                        GravityRadius = planet.GravityRadius,
                        AtmosphereRadius = planet.AtmosphereRadius,
                        HasAtmosphere = planet.HasAtmosphere,
                    });
                }
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot, Formatting.None));
                // Replace atomically in the same directory: an API reader always sees either
                // the previous complete catalogue or the new one, never a missing/truncated file.
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Planet snapshot write failed.");
            }
        }

        private sealed class PlanetSnapshotFile
        {
            public DateTime At { get; set; }
            public List<PlanetSnapshotEntry> Planets { get; set; } = new List<PlanetSnapshotEntry>();
        }

        private sealed class PlanetSnapshotEntry
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Generator { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
            public double SurfaceRadius { get; set; }
            public double GravityRadius { get; set; }
            public double AtmosphereRadius { get; set; }
            public bool HasAtmosphere { get; set; }
        }
    }
}
