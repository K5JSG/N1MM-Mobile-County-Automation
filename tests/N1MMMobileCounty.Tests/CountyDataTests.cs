using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using N1MMMobileCounty.Core;
using Xunit;

namespace N1MMMobileCounty.Tests
{
    /// <summary>QsoParties holds the installed-update folder in a static, so these tests run on their own, one at a time.</summary>
    [CollectionDefinition("county data", DisableParallelization = true)]
    public sealed class CountyDataCollection
    {
    }

    [Collection("county data")]
    public sealed class CountyDataTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "n1mm-county-data-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _release;
        private readonly string _installed;

        public CountyDataTests()
        {
            _release = Path.Combine(_root, "release");
            _installed = Path.Combine(_root, "installed");
            Directory.CreateDirectory(_release);
            QsoParties.UseOverrideFolder(_installed);
        }

        public void Dispose()
        {
            QsoParties.UseOverrideFolder(null);
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>Delaware's data with its codes changed, as a published file.</summary>
        private static (CountyDataEntry Entry, byte[] Bytes) Make(string id, string title, int rev, string codePrefix)
        {
            var counties = QsoParties.Load("DE").Counties
                .Select(c => new CountyShape(codePrefix + c.Abbreviation, c.Name, c.Rings.ToList()))
                .ToList();
            using var stream = new MemoryStream();
            CountyLocator.WriteCounties(stream, counties);
            var bytes = stream.ToArray();
            return (new CountyDataEntry(id, title, "DE", counties.Count, rev, CountyData.Sha256Hex(bytes), bytes.Length, $"party_{id}.bin", "test"), bytes);
        }

        private void Publish(int format, params (CountyDataEntry Entry, byte[] Bytes)[] files)
        {
            foreach (var f in files)
            {
                File.WriteAllBytes(Path.Combine(_release, f.Entry.File), f.Bytes);
            }

            var manifest = new CountyDataManifest { Format = format, Published = "2026-10-19T00:00:00Z", Parties = files.Select(f => f.Entry).ToList() };
            File.WriteAllText(Path.Combine(_release, "manifest.json"), manifest.ToJson());
        }

        private CountyDataUpdater Updater() => new(_release, _installed);

        [Fact]
        public async Task OnlyNewerRevisionsAndNewPartiesAreOffered()
        {
            var newer = Make("DE", "Delaware QSO Party", 2, "X");
            var same = Make("NY", "New York QSO Party", 1, "Y");
            var brandNew = Make("ZZ", "Zed QSO Party", 1, "Z");
            Publish(1, newer, same, brandNew);

            var check = await Updater().CheckAsync(CancellationToken.None);

            Assert.Equal(CountyDataUpdater.CheckStatus.Checked, check.Status);
            Assert.Equal(new[] { "DE", "ZZ" }, check.Updates.Select(u => u.Id).OrderBy(i => i).ToArray());
        }

        [Fact]
        public async Task InstalledDataReplacesTheShippedDataAndAddsNewParties()
        {
            var de = Make("DE", "Delaware QSO Party", 2, "X");
            var zz = Make("ZZ", "Zed QSO Party", 1, "Z");
            Publish(1, de, zz);
            Assert.Equal("KDE", QsoParties.Load("DE").Counties.OrderBy(c => c.Abbreviation).First().Abbreviation);

            var updater = Updater();
            var check = await updater.CheckAsync(CancellationToken.None);
            var result = await updater.InstallAsync(check.Updates, CancellationToken.None);

            Assert.Empty(result.Failed);
            Assert.Equal(2, result.Installed.Count);
            Assert.Contains(QsoParties.Load("DE").Counties, c => c.Abbreviation == "XKDE");
            Assert.Equal(2, QsoParties.ActiveRevision("DE"));
            Assert.Equal(1, QsoParties.EmbeddedRevision("DE"));
            Assert.NotNull(QsoParties.Find("ZZ"));
            Assert.Equal(0, QsoParties.EmbeddedRevision("ZZ"));
            Assert.Contains(QsoParties.Load("ZZ").Counties, c => c.Abbreviation == "ZNDE");

            // once installed there is nothing more to offer, and a second check agrees
            Assert.Empty((await updater.CheckAsync(CancellationToken.None)).Updates);
        }

        [Fact]
        public async Task InstalledDataIsStillThereAfterARestart()
        {
            Publish(1, Make("DE", "Delaware QSO Party", 2, "X"));
            var updater = Updater();
            await updater.InstallAsync((await updater.CheckAsync(CancellationToken.None)).Updates, CancellationToken.None);

            QsoParties.UseOverrideFolder(_installed); // what the program does when it starts

            Assert.Equal(2, QsoParties.ActiveRevision("DE"));
            Assert.Contains(QsoParties.Load("DE").Counties, c => c.Abbreviation == "XSDE");
        }

        [Fact]
        public async Task OnlyTheChosenPartiesAreInstalled()
        {
            Publish(1, Make("DE", "Delaware QSO Party", 2, "X"), Make("ZZ", "Zed QSO Party", 1, "Z"));
            var updater = Updater();
            var check = await updater.CheckAsync(CancellationToken.None);

            var result = await updater.InstallAsync(check.Updates.Where(u => u.Id == "DE"), CancellationToken.None);

            Assert.Equal(new[] { "DE" }, result.Installed.ToArray());
            Assert.Null(QsoParties.Find("ZZ"));
            Assert.Equal(new[] { "ZZ" }, (await updater.CheckAsync(CancellationToken.None)).Updates.Select(u => u.Id).ToArray()); // still offered later
        }

        [Fact]
        public async Task ADamagedDownloadChangesNothing()
        {
            var de = Make("DE", "Delaware QSO Party", 2, "X");
            Publish(1, de);
            var path = Path.Combine(_release, de.Entry.File);
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 0xFF; // same length, wrong content
            File.WriteAllBytes(path, bytes);

            var updater = Updater();
            var result = await updater.InstallAsync((await updater.CheckAsync(CancellationToken.None)).Updates, CancellationToken.None);

            Assert.Empty(result.Installed);
            Assert.Single(result.Failed);
            Assert.Equal(1, QsoParties.ActiveRevision("DE"));
            Assert.Contains(QsoParties.Load("DE").Counties, c => c.Abbreviation == "KDE");
        }

        [Fact]
        public void AnInstalledFileThatLaterGetsDamagedIsIgnored()
        {
            var de = Make("DE", "Delaware QSO Party", 2, "X");
            Directory.CreateDirectory(_installed);
            File.WriteAllBytes(Path.Combine(_installed, de.Entry.File), new byte[] { 1, 2, 3, 4 });
            File.WriteAllText(Path.Combine(_installed, QsoParties.OverrideManifestName), new CountyDataManifest { Parties = { de.Entry } }.ToJson());

            QsoParties.Reload();

            Assert.Equal(1, QsoParties.ActiveRevision("DE")); // back on the shipped data
            Assert.Contains(QsoParties.Load("DE").Counties, c => c.Abbreviation == "KDE");
        }

        [Fact]
        public async Task ANewerFileFormatAsksForANewerProgram()
        {
            Publish(CountyDataManifest.SupportedFormat + 1, Make("DE", "Delaware QSO Party", 2, "X"));

            var check = await Updater().CheckAsync(CancellationToken.None);

            Assert.Equal(CountyDataUpdater.CheckStatus.NeedsNewerProgram, check.Status);
            Assert.Empty(check.Updates);
        }

        [Fact]
        public async Task NoUpdateListMeansNothingHappens()
        {
            var check = await Updater().CheckAsync(CancellationToken.None); // no manifest.json in the folder

            Assert.Equal(CountyDataUpdater.CheckStatus.Failed, check.Status);
            Assert.Empty(check.Updates);
        }

        [Fact]
        public async Task UnsafeFileNamesAreNeverOffered()
        {
            var de = Make("DE", "Delaware QSO Party", 2, "X");
            var evil = de.Entry with { File = "..\\..\\evil.bin" };
            File.WriteAllText(Path.Combine(_release, "manifest.json"), new CountyDataManifest { Parties = { evil } }.ToJson());

            Assert.Empty((await Updater().CheckAsync(CancellationToken.None)).Updates);
        }

        [Fact]
        public async Task RemovingInstalledDataGoesBackToTheShippedData()
        {
            Publish(1, Make("DE", "Delaware QSO Party", 2, "X"));
            var updater = Updater();
            await updater.InstallAsync((await updater.CheckAsync(CancellationToken.None)).Updates, CancellationToken.None);

            updater.RemoveInstalled();

            Assert.Equal(1, QsoParties.ActiveRevision("DE"));
            Assert.Contains(QsoParties.Load("DE").Counties, c => c.Abbreviation == "KDE");
        }

        [Fact]
        public void ShippedDataSaysItsRevision()
        {
            Assert.All(QsoParties.All, p => Assert.Equal(1, QsoParties.EmbeddedRevision(p.Id)));
        }
    }
}
