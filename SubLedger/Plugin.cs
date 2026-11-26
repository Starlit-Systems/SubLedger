using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using SubLedger.Core;
using SubLedger.Core.Analytics;
using SubLedger.Core.Capture;
using SubLedger.Core.Import;
using SubLedger.Core.Storage;
using SubLedger.Game;
using SubLedger.Ui;

namespace SubLedger;

public sealed class Plugin : IDalamudPlugin
{
    public const string Name = "SubLedger";
    public const string RepoUrl = "https://github.com/starlit-systems/SubLedger";
    private const string CommandName = "/subledger";

    private readonly WindowSystem windowSystem = new(Name);
    private readonly VoyageCaptureService? capture;
    private BusinessCalendar? calendar;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        Config = Service.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Log = new LedgerLog();
        Log.LineAdded += MirrorToDalamudLog;
        Log.Info("Plugin", $"SubLedger {Version} starting.");

        ConfigDirectory = Service.PluginInterface.GetPluginConfigDirectory();
        var database = new LedgerDatabase(Path.Combine(ConfigDirectory, "subledger.db"));
        Sectors = new GameSectorCatalog();
        Store = new LedgerStore(database, new GameItemCatalog(), Sectors, Log, DeviceId());
        Backups = new BackupManager(database, Path.Combine(ConfigDirectory, "backups"), Log);
        Worker = new SerialWorker(Log);

        OpenLedger();

        Assets = new UiAssets();
        MainWindow = new MainWindow(this);
        windowSystem.AddWindow(MainWindow);

        if (LedgerError is null)
        {
            capture = new VoyageCaptureService(this);
            Worker.Enqueue("Daily backup", () => Store.WithExclusiveAccess(() => Backups.EnsureDaily(Config.BackupsToKeep)));
            if (Config.SubmarineTrackerSync)
                Worker.Enqueue("SubmarineTracker sync", () => ImportSubmarineTracker(automatic: true));
        }

