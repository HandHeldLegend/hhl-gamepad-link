// Gamepads that have been in Link mode on this PC, kept between runs in
// %LOCALAPPDATA%\HHL Gamepad Link\pads.ini (one "id=token|name|user name|mode" line each): the call
// token each one gave this app (so it can call the gamepad over from wherever it is connected), its
// names, for the list while it is elsewhere, and the mode it was last used in here.

namespace HhlGamepadLink;

static class KnownPads
{
    public sealed record Pad(string Id, byte[] Token, string Name, string UserName, byte Mode);

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
            if (known != null && known.Token.AsSpan().SequenceEqual(token))
                return;
            // A name it is known by stays: `name` is its mode's, which may be a console's
            pads[id] = new Pad(id, token, known?.Name ?? name, known?.UserName ?? "",
                               known?.Mode ?? Protocol.FormatNone);
            Save(pads);
        }
    }

    // A known gamepad's model and the user's name for it (from its advertisement or info), when they
    // change; null leaves one as it is
    public static void SetNames(string id, string? name, string? userName)
    {
        lock (_lock)
        {
            var pads = Load();
            if (!pads.TryGetValue(id, out var known))
                return;
            var named = known with { Name = name ?? known.Name, UserName = userName ?? known.UserName };
            if (named == known)
                return;
            pads[id] = named;
            Save(pads);
        }
    }

    // The mode a known gamepad was last used in here, when it changes
    public static void SetMode(string id, byte mode)
    {
        lock (_lock)
        {
            var pads = Load();
            if (!pads.TryGetValue(id, out var known) || known.Mode == mode)
                return;
            pads[id] = known with { Mode = mode };
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

    // A line without a whole token is skipped; a missing name is the id, a missing mode none
    static Pad? Parse(string line)
    {
        var kv = line.Split('=', 2);
        if (kv.Length != 2)
            return null;
        var parts = kv[1].Split('|', 4);
        byte[] token;
        try { token = Convert.FromHexString(parts[0]); }
        catch (FormatException) { return null; }
        if (token.Length != Protocol.TokenLength)
            return null;
        byte mode = parts.Length > 3 && byte.TryParse(parts[3], out byte m) ? m : Protocol.FormatNone;
        return new Pad(kv[0], token, parts.Length > 1 ? parts[1] : kv[0], parts.Length > 2 ? parts[2] : "",
                       mode);
    }

    static string Format(Pad p) =>
        $"{p.Id}={Convert.ToHexString(p.Token)}|{p.Name}|{p.UserName}|{p.Mode}";
}
