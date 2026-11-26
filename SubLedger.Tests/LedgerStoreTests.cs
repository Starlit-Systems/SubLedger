using SubLedger.Core.Capture;
using SubLedger.Core.Model;
using SubLedger.Core.Storage;

namespace SubLedger.Tests;

public class LedgerStoreTests
{
    private const long T0 = 1_780_000_000;
    private const long Day = 86400;

    [Fact]
    public void Records_a_voyage_with_exact_vendor_value()
    {
        using var ledger = new TempLedger();

        var result = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0, rings: 3, earrings: 2));

        Assert.True(result.Recorded);
        // 20 rubies @2 + 3 rings @8000 + 15 ingots @4 + 2 earrings @30000
        Assert.Equal(40 + 24_000 + 60 + 60_000, result.Voyage!.TotalGil);
        Assert.Equal("OJ", result.Voyage.Route);
        Assert.Equal(4, result.Voyage.Loot.Count);
    }

    [Fact]
    public void Voyages_survive_a_restart_with_ids_and_loot_intact()
    {
        using var ledger = new TempLedger();
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));

        ledger.Restart();

        var voyage = Assert.Single(ledger.Store.Snapshot().Voyages);
        Assert.Equal(TempLedger.Fc, voyage.FcId);
        Assert.Equal(TempLedger.Sub, voyage.SubRegister);
        Assert.Equal(84_100, voyage.TotalGil);
        Assert.Equal(4, voyage.Loot.Count);
        Assert.Equal("Salvaged Ring", voyage.Loot[1].ItemName);
    }

    [Fact]
    public void The_same_result_from_two_triggers_is_recorded_once()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));

        var first = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0) with { Trigger = "packet" });
        var second = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + 1) with { Trigger = "window" });

        Assert.True(first.Recorded);
        Assert.Equal(T0 - 60, first.Voyage!.ReturnUnix);
        Assert.False(second.Recorded);
        Assert.Single(ledger.Store.Snapshot().Voyages);
    }

    [Fact]
    public void Reopening_an_old_voyage_log_later_does_not_record_it_again()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));

        // Hours later, submarine still docked: the game shows the same report again.
        var again = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + 5 * 3600));

        Assert.Equal(RecordOutcome.DuplicateReview, again.Outcome);
        Assert.Single(ledger.Store.Snapshot().Voyages);
    }

    [Fact]
    public void Reopening_an_old_voyage_log_after_a_restart_does_not_record_it_again()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));

        ledger.Restart();
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: 0, now: T0 + Day));
        var again = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + Day));

        Assert.False(again.Recorded);
        Assert.Single(ledger.Store.Snapshot().Voyages);
    }

    [Fact]
    public void Viewing_the_previous_log_while_the_submarine_is_out_again_is_not_a_new_voyage()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));

        // Redeployed: the workshop now shows a return time a day ahead.
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 + Day, now: T0 + 120, live: true));
        var again = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + 3600));

        Assert.False(again.Recorded);
        Assert.Single(ledger.Store.Snapshot().Voyages);
    }

    [Fact]
    public void Consecutive_voyages_are_each_recorded_even_with_identical_loot()
    {
        using var ledger = new TempLedger();

        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));
        Assert.True(ledger.Store.RecordVoyage(TempLedger.OjHaul(T0)).Recorded);

        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 + Day, now: T0 + 120, live: true));
        var second = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + Day + 300));

        Assert.True(second.Recorded);
        Assert.Equal(T0 + Day, second.Voyage!.ReturnUnix);
        Assert.Equal(T0 + 120, second.Voyage.DispatchedUnix);
        Assert.Equal(2, ledger.Store.Snapshot().Voyages.Count);
    }

    [Fact]
    public void A_new_voyage_is_recorded_even_when_its_return_time_was_never_seen()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0, rings: 3));

        // Next day's result arrives without the workshop ever having shown the new return time. Different loot.
        var next = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + Day, rings: 5));

        Assert.True(next.Recorded);
        Assert.Equal(0, next.Voyage!.ReturnUnix);
        Assert.Equal(2, ledger.Store.Snapshot().Voyages.Count);
    }

    [Fact]
    public void Different_submarines_collected_together_are_separate_voyages()
    {
        using var ledger = new TempLedger();

        Assert.True(ledger.Store.RecordVoyage(TempLedger.OjHaul(T0, sub: 1)).Recorded);
        Assert.True(ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + 5, sub: 2)).Recorded);
        Assert.True(ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + 10, sub: 3)).Recorded);

        Assert.Equal(3, ledger.Store.Snapshot().Voyages.Count);
    }

    [Fact]
    public void Importing_twice_adds_nothing_the_second_time()
    {
        using var ledger = new TempLedger();
        VoyageCapture[] history =
        [
            TempLedger.OjHaul(T0) with { Source = VoyageSource.SubmarineTracker },
            TempLedger.OjHaul(T0 + Day) with { Source = VoyageSource.SubmarineTracker },
            TempLedger.OjHaul(T0 + 2 * Day) with { Source = VoyageSource.SubmarineTracker },
        ];

        var first = ledger.Store.ImportVoyages(history, []);
        var second = ledger.Store.ImportVoyages(history, []);

        Assert.Equal(3, first.Imported);
        Assert.Equal(3 * 84_100, first.ImportedGil);
        Assert.Equal(0, second.Imported);
        Assert.Equal(3, second.Duplicates);
        Assert.Equal(3, ledger.Store.Snapshot().Voyages.Count);
    }

    [Fact]
    public void An_imported_voyage_that_was_also_captured_live_counts_once()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));

        // SubmarineTracker saw the same collection two seconds apart.
        var import = ledger.Store.ImportVoyages([TempLedger.OjHaul(T0 + 2) with { Source = VoyageSource.SubmarineTracker }], []);

        Assert.Equal(0, import.Imported);
        Assert.Equal(1, import.Duplicates);
    }

    [Fact]
    public void A_live_capture_of_a_voyage_already_imported_counts_once()
    {
        using var ledger = new TempLedger();
        ledger.Store.ImportVoyages([TempLedger.OjHaul(T0) with { Source = VoyageSource.SubmarineTracker }], []);
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));

        var live = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + 3));

        Assert.Equal(RecordOutcome.DuplicateCollection, live.Outcome);
    }

    [Fact]
    public void A_deleted_voyage_leaves_the_totals_and_is_not_captured_again()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 - 60, now: T0 - 30));
        var voyage = ledger.Store.RecordVoyage(TempLedger.OjHaul(T0)).Voyage!;

        Assert.True(ledger.Store.SetVoyageDeleted(voyage.Id, true));
        ledger.Restart();

        Assert.Empty(ledger.Store.Snapshot().Voyages);
        Assert.False(ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + 1)).Recorded);
    }

    [Fact]
    public void An_unknown_item_is_recorded_at_zero_and_reported()
    {
        using var ledger = new TempLedger();
        var capture = new VoyageCapture
        {
            FcId = TempLedger.Fc,
            SubRegister = TempLedger.Sub,
            CollectedUnix = T0,
            Sectors = [new CapturedSector(FakeSectors.O, new CapturedItem(999_999, 4, false), null)],
        };

        var result = ledger.Store.RecordVoyage(capture);

        Assert.True(result.Recorded);
        Assert.Equal(0, result.Voyage!.TotalGil);
        Assert.Contains(ledger.Log.Snapshot(), l => l.Level == Core.LogLevel.Warning && l.Message.Contains("999999"));
    }

    [Fact]
    public void Free_companies_and_submarines_are_remembered()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveFreeCompany(TempLedger.Fc, "Starlit", "STAR", "Gilgamesh", T0);
        ledger.Store.ObserveSubmarine(TempLedger.Seen(returnUnix: T0 + Day, now: T0, live: true));

        ledger.Restart();

        var snapshot = ledger.Store.Snapshot();
        var fc = Assert.Single(snapshot.FreeCompanies);
        Assert.Equal("Starlit (Gilgamesh)", fc.DisplayName);
        var sub = Assert.Single(snapshot.Submarines);
        Assert.Equal("Nautilus", sub.Name);
        Assert.Equal(T0 + Day, sub.PendingReturnUnix);
        Assert.Equal([FakeSectors.O, FakeSectors.J], sub.PendingSectors);
    }

    [Fact]
    public void Backup_and_restore_bring_back_the_ledger()
    {
        using var ledger = new TempLedger();
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));
        var backups = new BackupManager(ledger.Store.Database, Path.Combine(ledger.Directory, "backups"), ledger.Log);

        var backup = backups.Create("manual");
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + Day, rings: 1));
        Assert.Equal(2, ledger.Store.Snapshot().Voyages.Count);

        backups.Restore(backup!.Path);
        ledger.Restart();

        Assert.Single(ledger.Store.Snapshot().Voyages);
        Assert.Contains(backups.List(), b => b.Reason == "before-restore");
    }

    [Fact]
    public void A_damaged_ledger_is_set_aside_and_the_newest_backup_takes_over()
    {
        using var ledger = new TempLedger();
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));
        var backups = new BackupManager(ledger.Store.Database, Path.Combine(ledger.Directory, "backups"), ledger.Log);
        backups.Create("daily");

        File.WriteAllText(ledger.DbPath, "this is not a database");
        Assert.ThrowsAny<Exception>(() => ledger.Open());

        var used = backups.RecoverFromDamage();
        ledger.Restart();

        Assert.NotNull(used);
        Assert.Single(ledger.Store.Snapshot().Voyages);
        Assert.Contains(Directory.GetFiles(ledger.Directory), f => f.Contains(".damaged-"));
    }

    [Fact]
    public void The_ledger_file_is_closed_between_operations()
    {
        using var ledger = new TempLedger();
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));

        // A file-sync tool must be able to take the file exclusively; an open connection would refuse this.
        using var exclusive = new FileStream(ledger.DbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(File.Exists(ledger.DbPath + "-wal"));
        Assert.False(File.Exists(ledger.DbPath + "-journal"));
    }
}
