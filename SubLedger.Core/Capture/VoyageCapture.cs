using System.Text;
using SubLedger.Core.Model;

namespace SubLedger.Core.Capture;

public sealed record CapturedItem(uint ItemId, int Quantity, bool Hq);

public sealed record CapturedSector(uint SectorId, CapturedItem? Primary, CapturedItem? Additional);

/// <summary>A voyage result exactly as read from the game (or an import), before pricing and duplicate checks.</summary>
public sealed record VoyageCapture
{
    public required ulong FcId { get; init; }
    public required uint SubRegister { get; init; }
    public string SubName { get; init; } = string.Empty;
    public ulong CharacterId { get; init; }

    /// <summary>The return time the game still showed when the result was read; usually 0 by then.</summary>
    public long LiveReturnUnix { get; init; }
    public required long CollectedUnix { get; init; }
    public required IReadOnlyList<CapturedSector> Sectors { get; init; }
    public VoyageSource Source { get; init; } = VoyageSource.Capture;

    /// <summary>What noticed the result: "window", "packet" or an import name.</summary>
    public string Trigger { get; init; } = string.Empty;

    /// <summary>A stable text form of the haul, used to recognise the same report shown twice.</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var s in Sectors)
        {
            if (s.SectorId == 0)
                continue;
            if (sb.Length > 0)
                sb.Append('|');
            sb.Append(s.SectorId).Append(':');
            Append(sb, s.Primary);
            sb.Append('+');
            Append(sb, s.Additional);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, CapturedItem? item)
    {
        if (item is null || item.ItemId == 0)
        {
            sb.Append('-');
            return;
        }

        sb.Append(item.ItemId).Append('x').Append(item.Quantity);
        if (item.Hq)
            sb.Append('h');
    }
}

public enum RecordOutcome
{
    Recorded,

    /// <summary>A voyage with this submarine and return time is already in the ledger.</summary>
    DuplicateVoyage,

    /// <summary>This submarine already has a voyage collected at the same moment (second trigger, or the same voyage from another source).</summary>
    DuplicateCollection,

    /// <summary>The report matches the submarine's last recorded voyage: an old voyage log opened again.</summary>
    DuplicateReview,

    /// <summary>The result held no sectors.</summary>
    Empty,
}

public sealed record RecordResult(RecordOutcome Outcome, Voyage? Voyage, string Detail)
{
    public bool Recorded => Outcome == RecordOutcome.Recorded;
}

/// <summary>What the workshop shows for one submarine right now.</summary>
public sealed record SubObservation
{
    public required ulong FcId { get; init; }
    public required uint Register { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Rank { get; init; }
    public long ReturnUnix { get; init; }
    public uint[] Sectors { get; init; } = [];
    public long NowUnix { get; init; }

    /// <summary>True when this session watched the return time change, so "now" is the dispatch time.</summary>
    public bool LiveDispatch { get; init; }
}
