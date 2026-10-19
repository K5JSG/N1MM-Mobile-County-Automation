using System.Linq;
using N1MMMobileCounty.Core;
using Xunit;

namespace N1MMMobileCounty.Tests
{
    public class QsoPartyTests
    {
        [Fact]
        public void EveryPartyLoadsWithItsCountiesAndUniqueCodes()
        {
            Assert.True(QsoParties.All.Count >= 45);
            foreach (var party in QsoParties.All)
            {
                var locator = QsoParties.Load(party.Id);
                Assert.Equal(party.CountyCount, locator.CountyCount);
                var codes = locator.Counties.Select(c => c.Abbreviation).ToList();
                Assert.Equal(codes.Count, codes.Distinct().Count()); // a code names exactly one county
                Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
            }
        }

        [Theory]
        [InlineData("AL", 67)]
        [InlineData("GA", 159)]
        [InlineData("TX", 254)]
        [InlineData("VA", 133)]   // 95 counties + 38 independent cities
        [InlineData("MD", 25)]    // 23 counties + Baltimore city + DC
        [InlineData("DE", 3)]
        [InlineData("NY", 62)]
        [InlineData("7QP", 259)]
        [InlineData("NEWE", 68)]
        public void CountyCountsMatchTheStates(string id, int expected)
        {
            Assert.Equal(expected, QsoParties.Find(id)!.CountyCount);
        }

        [Fact]
        public void NewYorkCodesAreTheOfficialNyqpOnes()
        {
            // The NYQP codes come straight from N1MM+'s own list now; they must agree with the 2026 rules table.
            var locator = QsoParties.Load("NY");
            foreach (var county in locator.Counties)
            {
                Assert.Equal(NyqpCounties.AbbreviationFor(county.Name), county.Abbreviation);
            }
        }

        [Theory]
        [InlineData("CA", 34.0522, -118.2437, "Los Angeles")]
        [InlineData("TX", 30.2672, -97.7431, "Travis")]
        [InlineData("OH", 39.9612, -82.9988, "Franklin")]
        [InlineData("IN", 39.7684, -86.1581, "Marion")]
        [InlineData("FL", 25.7617, -80.1918, "Miami-Dade")]
        [InlineData("IL", 41.8781, -87.6298, "Cook")]
        [InlineData("VA", 37.5407, -77.4360, "Richmond city")]
        [InlineData("MD", 39.2904, -76.6122, "Baltimore city, MD")]
        [InlineData("MD", 38.8977, -77.0365, "District of Columbia, DC")]
        [InlineData("MO", 38.6270, -90.1994, "St. Louis city")]
        [InlineData("PA", 39.9526, -75.1652, "Philadelphia")]
        [InlineData("LA", 29.9511, -90.0715, "Orleans Parish")]
        [InlineData("AZ", 33.4484, -112.0740, "Maricopa")]
        [InlineData("CO", 39.7392, -104.9903, "Denver")]
        public void FindsTheRightCountyInOtherParties(string party, double lat, double lon, string name)
        {
            Assert.Equal(name, QsoParties.Load(party).Find(lat, lon)?.Name);
        }

        [Theory]
        [InlineData("CP", 51.0447, -114.0719, "CCE")]   // downtown Calgary: Calgary Centre
        [InlineData("CP", 53.5461, -113.4938, "EDC")]   // downtown Edmonton: Edmonton Centre
        [InlineData("CP", 49.8951, -97.1384, "WPC")]    // downtown Winnipeg: Winnipeg Centre
        [InlineData("BC", 49.2827, -123.1207, "VAC")]   // downtown Vancouver: Vancouver Centre
        [InlineData("BC", 48.4284, -123.3656, "VIC")]   // Victoria
        public void CanadianPartiesUseElectoralDistrictCodes(string party, double lat, double lon, string code)
        {
            Assert.Equal(code, QsoParties.Load(party).Find(lat, lon)?.Abbreviation);
        }

        [Theory]
        [InlineData("ON", 43.6532, -79.3832, "TOR")]    // Toronto
        [InlineData("ON", 45.4215, -75.6972, "OTT")]    // Ottawa
        [InlineData("ON", 43.1394, -80.2644, "BFD")]    // Brantford (city, split from Brant County)
        [InlineData("ON", 43.1931, -80.3834, "BRA")]    // Paris: Brant County
        [InlineData("ON", 42.8368, -80.3032, "NFK")]    // Simcoe: Norfolk (split from Haldimand)
        [InlineData("ON", 42.9575, -79.8338, "HAL")]    // Cayuga: Haldimand
        [InlineData("ON", 46.4917, -80.9930, "SUD")]    // Greater Sudbury counts as Sudbury District
        [InlineData("QC", 45.5019, -73.5674, "MTL")]    // Montreal
        [InlineData("QC", 46.8139, -71.2080, "QUE")]    // Quebec City: Capitale-Nationale
        [InlineData("QC", 45.4765, -75.7013, "OTS")]    // Gatineau: Outaouais
        [InlineData("QC", 45.4042, -71.8929, "ETE")]    // Sherbrooke: Estrie
        [InlineData("QC", 45.6066, -73.7124, "LVL")]    // Laval
        [InlineData("AC", 44.6488, -63.5752, "NSHRM")]  // Halifax
        [InlineData("AC", 47.5615, -52.7126, "NLASJ")]  // St. John's: Newfoundland division 1
        [InlineData("AC", 46.2382, -63.1311, "PEQNS")]  // Charlottetown: Queens County
        [InlineData("AC", 45.9636, -66.6431, "NBYOR")]  // Fredericton: York County
        public void OntarioAndAtlanticCanadaCodes(string party, double lat, double lon, string code)
        {
            Assert.Equal(code, QsoParties.Load(party).Find(lat, lon)?.Abbreviation);
        }

