using System.Globalization;
using System.Text.RegularExpressions;

namespace SubLedger.Core.Import;

/// <summary>One day's total from a hand-kept list: either a day number or a calendar date, and an exact gil amount.</summary>
public sealed record HistoryRow(int LineNumber, int? DayNumber, DateOnly? Date, long Gil, string SourceText)
{
    /// <summary>The calendar day this row is for. Day 1 is the starting date.</summary>
    public DateOnly ResolveDate(DateOnly startDate)
        => Date ?? startDate.AddDays((DayNumber ?? 1) - 1);
}

public sealed record HistoryParseResult(List<HistoryRow> Rows, List<string> Problems);

/// <summary>
/// Reads hand-kept income lists: text such as "Day 1: 125k" / "Day 2: 1.047m", and two-column spreadsheets exported
/// as CSV (day, amount). Amounts are converted with decimal arithmetic, so "1.047m" is exactly 1,047,000 gil and a
/// plain "1047312" keeps every digit.
/// </summary>
public static partial class HistoryParser
{
    // "Day 12: 125k", "day12 - 1.047m", "12, 125000", "12<TAB>1,250,000", "Day 3 = 0"
    [GeneratedRegex(@"^\s*(?:day\s*)?#?(?<day>\d{1,5})\s*(?:[:=\-–—,;\t|]|\s)\s*(?<amount>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayLine();

    // "2026-04-23, 125k", "4/23/2026: 1.047m"
    [GeneratedRegex(@"^\s*(?<date>\d{4}-\d{1,2}-\d{1,2}|\d{1,2}/\d{1,2}/\d{2,4})\s*(?:[:=,;\t|]|\s-\s|\s)\s*(?<amount>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DateLine();

    [GeneratedRegex(@"^(?<sign>-)?(?<number>\d+(?:\.\d+)?)\s*(?<unit>k|m|mil|million|b|bil|billion|thousand)?\s*(?:g|gil)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Amount();

    private static readonly string[] DateFormats = ["yyyy-M-d", "M/d/yyyy", "M/d/yy"];

    public static HistoryParseResult Parse(string text)
    {
        var rows = new List<HistoryRow>();
        var problems = new List<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            var number = i + 1;
            var dateMatch = DateLine().Match(line);
            if (dateMatch.Success
                && DateOnly.TryParseExact(dateMatch.Groups["date"].Value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                if (TryParseAmount(dateMatch.Groups["amount"].Value, out var gil))
                    rows.Add(new HistoryRow(number, null, date, gil, line));
                else
                    problems.Add($"Line {number}: could not read the amount in \"{line}\".");
                continue;
            }

            var dayMatch = DayLine().Match(line);
            if (dayMatch.Success)
            {
                var day = int.Parse(dayMatch.Groups["day"].Value, CultureInfo.InvariantCulture);
                if (day < 1)
                    problems.Add($"Line {number}: day numbers start at 1.");
                else if (TryParseAmount(dayMatch.Groups["amount"].Value, out var gil))
                    rows.Add(new HistoryRow(number, day, null, gil, line));
                else
                    problems.Add($"Line {number}: could not read the amount in \"{line}\".");
                continue;
            }

            // A spreadsheet's header row ("Day,Amount") is expected and not worth reporting.
            if (rows.Count == 0 && !line.Any(char.IsDigit))
                continue;

            problems.Add($"Line {number}: not recognised: \"{line}\".");
        }

        return new HistoryParseResult(rows, problems);
    }

    /// <summary>Reads "125k", "1.047m", "1,250,000", "980 gil", "0". Returns exact gil.</summary>
    public static bool TryParseAmount(string text, out long gil)
    {
        gil = 0;
        var cleaned = text.Trim().Trim('"', '\'').Replace(",", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        if (cleaned.Length == 0)
            return false;

        var match = Amount().Match(cleaned);
        if (!match.Success
            || !decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            return false;

        var multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "k" or "thousand" => 1_000m,
            "m" or "mil" or "million" => 1_000_000m,
            "b" or "bil" or "billion" => 1_000_000_000m,
            _ => 1m,
        };

        var value = number * multiplier;
        if (value > long.MaxValue)
            return false;

        gil = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        if (match.Groups["sign"].Success)
            gil = -gil;
        return true;
    }
}
