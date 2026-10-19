using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace N1MMMobileCounty.Core
{
    /// <summary>
    /// County-data updates: the program ships with county data for every QSO party, and a release called "county-data" on the
    /// program's GitHub page may carry newer files (manifest.json plus one party_XX.bin per party). Checking needs no login.
    /// Downloaded files are checked (length, checksum, readable, right county count) before they replace anything, and are kept in
    /// the program's data folder, so no administrator rights are needed and the shipped data stays as the fallback.
    /// </summary>
    public sealed class CountyDataUpdater
    {
        public const string DefaultSource = "https://github.com/K5JSG/N1MM-Mobile-County-Automation/releases/download/county-data";

        private const long MaxFileBytes = 25L * 1024 * 1024;

        private static readonly HttpClient Http = CreateClient();

        private readonly string _source;
        private readonly string _folder;

        /// <summary>Receives notes about anything that went wrong quietly (optional).</summary>
        public Action<string>? Log { get; set; }

        /// <param name="source">An https address, or a folder (for testing), that holds manifest.json and the party files; empty = the default.</param>
        /// <param name="folder">Where installed updates are kept.</param>
        public CountyDataUpdater(string source, string folder)
        {
            _source = string.IsNullOrWhiteSpace(source) ? DefaultSource : source.Trim().TrimEnd('/', '\\');
            _folder = folder;
        }

        public enum CheckStatus
        {
            Checked,
            Failed,
            NeedsNewerProgram,
        }

        public sealed record CheckResult(CheckStatus Status, IReadOnlyList<CountyDataEntry> Updates, string Message);

        public sealed record InstallResult(IReadOnlyList<string> Installed, IReadOnlyList<string> Failed);

        /// <summary>Reads the published list and returns the parties whose published data is newer than what is in use.</summary>
        public async Task<CheckResult> CheckAsync(CancellationToken ct)
        {
            CountyDataManifest manifest;
            try
            {
                manifest = CountyDataManifest.Parse(Encoding.UTF8.GetString(await ReadAsync("manifest.json", 2 * 1024 * 1024, ct)));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                return new CheckResult(CheckStatus.Failed, Array.Empty<CountyDataEntry>(), ex.Message);
            }

            if (manifest.Format > CountyDataManifest.SupportedFormat)
            {
                return new CheckResult(CheckStatus.NeedsNewerProgram, Array.Empty<CountyDataEntry>(), $"county data format {manifest.Format}");
            }

            var updates = manifest.Parties
                .Where(p => CountyData.SafeFileName(p.File) && p.Id.Length > 0 && p.Rev > QsoParties.ActiveRevision(p.Id))
                .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new CheckResult(CheckStatus.Checked, updates, string.Empty);
        }

        /// <summary>Downloads, checks and installs the chosen parties' data. Parties that fail are left as they were.</summary>
        public async Task<InstallResult> InstallAsync(IEnumerable<CountyDataEntry> entries, CancellationToken ct)
        {
            var installed = new List<string>();
            var failed = new List<string>();
            Directory.CreateDirectory(_folder);
            var local = ReadLocalManifest();
            foreach (var entry in entries)
            {
                try
                {
                    if (entry.Size > MaxFileBytes)
                    {
                        failed.Add($"{entry.Id}: file too large");
                        continue;
                    }

                    var bytes = await ReadAsync(entry.File, MaxFileBytes, ct);
                    var problem = CountyData.Validate(entry, bytes);
                    if (problem != null)
                    {
                        failed.Add($"{entry.Id}: {problem}");
                        continue;
                    }

                    var target = Path.Combine(_folder, entry.File);
                    var temp = target + ".new";
                    await File.WriteAllBytesAsync(temp, bytes, ct);
                    File.Move(temp, target, true);
                    local.Parties.RemoveAll(p => p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
                    local.Parties.Add(entry);
                    WriteLocalManifest(local); // after every party, so a stop part-way keeps what was finished
                    installed.Add(entry.Id);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }

                    failed.Add($"{entry.Id}: {ex.Message}");
                }
            }

            QsoParties.Reload();
            return new InstallResult(installed, failed);
        }

        /// <summary>Removes every installed update, so the program uses the data it shipped with again.</summary>
        public void RemoveInstalled()
        {
            try
            {
                if (Directory.Exists(_folder))
                {
                    Directory.Delete(_folder, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log?.Invoke($"could not remove installed county data: {ex.Message}");
            }

            QsoParties.Reload();
        }

        private CountyDataManifest ReadLocalManifest()
        {
            try
            {
                var path = Path.Combine(_folder, QsoParties.OverrideManifestName);
                if (File.Exists(path))
                {
                    var manifest = CountyDataManifest.Parse(File.ReadAllText(path));
                    if (manifest.Format <= CountyDataManifest.SupportedFormat)
                    {
                        return manifest;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                // start a fresh list; the files themselves are checked again whenever they are loaded
            }

            return new CountyDataManifest();
        }

        private void WriteLocalManifest(CountyDataManifest manifest)
        {
            manifest.Published = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var path = Path.Combine(_folder, QsoParties.OverrideManifestName);
            File.WriteAllText(path + ".new", manifest.ToJson());
            File.Move(path + ".new", path, true);
        }

        private async Task<byte[]> ReadAsync(string name, long maxBytes, CancellationToken ct)
        {
            if (_source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || _source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var response = await Http.GetAsync($"{_source}/{name}", HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new IOException($"{name} is larger than expected");
                }

                return await response.Content.ReadAsByteArrayAsync(ct);
            }

            var path = Path.Combine(_source, name);
            if (new FileInfo(path).Length > maxBytes)
            {
                throw new IOException($"{name} is larger than expected");
            }

            return await File.ReadAllBytesAsync(path, ct);
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("N1MMMobileCountyAutomation");
            return client;
        }
    }
}
