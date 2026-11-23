using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>One keyd configuration file, as read from disk.</summary>
public readonly record struct KeydConfigFile(string Path, string Text);

/// <summary>
/// Whether keyd's configuration rewrites a shortcut before it reaches the app.
/// </summary>
/// <remarks>
/// <para>
/// keyd re-emits every key through its virtual keyboard, so a layer such as
/// <c>[meta]</c> with <c>v = S-insert</c> means Super+V arrives as Shift+Insert, and a chord
/// built on Super+V is never seen. The check is the layer whose modifiers are all part of the
/// shortcut and which maps the shortcut's key; when several do, the one with the most modifiers
/// wins, as keyd's composite layers win over their parts. A binding that re-emits the very chord
/// it caught (<c>[meta+alt]</c> with <c>v = M-A-v</c>) changes nothing and is not a problem.
/// </para>
/// <para>
/// Only the four modifier layers and <c>main</c> are understood; <c>altgr</c> is skipped because
/// the shortcut cannot tell Right Alt from Left. A file that does not parse says nothing: a
/// warning built on a misreading would be worse than none.
/// </para>
/// </remarks>
public static class KeydConfig
{
    public const string Directory = "/etc/keyd";

    /// <summary>Every <c>*.conf</c> in keyd's directory; unreadable files are left out.</summary>
    public static IReadOnlyList<KeydConfigFile> Read(string directory = Directory)
    {
        var files = new List<KeydConfigFile>();
        try
        {
            foreach (string path in System.IO.Directory.EnumerateFiles(directory, "*.conf").Order(StringComparer.Ordinal))
            {
                try { files.Add(new KeydConfigFile(path, File.ReadAllText(path))); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return files;
    }

    /// <summary>
    /// The sentence for the settings window when keyd rewrites <paramref name="shortcut"/>;
    /// <c>null</c> when no file does.
    /// </summary>
    public static string? Warning(IEnumerable<KeydConfigFile> files, KeyCombination shortcut, IShortcutKeymap keymap)
    {
        string key = keymap.LabelFor(shortcut.KeyCode).ToLowerInvariant();
        foreach (KeydConfigFile file in files)
        {
            if (Parse(file.Text) is not { } layers) { continue; }

            Layer? winner = null;
            string? output = null;
            foreach (Layer layer in layers)
            {
                if ((layer.Modifiers & ~shortcut.Modifiers) != HotkeyModifiers.None) { continue; }
                if (!layer.Bindings.TryGetValue(key, out string? bound)) { continue; }
                if (winner is not null && Count(layer.Modifiers) < Count(winner.Modifiers)) { continue; }

                winner = layer;
                output = bound;
            }

            if (winner is null || output is null) { continue; }
            if (Output(output) is { } chord && !chord.AltGr && chord.Key == key && chord.Modifiers == winner.Modifiers) { continue; }

            string from = new KeyCombination(shortcut.KeyCode, winner.Modifiers).DisplayString(keymap);
            return $"keyd turns {from} into {Describe(output)}, so this shortcut never arrives. "
                + $"Pick another, or change {file.Path}.";
        }

        return null;
    }

    private sealed record Layer(HotkeyModifiers Modifiers, Dictionary<string, string> Bindings);

    /// <summary>The modifier layers of one file, or <c>null</c> when it does not parse.</summary>
    private static List<Layer>? Parse(string text)
    {
        var layers = new List<Layer>();
        Layer? current = null;
        bool inSection = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) { continue; }

            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) { return null; }
                string name = line[1..^1].Split(':')[0].Trim();
                current = LayerModifiers(name) is { } modifiers ? new Layer(modifiers, new Dictionary<string, string>()) : null;
                if (current is not null) { layers.Add(current); }
                inSection = true;
                continue;
            }

            if (!inSection) { return null; }
            if (current is null) { continue; }

            int equals = line.IndexOf('=');
            if (equals <= 0) { return null; }
            string left = line[..equals].Trim().ToLowerInvariant();
            string right = line[(equals + 1)..].Trim();
            if (right.Length == 0) { return null; }
            current.Bindings[left] = right;
        }

        return layers;
    }

    /// <summary><c>meta+alt</c> → Super+Alt; <c>main</c> → none; anything else is not a modifier layer.</summary>
    private static HotkeyModifiers? LayerModifiers(string name)
    {
        if (name == "main") { return HotkeyModifiers.None; }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        foreach (string part in name.Split('+'))
        {
            HotkeyModifiers? one = part.Trim() switch
            {
                "control" => HotkeyModifiers.Control,
                "shift" => HotkeyModifiers.Shift,
                "alt" => HotkeyModifiers.Alt,
                "meta" => HotkeyModifiers.Windows,
                _ => null,
            };
            if (one is null) { return null; }
            modifiers |= one.Value;
        }

        return modifiers;
    }

    private readonly record struct Chord(HotkeyModifiers Modifiers, bool AltGr, string Key, string Display);

    /// <summary><c>S-insert</c> → Shift + insert; <c>null</c> for anything that is not a plain chord.</summary>
    private static Chord? Output(string output)
    {
        string[] parts = output.Split('-');
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        bool altGr = false;
        var display = new List<string>();
        for (int index = 0; index < parts.Length - 1; index++)
        {
            (HotkeyModifiers flag, string label) = parts[index] switch
            {
                "C" => (HotkeyModifiers.Control, "Ctrl"),
                "M" => (HotkeyModifiers.Windows, "Super"),
                "A" => (HotkeyModifiers.Alt, "Alt"),
                "S" => (HotkeyModifiers.Shift, "Shift"),
                "G" => (HotkeyModifiers.None, "AltGr"),
                _ => (HotkeyModifiers.None, string.Empty),
            };
            if (label.Length == 0) { return null; }
            modifiers |= flag;
            altGr |= label == "AltGr";
            display.Add(label);
        }

        string key = parts[^1].ToLowerInvariant();
        if (key.Length == 0 || !key.All(char.IsAsciiLetterOrDigit)) { return null; }
        display.Add(char.ToUpperInvariant(key[0]) + key[1..]);
        return new Chord(modifiers, altGr, key, string.Join('+', display));
    }

    private static string Describe(string output) => Output(output)?.Display ?? output;

    private static int Count(HotkeyModifiers modifiers) => int.PopCount((int)modifiers);
}
