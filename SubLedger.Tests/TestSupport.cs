using SubLedger.Core;
using SubLedger.Core.Capture;
using SubLedger.Core.Storage;

namespace SubLedger.Tests;

internal sealed class FakeItems : IItemCatalog
{
    public const uint Ruby = 5187;
    public const uint PlatinumIngot = 9360;
    public const uint SalvagedRing = 22500;
    public const uint SalvagedBracelet = 22501;
    public const uint ExtravagantEarring = 22506;
    public const uint ExtravagantNecklace = 22507;

    private readonly Dictionary<uint, ItemInfo> items = new()
    {
        [Ruby] = new("Ruby", 2),
        [PlatinumIngot] = new("Platinum Ingot", 4),
        [SalvagedRing] = new("Salvaged Ring", 8000),
        [SalvagedBracelet] = new("Salvaged Bracelet", 9000),
        [ExtravagantEarring] = new("Extravagant Salvaged Earring", 30000),
        [ExtravagantNecklace] = new("Extravagant Salvaged Necklace", 34500),
    };

    public ItemInfo? Get(uint itemId) => items.GetValueOrDefault(itemId);
}

internal sealed class FakeSectors : ISectorCatalog
{
    public const uint J = 10;
    public const uint O = 15;

    public string Letter(uint sectorId) => sectorId switch { J => "J", O => "O", 18 => "R", 26 => "Z", _ => string.Empty };

    public string Name(uint sectorId) => $"Sector {sectorId}";

    // As in the game: O needs 6 tanks, J needs 3.
    public int Tanks(uint sectorId) => sectorId switch { O => 6, J => 3, _ => 1 };
}

/// <summary>A ledger in a throwaway folder, removed when the test ends.</summary>
internal sealed class TempLedger : IDisposable
{
    public const ulong Fc = 0x8000_0000_1234_5678; // high bit set on purpose: must survive the signed column
    public const uint Sub = 1_700_000_000;

    public TempLedger()
    {
        Directory = Path.Combine(Path.GetTempPath(), "subledger-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DbPath = Path.Combine(Directory, "subledger.db");
        Log = new LedgerLog();
        Store = Open();
    }

    public string Directory { get; }
    public string DbPath { get; }
    public LedgerLog Log { get; }
    public LedgerStore Store { get; private set; }

    public LedgerStore Open()
    {
        var store = new LedgerStore(new LedgerDatabase(DbPath), new FakeItems(), new FakeSectors(), Log, "test-device");
        store.Load();
        return store;
    }

    /// <summary>Throws the in-memory ledger away and reads the file again, like a plugin restart.</summary>
    public void Restart() => Store = Open();

    public static VoyageCapture OjHaul(long collectedUnix, int rings = 3, int earrings = 2, long liveReturn = 0, uint sub = Sub) => new()
    {
        FcId = Fc,
        SubRegister = sub,
        SubName = "Nautilus",
        CollectedUnix = collectedUnix,
        LiveReturnUnix = liveReturn,
        Trigger = "test",
        Sectors =
        [
            new CapturedSector(FakeSectors.O, new CapturedItem(FakeItems.Ruby, 20, false), new CapturedItem(FakeItems.SalvagedRing, rings, false)),
            new CapturedSector(FakeSectors.J, new CapturedItem(FakeItems.PlatinumIngot, 15, false), new CapturedItem(FakeItems.ExtravagantEarring, earrings, false)),
        ],
    };

    public static SubObservation Seen(long returnUnix, long now, bool live = false, uint sub = Sub) => new()
    {
        FcId = Fc,
        Register = sub,
        Name = "Nautilus",
        Rank = 90,
        ReturnUnix = returnUnix,
        Sectors = [FakeSectors.O, FakeSectors.J],
        NowUnix = now,
        LiveDispatch = live,
    };

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch
        {
            // Temp folder; the OS cleans it up eventually.
        }
    }
}
