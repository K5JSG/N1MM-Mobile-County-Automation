using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace N1MMMobileCounty.Core
{
    /// <summary>
    /// Keeps the county in an N1MM+ function-key message file (.mc) in step with the Rover QTH. Two places follow it:
    /// the spoken <c>{ICAO ...}</c> group (<c>{ICAO ROVERQTH}</c> or <c>{ICAO {ROVERQTH}}</c> in any capitalisation, then
    /// <c>{ICAO MON}</c>, or <c>{ICAO ALL}, and {ICAO CAT}</c> on a county line), and the button label when it is the word
    /// ROVERQTH (then "MON" or "ALL/CAT"). A label that already holds the county of its own spoken group counts as a
    /// previously replaced label, so later county changes keep updating it. "#" comment lines are never touched.
    /// </summary>
    public static class VoiceMessageFile
    {
        private static readonly Regex CountyPhrase = new(
            @"\{ICAO (?:\{(?i:ROVERQTH)\}|(?i:ROVERQTH))\}|\{ICAO [A-Z]{3}\}(?:, and \{ICAO [A-Z]{3}\})*",
            RegexOptions.Compiled);

        private static readonly Regex CodeInPhrase = new(@"\{ICAO ([A-Z]{3})\}", RegexOptions.Compiled);

        private static readonly Regex KeyLine = new(@"^(?<key>F\d+ )(?<label>[^,]*)(?<rest>,.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>The spoken phrase for a county ("MON") or county line ("ALL/CAT"), or null if it is not valid.</summary>
        public static string? Phrase(string qth)
        {
            var parts = qth.Split('/');
            if (parts.Length is < 1 or > 2 || parts.Any(p => p.Length != 3 || !p.All(c => c is >= 'A' and <= 'Z')))
            {
                return null;
            }

            return string.Join(", and ", parts.Select(p => "{ICAO " + p + "}"));
        }

        /// <summary>Returns the file text with every county placeholder set to <paramref name="qth"/> (unchanged if qth is not valid).</summary>
        public static string Apply(string text, string qth)
        {
            var phrase = Phrase(qth);
            if (phrase == null)
            {
                return text;
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    lines[i] = ApplyToLine(lines[i], qth, phrase);
                }
            }

            return string.Join("\n", lines);
        }

        private static string ApplyToLine(string line, string qth, string phrase)
        {
            var key = KeyLine.Match(line);
            if (!key.Success)
            {
                return CountyPhrase.Replace(line, _ => phrase);
            }

            var label = key.Groups["label"].Value;
            var rest = key.Groups["rest"].Value;
            var codes = string.Join("/", CodeInPhrase.Matches(rest).Select(m => m.Groups[1].Value));
            var trimmed = label.Trim();
            if (trimmed.Equals("ROVERQTH", StringComparison.OrdinalIgnoreCase) || (codes.Length > 0 && trimmed == codes))
            {
                label = qth;
            }

            return key.Groups["key"].Value + label + CountyPhrase.Replace(rest, _ => phrase);
        }
    }
}
