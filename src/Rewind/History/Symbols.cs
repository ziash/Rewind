namespace Rewind.History;

/// <summary>A symbol on the chart: its name and how many decimals its prices have (spec §2, §4).</summary>
public sealed record SymbolInfo(string Name, int Digits)
{
    // Same symbols and decimals as rewind-download's table (src/Rewind.Download/Instruments.cs).
    public static readonly IReadOnlyList<SymbolInfo> All =
    [
        new("AUDJPY", 3), new("EURGBP", 5), new("EURUSD", 5), new("GBPJPY", 3), new("BTCUSD", 1), new("ETHUSD", 1),
        // Scout's other symbols (scout-lens.md §8 step 1)
        new("GBPUSD", 5), new("USDJPY", 3), new("EURJPY", 3), new("XAUUSD", 3), new("XAGUSD", 3), new("XPTUSD", 3),
        new("US500", 3), new("USTEC", 3), new("DE40", 3), new("XTIUSD", 3), new("XBRUSD", 3), new("XNGUSD", 4), new("SOLUSD", 2),
    ];

    public static SymbolInfo? Find(string name) => All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
