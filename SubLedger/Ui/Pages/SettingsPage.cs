using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using SubLedger.Core.Import;
using SubLedger.Core.Storage;

namespace SubLedger.Ui.Pages;

internal sealed class SettingsPage
{
    private enum Tab
    {
        General,
        Capture,
        Data,
        Import,
        Manual,
        Sync,
    }

    private readonly Plugin plugin;
    private readonly ManualRecordsSection manualRecords;
    private Tab tab = Tab.General;
    private string zoneFilter = string.Empty;
    private IReadOnlyList<TimeZoneInfo>? zones;

    private List<BackupInfo> backups = [];
    private DateTime backupsListedAt = DateTime.MinValue;
    private DateTime trackerCheckedAt = DateTime.MinValue;
    private int trackerFiles;

    public SettingsPage(Plugin plugin)
    {
        this.plugin = plugin;
        manualRecords = new ManualRecordsSection(plugin);
    }

    public void Draw()
    {
        var assets = plugin.Assets;
        Widgets.PageHeader("Settings", "Changes apply straight away.", assets);

        var tabs = new[] { (Tab.General, "General"), (Tab.Capture, "Recording"), (Tab.Data, "Ledger & backups"), (Tab.Import, "SubmarineTracker"), (Tab.Manual, "Hand-kept records"), (Tab.Sync, "Sync") };
        for (var i = 0; i < tabs.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.S(8f));
            if (Widgets.FilterPill(tabs[i].Item2, Theme.Gold, tab == tabs[i].Item1, assets))
                tab = tabs[i].Item1;
        }

        Widgets.VSpace(8f);

        switch (tab)
        {
            case Tab.General: DrawGeneral(assets); break;
            case Tab.Capture: DrawCapture(assets); break;
            case Tab.Data: DrawData(assets); break;
            case Tab.Import: DrawImport(assets); break;
            case Tab.Manual: manualRecords.Draw(assets); break;
            case Tab.Sync: DrawSync(assets); break;
        }

