namespace SubLedger.Core.Model;

/// <summary>Where a voyage record came from.</summary>
public enum VoyageSource
{
    /// <summary>Captured live by SubLedger from the game's voyage result.</summary>
    Capture = 0,

    /// <summary>Imported from a SubmarineTracker database.</summary>
    SubmarineTracker = 1,
}

/// <summary>One stack of items a voyage brought back, with the vendor price it had when recorded.</summary>
public sealed record LootLine(uint SectorId, int Slot, uint ItemId, string ItemName, int Quantity, bool Hq, long UnitPrice)
{
    public long TotalGil => UnitPrice * Quantity;
}

/// <summary>One completed submarine voyage. Gil values are exact integers.</summary>
public sealed class Voyage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ulong FcId { get; init; }

    /// <summary>The submarine's registration time: the game's stable identity for a submarine, unchanged by renames.</summary>
    public uint SubRegister { get; init; }
    public string SubName { get; init; } = string.Empty;
    public ulong CharacterId { get; init; }
    public VoyageSource Source { get; init; }
    public string Trigger { get; init; } = string.Empty;

    /// <summary>Sector letters in visit order, for example "OJ".</summary>
    public string Route { get; init; } = string.Empty;
    public uint[] SectorIds { get; init; } = [];

    /// <summary>When the submarine was sent out, or 0 when SubLedger did not see the dispatch.</summary>
    public long DispatchedUnix { get; init; }

    /// <summary>When the game scheduled the submarine to return, or 0 when unknown.</summary>
    public long ReturnUnix { get; init; }

    /// <summary>When the loot was collected. Income counts on this day.</summary>
    public long CollectedUnix { get; init; }
    public long TotalGil { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public long CreatedUnix { get; init; }
    public long UpdatedUnix { get; set; }
    public bool Deleted { get; set; }
    public IReadOnlyList<LootLine> Loot { get; init; } = [];
}

public sealed class FreeCompany
{
    public ulong FcId { get; init; }
    public string Name { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public long FirstSeenUnix { get; set; }
    public long LastSeenUnix { get; set; }

    public string DisplayName
    {
        get
        {
            var label = !string.IsNullOrWhiteSpace(Name) ? Name
                : !string.IsNullOrWhiteSpace(Tag) ? $"«{Tag}»"
                : $"FC {FcId:X}";
            return string.IsNullOrWhiteSpace(World) ? label : $"{label} ({World})";
        }
    }
}

public sealed class Character
{
    public ulong ContentId { get; init; }
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public ulong FcId { get; set; }
    public long LastSeenUnix { get; set; }
}

/// <summary>What SubLedger last knew about a submarine, including the voyage it is (or was) out on.</summary>
public sealed class SubmarineState
{
    public ulong FcId { get; init; }
    public uint Register { get; init; }
    public string Name { get; set; } = string.Empty;
    public int Rank { get; set; }

    /// <summary>Scheduled return of the last voyage seen under way.</summary>
    public long PendingReturnUnix { get; set; }

    /// <summary>When that voyage was sent out, if SubLedger watched it leave.</summary>
    public long PendingDispatchUnix { get; set; }
    public uint[] PendingSectors { get; set; } = [];

    /// <summary>True once that voyage's result has been recorded.</summary>
    public bool PendingConsumed { get; set; }
    public long LastSeenUnix { get; set; }
}

public enum LedgerEntryKind
{
    /// <summary>A hand-kept total for one day, from before automatic capture.</summary>
    HistoricalDay = 0,

    /// <summary>A manual correction, positive or negative, added on top of the day's income.</summary>
    Adjustment = 1,

    /// <summary>A running cost of keeping the submarines going (fuel, repair materials). Amount is the gil spent, positive.</summary>
    Expense = 2,

    /// <summary>A one-off purchase the fleet has to earn back (submarine parts, the workshop, the plot). Amount is positive.</summary>
    Investment = 3,
}

/// <summary>The named kinds of spending offered when an expense is entered.</summary>
public static class ExpenseCategories
{
    public static readonly (string Name, LedgerEntryKind Kind)[] All =
    [
        ("Submarine parts", LedgerEntryKind.Investment),
        ("Free Company house", LedgerEntryKind.Investment),
        ("Housing plot", LedgerEntryKind.Investment),
        ("Workshop", LedgerEntryKind.Investment),
        ("Other investment", LedgerEntryKind.Investment),
        ("Ceruleum tanks", LedgerEntryKind.Expense),
        ("Repair materials", LedgerEntryKind.Expense),
        ("Other running cost", LedgerEntryKind.Expense),
    ];

    public static IEnumerable<string> For(LedgerEntryKind kind) => All.Where(c => c.Kind == kind).Select(c => c.Name);

    public static bool IsCost(LedgerEntryKind kind) => kind is LedgerEntryKind.Expense or LedgerEntryKind.Investment;
}

/// <summary>A manual line in the ledger: a historical day total, a correction, or (later) an expense.</summary>
public sealed class LedgerEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The Free Company this belongs to, or 0 when not tied to one.</summary>
    public ulong FcId { get; init; }
    public LedgerEntryKind Kind { get; init; }
    public DateOnly BusinessDate { get; init; }
    public long AmountGil { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string BatchId { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public long CreatedUnix { get; init; }
    public long UpdatedUnix { get; set; }
    public bool Deleted { get; set; }
}
