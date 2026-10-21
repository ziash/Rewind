namespace Rewind.History;

/// <summary>A symbol on the chart: its name and how many decimals its prices have (spec §2, §4).</summary>
public sealed record SymbolInfo(string Name, int Digits)
{
    // Same symbols and decimals as rewind-download's table (src/Rewind.Download/Instruments.cs).
    public static readonly IReadOnlyList<SymbolInfo> All =
    [
        new("AUDJPY", 3), new("EURGBP", 5), new("EURUSD", 5), new("GBPJPY", 3), new("BTCUSD", 1), new("ETHUSD", 1),
    ];
}
