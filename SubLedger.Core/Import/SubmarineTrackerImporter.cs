using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SubLedger.Core.Capture;
using SubLedger.Core.Model;

namespace SubLedger.Core.Import;

public sealed record SubmarineTrackerData(
    List<VoyageCapture> Voyages,
    List<FreeCompany> FreeCompanies,
    int LootRows,
    int FilesRead,
    List<string> Warnings);

/// <summary>
/// Reads voyage history out of a SubmarineTracker database (https://github.com/Infiziert90/SubmarineTracker, MIT).
/// SubmarineTracker stores one row per sector of each voyage; rows sharing a Free Company, submarine and collection
/// time are one voyage. Only a throwaway copy of the file is opened, so the original is never touched or locked.
/// </summary>
public static class SubmarineTrackerImporter
{
    public const string DatabaseFileName = "submarine-sqlite.db";

    /// <summary>The database plus any conflict copies a file-sync tool left beside it (they can hold voyages the main file lacks).</summary>
    public static List<string> FindDatabases(string directory, bool includeConflictCopies)
    {
        var result = new List<string>();
        var main = Path.Combine(directory, DatabaseFileName);
        if (File.Exists(main))
            result.Add(main);

        if (includeConflictCopies && Directory.Exists(directory))
        {
            result.AddRange(Directory.GetFiles(directory, "submarine-sqlite*.db")
                .Where(f => !string.Equals(f, main, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    public static SubmarineTrackerData Read(IReadOnlyList<string> databasePaths, string scratchDirectory)
    {
        var data = new SubmarineTrackerData([], [], 0, 0, []);
        var voyages = new Dictionary<(ulong, uint, long), List<CapturedSector>>();
        var subNames = new Dictionary<(ulong, uint), string>();
        var companies = new Dictionary<ulong, FreeCompany>();
        var lootRows = 0;
        var filesRead = 0;

        foreach (var source in databasePaths)
        {
            var workDir = Path.Combine(scratchDirectory, "st-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(workDir);
                var copy = Path.Combine(workDir, DatabaseFileName);
                File.Copy(source, copy, true);

                // The write-ahead log holds the newest rows; without it the copy would be stale.
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    if (File.Exists(source + suffix))
                        File.Copy(source + suffix, copy + suffix, true);
                }

                lootRows += ReadFile(copy, voyages, subNames, companies);
                filesRead++;
            }
            catch (Exception ex)
            {
                data.Warnings.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workDir))
                        Directory.Delete(workDir, true);
                }
                catch
                {
                    // A leftover scratch folder is harmless.
                }
            }
        }

        foreach (var ((fcId, register, collected), sectorList) in voyages)
        {
            data.Voyages.Add(new VoyageCapture
            {
                FcId = fcId,
                SubRegister = register,
                SubName = subNames.GetValueOrDefault((fcId, register), string.Empty),
                CollectedUnix = collected,
                Sectors = sectorList,
                Source = VoyageSource.SubmarineTracker,
                Trigger = "SubmarineTracker",
            });
        }

        data.Voyages.Sort((a, b) => a.CollectedUnix.CompareTo(b.CollectedUnix));
        data.FreeCompanies.AddRange(companies.Values);
        return data with { LootRows = lootRows, FilesRead = filesRead };
    }

    private static int ReadFile(
        string path,
        Dictionary<(ulong, uint, long), List<CapturedSector>> voyages,
        Dictionary<(ulong, uint), string> subNames,
        Dictionary<ulong, FreeCompany> companies)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        var rows = 0;

        // Rows already seen in an earlier file (the main database and its conflict copies overlap almost entirely).
        var seenBefore = new HashSet<(ulong, uint, long)>(voyages.Keys);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT FreeCompanyId, SubmarineId, "Return", "Date", Sector,
                       PrimaryItem, PrimaryCount, PrimaryHQ, AdditionalItem, AdditionalCount, AdditionalHQ
                FROM loot ORDER BY "Return", rowid;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows++;
                var fcId = DecodeUInt64(r.GetValue(0));
                var register = (uint)ToInt64(r.GetValue(1));
                var collected = ToInt64(r.GetValue(2));
                if (collected == 0)
                    collected = ToInt64(r.GetValue(3));
                var sector = (uint)ToInt64(r.GetValue(4));
                if (fcId == 0 || collected == 0 || sector == 0)
                    continue;

                var key = (fcId, register, collected);
                if (seenBefore.Contains(key))
                    continue;

                if (!voyages.TryGetValue(key, out var list))
                    voyages[key] = list = [];

                list.Add(new CapturedSector(
                    sector,
                    Item(r.GetValue(5), r.GetValue(6), r.GetValue(7)),
                    Item(r.GetValue(8), r.GetValue(9), r.GetValue(10))));
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT FreeCompanyId, SubmarineId, Name FROM submarine;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fcId = DecodeUInt64(r.GetValue(0));
                var register = (uint)ToInt64(r.GetValue(1));
                var name = Convert.ToString(r.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty;
                if (fcId != 0 && !string.IsNullOrWhiteSpace(name))
                    subNames.TryAdd((fcId, register), name);
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT FreeCompanyId, FreeCompanyTag, World FROM freecompany;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fcId = DecodeUInt64(r.GetValue(0));
                if (fcId == 0 || companies.ContainsKey(fcId))
                    continue;

                companies[fcId] = new FreeCompany
                {
                    FcId = fcId,
                    Tag = Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                    World = Convert.ToString(r.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                };
            }
        }

        return rows;
    }

    private static CapturedItem? Item(object id, object count, object hq)
    {
        var itemId = (uint)ToInt64(id);
        var quantity = (int)ToInt64(count);
        return itemId == 0 || quantity <= 0 ? null : new CapturedItem(itemId, quantity, ToInt64(hq) != 0);
    }

    private static long ToInt64(object value) => value switch
    {
        null or DBNull => 0,
        long l => l,
        bool b => b ? 1 : 0,
        string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : bool.TryParse(s, out var flag) && flag ? 1 : 0,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// SubmarineTracker stores Free Company ids as a MessagePack-encoded unsigned integer in a blob column.
    /// </summary>
    internal static ulong DecodeUInt64(object value)
    {
        switch (value)
        {
            case long l:
                return unchecked((ulong)l);
            case byte[] { Length: > 0 } b:
                return b[0] switch
                {
                    <= 0x7F => b[0],
                    0xCC when b.Length >= 2 => b[1],
                    0xCD when b.Length >= 3 => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(1)),
                    0xCE when b.Length >= 5 => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(1)),
                    0xCF when b.Length >= 9 => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(1)),
                    _ => 0,
                };
            default:
                return 0;
        }
    }
}
