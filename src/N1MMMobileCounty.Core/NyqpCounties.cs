using System;
using System.Collections.Generic;

namespace N1MMMobileCounty.Core
{
    /// <summary>The 62 New York counties and their official NYQP three-letter abbreviations (2026 rules v1.1).</summary>
    public static class NyqpCounties
    {
        private static readonly string[][] Table =
        {
            new[] { "Albany", "ALB" }, new[] { "Allegany", "ALL" }, new[] { "Bronx", "BRX" },
            new[] { "Broome", "BRM" }, new[] { "Cattaraugus", "CAT" }, new[] { "Cayuga", "CAY" },
            new[] { "Chautauqua", "CHA" }, new[] { "Chemung", "CHE" }, new[] { "Chenango", "CGO" },
            new[] { "Clinton", "CLI" }, new[] { "Columbia", "COL" }, new[] { "Cortland", "COR" },
            new[] { "Delaware", "DEL" }, new[] { "Dutchess", "DUT" }, new[] { "Erie", "ERI" },
            new[] { "Essex", "ESS" }, new[] { "Franklin", "FRA" }, new[] { "Fulton", "FUL" },
            new[] { "Genesee", "GEN" }, new[] { "Greene", "GRE" }, new[] { "Hamilton", "HAM" },
            new[] { "Herkimer", "HER" }, new[] { "Jefferson", "JEF" }, new[] { "Kings", "KIN" },
            new[] { "Lewis", "LEW" }, new[] { "Livingston", "LIV" }, new[] { "Madison", "MAD" },
            new[] { "Monroe", "MON" }, new[] { "Montgomery", "MTG" }, new[] { "Nassau", "NAS" },
            new[] { "New York", "NEW" }, new[] { "Niagara", "NIA" }, new[] { "Oneida", "ONE" },
            new[] { "Onondaga", "ONO" }, new[] { "Ontario", "ONT" }, new[] { "Orange", "ORA" },
            new[] { "Orleans", "ORL" }, new[] { "Oswego", "OSW" }, new[] { "Otsego", "OTS" },
            new[] { "Putnam", "PUT" }, new[] { "Queens", "QUE" }, new[] { "Rensselaer", "REN" },
            new[] { "Richmond", "RIC" }, new[] { "Rockland", "ROC" }, new[] { "Saratoga", "SAR" },
            new[] { "Schenectady", "SCH" }, new[] { "Schoharie", "SCO" }, new[] { "Schuyler", "SCU" },
            new[] { "Seneca", "SEN" }, new[] { "St. Lawrence", "STL" }, new[] { "Steuben", "STE" },
            new[] { "Suffolk", "SUF" }, new[] { "Sullivan", "SUL" }, new[] { "Tioga", "TIO" },
            new[] { "Tompkins", "TOM" }, new[] { "Ulster", "ULS" }, new[] { "Warren", "WAR" },
            new[] { "Washington", "WAS" }, new[] { "Wayne", "WAY" }, new[] { "Westchester", "WES" },
            new[] { "Wyoming", "WYO" }, new[] { "Yates", "YAT" },
        };

        private static readonly Dictionary<string, string> AbbrByName = BuildMap();

        public static int Count => Table.Length;

        /// <summary>Returns the NYQP abbreviation for a county name, or null if the name is unknown.</summary>
        public static string? AbbreviationFor(string countyName)
        {
            return AbbrByName.TryGetValue(countyName.Trim(), out var abbr) ? abbr : null;
        }

        private static Dictionary<string, string> BuildMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Table)
            {
                map[row[0]] = row[1];
            }

            return map;
        }
    }
}
