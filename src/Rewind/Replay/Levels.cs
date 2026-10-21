using System.Text.Json;

namespace Rewind.Replay;

/// <summary>A horizontal line the owner placed on a symbol (spec §5); shown on every timeframe.</summary>
public sealed record Level(string Id, string Symbol, double Price, string Note);

/// <summary>The owner's levels, saved in sessions/default/levels.json after every change.</summary>
public sealed class Levels(string? path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _lock = new();
    private readonly List<Level> _all = Load(path);

    public IReadOnlyList<Level> For(string symbol)
    {
        lock (_lock)
            return _all.Where(l => l.Symbol == symbol).OrderByDescending(l => l.Price).ToList();
    }

    public Level Add(string symbol, double price, string note)
    {
        var level = new Level(Guid.NewGuid().ToString("N")[..8], symbol, price, note.Trim());
        lock (_lock)
        {
            _all.Add(level);
            Save();
        }
        return level;
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _all.RemoveAll(l => l.Id == id) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    private void Save()
    {
        if (path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_all, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static List<Level> Load(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? JsonSerializer.Deserialize<List<Level>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
