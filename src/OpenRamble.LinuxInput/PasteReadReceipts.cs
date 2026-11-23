using System.Runtime.InteropServices;

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

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string runtimeDirectory;
    private readonly Lock gate = new();

    /// <summary>The shim's directory once it is in place; <c>null</c> before, or when it cannot be.</summary>
    private string? shimDirectory;

    private bool shimTried;

    /// <param name="runtimeDirectory">
    /// The session's private runtime directory - <c>$XDG_RUNTIME_DIR</c>, see
    /// <see cref="ForThisSession"/>. It must be ours and 0700 already; the shim and the receipts
    /// live in <see cref="Directory"/> under it.
    /// </param>
    public PasteReadReceipts(string runtimeDirectory)
    {
        this.runtimeDirectory = runtimeDirectory;
        Directory = System.IO.Path.Combine(runtimeDirectory, "openramble", "paste");
    }

    /// <summary><c>&lt;runtime directory&gt;/openramble/paste</c>: where the shim and the receipts live.</summary>
    internal string Directory { get; }

    /// <summary>
    /// Receipts under <c>$XDG_RUNTIME_DIR</c>: per user, on tmpfs, and gone at logout - which is
    /// where a file that says "something was pasted just now" belongs. <c>null</c> without one.
    /// </summary>
    /// <remarks>
    /// Deliberately no fallback under <c>/tmp</c>, unlike the control socket: the shim's folder goes
    /// first on <c>wl-copy</c>'s PATH, and a <c>cat</c> planted in a shared folder would run as this
    /// user with the dictated words on its standard input. Without a private runtime directory
    /// reads are not seen, and the inserter keeps the fixed-delay restore and claims nothing.
    /// </remarks>
    public static PasteReadReceipts? ForThisSession() =>
        RuntimeDirectoryFrom(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")) is { } root
            ? new PasteReadReceipts(root)
            : null;

    /// <summary>The runtime directory, if <paramref name="value"/> names one at all: set and absolute.</summary>
    internal static string? RuntimeDirectoryFrom(string? value) =>
        value is { Length: > 0 } && System.IO.Path.IsPathRooted(value) ? value : null;

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

        string path = System.IO.Path.Combine(shim, $"receipt-{Guid.NewGuid():N}");
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

            // First on wl-copy's PATH, so every folder from the runtime directory down must be
            // ours alone: owned by us, 0700, and a real folder rather than a link to one.
            if (!System.IO.Path.IsPathRooted(runtimeDirectory) || !IsPrivateDirectory(runtimeDirectory)) { return null; }

            string? cat = RealCat();
            if (cat is null) { return null; }

            try
            {
                string folder = runtimeDirectory;
                foreach (string part in new[] { "openramble", "paste" })
                {
                    folder = System.IO.Path.Combine(folder, part);
                    // Only created when nothing at all is there; whatever is there is then checked.
                    if (StatusOf(folder) is null) { System.IO.Directory.CreateDirectory(folder, PrivateDirectoryMode); }
                    if (!IsPrivateDirectory(folder)) { return null; }
                }

                WriteShim(Script(cat));
                shimDirectory = Directory;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                shimDirectory = null;
            }

            return shimDirectory;
        }
    }

    /// <summary>
    /// The shim goes in under a fresh name and is renamed over <c>cat</c>: the new name cannot
    /// already exist (<c>O_EXCL</c>, which never follows a link), and the rename replaces whatever
    /// <c>cat</c> was - a link included - rather than writing through it.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void WriteShim(string script)
    {
        string temporary = System.IO.Path.Combine(Directory, $".{ShimName}-{Guid.NewGuid():N}");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = PrivateDirectoryMode,
            };
            using (var writer = new StreamWriter(new FileStream(temporary, options)))
            {
                writer.Write(script);
            }

            File.Move(temporary, System.IO.Path.Combine(Directory, ShimName), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }

    /// <summary>A folder itself, not one a link points at, owned by this user and 0700.</summary>
    internal static bool IsPrivateDirectory(string path) =>
        StatusOf(path) is { IsDirectory: true } status
        && status.Owner == ControlSocket.EffectiveUserId()
        && status.Permissions == PrivateDirectoryMode;

    /// <summary>What <c>lstat</c> says of a path, or <c>null</c> when it cannot be asked.</summary>
    /// <remarks>
    /// <c>statx</c> rather than <c>lstat</c>: its record has one layout on every architecture, so it
    /// is read here by offset without an <c>unsafe</c> struct. .NET has no managed way to ask who
    /// owns a file.
    /// </remarks>
    private static PathStatus? StatusOf(string path)
    {
        const int CurrentDirectory = -100; // AT_FDCWD
        const int NoFollow = 0x100; // AT_SYMLINK_NOFOLLOW
        const uint BasicStats = 0x7ff; // STATX_BASIC_STATS
        var record = new byte[256];
        if (Statx(CurrentDirectory, path, NoFollow, BasicStats, record) != 0) { return null; }

        uint owner = BitConverter.ToUInt32(record, 20); // stx_uid
        int mode = BitConverter.ToUInt16(record, 28); // stx_mode
        const int TypeMask = 0xF000;
        const int DirectoryType = 0x4000;
        const int PermissionMask = 0x1FF;
        return new PathStatus(owner, (mode & TypeMask) == DirectoryType, (UnixFileMode)(mode & PermissionMask));
    }

    private readonly record struct PathStatus(uint Owner, bool IsDirectory, UnixFileMode Permissions);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, [Out] byte[] record);

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
            .Where(folder => folder != Directory && !folder.Contains('\''));
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
