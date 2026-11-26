using System.Collections.Concurrent;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using SubLedger.Core;

namespace SubLedger.Game;

/// <summary>Item names and vendor sell prices straight from the game's item sheet, so prices are never hardcoded.</summary>
internal sealed class GameItemCatalog : IItemCatalog
{
    private readonly ExcelSheet<Item> sheet = Service.DataManager.GetExcelSheet<Item>();
    private readonly ConcurrentDictionary<uint, ItemInfo?> cache = new();

    public ItemInfo? Get(uint itemId) => cache.GetOrAdd(itemId, id =>
        sheet.TryGetRow(id, out var row) ? new ItemInfo(row.Name.ExtractText(), row.PriceLow) : null);
}

/// <summary>Submarine sector letters ("O", "J") and names from the game's exploration sheet.</summary>
internal sealed class GameSectorCatalog : ISectorCatalog
{
    private readonly ExcelSheet<SubmarineExploration> sheet = Service.DataManager.GetExcelSheet<SubmarineExploration>();
    private readonly ConcurrentDictionary<uint, (string Letter, string Name, int Tanks)> cache = new();

    public string Letter(uint sectorId) => Lookup(sectorId).Letter;

    public string Name(uint sectorId) => Lookup(sectorId).Name;

    public int Tanks(uint sectorId) => Lookup(sectorId).Tanks;

    private (string Letter, string Name, int Tanks) Lookup(uint sectorId) => cache.GetOrAdd(sectorId, id =>
        sheet.TryGetRow(id, out var row)
            ? (row.Location.ExtractText(), row.Destination.ExtractText(), row.CeruleumTankReq)
            : (string.Empty, string.Empty, 0));
}
