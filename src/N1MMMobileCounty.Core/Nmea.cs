using System;
using System.Globalization;

namespace N1MMMobileCounty.Core
{
    /// <summary>A position report decoded from NMEA 0183 RMC / GGA sentences.</summary>
    public sealed class GpsFix
    {
        public GpsFix(double latitude, double longitude, double speedKnots, int satellites)
        {
            Latitude = latitude;
            Longitude = longitude;
            SpeedKnots = speedKnots;
            Satellites = satellites;
        }

        public double Latitude { get; }

        public double Longitude { get; }

        public double SpeedKnots { get; }

        /// <summary>Satellites in use, or -1 when the sentence did not carry it.</summary>
        public int Satellites { get; }
    }

    public static class Nmea
    {
        /// <summary>True when the sentence's *hh checksum matches. Sentences without a checksum are rejected.</summary>
        public static bool ChecksumValid(string sentence)
        {
            var star = sentence.LastIndexOf('*');
            if (sentence.Length < 4 || sentence[0] != '$' || star < 0 || star + 3 > sentence.Length)
            {
                return false;
            }

            var sum = 0;
            for (var i = 1; i < star; i++)
            {
                sum ^= sentence[i];
            }

            return int.TryParse(sentence.Substring(star + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var given)
                && given == sum;
        }

        /// <summary>
        /// Decodes an RMC or GGA sentence from any talker (GP, GN, GL...). Returns null for other sentences,
        /// bad checksums, or reports that say there is no valid fix.
        /// </summary>
        public static GpsFix? ParseFix(string sentence)
        {
            sentence = sentence.Trim();
            if (!ChecksumValid(sentence))
            {
                return null;
            }

            var body = sentence.Substring(1, sentence.LastIndexOf('*') - 1);
            var f = body.Split(',');
            if (f[0].Length < 5)
            {
                return null;
            }

            var type = f[0].Substring(f[0].Length - 3);
            if (type == "RMC" && f.Length >= 8 && f[2] == "A")
            {
                return Build(f[3], f[4], f[5], f[6], ParseDouble(f[7]), -1);
            }

            if (type == "GGA" && f.Length >= 8 && int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality) && quality > 0)
            {
                int.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sats);
                return Build(f[2], f[3], f[4], f[5], 0, sats);
            }

            return null;
        }

        private static GpsFix? Build(string lat, string ns, string lon, string ew, double speed, int sats)
        {
            var latitude = ParseCoordinate(lat, 2);
            var longitude = ParseCoordinate(lon, 3);
            if (latitude == null || longitude == null || ns.Length == 0 || ew.Length == 0)
            {
                return null;
            }

            var la = ns == "S" ? -latitude.Value : latitude.Value;
            var lo = ew == "W" ? -longitude.Value : longitude.Value;
            return new GpsFix(la, lo, speed, sats);
        }

        /// <summary>Converts NMEA ddmm.mmmm / dddmm.mmmm to decimal degrees.</summary>
        private static double? ParseCoordinate(string value, int degreeDigits)
        {
            if (value.Length <= degreeDigits + 1 ||
                !double.TryParse(value.Substring(0, degreeDigits), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) ||
                !double.TryParse(value.Substring(degreeDigits), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
            {
                return null;
            }

            return degrees + (minutes / 60.0);
        }

        private static double ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
    }
}
