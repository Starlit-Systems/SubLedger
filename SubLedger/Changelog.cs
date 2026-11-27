using System;
using System.Collections.Generic;

namespace SubLedger;

internal sealed record ChangelogEntry(string Version, DateOnly Date, string[] Changes);

/// <summary>What changed in each version, newest first. Shown on the Updates page.</summary>
internal static class Changelog
{
    public static readonly IReadOnlyList<ChangelogEntry> Entries =
    [
        new("0.7.1.0", new DateOnly(2026, 11, 26),
        [
            "\"Farming: first sub\" now counts each submarine only from its own first OJ or JORZ voyage, so submarines still levelling while another farms are left out until they start farming too",
        ]),
        new("0.7.0.0", new DateOnly(2026, 11, 26),
        [
            "Two new Reports ranges leave the levelling weeks out of the averages: \"Farming: first sub\" counts from the first day one of your submarines came back from OJ or JORZ, and \"Farming: all subs\" from the first day all of them had",
            "Each Free Company counts from its own start, so a second fleet that began farming later is judged on its own farming days",
            "The farming routes are set under Settings, General, which also shows the dates found for each Free Company",
            "The HTML report has the same two ranges. Payback still covers everything, since the levelling weeks were paid for too",
        ]),
        new("0.6.0.0", new DateOnly(2026, 11, 26),
        [
            "With more than one Free Company, the picker at the top of the window now takes any mix: all of them, one, or any combination. Every page, export and the HTML report follow it",
            "Free Companies side by side: the dashboard gains a By Free Company table, Reports a Free Companies breakdown and comparison table, and Expenses a payback table per Free Company",
            "The Voyages list says which Free Company each voyage belongs to when several are shown",
            "Hand-kept lists and corrections let you choose which Free Company they belong to",
            "The HTML report has the same pick-any-mix Free Company filter and the same side-by-side comparisons",
        ]),
        new("0.5.1.0", new DateOnly(2026, 11, 26),
        [
            "Fuel is tracked in ceruleum tanks and company credits: every voyage's tanks are worked out from the sectors it visited (9 for OJ), shown on the Expenses page, the Reports page and the HTML report",
            "Ceruleum tanks are no longer offered as a gil running cost, since they can only be bought with company credits",
        ]),
        new("0.5.0.0", new DateOnly(2026, 11, 26),
        [
            "Sync between computers: under Settings, Sync, choose a folder that Google Drive, Dropbox, OneDrive or similar keeps the same on each computer, and every computer's voyages, spending and corrections end up in one ledger",
            "Each computer writes only its own file in that folder, so the sync service never has two computers fighting over one file",
            "Nothing is ever counted twice: the same voyage seen by two computers, or the same SubmarineTracker history imported on both, is recognised; removing or restoring a record carries across too",
            "The ledger is backed up before the first merge of each session",
        ]),
        new("0.4.0.0", new DateOnly(2026, 11, 26),
        [
            "Full HTML report: Reports, then Open HTML report, opens one page in your browser with every figure, chart and table, its own date presets, custom dates, grouping and Free Company filter, a donut of what the gil came from, sortable and searchable voyages with their loot, and the payback chart",
            "The report is a single file with nothing to install or download, works offline, and has a light and a dark look; it is saved in Documents\\SubLedger so you can keep or share it",
            "Lifetime now starts at your earliest spending as well as your first voyage, so parts and plots bought before the first voyage are included",
            "Fixed: with several Free Companies, a hand-kept total for one company was left out of the combined view on days another company recorded voyages",
        ]),
        new("0.3.0.0", new DateOnly(2026, 11, 26),
        [
            "New Expenses page: enter what you spent, as one-off investments (submarine parts, your Free Company house, the plot) or running costs (ceruleum tanks, repair materials), with quantity and price each, so \"3 x Shark-class Bow at 4m\" is one entry",
            "Payback: SubLedger works out your profit, your return on investment and the day your submarines earned everything back, or, if they have not yet, when they will at the current pace",
            "A chart of money earned against money invested, and a breakdown of where the money went",
            "The dashboard shows a payback bar once any spending is entered",
            "Reports show running costs, income after them, and investments for the chosen period, and exports include an expenses file",
            "Each Free Company has its own payback; the combined view adds them together",
        ]),
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
