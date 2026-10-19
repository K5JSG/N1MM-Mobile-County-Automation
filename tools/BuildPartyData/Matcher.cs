using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

/// <summary>
/// Pairs N1MM+'s county codes (abbreviations such as AUTA, ALB, ADA) with the Census counties of a state. A code is
/// normally the county name with letters left out, so every code is scored against every county name by how compactly
/// it fits in as a subsequence, and an optimal one-to-one assignment (Hungarian algorithm) is taken.
/// </summary>
internal static class Matcher
{
    public const int NoMatch = 100_000;

    public static string Letters(string text)
    {
        var d = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetter(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }

        return sb.ToString();
    }

    /// <summary>Names a county might be abbreviated from (St./Saint, "city" suffix for independent cities).</summary>
    public static List<string> Variants(string name, string nameLsad)
    {
        var list = new List<string>();
        var b = Letters(name);
        list.Add(b);
        if (b.StartsWith("SAINT", StringComparison.Ordinal))
        {
            list.Add("ST" + b.Substring(5));
        }
        else if (b.StartsWith("ST", StringComparison.Ordinal))
        {
            list.Add("SAINT" + b.Substring(2));
        }

        if (nameLsad.EndsWith(" city", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(b + "CITY");
        }

        return list;
    }

    /// <summary>
    /// For codes built from the first letters of the words of a riding name (CLC = Cloverdale-Langley City): like
    /// <see cref="Cost"/> but a letter that starts a word is worth much more than one in the middle of a word.
    /// </summary>
    public static int CostWords(string code, string name)
    {
        code = Letters(code);
        var letters = new StringBuilder();
        var starts = new List<bool>();
        var newWord = true;
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetter(ch))
            {
                letters.Append(char.ToUpperInvariant(ch));
                starts.Add(newWord);
                newWord = false;
            }
            else
            {
                newWord = true;
            }
        }

        var n = letters.Length;
        var m = code.Length;
        if (m == 0 || m > n)
        {
            return NoMatch;
        }

        const int inf = int.MaxValue / 4;
        var prev = new int[n];
        for (var i = 0; i < n; i++)
        {
            prev[i] = code[0] == letters[i] ? (starts[i] ? 0 : 300) : inf;
        }

        for (var j = 1; j < m; j++)
        {
            var cur = new int[n];
            Array.Fill(cur, inf);
            for (var i = j; i < n; i++)
            {
                if (letters[i] != code[j])
                {
                    continue;
                }

                for (var k = j - 1; k < i; k++)
                {
                    if (prev[k] < inf)
                    {
                        cur[i] = Math.Min(cur[i], prev[k] + ((i - k - 1) * 5) + (starts[i] ? 0 : 60));
                    }
                }
            }

            prev = cur;
        }

        var best = inf;
        for (var i = m - 1; i < n; i++)
        {
            best = Math.Min(best, prev[i]);
        }

        return best >= inf ? NoMatch : best + n;
    }

    /// <summary>Lower is better; <see cref="NoMatch"/> when the code cannot be formed from the name.</summary>
    public static int Cost(string code, IEnumerable<string> names)
    {
        var best = NoMatch;
        foreach (var n in names)
        {
            best = Math.Min(best, CostOne(code, n));
        }

        return best;
    }

    private static int CostOne(string code, string name)
    {
        code = Letters(code);
        var m = code.Length;
        var n = name.Length;
        if (m == 0 || m > n)
        {
            return NoMatch;
        }

        const int inf = int.MaxValue / 4;
        var prev = new int[n];
        for (var i = 0; i < n; i++)
        {
            prev[i] = code[0] == name[i] ? (i == 0 ? 0 : 400 + (i * 20)) : inf;
        }

        for (var j = 1; j < m; j++)
        {
            var cur = new int[n];
            Array.Fill(cur, inf);
            for (var i = j; i < n; i++)
            {
                if (name[i] != code[j])
                {
                    continue;
                }

                for (var k = j - 1; k < i; k++)
                {
                    if (prev[k] >= inf)
                    {
                        continue;
                    }

                    var gap = i - k - 1;
                    cur[i] = Math.Min(cur[i], prev[k] + (gap * 10));
                }
            }

            prev = cur;
        }

        var bestEnd = inf;
        for (var i = m - 1; i < n; i++)
        {
            if (prev[i] < inf)
            {
                bestEnd = Math.Min(bestEnd, prev[i] + ((n - 1 - i) * 2));
            }
        }

        return bestEnd >= inf ? NoMatch : bestEnd + n;
    }

    /// <summary>Minimum-cost one-to-one assignment; returns for each row the matched column (or -1 when padded).</summary>
    public static int[] Assign(int[,] cost, int rows, int cols)
    {
        var n = Math.Max(rows, cols);
        var a = new long[n + 1, n + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                a[i, j] = i <= rows && j <= cols ? cost[i - 1, j - 1] : NoMatch;
            }
        }

        var u = new long[n + 1];
        var v = new long[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = Enumerable.Repeat(long.MaxValue, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = long.MaxValue;
                var j1 = 0;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    var cur = a[i0, j] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        var result = Enumerable.Repeat(-1, rows).ToArray();
        for (var j = 1; j <= n; j++)
        {
            if (p[j] != 0 && p[j] <= rows && j <= cols)
            {
                result[p[j] - 1] = j - 1;
            }
        }

        return result;
    }
}
