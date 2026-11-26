using System;
using System.Collections.Generic;

namespace SubLedger;

internal sealed record ChangelogEntry(string Version, DateOnly Date, string[] Changes);

/// <summary>What changed in each version, newest first. Shown on the Updates page.</summary>
internal static class Changelog
{
    public static readonly IReadOnlyList<ChangelogEntry> Entries =
    [
        new("0.1.0.0", new DateOnly(2026, 11, 26),
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
