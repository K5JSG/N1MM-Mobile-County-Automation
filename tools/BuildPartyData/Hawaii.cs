using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using N1MMMobileCounty.Core;

/// <summary>
/// The Hawaii QSO Party: 14 multiplier districts, each a set of ZIP codes (the sponsor's mult maps at hawaiiqsoparty.org).
/// The district boundaries are the Census ZIP code tabulation areas (ZCTA) of those ZIP codes, merged per district.
/// </summary>
internal static class Hawaii
{
    public static int Problems { get; private set; }

    // Niihau (district NII) is the part of Kauai County west of this longitude
    private const double NiihauEastEdge = -159.95;

    public static void Build(string zctaShp, string zipTsv, List<string> n1mmCodes, List<CountyShape>? land, string outDir, StringBuilder manifest, StringBuilder review)
    {
        var rows = File.ReadLines(zipTsv).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).ToList();
        var zipToCode = new Dictionary<string, string>();
        foreach (var row in rows)
        {
            foreach (var zip in (row.Length > 2 ? row[2] : string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                zipToCode[zip] = row[0];
            }
        }

        var zctas = Shapefile.Read(zctaShp)
            .Where(r => r.Attributes["ZCTA5CE20"].StartsWith("967", StringComparison.Ordinal) || r.Attributes["ZCTA5CE20"].StartsWith("968", StringComparison.Ordinal))
            .ToList();

        review.AppendLine($"==== HI  Hawaii QSO Party  ({rows.Count} districts from ZIP lists, {n1mmCodes.Count} N1MM+ codes)");
        foreach (var code in n1mmCodes.Where(c => rows.All(r => r[0] != c)))
        {
            review.AppendLine($"  !! N1MM+ code {code} has no district in the ZIP table");
            Problems++;
        }

        var rings = rows.ToDictionary(r => r[0], r => new List<double[]>());
        var seen = new HashSet<string>();
        var unlisted = new List<Shapefile.Record>();
        foreach (var z in zctas)
        {
            var zip = z.Attributes["ZCTA5CE20"];
            if (!zipToCode.TryGetValue(zip, out var code))
            {
                var c = Centre(z.Rings);
                if (c.Lat > 0)
                {
                    unlisted.Add(z); // American Samoa (96799) is south of the equator
                }

                continue;
            }

            seen.Add(zip);
            foreach (var ring in z.Rings)
            {
                rings[code].Add(ring);
            }
        }

        // Niihau has no ZIP area of its own in the Census data: take it from Kauai County's islands
        if (land != null)
        {
            foreach (var ring in land.Where(l => l.Name == "Kauai").SelectMany(l => l.Rings).Where(r => Centre(new List<double[]> { r }).Lon < NiihauEastEdge))
            {
                rings["NII"].Add(ring);
            }
        }

        // ZIP areas the sponsor's maps leave unlabelled (small or PO-box ZIPs): join the district whose boundary is nearest
        foreach (var z in unlisted)
        {
            var best = rows.Select(r => (Code: r[0], Dist: MinDistance(z.Rings, rings[r[0]]))).Where(x => x.Dist < double.MaxValue).OrderBy(x => x.Dist).First();
            var c = Centre(z.Rings);
            review.AppendLine($"  ?  ZCTA {z.Attributes["ZCTA5CE20"]} is on no ZIP list (centre {c.Lat:F3}, {c.Lon:F3}): joined {best.Code}, nearest ({best.Dist * 111.32:F2} km)");
            rings[best.Code].AddRange(z.Rings);
        }

        foreach (var zip in zipToCode.Keys.Where(z => !seen.Contains(z)))
        {
            review.AppendLine($"  -- ZIP {zip} ({zipToCode[zip]}) has no Census ZCTA");
        }

        if (land != null)
        {
            FillUncovered(rows.Select(r => r[0]).ToList(), rings, land, review);
        }

        var shapes = new List<CountyShape>();
        foreach (var row in rows)
        {
            if (rings[row[0]].Count == 0)
            {
                review.AppendLine($"  !! {row[0]} {row[1]}: no boundary");
                Problems++;
                continue;
            }

            review.AppendLine($"    {row[0],-5} {row[1],-18} {rings[row[0]].Count,3} rings  ZIPs {(row.Length > 2 ? row[2] : "-")}");
            shapes.Add(new CountyShape(row[0], row[1], rings[row[0]]));
        }

        shapes.Sort((a, b) => string.CompareOrdinal(a.Abbreviation, b.Abbreviation));
        if (land != null)
        {
            CheckCoverage(shapes, land, review);
        }

        using (var output = File.Create(Path.Combine(outDir, "party_HI.bin")))
        {
            CountyLocator.WriteCounties(output, shapes);
        }

        manifest.AppendLine($"HI\tHawaii QSO Party\tparty_HI.bin\tHI\t{shapes.Count}");
        Console.WriteLine($"HI    {shapes.Count,4} districts {new FileInfo(Path.Combine(outDir, "party_HI.bin")).Length,10:N0} bytes");
    }

