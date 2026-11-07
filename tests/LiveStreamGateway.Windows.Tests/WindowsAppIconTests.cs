using System.Drawing;
using System.Windows.Forms;

namespace LiveStreamGateway.Tests;

public sealed class WindowsAppIconTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void EmbeddedIcon_ContainsAllExpectedPngFrames()
    {
        IconFrame[] frames = ReadFrames();

        Assert.Equal(ExpectedSizes, frames.Select(frame => frame.Width).Order());
        foreach (IconFrame frame in frames)
        {
            Assert.Equal(frame.Width, frame.Height);
            Assert.Equal(PngSignature, frame.PngData[..PngSignature.Length]);
            using var imageStream = new MemoryStream(frame.PngData, writable: false);
            using var image = new Bitmap(imageStream);
            Assert.Equal(frame.Width, image.Width);
            Assert.Equal(frame.Height, image.Height);
        }
    }

    [Fact]
    public void EmbeddedIcon_EachFramePreservesTransparentCornersAndNearOpaqueArtwork()
    {
        foreach (IconFrame frame in ReadFrames())
        {
            using var imageStream = new MemoryStream(frame.PngData, writable: false);
            using var image = new Bitmap(imageStream);

            Assert.True(Image.IsAlphaPixelFormat(image.PixelFormat));
            Assert.Equal(0, image.GetPixel(0, 0).A);
            Assert.Equal(0, image.GetPixel(image.Width - 1, 0).A);
            Assert.Equal(0, image.GetPixel(0, image.Height - 1).A);
            Assert.Equal(0, image.GetPixel(image.Width - 1, image.Height - 1).A);
            Assert.InRange(image.GetPixel(image.Width / 2, image.Height / 2).A, (byte)250, byte.MaxValue);
        }
    }

    [Fact]
    public void LoadForTray_ReturnsUsableIconAfterResourceStreamHasClosed()
    {
        using Icon icon = WindowsAppIcon.LoadForTray();

        Assert.Equal(SystemInformation.SmallIconSize, icon.Size);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        // LoadForTray has already disposed the stream and source Icon at this point.
        using Bitmap image = icon.ToBitmap();
        Assert.Equal(icon.Size, image.Size);
        Assert.Equal(0, image.GetPixel(0, 0).A);
        Assert.InRange(image.GetPixel(image.Width / 2, image.Height / 2).A, (byte)250, byte.MaxValue);
    }

    [Fact]
    public void RepeatedLoads_OwnIndependentIconLifetimes()
    {
        using Icon first = WindowsAppIcon.LoadForTray();
        using Icon second = WindowsAppIcon.LoadForTray();
        Assert.NotSame(first, second);

        first.Dispose();

        Assert.NotEqual(IntPtr.Zero, second.Handle);
        using Bitmap image = second.ToBitmap();
        Assert.Equal(SystemInformation.SmallIconSize, image.Size);
        Assert.InRange(image.GetPixel(image.Width / 2, image.Height / 2).A, (byte)250, byte.MaxValue);
    }

    private static IconFrame[] ReadFrames()
    {
        using Stream resource = typeof(WindowsAppIcon).Assembly.GetManifestResourceStream(WindowsAppIcon.ResourceName)
            ?? throw new InvalidOperationException("未找到测试所需的内嵌应用图标。");
        using var data = new MemoryStream();
        resource.CopyTo(data);
        byte[] bytes = data.ToArray();
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));

        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        int count = reader.ReadUInt16();
        Assert.Equal(ExpectedSizes.Length, count);
        var frames = new List<IconFrame>(count);
        for (int index = 0; index < count; index++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            if (width == 0) width = 256;
            if (height == 0) height = 256;
            reader.ReadByte(); // Palette color count.
            Assert.Equal(0, reader.ReadByte());
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            uint length = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();

            Assert.InRange((long)offset, 6L + count * 16, bytes.LongLength - 1);
            Assert.InRange((long)length, PngSignature.LongLength, bytes.LongLength - offset);
            frames.Add(new IconFrame(width, height, bytes.AsSpan((int)offset, (int)length).ToArray()));
        }

        return frames.ToArray();
    }

    private sealed record IconFrame(int Width, int Height, byte[] PngData);
}
