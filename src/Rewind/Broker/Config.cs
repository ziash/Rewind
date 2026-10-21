using System.Text.Json;

namespace Rewind.Broker;

/// <summary>Trading details of one symbol (spec §7, config/symbols.json). Volumes in Open API units (hundredths of a unit).</summary>
public sealed record SymbolSpec(string Name, string Base, string Quote, int Digits, int PipPosition, long LotSize,
    long MinVolume, long StepVolume, long MaxVolume, double CommissionPerMillionUsd)
{
    public double PipSize => Math.Pow(10, -PipPosition);
    public double Lots(long volume) => (double)volume / LotSize;
    public long Volume(double lots) => (long)Math.Round(lots * LotSize);
    public double Round(double price) => Math.Round(price, Digits);
}

/// <summary>config/rewind.json (spec §7).</summary>
public sealed record RewindConfig(double StartBalance = 5000, int Leverage = 500, Dictionary<string, double>? FallbackRates = null);

public static class ConfigFiles
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record SymbolsFile(List<SymbolSpec> Symbols);

    /// <summary>The live copy in ~/rewind/config/ if it exists, else the reference copy shipped next to the program.</summary>
    public static string Find(string configDir, string file)
    {
        var live = Path.Combine(configDir, file);
        return File.Exists(live) ? live : Path.Combine(AppContext.BaseDirectory, "config", file);
    }

    public static Dictionary<string, SymbolSpec> Symbols(string path) =>
        JsonSerializer.Deserialize<SymbolsFile>(File.ReadAllText(path), Json)!.Symbols.ToDictionary(s => s.Name);

    public static RewindConfig Rewind(string path) => JsonSerializer.Deserialize<RewindConfig>(File.ReadAllText(path), Json)!;
}
