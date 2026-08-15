using System.Buffers.Binary;

namespace OpenRamble.App.Linux;

/// <summary>
/// The one thing this shell needs out of a Windows icon file: the PNG inside it.
/// </summary>
/// <remarks>
/// <para>
/// The brand mark ships as <c>OpenRamble.ico</c> because that is what the Windows executable needs
/// it to be, and it is linked into this project rather than copied so the two platforms cannot
/// draw different marks. An <c>.ico</c> is a directory of images, and since Vista the large planes
/// are ordinary PNG files stored whole - so reading the file is finding the right directory entry
/// and handing its bytes to the image decoder, not writing an icon decoder.
/// </para>
/// <para>
/// The planes stored as bitmaps are deliberately not read. They are the small ones, they are
/// upside down, and their alpha lives in a separate mask - three details to get right for a
/// picture we would immediately downscale from the PNG anyway.
/// </para>
/// </remarks>
internal static class IconFile
{
    private const int HeaderLength = 6;
    private const int EntryLength = 16;

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The largest PNG plane in the file, or <c>null</c> if it holds none.
    /// </summary>
    /// <remarks>
    /// The largest rather than the nearest to the size wanted: everything drawn from it is
    /// downscaled, and downscaling is the direction that does not invent detail.
    /// </remarks>
    internal static byte[]? LargestPng(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderLength) { return null; }
        // Reserved must be zero and the type must be 1 (icon, not cursor). A file that fails this
        // is not the one we linked in, and guessing at its layout is how a wrong picture ships.
        if (BinaryPrimitives.ReadUInt16LittleEndian(file) != 0) { return null; }
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[2..]) != 1) { return null; }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (file.Length < HeaderLength + (count * EntryLength)) { return null; }

        byte[]? best = null;
        int bestWidth = 0;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = file.Slice(HeaderLength + (index * EntryLength), EntryLength);
            // A zero in the width byte means 256: the field is one byte wide and 256 does not fit.
            int width = entry[0] == 0 ? 256 : entry[0];
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), int.MaxValue);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (length <= 0 || offset + length > file.Length) { continue; }

            ReadOnlySpan<byte> image = file.Slice((int)offset, length);
            if (!image.StartsWith(PngSignature)) { continue; }
            if (best is not null && bestWidth >= width) { continue; }

            best = image.ToArray();
            bestWidth = width;
        }

        return best;
    }
}