        [Theory]
        // Codes taken from sponsors' own published tables (Missouri: St. Louis City is STL and St. Louis County is SLC, the reverse of what the letters suggest).
        [InlineData("MO", 38.6270, -90.1994, "STL")]    // downtown St. Louis (independent city)
        [InlineData("MO", 38.6426, -90.3237, "SLC")]    // Clayton, St. Louis County
        [InlineData("MD", 39.2904, -76.6122, "BAL")]    // downtown Baltimore: Baltimore City is BAL (not BCT)
        [InlineData("MD", 39.4015, -76.6019, "BCT")]    // Towson, Baltimore County
        [InlineData("KY", 36.6103, -88.3148, "CAW")]    // Murray, Calloway County
        [InlineData("CO", 37.3000, -108.5900, "MON")]   // Cortez, Montezuma County
        [InlineData("CO", 38.4783, -107.8762, "MOT")]   // Montrose
        [InlineData("FL", 29.6516, -82.3248, "ALC")]    // Gainesville, Alachua County
        public void CodesAgreeWithSponsorTables(string party, double lat, double lon, string code)
        {
            Assert.Equal(code, QsoParties.Load(party).Find(lat, lon)?.Abbreviation);
        }

        [Theory]
        // Hawaii's districts are ZIP code groups (sponsor's mult maps). N1MM+ calls Lanai LAN where the sponsor's map says LNI.
        [InlineData(21.2793, -157.8294, "HON")]   // Waikiki
        [InlineData(21.3069, -157.8583, "HON")]   // downtown Honolulu
        [InlineData(21.3350, -158.0560, "LHN")]   // Kapolei
        [InlineData(21.3972, -157.9733, "PRL")]   // Pearl City
        [InlineData(21.4020, -157.7390, "WHN")]   // Kailua, windward Oahu
        [InlineData(19.7297, -155.0900, "HIL")]   // Hilo
        [InlineData(19.6400, -155.9960, "KON")]   // Kailua-Kona
        [InlineData(20.0200, -155.6700, "KOH")]   // Waimea (Kohala)
        [InlineData(19.4300, -155.2300, "VOL")]   // Volcano village
        [InlineData(21.9811, -159.3711, "KAU")]   // Lihue
        [InlineData(21.9000, -160.1500, "NII")]   // Niihau
        [InlineData(20.8783, -156.6825, "MAU")]   // Lahaina
        [InlineData(21.0890, -157.0230, "MOL")]   // Kaunakakai
        [InlineData(20.8270, -156.9190, "LAN")]   // Lanai City
        [InlineData(21.1900, -156.9800, "KAL")]   // Kalaupapa
        [InlineData(19.7000, -155.4500, "HIL")]   // Saddle Road, the sponsor's map has this in the Hilo district
        public void HawaiiDistrictCodes(double lat, double lon, string code)
        {
            Assert.Equal(code, QsoParties.Load("HI").Find(lat, lon)?.Abbreviation);
        }

        [Fact]
        public void HawaiiHasTheFourteenN1mmCodes()
        {
            var codes = QsoParties.Load("HI").Counties.Select(c => c.Abbreviation).OrderBy(c => c).ToArray();
            Assert.Equal(new[] { "HIL", "HON", "KAL", "KAU", "KOH", "KON", "LAN", "LHN", "MAU", "MOL", "NII", "PRL", "VOL", "WHN" }, codes);
        }

        [Fact]
        public void CanadianPartyCounts()
        {
            Assert.Equal(65, QsoParties.Find("CP")!.CountyCount);
            Assert.Equal(43, QsoParties.Find("BC")!.CountyCount);
            Assert.Equal(50, QsoParties.Find("ON")!.CountyCount);
            Assert.Equal(47, QsoParties.Find("AC")!.CountyCount);
            Assert.Equal(17, QsoParties.Find("QC")!.CountyCount);
        }

        [Fact]
        public void MultiStatePartiesUseStateInTheCode()
        {
            Assert.Equal("IDADA", QsoParties.Load("7QP").Find(43.6150, -116.2023)?.Abbreviation);   // Boise
            Assert.Equal("MASUF", QsoParties.Load("NEWE").Find(42.3601, -71.0589)?.Abbreviation);   // Boston
            Assert.Equal("Suffolk, MA", QsoParties.Load("NEWE").Find(42.3601, -71.0589)?.Name);
        }

        [Fact]
        public void OutsideThePartyAreaThereIsNoCounty()
        {
            Assert.Null(QsoParties.Load("TX").Find(40.7128, -74.0060)); // New York City is not in Texas
        }

        [Fact]
        public void TrackerResetForgetsTheCommittedCounty()
        {
            var tracker = new CountyTracker(1);
            Assert.True(tracker.Update("ALB"));
            tracker.Reset();
            Assert.Null(tracker.Current);
            Assert.True(tracker.Update("ALB"));
        }
    }
}
