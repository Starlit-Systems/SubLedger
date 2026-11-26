using System;
using Dalamud.Configuration;

namespace SubLedger;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Windows time zone id used to decide which day income belongs to. Empty means this computer's zone.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>Minutes after midnight at which a new ledger day starts.</summary>
    public int DayCutoffMinutes { get; set; }

    /// <summary>Show 1.05m instead of 1,047,250 in headline figures. The stored values are always exact.</summary>
    public bool AbbreviateGil { get; set; }

    /// <summary>Record results when the voyage result window opens.</summary>
    public bool CaptureFromWindow { get; set; } = true;

    /// <summary>Also record results from the game's voyage result message, in case the window is skipped.</summary>
    public bool CaptureFromPacket { get; set; } = true;

    public bool AnnounceInChat { get; set; }

    /// <summary>Number of automatic daily backups to keep.</summary>
    public int BackupsToKeep { get; set; } = 14;

    /// <summary>Re-read SubmarineTracker's history at startup to fill any gaps. Turned on by the first import.</summary>
    public bool SubmarineTrackerSync { get; set; }

    public bool SubmarineTrackerIncludeConflictCopies { get; set; } = true;

    /// <summary>The user chose not to import SubmarineTracker history; stop offering it on the dashboard.</summary>
    public bool SubmarineTrackerOfferDismissed { get; set; }

    /// <summary>Keep this ledger in step with other computers through a shared folder.</summary>
    public bool SyncEnabled { get; set; }

    /// <summary>The shared folder, kept in step between computers by a sync service.</summary>
    public string SyncFolder { get; set; } = string.Empty;

    /// <summary>How this computer is named to the others. Empty means the Windows computer name.</summary>
    public string SyncDeviceName { get; set; } = string.Empty;

    /// <summary>Free Company shown in the window; 0 shows all of them combined.</summary>
    public ulong SelectedFreeCompany { get; set; }

    /// <summary>The newest version whose notes the user has opened the Updates page for.</summary>
    public string LastSeenChangelog { get; set; } = string.Empty;

    public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
