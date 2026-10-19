using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using N1MMMobileCounty.Core;

// Writes a fake NMEA "drive" down the Hudson Valley into New York City, one fix per ~1 km, for practising with
//   N1MMMobileCountyAutomation.exe --replay practice-drive.nmea
// and prints the county sequence the app should show.
//   usage: MakePracticeTrack <output.nmea>
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: MakePracticeTrack <output.nmea>");
    return 1;
}

var waypoints = new (double Lat, double Lon)[]
{
    (42.6526, -73.7562), // Albany
    (42.7284, -73.6918), // Troy
    (42.2529, -73.7910), // Hudson
    (41.7004, -73.9210), // Poughkeepsie
    (41.4301, -73.6807), // Carmel
    (41.0340, -73.7629), // White Plains
    (40.8448, -73.8648), // Bronx
    (40.7580, -73.9855), // Manhattan
};

const double StepMeters = 1000;
var locator = CountyLocator.LoadEmbedded();
var output = new StringBuilder();
var sequence = new List<string>();
var time = new DateTime(2026, 10, 17, 14, 0, 0, DateTimeKind.Utc);

for (var leg = 0; leg < waypoints.Length - 1; leg++)
{
    var (lat0, lon0) = waypoints[leg];
    var (lat1, lon1) = waypoints[leg + 1];
    var meters = Math.Sqrt(Math.Pow((lat1 - lat0) * 111320, 2) + Math.Pow((lon1 - lon0) * 111320 * Math.Cos(lat0 * Math.PI / 180), 2));
    var steps = Math.Max(1, (int)(meters / StepMeters));
    for (var i = 0; i < steps; i++)
    {
        var lat = lat0 + ((lat1 - lat0) * i / steps);
        var lon = lon0 + ((lon1 - lon0) * i / steps);
        output.Append(Sentence($"GPGGA,{time:HHmmss}.00,{Dm(lat, 2, 'N', 'S')},{Dm(lon, 3, 'E', 'W')},1,09,0.9,100.0,M,-34.0,M,,"));
        output.Append(Sentence($"GPRMC,{time:HHmmss}.00,A,{Dm(lat, 2, 'N', 'S')},{Dm(lon, 3, 'E', 'W')},52.0,180.0,{time:ddMMyy},,,A"));
        time = time.AddSeconds(1);
        var abbr = locator.Find(lat, lon)?.Abbreviation ?? "---";
        if (sequence.Count == 0 || sequence[^1] != abbr)
        {
            sequence.Add(abbr);
        }
    }
}

File.WriteAllText(args[0], output.ToString());
Console.WriteLine($"Wrote {args[0]}: {output.ToString().Split('\n').Length / 2} fixes");
Console.WriteLine("Counties along the route: " + string.Join(" > ", sequence));
return 0;

static string Dm(double value, int degreeDigits, char positive, char negative)
{
    var hemisphere = value >= 0 ? positive : negative;
    value = Math.Abs(value);
    var degrees = (int)value;
    var minutes = (value - degrees) * 60;
    return string.Create(CultureInfo.InvariantCulture, $"{degrees.ToString().PadLeft(degreeDigits, '0')}{minutes:00.00000},{hemisphere}");
}

static string Sentence(string body)
{
    var sum = 0;
    foreach (var ch in body)
    {
        sum ^= ch;
    }

    return $"${body}*{sum:X2}\r\n";
}
