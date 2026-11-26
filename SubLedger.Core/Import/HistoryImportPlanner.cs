using SubLedger.Core.Analytics;
using SubLedger.Core.Model;

namespace SubLedger.Core.Import;

public enum HistoryRowStatus
{
    /// <summary>A new day: it will be imported and will count.</summary>
    Counts,

    /// <summary>
    /// The ledger already has recorded voyages for this day. The total is imported and kept on file, but the
    /// recorded voyages are what count, so income is not doubled.
    /// </summary>
    KeptButNotCounted,

    /// <summary>The ledger already holds a hand-kept total for this day; importing another would double it. Skipped.</summary>
    AlreadyImported,

    /// <summary>The same day appears again further down the file; the later line is used. Skipped.</summary>
    RepeatedInFile,
}

public sealed record PlannedHistoryRow(HistoryRow Row, DateOnly Date, HistoryRowStatus Status)
{
    public bool WillImport => Status is HistoryRowStatus.Counts or HistoryRowStatus.KeptButNotCounted;
}

/// <summary>What importing a hand-kept list would do, worked out before anything is written.</summary>
public sealed record HistoryImportPlan(IReadOnlyList<PlannedHistoryRow> Rows)
{
    public int ImportCount => Rows.Count(r => r.WillImport);
    public int CountedDays => Rows.Count(r => r.Status == HistoryRowStatus.Counts);
    public long CountedGil => Rows.Where(r => r.Status == HistoryRowStatus.Counts).Sum(r => r.Row.Gil);
    public int OverlapDays => Rows.Count(r => r.Status == HistoryRowStatus.KeptButNotCounted);
    public long OverlapGil => Rows.Where(r => r.Status == HistoryRowStatus.KeptButNotCounted).Sum(r => r.Row.Gil);
    public int SkippedCount => Rows.Count(r => !r.WillImport);
}

public static class HistoryImportPlanner
{
    /// <summary>
    /// Decides what each parsed line would do. <paramref name="startDate"/> is the calendar day that "Day 1" means;
    /// lines that carry their own date ignore it. <paramref name="existing"/> is the ledger's current daily picture
    /// for the Free Company the import is for.
    /// </summary>
    public static HistoryImportPlan Plan(
        IReadOnlyList<HistoryRow> rows, DateOnly startDate, SortedDictionary<DateOnly, DayTotal> existing)
    {
        // When a day is listed more than once, the last line is the one that stands.
        var lastLineForDate = new Dictionary<DateOnly, int>();
        foreach (var row in rows)
            lastLineForDate[row.ResolveDate(startDate)] = row.LineNumber;

        var planned = new List<PlannedHistoryRow>(rows.Count);
        foreach (var row in rows)
        {
            var date = row.ResolveDate(startDate);
            existing.TryGetValue(date, out var day);

            var status = lastLineForDate[date] != row.LineNumber ? HistoryRowStatus.RepeatedInFile
                : day is { HasManual: true } ? HistoryRowStatus.AlreadyImported
                : day is { Voyages: > 0 } ? HistoryRowStatus.KeptButNotCounted
                : HistoryRowStatus.Counts;

            planned.Add(new PlannedHistoryRow(row, date, status));
        }

        return new HistoryImportPlan(planned);
    }

    /// <summary>Turns the lines that will be imported into ledger entries sharing one batch id, so the import can be undone as a whole.</summary>
    public static List<LedgerEntry> ToEntries(HistoryImportPlan plan, LedgerStore store, ulong fcId, string sourceName, string batchId)
    {
        var entries = new List<LedgerEntry>();
        foreach (var row in plan.Rows)
        {
            if (!row.WillImport)
                continue;

            entries.Add(store.NewEntry(
                LedgerEntryKind.HistoricalDay, row.Date, row.Row.Gil, fcId,
                note: row.Row.SourceText, source: sourceName, batchId: batchId));
        }

        return entries;
    }
}
