using Microsoft.Data.Sqlite;
using SubLedger.Core.Import;
using SubLedger.Core.Model;

namespace SubLedger.Tests;

public class SubmarineTrackerImportTests
{
    /// <summary>Builds a small database with SubmarineTracker's table layout, in write-ahead-log mode like the real one.</summary>
    private static string CreateTrackerDatabase(string directory, params (long Collected, uint Sector, uint Primary, int PrimaryCount, uint Extra, int ExtraCount)[] rows)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SubmarineTrackerImporter.DatabaseFileName);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE freecompany (FreeCompanyId BLOB PRIMARY KEY NOT NULL, FreeCompanyTag TEXT NOT NULL, World TEXT NOT NULL,
                    CharacterName TEXT NOT NULL, UnlockedSectors BLOB NOT NULL, ExploredSectors BLOB NOT NULL);
                CREATE TABLE submarine (FreeCompanyId BLOB NOT NULL, SubmarineId INTEGER NOT NULL, "Return" INTEGER NOT NULL,
                    Name TEXT NOT NULL, Rank INTEGER NOT NULL, PRIMARY KEY (FreeCompanyId, SubmarineId));
                CREATE TABLE loot (FreeCompanyId BLOB NOT NULL, SubmarineId INTEGER NOT NULL, "Return" INTEGER NOT NULL,
                    Sector INTEGER NOT NULL, PrimaryItem INTEGER NOT NULL, PrimaryCount INTEGER NOT NULL, PrimaryHQ BOOLEAN NOT NULL,
                    AdditionalItem INTEGER NOT NULL, AdditionalCount INTEGER NOT NULL, AdditionalHQ BOOLEAN NOT NULL,
                    "Date" INTEGER NOT NULL, Valid BOOLEAN NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        // MessagePack uint64: 0xCF followed by eight big-endian bytes.
        var fc = new byte[9];
        fc[0] = 0xCF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(fc.AsSpan(1), TempLedger.Fc);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO freecompany VALUES ($fc, 'STAR', 'Gilgamesh', 'Someone', X'80', X'80');
                INSERT INTO submarine VALUES ($fc, $sub, 0, 'Nautilus', 90);
                """;
            cmd.Parameters.AddWithValue("$fc", fc);
            cmd.Parameters.AddWithValue("$sub", (long)TempLedger.Sub);
            cmd.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO loot VALUES ($fc, $sub, $collected, $sector, $item, $count, 0, $item2, $count2, 0, $collected, 1);";
            cmd.Parameters.AddWithValue("$fc", fc);
            cmd.Parameters.AddWithValue("$sub", (long)TempLedger.Sub);
            cmd.Parameters.AddWithValue("$collected", row.Collected);
            cmd.Parameters.AddWithValue("$sector", (long)row.Sector);
            cmd.Parameters.AddWithValue("$item", (long)row.Primary);
            cmd.Parameters.AddWithValue("$count", row.PrimaryCount);
            cmd.Parameters.AddWithValue("$item2", (long)row.Extra);
            cmd.Parameters.AddWithValue("$count2", row.ExtraCount);
            cmd.ExecuteNonQuery();
        }

        return path;
    }

    [Fact]
    public void Sector_rows_are_grouped_into_voyages_and_valued()
    {
        using var ledger = new TempLedger();
        var path = CreateTrackerDatabase(
            Path.Combine(ledger.Directory, "tracker"),
            (1_780_000_000, FakeSectors.O, FakeItems.Ruby, 20, FakeItems.SalvagedRing, 3),
            (1_780_000_000, FakeSectors.J, FakeItems.PlatinumIngot, 15, FakeItems.ExtravagantEarring, 2),
            (1_780_086_400, FakeSectors.O, FakeItems.SalvagedBracelet, 4, 0, 0),
            (1_780_086_400, FakeSectors.J, FakeItems.ExtravagantNecklace, 1, 0, 0));

        var data = SubmarineTrackerImporter.Read([path], ledger.Directory);
        var result = ledger.Store.ImportVoyages(data.Voyages, data.FreeCompanies);

        Assert.Empty(data.Warnings);
        Assert.Equal(4, data.LootRows);
        Assert.Equal(2, result.Imported);
        Assert.Equal(84_100 + 36_000 + 34_500, result.ImportedGil);

        var snapshot = ledger.Store.Snapshot();
        Assert.All(snapshot.Voyages, v =>
        {
            Assert.Equal(VoyageSource.SubmarineTracker, v.Source);
            Assert.Equal(TempLedger.Fc, v.FcId);
            Assert.Equal("Nautilus", v.SubName);
            Assert.Equal("OJ", v.Route);
        });
        var fc = Assert.Single(snapshot.FreeCompanies);
        Assert.Equal("STAR", fc.Tag);
        Assert.Equal("Gilgamesh", fc.World);
    }

    [Fact]
    public void Conflict_copies_add_only_the_voyages_the_main_file_lacks()
    {
        using var ledger = new TempLedger();
        var dir = Path.Combine(ledger.Directory, "tracker");
        CreateTrackerDatabase(dir,
            (1_780_000_000, FakeSectors.O, FakeItems.SalvagedRing, 3, 0, 0),
            (1_780_086_400, FakeSectors.O, FakeItems.SalvagedRing, 2, 0, 0));

        // A sync conflict copy holding one shared voyage and one the main file never got.
        var conflictDir = Path.Combine(ledger.Directory, "conflict-build");
        var conflict = CreateTrackerDatabase(conflictDir,
            (1_780_000_000, FakeSectors.O, FakeItems.SalvagedRing, 3, 0, 0),
            (1_780_043_200, FakeSectors.O, FakeItems.SalvagedRing, 5, 0, 0));
        File.Copy(conflict, Path.Combine(dir, "submarine-sqlite.sync-conflict-20260521-224521-XSP2KHT.db"));

        var files = SubmarineTrackerImporter.FindDatabases(dir, includeConflictCopies: true);
        var data = SubmarineTrackerImporter.Read(files, ledger.Directory);
        var result = ledger.Store.ImportVoyages(data.Voyages, data.FreeCompanies);

        Assert.Equal(2, files.Count);
        Assert.Equal(3, result.Imported);
        Assert.Equal((3 + 2 + 5) * 8000, result.ImportedGil);
        Assert.Single(SubmarineTrackerImporter.FindDatabases(dir, includeConflictCopies: false));
    }

    [Fact]
    public void An_unreadable_file_is_reported_and_the_rest_still_import()
    {
        using var ledger = new TempLedger();
        var dir = Path.Combine(ledger.Directory, "tracker");
        var good = CreateTrackerDatabase(dir, (1_780_000_000, FakeSectors.O, FakeItems.SalvagedRing, 3, 0, 0));
        var bad = Path.Combine(dir, "submarine-sqlite.sync-conflict-broken.db");
        File.WriteAllText(bad, "not a database");

        var data = SubmarineTrackerImporter.Read([good, bad], ledger.Directory);

        Assert.Single(data.Voyages);
        Assert.Single(data.Warnings);
        Assert.Equal(1, data.FilesRead);
    }

    [Fact]
    public void The_original_file_is_left_untouched()
    {
        using var ledger = new TempLedger();
        var path = CreateTrackerDatabase(Path.Combine(ledger.Directory, "tracker"), (1_780_000_000, FakeSectors.O, FakeItems.SalvagedRing, 3, 0, 0));
        var before = File.GetLastWriteTimeUtc(path);
        var size = new FileInfo(path).Length;

        SubmarineTrackerImporter.Read([path], ledger.Directory);

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Equal(size, new FileInfo(path).Length);
        Assert.Empty(Directory.GetDirectories(ledger.Directory, "st-import-*"));
    }

    [Theory]
    [InlineData(new byte[] { 0x05 }, 5UL)]
    [InlineData(new byte[] { 0xCC, 0xFF }, 255UL)]
    [InlineData(new byte[] { 0xCD, 0x01, 0x00 }, 256UL)]
    [InlineData(new byte[] { 0xCE, 0x00, 0x01, 0x00, 0x00 }, 65536UL)]
    [InlineData(new byte[] { 0xCF, 0x80, 0x00, 0x00, 0x00, 0x12, 0x34, 0x56, 0x78 }, 0x8000_0000_1234_5678UL)]
    public void Free_company_ids_are_decoded_from_messagepack(byte[] blob, ulong expected)
        => Assert.Equal(expected, SubmarineTrackerImporter.DecodeUInt64(blob));
}
