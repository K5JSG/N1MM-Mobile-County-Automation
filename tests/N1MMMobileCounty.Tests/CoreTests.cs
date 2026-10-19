using N1MMMobileCounty.Core;
using Xunit;

namespace N1MMMobileCounty.Tests
{
    public class CountyLocatorTests
    {
        private static readonly CountyLocator Locator = CountyLocator.LoadEmbedded();

        [Fact]
        public void EmbeddedDataHasAll62Counties()
        {
            Assert.Equal(62, Locator.CountyCount);
            Assert.Equal(NyqpCounties.Count, Locator.CountyCount);
        }

        [Theory]
        [InlineData(42.6526, -73.7562, "ALB")]  // Albany
        [InlineData(40.7580, -73.9855, "NEW")]  // Times Square, New York County
        [InlineData(40.8448, -73.8648, "BRX")]  // Bronx
        [InlineData(40.6501, -73.9496, "KIN")]  // Brooklyn
        [InlineData(40.7282, -73.7949, "QUE")]  // Queens
        [InlineData(40.5795, -74.1502, "RIC")]  // Staten Island
        [InlineData(43.1566, -77.6088, "MON")]  // Rochester
        [InlineData(42.8864, -78.8784, "ERI")]  // Buffalo
        [InlineData(43.0481, -76.1474, "ONO")]  // Syracuse
        [InlineData(44.6995, -73.4529, "CLI")]  // Plattsburgh
        [InlineData(42.4440, -76.5019, "TOM")]  // Ithaca
        [InlineData(44.2795, -73.9799, "ESS")]  // Lake Placid, Essex County
        [InlineData(40.9115, -72.4600, "SUF")]  // Long Island east
        [InlineData(41.1220, -73.7949, "WES")]  // Westchester
        [InlineData(43.31549, -77.93796, "MON")] // live GPS fix 2026-10-10 (N1MM+ already showed MON)
        public void FindsTheRightCounty(double lat, double lon, string expected)
        {
            Assert.Equal(expected, Locator.Find(lat, lon)?.Abbreviation);
        }

        [Fact]
        public void FindNearbyOffersTheCountyAcrossTheLine()
        {
            // Roosevelt Hwy on the Monroe/Orleans line (live drive 2026-10-11): ORL fix, MON a few hundred m south.
            var near = Locator.FindNearby(43.26435, -77.99637, "ORL", 3000, 3);
            Assert.Contains(near, c => c.Abbreviation == "MON");
            Assert.DoesNotContain(near, c => c.Abbreviation == "ORL");
        }

        [Fact]
        public void FindNearbyIsEmptyFarFromAnyBoundary()
        {
            Assert.Empty(Locator.FindNearby(43.1566, -77.6088, "MON", 3000, 3)); // downtown Rochester
        }

        [Theory]
        [InlineData(39.9526, -75.1652)]  // Philadelphia, PA
        [InlineData(42.3601, -71.0589)]  // Boston, MA
        [InlineData(44.5, -80.0)]        // Georgian Bay, Ontario
        public void ReturnsNullOutsideNewYork(double lat, double lon)
        {
            Assert.Null(Locator.Find(lat, lon));
        }
    }

    public class NmeaTests
    {
        [Fact]
        public void ParsesRmcFix()
        {
            const string sentence = "$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A";
            var fix = Nmea.ParseFix(sentence);
            Assert.NotNull(fix);
            Assert.Equal(48.1173, fix!.Latitude, 4);
            Assert.Equal(11.516667, fix.Longitude, 5);
            Assert.Equal(22.4, fix.SpeedKnots, 1);
        }

        [Fact]
        public void ParsesGgaWithSatellitesAndWestSouth()
        {
            const string body = "GPGGA,123519,4807.038,S,01131.000,W,1,08,0.9,545.4,M,46.9,M,,";
            var fix = Nmea.ParseFix($"${body}*{Checksum(body)}");
            Assert.NotNull(fix);
            Assert.Equal(-48.1173, fix!.Latitude, 4);
            Assert.Equal(-11.516667, fix.Longitude, 5);
            Assert.Equal(8, fix.Satellites);
        }

        [Fact]
        public void RejectsVoidRmcBadChecksumAndNoFixGga()
        {
            Assert.Null(Nmea.ParseFix("$GPRMC,123519,V,,,,,,,230394,,*" + Checksum("GPRMC,123519,V,,,,,,,230394,,")));
            Assert.Null(Nmea.ParseFix("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*00"));
            Assert.Null(Nmea.ParseFix("$GPGGA,,,,,,0,13,28,31,,,,,,,,,1.6,1.3,0.9*36"));
        }

