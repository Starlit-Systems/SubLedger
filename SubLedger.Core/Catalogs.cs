namespace SubLedger.Core;

public sealed record ItemInfo(string Name, long VendorPrice);

/// <summary>Item names and NPC vendor sell prices. The plugin answers from the game's own item data.</summary>
public interface IItemCatalog
{
    ItemInfo? Get(uint itemId);
}

/// <summary>Submarine sector letters and names.</summary>
public interface ISectorCatalog
{
    /// <summary>The sector's map letter, for example "O", or an empty string when unknown.</summary>
    string Letter(uint sectorId);

    string Name(uint sectorId);
}

public static class Valuation
{
    /// <summary>
    /// What one item sells for at an NPC vendor. High-quality items sell for 10% more, rounded up.
    /// </summary>
    public static long UnitPrice(long vendorPrice, bool hq)
        => hq ? (vendorPrice * 11 + 9) / 10 : vendorPrice;
}
