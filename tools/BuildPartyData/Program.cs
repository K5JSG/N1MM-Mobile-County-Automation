using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using N1MMMobileCounty.Core;

// Builds the per-QSO-party county data embedded into N1MMMobileCounty.Core:
//   one party_<ID>.bin per party (boundaries of that party's counties, each labelled with the party's county code),
//   parties.tsv (id, title, file, states, count) and review.txt (every code -> county pairing, to eyeball).
//
// Inputs: the Census cartographic-boundary county shapefile (cb_*_us_county_500k) and N1MM+'s QSOParty.sec
// (SupportFiles folder), whose SubType lists are exactly the county codes N1MM+ accepts for each party.
//   usage: BuildPartyData <counties.shp> <QSOParty.sec> <output dir> [overrides.txt] [--fed <ED_CF.shp> <cp_names.tsv>]
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: BuildPartyData <counties.shp> <QSOParty.sec> <output dir> [overrides.txt]");
    return 1;
}

var outDir = args[2];
Directory.CreateDirectory(outDir);

// --- N1MM+ code lists -------------------------------------------------------------------------------------------
var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
List<string>? current = null;
foreach (var raw in File.ReadLines(args[1]))
{
    var line = raw.Trim();
    var header = Regex.Match(line, @"^Type=QSOPARTY\s+SubType=(\S+)");
    if (header.Success)
    {
        current = lists[header.Groups[1].Value] = new List<string>();
        continue;
    }

    if (current == null || line.Length == 0 || line[0] == '\'')
    {
        continue;
    }

    // "ALIAS LOGGED" lines: the last word is what N1MM+ logs; a single word is its own logged code.
    var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    var code = words[words.Length - 1].ToUpperInvariant();
    if (!current.Contains(code))
    {
        current.Add(code);
    }
}

// --- Census counties --------------------------------------------------------------------------------------------
var counties = Shapefile.Read(args[0]);
var byState = counties.GroupBy(c => c.Attributes["STUSPS"]).ToDictionary(g => g.Key, g => g.ToList());

var parties = new List<Party>
{
    new("AL", "Alabama QSO Party", "AL"), new("AZ", "Arizona QSO Party", "AZ"), new("AR", "Arkansas QSO Party", "AR"),
    new("CA", "California QSO Party", "CA"), new("CO", "Colorado QSO Party", "CO"), new("DE", "Delaware QSO Party", "DE"),
    new("FL", "Florida QSO Party", "FL"), new("GA", "Georgia QSO Party", "GA"), new("ID", "Idaho QSO Party", "ID"),
    new("IL", "Illinois QSO Party", "IL"), new("IN", "Indiana QSO Party", "IN", StateInCode.None, true), new("IA", "Iowa QSO Party", "IA"),
    new("KS", "Kansas QSO Party", "KS"), new("KY", "Kentucky QSO Party", "KY"), new("LA", "Louisiana QSO Party", "LA"),
    new("ME", "Maine QSO Party", "ME"), new("MD", "Maryland-DC QSO Party", "MD DC"), new("MI", "Michigan QSO Party", "MI"),
    new("MN", "Minnesota QSO Party", "MN"), new("MS", "Mississippi QSO Party", "MS"), new("MO", "Missouri QSO Party", "MO"),
    new("MT", "Montana QSO Party", "MT"), new("NE", "Nebraska QSO Party", "NE"), new("NV", "Nevada QSO Party", "NV", StateInCode.None, true),
    new("NH", "New Hampshire QSO Party", "NH"), new("NJ", "New Jersey QSO Party", "NJ"), new("NM", "New Mexico QSO Party", "NM"),
    new("NY", "New York QSO Party", "NY"), new("NC", "North Carolina QSO Party", "NC"), new("ND", "North Dakota QSO Party", "ND"),
    new("OH", "Ohio QSO Party", "OH"), new("OK", "Oklahoma QSO Party", "OK"), new("OR", "Oregon QSO Party", "OR"),
    new("PA", "Pennsylvania QSO Party", "PA"), new("SC", "South Carolina QSO Party", "SC"), new("SD", "South Dakota QSO Party", "SD"),
    new("TN", "Tennessee QSO Party", "TN"), new("TX", "Texas QSO Party", "TX"), new("VT", "Vermont QSO Party", "VT"),
    new("VA", "Virginia QSO Party", "VA"), new("WA", "Washington State Salmon Run", "WA"), new("WV", "West Virginia QSO Party", "WV"),
    new("WI", "Wisconsin QSO Party", "WI"),
    new("7QP", "7th Call Area QSO Party", "AZ ID MT NV OR UT WA WY", StateInCode.Prefix),
    new("NEWE", "New England QSO Party", "CT ME MA NH RI VT", StateInCode.Prefix),
};

