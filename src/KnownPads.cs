// Gamepads that have been in Link mode on this PC, kept between runs in
// %LOCALAPPDATA%\HHL Gamepad Link\pads.ini (one "id=token|name|user name" line each): the call token
// each one gave this app (so it can call the gamepad over from wherever it is connected), and its
// names, for the list while it is elsewhere.

namespace HhlGamepadLink;

static class KnownPads
{
    public sealed record Pad(string Id, byte[] Token, string Name, string UserName);

    static readonly string FilePath = Path.Combine(Settings.Folder, "pads.ini");
    static readonly object _lock = new();
    static Dictionary<string, Pad>? _pads; // Read from the file on first use

    public static Pad? Get(string id)
    {
        lock (_lock)
            return Load().GetValueOrDefault(id);
    }

    public static IReadOnlyList<Pad> All()
    {
        lock (_lock)
            return Load().Values.ToList();
    }

    public static void Remember(string id, byte[] token, string name)
    {
        lock (_lock)
        {
            var pads = Load();
            pads.TryGetValue(id, out var known);
            if (known != null && known.Name == name && known.Token.AsSpan().SequenceEqual(token))
                return;
            pads[id] = new Pad(id, token, name, known?.UserName ?? "");
            Save(pads);
        }
    }

    // The user's name for a known gamepad (from its advertisement), when it changes
    public static void SetUserName(string id, string userName)
    {
        lock (_lock)
        {
            var pads = Load();
            if (!pads.TryGetValue(id, out var known) || known.UserName == userName)
                return;
            pads[id] = known with { UserName = userName };
            Save(pads);
        }
    }

    public static void Forget(string id)
    {
        lock (_lock)
        {
            var pads = Load();
            if (pads.Remove(id))
                Save(pads);
        }
    }

    static Dictionary<string, Pad> Load()
    {
        if (_pads != null)
            return _pads;
        _pads = new();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
                if (Parse(line) is Pad pad)
                    _pads[pad.Id] = pad;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _pads;
    }

    static void Save(Dictionary<string, Pad> pads)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllLines(FilePath, pads.Values.Select(Format));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A line without a whole token is skipped; a missing name is the id
    static Pad? Parse(string line)
    {
        var kv = line.Split('=', 2);
        if (kv.Length != 2)
            return null;
        var parts = kv[1].Split('|', 3);
        byte[] token;
        try { token = Convert.FromHexString(parts[0]); }
        catch (FormatException) { return null; }
        if (token.Length != Protocol.TokenLength)
            return null;
        return new Pad(kv[0], token, parts.Length > 1 ? parts[1] : kv[0], parts.Length > 2 ? parts[2] : "");
    }

    static string Format(Pad p) => $"{p.Id}={Convert.ToHexString(p.Token)}|{p.Name}|{p.UserName}";
}
