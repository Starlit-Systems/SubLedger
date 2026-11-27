using Microsoft.Data.Sqlite;
using SubLedger.Core;
using SubLedger.Core.Storage;
using SubLedger.Core.Sync;

namespace SubLedger.Tests;

public class IgnoreTests
{
    private const long T0 = 1_780_000_000;

    [Fact]
    public void An_ignored_free_company_keeps_recording_and_the_choice_survives_a_restart()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveFreeCompany(TempLedger.Fc, "Helping Hands", "HELP", "Golem", T0);
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));

        Assert.True(ledger.Store.SetFreeCompanyIgnored(TempLedger.Fc, true));
        Assert.False(ledger.Store.SetFreeCompanyIgnored(TempLedger.Fc, true));
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0 + 86400, rings: 5));
        ledger.Restart();

        var snapshot = ledger.Store.Snapshot();
        Assert.True(Assert.Single(snapshot.FreeCompanies).Ignored);
        Assert.Equal(2, snapshot.Voyages.Count);

        Assert.True(ledger.Store.SetFreeCompanyIgnored(TempLedger.Fc, false));
        ledger.Restart();
        Assert.False(Assert.Single(ledger.Store.Snapshot().FreeCompanies).Ignored);
    }

    [Fact]
    public void A_ledger_from_before_ignoring_existed_is_brought_up_to_date()
    {
        using var ledger = new TempLedger();
        ledger.Store.ObserveFreeCompany(TempLedger.Fc, "Starlit", "STAR", "Golem", T0);
        ledger.Store.RecordVoyage(TempLedger.OjHaul(T0));

        // Take the ignore columns back out, as a file written by 0.7 would be.
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={ledger.DbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE free_companies DROP COLUMN ignored; ALTER TABLE free_companies DROP COLUMN ignored_changed_unix;";
            cmd.ExecuteNonQuery();
        }

        // An older file can still be read as it is (another computer's sync file is never changed)...
        var data = new LedgerDatabase(ledger.DbPath).LoadAll();
        Assert.False(Assert.Single(data.FreeCompanies).Ignored);

        // ...and opening it as this computer's ledger adds the columns.
        ledger.Restart();
        Assert.True(ledger.Store.SetFreeCompanyIgnored(TempLedger.Fc, true));
        ledger.Restart();
        Assert.True(Assert.Single(ledger.Store.Snapshot().FreeCompanies).Ignored);
        Assert.Single(ledger.Store.Snapshot().Voyages);
    }

    [Fact]
    public void Ignoring_travels_through_sync_and_seeing_the_free_company_again_does_not_undo_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "subledger-ignore-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var shared = Path.Combine(root, "shared");
            LedgerStore Open(string device)
            {
                var store = new LedgerStore(new LedgerDatabase(Path.Combine(root, device, "subledger.db")), new FakeItems(), new FakeSectors(), new LedgerLog(), device);
                store.Load();
                return store;
            }

            var a = Open("desktop");
            var b = Open("laptop");
            var syncA = new SyncFolder(shared, "desktop", "Desktop");
            var syncB = new SyncFolder(shared, "laptop", "Laptop");
            var scratch = Path.Combine(root, "scratch");

            a.ObserveFreeCompany(TempLedger.Fc, "Helping Hands", "HELP", "Golem", T0);
            b.ObserveFreeCompany(TempLedger.Fc, "Helping Hands", "HELP", "Golem", T0);
            a.SetFreeCompanyIgnored(TempLedger.Fc, true);

            // The laptop sees the Free Company again, renamed, after the desktop chose to ignore it.
            b.ObserveFreeCompany(TempLedger.Fc, "Helping Hands Too", "HELP", "Golem", T0 + 3600);

            syncA.Publish(a.Database);
            syncB.Pull(b, scratch, force: true);
            syncB.Publish(b.Database);
            syncA.Pull(a, scratch, force: true);

            Assert.True(Assert.Single(b.Snapshot().FreeCompanies).Ignored);
            Assert.True(Assert.Single(a.Snapshot().FreeCompanies).Ignored);
            Assert.Equal("Helping Hands Too", Assert.Single(a.Snapshot().FreeCompanies).Name);

            // Syncing again changes nothing.
            syncA.Publish(a.Database);
            Assert.False(syncB.Pull(b, scratch, force: true).Merged.Changed);

            // Counting it again on the laptop reaches the desktop.
            Thread.Sleep(1100);
            b.SetFreeCompanyIgnored(TempLedger.Fc, false);
            syncB.Publish(b.Database);
            syncA.Pull(a, scratch, force: true);
            Assert.False(Assert.Single(a.Snapshot().FreeCompanies).Ignored);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }
}
