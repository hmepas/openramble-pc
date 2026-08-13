using System.Runtime.InteropServices;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// The tray picture, drawn from code.
///
/// A rounded brand mark plus the status dot from <see cref="TrayBadge"/> - one shape, one meaning
/// per colour, exactly the vocabulary <see cref="StatusColorRole"/> defines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drawn, not shipped.</b> The macOS build loads <c>BrandIconMenuBar</c> from its asset
/// catalog. That asset is a macOS template image and does not cross over, and this port has no
/// designed Windows icon yet - so the mark is computed into a pixel buffer instead of a
/// placeholder <c>.ico</c> being invented and then quietly shipped as if it were the brand. When
/// a real icon exists this file becomes a resource load.
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

    /// <summary>
    /// 32 pixels: the size the shell asks for at 200% scaling, and it downsamples cleanly for
    /// 100%. Asking the shell for its exact metric would mean re-rendering on every DPI change
    /// for a picture 16 pixels across.
    /// </summary>
    private const int Size = 32;

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
        uint[] pixels = new uint[Size * Size];
        DrawMark(pixels);
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
    /// The mark: a rounded square outline. Deliberately not a microphone - Windows draws its own
    /// microphone glyph in the tray while anything records, and two microphones side by side read
    /// as two applications.
    /// </summary>
    private static void DrawMark(uint[] pixels)
    {
        const int inset = 4;
        const int radius = 7;
        const double stroke = 2.6;
        uint colour = 0xFFE8E8E8;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double distance = RoundedSquareDistance(x, y, inset, Size - 1 - inset, radius);
                double coverage = Math.Clamp((stroke / 2) - Math.Abs(distance) + 0.5, 0, 1);
                if (coverage > 0) { pixels[(y * Size) + x] = Blend(colour, coverage); }
            }
        }
    }

    /// <summary>The dot sits bottom-right, clear of the mark's stroke so both stay readable.</summary>
    private static void DrawBadge(uint[] pixels, uint colour)
    {
        const double centreX = 23.5;
        const double centreY = 23.5;
        const double radius = 6.5;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double distance = Math.Sqrt(((x - centreX) * (x - centreX)) + ((y - centreY) * (y - centreY)));
                double coverage = Math.Clamp(radius - distance + 0.5, 0, 1);
                if (coverage > 0) { pixels[(y * Size) + x] = Blend(colour, coverage); }
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

    private static double RoundedSquareDistance(int x, int y, double min, double max, double radius)
    {
        double halfWidth = ((max - min) / 2) - radius;
        double centre = (min + max) / 2;
        double dx = Math.Abs(x - centre) - halfWidth;
        double dy = Math.Abs(y - centre) - halfWidth;
        double outside = Math.Sqrt((Math.Max(dx, 0) * Math.Max(dx, 0)) + (Math.Max(dy, 0) * Math.Max(dy, 0)));
        return outside + Math.Min(Math.Max(dx, dy), 0) - radius;
    }

    /// <summary>Premultiplied, as every 32-bit GDI surface expects.</summary>
    private static uint Blend(uint colour, double coverage)
    {
        byte alpha = (byte)Math.Round(((colour >> 24) & 0xFF) * coverage);
        byte red = (byte)Math.Round(((colour >> 16) & 0xFF) * coverage);
        byte green = (byte)Math.Round(((colour >> 8) & 0xFF) * coverage);
        byte blue = (byte)Math.Round((colour & 0xFF) * coverage);
        return ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

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
