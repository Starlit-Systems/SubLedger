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

    /// <summary>Ceruleum tanks the game charges for visiting this sector.</summary>
    int Tanks(uint sectorId);
}

/// <summary>
/// Submarine fuel. Ceruleum tanks cannot be bought with gil, only with company credits, so fuel is tracked in tanks
/// and credits and never counted as a gil cost.
/// </summary>
public static class Fuel
{
    /// <summary>Company credits per ceruleum tank at the Free Company credit exchange.</summary>
    public const int CreditsPerTank = 100;

    /// <summary>Tanks a voyage used: the sum of each visited sector's requirement, as the game charges it.</summary>
    public static int Tanks(Model.Voyage voyage, ISectorCatalog sectors)
    {
        var tanks = 0;
        foreach (var sector in voyage.SectorIds)
            tanks += sectors.Tanks(sector);
        return tanks;
    }

    public static long Tanks(IEnumerable<Model.Voyage> voyages, ISectorCatalog sectors)
    {
        long tanks = 0;
        foreach (var v in voyages)
        {
            if (!v.Deleted)
                tanks += Tanks(v, sectors);
        }

        return tanks;
    }
}

public static class Valuation
{
    /// <summary>
    /// What one item sells for at an NPC vendor. High-quality items sell for 10% more, rounded up.
    /// </summary>
    public static long UnitPrice(long vendorPrice, bool hq)
        => hq ? (vendorPrice * 11 + 9) / 10 : vendorPrice;
}
