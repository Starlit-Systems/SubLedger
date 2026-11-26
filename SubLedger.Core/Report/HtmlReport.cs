using System.Globalization;
using System.Net;
using System.Text.Json;
using SubLedger.Core.Analytics;
using SubLedger.Core.Model;

namespace SubLedger.Core.Report;

/// <summary>How the report opens: the period, grouping and Free Company chosen in the plugin. The page can change all three.</summary>
public sealed record ReportStart(DateRange Range, RangePreset Preset, PeriodGrouping Grouping, ulong FcId);

/// <summary>
/// Writes the ledger into one self-contained HTML page: data, styles, charts and logic all inline, nothing fetched
/// from the internet, so it opens in any browser, offline, and can be kept or shared as a single file.
/// The page recalculates everything for whatever period and Free Company the reader picks, using the same rules as
/// the plugin.
/// </summary>
public static class HtmlReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(
        LedgerSnapshot snapshot,
        BusinessCalendar calendar,
        ISectorCatalog sectors,
        DateOnly today,
        ReportStart start,
        string version,
        byte[]? logoPng,
        DateTimeOffset generatedAt)
    {
        var template = LoadTemplate();

        // Free Companies seen in voyages or entries, in a stable order. Indexes keep the embedded data small.
        var companies = new List<(ulong Id, string Name)>();
        var companyIndex = new Dictionary<ulong, int>();
        int CompanyIndex(ulong id)
        {
            if (id == 0)
                return -1;
            if (companyIndex.TryGetValue(id, out var i))
                return i;
            var name = snapshot.FreeCompanies.FirstOrDefault(c => c.FcId == id)?.DisplayName ?? $"Free Company {id:X}";
            companies.Add((id, name));
            return companyIndex[id] = companies.Count - 1;
        }

        foreach (var fc in snapshot.FreeCompanies.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
            CompanyIndex(fc.FcId);

        var subs = new List<(int Fc, string Name)>();
        var subIndex = new Dictionary<(ulong, uint), int>();
        var items = new List<string>();
        var itemIndex = new Dictionary<uint, int>();

        var voyages = new List<object[]>();
        foreach (var v in snapshot.Voyages.OrderBy(v => v.CollectedUnix))
        {
            if (!subIndex.TryGetValue((v.FcId, v.SubRegister), out var s))
            {
                // The most recent name the submarine carried.
                var name = snapshot.Voyages.Where(x => x.FcId == v.FcId && x.SubRegister == v.SubRegister && !string.IsNullOrWhiteSpace(x.SubName))
                    .MaxBy(x => x.CollectedUnix)?.SubName ?? "Submarine";
                subs.Add((CompanyIndex(v.FcId), name));
                s = subIndex[(v.FcId, v.SubRegister)] = subs.Count - 1;
            }

            var loot = new List<long[]>();
            foreach (var line in v.Loot)
            {
                if (!itemIndex.TryGetValue(line.ItemId, out var item))
                {
                    items.Add(line.ItemName);
                    item = itemIndex[line.ItemId] = items.Count - 1;
                }

                loot.Add([item, line.Quantity, line.TotalGil]);
            }

            voyages.Add(
            [
                calendar.DateOf(v.CollectedUnix).ToString("yyyy-MM-dd", Inv),
                calendar.ToLocal(v.CollectedUnix).ToString("HH:mm", Inv),
                v.CollectedUnix,
                s,
                v.Route,
                v.TotalGil,
                loot,
                Fuel.Tanks(v, sectors),
            ]);
        }

        var entries = snapshot.Entries
            .Where(e => e.Kind is LedgerEntryKind.HistoricalDay or LedgerEntryKind.Adjustment or LedgerEntryKind.Expense or LedgerEntryKind.Investment)
            .OrderBy(e => e.BusinessDate)
            .Select(e => new object[]
            {
                e.BusinessDate.ToString("yyyy-MM-dd", Inv),
                CompanyIndex(e.FcId),
                e.Kind switch
                {
                    LedgerEntryKind.HistoricalDay => "hand",
                    LedgerEntryKind.Adjustment => "adj",
                    LedgerEntryKind.Investment => "inv",
                    _ => "run",
                },
                e.AmountGil,
                e.Category,
                e.Note,
            })
            .ToList();

        var cutoff = TimeSpan.FromMinutes(calendar.CutoffMinutes);
        var data = new
        {
            version,
            generated = TimeZoneInfo.ConvertTime(generatedAt, calendar.Zone).ToString("d MMMM yyyy, HH:mm", Inv),
            today = today.ToString("yyyy-MM-dd", Inv),
            timeZone = calendar.Zone.DisplayName,
            cutoff = $"{cutoff.Hours:00}:{cutoff.Minutes:00}",
            creditsPerTank = Fuel.CreditsPerTank,
            initial = new
            {
                from = start.Range.From.ToString("yyyy-MM-dd", Inv),
                to = start.Range.To.ToString("yyyy-MM-dd", Inv),
                preset = PresetKey(start.Preset),
                group = start.Grouping.ToString().ToLowerInvariant(),
                fc = start.FcId == 0 ? -1 : CompanyIndex(start.FcId),
            },
            companies = companies.Select(c => new { id = c.Id.ToString("X16", Inv), name = c.Name }),
            subs = subs.Select(s => new { fc = s.Fc, name = s.Name }),
            items,
            voyages,
            entries,
        };

        // The default encoder escapes <, > and &, so nothing in the data can close the script element it sits in.
        var json = JsonSerializer.Serialize(data);
        var logo = logoPng is { Length: > 0 } ? "data:image/png;base64," + Convert.ToBase64String(logoPng) : string.Empty;

        return template
            .Replace("__TITLE__", WebUtility.HtmlEncode($"SubLedger report, {today.ToString("d MMMM yyyy", Inv)}"))
            .Replace("__LOGO__", logo)
            .Replace("__SUBLEDGER_DATA__", json);
    }

    private static string PresetKey(RangePreset preset) => preset switch
    {
        RangePreset.Last7Days => "last7",
        RangePreset.Last30Days => "last30",
        RangePreset.Last90Days => "last90",
        RangePreset.ThisWeek => "thisweek",
        RangePreset.LastWeek => "lastweek",
        RangePreset.ThisMonth => "thismonth",
        RangePreset.LastMonth => "lastmonth",
        RangePreset.ThisQuarter => "thisquarter",
        RangePreset.LastQuarter => "lastquarter",
        RangePreset.ThisYear => "thisyear",
        RangePreset.Lifetime => "lifetime",
        _ => "custom",
    };

    private static string LoadTemplate()
    {
        using var stream = typeof(HtmlReport).Assembly.GetManifestResourceStream("SubLedger.Core.Report.report.html")
            ?? throw new InvalidOperationException("The report template is missing from SubLedger.Core.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
