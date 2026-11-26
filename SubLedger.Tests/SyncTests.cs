using SubLedger.Core;
using SubLedger.Core.Capture;
using SubLedger.Core.Model;
using SubLedger.Core.Storage;
using SubLedger.Core.Sync;

namespace SubLedger.Tests;

public class SyncTests
{
    private const long T0 = 1_780_000_000;
    private const long Day = 86400;

    /// <summary>Two computers, each with its own ledger, sharing one sync folder.</summary>
    private sealed class TwoComputers : IDisposable
    {
        public TwoComputers()
        {
            Root = Path.Combine(Path.GetTempPath(), "subledger-sync-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Shared = Path.Combine(Root, "Google Drive", "SubLedger");
            A = Open("desktop");
            B = Open("laptop");
            SyncA = new SyncFolder(Shared, "desktop", "Desktop");
            SyncB = new SyncFolder(Shared, "laptop", "Laptop");
        }

        public string Root { get; }
        public string Shared { get; }
        public LedgerStore A { get; private set; }
        public LedgerStore B { get; private set; }
        public SyncFolder SyncA { get; }
        public SyncFolder SyncB { get; }
        public LedgerLog Log { get; } = new();

        private LedgerStore Open(string device)
        {
            var store = new LedgerStore(new LedgerDatabase(Path.Combine(Root, device, "subledger.db")), new FakeItems(), new FakeSectors(), Log, device);
            store.Load();
            return store;
        }

        public void ReopenA() => A = Open("desktop");

        /// <summary>A publishes, B pulls.</summary>
        public SyncResult AtoB()
        {
            SyncA.Publish(A.Database);
            return SyncB.Pull(B, Path.Combine(Root, "scratch"), force: true);
        }

        /// <summary>B publishes, A pulls.</summary>
        public SyncResult BtoA()
        {
            SyncB.Publish(B.Database);
            return SyncA.Pull(A, Path.Combine(Root, "scratch"), force: true);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* temp */ }
        }
    }

    private static long Total(LedgerStore store) => store.Snapshot().Voyages.Sum(v => v.TotalGil);

    [Fact]
    public void Voyages_recorded_on_one_computer_arrive_on_the_other()
    {
        using var pcs = new TwoComputers();
        pcs.A.RecordVoyage(TempLedger.OjHaul(T0));
        pcs.A.RecordVoyage(TempLedger.OjHaul(T0 + Day, rings: 5));
        pcs.A.ObserveFreeCompany(TempLedger.Fc, "Starlit", "STAR", "Golem", T0);

        var result = pcs.AtoB();

        Assert.Equal(2, result.Merged.VoyagesAdded);
        Assert.Equal(Total(pcs.A), Total(pcs.B));
        Assert.Equal("Starlit (Golem)", Assert.Single(pcs.B.Snapshot().FreeCompanies).DisplayName);
        var peer = Assert.Single(result.Peers);
        Assert.Equal("Desktop", peer.DeviceName);
        Assert.Equal(2, peer.Voyages);
    }

    [Fact]
    public void Syncing_again_and_again_changes_nothing()
    {
        using var pcs = new TwoComputers();
        pcs.A.RecordVoyage(TempLedger.OjHaul(T0));
        pcs.B.RecordVoyage(TempLedger.OjHaul(T0 + Day, sub: 2));

        pcs.AtoB();
        pcs.BtoA();
        var again = pcs.AtoB();
        var andAgain = pcs.BtoA();

        Assert.False(again.Merged.Changed);
        Assert.False(andAgain.Merged.Changed);
        Assert.Equal(2, pcs.A.Snapshot().Voyages.Count);
        Assert.Equal(2, pcs.B.Snapshot().Voyages.Count);
        Assert.Equal(Total(pcs.A), Total(pcs.B));
    }

    [Fact]
    public void The_same_history_imported_on_both_computers_is_not_doubled()
    {
        using var pcs = new TwoComputers();
        VoyageCapture[] history = [.. Enumerable.Range(0, 5).Select(i => TempLedger.OjHaul(T0 + i * Day) with { Source = VoyageSource.SubmarineTracker })];
        pcs.A.ImportVoyages(history, []);
        pcs.B.ImportVoyages(history, []);

        var result = pcs.AtoB();

        Assert.Equal(0, result.Merged.VoyagesAdded);
        Assert.Equal(5, result.Merged.Skipped);
        Assert.Equal(5, pcs.B.Snapshot().Voyages.Count);
    }