        [Fact]
        public void AcceptsAnyTalker()
        {
            const string body = "GNRMC,123519,A,4237.038,N,07345.000,W,5.0,84.4,230394,,";
            var fix = Nmea.ParseFix($"${body}*{Checksum(body)}");
            Assert.NotNull(fix);
            Assert.Equal(-73.75, fix!.Longitude, 4);
        }

        private static string Checksum(string body)
        {
            var sum = 0;
            foreach (var ch in body)
            {
                sum ^= ch;
            }

            return sum.ToString("X2");
        }
    }

    public class CountyTrackerTests
    {
        [Fact]
        public void FirstCountyNeedsConfirmationThenCommits()
        {
            var tracker = new CountyTracker(3);
            Assert.False(tracker.Update("ALB"));
            Assert.False(tracker.Update("ALB"));
            Assert.True(tracker.Update("ALB"));
            Assert.Equal("ALB", tracker.Current);
            Assert.False(tracker.Update("ALB"));
        }

        [Fact]
        public void JitterAcrossCountyLineDoesNotChangeCounty()
        {
            var tracker = new CountyTracker(3);
            for (var i = 0; i < 3; i++)
            {
                tracker.Update("ALB");
            }

            Assert.False(tracker.Update("REN"));
            Assert.False(tracker.Update("ALB"));
            Assert.False(tracker.Update("REN"));
            Assert.False(tracker.Update("REN"));
            Assert.False(tracker.Update("ALB"));
            Assert.Equal("ALB", tracker.Current);
        }

        [Fact]
        public void SustainedNewCountyCommits()
        {
            var tracker = new CountyTracker(3);
            for (var i = 0; i < 3; i++)
            {
                tracker.Update("ALB");
            }

            Assert.False(tracker.Update("REN"));
            Assert.False(tracker.Update("REN"));
            Assert.True(tracker.Update("REN"));
            Assert.Equal("REN", tracker.Current);
        }

        [Fact]
        public void OutsideNewYorkIsAnEmptyCounty()
        {
            var tracker = new CountyTracker(2);
            tracker.Update("ALB");
            tracker.Update("ALB");
            tracker.Update(null);
            Assert.True(tracker.Update(null));
            Assert.Equal(string.Empty, tracker.Current);
        }
    }
}

namespace N1MMMobileCounty.Tests
{
    public class PracticeDriveTests
    {
        [Fact]
        public void PracticeDriveProducesTheExpectedCountySwitches()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "practice", "practice-drive.nmea")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            var locator = CountyLocator.LoadEmbedded();
            var tracker = new CountyTracker(3);
            var switches = new System.Collections.Generic.List<string>();
            foreach (var line in System.IO.File.ReadLines(System.IO.Path.Combine(dir!.FullName, "practice", "practice-drive.nmea")))
            {
                var fix = Nmea.ParseFix(line);
                if (fix != null && tracker.Update(locator.Find(fix.Latitude, fix.Longitude)?.Abbreviation))
                {
                    switches.Add(tracker.Current!);
                }
            }

            Assert.Equal(new[] { "ALB", "REN", "COL", "GRE", "COL", "DUT", "PUT", "WES", "BRX", "NEW" }, switches);
        }
    }
}

namespace N1MMMobileCounty.Tests
{
    public class VoiceMessageFileTests
    {
        private const string Template = "F2 Exch,{TTSParms 0.8}{TTS Five nine, {ICAO {ROVERQTH}}}\r\nF5 His Call,{TTSTrim}{TTS{ICAO !}}\r\n";

        [Fact]
        public void SetsASingleCounty()
        {
            var result = VoiceMessageFile.Apply(Template, "MON");
            Assert.Equal("F2 Exch,{TTSParms 0.8}{TTS Five nine, {ICAO MON}}\r\nF5 His Call,{TTSTrim}{TTS{ICAO !}}\r\n", result);
        }

        [Fact]
        public void SetsACountyLineAsTwoCounties()
        {
            var result = VoiceMessageFile.Apply(Template, "ALL/CAT");
            Assert.Contains("{TTS Five nine, {ICAO ALL}, and {ICAO CAT}}", result);
        }

