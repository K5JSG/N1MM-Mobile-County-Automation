using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using N1MMMobileCounty.Core;

/// <summary>
/// The Canadian parties that use federal electoral districts (2023 representation order): the Canadian Prairies QSO Party
/// (65 districts in AB/SK/MB, code list from cpqp.ve6hams.ca) and the British Columbia QSO Party (43 districts, N1MM+ code list).
/// Elections Canada publishes the boundaries in Statistics Canada's Lambert projection, converted here to latitude/longitude.
/// </summary>
internal static class Canada
{
    // Statistics Canada Lambert (NAD83, GRS80)
    private const double A = 6378137.0;
    private const double E2 = 0.0066943800229;
    private const double Lat1 = 49.0, Lat2 = 77.0, Lat0 = 63.390675, Lon0 = -91.8666666666667;
    private const double FalseEasting = 6200000.0, FalseNorthing = 3000000.0;

    private static readonly double E = Math.Sqrt(E2);
    private static readonly double N;
    private static readonly double F;
    private static readonly double Rho0;

    static Canada()
    {
        double M(double p) => Math.Cos(p) / Math.Sqrt(1 - (E2 * Math.Sin(p) * Math.Sin(p)));
        double T(double p) => Math.Tan((Math.PI / 4) - (p / 2)) / Math.Pow((1 - (E * Math.Sin(p))) / (1 + (E * Math.Sin(p))), E / 2);
        var p1 = Lat1 * Math.PI / 180;
        var p2 = Lat2 * Math.PI / 180;
        var p0 = Lat0 * Math.PI / 180;
        N = (Math.Log(M(p1)) - Math.Log(M(p2))) / (Math.Log(T(p1)) - Math.Log(T(p2)));
        F = M(p1) / (N * Math.Pow(T(p1), N));
        Rho0 = A * F * Math.Pow(T(p0), N);
    }

    /// <summary>Projected metres to (latitude, longitude) in degrees.</summary>
    public static (double Lat, double Lon) Inverse(double x, double y)
    {
        var dx = x - FalseEasting;
        var dy = Rho0 - (y - FalseNorthing);
        var rho = Math.Sign(N) * Math.Sqrt((dx * dx) + (dy * dy));
        var t = Math.Pow(rho / (A * F), 1 / N);
        var theta = Math.Atan2(dx, dy);
        var lon = (theta / N) + (Lon0 * Math.PI / 180);
        var lat = (Math.PI / 2) - (2 * Math.Atan(t));
        for (var i = 0; i < 10; i++)
        {
            var s = E * Math.Sin(lat);
            lat = (Math.PI / 2) - (2 * Math.Atan(t * Math.Pow((1 - s) / (1 + s), E / 2)));
        }

        return (lat * 180 / Math.PI, lon * 180 / Math.PI);
    }

    public static void Build(string fedShp, string cpNamesTsv, List<string> bcCodes, string outDir, StringBuilder manifest, StringBuilder review)
    {
        var records = Shapefile.Read(fedShp);
        var districts = records
            .Select(r => new District(
                int.Parse(r.Attributes["ED_CF_NUM"]),
                CleanName(r.Attributes["ED_NAMEE"]),
                r.Rings))
            .ToList();

        // --- Canadian Prairies (name list) ---
        var provinces = new Dictionary<string, int> { ["MB"] = 46, ["SK"] = 47, ["AB"] = 48 };
        var cp = File.ReadLines(cpNamesTsv)
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split('\t'))
            .Select(f => (Code: f[0], Name: f[1], Prov: f[2]))
            .ToList();
        var cpPool = districts.Where(d => d.Number / 1000 is 46 or 47 or 48).ToList();
        var cpCost = new int[cp.Count, cpPool.Count];
        for (var i = 0; i < cp.Count; i++)
        {
            for (var j = 0; j < cpPool.Count; j++)
            {
                cpCost[i, j] = cpPool[j].Number / 1000 != provinces[cp[i].Prov] ? Matcher.NoMatch : NameDistance(cp[i].Name, cpPool[j].Name);
            }
        }

        var cpAssign = Matcher.Assign(cpCost, cp.Count, cpPool.Count);
        var cpShapes = new List<CountyShape>();
        review.AppendLine($"==== CP  Canadian Prairies QSO Party  ({cp.Count} codes, 65 districts in AB/SK/MB)");
        for (var i = 0; i < cp.Count; i++)
        {
            var j = cpAssign[i];
            if (j < 0 || cpCost[i, j] >= Matcher.NoMatch)
            {
                review.AppendLine($"  !! {cp[i].Code,-4} {cp[i].Name}: no district fits");
                Problems++;
                continue;
            }

            review.AppendLine($"  {(cpCost[i, j] > 0 ? "?" : " ")} {cp[i].Code,-4} {cp[i].Name,-42} -> {cpPool[j].Name}  (distance {cpCost[i, j]})");
            cpShapes.Add(ToShape(cp[i].Code, cpPool[j].Name, cpPool[j]));
        }

