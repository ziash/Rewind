using System.Text.RegularExpressions;
using Rewind.Broker;

namespace Rewind.Replay;

/// <summary>
/// Sessions (spec §8): each is a folder in ~/rewind/sessions/ with its replay position, levels, account and CSV files.
/// The one in use is remembered in sessions/current.txt. Starting or switching a session only when the account is flat.
/// </summary>
public sealed partial class Sessions(string root, ReplayClock clock, Levels levels, SimBroker broker)
{
    private readonly Lock _lock = new();

    public string Current { get; private set; } = "default";

    private string Dir(string name) => Path.Combine(root, name);
    private string CurrentFile => Path.Combine(root, "current.txt");

    public IReadOnlyList<string> List() =>
        Directory.Exists(root) ? Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().Order().ToList() : [];

    /// <summary>At start: the session in use last time (or "default").</summary>
    public void OpenLast()
    {
        var name = File.Exists(CurrentFile) ? File.ReadAllText(CurrentFile).Trim() : "default";
        Load(Valid(name) && Directory.Exists(Dir(name)) ? name : "default", start: null, balance: null);
    }

    public void Switch(string name)
    {
        lock (_lock)
        {
            if (!List().Contains(name))
                throw new ArgumentException($"No session {name}");
            RequireFlat();
            Load(name, null, null);
        }
    }

    public void Create(string name, DateTime startUtc, double? balance)
    {
        lock (_lock)
        {
            name = name.Trim();
            if (!Valid(name))
                throw new ArgumentException("Session name: letters, digits, - and _ only (up to 40)");
            if (List().Contains(name))
                throw new ArgumentException($"Session {name} exists already");
            if (balance is <= 0)
                throw new ArgumentException("Start balance must be above 0");
            RequireFlat();
            Load(name, startUtc, balance);
        }
    }

    private void RequireFlat()
    {
        if (!broker.IsFlat)
            throw new ArgumentException("Close all positions and orders first: sessions change only when the account is flat (spec §8)");
    }

    private void Load(string name, DateTime? start, double? balance)
    {
        var dir = Dir(name);
        Directory.CreateDirectory(dir);
        levels.Open(Path.Combine(dir, "levels.json"));
        clock.Open(Path.Combine(dir, "replay.json"), start);
        broker.Open(dir, clock.State.CursorUtc, balance);
        Current = name;
        File.WriteAllText(CurrentFile, name);
        clock.Refresh();
    }

    private static bool Valid(string name) => NameRule().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex NameRule();
}
