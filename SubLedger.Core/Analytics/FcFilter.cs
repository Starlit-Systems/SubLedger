namespace SubLedger.Core.Analytics;

/// <summary>
/// Which Free Companies a view covers: all of them, or any chosen set. Every figure in the ledger can be worked out
/// for any combination.
/// <para>
/// Records not tied to a Free Company (a hand-kept total entered before any Free Company was known, say) only appear
/// when all Free Companies are shown, since they cannot be said to belong to any chosen one.
/// </para>
/// </summary>
public readonly struct FcFilter : IEquatable<FcFilter>
{
    private readonly ulong[]? ids;

    private FcFilter(ulong[] ids) => this.ids = ids;

    /// <summary>Every Free Company.</summary>
    public static FcFilter All => default;

    public static FcFilter Of(IEnumerable<ulong> fcIds)
    {
        var chosen = fcIds.Where(id => id != 0).Distinct().Order().ToArray();
        return chosen.Length == 0 ? All : new FcFilter(chosen);
    }

    /// <summary>A single Free Company; 0 means all of them.</summary>
    public static implicit operator FcFilter(ulong fcId) => fcId == 0 ? All : new FcFilter([fcId]);

    public bool IsAll => ids is null;

    /// <summary>The chosen Free Companies, in a stable order; empty when all are shown.</summary>
    public IReadOnlyList<ulong> Ids => ids ?? [];

    /// <summary>The one chosen Free Company, or 0 when the filter is all of them or several.</summary>
    public ulong Single => ids is { Length: 1 } ? ids[0] : 0;

    public bool Includes(ulong fcId) => ids is null || Array.IndexOf(ids, fcId) >= 0;

    public bool Equals(FcFilter other)
        => ids is null ? other.ids is null : other.ids is not null && ids.AsSpan().SequenceEqual(other.ids);

    public override bool Equals(object? obj) => obj is FcFilter other && Equals(other);

    public override int GetHashCode()
    {
        if (ids is null)
            return 0;

        var hash = new HashCode();
        foreach (var id in ids)
            hash.Add(id);
        return hash.ToHashCode();
    }

    public static bool operator ==(FcFilter a, FcFilter b) => a.Equals(b);

    public static bool operator !=(FcFilter a, FcFilter b) => !a.Equals(b);
}
