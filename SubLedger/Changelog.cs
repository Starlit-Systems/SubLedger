using System;
using System.Collections.Generic;

namespace SubLedger;

internal sealed record ChangelogEntry(string Version, DateOnly Date, string[] Changes);

/// <summary>What changed in each version, newest first. Shown on the Updates page.</summary>
internal static class Changelog
{
    public static readonly IReadOnlyList<ChangelogEntry> Entries =
    [
        new("0.2.1.0", new DateOnly(2026, 11, 26),
        [
            "Latest voyages on the dashboard is grouped by Free Company: each one gets its own heading and its own row of up to four submarines, wrapping onto more rows when the window is narrow",
            "Every active submarine is shown; there is no longer a limit of twelve",
        ]),
        new("0.2.0.0", new DateOnly(2026, 11, 26),
        [
            "New Reports page: pick any period (last 7, 30 or 90 days, this or last week, month or quarter, this year, lifetime, or your own dates) and see its income, daily average, typical day, best day and how many days were recorded",
            "Income chart per day, week, month or quarter, with a 7-day average line on daily views, plus a running-total view",
            "Where the gil came from: a ranked breakdown by item, by submarine or by route",
            "Export any period as spreadsheet files (CSV) or one data file (JSON), exact to the gil, into Documents\\SubLedger",
            "Hand-kept records: paste or load a day-by-day list (\"Day 1: 125k\", or a two-column spreadsheet) under Settings, with a preview of what will count before anything is saved; an import can be removed again as a whole",
            "Corrections: add or subtract gil on any day by hand",
            "Chart bars and lines use calmer tones that are easier to tell apart, including for colour-blind readers",
        ]),
        new("0.1.1.0", new DateOnly(2026, 11, 26),
        [
            "Latest voyages on the dashboard is now one card per submarine, each showing its last five voyages",
            "The daily chart uses its full height and names the month along the bottom",
            "Buttons line up with the search boxes beside them, and the Console page no longer shows a stray scrollbar",
        ]),
        new("0.1.0.1", new DateOnly(2026, 11, 26),
        [
            "First version: every submarine voyage you collect is recorded automatically, with each item valued at its NPC vendor price from the game's own data",
            "A voyage is never counted twice, even across restarts or when an old voyage log is opened again",
            "One-click import of your existing SubmarineTracker history",
            "Dashboard with today, 7-day, 30-day and lifetime income, a daily chart, and income by submarine and by route",
            "A searchable list of every voyage with its full loot",
            "The ledger keeps a dated backup every day and repairs itself from the newest backup if the file is ever damaged",
            "Works per Free Company, with a combined view when you have more than one",
        ]),
    ];

    public static ChangelogEntry Latest => Entries[0];
}
