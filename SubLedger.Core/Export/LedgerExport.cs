using System.Globalization;
using System.Text;
using System.Text.Json;
using SubLedger.Core.Analytics;
using SubLedger.Core.Model;

namespace SubLedger.Core.Export;

/// <summary>What an export needs besides the records themselves.</summary>
public sealed record ExportContext(
    BusinessCalendar Calendar,
    IReadOnlyDictionary<ulong, string> FreeCompanyNames,
    ISectorCatalog Sectors,
    string AppVersion);

/// <summary>
/// Writes the ledger out as CSV (for spreadsheets) and JSON (for other programs). Amounts are exact gil, dates are
/// ISO 8601, and numbers never carry thousands separators, so the files import cleanly anywhere.
/// </summary>
public static class LedgerExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One row per voyage.</summary>
    public static string VoyagesCsv(IEnumerable<Voyage> voyages, ExportContext context)
    {
        var sb = new StringBuilder();
        Row(sb, "voyage_id", "ledger_day", "collected_local", "collected_utc", "free_company", "submarine", "route",
            "total_gil", "items", "returned_utc", "sent_out_utc", "source");
        foreach (var v in voyages.OrderBy(v => v.CollectedUnix))
        {
            Row(sb,
                v.Id,
                Day(context.Calendar.DateOf(v.CollectedUnix)),
                context.Calendar.ToLocal(v.CollectedUnix).ToString("yyyy-MM-dd HH:mm:ss", Inv),
                Utc(v.CollectedUnix),
                context.FreeCompanyNames.GetValueOrDefault(v.FcId, string.Empty),
                v.SubName,
                v.Route,
                v.TotalGil.ToString(Inv),
                v.Loot.Sum(l => (long)l.Quantity).ToString(Inv),
                v.ReturnUnix == 0 ? string.Empty : Utc(v.ReturnUnix),
                v.DispatchedUnix == 0 ? string.Empty : Utc(v.DispatchedUnix),
                SourceName(v.Source));
        }

        return sb.ToString();
    }

    /// <summary>One row per stack of items a voyage brought back.</summary>
    public static string LootCsv(IEnumerable<Voyage> voyages, ExportContext context)
    {
        var sb = new StringBuilder();
        Row(sb, "voyage_id", "ledger_day", "free_company", "submarine", "route", "sector", "sector_name", "item_id",
            "item", "quantity", "high_quality", "unit_price_gil", "total_gil");
        foreach (var v in voyages.OrderBy(v => v.CollectedUnix))
        {
            var day = Day(context.Calendar.DateOf(v.CollectedUnix));
            var company = context.FreeCompanyNames.GetValueOrDefault(v.FcId, string.Empty);
            foreach (var line in v.Loot)
            {
                Row(sb,
                    v.Id, day, company, v.SubName, v.Route,
                    context.Sectors.Letter(line.SectorId),
                    context.Sectors.Name(line.SectorId),
                    line.ItemId.ToString(Inv),
                    line.ItemName,
                    line.Quantity.ToString(Inv),
                    line.Hq ? "yes" : "no",
                    line.UnitPrice.ToString(Inv),
                    line.TotalGil.ToString(Inv));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// One row per day of the range, including days with nothing: "recorded" (income may be zero) or "no record".
    /// </summary>
    public static string DailyCsv(SortedDictionary<DateOnly, DayTotal> days, DateRange range)
    {
        var sb = new StringBuilder();
        Row(sb, "ledger_day", "status", "income_gil", "voyages", "voyage_gil", "hand_kept_gil", "hand_kept_counted",
            "correction_gil");
        for (var date = range.From; date <= range.To; date = date.AddDays(1))
        {
            if (days.TryGetValue(date, out var day))
            {
                Row(sb, Day(date), "recorded", day.IncomeGil.ToString(Inv), day.Voyages.ToString(Inv),
                    day.CapturedGil.ToString(Inv),
                    day.HasManual ? day.ManualGil.ToString(Inv) : string.Empty,
                    day.HasManual ? day.ManualExcluded ? "no" : "yes" : string.Empty,
                    day.AdjustmentGil.ToString(Inv));
            }
            else
            {
                Row(sb, Day(date), "no record", string.Empty, "0", string.Empty, string.Empty, string.Empty, string.Empty);
            }
        }

        return sb.ToString();
    }

    /// <summary>One row per expense or investment.</summary>
    public static string CostsCsv(IEnumerable<LedgerEntry> entries, ExportContext context)
    {
        var sb = new StringBuilder();
        Row(sb, "date", "type", "category", "description", "amount_gil", "free_company");
        foreach (var e in entries.Where(e => !e.Deleted && ExpenseCategories.IsCost(e.Kind)).OrderBy(e => e.BusinessDate))
        {
            Row(sb,
                Day(e.BusinessDate),
                e.Kind == LedgerEntryKind.Investment ? "investment" : "running cost",
                e.Category,
                e.Note,
                e.AmountGil.ToString(Inv),
                context.FreeCompanyNames.GetValueOrDefault(e.FcId, string.Empty));
        }

        return sb.ToString();
    }

    /// <summary>The whole report as one JSON document: summary, per-period totals, breakdowns and every voyage.</summary>
    public static string Json(
        RangeReport report,
        IEnumerable<Voyage> voyages,
        SortedDictionary<DateOnly, DayTotal> days,
        ExportContext context,
        DateTimeOffset generatedAt,
        IEnumerable<LedgerEntry>? costs = null)
    {
        var document = new
        {
            generator = "SubLedger",
            version = context.AppVersion,
            generatedUtc = generatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv),
            timeZone = context.Calendar.Zone.Id,
            dayStartsAtMinutes = context.Calendar.CutoffMinutes,
            currency = "gil",
            range = new { from = Day(report.Range.From), to = Day(report.Range.To) },
            summary = new
            {
                totalGil = report.TotalGil,
                voyages = report.Voyages,
                recordedDays = report.RecordedDays,
                zeroIncomeDays = report.ZeroDays,
                daysWithoutRecord = report.MissingDays,
                averagePerDayGil = report.AveragePerDay,
                averagePerRecordedDayGil = report.AveragePerRecordedDay,
                medianDayGil = report.MedianDay,
                averagePerVoyageGil = report.AveragePerVoyage,
                bestDay = report.BestDay is { } best ? new { day = Day(best.Date), incomeGil = best.IncomeGil } : null,
                worstDay = report.WorstDay is { } worst ? new { day = Day(worst.Date), incomeGil = worst.IncomeGil } : null,
                handKeptAndCorrectionsGil = report.ManualGil,
                runningCostsGil = report.RunningCostsGil,
                investedGil = report.InvestedGil,
                netAfterRunningCostsGil = report.NetGil,
            },
            costs = (costs ?? []).Where(e => !e.Deleted && ExpenseCategories.IsCost(e.Kind) && report.Range.Contains(e.BusinessDate)).OrderBy(e => e.BusinessDate).Select(e => new
            {
                date = Day(e.BusinessDate),
                type = e.Kind == LedgerEntryKind.Investment ? "investment" : "running cost",
                category = e.Category,
                description = e.Note,
                amountGil = e.AmountGil,
                freeCompany = context.FreeCompanyNames.GetValueOrDefault(e.FcId, string.Empty),
            }),
            periods = report.Periods.Select(p => new
            {
                grouping = report.Grouping.ToString().ToLowerInvariant(),
                from = Day(p.From),
                to = Day(p.To),
                label = p.Title,
                incomeGil = p.IncomeGil,
                voyages = p.Voyages,
                recordedDays = p.RecordedDays,
                daysWithoutRecord = p.MissingDays,
            }),
            days = Enumerable.Range(0, report.Range.Days).Select(i => report.Range.From.AddDays(i)).Select(date =>
                days.TryGetValue(date, out var d)
                    ? new { day = Day(date), recorded = true, incomeGil = (long?)d.IncomeGil, voyages = d.Voyages }
                    : new { day = Day(date), recorded = false, incomeGil = (long?)null, voyages = 0 }),
            submarines = report.BySubmarine.Select(Group),
            routes = report.ByRoute.Select(Group),
            items = report.ByItem.Select(i => new { itemId = i.ItemId, name = i.Name, quantity = i.Quantity, totalGil = i.TotalGil }),
            voyages = voyages.OrderBy(v => v.CollectedUnix).Select(v => new
            {
                id = v.Id,
                ledgerDay = Day(context.Calendar.DateOf(v.CollectedUnix)),
                collectedUtc = Utc(v.CollectedUnix),
                returnedUtc = v.ReturnUnix == 0 ? null : Utc(v.ReturnUnix),
                sentOutUtc = v.DispatchedUnix == 0 ? null : Utc(v.DispatchedUnix),
                // 64-bit ids are written as text: many JSON readers cannot hold them as numbers without losing digits.
                freeCompanyId = v.FcId.ToString("X16", Inv),
                freeCompany = context.FreeCompanyNames.GetValueOrDefault(v.FcId, string.Empty),
                submarine = v.SubName,
                submarineRegistered = v.SubRegister,
                route = v.Route,
                totalGil = v.TotalGil,
                source = SourceName(v.Source),
                loot = v.Loot.Select(l => new
                {
                    sector = context.Sectors.Letter(l.SectorId),
                    itemId = l.ItemId,
                    item = l.ItemName,
                    quantity = l.Quantity,
                    highQuality = l.Hq,
                    unitPriceGil = l.UnitPrice,
                    totalGil = l.TotalGil,
                }),
            }),
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object Group(GroupStats g) => new
    {
        name = g.Label,
        voyages = g.Voyages,
        totalGil = g.TotalGil,
        averagePerVoyageGil = g.AveragePerVoyage,
        gilPer24Hours = g.GilPer24Hours,
    };

    private static string SourceName(VoyageSource source) => source switch
    {
        VoyageSource.SubmarineTracker => "SubmarineTracker import",
        _ => "recorded by SubLedger",
    };

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", Inv);

    private static string Utc(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv);

    private static void Row(StringBuilder sb, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(Cell(cells[i]));
        }

        sb.Append("\r\n");
    }

    /// <summary>Quotes a value when it needs it, and stops spreadsheets treating text as a formula.</summary>
    internal static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' && !long.TryParse(value, NumberStyles.Integer, Inv, out _))
            value = "'" + value;

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
