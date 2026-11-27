using SubLedger.Core.Model;

namespace SubLedger.Core.Analytics;

/// <summary>Which day counts as the start of farming for a Free Company.</summary>
public enum FarmingStart
{
    /// <summary>The first day any of its submarines came back from a farming route.</summary>
    FirstSubmarine,

    /// <summary>The first day by which every one of its submarines had come back from a farming route.</summary>
    WholeFleet,
}

/// <summary>
/// When each Free Company's submarines started earning rather than levelling, judged by voyages on money routes such
/// as OJ or JORZ. Reports can count from then, so the weeks of low-value levelling voyages do not drag down the
/// averages.
/// <para>
/// Each Free Company has its own start, since a second fleet may have begun farming months after the first. Records
/// tied to no Free Company count from the earliest start. A Free Company that has never run a farming route has no
/// farming days at all. For <see cref="FarmingStart.WholeFleet"/>, submarines that have never run a farming route
/// (one still levelling, say) are not waited for.
/// </para>
/// </summary>
public sealed class FarmingPeriod
{
    /// <summary>The routes counted as farming unless the user lists others.</summary>
    public const string DefaultRoutes = "OJ, JORZ";

    private readonly Dictionary<ulong, DateOnly> starts;
    private readonly Dictionary<(ulong Fc, uint Sub), DateOnly> subStarts;

    private FarmingPeriod(FarmingStart rule, Dictionary<ulong, DateOnly> starts, Dictionary<(ulong, uint), DateOnly> subStarts, IReadOnlyList<string> routes)
    {
        Rule = rule;
        this.starts = starts;
        this.subStarts = subStarts;
        Routes = routes;
    }

    /// <summary>Each submarine's first voyage on a farming route, by Free Company and submarine.</summary>
    public IReadOnlyDictionary<(ulong Fc, uint Sub), DateOnly> SubmarineStarts => subStarts;

    public FarmingStart Rule { get; }

    /// <summary>The day farming began, for each Free Company that has farmed.</summary>
    public IReadOnlyDictionary<ulong, DateOnly> Starts => starts;

    /// <summary>The routes that count as farming, as letters: "OJ", "JORZ".</summary>
    public IReadOnlyList<string> Routes { get; }

    public bool IsEmpty => starts.Count == 0;

    /// <summary>Route list text, such as "OJ, JORZ", as route letters. Anything that is not a letter separates routes.</summary>
    public static List<string> ParseRoutes(string? text)
    {
        var routes = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in (text ?? string.Empty) + " ")
        {
            if (char.IsAsciiLetter(ch))
            {
                current.Append(char.ToUpperInvariant(ch));
                continue;
            }

            if (current.Length > 0 && !routes.Contains(current.ToString()))
                routes.Add(current.ToString());
            current.Clear();
        }

        return routes;
    }

    public static FarmingPeriod Detect(IEnumerable<Voyage> voyages, BusinessCalendar calendar, IEnumerable<string> routes, FarmingStart rule)
    {
        var wanted = routes.Select(r => string.Concat(r.Where(char.IsAsciiLetter)).ToUpperInvariant()).Where(r => r.Length > 0).Distinct().ToList();

        // Each submarine's first voyage on a farming route.
        var firstBySub = new Dictionary<(ulong Fc, uint Sub), DateOnly>();
        foreach (var v in voyages)
        {
            if (v.Deleted || !wanted.Contains((v.Route ?? string.Empty).ToUpperInvariant()))
                continue;
            var date = calendar.DateOf(v.CollectedUnix);
            var key = (v.FcId, v.SubRegister);
            if (!firstBySub.TryGetValue(key, out var first) || date < first)
                firstBySub[key] = date;
        }

        // Then per Free Company: the earliest of those for the first submarine, the latest for the whole fleet.
        var starts = new Dictionary<ulong, DateOnly>();
        foreach (var ((fcId, _), date) in firstBySub)
        {
            if (!starts.TryGetValue(fcId, out var current)
                || (rule == FarmingStart.FirstSubmarine ? date < current : date > current))
                starts[fcId] = date;
        }

        return new FarmingPeriod(rule, starts, firstBySub, wanted);
    }

    /// <summary>
    /// Whether a voyage counts. Counting from the first submarine, each submarine counts only from its own first
    /// farming voyage, so the others' levelling voyages stay out until they join. Counting from the whole fleet,
    /// every voyage from the Free Company's start counts.
    /// </summary>
    public bool Covers(Voyage voyage, DateOnly date)
    {
        if (Rule == FarmingStart.FirstSubmarine)
            return subStarts.TryGetValue((voyage.FcId, voyage.SubRegister), out var start) && date >= start;

        return Covers(voyage.FcId, date);
    }

    /// <summary>The earliest start among the chosen Free Companies, or null if none of them has farmed.</summary>
    public DateOnly? FirstFor(FcFilter fcs)
    {
        DateOnly? first = null;
        foreach (var (fcId, date) in starts)
        {
            if ((fcId == 0 ? fcs.IsAll : fcs.Includes(fcId)) && (first is null || date < first))
                first = date;
        }

        return first;
    }

    /// <summary>
    /// Whether a record from this Free Company on this day falls inside its farming period. Hand-kept totals have no
    /// submarine, so they count from the Free Company's start under either rule.
    /// </summary>
    public bool Covers(ulong fcId, DateOnly date)
    {
        if (starts.TryGetValue(fcId, out var start))
            return date >= start;

        // Records tied to no Free Company count from the earliest farming start.
        return fcId == 0 && FirstFor(FcFilter.All) is { } first && date >= first;
    }

    /// <summary>The voyages and entries that fall inside the farming period.</summary>
    public (List<Voyage> Voyages, List<LedgerEntry> Entries) Apply(
        IEnumerable<Voyage> voyages, IEnumerable<LedgerEntry> entries, BusinessCalendar calendar)
        => ([.. voyages.Where(v => Covers(v, calendar.DateOf(v.CollectedUnix)))],
            [.. entries.Where(e => Covers(e.FcId, e.BusinessDate))]);
}
