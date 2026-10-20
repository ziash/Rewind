namespace Rewind.Download;

/// <summary>
/// A symbol in the Dukascopy data feed (spec §4). Prices in the files are whole numbers: price = value / Factor.
/// Factors checked against the broker's prices in step 1.
/// </summary>
/// <param name="Name">Our name and Dukascopy's (both use e.g. EURUSD).</param>
/// <param name="Digits">Decimals written to the CSV files: log10 of the factor.</param>
/// <param name="Forex">Forex is closed on Saturdays: those days are not downloaded.</param>
internal sealed record Instrument(string Name, int Factor, bool Forex)
{
    public int Digits => (int)Math.Round(Math.Log10(Factor));

    public static readonly IReadOnlyList<Instrument> All =
    [
        new("AUDJPY", 1_000, Forex: true),
        new("EURGBP", 100_000, Forex: true),
        new("EURUSD", 100_000, Forex: true),
        new("GBPJPY", 1_000, Forex: true),
        new("BTCUSD", 10, Forex: false),
        new("ETHUSD", 10, Forex: false),
    ];

    public static Instrument Find(string name) =>
        All.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown symbol {name}; known: {string.Join(", ", All.Select(i => i.Name))}");

    /// <summary>Days with data to fetch: forex skips Saturdays (market closed all day).</summary>
    public bool Trades(DateOnly day) => !(Forex && day.DayOfWeek == DayOfWeek.Saturday);
}

/// <summary>Bid or ask side; each has its own file per day.</summary>
internal enum Side { Bid, Ask }
