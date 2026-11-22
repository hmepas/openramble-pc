namespace DictationCore;

/// <summary>
/// How to handle text in a specific application.
///
/// In the terminal and code editor, a person dictates what should go into the file
/// verbatim. In a letter and chat - a phrase that you do not mind tidying up.
/// </summary>
public enum PromptMode
{
    /// <summary>Literally: minimal cosmetics, code inviolable.</summary>
    Verbatim,

    /// <summary>Prose: processing is allowed that does not change the meaning.</summary>
    Prose,
}

/// <summary>
/// Category of the recipient application.
///
/// Nine categories - the seed transferred from wai-computer. A category, not a
/// flat list of bundle ids, because that is how a person thinks:
/// "literally in terminals" - one decision, not twenty.
/// </summary>
public enum ApplicationCategory
{
    Terminal,
    CodeEditor,
    Notes,
    Messaging,
    Mail,
    Browser,
    Design,
    Office,
    Unknown,
}

/// <remarks>
/// The Swift original is a computed property on the enum; C# enums carry no members,
/// so the default lives next to it as an extension.
/// </remarks>
public static class ApplicationCategoryExtensions
{
    /// <summary>Default mode for the category.</summary>
    public static PromptMode DefaultMode(this ApplicationCategory category) =>
        category switch
        {
            ApplicationCategory.Terminal or ApplicationCategory.CodeEditor => PromptMode.Verbatim,
            ApplicationCategory.Notes or ApplicationCategory.Messaging or ApplicationCategory.Mail
                or ApplicationCategory.Browser or ApplicationCategory.Design
                or ApplicationCategory.Office => PromptMode.Prose,
            // **Default of the unknown - verbatim.** An error in this direction will leave an
            // extra word; an error in the other will silently rewrite the command in the terminal,
            // which we just did not recognize.
            _ => PromptMode.Verbatim,
        };
}

/// <summary>
/// Deciding "which mode to apply to this application".
///
/// A pure type with tests, as required by the repository rules: this is policy, not logic
/// inside the view model. The only input is what the application already knows about the
/// target: bundle id and human readable name. Neither the screen nor the contents of the window,
/// no screenshots - only these two fields.
/// </summary>
public sealed record PromptModePolicy
{
    public PromptModePolicy(IReadOnlyDictionary<string, PromptMode>? overrides = null)
    {
        Overrides = overrides ?? new Dictionary<string, PromptMode>(StringComparer.Ordinal);
    }

    /// <summary>What the person chose with his hands: bundle id → mode. More important than the map.</summary>
    public IReadOnlyDictionary<string, PromptMode> Overrides { get; }

    /// <summary>Precedent: the person's choice → the category map → the default.</summary>
    public PromptMode Mode(TargetApplication? target)
    {
        if (target is null) { return ApplicationCategory.Unknown.DefaultMode(); }
        if (target.BundleIdentifier is { } identifier
            && Overrides.TryGetValue(identifier.ToLowerInvariant(), out PromptMode @override))
        {
            return @override;
        }
        return Category(target).DefaultMode();
    }

    /// <summary>The target's category. Public - the settings show it to the person.</summary>
    public static ApplicationCategory Category(TargetApplication? target)
    {
        if (target is null) { return ApplicationCategory.Unknown; }
        if (target.BundleIdentifier?.ToLowerInvariant() is { } identifier
            && Seed.TryGetValue(identifier, out ApplicationCategory known))
        {
            return known;
        }
        // The name is the second attempt, not the first. The bundle id is stable, and the name is
        // localized and changes between versions; relying on it as the main feature
        // would mean changing the mode depending on the system language.
        return CategoryByName(target.LocalizedName);
    }

