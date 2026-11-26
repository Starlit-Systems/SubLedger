using System.Globalization;
using SubLedger.Core.Storage;

namespace SubLedger.Core.Sync;

/// <summary>Another computer's copy of the ledger, as found in the sync folder.</summary>
public sealed record PeerInfo(string DeviceId, string DeviceName, DateTime UpdatedLocal, int Voyages, string FileName);

public sealed record SyncResult(IReadOnlyList<PeerInfo> Peers, MergeResult Merged, IReadOnlyList<string> Problems, bool Published);

/// <summary>
/// Keeps ledgers on several computers in step through a shared folder that a sync service (Google Drive, Dropbox,
/// OneDrive, Syncthing, a network share) copies between them.
/// <para>
/// Each computer writes exactly one file there, <c>subledger-sync-&lt;device&gt;.db</c>, a complete copy of its own
/// ledger, and only ever reads the others'. No two computers write the same file, so the sync service never has a
/// conflict to resolve, and a computer that is switched off simply contributes its last copy.
/// </para>
/// </summary>
public sealed class SyncFolder
{
    public const string Prefix = "subledger-sync-";
    public const string Extension = ".db";
    private const string MetaDeviceId = "sync_device_id";
    private const string MetaDeviceName = "sync_device_name";
    private const string MetaPublished = "sync_published_unix";

    private readonly Dictionary<string, DateTime> mergedVersions = [];

    public SyncFolder(string folder, string deviceId, string deviceName)
    {
        Folder = folder;
        DeviceId = deviceId;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? deviceId : deviceName.Trim();
    }

    public string Folder { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }

    public string OwnFile => Path.Combine(Folder, Prefix + DeviceId + Extension);

    /// <summary>Writes this computer's ledger to the folder: to a temporary name first, then swapped in whole.</summary>
    public void Publish(LedgerDatabase db)
    {
        Directory.CreateDirectory(Folder);
        db.SetMeta(MetaDeviceId, DeviceId);
        db.SetMeta(MetaDeviceName, DeviceName);
        db.SetMeta(MetaPublished, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        var temporary = OwnFile + ".writing";
        db.BackupTo(temporary);
        File.Move(temporary, OwnFile, overwrite: true);
    }

    /// <summary>
    /// Reads every other computer's file and merges it into <paramref name="store"/>. A file unchanged since it was
    /// last merged is skipped unless <paramref name="force"/> is set.
    /// </summary>
    public SyncResult Pull(LedgerStore store, string scratchDirectory, bool force = false, Action? beforeFirstChange = null)
    {
        var peers = new List<PeerInfo>();
        var problems = new List<string>();
        int added = 0, entries = 0, removals = 0, restorations = 0, companies = 0, skipped = 0;
        var notified = false;

        if (!Directory.Exists(Folder))
            return new SyncResult(peers, new MergeResult(0, 0, 0, 0, 0, 0), [$"The sync folder {Folder} does not exist."], false);

        foreach (var file in Directory.GetFiles(Folder, Prefix + "*" + Extension))
        {
            var name = Path.GetFileName(file);
            var deviceId = name[Prefix.Length..^Extension.Length];
            if (string.Equals(deviceId, DeviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            var copyDir = Path.Combine(scratchDirectory, "sync-" + Guid.NewGuid().ToString("N"));
            try
            {
                var written = File.GetLastWriteTimeUtc(file);

                // A cloud client may hold the file open; work from a private copy.
                Directory.CreateDirectory(copyDir);
                var copy = Path.Combine(copyDir, name);
                File.Copy(file, copy, true);

                var check = LedgerDatabase.QuickCheck(copy);
                if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{name} is damaged or still being copied ({check}); it will be tried again later.");
                    continue;
                }

                var peerDb = new LedgerDatabase(copy);
                var data = peerDb.LoadAll();
                var deviceName = peerDb.GetMeta(MetaDeviceName) ?? deviceId;
                peers.Add(new PeerInfo(deviceId, deviceName, written.ToLocalTime(), data.Voyages.Count(v => !v.Deleted), name));

                if (!force && mergedVersions.TryGetValue(file, out var seen) && seen == written)
                    continue;

                if (!notified)
                {
                    beforeFirstChange?.Invoke();
                    notified = true;
                }

                var merged = store.MergeFrom(data);
                added += merged.VoyagesAdded;
                entries += merged.EntriesAdded;
                removals += merged.Removals;
                restorations += merged.Restorations;
                companies += merged.CompaniesUpdated;
                skipped += merged.Skipped;
                mergedVersions[file] = written;
            }
            catch (Exception ex)
            {
                problems.Add($"{name} could not be read: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(copyDir))
                        Directory.Delete(copyDir, true);
                }
                catch
                {
                    // A leftover scratch copy is harmless.
                }
            }
        }

        return new SyncResult(peers, new MergeResult(added, entries, removals, restorations, companies, skipped), problems, false);
    }
}
