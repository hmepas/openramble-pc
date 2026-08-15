namespace OpenRamble.AppCore;

/// <summary>
/// Where the application stores its files.
///
/// The root is set externally: a test must run in a temporary folder, not in the real profile
/// of the person who is dictating at this moment.
/// </summary>
/// <remarks>
/// <para>
/// The macOS layout is <c>~/Library/Application Support/OpenRamble/{Models,Takes,RecoveredAudio}</c>.
/// The Windows layout keeps the three subfolders and moves the root to
/// <c>%LOCALAPPDATA%\OpenRamble</c>. On Linux the same three subfolders land under
/// <c>$XDG_DATA_HOME/OpenRamble</c>, which is <c>~/.local/share/OpenRamble</c> unless the person
/// set the variable - <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves to
/// the XDG data directory there, so the code is unchanged and only the answer differs. The
/// spelled-out paths are pinned by <c>AppPathsTests</c>, because "it maps correctly" is the kind
/// of claim that stays true only while somebody checks it.
/// </para>
/// <para>
/// <b>Local, not roaming, and that is the whole backup story.</b> The macOS original marks
/// <c>Takes</c> and <c>RecoveredAudio</c> with <c>isExcludedFromBackup</c>, because Application
/// Support goes into Time Machine and iCloud, and a person's voice has no business surviving
/// both the dictation and the application. Windows has no per-directory backup flag, and it
/// does not need one here: <c>%LOCALAPPDATA%</c> is by definition the folder that does not roam
/// between machines and that OneDrive's Known Folder Move does not touch. Choosing this root
/// over <c>%APPDATA%</c> IS the exclusion, which is why it is not a detail to change casually.
/// Linux offers the same choice in the same shape: <c>$XDG_DATA_HOME</c> is the per-machine data
/// directory, and it is deliberately not <c>$XDG_CONFIG_HOME</c>, which is the one people put in
/// a dotfiles repository and sync between machines.
/// </para>
/// <para>
/// The legacy directory migration of the original (<c>WaiDictation</c> → <c>OpenRamble</c>) is
/// not ported: there is no released Windows build that ever wrote under the old name, so the
/// migration could only ever move somebody else's folder.
/// </para>
/// </remarks>
public sealed class AppPaths
{
    private readonly string root;

    public AppPaths(string root)
    {
        this.root = root;
    }

    /// <summary>
    /// The usual location: the current user's local (non-roaming) application data -
    /// <c>%LOCALAPPDATA%</c> on Windows, the XDG data directory on Linux.
    ///
    /// The directory itself is not created here: <see cref="Support"/> creates it along with
    /// its own folder. A file-system failure should be visible where we are going to write, not
    /// while computing the name of the place.
    /// </summary>
    public static AppPaths Standard() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Application data root.</summary>
    public string Support()
    {
        string directory = Path.Combine(root, "OpenRamble");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Where the recognition models live.
    /// </summary>
    /// <remarks>The directory is not created here - whoever installs a model creates it.</remarks>
    public string Models() => Path.Combine(Support(), "Models");

    /// <summary>
    /// Where takes are written during dictation.
    ///
    /// The file lives exactly until the end of recognition and is deleted immediately - the
    /// user's voice has no business staying on disk.
    /// </summary>
    public string Takes() => Ensure(Path.Combine(Support(), "Takes"));

    /// <summary>WAVs saved only after a technical error or a killed process.</summary>
    public string AudioRecovery() => Ensure(Path.Combine(Support(), "RecoveredAudio"));

    private static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