    /// <summary>
    /// Category seeds. The list is obviously incomplete - that is why <see cref="ApplicationCategory.Unknown"/>
    /// is verbatim by default: an unfamiliar application gets the safe mode rather than a guess.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, ApplicationCategory> Seed =
        new Dictionary<string, ApplicationCategory>(StringComparer.Ordinal)
        {
            // Terminals
            ["com.apple.terminal"] = ApplicationCategory.Terminal,
            ["com.googlecode.iterm2"] = ApplicationCategory.Terminal,
            ["dev.warp.warp-stable"] = ApplicationCategory.Terminal,
            ["co.zeit.hyper"] = ApplicationCategory.Terminal,
            ["net.kovidgoyal.kitty"] = ApplicationCategory.Terminal,
            ["com.github.wez.wezterm"] = ApplicationCategory.Terminal,
            ["com.mitchellh.ghostty"] = ApplicationCategory.Terminal,
            // Code editors
            ["com.microsoft.vscode"] = ApplicationCategory.CodeEditor,
            ["com.microsoft.vscodeinsiders"] = ApplicationCategory.CodeEditor,
            ["com.todesktop.230313mzl4w4u92"] = ApplicationCategory.CodeEditor,  // Cursor
            ["com.apple.dt.xcode"] = ApplicationCategory.CodeEditor,
            ["com.jetbrains.intellij"] = ApplicationCategory.CodeEditor,
            ["com.jetbrains.pycharm"] = ApplicationCategory.CodeEditor,
            ["com.jetbrains.webstorm"] = ApplicationCategory.CodeEditor,
            ["com.sublimetext.4"] = ApplicationCategory.CodeEditor,
            ["dev.zed.zed"] = ApplicationCategory.CodeEditor,
            ["com.panic.nova"] = ApplicationCategory.CodeEditor,
            // Notes
            ["com.apple.notes"] = ApplicationCategory.Notes,
            ["notion.id"] = ApplicationCategory.Notes,
            ["md.obsidian"] = ApplicationCategory.Notes,
            ["com.agiletortoise.drafts-osx"] = ApplicationCategory.Notes,
            ["net.shinyfrog.bear"] = ApplicationCategory.Notes,
            // Messengers
            ["com.tinyspeck.slackmacgap"] = ApplicationCategory.Messaging,
            ["com.hnc.discord"] = ApplicationCategory.Messaging,
            ["ru.keepcoder.telegram"] = ApplicationCategory.Messaging,
            ["net.whatsapp.whatsapp"] = ApplicationCategory.Messaging,
            ["com.apple.mobilesms"] = ApplicationCategory.Messaging,
            ["us.zoom.xos"] = ApplicationCategory.Messaging,
            // Mail
            ["com.apple.mail"] = ApplicationCategory.Mail,
            ["com.readdle.smartemail-mac"] = ApplicationCategory.Mail,
            ["com.superhuman.mail"] = ApplicationCategory.Mail,
            // Browsers
            ["com.apple.safari"] = ApplicationCategory.Browser,
            ["com.google.chrome"] = ApplicationCategory.Browser,
            ["org.mozilla.firefox"] = ApplicationCategory.Browser,
            ["company.thebrowser.browser"] = ApplicationCategory.Browser,  // Arc
            ["com.brave.browser"] = ApplicationCategory.Browser,
            // Design
            ["com.figma.desktop"] = ApplicationCategory.Design,
            ["com.bohemiancoding.sketch3"] = ApplicationCategory.Design,
            ["com.adobe.photoshop"] = ApplicationCategory.Design,
            // Office
            ["com.microsoft.word"] = ApplicationCategory.Office,
            ["com.apple.iwork.pages"] = ApplicationCategory.Office,
            ["com.microsoft.powerpoint"] = ApplicationCategory.Office,

            // Port addition: the identifiers the other platforms put where macOS puts a bundle
            // id - a Linux window's class (the Wayland app_id, or the X11 WM_CLASS class) and a
            // Windows process name. Lowercased like the rest; one key means the same app on both
            // ("code", "alacritty", "firefox"). Kept to apps whose identifier is certain, because
            // a terminal gets a real Return and an unknown app is verbatim anyway.
            // Terminals
            ["kitty"] = ApplicationCategory.Terminal,
            ["alacritty"] = ApplicationCategory.Terminal,
            ["foot"] = ApplicationCategory.Terminal,
            ["footclient"] = ApplicationCategory.Terminal,
            ["org.wezfurlong.wezterm"] = ApplicationCategory.Terminal,
            ["org.gnome.terminal"] = ApplicationCategory.Terminal,
            ["gnome-terminal-server"] = ApplicationCategory.Terminal,
            ["org.gnome.console"] = ApplicationCategory.Terminal,
            ["org.gnome.ptyxis"] = ApplicationCategory.Terminal,
            ["org.kde.konsole"] = ApplicationCategory.Terminal,
            ["konsole"] = ApplicationCategory.Terminal,
            ["xfce4-terminal"] = ApplicationCategory.Terminal,
            ["xterm"] = ApplicationCategory.Terminal,
            ["urxvt"] = ApplicationCategory.Terminal,
            ["st-256color"] = ApplicationCategory.Terminal,
            ["terminator"] = ApplicationCategory.Terminal,
            ["com.gexperts.tilix"] = ApplicationCategory.Terminal,
            ["windowsterminal"] = ApplicationCategory.Terminal,
            ["cmd"] = ApplicationCategory.Terminal,
            ["powershell"] = ApplicationCategory.Terminal,
            ["pwsh"] = ApplicationCategory.Terminal,
            ["wezterm-gui"] = ApplicationCategory.Terminal,
            // Code editors
            ["code"] = ApplicationCategory.CodeEditor,
            ["code-oss"] = ApplicationCategory.CodeEditor,
            ["codium"] = ApplicationCategory.CodeEditor,
            ["vscodium"] = ApplicationCategory.CodeEditor,
            ["cursor"] = ApplicationCategory.CodeEditor,
            ["jetbrains-idea"] = ApplicationCategory.CodeEditor,
            ["jetbrains-pycharm"] = ApplicationCategory.CodeEditor,
            ["jetbrains-webstorm"] = ApplicationCategory.CodeEditor,
            ["sublime_text"] = ApplicationCategory.CodeEditor,
            ["neovide"] = ApplicationCategory.CodeEditor,
            ["emacs"] = ApplicationCategory.CodeEditor,
            ["devenv"] = ApplicationCategory.CodeEditor,
            ["idea64"] = ApplicationCategory.CodeEditor,
            ["pycharm64"] = ApplicationCategory.CodeEditor,
            // Notes
            ["obsidian"] = ApplicationCategory.Notes,
            ["md.obsidian.obsidian"] = ApplicationCategory.Notes,
            ["notion"] = ApplicationCategory.Notes,
            // Messengers
            ["slack"] = ApplicationCategory.Messaging,
            ["discord"] = ApplicationCategory.Messaging,
            ["org.telegram.desktop"] = ApplicationCategory.Messaging,
            ["telegramdesktop"] = ApplicationCategory.Messaging,
            ["telegram"] = ApplicationCategory.Messaging,
            ["signal"] = ApplicationCategory.Messaging,
            ["ms-teams"] = ApplicationCategory.Messaging,
            // Mail
            ["thunderbird"] = ApplicationCategory.Mail,
            ["org.mozilla.thunderbird"] = ApplicationCategory.Mail,
            ["org.gnome.evolution"] = ApplicationCategory.Mail,
            ["outlook"] = ApplicationCategory.Mail,
            // Browsers
            ["firefox"] = ApplicationCategory.Browser,
            ["chromium"] = ApplicationCategory.Browser,
            ["google-chrome"] = ApplicationCategory.Browser,
            ["brave-browser"] = ApplicationCategory.Browser,
            ["chrome"] = ApplicationCategory.Browser,
            ["msedge"] = ApplicationCategory.Browser,
            ["brave"] = ApplicationCategory.Browser,
            // Office
            ["libreoffice-writer"] = ApplicationCategory.Office,
            ["winword"] = ApplicationCategory.Office,
        };

