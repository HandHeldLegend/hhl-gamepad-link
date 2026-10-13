// Gamepads that have been in Link mode on this PC, kept between runs in
// %LOCALAPPDATA%\HHL Gamepad Link\pads.ini (one "id=token|name" line each): the call token each one
// gave this app (so it can call the gamepad over from wherever it is connected) and its name, for
// the list while it is elsewhere.

namespace HhlGamepadLink;

static class KnownPads
{
    public sealed record Pad(string Id, byte[] Token, string Name);

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HHL Gamepad Link", "pads.ini");
    static readonly object Lock = new();
    static Dictionary<string, Pad>? _pads;

    static Dictionary<string, Pad> Load()
    {
        if (_pads != null)
            return _pads;
        _pads = new();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2)
                    continue;
                var parts = kv[1].Split('|', 2);
                try
                {
                    var token = Convert.FromHexString(parts[0]);
                    if (token.Length == Protocol.TokenLength)
                        _pads[kv[0]] = new Pad(kv[0], token, parts.Length > 1 ? parts[1] : kv[0]);
                }
                catch (FormatException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _pads;
    }

    public static Pad? Get(string id)
    {
        lock (Lock)
            return Load().GetValueOrDefault(id);
    }

    public static IReadOnlyList<Pad> All()
    {
        lock (Lock)
            return Load().Values.ToList();
    }

    public static void Remember(string id, byte[] token, string name)
    {
        lock (Lock)
        {
            var pads = Load();
            if (pads.TryGetValue(id, out var known) && known.Token.AsSpan().SequenceEqual(token) && known.Name == name)
                return;
            pads[id] = new Pad(id, token, name);
            Save(pads);
        }
    }

    public static void Forget(string id)
    {
        lock (Lock)
        {
            if (Load().Remove(id))
                Save(_pads!);
        }
    }

    static void Save(Dictionary<string, Pad> pads)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, pads.Values.Select(p => $"{p.Id}={Convert.ToHexString(p.Token)}|{p.Name}"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
