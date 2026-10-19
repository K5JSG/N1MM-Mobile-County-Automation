using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace N1MMMobileCounty.Core
{
    /// <summary>One county's boundary: rings of (lat, lon) points plus a bounding box for fast rejection.</summary>
    public sealed class CountyShape
    {
        public CountyShape(string abbreviation, string name, List<double[]> rings)
        {
            Abbreviation = abbreviation;
            Name = name;
            Rings = rings;
            MinLat = MinLon = double.MaxValue;
            MaxLat = MaxLon = double.MinValue;
            foreach (var ring in rings)
            {
                for (var i = 0; i < ring.Length; i += 2)
                {
                    MinLat = Math.Min(MinLat, ring[i]);
                    MaxLat = Math.Max(MaxLat, ring[i]);
                    MinLon = Math.Min(MinLon, ring[i + 1]);
                    MaxLon = Math.Max(MaxLon, ring[i + 1]);
                }
            }
        }

        public string Abbreviation { get; }

        public string Name { get; }

        /// <summary>Each ring is a flat array: lat0, lon0, lat1, lon1, ...</summary>
        public List<double[]> Rings { get; }

        public double MinLat { get; }

        public double MaxLat { get; }

        public double MinLon { get; }

        public double MaxLon { get; }
    }

    /// <summary>Offline lat/lon to New York county lookup using the bundled Census boundary data.</summary>
    public sealed class CountyLocator
    {
        private const double MetersPerDegreeLat = 111_320.0;
        private readonly List<CountyShape> _counties;

        public CountyLocator(List<CountyShape> counties)
        {
            _counties = counties;
        }

        public int CountyCount => _counties.Count;

        public IReadOnlyList<CountyShape> Counties => _counties;

        /// <summary>
        /// Points that fall just outside every polygon (shoreline simplification, bridges, ferries) are
        /// assigned to the nearest county if it is within this many meters. Default 1000 m.
        /// </summary>
        public double ShoreToleranceMeters { get; set; } = 1000.0;

        /// <summary>The New York QSO Party counties (the original data set; other parties: <see cref="QsoParties.Load"/>).</summary>
        public static CountyLocator LoadEmbedded()
        {
            return QsoParties.Load("NY");
        }

        public static List<CountyShape> ReadCounties(Stream stream)
        {
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != 0x3143594E) // "NYC1"
            {
                throw new InvalidDataException("Unrecognized county data file.");
            }

            var counties = new List<CountyShape>();
            var count = reader.ReadInt32();
            for (var c = 0; c < count; c++)
            {
                var abbr = reader.ReadString();
                var name = reader.ReadString();
                var ringCount = reader.ReadInt32();
                var rings = new List<double[]>(ringCount);
                for (var r = 0; r < ringCount; r++)
                {
                    var pointCount = reader.ReadInt32();
                    var ring = new double[pointCount * 2];
                    for (var i = 0; i < ring.Length; i++)
                    {
                        ring[i] = reader.ReadInt32() / 1e5;
                    }

                    rings.Add(ring);
                }

                counties.Add(new CountyShape(abbr, name, rings));
            }

            return counties;
        }

        public static void WriteCounties(Stream stream, IReadOnlyList<CountyShape> counties)
        {
            using var writer = new BinaryWriter(stream);
            writer.Write(0x3143594E);
            writer.Write(counties.Count);
            foreach (var county in counties)
            {
                writer.Write(county.Abbreviation);
                writer.Write(county.Name);
                writer.Write(county.Rings.Count);
                foreach (var ring in county.Rings)
                {
                    writer.Write(ring.Length / 2);
                    foreach (var value in ring)
                    {
                        writer.Write((int)Math.Round(value * 1e5));
                    }
                }
            }
        }

        /// <summary>Returns the county containing the point, or null when the point is outside New York.</summary>
        public CountyShape? Find(double lat, double lon)
        {
            foreach (var county in _counties)
            {
                if (lat < county.MinLat || lat > county.MaxLat || lon < county.MinLon || lon > county.MaxLon)
                {
                    continue;
                }

                if (Contains(county, lat, lon))
                {
                    return county;
                }
            }

            return FindNearest(lat, lon);
        }

        /// <summary>
        /// Other counties whose boundary lies within <paramref name="maxMeters"/> of the point, nearest first
        /// (at most <paramref name="max"/>). Used to offer the county on the far side of a county line.
        /// </summary>
        public List<CountyShape> FindNearby(double lat, double lon, string excludeAbbreviation, double maxMeters, int max)
        {
            var metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(lat * Math.PI / 180.0);
            var padLat = maxMeters / MetersPerDegreeLat;
            var padLon = maxMeters / metersPerDegreeLon;
            var found = new List<(double Meters, CountyShape County)>();
            foreach (var county in _counties)
            {
                if (county.Abbreviation == excludeAbbreviation ||
                    lat < county.MinLat - padLat || lat > county.MaxLat + padLat ||
                    lon < county.MinLon - padLon || lon > county.MaxLon + padLon)
                {
                    continue;
                }

                var meters = Contains(county, lat, lon) ? 0 : DistanceToCounty(county, lat, lon, metersPerDegreeLon);
                if (meters <= maxMeters)
                {
                    found.Add((meters, county));
                }
            }

            found.Sort((a, b) => a.Meters.CompareTo(b.Meters));
            var result = new List<CountyShape>();
            for (var i = 0; i < found.Count && i < max; i++)
            {
                result.Add(found[i].County);
            }

            return result;
        }

        private static bool Contains(CountyShape county, double lat, double lon)
        {
            // Even-odd rule across every ring, so holes (and islands inside holes) work out naturally.
            var inside = false;
            foreach (var ring in county.Rings)
            {
                var n = ring.Length / 2;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    var latI = ring[i * 2];
                    var lonI = ring[i * 2 + 1];
                    var latJ = ring[j * 2];
                    var lonJ = ring[j * 2 + 1];
                    if ((lonI > lon) != (lonJ > lon) &&
                        lat < ((latJ - latI) * (lon - lonI) / (lonJ - lonI)) + latI)
                    {
                        inside = !inside;
                    }
                }
            }

            return inside;
        }

        private CountyShape? FindNearest(double lat, double lon)
        {
            if (ShoreToleranceMeters <= 0)
            {
                return null;
            }

            var metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(lat * Math.PI / 180.0);
            var padLat = ShoreToleranceMeters / MetersPerDegreeLat;
            var padLon = ShoreToleranceMeters / metersPerDegreeLon;
            CountyShape? best = null;
            var bestMeters = ShoreToleranceMeters;
            foreach (var county in _counties)
            {
                if (lat < county.MinLat - padLat || lat > county.MaxLat + padLat ||
                    lon < county.MinLon - padLon || lon > county.MaxLon + padLon)
                {
                    continue;
                }

                var meters = DistanceToCounty(county, lat, lon, metersPerDegreeLon);
                if (meters < bestMeters)
                {
                    bestMeters = meters;
                    best = county;
                }
            }

            return best;
        }

        private static double DistanceToCounty(CountyShape county, double lat, double lon, double metersPerDegreeLon)
        {
            var best = double.MaxValue;
            foreach (var ring in county.Rings)
            {
                var n = ring.Length / 2;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    var ax = (ring[j * 2 + 1] - lon) * metersPerDegreeLon;
                    var ay = (ring[j * 2] - lat) * MetersPerDegreeLat;
                    var bx = (ring[i * 2 + 1] - lon) * metersPerDegreeLon;
                    var by = (ring[i * 2] - lat) * MetersPerDegreeLat;
                    best = Math.Min(best, DistanceToSegmentFromOrigin(ax, ay, bx, by));
                }
            }

            return best;
        }

        private static double DistanceToSegmentFromOrigin(double ax, double ay, double bx, double by)
        {
            var dx = bx - ax;
            var dy = by - ay;
            var lengthSquared = (dx * dx) + (dy * dy);
            var t = lengthSquared < 1e-9 ? 0 : Math.Max(0, Math.Min(1, -((ax * dx) + (ay * dy)) / lengthSquared));
            var px = ax + (t * dx);
            var py = ay + (t * dy);
            return Math.Sqrt((px * px) + (py * py));
        }
    }
}