    /// <summary>
    /// ZIP areas only cover populated land: Mauna Kea and Mauna Loa, Haleakala, the Alakai swamp and other empty country belong
    /// to no ZIP area, while the sponsor's maps give every acre to a district. Land that no ZIP area covers (Census county polygons
    /// minus the ZIP areas) goes to the district with the nearest ZIP boundary, as small rectangles on a 0.002 degree grid.
    /// </summary>
    private static void FillUncovered(List<string> codes, Dictionary<string, List<double[]>> rings, List<CountyShape> land, StringBuilder review)
    {
        const double Step = 0.002;
        var covered = new CountyLocator(codes.Where(c => rings[c].Count > 0).Select(c => new CountyShape(c, c, rings[c])).ToList()) { ShoreToleranceMeters = 0 };
        var landLocator = new CountyLocator(land) { ShoreToleranceMeters = 0 };

        // boundary vertices of every district, for the nearest-district search
        var vLat = new List<double>();
        var vLon = new List<double>();
        var vCode = new List<int>();
        for (var k = 0; k < codes.Count; k++)
        {
            foreach (var ring in rings[codes[k]])
            {
                for (var i = 0; i < ring.Length; i += 2)
                {
                    vLat.Add(ring[i]);
                    vLon.Add(ring[i + 1]);
                    vCode.Add(k);
                }
            }
        }

        var cells = 0;
        var added = new Dictionary<string, int>();
        for (var lat = 18.8; lat <= 22.3; lat += Step)
        {
            var runStart = double.NaN;
            var runCode = -1;
            void Flush(double lonEnd)
            {
                if (runCode >= 0)
                {
                    var (latS, latN, lonW, lonE) = (lat - (Step / 2), lat + (Step / 2), runStart - (Step / 2), lonEnd + (Step / 2));
                    rings[codes[runCode]].Add(new[] { latS, lonW, latS, lonE, latN, lonE, latN, lonW, latS, lonW });
                    added[codes[runCode]] = 1 + (added.TryGetValue(codes[runCode], out var a) ? a : 0);
                }

                runCode = -1;
            }

            var lastLon = double.NaN;
            for (var lon = -160.3; lon <= -154.7; lon += Step)
            {
                var code = -1;
                if (landLocator.Find(lat, lon) != null && covered.Find(lat, lon) == null)
                {
                    cells++;
                    var cosLat = Math.Cos(lat * Math.PI / 180);
                    var bestD = double.MaxValue;
                    for (var v = 0; v < vLat.Count; v++)
                    {
                        var dLat = vLat[v] - lat;
                        var dLon = (vLon[v] - lon) * cosLat;
                        var d = (dLat * dLat) + (dLon * dLon);
                        if (d < bestD)
                        {
                            bestD = d;
                            code = vCode[v];
                        }
                    }
                }

                if (code != runCode)
                {
                    Flush(lastLon);
                    runCode = code;
                    runStart = lon;
                }

                lastLon = lon;
            }

            Flush(lastLon);
        }

        review.AppendLine($"  filled {cells} uncovered land cells (0.002 degrees) with the nearest district: " + string.Join(' ', added.OrderBy(a => a.Key).Select(a => $"{a.Key}:{a.Value}")));
    }

    /// <summary>Smallest vertex-to-vertex distance in degrees between two ring sets (double.MaxValue when either is empty).</summary>
    private static double MinDistance(List<double[]> a, List<double[]> b)
    {
        var best = double.MaxValue;
        foreach (var ra in a)
        {
            foreach (var rb in b)
            {
                for (var i = 0; i < ra.Length; i += 2)
                {
                    for (var j = 0; j < rb.Length; j += 2)
                    {
                        var dLat = ra[i] - rb[j];
                        var dLon = (ra[i + 1] - rb[j + 1]) * 0.94; // cos(21 degrees)
                        var d = Math.Sqrt((dLat * dLat) + (dLon * dLon));
                        if (d < best)
                        {
                            best = d;
                        }
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Land (Census county polygons) that no district covers, sampled on a 0.004 degree grid (about 400 m).</summary>
    public static void CheckCoverage(List<CountyShape> districts, List<CountyShape> land, StringBuilder review)
    {
        var districtLocator = new CountyLocator(districts) { ShoreToleranceMeters = 0 };
        var landLocator = new CountyLocator(land) { ShoreToleranceMeters = 0 };
        var gaps = new List<(double Lat, double Lon)>();
        var total = 0;
        for (var lat = 18.8; lat <= 22.3; lat += 0.004)
        {
            for (var lon = -160.3; lon <= -154.7; lon += 0.004)
            {
                if (landLocator.Find(lat, lon) == null)
                {
                    continue;
                }

                total++;
                if (districtLocator.Find(lat, lon) == null)
                {
                    gaps.Add((lat, lon));
                }
            }
        }

        review.AppendLine($"  coverage: {total} land points sampled, {gaps.Count} not in any district");
        foreach (var g in gaps.Take(40))
        {
            review.AppendLine($"    gap at {g.Lat:F3}, {g.Lon:F3}");
        }

        Console.WriteLine($"HI coverage: {total} land points, {gaps.Count} uncovered");
    }

    private static (double Lat, double Lon) Centre(List<double[]> rings)
    {
        double lat = 0, lon = 0;
        var n = 0;
        foreach (var ring in rings)
        {
            for (var i = 0; i < ring.Length; i += 2)
            {
                lat += ring[i];
                lon += ring[i + 1];
                n++;
            }
        }

        return n == 0 ? (0, 0) : (lat / n, lon / n);
    }
}
