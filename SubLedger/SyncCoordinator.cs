using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin.Services;
using SubLedger.Core;
using SubLedger.Core.Sync;

namespace SubLedger;

/// <summary>
/// Runs the sync folder in the background: merges the other computers' ledgers in shortly after start-up and every
/// few minutes, and writes this computer's copy out a little while after anything changes. All the work happens on
/// the plugin's worker thread, one job at a time with everything else that writes the ledger.
/// </summary>
internal sealed class SyncCoordinator : IDisposable
{
    private static readonly TimeSpan PullEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PublishAfterQuiet = TimeSpan.FromSeconds(20);

    private readonly Plugin plugin;
    private readonly object gate = new();
    private SyncFolder? folder;
    private DateTime nextPull = DateTime.UtcNow.AddSeconds(15);
    private DateTime nextTick = DateTime.MinValue;
    private long publishedVersion = -1;
    private long seenVersion = -1;
    private DateTime versionChangedAt = DateTime.UtcNow;
    private bool backedUpThisSession;
    private bool running;

    public SyncCoordinator(Plugin plugin)
    {
        this.plugin = plugin;
        Service.Framework.Update += OnUpdate;
    }

    public bool Running => running;
    public string Status { get; private set; } = string.Empty;
    public bool StatusIsProblem { get; private set; }
    public DateTime? LastSyncLocal { get; private set; }
    public IReadOnlyList<PeerInfo> Peers { get; private set; } = [];

    public string DeviceName => string.IsNullOrWhiteSpace(plugin.Config.SyncDeviceName) ? Environment.MachineName : plugin.Config.SyncDeviceName.Trim();

    private bool Enabled => plugin.Config.SyncEnabled && !string.IsNullOrWhiteSpace(plugin.Config.SyncFolder) && plugin.LedgerError is null;

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextTick)
            return;
        nextTick = now.AddSeconds(5);

        if (!Enabled || running)
            return;

        var version = plugin.Store.Version;
        if (version != seenVersion)
        {
            seenVersion = version;
            versionChangedAt = now;
        }

        if (now >= nextPull)
            Run(pull: true, force: false);
        else if (version != publishedVersion && now - versionChangedAt >= PublishAfterQuiet)
            Run(pull: false, force: false);
    }

    /// <summary>Merges every other computer's file now, then writes this computer's.</summary>
    public void SyncNow() => Run(pull: true, force: true);

    private SyncFolder Folder()
    {
        lock (gate)
        {
            var path = plugin.Config.SyncFolder.Trim();
            if (folder is null || folder.Folder != path || folder.DeviceName != DeviceName)
                folder = new SyncFolder(path, plugin.DeviceId, DeviceName);
            return folder;
        }
    }

    private void Run(bool pull, bool force)
    {
        if (!Enabled || running)
            return;

        running = true;
        nextPull = DateTime.UtcNow + PullEvery;
        plugin.Worker.Enqueue(pull ? "Sync" : "Publish to sync folder", () =>
        {
            try
            {
                var sync = Folder();
                if (!Directory.Exists(sync.Folder))
                {
                    SetStatus($"The folder {sync.Folder} does not exist. Choose another, or check that your sync service is running.", problem: true);
                    return;
                }

                var summary = string.Empty;
                if (pull)
                {
                    var result = sync.Pull(plugin.Store, Path.Combine(Path.GetTempPath(), "SubLedger"), force, BackupOnce);
                    Peers = result.Peers;
                    foreach (var problem in result.Problems)
                        plugin.Log.Warning("Sync", problem);

                    var m = result.Merged;
                    if (m.Changed)
                    {
                        summary = $" Brought in {Plural(m.VoyagesAdded, "voyage")} and {Plural(m.EntriesAdded, "other record")}"
                            + (m.Removals + m.Restorations > 0 ? $", and {Plural(m.Removals + m.Restorations, "removal or restore")}." : ".");
                        plugin.Log.Info("Sync", $"Merged from {Plural(result.Peers.Count, "other computer")}:{summary}");
                    }
                    else
                    {
                        plugin.Log.Debug("Sync", $"Checked {Plural(result.Peers.Count, "other computer")}; nothing new.");
                    }
                }

                var version = plugin.Store.Version;
                plugin.Store.WithExclusiveAccess(() => sync.Publish(plugin.Store.Database));
                publishedVersion = version;
                LastSyncLocal = DateTime.Now;
                var others = Peers.Count == 0 ? "No other computers have written to this folder yet." : $"{Plural(Peers.Count, "other computer")} found.";
                SetStatus($"Last synced {LastSyncLocal:HH:mm}. {others}{summary}", problem: false);
                plugin.Log.Verbose("Sync", "This computer's ledger was written to the sync folder.");
            }
            catch (Exception ex)
            {
                SetStatus($"Sync failed: {ex.Message}", problem: true);
                plugin.Log.Error("Sync", "Sync failed", ex);
            }
            finally
            {
                running = false;
            }
        });
    }

    /// <summary>The first time another computer's records are merged in a session, the ledger is backed up first.</summary>
    private void BackupOnce()
    {
        if (backedUpThisSession)
            return;

        backedUpThisSession = true;
        plugin.Store.WithExclusiveAccess(() => plugin.Backups.Create("before-sync"));
    }

    private void SetStatus(string text, bool problem)
    {
        Status = text;
        StatusIsProblem = problem;
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>On unload, writes this computer's latest copy so the others see everything recorded this session.</summary>
    public void Dispose()
    {
        Service.Framework.Update -= OnUpdate;
        if (Enabled && plugin.Store.Version != publishedVersion && !running)
        {
            var sync = Folder();
            plugin.Worker.Enqueue("Final publish to sync folder", () =>
            {
                if (Directory.Exists(sync.Folder))
                    plugin.Store.WithExclusiveAccess(() => sync.Publish(plugin.Store.Database));
            });
        }
    }
}
