using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace N1MMMobileCounty.Core
{
    /// <summary>One QSO party whose county boundaries and county codes are embedded (or installed from a county-data update).</summary>
    /// <param name="Id">The N1MM+ SubType name of the party (NY, TX, 7QP, ...).</param>
    /// <param name="Title">Display name, e.g. "New York QSO Party".</param>
    /// <param name="Resource">Name of the embedded boundary file (empty for a party that exists only as an installed update).</param>
    /// <param name="Area">Postal codes of the states (or provinces) the party's counties are in.</param>
    /// <param name="CountyCount">How many counties the party has.</param>
    /// <param name="Rev">Revision of the data in use: 1 for the data the program shipped with.</param>
    public sealed record QsoPartyInfo(string Id, string Title, string Resource, string Area, int CountyCount, int Rev = 1);

    /// <summary>
    /// The QSO parties the app knows. For each, the embedded file holds the boundary of every county in the party's area,
    /// labelled with the county code N1MM+ accepts for that party (so "ALB" for NYQP, "ALAM" for CQP, "IDADA" for the 7QP...).
    /// A county-data update the user installed (files in an "override folder", see <see cref="UseOverrideFolder"/>) takes the place of
    /// a party's embedded file, and may add a party the program shipped without.
    /// </summary>
    public static class QsoParties
    {
        public const string OverrideManifestName = "manifest.json";

        private sealed class State
        {
            public List<QsoPartyInfo> All = new();
            public Dictionary<string, string> OverridePaths = new(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly object Gate = new();
        private static readonly Lazy<Dictionary<string, int>> EmbeddedRevisions = new(ReadEmbeddedRevisions);
        private static string? _overrideFolder;
        private static State? _state;

        public static IReadOnlyList<QsoPartyInfo> All => Current().All;

        /// <summary>The folder holding installed county-data updates (manifest.json and the party files), or null.</summary>
        public static string? OverrideFolder => _overrideFolder;

        /// <summary>Points the program at the folder of installed county-data updates and re-reads the party list.</summary>
        public static void UseOverrideFolder(string? folder)
        {
            lock (Gate)
            {
                _overrideFolder = folder;
                _state = null;
            }
        }

        /// <summary>Re-reads the installed updates (after one was installed or removed).</summary>
        public static void Reload()
        {
            lock (Gate)
            {
                _state = null;
            }
        }

        public static QsoPartyInfo? Find(string? id)
        {
            return string.IsNullOrWhiteSpace(id)
                ? null
                : All.FirstOrDefault(p => p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The revision of the data the program shipped with for this party (1 when it did not say; 0 when it has none).</summary>
        public static int EmbeddedRevision(string id)
        {
            if (EmbeddedParties().All(p => !p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                return 0;
            }

            return EmbeddedRevisions.Value.TryGetValue(id, out var rev) ? rev : 1;
        }

        /// <summary>The revision of the data in use for this party now (installed update or embedded); 0 when the party is unknown.</summary>
        public static int ActiveRevision(string id) => Find(id)?.Rev ?? 0;

        public static CountyLocator Load(string id)
        {
            var info = Find(id) ?? throw new InvalidOperationException($"Unknown QSO party '{id}'.");
            if (Current().OverridePaths.TryGetValue(info.Id, out var path))
            {
                using var file = File.OpenRead(path);
                return new CountyLocator(CountyLocator.ReadCounties(file));
            }

            using var stream = typeof(QsoParties).Assembly.GetManifestResourceStream(info.Resource)
                ?? throw new InvalidOperationException($"Embedded county data ({info.Resource}) is missing.");
            return new CountyLocator(CountyLocator.ReadCounties(stream));
        }

        private static State Current()
        {
            lock (Gate)
            {
                return _state ??= Build(_overrideFolder);
            }
        }

        private static State Build(string? folder)
        {
            var state = new State();
            var revisions = EmbeddedRevisions.Value;
            foreach (var p in EmbeddedParties())
            {
                state.All.Add(p with { Rev = revisions.TryGetValue(p.Id, out var rev) ? rev : 1 });
            }

            if (string.IsNullOrEmpty(folder))
            {
                return state;
            }

            try
            {
                var manifestPath = Path.Combine(folder, OverrideManifestName);
                if (!File.Exists(manifestPath))
                {
                    return state;
                }

                var manifest = CountyDataManifest.Parse(File.ReadAllText(manifestPath));
                if (manifest.Format > CountyDataManifest.SupportedFormat)
                {
                    return state;
                }

                foreach (var entry in manifest.Parties)
                {
                    var existing = state.All.FindIndex(p => p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0 && state.All[existing].Rev >= entry.Rev)
                    {
                        continue; // the program itself now ships data at least this new
                    }

                    var path = CountyData.SafeFileName(entry.File) ? Path.Combine(folder, entry.File) : string.Empty;
                    if (path.Length == 0 || !File.Exists(path) || CountyData.Validate(entry, File.ReadAllBytes(path)) != null)
                    {
                        continue; // damaged or missing: keep what the program shipped with
                    }

                    var info = new QsoPartyInfo(entry.Id, entry.Title, existing >= 0 ? state.All[existing].Resource : string.Empty, entry.Area, entry.Count, entry.Rev);
                    if (existing >= 0)
                    {
                        state.All[existing] = info;
                    }
                    else
                    {
                        state.All.Add(info);
                    }

                    state.OverridePaths[entry.Id] = path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                // an unreadable update folder is never a reason to stop: the shipped data is still there
            }

            return state;
        }

        private static List<QsoPartyInfo> EmbeddedParties()
        {
            using var stream = typeof(QsoParties).Assembly.GetManifestResourceStream("parties.tsv")
                ?? throw new InvalidOperationException("Embedded QSO party list (parties.tsv) is missing.");
            using var reader = new StreamReader(stream);
            var list = new List<QsoPartyInfo>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var f = line.Split('\t');
                if (f.Length >= 5 && int.TryParse(f[4], out var count))
                {
                    list.Add(new QsoPartyInfo(f[0], f[1], f[2], f[3], count));
                }
            }

            return list;
        }

        /// <summary>data-rev.tsv: "id TAB revision" for each party, written when the shipped data is built.</summary>
        private static Dictionary<string, int> ReadEmbeddedRevisions()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var stream = typeof(QsoParties).Assembly.GetManifestResourceStream("data-rev.tsv");
            if (stream == null)
            {
                return map;
            }

            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var f = line.Split('\t');
                if (f.Length >= 2 && int.TryParse(f[1], out var rev))
                {
                    map[f[0]] = rev;
                }
            }

            return map;
        }
    }
}
