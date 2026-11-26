using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using SubLedger.Core;
using SubLedger.Core.Analytics;
using SubLedger.Core.Capture;
using SubLedger.Core.Export;
using SubLedger.Core.Model;
using SubLedger.Core.Report;
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
            HelpMessage = "Open SubLedger. /subledger reports | expenses | voyages | console | settings | about",
        });

        Service.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Service.PluginInterface.UiBuilder.Draw += FileDialogs.Draw;
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

    /// <summary>The file picker used when loading a hand-kept list.</summary>
    public FileDialogManager FileDialogs { get; } = new();

    /// <summary>What the last export did, for the Reports page.</summary>
    public string ExportStatus { get; private set; } = string.Empty;
    public bool ExportRunning { get; private set; }

    /// <summary>Exports go to a SubLedger folder in the user's Documents, where they are easy to find.</summary>
    public string ExportDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SubLedger");

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

    private ExportContext NewExportContext()
    {
        var names = Store.Snapshot().FreeCompanies.ToDictionary(c => c.FcId, c => c.DisplayName);
        return new ExportContext(Calendar, names, Sectors, Version);
    }

    private static string ExportName(DateRange range, string suffix)
        => $"SubLedger {range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd} {suffix}";

    /// <summary>Writes the period's voyages, items and daily totals as three spreadsheet files.</summary>
    public void ExportCsv(RangeReport report, IReadOnlyList<Voyage> voyages, SortedDictionary<DateOnly, DayTotal> days)
        => RunExport("Export spreadsheets", () =>
        {
            var context = NewExportContext();
            // The byte-order mark makes Excel read the files as UTF-8, so names with accents survive.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(Path.Combine(ExportDirectory, ExportName(report.Range, "voyages.csv")), LedgerExport.VoyagesCsv(voyages, context), utf8);
            File.WriteAllText(Path.Combine(ExportDirectory, ExportName(report.Range, "items.csv")), LedgerExport.LootCsv(voyages, context), utf8);
            File.WriteAllText(Path.Combine(ExportDirectory, ExportName(report.Range, "daily.csv")), LedgerExport.DailyCsv(days, report.Range), utf8);
            var costs = Store.Snapshot().Entries.Where(e => ExpenseCategories.IsCost(e.Kind) && report.Range.Contains(e.BusinessDate)
                && (Config.SelectedFreeCompany == 0 || e.FcId == Config.SelectedFreeCompany)).ToList();
            if (costs.Count == 0)
                return $"Three spreadsheet files written to {ExportDirectory}";

            File.WriteAllText(Path.Combine(ExportDirectory, ExportName(report.Range, "expenses.csv")), LedgerExport.CostsCsv(costs, context), utf8);
            return $"Four spreadsheet files written to {ExportDirectory}";
        });

    /// <summary>Writes the period as one JSON document.</summary>
    public void ExportJson(RangeReport report, IReadOnlyList<Voyage> voyages, SortedDictionary<DateOnly, DayTotal> days)
        => RunExport("Export data file", () =>
        {
            var name = ExportName(report.Range, "data.json");
            var costs = Store.Snapshot().Entries.Where(e => Config.SelectedFreeCompany == 0 || e.FcId == Config.SelectedFreeCompany);
            File.WriteAllText(Path.Combine(ExportDirectory, name), LedgerExport.Json(report, voyages, days, NewExportContext(), DateTimeOffset.UtcNow, costs), new UTF8Encoding(false));
            return $"{name} written to {ExportDirectory}";
        });

    private void RunExport(string name, Func<string> export)
    {
        if (ExportRunning)
            return;

        ExportRunning = true;
        Worker.Enqueue(name, () =>
        {
            try
            {
                Directory.CreateDirectory(ExportDirectory);
                ExportStatus = export();
                Log.Info("Export", ExportStatus + ".");
            }
            catch (Exception ex)
            {
                ExportStatus = $"Export failed: {ex.Message}";
                Log.Error("Export", name + " failed", ex);
            }
            finally
            {
                ExportRunning = false;
            }
        });
    }

    /// <summary>Writes the self-contained HTML report and opens it in the default browser.</summary>
    public void OpenHtmlReport(DateRange range, RangePreset preset, PeriodGrouping grouping)
        => RunExport("HTML report", () =>
        {
            var calendar = Calendar;
            var today = calendar.Today(DateTimeOffset.Now);
            byte[]? logo = null;
            try
            {
                var directory = Service.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty;
                logo = File.ReadAllBytes(Path.Combine(directory, "Assets", "logo-small.png"));
            }
            catch (Exception ex)
            {
                Log.Debug("Export", $"Report logo not found: {ex.Message}");
            }

            var html = HtmlReport.Build(Store.Snapshot(), calendar, today, new ReportStart(range, preset, grouping, Config.SelectedFreeCompany), Version, logo, DateTimeOffset.Now);
            var name = $"SubLedger report {today:yyyy-MM-dd}.html";
            var path = Path.Combine(ExportDirectory, name);
            File.WriteAllText(path, html, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return $"{name} written to {ExportDirectory} and opened in your browser";
        });

    public void OpenExportFolder()
    {
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            Process.Start(new ProcessStartInfo(ExportDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning("Export", $"Could not open {ExportDirectory}: {ex.Message}");
        }
    }

    /// <summary>The Free Company a hand-entered record belongs to: the one being viewed, or the only one there is.</summary>
    public ulong ManualEntryFreeCompany()
    {
        if (Config.SelectedFreeCompany != 0)
            return Config.SelectedFreeCompany;

        var companies = Store.Snapshot().FreeCompanies;
        return companies.Count == 1 ? companies[0].FcId : 0;
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
            case "reports" or "report": MainWindow.ShowPage(Page.Reports); break;
            case "expenses" or "costs" or "payback": MainWindow.ShowPage(Page.Expenses); break;
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
        Service.PluginInterface.UiBuilder.Draw -= FileDialogs.Draw;
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