// code overrides found during review: "PARTY:CODE<TAB>county" (county = Census NAMELSAD, optionally ", ST")
var overrides = Overrides.Load(args.Length > 3 && !args[3].StartsWith("--") ? args[3] : string.Empty);

var review = new StringBuilder();
var prior = new Dictionary<(string State, string Code), Shapefile.Record>(); // single-state results, reused by the multi-state lists
var manifest = new StringBuilder();
var problems = 0;
foreach (var party in parties)
{
    if (!lists.TryGetValue(party.Id, out var codes))
    {
        Console.Error.WriteLine($"No N1MM+ list for {party.Id}");
        problems++;
        continue;
    }

    // group the codes by the state they belong to
    var groups = new Dictionary<string, List<(string Code, string CountyPart)>>();
    foreach (var code in codes)
    {
        var state = "*";
        var countyPart = code;
        if (party.StripOwnState && code.StartsWith(party.States, StringComparison.Ordinal))
        {
            countyPart = code.Substring(2);
        }
        else if (party.Where == StateInCode.Prefix)
        {
            state = code.Substring(0, 2);
            countyPart = code.Substring(2);
        }
        else if (party.Where == StateInCode.Suffix)
        {
            state = code.Substring(code.Length - 2);
            countyPart = code.Substring(0, code.Length - 2);
        }

        if (!groups.TryGetValue(state, out var g))
        {
            groups[state] = g = new List<(string, string)>();
        }

        g.Add((code, countyPart));
    }

    var shapes = new List<CountyShape>();
    review.AppendLine($"==== {party.Id}  {party.Title}  ({codes.Count} codes, states {party.States})");
    foreach (var (state, items) in groups)
    {
        var pool = state == "*"
            ? party.States.Split(' ').SelectMany(s => byState[s]).ToList()
            : byState[state];
        var picked = new Dictionary<string, Shapefile.Record>(); // code -> county
        var viaCity = new List<string>();
        var remaining = new List<(string Code, string CountyPart)>();
        foreach (var item in items)
        {
            if (overrides.TryGetValue($"{party.Id}:{item.Code}", out var wanted))
            {
                if (wanted == "-")
                {
                    continue; // code for a county that no longer exists
                }

                var hit = pool.FirstOrDefault(c => Overrides.Matches(c, wanted));
                if (hit == null)
                {
                    Console.Error.WriteLine($"override {party.Id}:{item.Code} -> '{wanted}' matches no county");
                    problems++;
                    continue;
                }

                picked[item.Code] = hit;
            }
            else if (party.Where == StateInCode.Prefix && prior.TryGetValue((state, item.CountyPart), out var known))
            {
                picked[item.Code] = known; // same county code as in that state's own list
            }
            else
            {
                remaining.Add(item);
            }
        }

        var free = pool.Where(c => !picked.Values.Contains(c)).ToList();
        var cityCodes = remaining.Where(r => r.CountyPart.EndsWith('X') && r.CountyPart.Length >= 2 && pool.Any(p => p.Attributes["STUSPS"] == "VA")).ToList();
        if (cityCodes.Count > 0)
        {
            // Virginia: a code ending in X is an independent city (FXX = Fairfax city); match those among the cities only.
            var cities = free.Where(c => c.Attributes["STUSPS"] == "VA" && c.Attributes["NAMELSAD"].EndsWith(" city", StringComparison.Ordinal)).ToList();
            var cityNames = cities.Select(c => Matcher.Variants(c.Attributes["NAME"], c.Attributes["NAMELSAD"])).ToList();
            var stems = cityCodes.Select(r => r.CountyPart.Substring(0, r.CountyPart.Length - 1)).ToList();
            var cityCost = new int[cityCodes.Count, cities.Count];
            for (var i = 0; i < cityCodes.Count; i++)
            {
                for (var j = 0; j < cities.Count; j++)
                {
                    cityCost[i, j] = Matcher.Cost(stems[i], cityNames[j]);
                }
            }

            var cityAssignment = Matcher.Assign(cityCost, cityCodes.Count, cities.Count);
            for (var i = 0; i < cityCodes.Count; i++)
            {
                var j = cityAssignment[i];
                if (j >= 0 && cityCost[i, j] < Matcher.NoMatch)
                {
                    picked[cityCodes[i].Code] = cities[j];
                    viaCity.Add(cityCodes[i].Code);
                    remaining.Remove(cityCodes[i]);
                    free.Remove(cities[j]);
                }
            }
        }

        var names = free.Select(c => Matcher.Variants(c.Attributes["NAME"], c.Attributes["NAMELSAD"])).ToList();
        var cost = new int[remaining.Count, free.Count];
        for (var i = 0; i < remaining.Count; i++)
        {
            for (var j = 0; j < free.Count; j++)
            {
                cost[i, j] = Matcher.Cost(remaining[i].CountyPart, names[j]);
            }
        }

        var assignment = Matcher.Assign(cost, remaining.Count, free.Count);
        if (state != "*" || party.Where == StateInCode.None)
        {
            SettleByListOrder(remaining, free, cost, assignment, items, pool, review);
        }

        var viaOverride = picked.Keys.Where(k => !viaCity.Contains(k)).ToList();
        foreach (var code in viaCity)
        {
            review.AppendLine($"  c {code,-8} {picked[code].Attributes["NAMELSAD"],-34} VA  (city code)");
        }

        for (var i = 0; i < remaining.Count; i++)
        {
            var j = assignment[i];
            if (j < 0 || cost[i, j] >= Matcher.NoMatch)
            {
                review.AppendLine($"  !! {remaining[i].Code,-8} no county fits");
                problems++;
                continue;
            }

            picked[remaining[i].Code] = free[j];
            var rival = int.MaxValue;
            var rivalName = string.Empty;
            for (var k = 0; k < free.Count; k++)
            {
                if (k != j && cost[i, k] < rival)
                {
                    rival = cost[i, k];
                    rivalName = free[k].Attributes["NAMELSAD"];
                }
            }

            var flag = cost[i, j] > 450 ? "?" : (rival - cost[i, j] < 25 ? "~" : " ");
            review.AppendLine($"  {flag} {remaining[i].Code,-8} {free[j].Attributes["NAMELSAD"],-34} {free[j].Attributes["STUSPS"]}  cost {cost[i, j]}" + (flag == "~" ? $"   (also fits {rivalName}, {rival})" : string.Empty));
        }

        // Pairs of codes whose counties could be swapped at little extra cost: the code letters alone cannot decide those.
        for (var a = 0; a < remaining.Count; a++)
        {
            for (var b = a + 1; b < remaining.Count; b++)
            {
                var ja = assignment[a];
                var jb = assignment[b];
                if (ja < 0 || jb < 0 || cost[a, jb] >= Matcher.NoMatch || cost[b, ja] >= Matcher.NoMatch)
                {
                    continue;
                }

                var extra = cost[a, jb] + cost[b, ja] - cost[a, ja] - cost[b, jb];
                if (extra < 45)
                {
                    review.AppendLine($"  SWAP? {remaining[a].Code} {free[ja].Attributes["NAME"]} / {remaining[b].Code} {free[jb].Attributes["NAME"]}  (swapping costs only {extra} more)");
                }
            }
        }

        foreach (var code in viaOverride)
        {
            var county = picked[code];
            review.AppendLine($"  = {code,-8} {county.Attributes["NAMELSAD"],-34} {county.Attributes["STUSPS"]}  (override)");
        }

        var used = new HashSet<Shapefile.Record>(picked.Values);
        foreach (var c in pool.Where(c => !used.Contains(c)))
        {
            review.AppendLine($"  -- no code for {c.Attributes["NAMELSAD"]}, {c.Attributes["STUSPS"]}");
            problems++;
        }

        // Many N1MM+ lists are in county-name order; if this one is, the position of a code is a second opinion.
        if (picked.Count == items.Count && items.Count == pool.Count)
        {
            var alpha = pool.OrderBy(c => Matcher.Letters(c.Attributes["NAME"]), StringComparer.Ordinal).ToList();
            var agree = 0;
            var disagree = new List<string>();
            for (var i = 0; i < items.Count; i++)
            {
                if (picked.TryGetValue(items[i].Code, out var c) && ReferenceEquals(alpha[i], c))
                {
                    agree++;
                }
                else
                {
                    disagree.Add($"{items[i].Code}->{(picked.TryGetValue(items[i].Code, out var cc) ? cc.Attributes["NAME"] : "?")} (alphabetical #{i + 1} is {alpha[i].Attributes["NAME"]})");
                }
            }

            review.AppendLine($"  ORDER {party.Id} {state}: {agree}/{items.Count} codes sit at their county's alphabetical position" + (agree >= items.Count * 0.9 && disagree.Count > 0 ? "  DIFFERENT: " + string.Join("; ", disagree) : string.Empty));
        }

        if (party.Where == StateInCode.None)
        {
            foreach (var (code, county) in picked)
            {
                prior[(county.Attributes["STUSPS"], code)] = county;
            }
        }

        var multiState = party.States.Contains(' ');
        foreach (var (code, county) in picked)
        {
            var lsad = county.Attributes["NAMELSAD"];
            var label = lsad.EndsWith(" city", StringComparison.Ordinal) ||
                        lsad.EndsWith("Parish", StringComparison.Ordinal) ||
                        lsad.EndsWith("Borough", StringComparison.Ordinal)
                ? lsad
                : county.Attributes["NAME"];
            shapes.Add(new CountyShape(code, multiState ? $"{label}, {county.Attributes["STUSPS"]}" : label, county.Rings));
        }
    }

    shapes.Sort((a, b) => string.CompareOrdinal(a.Abbreviation, b.Abbreviation));
    var file = $"party_{party.Id}.bin";
    using (var output = File.Create(Path.Combine(outDir, file)))
    {
        CountyLocator.WriteCounties(output, shapes);
    }

    manifest.AppendLine($"{party.Id}\t{party.Title}\t{file}\t{party.States}\t{shapes.Count}");
    Console.WriteLine($"{party.Id,-5} {shapes.Count,4} counties  {new FileInfo(Path.Combine(outDir, file)).Length,10:N0} bytes");
}

