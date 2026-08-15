using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using OpenRamble.AppCore;

namespace OpenRamble.App.Linux;

/// <summary>
/// The tray picture: the brand mark with the status dot from <see cref="TrayBadge"/> on it - one
/// shape, one meaning per colour, exactly the vocabulary <see cref="StatusColorRole"/> defines.
/// </summary>
/// <remarks>
/// <para>
/// The mark is the real one, the same art both other platforms ship, read out of the linked
/// <c>OpenRamble.ico</c> - see <see cref="IconFile"/> for why an icon file is the source. Only the
/// dot is drawn here, over the mark, and the geometry is the Windows shell's <c>TrayIconArt</c>
/// scaled up: bottom right, on the mark rather than beside it, because the tray hands out one
/// square and the mark already fills it.
/// </para>
/// <para>
/// Drawn with Skia rather than composed by hand into a pixel buffer, which is the one place this
/// deliberately differs from the Windows original. There the dot is arithmetic over premultiplied
/// bytes because a GDI icon has to be assembled that way; here there is a drawing context, the
/// anti-aliasing is the platform's, and the twenty lines of blending do not have to exist twice.
/// </para>
/// <para>
/// Pictures are built once per badge and cached: the tray is refreshed on every state change, and
/// a status notifier item re-sends the whole image each time it is set.
/// </para>
/// </remarks>
internal static class TrayIconArt
{
    /// <summary>
    /// 64 pixels. Twice what the Windows shell draws, because a status notifier host is free to
    /// display the icon at whatever size its panel uses - 48 on a scaled Wayland bar is ordinary -
    /// and it downsamples cleanly for the 22-pixel trays.
    /// </summary>
    private const int Size = 64;

    private static readonly Uri MarkUri = new("avares://OpenRamble.App.Linux/Assets/OpenRamble.ico");

    /// <summary>
    /// The mark's own white. The dot sits on a halo of it, so its edge keeps its own shape where
    /// it crosses the dark art underneath - at tray size that edge is the whole signal.
    /// </summary>
    private static readonly ImmutableSolidColorBrush Halo = new(Color.FromRgb(0xF9, 0xF9, 0xF9));

    private static readonly Dictionary<TrayBadge, Bitmap> Cache = [];
    private static readonly Lock Gate = new();

    private static Bitmap? mark;

    /// <summary>
    /// The picture for one badge, or <c>null</c> when the mark could not be read.
    /// </summary>
    /// <remarks>
    /// A missing picture is not a reason to have no tray icon: a status notifier item with no
    /// image still carries the menu and the tooltip, which is the whole interface. The caller
    /// shows what it has.
    /// </remarks>
    internal static Bitmap? Icon(TrayBadge badge)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(badge, out Bitmap? cached)) { return cached; }

            Bitmap? built = Build(badge);
            if (built is not null) { Cache[badge] = built; }
            return built;
        }
    }

    /// <summary>
    /// The plain mark, for a window's own icon. <c>null</c> when it could not be read.
    /// </summary>
    /// <remarks>
    /// Set from code rather than in the markup, and the reason is the file format: the art is an
    /// <c>.ico</c>, which Avalonia's icon converter would hand straight to the image decoder.
    /// <see cref="IconFile"/> is what knows how to get a picture out of one.
    /// </remarks>
    internal static WindowIcon? WindowMark()
    {
        lock (Gate)
        {
            return Mark() is Bitmap art ? new WindowIcon(art) : null;
        }
    }

    internal static void Release()
    {
        lock (Gate)
        {
            foreach (Bitmap picture in Cache.Values) { picture.Dispose(); }
            Cache.Clear();
            mark?.Dispose();
            mark = null;
        }
    }

    private static Bitmap? Build(TrayBadge badge)
    {
        Bitmap? art = Mark();
        if (art is null) { return null; }

        var canvas = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (DrawingContext context = canvas.CreateDrawingContext())
        {
            context.DrawImage(art, new Rect(0, 0, Size, Size));
            if (BadgeBrush(badge) is IBrush colour)
            {
                // The same proportions as the 32-pixel Windows dot, doubled: centre 23.5, radius
                // 6.5, halo 1.25.
                var centre = new Point(47, 47);
                context.DrawEllipse(Halo, null, centre, 15.5, 15.5);
                context.DrawEllipse(colour, null, centre, 13, 13);
            }
        }

        return canvas;
    }

    /// <summary>Same table as <see cref="StatusBrushes"/>: one meaning per colour, everywhere.</summary>
    private static IBrush? BadgeBrush(TrayBadge badge) => badge switch
    {
        TrayBadge.Recording => StatusBrushes.Recording,
        TrayBadge.Working => StatusBrushes.Processing,
        TrayBadge.Attention => StatusBrushes.Attention,
        _ => null,
    };

    private static Bitmap? Mark()
    {
        if (mark is not null) { return mark; }

        try
        {
            using Stream file = AssetLoader.Open(MarkUri);
            using var bytes = new MemoryStream();
            file.CopyTo(bytes);

            byte[]? png = IconFile.LargestPng(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
            if (png is null)
            {
                DiagnosticLog.Emit("tray.art", ("ok", false), ("error", "no PNG plane in the icon"));
                return null;
            }

            using var decoded = new MemoryStream(png);
            mark = new Bitmap(decoded);
            return mark;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException)
        {
            DiagnosticLog.EmitException("tray.art", error, ("ok", false));
            return null;
        }
    }
}