    private static ApplicationCategory CategoryByName(string? name)
    {
        if (name is null || name.Length == 0) { return ApplicationCategory.Unknown; }
        string lowercased = name.ToLowerInvariant();
        // Only those words that themselves mean a category. "Code" is
        // not included here intentionally: too many things are called that way.
        //
        // A name never makes a terminal. A terminal gets a real Return for
        // "Send it.", and a name cannot earn that: Apple's Console is a log
        // viewer, and a web app called "… console" is a web page. An unknown
        // app is verbatim anyway, so nothing else is lost.
        if (lowercased.Contains("mail", StringComparison.Ordinal)) { return ApplicationCategory.Mail; }
        return ApplicationCategory.Unknown;
    }

    /// <remarks>
    /// The Swift original is <c>Equatable</c>, and its dictionary compares by content;
    /// the synthesized record equality would compare the dictionary by reference.
    /// </remarks>
    public bool Equals(PromptModePolicy? other)
    {
        if (other is null) { return false; }
        if (ReferenceEquals(this, other)) { return true; }
        if (Overrides.Count != other.Overrides.Count) { return false; }
        return Overrides.All(entry =>
            other.Overrides.TryGetValue(entry.Key, out PromptMode mode) && mode == entry.Value);
    }

    public override int GetHashCode()
    {
        int hash = 0;
        foreach (KeyValuePair<string, PromptMode> entry in Overrides)
        {
            hash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        return hash;
    }
}
