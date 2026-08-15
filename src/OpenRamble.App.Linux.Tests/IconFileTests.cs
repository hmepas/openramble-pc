using System.Buffers.Binary;
using OpenRamble.App.Linux;

namespace OpenRamble.App.Linux.Tests;

/// <summary>
/// Getting the brand mark out of the icon file both other platforms already ship.
/// </summary>
public sealed class IconFileTests
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The one test here about the real world: the art that ships still has a plane this shell can
    /// read. It is the claim that breaks silently when somebody re-exports the icon without the
    /// large PNG - and the symptom would be a tray with no picture in it.
    /// </summary>
    [Fact]
    public void The_shipped_mark_has_a_PNG_plane()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "OpenRamble.ico"));

        byte[]? png = IconFile.LargestPng(file);

        Assert.NotNull(png);
        Assert.True(png.AsSpan().StartsWith(PngSignature));
    }

    [Fact]
    public void The_largest_plane_wins()
    {
        byte[] small = Png(16);
        byte[] large = Png(64);
        byte[] file = Icon([(16, small), (64, large)]);

        Assert.Equal(large, IconFile.LargestPng(file));
    }

    /// <summary>A zero in the width byte means 256, which is the biggest plane there can be.</summary>
    [Fact]
    public void A_width_of_zero_reads_as_256()
    {
        byte[] biggest = Png(9);
        byte[] file = Icon([(128, Png(5)), (0, biggest)]);

        Assert.Equal(biggest, IconFile.LargestPng(file));
    }

    [Fact]
    public void Planes_that_are_not_PNG_are_skipped()
    {
        byte[] png = Png(32);
        byte[] file = Icon([(64, [0x42, 0x4D, 0, 0, 0, 0]), (32, png)]);

        Assert.Equal(png, IconFile.LargestPng(file));
    }

    [Fact]
    public void A_file_with_no_PNG_plane_has_nothing_to_give()
    {
        byte[] file = Icon([(64, [0x42, 0x4D, 0, 0, 0, 0])]);

        Assert.Null(IconFile.LargestPng(file));
    }

    /// <summary>
    /// A cursor file has the same layout and a different type. Reading one as an icon would draw
    /// somebody's mouse pointer into the notification area.
    /// </summary>
    [Fact]
    public void A_cursor_is_not_an_icon()
    {
        byte[] file = Icon([(64, Png(64))]);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 2);

        Assert.Null(IconFile.LargestPng(file));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    public void A_file_too_short_for_its_own_directory_is_refused(int length)
    {
        byte[] file = Icon([(64, Png(64))]);

        Assert.Null(IconFile.LargestPng(file.AsSpan(0, length)));
    }

    /// <summary>
    /// A directory entry pointing past the end of the file is the shape a truncated download
    /// arrives in, and reading it would be a read out of bounds.
    /// </summary>
    [Fact]
    public void An_entry_pointing_outside_the_file_is_skipped()
    {
        byte[] file = Icon([(64, Png(64))]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(6 + 8), (uint)file.Length);

        Assert.Null(IconFile.LargestPng(file));
    }

    /// <summary>A plane long enough to be a signature and nothing more, to keep the fixtures small.</summary>
    private static byte[] Png(int seed) => [.. PngSignature, (byte)seed];

    private static byte[] Icon(IReadOnlyList<(int Width, byte[] Data)> planes)
    {
        int directory = 6 + (planes.Count * 16);
        byte[] file = new byte[directory + planes.Sum(plane => plane.Data.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), (ushort)planes.Count);

        int offset = directory;
        for (int index = 0; index < planes.Count; index++)
        {
            (int width, byte[] data) = planes[index];
            Span<byte> entry = file.AsSpan(6 + (index * 16), 16);
            entry[0] = (byte)width;
            entry[1] = (byte)width;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            data.CopyTo(file.AsSpan(offset));
            offset += data.Length;
        }

        return file;
    }
}