var fedAt = Array.IndexOf(args, "--fed");
if (fedAt >= 0 && args.Length > fedAt + 2)
{
    // Canadian parties on federal electoral districts: --fed <ED_CF.shp> <cp_names.tsv>
    Canada.Build(args[fedAt + 1], args[fedAt + 2], lists["BC"], outDir, manifest, review);
    problems += Canada.Problems;
}

var censusAt = Array.IndexOf(args, "--census");
if (censusAt >= 0 && args.Length > censusAt + 4)
{
    // Ontario and Atlantic Canada: --census <dir with the GeoJSON boundary files> <on_names.tsv> <ac_names.tsv> <qc_names.tsv>
    Canada.BuildCensusParties(args[censusAt + 1], args[censusAt + 2], args[censusAt + 3], args[censusAt + 4], outDir, manifest, review);
    problems += Canada.Problems;
}

var hawaiiAt = Array.IndexOf(args, "--hawaii");
if (hawaiiAt >= 0 && args.Length > hawaiiAt + 2)
{
    // Hawaii districts are ZIP code lists: --hawaii <cb_*_us_zcta520_500k.shp> <hi_zips.tsv>
    var hawaiiLand = byState["HI"].Select(c => new CountyShape(c.Attributes["NAME"], c.Attributes["NAME"], c.Rings)).ToList();
    Hawaii.Build(args[hawaiiAt + 1], args[hawaiiAt + 2], lists["HI"], hawaiiLand, outDir, manifest, review);
    problems += Hawaii.Problems;
}

