namespace OpenRamble.LinuxInput;

/// <summary>
/// Knowing that somebody read a dictation off the clipboard - the Linux read receipt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the signal comes from.</b> The macOS original hands the text out through a lazy
/// pasteboard provider and takes AppKit's call for the data as the receipt. Here the offer is held
/// by <c>wl-copy</c>, and <c>wl-copy</c> serves every paste request the same way: it forks and runs
/// <c>cat</c> on its private copy of the text, looked up on its <c>PATH</c>. So a <c>cat</c> of
/// ours, first on the <c>PATH</c> this process hands <c>wl-copy</c>, is run once per read - by the
/// application being pasted into, by a clipboard watcher, by anything - and notes the read before
/// handing over to the real <c>cat</c>. Nothing else on Wayland says a read happened: the protocol
/// tells the client that owns the selection, and that client is <c>wl-copy</c>.
/// </para>
/// <para>
/// <b>What it does not do.</b> It never consumes the offer: unlike <c>wl-copy --paste-once</c>,
/// whose single serve a clipboard watcher such as cliphist would take before the application ever
/// asked, every read is served whole and only counted. And it carries no words: the receipt is a
/// file of dots, one per read, under the runtime directory, gone when the paste is over.
/// </para>
/// <para>
/// <b>Why the dependence on <c>wl-copy</c>'s inside is safe to have.</b> It rests on one detail of
/// wl-clipboard (2.x execs <c>cat</c> to serve a copy), and a future version may serve some other
/// way. The inserter therefore asks once, with a read of its own, whether a read is noted at all,
/// and where it is not it falls back to the old fixed-delay restore and claims nothing about the
/// paste - rather than calling every paste on that machine unread.
/// </para>
/// </remarks>
internal sealed class PasteReadReceipts
{
    /// <summary>Where the receipt's path travels to <c>wl-copy</c>, and through it to the shim.</summary>
    public const string ReceiptVariable = "OPENRAMBLE_PASTE_RECEIPT";

    private const string ShimName = "cat";

    private readonly string directory;
    private readonly Lock gate = new();

    /// <summary>The shim's directory once it is in place; <c>null</c> before, or when it cannot be.</summary>
    private string? shimDirectory;

    private bool shimTried;

    /// <param name="directory">
    /// Private to this user: the shim and the receipts live here. See <see cref="DefaultDirectory"/>.
    /// </param>
    public PasteReadReceipts(string directory)
    {
        this.directory = directory;
    }

    /// <summary>
    /// <c>$XDG_RUNTIME_DIR/openramble/paste</c>: per user, on tmpfs, and gone at logout - which is
    /// where a file that says "something was pasted just now" belongs.
    /// </summary>
    /// <remarks>
    /// The control socket's directory decides the root, so a relative or unset
    /// <c>XDG_RUNTIME_DIR</c> falls back the same way: to a uid-named folder, never to one resolved
    /// against the working directory - the shim's folder goes first on <c>wl-copy</c>'s PATH.
    /// </remarks>
    public static string DefaultDirectory() => Path.Combine(ControlSocket.DefaultDirectory, "paste");

    /// <summary>
    /// A receipt for one offer, or <c>null</c> when none can be kept here - no <c>cat</c> to hand
    /// over to, or a directory that cannot be written. The paste goes ahead either way.
    /// </summary>
    public Receipt? Open()
    {
        // How the platform analyser is told what this assembly's name already says: file modes
        // exist on one operating system. See ControlSocketServer for the same question.
        if (!OperatingSystem.IsLinux()) { return null; }

        string? shim = Shim();
        if (shim is null) { return null; }

        string path = Path.Combine(directory, $"receipt-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(path)) { }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string inherited = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Receipt(
            path,
            new Dictionary<string, string>
            {
                ["PATH"] = inherited.Length == 0 ? shim : $"{shim}:{inherited}",
                [ReceiptVariable] = path,
            });
    }

    /// <summary>
    /// The shim, written once per process. The real <c>cat</c> is named by its full path inside
    /// it, so the shim never finds itself.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private string? Shim()
    {
        lock (gate)
        {
            if (shimTried) { return shimDirectory; }
            shimTried = true;

            // First on wl-copy's PATH, so it must be absolute and nobody else's to write into.
            if (!Path.IsPathRooted(directory)) { return null; }

            string? cat = RealCat();
            if (cat is null) { return null; }

            try
            {
                const UnixFileMode privateDirectory =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(directory, privateDirectory);
                // Again, explicitly: CreateDirectory applies the mode only to a directory it
                // creates, and a folder someone else owns refuses the change - so no shim there.
                File.SetUnixFileMode(directory, privateDirectory);
                string path = Path.Combine(directory, ShimName);
                File.WriteAllText(path, Script(cat));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                shimDirectory = directory;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                shimDirectory = null;
            }

            return shimDirectory;
        }
    }

    /// <summary>
    /// The shim itself. A receipt that no longer exists is not recreated: a late read of an offer
    /// already put away belongs to nobody.
    /// </summary>
    internal static string Script(string realCat) =>
        "#!/bin/sh\n"
        + "# OpenRamble: wl-copy runs cat to serve each paste of a dictation; this notes the read.\n"
        + $"[ -n \"${{{ReceiptVariable}:-}}\" ] && [ -f \"${ReceiptVariable}\" ] && printf . >> \"${ReceiptVariable}\"\n"
        + $"exec '{realCat}' \"$@\"\n";

    private string? RealCat()
    {
        // Never the shim's own folder, and never one the script could not quote.
        IEnumerable<string> folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(':')
            .Where(folder => folder != directory && !folder.Contains('\''));
        return DictationCore.ExecutablePath.Find(ShimName, string.Join(':', folders));
    }
}

/// <summary>The reads of one offer, counted by the shim. Deleted when the paste is over.</summary>
/// <param name="Path">The file of dots. Its name is random; nothing in it is the person's.</param>
/// <param name="Environment">What <c>wl-copy</c> is started with so its reads come here.</param>
internal sealed record Receipt(string Path, IReadOnlyDictionary<string, string> Environment) : IDisposable
{
    /// <summary>How many times the offer has been read so far.</summary>
    public int Reads
    {
        get
        {
            try
            {
                var file = new FileInfo(Path);
                return file.Exists ? (int)file.Length : 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left in the runtime directory, which the session clears; it holds no words.
        }
    }
}