        [Fact]
        public void CanBeAppliedRepeatedlyAndBackToASingleCounty()
        {
            var line = VoiceMessageFile.Apply(VoiceMessageFile.Apply(Template, "ALL/CAT"), "ALL");
            Assert.Contains("{TTS Five nine, {ICAO ALL}}", line);
            Assert.DoesNotContain("CAT", line);
            Assert.Equal(VoiceMessageFile.Apply(Template, "ORL"), VoiceMessageFile.Apply(VoiceMessageFile.Apply(Template, "MON"), "ORL"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("mon")]
        [InlineData("ALL/CAT/MON")]
        [InlineData("AL/CAT")]
        public void LeavesTheFileAloneForAnInvalidCounty(string qth)
        {
            Assert.Equal(Template, VoiceMessageFile.Apply(Template, qth));
        }
    }
}

namespace N1MMMobileCounty.Tests
{
    public class VoiceMessageFileBareTokenTests
    {
        [Theory]
        [InlineData("F2 Exch,{TTSParms 0.8}{TTS Five nine, {ICAO ROVERQTH}}")]
        [InlineData("F2 Exch,{TTSParms 0.8}{TTS Five nine, {ICAO {ROVERQTH}}}")]
        public void ReplacesBothSpellingsOfTheRoverQthToken(string line)
        {
            Assert.Equal("F2 Exch,{TTSParms 0.8}{TTS Five nine, {ICAO ALL}, and {ICAO CAT}}", VoiceMessageFile.Apply(line, "ALL/CAT"));
        }
    }
}

namespace N1MMMobileCounty.Tests
{
    public class VoiceMessageFileCommentTests
    {
        [Fact]
        public void LeavesCommentLinesAloneAndFixesTheUsersPastedFile()
        {
            const string text = "# F2 speaks the county ({ICAO {ROVERQTH}}), try {ICAO ROVERQTH}\r\nF2 Exch,{TTS Five nine, {ICAO ROVERQTH}}\r\nF6 County,{TTSParms 0.8}{TTS {ICAO ROVERQTH}}\r\nF7 Rpt Exch,{TTS Five nine, {ICAO {ROVERQTH}}}\r\n";
            var result = VoiceMessageFile.Apply(text, "ALL/CAT");
            Assert.StartsWith("# F2 speaks the county ({ICAO {ROVERQTH}}), try {ICAO ROVERQTH}\r\n", result);
            Assert.Contains("F2 Exch,{TTS Five nine, {ICAO ALL}, and {ICAO CAT}}\r\n", result);
            Assert.Contains("F6 County,{TTSParms 0.8}{TTS {ICAO ALL}, and {ICAO CAT}}\r\n", result);
            Assert.Contains("F7 Rpt Exch,{TTS Five nine, {ICAO ALL}, and {ICAO CAT}}\r\n", result);
        }
    }
}

namespace N1MMMobileCounty.Tests
{
    public class VoiceMessageFileCaseTests
    {
        [Fact]
        public void PlaceholderIsMatchedAnyCaseButCountyCodesMustBeUpperCase()
        {
            Assert.Equal("F7 x,{TTS Five nine, {ICAO MON}}}", VoiceMessageFile.Apply("F7 x,{TTS Five nine, {ICAO ROVERQTh}}}", "MON"));
            Assert.Equal("{ICAO mon}", VoiceMessageFile.Apply("{ICAO mon}", "ORL"));
        }
    }
}

namespace N1MMMobileCounty.Tests
{
    public class VoiceMessageFileLabelTests
    {
        private const string Pasted = "F6 ROVERQTH,{TTSParms 0.8}{TTS {ICAO ROVERQTH}}\r\nF9 QSL,{TTSParms 0.8}{TTS Q S L, thank you}\r\nF7 QRZ?,{TTS Q R Zed}\r\n";

        [Fact]
        public void ReplacesTheButtonLabelToo()
        {
            var result = VoiceMessageFile.Apply(Pasted, "MON");
            Assert.Contains("F6 MON,{TTSParms 0.8}{TTS {ICAO MON}}\r\n", result);
            Assert.Contains("F9 QSL,{TTSParms 0.8}{TTS Q S L, thank you}\r\n", result); // a real 3-letter label is not a county
        }

        [Fact]
        public void KeepsUpdatingAnAlreadyReplacedLabelIncludingCountyLines()
        {
            var line = VoiceMessageFile.Apply(VoiceMessageFile.Apply(Pasted, "MON"), "ALL/CAT");
            Assert.Contains("F6 ALL/CAT,{TTSParms 0.8}{TTS {ICAO ALL}, and {ICAO CAT}}\r\n", line);
            var back = VoiceMessageFile.Apply(line, "ORL");
            Assert.Contains("F6 ORL,{TTSParms 0.8}{TTS {ICAO ORL}}\r\n", back);
            Assert.Contains("F9 QSL,", back);
        }

        [Fact]
        public void LabelWithTheWordRoverQthInAnyCaseIsReplacedEvenWithoutASpokenGroup()
        {
            Assert.Equal("F6 MON,{WIPE}", VoiceMessageFile.Apply("F6 RoverQTh,{WIPE}", "MON"));
        }
    }
}
