using System.Globalization;

namespace SubLedger.Core;

public static class GilFormat
{
    /// <summary>Every digit, with thousands separators: 1,047,250.</summary>
    public static string Exact(long gil) => gil.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Short form for tight spaces: 125k, 1.05m, 2.31b. Values under 10,000 are shown in full.</summary>
    public static string Short(long gil)
    {
        var abs = Math.Abs(gil);
        if (abs < 10_000)
            return Exact(gil);
        // The limits sit just under the round number so 999,999 reads "1m" rather than "1000k".
        if (abs < 999_500)
            return Scaled(gil, 1_000m, "k");
        if (abs < 999_500_000)
            return Scaled(gil, 1_000_000m, "m");
        return Scaled(gil, 1_000_000_000m, "b");
    }

    public static string Format(long gil, bool abbreviate) => abbreviate ? Short(gil) : Exact(gil);

    private static string Scaled(long gil, decimal unit, string suffix)
    {
        var value = gil / unit;
        var abs = Math.Abs(value);
        var format = abs >= 100 ? "0" : abs >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.InvariantCulture) + suffix;
    }
}
