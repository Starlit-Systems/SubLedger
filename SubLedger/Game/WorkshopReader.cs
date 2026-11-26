using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using SubLedger.Core.Capture;

namespace SubLedger.Game;

/// <summary>
/// Read-only access to the Free Company workshop's submarine data. Nothing here writes to the game, calls a game
/// function that changes state, or keeps a pointer past the call that read it.
/// <para>
/// The memory layout and the meaning of slot 4 ("the submarine whose menu is open") follow SubmarineTracker
/// (Infiziert90, MIT), which has read voyage results this way for years.
/// </para>
/// </summary>
internal static unsafe class WorkshopReader
{
    /// <summary>Island Sanctuary fills the same workshop structure (since 6.4); its territories have this intended use.</summary>
    private const uint IslandSanctuaryIntendedUse = 49;

    public static ulong CurrentFreeCompanyId()
    {
        var proxy = InfoProxyFreeCompany.Instance();
        return proxy == null ? 0 : proxy->Id;
    }

    public static string CurrentFreeCompanyName()
    {
        var proxy = InfoProxyFreeCompany.Instance();
        return proxy == null ? string.Empty : proxy->NameString;
    }

    private static WorkshopTerritory* Workshop()
    {
        var housing = HousingManager.Instance();
        if (housing == null || housing->WorkshopTerritory == null)
            return null;

        var territory = Service.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Service.ClientState.TerritoryType);
        if (territory is { } row && row.TerritoryIntendedUse.RowId == IslandSanctuaryIntendedUse)
            return null;

        return housing->WorkshopTerritory;
    }

    public static bool IsInWorkshop() => Workshop() != null;

    /// <summary>Every registered submarine as the workshop shows it right now. Empty outside a workshop.</summary>
    public static List<SubObservation> ReadSubmarines(ulong fcId, long nowUnix)
    {
        var result = new List<SubObservation>();
        var workshop = Workshop();
        if (workshop == null || fcId == 0)
            return result;

        var subs = workshop->Submersible.Data;
        for (var i = 0; i < subs.Length; i++)
        {
            ref var sub = ref subs[i];
            if (sub.RankId == 0)
                continue;

            var sectors = new List<uint>(5);
            foreach (var point in sub.CurrentExplorationPoints)
            {
                if (point != 0)
                    sectors.Add(point);
            }

            result.Add(new SubObservation
            {
                FcId = fcId,
                Register = sub.RegisterTime,
                Name = ExtractName(sub.Name),
                Rank = sub.RankId,
                ReturnUnix = sub.ReturnTime,
                Sectors = [.. sectors],
                NowUnix = nowUnix,
            });
        }

        return result;
    }

    /// <summary>
    /// Reads the voyage result the game is presenting: the submarine whose menu is open and what each sector gave.
    /// Returns null when there is no result to read.
    /// </summary>
    public static VoyageCapture? ReadVoyageResult(string trigger)
    {
        var workshop = Workshop();
        if (workshop == null)
            return null;

        var fcId = CurrentFreeCompanyId();
        if (fcId == 0)
            return null;

        var current = workshop->Submersible.DataPointers[4].Value;
        if (current == null)
            return null;

        var gathered = current->GatheredData;
        if (gathered.Length == 0 || gathered[0].ItemIdPrimary == 0)
            return null;

        var sectors = new List<CapturedSector>(5);
        for (var i = 0; i < gathered.Length; i++)
        {
            ref var g = ref gathered[i];
            if (g.Point == 0)
                continue;

            sectors.Add(new CapturedSector(
                g.Point,
                g.ItemIdPrimary != 0 ? new CapturedItem(g.ItemIdPrimary, g.ItemCountPrimary, g.ItemHQPrimary) : null,
                g.ItemIdAdditional != 0 ? new CapturedItem(g.ItemIdAdditional, g.ItemCountAdditional, g.ItemHQAdditional) : null));
        }

        if (sectors.Count == 0)
            return null;

        return new VoyageCapture
        {
            FcId = fcId,
            SubRegister = current->RegisterTime,
            SubName = ExtractName(current->Name),
            CharacterId = Service.PlayerState.ContentId,
            LiveReturnUnix = current->ReturnTime,
            CollectedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Sectors = sectors,
            Trigger = trigger,
        };
    }

    /// <summary>Submarine names sit in a fixed, zero-padded buffer.</summary>
    private static string ExtractName(Span<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        var bytes = end >= 0 ? raw[..end] : raw;
        return new ReadOnlySeStringSpan(bytes).ExtractText();
    }
}
