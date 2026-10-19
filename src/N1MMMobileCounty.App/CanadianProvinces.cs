using System.Collections.Generic;

namespace N1MMMobileCounty.App
{
    /// <summary>
    /// The 13 Canadian multipliers exactly as the 2026 NYQP rules (v1.1, 2026-09-25, "Canadian Multiplier List")
    /// define them: Canada Post abbreviations. The rules do not list US states, so they are not here.
    /// </summary>
    internal static class CanadianProvinces
    {
        public static readonly IReadOnlyDictionary<string, string> NamesByAbbreviation = new Dictionary<string, string>
        {
            ["AB"] = "Alberta",
            ["BC"] = "British Columbia",
            ["MB"] = "Manitoba",
            ["NB"] = "New Brunswick",
            ["NL"] = "Newfoundland and Labrador",
            ["NT"] = "Northwest Territories",
            ["NS"] = "Nova Scotia",
            ["NU"] = "Nunavut",
            ["ON"] = "Ontario",
            ["PE"] = "Prince Edward Island",
            ["QC"] = "Quebec",
            ["SK"] = "Saskatchewan",
            ["YT"] = "Yukon",
        };
    }
}
