using SubLedger.Core.Capture;
using SubLedger.Core.Model;
using SubLedger.Core.Storage;

namespace SubLedger.Core;

/// <summary>An unchanging view of the ledger for the UI and analytics. Deleted records are left out.</summary>
public sealed record LedgerSnapshot(
    long Version,
    IReadOnlyList<Voyage> Voyages,
    IReadOnlyList<FreeCompany> FreeCompanies,
    IReadOnlyList<SubmarineState> Submarines,
    IReadOnlyList<LedgerEntry> Entries)
{
    public static readonly LedgerSnapshot Empty = new(0, [], [], [], []);
}

public sealed record ImportResult(int Imported, int Duplicates, int Empty, long ImportedGil);

/// <summary>
/// The ledger: prices incoming voyages, refuses duplicates, stores them, and keeps an in-memory copy for reading.
/// Thread-safe. Knows nothing about the game or the UI.
/// </summary>
public sealed class LedgerStore
{
    /// <summary>Two results for one submarine this close together are the same collection.</summary>
    public const long SameCollectionSeconds = 600;

    /// <summary>A return time this far past the collection time still counts as "already returned" (clock differences).</summary>
    public const long ReturnToleranceSeconds = 900;

    private readonly object gate = new();
    private readonly LedgerDatabase db;
    private readonly IItemCatalog items;
    private readonly ISectorCatalog sectors;
    private readonly LedgerLog log;
    private readonly string deviceId;

    private readonly List<Voyage> voyages = [];
    private readonly Dictionary<(ulong, uint), List<Voyage>> voyagesBySub = [];
    private readonly Dictionary<ulong, FreeCompany> freeCompanies = [];
    private readonly Dictionary<ulong, Character> characters = [];
    private readonly Dictionary<(ulong, uint), SubmarineState> submarines = [];
    private readonly List<LedgerEntry> entries = [];

    private long version;
    private LedgerSnapshot? cached;

    public LedgerStore(LedgerDatabase db, IItemCatalog items, ISectorCatalog sectors, LedgerLog log, string deviceId)
    {
        this.db = db;
        this.items = items;
        this.sectors = sectors;
        this.log = log;
        this.deviceId = deviceId;
    }

    public LedgerDatabase Database => db;

    /// <summary>Changes whenever the ledger's contents change.</summary>
    public long Version
    {
        get
        {
            lock (gate)
                return version;
        }
    }

    /// <summary>Reads the whole ledger from disk, replacing what is in memory.</summary>
    public void Load()
    {
        lock (gate)
        {
            db.Initialize();
            var data = db.LoadAll();

            voyages.Clear();
            voyagesBySub.Clear();
            freeCompanies.Clear();
            characters.Clear();
            submarines.Clear();
            entries.Clear();

            foreach (var v in data.Voyages)
                AddToMemory(v);
            foreach (var fc in data.FreeCompanies)
                freeCompanies[fc.FcId] = fc;
            foreach (var c in data.Characters)
                characters[c.ContentId] = c;
            foreach (var s in data.Submarines)
                submarines[(s.FcId, s.Register)] = s;
            entries.AddRange(data.Entries);
            Touch();
        }
    }