File.WriteAllText(Path.Combine(outDir, "parties.tsv"), manifest.ToString());
File.WriteAllText(Path.Combine(outDir, "review.txt"), review.ToString());
Console.WriteLine($"{problems} problem(s); see review.txt");
return problems == 0 ? 0 : 2;

static void SettleByListOrder(
    List<(string Code, string CountyPart)> remaining,
    List<Shapefile.Record> free,
    int[,] cost,
    int[] assignment,
    List<(string Code, string CountyPart)> items,
    List<Shapefile.Record> pool,
    StringBuilder review)
{
    // N1MM+ lists are often in county-name order. Where the list as a whole follows that order (>= 85% of the codes),
    // two codes whose counties could be swapped at little cost are put in the order the list implies.
    if (remaining.Count != items.Count || remaining.Count != pool.Count)
    {
        return;
    }

    string Key(Shapefile.Record c) => Matcher.Letters(c.Attributes["NAME"]);
    var alpha = free.OrderBy(Key, StringComparer.Ordinal).ToList();
    var agree = 0;
    for (var i = 0; i < remaining.Count; i++)
    {
        if (assignment[i] >= 0 && ReferenceEquals(alpha[i], free[assignment[i]]))
        {
            agree++;
        }
    }

    if (agree < remaining.Count * 0.85)
    {
        return;
    }

    for (var a = 0; a < remaining.Count; a++)
    {
        for (var b = a + 1; b < remaining.Count; b++)
        {
            var ja = assignment[a];
            var jb = assignment[b];
            if (ja < 0 || jb < 0 || cost[a, jb] >= Matcher.NoMatch || cost[b, ja] >= Matcher.NoMatch)
            {
                continue;
            }

            var extra = cost[a, jb] + cost[b, ja] - cost[a, ja] - cost[b, jb];
            var inOrder = string.CompareOrdinal(Key(free[ja]), Key(free[jb])) < 0; // a is listed before b
            var swappedInOrder = string.CompareOrdinal(Key(free[jb]), Key(free[ja])) < 0;
            if (extra < 45 && !inOrder && swappedInOrder)
            {
                assignment[a] = jb;
                assignment[b] = ja;
                review.AppendLine($"  (list order: {remaining[a].Code} = {free[jb].Attributes["NAME"]}, {remaining[b].Code} = {free[ja].Attributes["NAME"]})");
            }
        }
    }
}

internal enum StateInCode { None, Prefix, Suffix }

internal sealed record Party(string Id, string Title, string States, StateInCode Where = StateInCode.None, bool StripOwnState = false);

internal static class Overrides
{
    public static Dictionary<string, string> Load(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path.Length == 0 || !File.Exists(path))
        {
            return map;
        }

        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#')
            {
                continue;
            }

            var parts = t.Split('\t');
            map[parts[0]] = parts[1];
        }

        return map;
    }

    /// <summary>The county's NAMELSAD (e.g. "Baltimore city"), optionally followed by ", ST", equals the wanted text.</summary>
    public static bool Matches(Shapefile.Record county, string wanted)
    {
        var lsad = county.Attributes["NAMELSAD"];
        var st = county.Attributes["STUSPS"];
        return string.Equals(wanted, lsad, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(wanted, $"{lsad}, {st}", StringComparison.OrdinalIgnoreCase);
    }
}
