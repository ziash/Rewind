namespace Rewind.Download;

/// <summary>
/// A symbol in the Dukascopy data feed (spec §4). Prices in the files are whole numbers: price = value / Factor.
/// Factors checked against the broker's prices in step 1.
/// </summary>
/// <param name="Name">Our name (as Scout and TM use it, e.g. US500).</param>
/// <param name="Forex">Closed on Saturdays (forex, metals, indices, energies): those days are not downloaded.</param>
/// <param name="FeedName">Dukascopy's name when it differs from ours (e.g. USA500IDXUSD for US500).</param>
internal sealed record Instrument(string Name, int Factor, bool Forex, string? FeedName = null)
{
    /// <summary>Decimals written to the CSV files: log10 of the factor.</summary>
    public int Digits => (int)Math.Round(Math.Log10(Factor));

    /// <summary>The name in the feed's URLs.</summary>
    public string Feed => FeedName ?? Name;

    public static readonly IReadOnlyList<Instrument> All =
    [
        new("AUDJPY", 1_000, Forex: true),
        new("EURGBP", 100_000, Forex: true),
        new("EURUSD", 100_000, Forex: true),
        new("GBPJPY", 1_000, Forex: true),
        new("BTCUSD", 10, Forex: false),
        new("ETHUSD", 10, Forex: false),
        // Scout's other symbols (scout-lens.md §8 step 1; Trader config/scout.json). Names and factors checked against
        // Pepperstone's / IC Markets' prices of 6-Nov-2026 (spec §4).
        new("GBPUSD", 100_000, Forex: true),
        new("USDJPY", 1_000, Forex: true),
        new("EURJPY", 1_000, Forex: true),
        new("XAUUSD", 1_000, Forex: true),
        new("XAGUSD", 1_000, Forex: true),
        new("XPTUSD", 1_000, Forex: true, "XPTCMDUSD"),
        new("US500", 1_000, Forex: true, "USA500IDXUSD"),
        new("USTEC", 1_000, Forex: true, "USATECHIDXUSD"),
        new("DE40", 1_000, Forex: true, "DEUIDXEUR"),
        new("XTIUSD", 1_000, Forex: true, "LIGHTCMDUSD"),
        new("XBRUSD", 1_000, Forex: true, "BRENTCMDUSD"),
        new("XNGUSD", 1_000, Forex: true, "GASCMDUSD"),
        new("SOLUSD", 1_000, Forex: false),
    ];

    public static Instrument Find(string name) =>
        All.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown symbol {name}; known: {string.Join(", ", All.Select(i => i.Name))}");

    /// <summary>Days with data to fetch: forex skips Saturdays (market closed all day).</summary>
    public bool Trades(DateOnly day) => !(Forex && day.DayOfWeek == DayOfWeek.Saturday);
}

/// <summary>Bid or ask side; each has its own file per day.</summary>
internal enum Side { Bid, Ask }
