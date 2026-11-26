using System.Globalization;

namespace SubLedger.Core.Storage;

public sealed record BackupInfo(string Path, string FileName, DateTime CreatedLocal, long SizeBytes, string Reason);

/// <summary>
/// Dated copies of the ledger file, and getting the ledger back from one.
/// Callers must make sure nothing else is writing the ledger while a method here runs
/// (<see cref="LedgerStore.WithExclusiveAccess"/>).
/// </summary>
public sealed class BackupManager
{
    private const string Prefix = "subledger-";
    private const string Stamp = "yyyyMMdd-HHmmss";

    private readonly LedgerDatabase db;
    private readonly LedgerLog log;

    public BackupManager(LedgerDatabase db, string backupDirectory, LedgerLog log)
    {
        this.db = db;
        this.log = log;
        Directory = backupDirectory;
    }

    public string Directory { get; }

    /// <summary>Writes a backup now. <paramref name="reason"/> becomes part of the file name ("daily", "manual", ...).</summary>
    public BackupInfo? Create(string reason)
    {
        if (!File.Exists(db.Path))
            return null;

        System.IO.Directory.CreateDirectory(Directory);
        var name = $"{Prefix}{DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture)}-{Sanitize(reason)}.db";
        var path = Path.Combine(Directory, name);
        db.BackupTo(path);
        log.Info("Backup", $"Backup written: {name}");
        return Describe(path);
    }

    /// <summary>Makes one backup per calendar day and removes the oldest automatic ones beyond <paramref name="keep"/>.</summary>
    public void EnsureDaily(int keep)
    {
        var today = DateTime.Now.Date;
        var all = List();
        if (!all.Any(b => b.Reason == "daily" && b.CreatedLocal.Date == today))
            Create("daily");

        var daily = List().Where(b => b.Reason == "daily").OrderByDescending(b => b.CreatedLocal).ToList();
        foreach (var old in daily.Skip(Math.Max(1, keep)))
        {
            try
            {
                File.Delete(old.Path);
            }
            catch (Exception ex)
            {
                log.Warning("Backup", $"Could not remove old backup {old.FileName}: {ex.Message}");
            }
        }
    }

    /// <summary>Newest first.</summary>
    public List<BackupInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];

        return [.. System.IO.Directory.GetFiles(Directory, Prefix + "*.db")
            .Select(Describe)
            .OrderByDescending(b => b.CreatedLocal)];
    }

    /// <summary>
    /// Replaces the ledger with a backup. The backup is checked first, and the ledger being replaced is itself
    /// backed up, so a restore can be undone.
    /// </summary>
    public void Restore(string backupPath)
    {
        var check = LedgerDatabase.QuickCheck(backupPath);
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"That backup is damaged ({check}); nothing was changed.");

        if (File.Exists(db.Path))
        {
            try
            {
                Create("before-restore");
            }
            catch (Exception ex)
            {
                // The current file may be the damaged one; keep a raw copy instead.
                var aside = db.Path + $".replaced-{DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture)}";
                File.Copy(db.Path, aside, true);
                log.Warning("Backup", $"Current ledger could not be backed up cleanly ({ex.Message}); raw copy kept as {Path.GetFileName(aside)}.");
            }
        }

        File.Copy(backupPath, db.Path, true);
        var journal = db.Path + "-journal";
        if (File.Exists(journal))
            File.Delete(journal);
        log.Info("Backup", $"Ledger restored from {Path.GetFileName(backupPath)}.");
    }

    /// <summary>
    /// Called when the ledger will not open or fails its check: sets the damaged file aside and restores the newest
    /// healthy backup. Returns the backup used, or null when there was none (the ledger then starts empty).
    /// </summary>
    public BackupInfo? RecoverFromDamage()
    {
        if (File.Exists(db.Path))
        {
            var aside = db.Path + $".damaged-{DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture)}";
            File.Move(db.Path, aside, true);
            log.Error("Backup", $"The ledger file is damaged. It was set aside as {Path.GetFileName(aside)}.");
        }

        var journal = db.Path + "-journal";
        if (File.Exists(journal))
            File.Delete(journal);

        foreach (var backup in List())
        {
            try
            {
                if (!string.Equals(LedgerDatabase.QuickCheck(backup.Path), "ok", StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(backup.Path, db.Path, true);
                log.Warning("Backup", $"Ledger recovered from backup {backup.FileName}. Voyages recorded after that backup are missing.");
                return backup;
            }
            catch (Exception ex)
            {
                log.Warning("Backup", $"Backup {backup.FileName} could not be used: {ex.Message}");
            }
        }

        log.Error("Backup", "No healthy backup was found. The ledger starts empty.");
        return null;
    }

    private static BackupInfo Describe(string path)
    {
        var file = new FileInfo(path);
        var stem = Path.GetFileNameWithoutExtension(path)[Prefix.Length..];
        var created = file.LastWriteTime;
        var reason = string.Empty;
        if (stem.Length >= Stamp.Length
            && DateTime.TryParseExact(stem[..Stamp.Length], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            created = parsed;
            reason = stem.Length > Stamp.Length + 1 ? stem[(Stamp.Length + 1)..] : string.Empty;
        }

        return new BackupInfo(path, file.Name, created, file.Exists ? file.Length : 0, reason);
    }

    private static string Sanitize(string reason)
    {
        var clean = new string([.. reason.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        return clean.Length == 0 ? "manual" : clean;
    }
}
