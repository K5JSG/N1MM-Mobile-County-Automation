using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace N1MMMobileCounty.Core
{
    /// <summary>One party's county data as published in a county-data release (and as installed on a PC).</summary>
    /// <param name="Id">The N1MM+ SubType name of the party (NY, TX, HI ...).</param>
    /// <param name="Title">Display name, e.g. "Hawaii QSO Party".</param>
    /// <param name="Area">Postal codes of the states or provinces the party's counties are in.</param>
    /// <param name="Count">Number of counties (districts) in the file.</param>
    /// <param name="Rev">Revision of this party's data: 1 for the data the program shipped with, +1 for every republish.</param>
    /// <param name="Sha256">Lower-case hex SHA-256 of the file.</param>
    /// <param name="Size">File length in bytes.</param>
    /// <param name="File">File name, party_ID.bin.</param>
    /// <param name="Note">What changed (shown to the user), e.g. "code ABC added".</param>
    public sealed record CountyDataEntry(string Id, string Title, string Area, int Count, int Rev, string Sha256, long Size, string File, string Note = "");

    /// <summary>
    /// The list of party data files in a county-data release (manifest.json), or of the data files a PC has installed.
    /// <see cref="Format"/> changes only when the file layout does; a program that does not know a newer format leaves the data alone.
    /// </summary>
    public sealed class CountyDataManifest
    {
        public const int SupportedFormat = 1;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public int Format { get; set; } = SupportedFormat;

        /// <summary>UTC time the manifest was written, ISO 8601.</summary>
        public string Published { get; set; } = string.Empty;

        public List<CountyDataEntry> Parties { get; set; } = new();

        public static CountyDataManifest Parse(string json)
        {
            var manifest = JsonSerializer.Deserialize<CountyDataManifest>(json, Options)
                ?? throw new InvalidDataException("Empty county data manifest.");
            manifest.Parties ??= new List<CountyDataEntry>();
            return manifest;
        }

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        public CountyDataEntry? Find(string id)
        {
            return Parties.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Checking and installing downloaded county data files (shared by the program and the server tool).</summary>
    public static class CountyData
    {
        public static string Sha256Hex(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        /// <summary>
        /// Why a downloaded file cannot be used, or null when it is good: right length and checksum, readable, the right number of
        /// counties, every county code unique and non-empty.
        /// </summary>
        public static string? Validate(CountyDataEntry entry, byte[] bytes)
        {
            if (!SafeFileName(entry.File))
            {
                return $"unsafe file name '{entry.File}'";
            }

            if (bytes.LongLength != entry.Size)
            {
                return $"{entry.File}: {bytes.LongLength} bytes, expected {entry.Size}";
            }

            if (!Sha256Hex(bytes).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"{entry.File}: checksum does not match";
            }

            List<CountyShape> counties;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                counties = CountyLocator.ReadCounties(stream);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
            {
                return $"{entry.File}: not a county data file ({ex.Message})";
            }

            if (counties.Count != entry.Count)
            {
                return $"{entry.File}: {counties.Count} counties, expected {entry.Count}";
            }

            var codes = counties.Select(c => c.Abbreviation).ToList();
            if (codes.Any(string.IsNullOrWhiteSpace) || codes.Count != codes.Distinct(StringComparer.Ordinal).Count())
            {
                return $"{entry.File}: empty or repeated county codes";
            }

            return null;
        }

        /// <summary>A plain "party_XX.bin" style name: no folders, no odd characters.</summary>
        public static bool SafeFileName(string name)
        {
            return name.Length is > 0 and < 64 &&
                   name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.') &&
                   !name.Contains("..", StringComparison.Ordinal);
        }
    }
}