        Service.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open SubLedger. /subledger voyages | console | settings | about",
        });

        Service.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Service.PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        Service.PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
    }

    public string Version { get; }
    public Configuration Config { get; }
    public LedgerLog Log { get; }
    public LedgerStore Store { get; }
    public BackupManager Backups { get; }
    public SerialWorker Worker { get; }
    public string ConfigDirectory { get; }
    public ISectorCatalog Sectors { get; }
    internal UiAssets Assets { get; }
    internal MainWindow MainWindow { get; }

    /// <summary>Set when the ledger could not be opened at all; nothing is recorded while this is set.</summary>
    public string? LedgerError { get; private set; }

    /// <summary>What the last SubmarineTracker import did, for the Settings page.</summary>
    public string ImportStatus { get; private set; } = string.Empty;
    public bool ImportRunning { get; private set; }

    public bool PacketTriggerAvailable => capture?.PacketTriggerAvailable ?? false;

    /// <summary>The time zone and day cutoff the user chose, rebuilt when either changes.</summary>
    public BusinessCalendar Calendar
    {
        get
        {
            var zone = ResolveTimeZone(Config.TimeZoneId);
            if (calendar is null || calendar.Zone.Id != zone.Id || calendar.CutoffMinutes != Config.DayCutoffMinutes)
                calendar = new BusinessCalendar(zone, Config.DayCutoffMinutes);
            return calendar;
        }
    }

    public string SubmarineTrackerDirectory
        => Path.Combine(Path.GetDirectoryName(ConfigDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? ConfigDirectory, "SubmarineTracker");

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Local;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>A short, stable id for this computer, so records say where they were written.</summary>
    private static string DeviceId()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private void OpenLedger()
    {
        try
        {
            Store.Load();
            var check = Store.Database.QuickCheck();
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"integrity check reported: {check}");

            var snapshot = Store.Snapshot();
            Log.Info("Ledger", $"Ledger opened: {snapshot.Voyages.Count} voyages, {snapshot.FreeCompanies.Count} Free Companies.");
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 11 or 26 })
        {
            // 11 = file is corrupt, 26 = file is not a database.
            Log.Error("Ledger", "The ledger file is damaged", ex);
        }
        catch (Exception ex)
        {
            // Anything else (a newer file layout, the database engine failing to load) is not fixed by a restore.
            LedgerError = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error("Ledger", "The ledger could not be opened. Nothing will be recorded until this is fixed", ex);
            return;
        }

        try
        {
            Backups.RecoverFromDamage();
            Store.Load();
            Log.Warning("Ledger", $"Ledger reopened after recovery: {Store.Snapshot().Voyages.Count} voyages.");
        }
        catch (Exception ex)
        {
            LedgerError = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error("Ledger", "Recovery failed. Nothing will be recorded until this is fixed", ex);
        }
    }

    /// <summary>Stores a captured result and reports what happened. Runs on the worker thread.</summary>
    internal void RecordCapture(VoyageCapture captured)
    {
        var result = Store.RecordVoyage(captured);
        switch (result.Outcome)
        {
            case RecordOutcome.Recorded when result.Voyage is { } v:
                var identified = v.ReturnUnix != 0 ? string.Empty : " (return time was not seen; identified by its contents)";
                Log.Info("Ledger", $"Recorded '{v.SubName}' route {v.Route}: {GilFormat.Exact(v.TotalGil)} gil, via {captured.Trigger}{identified}.");
                if (Config.AnnounceInChat)
                    Service.ChatGui.Print($"[SubLedger] {v.SubName} ({v.Route}): {GilFormat.Exact(v.TotalGil)} gil recorded.");
                break;
            case RecordOutcome.DuplicateCollection:
            case RecordOutcome.DuplicateVoyage:
                // The normal case for the second of the two triggers.
                Log.Verbose("Ledger", $"'{captured.SubName}' via {captured.Trigger}: not recorded again, {result.Detail}.");
                break;
            default:
                Log.Debug("Ledger", $"'{captured.SubName}' via {captured.Trigger}: not recorded, {result.Detail}.");
                break;
        }
    }

    /// <summary>Queues a SubmarineTracker import; the result appears in the console and on the Settings page.</summary>
    public void StartSubmarineTrackerImport()
    {
        if (ImportRunning)
            return;

        ImportRunning = true;
        ImportStatus = "Importing...";
        Worker.Enqueue("SubmarineTracker import", () =>
        {
            try
            {
                ImportSubmarineTracker(automatic: false);
            }
            finally
            {
                ImportRunning = false;
            }
        });
    }

    private void ImportSubmarineTracker(bool automatic)
    {
        try
        {
            var files = SubmarineTrackerImporter.FindDatabases(SubmarineTrackerDirectory, Config.SubmarineTrackerIncludeConflictCopies);
            if (files.Count == 0)
            {
                ImportStatus = "No SubmarineTracker data was found on this computer.";
                if (!automatic)
                    Log.Warning("Import", $"{ImportStatus} Looked in {SubmarineTrackerDirectory}.");
                return;
            }

            if (!automatic)
                Store.WithExclusiveAccess(() => Backups.Create("before-import"));

            var data = SubmarineTrackerImporter.Read(files, Path.Combine(Path.GetTempPath(), "SubLedger"));
            foreach (var warning in data.Warnings)
                Log.Warning("Import", $"SubmarineTracker file skipped: {warning}");

            var result = Store.ImportVoyages(data.Voyages, data.FreeCompanies);
            ImportStatus = result.Imported == 0
                ? $"Nothing new: all {result.Duplicates} SubmarineTracker voyages are already in the ledger."
                : $"Imported {result.Imported} voyages worth {GilFormat.Exact(result.ImportedGil)} gil ({result.Duplicates} were already in the ledger).";

            if (result.Imported > 0 || !automatic)
                Log.Info("Import", $"SubmarineTracker: {ImportStatus} Read {data.FilesRead} file(s), {data.LootRows} rows.");
            else
                Log.Debug("Import", $"SubmarineTracker sync: {ImportStatus}");
        }
        catch (Exception ex)
        {
            ImportStatus = $"Import failed: {ex.Message}";
            Log.Error("Import", "SubmarineTracker import failed", ex);
        }
    }

    public void BackupNow()
    {
        Worker.Enqueue("Manual backup", () => Store.WithExclusiveAccess(() => Backups.Create("manual")));
    }

    public void RestoreBackup(string path)
    {
        Worker.Enqueue("Restore backup", () =>
        {
            Store.WithExclusiveAccess(() => Backups.Restore(path));
            Store.Load();
            Log.Info("Ledger", $"Ledger reloaded: {Store.Snapshot().Voyages.Count} voyages.");
        });
    }

    private static void MirrorToDalamudLog(LogLine line)
    {
        var text = $"[{line.Source}] {line.Message}";
        switch (line.Level)
        {
            case LogLevel.Error: Service.Log.Error(text); break;
            case LogLevel.Warning: Service.Log.Warning(text); break;
            case LogLevel.Info: Service.Log.Information(text); break;
            case LogLevel.Debug: Service.Log.Debug(text); break;
            default: Service.Log.Verbose(text); break;
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "voyages": MainWindow.ShowPage(Page.Voyages); break;
            case "console" or "log": MainWindow.ShowPage(Page.Console); break;
            case "settings" or "config": MainWindow.ShowPage(Page.Settings); break;
            case "updates": MainWindow.ShowPage(Page.Updates); break;
            case "about": MainWindow.ShowPage(Page.About); break;
            default: MainWindow.Toggle(); break;
        }
    }

    private void OpenMain() => MainWindow.IsOpen = true;

    private void OpenSettings() => MainWindow.ShowPage(Page.Settings);

    public void Dispose()
    {
        Service.PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        Service.PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        Service.CommandManager.RemoveHandler(CommandName);

        capture?.Dispose();

        // Lets a voyage that is still being written finish before the plugin unloads.
        Worker.Dispose();

        windowSystem.RemoveAllWindows();
        Assets.Dispose();
        Log.LineAdded -= MirrorToDalamudLog;
    }
}