    [Fact]
    public void A_removed_voyage_is_removed_everywhere_and_restoring_it_travels_too()
    {
        using var pcs = new TwoComputers();
        var voyage = pcs.A.RecordVoyage(TempLedger.OjHaul(T0)).Voyage!;
        pcs.AtoB();

        Thread.Sleep(1100); // change times are kept to the second
        pcs.B.SetVoyageDeleted(voyage.Id, true);
        var removed = pcs.BtoA();
        Assert.Equal(1, removed.Merged.Removals);
        Assert.Empty(pcs.A.Snapshot().Voyages);

        Thread.Sleep(1100);
        pcs.A.SetVoyageDeleted(voyage.Id, false);
        var restored = pcs.AtoB();
        Assert.Equal(1, restored.Merged.Restorations);
        Assert.Single(pcs.B.Snapshot().Voyages);
    }

    [Fact]
    public void Removing_one_copy_of_a_twice_imported_voyage_removes_it_on_the_other_computer()
    {
        using var pcs = new TwoComputers();
        var capture = TempLedger.OjHaul(T0) with { Source = VoyageSource.SubmarineTracker };
        pcs.A.ImportVoyages([capture], []);
        pcs.B.ImportVoyages([capture], []);

        Thread.Sleep(1100);
        pcs.A.SetVoyageDeleted(pcs.A.Snapshot().Voyages[0].Id, true);
        pcs.AtoB();

        Assert.Empty(pcs.B.Snapshot().Voyages);
    }

    [Fact]
    public void Spending_and_corrections_travel_and_a_hand_kept_day_is_not_doubled()
    {
        using var pcs = new TwoComputers();
        var date = new DateOnly(2026, 4, 23);
        pcs.A.AddEntries(
        [
            pcs.A.NewEntry(LedgerEntryKind.Investment, date, 16_000_000, TempLedger.Fc, "4 x parts", "test", category: "Submarine parts"),
            pcs.A.NewEntry(LedgerEntryKind.HistoricalDay, date, 125_000, TempLedger.Fc, "Day 1: 125k", "list.txt"),
        ]);
        // The laptop imported the same list on its own.
        pcs.B.AddEntries([pcs.B.NewEntry(LedgerEntryKind.HistoricalDay, date, 125_000, TempLedger.Fc, "Day 1: 125k", "list.txt")]);

        var result = pcs.AtoB();

        Assert.Equal(1, result.Merged.EntriesAdded);
        Assert.Equal(1, result.Merged.Skipped);
        var entries = pcs.B.Snapshot().Entries;
        Assert.Single(entries, e => e.Kind == LedgerEntryKind.HistoricalDay);
        Assert.Single(entries, e => e.Kind == LedgerEntryKind.Investment && e.AmountGil == 16_000_000);
    }

    [Fact]
    public void Merged_records_survive_a_restart()
    {
        using var pcs = new TwoComputers();
        pcs.B.RecordVoyage(TempLedger.OjHaul(T0));
        pcs.BtoA();

        pcs.ReopenA();

        Assert.Single(pcs.A.Snapshot().Voyages);
        Assert.Equal("laptop", pcs.A.Snapshot().Voyages[0].DeviceId);
    }

    [Fact]
    public void An_unchanged_file_is_not_merged_twice_and_a_broken_one_is_reported()
    {
        using var pcs = new TwoComputers();
        pcs.A.RecordVoyage(TempLedger.OjHaul(T0));
        pcs.SyncA.Publish(pcs.A.Database);
        File.WriteAllText(Path.Combine(pcs.Shared, SyncFolder.Prefix + "broken" + SyncFolder.Extension), "half-copied");
        var scratch = Path.Combine(pcs.Root, "scratch");

        var first = pcs.SyncB.Pull(pcs.B, scratch);
        var second = pcs.SyncB.Pull(pcs.B, scratch);

        Assert.Equal(1, first.Merged.VoyagesAdded);
        Assert.Single(first.Problems);
        Assert.False(second.Merged.Changed);
        Assert.Single(second.Peers);
    }

    [Fact]
    public void Each_computer_writes_only_its_own_file()
    {
        using var pcs = new TwoComputers();
        pcs.SyncA.Publish(pcs.A.Database);
        pcs.SyncB.Publish(pcs.B.Database);

        var files = Directory.GetFiles(pcs.Shared).Select(f => Path.GetFileName(f)!).Order().ToArray();

        Assert.Equal(["subledger-sync-desktop.db", "subledger-sync-laptop.db"], files);
    }
}
