using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// Which contest N1MM+ has open, read from its settings file (N1MM Logger.ini): the most recent contest line looks like
    /// <c>RecentContest 1=QSOPARTY|2026-10-17 14:00:00|C:\...\W2R.s3db|1|NY</c>, i.e. contest type, start, log file, id and
    /// (for QSO parties) the party's SubType, which is the same name this app uses for its parties (NY, TX, 7QP...).
    /// </summary>
    internal static class N1mmContest
    {
        private static string? _foundIni;

        /// <summary>The contest type (QSOPARTY, DX, POTA...) and, for a QSO party, its SubType; null when it cannot be read.</summary>
        public static (string Type, string SubType)? ReadCurrent(string configuredIni)
        {
            var ini = FindIni(configuredIni);
            if (ini == null)
            {
                return null;
            }

            try
            {
                // N1MM+ has the file open and rewrites it; share politely.
                using var stream = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("RecentContest 1=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fields = line.Substring(line.IndexOf('=') + 1).Split('|');
                    if (fields.Length == 0 || fields[0].Length == 0)
                    {
                        return null;
                    }

                    return (fields[0].Trim(), fields.Length >= 5 ? fields[4].Trim() : string.Empty);
                }
            }
            catch (IOException)
            {
                // being rewritten right now: try again on the next check
            }
            catch (UnauthorizedAccessException)
            {
            }

            return null;
        }

        /// <summary>The settings file: the configured path, else the newest "N1MM Logger.ini" in the usual folders under Documents.</summary>
        public static string? FindIni(string configuredIni)
        {
            if (!string.IsNullOrWhiteSpace(configuredIni))
            {
                return File.Exists(configuredIni) ? configuredIni : null;
            }

            if (_foundIni != null && File.Exists(_foundIni))
            {
                return _foundIni;
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var candidates = new List<string> { Path.Combine(documents, "N1MM Logger+") };
            try
            {
                candidates.AddRange(Directory.EnumerateDirectories(documents).Select(d => Path.Combine(d, "N1MM Logger+")));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            _foundIni = candidates
                .Select(c => Path.Combine(c, "N1MM Logger.ini"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            return _foundIni;
        }
    }
}
