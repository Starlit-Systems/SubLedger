using System.Globalization;
using Microsoft.Data.Sqlite;
using SubLedger.Core.Model;

namespace SubLedger.Core.Storage;

public sealed record LedgerData(
    List<FreeCompany> FreeCompanies,
    List<Character> Characters,
    List<SubmarineState> Submarines,
    List<Voyage> Voyages,
    List<LedgerEntry> Entries);

/// <summary>
/// The SQLite file behind the ledger.
/// <para>
/// Every operation opens the file, does its work and closes it again, and the file uses a rollback journal rather
/// than a write-ahead log. Between operations the ledger is therefore one complete, closed file, which is what file
/// sync tools (Syncthing, cloud drives) need in order to copy it without producing conflict copies.
/// </para>
/// <para>
/// Every row carries the device that wrote it and when it last changed, and rows are flagged as deleted rather than
/// removed, so that ledgers from several computers can later be merged row by row.
/// </para>
/// </summary>
public sealed class LedgerDatabase
{
    public const int SchemaVersion = 1;

    public LedgerDatabase(string path)
    {
        Path = path;
    }

    public string Path { get; }

    private static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
        DefaultTimeout = 10,
    }.ToString();

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString(Path, SqliteOpenMode.ReadWriteCreate));
        try
        {
            conn.Open();
            Exec(conn, null, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
            return conn;
        }
        catch
        {
            // A damaged file fails here; the handle must be released so the file can be set aside.
            conn.Dispose();
            throw;
        }
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Creates the file and its tables if needed and brings an older file up to the current layout.</summary>
    public void Initialize()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var conn = Open();
        long version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"This ledger was written by a newer SubLedger (layout {version}, this version reads up to {SchemaVersion}). Update the plugin.");

        if (version < 1)
        {
            using var tx = conn.BeginTransaction();
            Exec(conn, tx, SchemaV1);
            Exec(conn, tx, "PRAGMA user_version = 1;");
            tx.Commit();
        }
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS free_companies (
            fc_id           INTEGER PRIMARY KEY,
            name            TEXT NOT NULL DEFAULT '',
            tag             TEXT NOT NULL DEFAULT '',
            world           TEXT NOT NULL DEFAULT '',
            first_seen_unix INTEGER NOT NULL DEFAULT 0,
            last_seen_unix  INTEGER NOT NULL DEFAULT 0,
            updated_unix    INTEGER NOT NULL DEFAULT 0,
            device_id       TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS characters (
            content_id     INTEGER PRIMARY KEY,
            name           TEXT NOT NULL DEFAULT '',
            world          TEXT NOT NULL DEFAULT '',
            fc_id          INTEGER NOT NULL DEFAULT 0,
            last_seen_unix INTEGER NOT NULL DEFAULT 0,
            updated_unix   INTEGER NOT NULL DEFAULT 0,
            device_id      TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS submarines (
            fc_id                 INTEGER NOT NULL,
            register_unix         INTEGER NOT NULL,
            name                  TEXT NOT NULL DEFAULT '',
            rank                  INTEGER NOT NULL DEFAULT 0,
            pending_return_unix   INTEGER NOT NULL DEFAULT 0,
            pending_dispatch_unix INTEGER NOT NULL DEFAULT 0,
            pending_sectors       TEXT NOT NULL DEFAULT '',
            pending_consumed      INTEGER NOT NULL DEFAULT 0,
            last_seen_unix        INTEGER NOT NULL DEFAULT 0,
            updated_unix          INTEGER NOT NULL DEFAULT 0,
            device_id             TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (fc_id, register_unix)
        );

        CREATE TABLE IF NOT EXISTS voyages (
            id                TEXT PRIMARY KEY,
            fc_id             INTEGER NOT NULL,
            sub_register_unix INTEGER NOT NULL,
            sub_name          TEXT NOT NULL DEFAULT '',
            character_id      INTEGER NOT NULL DEFAULT 0,
            source            INTEGER NOT NULL,
            capture_trigger   TEXT NOT NULL DEFAULT '',
            route             TEXT NOT NULL DEFAULT '',
            sector_ids        TEXT NOT NULL DEFAULT '',
            dispatched_unix   INTEGER NOT NULL DEFAULT 0,
            return_unix       INTEGER NOT NULL DEFAULT 0,
            collected_unix    INTEGER NOT NULL,
            total_gil         INTEGER NOT NULL,
            fingerprint       TEXT NOT NULL DEFAULT '',
            note              TEXT NOT NULL DEFAULT '',
            device_id         TEXT NOT NULL DEFAULT '',
            created_unix      INTEGER NOT NULL DEFAULT 0,
            updated_unix      INTEGER NOT NULL DEFAULT 0,
            deleted           INTEGER NOT NULL DEFAULT 0
        );

        -- One record per voyage: a submarine cannot return from two voyages at the same scheduled time.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_voyages_return
            ON voyages (fc_id, sub_register_unix, return_unix) WHERE return_unix <> 0;
        CREATE INDEX IF NOT EXISTS ix_voyages_collected ON voyages (collected_unix);
        CREATE INDEX IF NOT EXISTS ix_voyages_sub ON voyages (fc_id, sub_register_unix);

        CREATE TABLE IF NOT EXISTS voyage_loot (
            voyage_id  TEXT NOT NULL REFERENCES voyages(id) ON DELETE CASCADE,
            seq        INTEGER NOT NULL,
            sector_id  INTEGER NOT NULL,
            slot       INTEGER NOT NULL,
            item_id    INTEGER NOT NULL,
            item_name  TEXT NOT NULL DEFAULT '',
            quantity   INTEGER NOT NULL,
            hq         INTEGER NOT NULL DEFAULT 0,
            unit_price INTEGER NOT NULL,
            total_gil  INTEGER NOT NULL,
            PRIMARY KEY (voyage_id, seq)
        );

        -- Manual lines: historical day totals and corrections now; expenses, investments and milestones later.
        CREATE TABLE IF NOT EXISTS ledger_entries (
            id            TEXT PRIMARY KEY,
            fc_id         INTEGER NOT NULL DEFAULT 0,
            kind          INTEGER NOT NULL,
            business_date TEXT NOT NULL,
            amount_gil    INTEGER NOT NULL,
            category      TEXT NOT NULL DEFAULT '',
            note          TEXT NOT NULL DEFAULT '',
            source        TEXT NOT NULL DEFAULT '',
            batch_id      TEXT NOT NULL DEFAULT '',
            device_id     TEXT NOT NULL DEFAULT '',
            created_unix  INTEGER NOT NULL DEFAULT 0,
            updated_unix  INTEGER NOT NULL DEFAULT 0,
            deleted       INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_entries_date ON ledger_entries (business_date);
        """;

    /// <summary>Runs SQLite's own consistency check. Returns "ok" for a healthy file.</summary>
    public string QuickCheck() => QuickCheck(Path);

    public static string QuickCheck(string path)
    {
        using var conn = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Writes a complete, compacted copy of the ledger to <paramref name="destination"/>.</summary>
    public void BackupTo(string destination)
    {
        if (File.Exists(destination))
            File.Delete(destination);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $dest;";
        cmd.Parameters.AddWithValue("$dest", destination);
        cmd.ExecuteNonQuery();
    }

    public LedgerData LoadAll()
    {
        using var conn = Open();
        var data = new LedgerData([], [], [], [], []);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT fc_id, name, tag, world, first_seen_unix, last_seen_unix FROM free_companies;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.FreeCompanies.Add(new FreeCompany
                {
                    FcId = ToU64(r.GetInt64(0)),
                    Name = r.GetString(1),
                    Tag = r.GetString(2),
                    World = r.GetString(3),
                    FirstSeenUnix = r.GetInt64(4),
                    LastSeenUnix = r.GetInt64(5),
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT content_id, name, world, fc_id, last_seen_unix FROM characters;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Characters.Add(new Character
                {
                    ContentId = ToU64(r.GetInt64(0)),
                    Name = r.GetString(1),
                    World = r.GetString(2),
                    FcId = ToU64(r.GetInt64(3)),
                    LastSeenUnix = r.GetInt64(4),
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fc_id, register_unix, name, rank, pending_return_unix, pending_dispatch_unix,
                       pending_sectors, pending_consumed, last_seen_unix
                FROM submarines;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Submarines.Add(new SubmarineState
                {
                    FcId = ToU64(r.GetInt64(0)),
                    Register = (uint)r.GetInt64(1),
                    Name = r.GetString(2),
                    Rank = r.GetInt32(3),
                    PendingReturnUnix = r.GetInt64(4),
                    PendingDispatchUnix = r.GetInt64(5),
                    PendingSectors = ParseIds(r.GetString(6)),
                    PendingConsumed = r.GetInt64(7) != 0,
                    LastSeenUnix = r.GetInt64(8),
                });
            }
        }

        var loot = new Dictionary<string, List<LootLine>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT voyage_id, sector_id, slot, item_id, item_name, quantity, hq, unit_price
                FROM voyage_loot ORDER BY voyage_id, seq;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                if (!loot.TryGetValue(id, out var list))
                    loot[id] = list = [];
                list.Add(new LootLine(
                    (uint)r.GetInt64(1), r.GetInt32(2), (uint)r.GetInt64(3), r.GetString(4),
                    r.GetInt32(5), r.GetInt64(6) != 0, r.GetInt64(7)));
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, fc_id, sub_register_unix, sub_name, character_id, source, capture_trigger, route, sector_ids,
                       dispatched_unix, return_unix, collected_unix, total_gil, fingerprint, note, device_id,
                       created_unix, updated_unix, deleted
                FROM voyages ORDER BY collected_unix;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                data.Voyages.Add(new Voyage
                {
                    Id = id,
                    FcId = ToU64(r.GetInt64(1)),
                    SubRegister = (uint)r.GetInt64(2),
                    SubName = r.GetString(3),
                    CharacterId = ToU64(r.GetInt64(4)),
                    Source = (VoyageSource)r.GetInt32(5),
                    Trigger = r.GetString(6),
                    Route = r.GetString(7),
                    SectorIds = ParseIds(r.GetString(8)),
                    DispatchedUnix = r.GetInt64(9),
                    ReturnUnix = r.GetInt64(10),
                    CollectedUnix = r.GetInt64(11),
                    TotalGil = r.GetInt64(12),
                    Fingerprint = r.GetString(13),
                    Note = r.GetString(14),
                    DeviceId = r.GetString(15),
                    CreatedUnix = r.GetInt64(16),
                    UpdatedUnix = r.GetInt64(17),
                    Deleted = r.GetInt64(18) != 0,
                    Loot = loot.TryGetValue(id, out var lines) ? lines : [],
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, fc_id, kind, business_date, amount_gil, category, note, source, batch_id, device_id,
                       created_unix, updated_unix, deleted
                FROM ledger_entries ORDER BY business_date;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Entries.Add(new LedgerEntry
                {
                    Id = r.GetString(0),
                    FcId = ToU64(r.GetInt64(1)),
                    Kind = (LedgerEntryKind)r.GetInt32(2),
                    BusinessDate = DateOnly.ParseExact(r.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    AmountGil = r.GetInt64(4),
                    Category = r.GetString(5),
                    Note = r.GetString(6),
                    Source = r.GetString(7),
                    BatchId = r.GetString(8),
                    DeviceId = r.GetString(9),
                    CreatedUnix = r.GetInt64(10),
                    UpdatedUnix = r.GetInt64(11),
                    Deleted = r.GetInt64(12) != 0,
                });
            }
        }

        return data;
    }

    /// <summary>Writes voyages and their loot in one transaction: either all of them are stored or none.</summary>
    public void InsertVoyages(IReadOnlyCollection<Voyage> voyages)
    {
        if (voyages.Count == 0)
            return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using var insertVoyage = conn.CreateCommand();
        insertVoyage.Transaction = tx;
        insertVoyage.CommandText = """
            INSERT INTO voyages (id, fc_id, sub_register_unix, sub_name, character_id, source, capture_trigger, route,
                                 sector_ids, dispatched_unix, return_unix, collected_unix, total_gil, fingerprint, note,
                                 device_id, created_unix, updated_unix, deleted)
            VALUES ($id, $fc, $sub, $subName, $char, $source, $trigger, $route, $sectors, $dispatched, $return,
                    $collected, $total, $fingerprint, $note, $device, $created, $updated, $deleted);
            """;
        var vp = AddParameters(insertVoyage, "$id", "$fc", "$sub", "$subName", "$char", "$source", "$trigger", "$route",
            "$sectors", "$dispatched", "$return", "$collected", "$total", "$fingerprint", "$note", "$device", "$created",
            "$updated", "$deleted");

        using var insertLoot = conn.CreateCommand();
        insertLoot.Transaction = tx;
        insertLoot.CommandText = """
            INSERT INTO voyage_loot (voyage_id, seq, sector_id, slot, item_id, item_name, quantity, hq, unit_price, total_gil)
            VALUES ($voyage, $seq, $sector, $slot, $item, $name, $quantity, $hq, $unit, $total);
            """;
        var lp = AddParameters(insertLoot, "$voyage", "$seq", "$sector", "$slot", "$item", "$name", "$quantity", "$hq",
            "$unit", "$total");

        foreach (var v in voyages)
        {
            vp[0].Value = v.Id;
            vp[1].Value = ToI64(v.FcId);
            vp[2].Value = (long)v.SubRegister;
            vp[3].Value = v.SubName;
            vp[4].Value = ToI64(v.CharacterId);
            vp[5].Value = (int)v.Source;
            vp[6].Value = v.Trigger;
            vp[7].Value = v.Route;
            vp[8].Value = FormatIds(v.SectorIds);
            vp[9].Value = v.DispatchedUnix;
            vp[10].Value = v.ReturnUnix;
            vp[11].Value = v.CollectedUnix;
            vp[12].Value = v.TotalGil;
            vp[13].Value = v.Fingerprint;
            vp[14].Value = v.Note;
            vp[15].Value = v.DeviceId;
            vp[16].Value = v.CreatedUnix;
            vp[17].Value = v.UpdatedUnix;
            vp[18].Value = v.Deleted ? 1 : 0;
            insertVoyage.ExecuteNonQuery();

            var seq = 0;
            foreach (var line in v.Loot)
            {
                lp[0].Value = v.Id;
                lp[1].Value = seq++;
                lp[2].Value = (long)line.SectorId;
                lp[3].Value = line.Slot;
                lp[4].Value = (long)line.ItemId;
                lp[5].Value = line.ItemName;
                lp[6].Value = line.Quantity;
                lp[7].Value = line.Hq ? 1 : 0;
                lp[8].Value = line.UnitPrice;
                lp[9].Value = line.TotalGil;
                insertLoot.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public void SetVoyageDeleted(string id, bool deleted, long updatedUnix)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE voyages SET deleted = $deleted, updated_unix = $updated WHERE id = $id;";
        cmd.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", updatedUnix);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpsertFreeCompanies(IReadOnlyCollection<FreeCompany> companies, string deviceId, long nowUnix)
    {
        if (companies.Count == 0)
            return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO free_companies (fc_id, name, tag, world, first_seen_unix, last_seen_unix, updated_unix, device_id)
            VALUES ($id, $name, $tag, $world, $first, $last, $updated, $device)
            ON CONFLICT (fc_id) DO UPDATE SET
                name = excluded.name, tag = excluded.tag, world = excluded.world,
                last_seen_unix = excluded.last_seen_unix, updated_unix = excluded.updated_unix,
                device_id = excluded.device_id;
            """;
        var p = AddParameters(cmd, "$id", "$name", "$tag", "$world", "$first", "$last", "$updated", "$device");
        foreach (var fc in companies)
        {
            p[0].Value = ToI64(fc.FcId);
            p[1].Value = fc.Name;
            p[2].Value = fc.Tag;
            p[3].Value = fc.World;
            p[4].Value = fc.FirstSeenUnix;
            p[5].Value = fc.LastSeenUnix;
            p[6].Value = nowUnix;
            p[7].Value = deviceId;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void UpsertCharacter(Character character, string deviceId, long nowUnix)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO characters (content_id, name, world, fc_id, last_seen_unix, updated_unix, device_id)
            VALUES ($id, $name, $world, $fc, $last, $updated, $device)
            ON CONFLICT (content_id) DO UPDATE SET
                name = excluded.name, world = excluded.world, fc_id = excluded.fc_id,
                last_seen_unix = excluded.last_seen_unix, updated_unix = excluded.updated_unix,
                device_id = excluded.device_id;
            """;
        cmd.Parameters.AddWithValue("$id", ToI64(character.ContentId));
        cmd.Parameters.AddWithValue("$name", character.Name);
        cmd.Parameters.AddWithValue("$world", character.World);
        cmd.Parameters.AddWithValue("$fc", ToI64(character.FcId));
        cmd.Parameters.AddWithValue("$last", character.LastSeenUnix);
        cmd.Parameters.AddWithValue("$updated", nowUnix);
        cmd.Parameters.AddWithValue("$device", deviceId);
        cmd.ExecuteNonQuery();
    }

    public void UpsertSubmarines(IReadOnlyCollection<SubmarineState> submarines, string deviceId, long nowUnix)
    {
        if (submarines.Count == 0)
            return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO submarines (fc_id, register_unix, name, rank, pending_return_unix, pending_dispatch_unix,
                                    pending_sectors, pending_consumed, last_seen_unix, updated_unix, device_id)
            VALUES ($fc, $register, $name, $rank, $return, $dispatch, $sectors, $consumed, $last, $updated, $device)
            ON CONFLICT (fc_id, register_unix) DO UPDATE SET
                name = excluded.name, rank = excluded.rank,
                pending_return_unix = excluded.pending_return_unix,
                pending_dispatch_unix = excluded.pending_dispatch_unix,
                pending_sectors = excluded.pending_sectors,
                pending_consumed = excluded.pending_consumed,
                last_seen_unix = excluded.last_seen_unix,
                updated_unix = excluded.updated_unix,
                device_id = excluded.device_id;
            """;
        var p = AddParameters(cmd, "$fc", "$register", "$name", "$rank", "$return", "$dispatch", "$sectors", "$consumed",
            "$last", "$updated", "$device");
        foreach (var s in submarines)
        {
            p[0].Value = ToI64(s.FcId);
            p[1].Value = (long)s.Register;
            p[2].Value = s.Name;
            p[3].Value = s.Rank;
            p[4].Value = s.PendingReturnUnix;
            p[5].Value = s.PendingDispatchUnix;
            p[6].Value = FormatIds(s.PendingSectors);
            p[7].Value = s.PendingConsumed ? 1 : 0;
            p[8].Value = s.LastSeenUnix;
            p[9].Value = nowUnix;
            p[10].Value = deviceId;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void InsertEntries(IReadOnlyCollection<LedgerEntry> entries)
    {
        if (entries.Count == 0)
            return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ledger_entries (id, fc_id, kind, business_date, amount_gil, category, note, source, batch_id,
                                        device_id, created_unix, updated_unix, deleted)
            VALUES ($id, $fc, $kind, $date, $amount, $category, $note, $source, $batch, $device, $created, $updated, $deleted);
            """;
        var p = AddParameters(cmd, "$id", "$fc", "$kind", "$date", "$amount", "$category", "$note", "$source", "$batch",
            "$device", "$created", "$updated", "$deleted");
        foreach (var e in entries)
        {
            p[0].Value = e.Id;
            p[1].Value = ToI64(e.FcId);
            p[2].Value = (int)e.Kind;
            p[3].Value = e.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            p[4].Value = e.AmountGil;
            p[5].Value = e.Category;
            p[6].Value = e.Note;
            p[7].Value = e.Source;
            p[8].Value = e.BatchId;
            p[9].Value = e.DeviceId;
            p[10].Value = e.CreatedUnix;
            p[11].Value = e.UpdatedUnix;
            p[12].Value = e.Deleted ? 1 : 0;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void SetEntryDeleted(string id, bool deleted, long updatedUnix)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ledger_entries SET deleted = $deleted, updated_unix = $updated WHERE id = $id;";
        cmd.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", updatedUnix);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static SqliteParameter[] AddParameters(SqliteCommand cmd, params string[] names)
    {
        var result = new SqliteParameter[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = names[i];
            cmd.Parameters.Add(p);
            result[i] = p;
        }

        return result;
    }

    // SQLite integers are signed 64-bit; the game's 64-bit ids are unsigned. The bit pattern is kept as is.
    private static long ToI64(ulong value) => unchecked((long)value);

    private static ulong ToU64(long value) => unchecked((ulong)value);

    private static string FormatIds(uint[] ids) => string.Join(',', ids);

    private static uint[] ParseIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<uint>(parts.Length);
        foreach (var part in parts)
        {
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                result.Add(id);
        }

        return [.. result];
    }
}