        foreach (var d in cpPool.Where(d => !cpShapes.Any(s => s.Name == d.Name)))
        {
            review.AppendLine($"  -- no code for {d.Name}");
            Problems++;
        }

        Write(outDir, "CP", "Canadian Prairies QSO Party", "AB MB SK", cpShapes, manifest);

        // --- British Columbia (letters) ---
        var bcPool = districts.Where(d => d.Number / 1000 == 59).ToList();
        
        var bcCost = new int[bcCodes.Count, bcPool.Count];
        for (var i = 0; i < bcCodes.Count; i++)
        {
            for (var j = 0; j < bcPool.Count; j++)
            {
                bcCost[i, j] = Matcher.CostWords(bcCodes[i], bcPool[j].Name);
            }
        }

        var bcAssign = Matcher.Assign(bcCost, bcCodes.Count, bcPool.Count);
        var bcShapes = new List<CountyShape>();
        review.AppendLine($"==== BC  British Columbia QSO Party  ({bcCodes.Count} codes, {bcPool.Count} districts)");
        for (var i = 0; i < bcCodes.Count; i++)
        {
            var j = bcAssign[i];
            if (j < 0 || bcCost[i, j] >= Matcher.NoMatch)
            {
                review.AppendLine($"  !! {bcCodes[i],-4} no district fits");
                Problems++;
                continue;
            }

            review.AppendLine($"  {(bcCost[i, j] > 450 ? "?" : " ")} {bcCodes[i],-4} {bcPool[j].Name,-50} cost {bcCost[i, j]}");
            bcShapes.Add(ToShape(bcCodes[i], bcPool[j].Name, bcPool[j]));
        }

        foreach (var d in bcPool.Where(d => !bcShapes.Any(s => s.Name == d.Name)))
        {
            review.AppendLine($"  -- no code for {d.Name}");
            Problems++;
        }

