using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// The tray picture: the brand mark with the status dot from <see cref="TrayBadge"/> on it - one
/// shape, one meaning per colour, exactly the vocabulary <see cref="StatusColorRole"/> defines.
/// </summary>
/// <remarks>
/// <para>
/// The mark is the real one, the same art the macOS app ships, read out of
/// <c>Assets/OpenRamble.ico</c> - the very file that is also the executable's icon, so the tray,
/// the taskbar and Alt+Tab cannot drift apart. Only the dot is drawn here, over the mark's
/// 32-pixel plane.
/// </para>
/// <para>
/// Icons are built once per badge and cached: the tray asks for the picture on every status
/// change, and <c>CreateIconIndirect</c> allocates a GDI object each time.
/// </para>
/// </remarks>
public static partial class TrayIconArt
{
    private static readonly Dictionary<TrayBadge, nint> Cache = [];
    private static readonly Lock Gate = new();
    private static uint[]? mark;

    /// <summary>
    /// 32 pixels: the size the shell asks for at 200% scaling, and it downsamples cleanly for
    /// 100%. Asking the shell for its exact metric would mean re-rendering on every DPI change
    /// for a picture 16 pixels across.
    /// </summary>
    private const int Size = 32;

    /// <summary>
    /// The mark's own white. The dot sits on a halo of it, so its edge keeps its own shape where
    /// it crosses the dark art underneath - at 16 pixels that edge is the whole signal.
    /// </summary>
    private const uint Halo = 0xFFF9F9F9;

    public static nint Icon(TrayBadge badge)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(badge, out nint cached)) { return cached; }
            nint icon = Build(badge);
            Cache[badge] = icon;
            return icon;
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            foreach (nint icon in Cache.Values) { DestroyIcon(icon); }
            Cache.Clear();
        }
    }

    private static nint Build(TrayBadge badge)
    {
        uint[] pixels = (uint[])Mark().Clone();
        if (badge != TrayBadge.Hidden) { DrawBadge(pixels, BadgeColor(badge)); }

        nint colour = CreateBitmap(Size, Size, 1, 32, MemoryMarshal.AsBytes<uint>(pixels).ToArray());
        // A 32-bit colour bitmap carries its own alpha, so the mask only has to exist. All-zero
        // means "no pixel is transparent by mask", which hands the decision to the alpha channel.
        nint mask = CreateBitmap(Size, Size, 1, 1, new byte[Size * Size / 8]);

        var info = new IconInfo
        {
            IsIcon = 1,
            HotspotX = 0,
            HotspotY = 0,
            Mask = mask,
            Colour = colour,
        };

        nint icon = CreateIconIndirect(ref info);
        DeleteObject(colour);
        DeleteObject(mask);
        return icon;
    }

    /// <summary>
    /// The brand mark's 32-pixel plane, premultiplied, as every 32-bit GDI surface expects.
    /// Decoded once: the file rides inside the assembly and cannot change under us.
    /// </summary>
    private static uint[] Mark()
    {
        if (mark is not null) { return mark; }

        var decoder = new IconBitmapDecoder(
            new Uri("pack://application:,,,/Assets/OpenRamble.ico"),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        // The plane is in the file; picking the nearest and scaling is for the decoder, which is
        // free to hand back one frame of its own choosing rather than every plane.
        BitmapSource source = decoder.Frames
            .OrderBy(frame => Math.Abs(frame.PixelWidth - Size))
            .First();
        if (source.PixelWidth != Size || source.PixelHeight != Size)
        {
            source = new TransformedBitmap(
                source,
                new ScaleTransform((double)Size / source.PixelWidth, (double)Size / source.PixelHeight));
        }

        uint[] pixels = new uint[Size * Size];
        new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0)
            .CopyPixels(pixels, Size * sizeof(uint), 0);
        mark = pixels;
        return pixels;
    }

    /// <summary>
    /// The dot sits bottom-right, on the mark rather than beside it: the tray hands out one
    /// square and the mark already fills it.
    /// </summary>
    private static void DrawBadge(uint[] pixels, uint colour)
    {
        const double centreX = 23.5;
        const double centreY = 23.5;
        const double radius = 6.5;
        const double halo = 1.25;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double distance = Math.Sqrt(((x - centreX) * (x - centreX)) + ((y - centreY) * (y - centreY)));
                int index = (y * Size) + x;

                double ring = Math.Clamp(radius + halo - distance + 0.5, 0, 1);
                if (ring > 0) { pixels[index] = Over(pixels[index], Blend(Halo, ring)); }

                double coverage = Math.Clamp(radius - distance + 0.5, 0, 1);
                if (coverage > 0) { pixels[index] = Over(pixels[index], Blend(colour, coverage)); }
            }
        }
    }

    /// <summary>Same table as <see cref="StatusBrushes"/>: one meaning per colour, everywhere.</summary>
    private static uint BadgeColor(TrayBadge badge) => badge switch
    {
        TrayBadge.Recording => 0xFFE5484D,
        TrayBadge.Working => 0xFF3E63DD,
        TrayBadge.Attention => 0xFFF76B15,
        _ => 0,
    };

    /// <summary>Premultiplied, as every 32-bit GDI surface expects.</summary>
    private static uint Blend(uint colour, double coverage)
    {
        byte alpha = (byte)Math.Round(((colour >> 24) & 0xFF) * coverage);
        byte red = (byte)Math.Round(((colour >> 16) & 0xFF) * coverage);
        byte green = (byte)Math.Round(((colour >> 8) & 0xFF) * coverage);
        byte blue = (byte)Math.Round((colour & 0xFF) * coverage);
        return ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

    /// <summary>
    /// Source-over, both sides premultiplied. Assigning instead would punch the mark out along
    /// the dot's anti-aliased rim, which over opaque art reads as a hole rather than an edge.
    /// </summary>
    private static uint Over(uint under, uint over)
    {
        double keep = 1 - (((over >> 24) & 0xFF) / 255.0);
        return ((uint)Mix(over, under, 24, keep) << 24)
            | ((uint)Mix(over, under, 16, keep) << 16)
            | ((uint)Mix(over, under, 8, keep) << 8)
            | Mix(over, under, 0, keep);
    }

    private static byte Mix(uint over, uint under, int shift, double keep) =>
        (byte)Math.Clamp(Math.Round(((over >> shift) & 0xFF) + (((under >> shift) & 0xFF) * keep)), 0, 255);

    /// <remarks>
    /// <c>IsIcon</c> is a raw <c>BOOL</c> rather than a marshalled <c>bool</c>: the struct has to
    /// stay blittable for the source-generated import, which is the whole point of using one.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;

        public int HotspotX;
        public int HotspotY;
        public nint Mask;
        public nint Colour;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "CreateBitmap")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll", EntryPoint = "CreateIconIndirect")]
    private static partial nint CreateIconIndirect(ref IconInfo info);

    [LibraryImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