    public LedgerSnapshot Snapshot()
    {
        lock (gate)
        {
            return cached ??= new LedgerSnapshot(
                version,
                [.. voyages.Where(v => !v.Deleted).OrderByDescending(v => v.CollectedUnix)],
                [.. freeCompanies.Values.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)],
                [.. submarines.Values.OrderBy(s => s.Register)],
                [.. entries.Where(e => !e.Deleted)]);
        }
    }

    /// <summary>
    /// Records a voyage result captured live from the game, unless it is one the ledger already holds.
    /// </summary>
    public RecordResult RecordVoyage(VoyageCapture capture)
    {
        lock (gate)
        {
            var result = Evaluate(capture, out var consumesPending);
            if (!result.Recorded || result.Voyage is null)
                return result;

            db.InsertVoyages([result.Voyage]);
            AddToMemory(result.Voyage);

            if (consumesPending && submarines.TryGetValue((capture.FcId, capture.SubRegister), out var state))
            {
                state.PendingConsumed = true;
                db.UpsertSubmarines([state], deviceId, Now());
            }

            Touch();
            return result;
        }
    }

    /// <summary>
    /// Adds voyages from another source (SubmarineTracker). Voyages the ledger already holds are skipped, so running
    /// the same import again, or importing something SubLedger also captured live, never counts income twice.
    /// </summary>
    public ImportResult ImportVoyages(IReadOnlyList<VoyageCapture> captures, IReadOnlyList<FreeCompany> companies)
    {
        lock (gate)
        {
            var added = new List<Voyage>();
            int duplicates = 0, empty = 0;
            long gil = 0;

            foreach (var capture in captures.OrderBy(c => c.CollectedUnix))
            {
                var result = Evaluate(capture, out _);
                switch (result.Outcome)
                {
                    case RecordOutcome.Recorded when result.Voyage is not null:
                        // Held in memory straight away so later rows in this same batch are checked against it.
                        AddToMemory(result.Voyage);
                        added.Add(result.Voyage);
                        gil += result.Voyage.TotalGil;
                        break;
                    case RecordOutcome.Empty:
                        empty++;
                        break;
                    default:
                        duplicates++;
                        break;
                }
            }

            try
            {
                db.InsertVoyages(added);
            }
            catch
            {
                foreach (var v in added)
                    RemoveFromMemory(v);
                throw;
            }

            var now = Now();
            var changedCompanies = new List<FreeCompany>();
            foreach (var incoming in companies)
            {
                if (incoming.FcId == 0)
                    continue;

                if (!freeCompanies.TryGetValue(incoming.FcId, out var known))
                {
                    known = new FreeCompany
                    {
                        FcId = incoming.FcId,
                        Name = incoming.Name,
                        Tag = incoming.Tag,
                        World = incoming.World,
                        FirstSeenUnix = now,
                        LastSeenUnix = now,
                    };
                    freeCompanies[known.FcId] = known;
                    changedCompanies.Add(known);
                    continue;
                }

                // An import only fills blanks; what the game itself reported wins.
                var changed = false;
                if (string.IsNullOrWhiteSpace(known.Name) && !string.IsNullOrWhiteSpace(incoming.Name)) { known.Name = incoming.Name; changed = true; }
                if (string.IsNullOrWhiteSpace(known.Tag) && !string.IsNullOrWhiteSpace(incoming.Tag)) { known.Tag = incoming.Tag; changed = true; }
                if (string.IsNullOrWhiteSpace(known.World) && !string.IsNullOrWhiteSpace(incoming.World)) { known.World = incoming.World; changed = true; }
                if (changed)
                    changedCompanies.Add(known);
            }

            db.UpsertFreeCompanies(changedCompanies, deviceId, now);
            Touch();
            return new ImportResult(added.Count, duplicates, empty, gil);
        }
    }

    /// <summary>Prices a capture and decides whether it is new. Does not store anything.</summary>
    private RecordResult Evaluate(VoyageCapture capture, out bool consumesPending)
    {
        consumesPending = false;

        var visited = capture.Sectors.Where(s => s.SectorId != 0).ToList();
        if (visited.Count == 0)
            return new RecordResult(RecordOutcome.Empty, null, "the result held no sectors");

        var key = (capture.FcId, capture.SubRegister);
        submarines.TryGetValue(key, out var state);
        voyagesBySub.TryGetValue(key, out var known);

        // Which voyage is this? The game clears the submarine's return time just before showing the result, so the
        // answer is normally the return time last seen while the submarine was still out.
        long returnUnix = 0, dispatchedUnix = 0;
        if (capture.Source == VoyageSource.Capture)
        {
            var latest = capture.CollectedUnix + ReturnToleranceSeconds;
            if (capture.LiveReturnUnix != 0 && capture.LiveReturnUnix <= latest)
                returnUnix = capture.LiveReturnUnix;
            else if (state is { PendingReturnUnix: not 0, PendingConsumed: false } && state.PendingReturnUnix <= latest)
                returnUnix = state.PendingReturnUnix;

            if (returnUnix != 0 && state is not null && state.PendingReturnUnix == returnUnix)
            {
                consumesPending = true;
                if (state.PendingDispatchUnix != 0 && state.PendingDispatchUnix < returnUnix)
                    dispatchedUnix = state.PendingDispatchUnix;
            }
        }

        var fingerprint = capture.Fingerprint();

        if (known is not null)
        {
            if (returnUnix != 0 && known.Any(v => v.ReturnUnix == returnUnix))
                return new RecordResult(RecordOutcome.DuplicateVoyage, null, "this voyage is already in the ledger");

            if (known.Any(v => Math.Abs(v.CollectedUnix - capture.CollectedUnix) <= SameCollectionSeconds))
                return new RecordResult(RecordOutcome.DuplicateCollection, null, "this collection is already in the ledger");

            if (returnUnix == 0 && capture.Source == VoyageSource.Capture)
            {
                // No way to tell which voyage this is by time. A report identical to the submarine's last recorded
                // voyage is that voyage's log being looked at again.
                var last = known.Where(v => v.CollectedUnix <= capture.CollectedUnix).MaxBy(v => v.CollectedUnix);
                if (last is not null && last.Fingerprint == fingerprint)
                    return new RecordResult(RecordOutcome.DuplicateReview, null, "identical to this submarine's last recorded voyage (an old voyage log opened again)");
            }
        }

        var loot = new List<LootLine>();
        var unpriced = new List<uint>();
        foreach (var sector in visited)
        {
            AddLoot(loot, unpriced, sector.SectorId, 0, sector.Primary);
            AddLoot(loot, unpriced, sector.SectorId, 1, sector.Additional);
        }

        if (unpriced.Count > 0)
            log.Warning("Valuation", $"No item data for item id(s) {string.Join(", ", unpriced.Distinct())}; valued at 0 gil.");

        var now = Now();
        var name = !string.IsNullOrWhiteSpace(capture.SubName) ? capture.SubName : state?.Name ?? string.Empty;
        var voyage = new Voyage
        {
            FcId = capture.FcId,
            SubRegister = capture.SubRegister,
            SubName = name,
            CharacterId = capture.CharacterId,
            Source = capture.Source,
            Trigger = capture.Trigger,
            Route = string.Concat(visited.Select(s => RouteLetter(s.SectorId))),
            SectorIds = [.. visited.Select(s => s.SectorId)],
            DispatchedUnix = dispatchedUnix,
            ReturnUnix = returnUnix,
            CollectedUnix = capture.CollectedUnix,
            TotalGil = loot.Sum(l => l.TotalGil),
            Fingerprint = fingerprint,
            DeviceId = deviceId,
            CreatedUnix = now,
            UpdatedUnix = now,
            Loot = loot,
        };

        return new RecordResult(RecordOutcome.Recorded, voyage, string.Empty);
    }

    private void AddLoot(List<LootLine> loot, List<uint> unpriced, uint sectorId, int slot, CapturedItem? item)
    {
        if (item is null || item.ItemId == 0 || item.Quantity <= 0)
            return;

        var info = items.Get(item.ItemId);
        if (info is null)
            unpriced.Add(item.ItemId);

        loot.Add(new LootLine(
            sectorId, slot, item.ItemId,
            info?.Name ?? $"Item #{item.ItemId}",
            item.Quantity, item.Hq,
            Valuation.UnitPrice(info?.VendorPrice ?? 0, item.Hq)));
    }

    private string RouteLetter(uint sectorId)
    {
        var letter = sectors.Letter(sectorId);
        return string.IsNullOrEmpty(letter) ? "?" : letter;
    }

    /// <summary>Notes what the workshop shows for a submarine. Returns true when something was new.</summary>
    public bool ObserveSubmarine(SubObservation o)
    {
        lock (gate)
        {
            var key = (o.FcId, o.Register);
            var changed = false;
            if (!submarines.TryGetValue(key, out var state))
            {
                state = new SubmarineState { FcId = o.FcId, Register = o.Register };
                submarines[key] = state;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(o.Name) && state.Name != o.Name) { state.Name = o.Name; changed = true; }
            if (o.Rank != 0 && state.Rank != o.Rank) { state.Rank = o.Rank; changed = true; }

            // A return time of 0 is not stored: the game zeroes it at collection, a moment before the result that
            // needs it is read.
            if (o.ReturnUnix != 0 && o.ReturnUnix != state.PendingReturnUnix)
            {
                state.PendingReturnUnix = o.ReturnUnix;
                state.PendingSectors = o.Sectors;
                state.PendingDispatchUnix = o.LiveDispatch && o.ReturnUnix > o.NowUnix ? o.NowUnix : 0;
                state.PendingConsumed = voyagesBySub.TryGetValue(key, out var list) && list.Any(v => v.ReturnUnix == o.ReturnUnix);
                changed = true;
            }

            state.LastSeenUnix = o.NowUnix;
            if (!changed)
                return false;

            db.UpsertSubmarines([state], deviceId, Now());
            Touch();
            return true;
        }
    }

    public bool ObserveFreeCompany(ulong fcId, string name, string tag, string world, long nowUnix)
    {
        if (fcId == 0)
            return false;

        lock (gate)
        {
            var changed = false;
            if (!freeCompanies.TryGetValue(fcId, out var fc))
            {
                fc = new FreeCompany { FcId = fcId, FirstSeenUnix = nowUnix };
                freeCompanies[fcId] = fc;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(name) && fc.Name != name) { fc.Name = name; changed = true; }
            if (!string.IsNullOrWhiteSpace(tag) && fc.Tag != tag) { fc.Tag = tag; changed = true; }
            if (!string.IsNullOrWhiteSpace(world) && fc.World != world) { fc.World = world; changed = true; }
            fc.LastSeenUnix = nowUnix;
            if (!changed)
                return false;

            db.UpsertFreeCompanies([fc], deviceId, Now());
            Touch();
            return true;
        }
    }

    public bool ObserveCharacter(ulong contentId, string name, string world, ulong fcId, long nowUnix)
    {
        if (contentId == 0)
            return false;

        lock (gate)
        {
            var changed = false;
            if (!characters.TryGetValue(contentId, out var c))
            {
                c = new Character { ContentId = contentId };
                characters[contentId] = c;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(name) && c.Name != name) { c.Name = name; changed = true; }
            if (!string.IsNullOrWhiteSpace(world) && c.World != world) { c.World = world; changed = true; }
            if (fcId != 0 && c.FcId != fcId) { c.FcId = fcId; changed = true; }
            c.LastSeenUnix = nowUnix;
            if (!changed)
                return false;

            db.UpsertCharacter(c, deviceId, Now());
            return true;
        }
    }

    /// <summary>Hides a voyage from every total (or brings it back). It is kept on file so it is not captured again.</summary>
    public bool SetVoyageDeleted(string id, bool deleted)
    {
        lock (gate)
        {
            var voyage = voyages.FirstOrDefault(v => v.Id == id);
            if (voyage is null || voyage.Deleted == deleted)
                return false;

            var now = Now();
            db.SetVoyageDeleted(id, deleted, now);
            voyage.Deleted = deleted;
            voyage.UpdatedUnix = now;
            Touch();
            return true;
        }
    }

    public void AddEntries(IReadOnlyCollection<LedgerEntry> newEntries)
    {
        lock (gate)
        {
            db.InsertEntries(newEntries);
            entries.AddRange(newEntries);
            Touch();
        }
    }

    public bool SetEntryDeleted(string id, bool deleted)
    {
        lock (gate)
        {
            var entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is null || entry.Deleted == deleted)
                return false;

            var now = Now();
            db.SetEntryDeleted(id, deleted, now);
            entry.Deleted = deleted;
            entry.UpdatedUnix = now;
            Touch();
            return true;
        }
    }

    /// <summary>Removes every entry that came in with one import. Returns how many were removed.</summary>
    public int DeleteBatch(string batchId)
    {
        if (string.IsNullOrEmpty(batchId))
            return 0;

        lock (gate)
        {
            var batch = entries.Where(e => !e.Deleted && e.BatchId == batchId).ToList();
            if (batch.Count == 0)
                return 0;

            var now = Now();
            db.SetEntriesDeleted([.. batch.Select(e => e.Id)], true, now);
            foreach (var entry in batch)
            {
                entry.Deleted = true;
                entry.UpdatedUnix = now;
            }

            Touch();
            return batch.Count;
        }
    }

    public LedgerEntry NewEntry(LedgerEntryKind kind, DateOnly date, long amountGil, ulong fcId, string note, string source, string batchId = "", string category = "")
    {
        var now = Now();
        return new LedgerEntry
        {
            FcId = fcId,
            Kind = kind,
            BusinessDate = date,
            AmountGil = amountGil,
            Category = category,
            Note = note,
            Source = source,
            BatchId = batchId,
            DeviceId = deviceId,
            CreatedUnix = now,
            UpdatedUnix = now,
        };
    }

    /// <summary>Runs <paramref name="action"/> while nothing else can touch the ledger file (backups, restores).</summary>
    public void WithExclusiveAccess(Action action)
    {
        lock (gate)
            action();
    }

    private void AddToMemory(Voyage v)
    {
        voyages.Add(v);
        var key = (v.FcId, v.SubRegister);
        if (!voyagesBySub.TryGetValue(key, out var list))
            voyagesBySub[key] = list = [];
        list.Add(v);
    }

    private void RemoveFromMemory(Voyage v)
    {
        voyages.Remove(v);
        if (voyagesBySub.TryGetValue((v.FcId, v.SubRegister), out var list))
            list.Remove(v);
    }

    private void Touch()
    {
        version++;
        cached = null;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