        Write(outDir, "BC", "British Columbia QSO Party", "BC", bcShapes, manifest);
    }

    public static int Problems { get; private set; }

    /// <summary>
    /// Ontario (counties, districts, regions) and Atlantic Canada (counties; Newfoundland and Labrador divisions) on Statistics
    /// Canada census division (and, where a party splits one, census subdivision) boundaries saved as GeoJSON in latitude/longitude.
    /// </summary>
    public static void BuildCensusParties(string dir, string onTsv, string acTsv, string qcTsv, string outDir, StringBuilder manifest, StringBuilder review)
    {
        var polygons = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var props = feature.GetProperty("properties");
                string key;
                if (props.TryGetProperty("CSDUID", out var csd))
                {
                    key = $"csd:{csd.GetString()}";
                }
                else if (props.TryGetProperty("ERUID", out var er))
                {
                    key = $"er:{er.GetString()}";
                }
                else
                {
                    key = $"cd:{props.GetProperty("PRUID").GetString()}:{props.GetProperty("CDNAME").GetString()}";
                }

                polygons[key] = ReadGeometry(feature.GetProperty("geometry"));
            }
        }

        BuildFromKeys("ON", "Ontario QSO Party", "ON", onTsv, polygons, outDir, manifest, review);
        BuildFromKeys("AC", "Atlantic Canada QSO Party", "NB NL NS PE", acTsv, polygons, outDir, manifest, review);
        BuildFromKeys("QC", "Quebec QSO Party", "QC", qcTsv, polygons, outDir, manifest, review);
    }

    private static void BuildFromKeys(string id, string title, string area, string tsv, Dictionary<string, List<double[]>> polygons, string outDir, StringBuilder manifest, StringBuilder review)
    {
        var rows = File.ReadLines(tsv).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).ToList();
        var shapes = new List<CountyShape>();
        var used = new HashSet<string>();
        review.AppendLine($"==== {id}  {title}  ({rows.Count} codes)");
        foreach (var row in rows)
        {
            var rings = new List<double[]>();
            foreach (var key in row[2].Split('+'))
            {
                if (!polygons.TryGetValue(key, out var parts))
                {
                    review.AppendLine($"  !! {row[0],-6} {row[1]}: no polygon for '{key}'");
                    Problems++;
                    continue;
                }

                used.Add(key);
                rings.AddRange(parts);
            }

            if (rings.Count == 0)
            {
                continue;
            }

            review.AppendLine($"    {row[0],-6} {row[1],-50} <- {row[2]}");
            shapes.Add(new CountyShape(row[0], row[1], rings));
        }

        // polygons of this party's provinces that no code uses (Brant and Haldimand-Norfolk are replaced by their subdivisions)
        var prefixes = rows.SelectMany(r => r[2].Split('+')).Where(k => k.StartsWith("cd:", StringComparison.Ordinal)).Select(k => string.Join(':', k.Split(':').Take(2))).Distinct().ToList();
        foreach (var key in polygons.Keys.Where(k => k.StartsWith("cd:", StringComparison.Ordinal) && prefixes.Contains(string.Join(':', k.Split(':').Take(2))) && !used.Contains(k)))
        {
            review.AppendLine($"  -- polygon not used: {key}");
        }

        Write(outDir, id, title, area, shapes, manifest);
    }

    private static List<double[]> ReadGeometry(JsonElement geometry)
    {
        var rings = new List<double[]>();
        void AddRing(JsonElement ring)
        {
            var pts = new List<(double X, double Y)>();
            foreach (var p in ring.EnumerateArray())
            {
                pts.Add((p[0].GetDouble(), p[1].GetDouble())); // lon, lat
            }

            var flat = new double[pts.Count * 2];
            for (var i = 0; i < pts.Count; i++)
            {
                flat[i * 2] = pts[i].Y;
                flat[(i * 2) + 1] = pts[i].X;
            }

            if (pts.Count >= 4)
            {
                rings.Add(flat);
            }
        }

        var coords = geometry.GetProperty("coordinates");
        if (geometry.GetProperty("type").GetString() == "Polygon")
        {
            foreach (var ring in coords.EnumerateArray())
            {
                AddRing(ring);
            }
        }
        else
        {
            foreach (var polygon in coords.EnumerateArray())
            {
                foreach (var ring in polygon.EnumerateArray())
                {
                    AddRing(ring);
                }
            }
        }

        return rings;
    }

    /// <summary>The Elections Canada file repeats the name of some districts ("A,A"); keep one.</summary>
    private static string CleanName(string name)
    {
        var parts = name.Split(',').Select(p => p.Trim()).Distinct().ToArray();
        return parts.Length == 0 ? name : parts[0];
    }

    private sealed record District(int Number, string Name, List<double[]> Rings);

    /// <summary>Projected rings to a lat/lon shape, thinned to about 30 m (the source follows every shoreline inlet).</summary>
    private static CountyShape ToShape(string code, string name, District d)
    {
        var rings = new List<double[]>();
        foreach (var ring in d.Rings)
        {
            // Shapefile.Read stored (y, x) pairs of the projected coordinates as (lat, lon): ring[2i] = y, ring[2i+1] = x
            var pts = new List<(double X, double Y)>();
            for (var i = 0; i < ring.Length; i += 2)
            {
                pts.Add((ring[i + 1], ring[i]));
            }

            var kept = Simplify(pts, 30.0);
            if (kept.Count < 4)
            {
                continue;
            }

            var flat = new double[kept.Count * 2];
            for (var i = 0; i < kept.Count; i++)
            {
                var (lat, lon) = Inverse(kept[i].X, kept[i].Y);
                flat[i * 2] = lat;
                flat[(i * 2) + 1] = lon;
            }

            rings.Add(flat);
        }

        return new CountyShape(code, name, rings);
    }

    private static List<(double X, double Y)> Simplify(List<(double X, double Y)> pts, double tolerance)
    {
        if (pts.Count < 8)
        {
            return pts;
        }

        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var maxDist = 0.0;
            var index = -1;
            for (var i = a + 1; i < b; i++)
            {
                var dist = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    index = i;
                }
            }

            if (index >= 0 && maxDist > tolerance)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, b));
            }
        }

        var result = new List<(double X, double Y)>();
        for (var i = 0; i < pts.Count; i++)
        {
            if (keep[i])
            {
                result.Add(pts[i]);
            }
        }

        return result;
    }

    private static double DistanceToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = (dx * dx) + (dy * dy);
        if (len2 == 0)
        {
            return Math.Sqrt(((p.X - a.X) * (p.X - a.X)) + ((p.Y - a.Y) * (p.Y - a.Y)));
        }

        var t = Math.Max(0, Math.Min(1, (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2));
        var px = a.X + (t * dx);
        var py = a.Y + (t * dy);
        return Math.Sqrt(((p.X - px) * (p.X - px)) + ((p.Y - py) * (p.Y - py)));
    }

    private static void Write(string outDir, string id, string title, string area, List<CountyShape> shapes, StringBuilder manifest)
    {
        shapes.Sort((a, b) => string.CompareOrdinal(a.Abbreviation, b.Abbreviation));
        var file = $"party_{id}.bin";
        using (var output = File.Create(Path.Combine(outDir, file)))
        {
            CountyLocator.WriteCounties(output, shapes);
        }

        manifest.AppendLine($"{id}\t{title}\t{file}\t{area}\t{shapes.Count}");
        Console.WriteLine($"{id,-5} {shapes.Count,4} districts {new FileInfo(Path.Combine(outDir, file)).Length,10:N0} bytes");
    }

    /// <summary>0 when the names are the same ignoring case, accents, spaces and punctuation; otherwise edit distance.</summary>
    private static int NameDistance(string a, string b)
    {
        var x = Matcher.Letters(a);
        var y = Matcher.Letters(b);
        if (x == y)
        {
            return 0;
        }

        if (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal))
        {
            return 3;
        }

        var d = new int[x.Length + 1, y.Length + 1];
        for (var i = 0; i <= x.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= y.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= x.Length; i++)
        {
            for (var j = 1; j <= y.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            }
        }

        return d[x.Length, y.Length] + 4;
    }
}