        Widgets.VSpace(10f);
    }

    private void Save() => plugin.Config.Save();

    private void ToggleRow(string label, string description, bool value, Action<bool> set)
    {
        Widgets.SettingRow(label, description, Theme.S(42f), Theme.S(22f), () =>
        {
            var current = value;
            if (Widgets.Toggle(label, ref current))
            {
                set(current);
                Save();
            }
        });
    }

    private void DrawGeneral(UiAssets assets)
    {
        var config = plugin.Config;

        Widgets.SectionLabel("Days", assets);
        Widgets.BeginPanel();

        var calendar = plugin.Calendar;
        var zoneLabel = string.IsNullOrEmpty(config.TimeZoneId) ? $"This computer ({TimeZoneInfo.Local.DisplayName})" : calendar.Zone.DisplayName;
        Widgets.SettingRow("Time zone", "Decides which day a collection belongs to. Voyages are stored as exact moments, so changing this re-sorts past days too.",
            Theme.S(330f), ImGui.GetFrameHeight(), () =>
            {
                ImGui.SetNextItemWidth(Theme.S(330f));
                if (!ImGui.BeginCombo("##zone", zoneLabel, ImGuiComboFlags.HeightLarge))
                    return;

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##zoneFilter", "Type to filter", ref zoneFilter, 48);
                if (ImGui.Selectable($"This computer ({TimeZoneInfo.Local.DisplayName})", string.IsNullOrEmpty(config.TimeZoneId)))
                {
                    config.TimeZoneId = string.Empty;
                    Save();
                }

                zones ??= TimeZoneInfo.GetSystemTimeZones();
                foreach (var zone in zones)
                {
                    if (zoneFilter.Length > 0 && !zone.DisplayName.Contains(zoneFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable($"{zone.DisplayName}##{zone.Id}", config.TimeZoneId == zone.Id))
                    {
                        config.TimeZoneId = zone.Id;
                        Save();
                    }
                }

                ImGui.EndCombo();
            });

        Widgets.PanelDivider();

        var cutoff = TimeSpan.FromMinutes(config.DayCutoffMinutes);
        Widgets.SettingRow("A new day starts at", "Collections before this time count towards the previous day. Leave at 00:00 for ordinary calendar days.",
            Theme.S(168f), ImGui.GetFrameHeight(), () =>
            {
                var hour = cutoff.Hours;
                var minute = cutoff.Minutes;
                ImGui.SetNextItemWidth(Theme.S(74f));
                var changed = ImGui.DragInt("##cutoffHour", ref hour, 0.1f, 0, 23, "%02d h");
                ImGui.SameLine(0, Theme.S(8f));
                ImGui.SetNextItemWidth(Theme.S(74f));
                changed |= ImGui.DragInt("##cutoffMinute", ref minute, 0.25f, 0, 59, "%02d min");
                if (changed)
                {
                    config.DayCutoffMinutes = Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59);
                    Save();
                }
            });

        Widgets.PanelDivider();
        var now = DateTimeOffset.Now;
        Widgets.Text($"Right now it is {calendar.ToLocal(now.ToUnixTimeSeconds()).ToString("HH:mm", CultureInfo.InvariantCulture)} there, and the ledger day is {calendar.Today(now).ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture)}.", Theme.TextDim);
        Widgets.EndPanel();
        Widgets.VSpace(12f);

        Widgets.SectionLabel("Numbers", assets);
        Widgets.BeginPanel();
        ToggleRow("Shorten large amounts", "Show 1.05m instead of 1,047,250 in the dashboard's headline figures. The ledger always stores the exact amount, and hovering a figure shows it.",
            config.AbbreviateGil, v => config.AbbreviateGil = v);
        Widgets.EndPanel();
        Widgets.VSpace(12f);

        DrawFarming(assets);
    }

    private void DrawFarming(UiAssets assets)
    {
        var config = plugin.Config;
        Widgets.SectionLabel("Farming", assets);
        Widgets.BeginPanel();

        Widgets.SettingRow("Farming routes", "Voyages on these routes are farming rather than levelling. The Reports page can count from the day farming began, so levelling days do not drag the averages down. Separate routes with commas.",
            Theme.S(220f), ImGui.GetFrameHeight(), () =>
            {
                var text = config.FarmingRoutes;
                ImGui.SetNextItemWidth(Theme.S(220f));
                if (ImGui.InputTextWithHint("##farmingRoutes", Core.Analytics.FarmingPeriod.DefaultRoutes, ref text, 128))
                {
                    config.FarmingRoutes = text;
                    Save();
                }
            });

        Widgets.PanelDivider();

        var first = plugin.Farming(Core.Analytics.FarmingStart.FirstSubmarine);
        var all = plugin.Farming(Core.Analytics.FarmingStart.WholeFleet);
        if (first.IsEmpty)
        {
            Widgets.TextWrapped(first.Routes.Count == 0
                ? "No farming routes listed, so every day counts as levelling."
                : $"No submarine has come back from {string.Join(" or ", first.Routes)} yet.", Theme.TextDim);
        }
        else
        {
            var names = plugin.Store.Snapshot().FreeCompanies.ToDictionary(c => c.FcId, c => c.DisplayName);
            foreach (var (fcId, date) in first.Starts.OrderBy(kv => kv.Value))
            {
                var name = fcId == 0 ? "Submarines with no Free Company" : names.GetValueOrDefault(fcId, "Free Company");
                var allText = all.Starts.TryGetValue(fcId, out var allDate) ? allDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "-";
                Widgets.Text(name, Theme.Text);
                Widgets.Text($"First submarine farming: {date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}    All submarines farming: {allText}", Theme.TextDim);
            }
        }

        Widgets.EndPanel();
    }

    private void DrawCapture(UiAssets assets)
    {
        var config = plugin.Config;

        Widgets.SectionLabel("How voyages are noticed", assets);
        Widgets.BeginPanel();
        Widgets.Text("SubLedger notices a finished voyage in two independent ways and keeps only one record of it. Leave both on unless you are tracking down a problem.", Theme.TextDim);
        Widgets.PanelDivider();
        ToggleRow("When the voyage result window opens", "Reads the result as the game displays it.", config.CaptureFromWindow, v => config.CaptureFromWindow = v);
        Widgets.PanelDivider();
        ToggleRow("When the game receives the voyage result",
            plugin.PacketTriggerAvailable
                ? "Catches the result even if another plugin closes the window instantly."
                : "Not available right now: this trigger could not be set up with the current game version. The window trigger still works.",
            config.CaptureFromPacket, v => config.CaptureFromPacket = v);
        Widgets.EndPanel();
        Widgets.VSpace(12f);

        Widgets.SectionLabel("Feedback", assets);
        Widgets.BeginPanel();
        ToggleRow("Say so in chat", "Print a line in your chat log, visible only to you, each time a voyage is recorded.", config.AnnounceInChat, v => config.AnnounceInChat = v);
        Widgets.EndPanel();
    }

    private void DrawData(UiAssets assets)
    {
        var config = plugin.Config;
        if ((DateTime.UtcNow - backupsListedAt).TotalSeconds > 3)
        {
            backupsListedAt = DateTime.UtcNow;
            try
            {
                backups = plugin.Backups.List();
            }
            catch
            {
                backups = [];
            }
        }

        var snapshot = plugin.Store.Snapshot();
        var path = plugin.Store.Database.Path;
        var size = File.Exists(path) ? new FileInfo(path).Length : 0;

        Widgets.SectionLabel("The ledger file", assets);
        Widgets.BeginPanel();
        Widgets.Text($"{snapshot.Voyages.Count.ToString("N0", CultureInfo.InvariantCulture)} voyages · {FormatSize(size)}", Theme.Text);
        Widgets.Text(path, Theme.TextDim);
        Widgets.Text("One self-contained file, closed whenever SubLedger is not writing to it, so file-sync tools such as Syncthing can copy it safely.", Theme.TextDim);
        Widgets.VSpace(4f);
        if (Widgets.Button("Open folder", FontAwesomeIcon.FolderOpen))
            OpenFolder(plugin.ConfigDirectory);
        Widgets.EndPanel();
        Widgets.VSpace(12f);

        Widgets.SectionLabel("Backups", assets);
        Widgets.BeginPanel();
        Widgets.SettingRow("Daily backups to keep", "A dated copy is made the first time the plugin loads each day. Older ones beyond this number are removed.",
            Theme.S(110f), ImGui.GetFrameHeight(), () =>
            {
                var keep = config.BackupsToKeep;
                ImGui.SetNextItemWidth(Theme.S(110f));
                if (ImGui.DragInt("##keep", ref keep, 0.1f, 3, 120))
                {
                    config.BackupsToKeep = Math.Clamp(keep, 3, 120);
                    Save();
                }
            });
        Widgets.PanelDivider();
        if (Widgets.Button("Back up now", FontAwesomeIcon.Save, Widgets.ButtonKind.Primary))
        {
            plugin.BackupNow();
            backupsListedAt = DateTime.MinValue.AddSeconds(1);
        }

        ImGui.SameLine(0, Theme.S(8f));
        if (Widgets.Button("Open backups folder", FontAwesomeIcon.FolderOpen))
        {
            Directory.CreateDirectory(plugin.Backups.Directory);
            OpenFolder(plugin.Backups.Directory);
        }

        Widgets.VSpace(6f);
        if (backups.Count == 0)
        {
            Widgets.Text("No backups yet.", Theme.TextDim);
        }
        else
        {
            foreach (var backup in backups.Take(12))
            {
                var rowStart = ImGui.GetCursorScreenPos();
                ImGui.AlignTextToFramePadding();
                Widgets.Text(backup.CreatedLocal.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture), Theme.TextSoft);
                ImGui.SameLine(Theme.S(230f));
                Widgets.Text(DescribeReason(backup.Reason), Theme.TextDim);
                ImGui.SameLine(Theme.S(400f));
                Widgets.Text(FormatSize(backup.SizeBytes), Theme.TextDim);

                var buttonWidth = Theme.S(96f);
                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new Vector2(Widgets.PanelRight - buttonWidth, rowStart.Y));
                ImGui.PushID(backup.FileName);
                if (Widgets.Button("Restore", FontAwesomeIcon.Undo, Widgets.ButtonKind.Danger) && ImGui.GetIO().KeyCtrl)
                {
                    plugin.RestoreBackup(backup.Path);
                    backupsListedAt = DateTime.MinValue.AddSeconds(1);
                }

                Widgets.Tooltip("Hold Ctrl and click. The ledger goes back to how it was at this backup; the current ledger is backed up first, so this can be undone.");
                ImGui.PopID();
            }

            if (backups.Count > 12)
                Widgets.Text($"...and {backups.Count - 12} older, in the backups folder.", Theme.TextDim);
        }

        Widgets.EndPanel();
    }

    private void DrawImport(UiAssets assets)
    {
        var config = plugin.Config;
        if ((DateTime.UtcNow - trackerCheckedAt).TotalSeconds > 5)
        {
            trackerCheckedAt = DateTime.UtcNow;
            try
            {
                trackerFiles = SubmarineTrackerImporter.FindDatabases(plugin.SubmarineTrackerDirectory, config.SubmarineTrackerIncludeConflictCopies).Count;
            }
            catch
            {
                trackerFiles = 0;
            }
        }

        Widgets.SectionLabel("SubmarineTracker", assets);
        Widgets.BeginPanel();
        if (trackerFiles == 0)
        {
            Widgets.Text("No SubmarineTracker history was found on this computer.", Theme.TextSoft);
            Widgets.Text($"Looked in {plugin.SubmarineTrackerDirectory}", Theme.TextDim);
        }
        else
        {
            Widgets.Text("SubmarineTracker history found", Theme.Text);
            Widgets.Text(
                "Its past voyages can be copied into the ledger, valued at today's vendor prices. Importing is safe to repeat: voyages already in the ledger, "
                + "including ones SubLedger recorded itself, are skipped. SubmarineTracker's file is only read.",
                Theme.TextDim);
            Widgets.PanelDivider();
            ToggleRow("Check again every time the plugin loads", "Fills in anything SubLedger did not see for itself, for example voyages collected while it was switched off.",
                config.SubmarineTrackerSync, v => config.SubmarineTrackerSync = v);
            Widgets.PanelDivider();
            ToggleRow("Also read sync conflict copies", "File-sync tools sometimes leave extra copies of SubmarineTracker's file beside it. They can hold voyages the main file is missing.",
                config.SubmarineTrackerIncludeConflictCopies, v =>
                {
                    config.SubmarineTrackerIncludeConflictCopies = v;
                    trackerCheckedAt = DateTime.MinValue;
                });
            Widgets.PanelDivider();
            if (Widgets.Button(plugin.ImportRunning ? "Importing..." : "Import now", FontAwesomeIcon.FileImport, Widgets.ButtonKind.Primary, !plugin.ImportRunning))
                plugin.StartSubmarineTrackerImport();
            if (!string.IsNullOrEmpty(plugin.ImportStatus))
            {
                ImGui.SameLine(0, Theme.S(12f));
                ImGui.AlignTextToFramePadding();
                Widgets.Text(plugin.ImportStatus, plugin.ImportStatus.StartsWith("Import failed", StringComparison.Ordinal) ? Theme.Rose : Theme.Mint);
            }
        }

        Widgets.EndPanel();
    }

    private string syncFolderEdit = string.Empty;
    private string syncNameEdit = string.Empty;
    private bool syncFieldsLoaded;

    private void DrawSync(UiAssets assets)
    {
        var config = plugin.Config;
        var sync = plugin.Sync;
        if (!syncFieldsLoaded)
        {
            syncFolderEdit = config.SyncFolder;
            syncNameEdit = config.SyncDeviceName;
            syncFieldsLoaded = true;
        }

        Widgets.SectionLabel("Sync between computers", assets);
        Widgets.BeginPanel();
        Widgets.Text("Play on more than one computer? Keep one ledger across all of them.", Theme.Text);
        Widgets.Text(
            "Choose a folder that a sync service keeps the same on every computer, such as a folder inside Google Drive, Dropbox or OneDrive, "
            + "and choose the same folder on each computer. Each one writes its own copy of the ledger there and merges in the others', "
            + "so a voyage collected on the laptop appears on the desktop. A voyage is never counted twice, even if both computers imported the same history.",
            Theme.TextDim);
        Widgets.VSpace(2f);
        Widgets.Text(
            "Already copying the whole plugin settings folder between computers (Syncthing, for example)? Then the ledger is already shared and you do not need this.",
            Theme.TextDim);
        Widgets.PanelDivider();

        ToggleRow("Sync this ledger", "Turn on once a folder is chosen below.", config.SyncEnabled, v =>
        {
            config.SyncEnabled = v;
            if (v)
                plugin.Sync.SyncNow();
        });
        Widgets.PanelDivider();

        ImGui.AlignTextToFramePadding();
        Widgets.Text("Shared folder", Theme.Text);
        var buttonWidth = Theme.S(150f);
        ImGui.SetNextItemWidth(MathF.Max(Theme.S(200f), Widgets.PanelRight - ImGui.GetCursorScreenPos().X - buttonWidth - Theme.S(10f)));
        if (ImGui.InputTextWithHint("##syncFolder", @"e.g. C:\Users\you\Google Drive\SubLedger", ref syncFolderEdit, 400))
        {
            config.SyncFolder = syncFolderEdit.Trim();
            Save();
        }

        ImGui.SameLine(0, Theme.S(10f));
        if (Widgets.Button("Choose folder", FontAwesomeIcon.FolderOpen))
        {
            plugin.FileDialogs.OpenFolderDialog("Choose the shared folder", (ok, path) =>
            {
                if (!ok || string.IsNullOrWhiteSpace(path))
                    return;
                syncFolderEdit = path;
                config.SyncFolder = path.Trim();
                Save();
            });
        }

        if (config.SyncFolder.Length > 0 && !Directory.Exists(config.SyncFolder))
            Widgets.Text("That folder does not exist yet. Create it inside your sync service's folder, or choose another.", Theme.Amber);

        Widgets.VSpace(2f);
        ImGui.AlignTextToFramePadding();
        Widgets.Text("This computer's name", Theme.Text);
        ImGui.SameLine(0, Theme.S(10f));
        ImGui.SetNextItemWidth(Theme.S(240f));
        if (ImGui.InputTextWithHint("##syncName", Environment.MachineName, ref syncNameEdit, 40))
        {
            config.SyncDeviceName = syncNameEdit.Trim();
            Save();
        }

        ImGui.SameLine(0, Theme.S(10f));
        ImGui.AlignTextToFramePadding();
        Widgets.Text("How the other computers list this one.", Theme.TextDim);

        Widgets.PanelDivider();
        var ready = config.SyncEnabled && config.SyncFolder.Length > 0;
        if (Widgets.Button(sync.Running ? "Syncing..." : "Sync now", FontAwesomeIcon.SyncAlt, Widgets.ButtonKind.Primary, ready && !sync.Running))
            sync.SyncNow();
        ImGui.SameLine(0, Theme.S(12f));
        ImGui.AlignTextToFramePadding();
        if (!ready)
            Widgets.Text("Choose a folder and turn sync on first.", Theme.TextDim);
        else if (sync.Status.Length > 0)
            Widgets.Text(sync.Status, sync.StatusIsProblem ? Theme.Rose : Theme.Mint);
        else
            Widgets.Text("Syncs by itself a few seconds after start-up, every five minutes, and shortly after anything is recorded.", Theme.TextDim);

        if (sync.Peers.Count > 0)
        {
            Widgets.PanelDivider();
            Widgets.Text("Other computers in this folder", Theme.TextSoft);
            foreach (var peer in sync.Peers)
                Widgets.Text($"{peer.DeviceName} · {peer.Voyages.ToString("N0", CultureInfo.InvariantCulture)} voyages · last wrote {peer.UpdatedLocal.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}", Theme.TextDim);
        }

        Widgets.PanelDivider();
        Widgets.Text("The folder holds a full copy of your ledger, including character and Free Company names. Only share it with your own computers.", Theme.TextDim);
        Widgets.EndPanel();
    }

    private static string DescribeReason(string reason) => reason switch
    {
        "daily" => "Automatic, daily",
        "manual" => "Made by hand",
        "before-restore" => "Before a restore",
        "before-import" => "Before an import",
        "before-sync" => "Before a sync",
        "" => "Backup",
        _ => reason,
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
    };

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            plugin.Log.Warning("Settings", $"Could not open {path}: {ex.Message}");
        }
    }
}
