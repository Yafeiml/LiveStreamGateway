using System.Buffers.Binary;

namespace LiveStreamGateway.Tests;

public sealed class AppIconAssetsTests
{
    [Fact]
    public void Favicon_IsTheEmbeddedWindowsIcon()
    {
        using Stream resource = typeof(AppIconAssets).Assembly.GetManifestResourceStream(AppIconAssets.ResourceName)!;
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);

        Assert.Equal(buffer.ToArray(), AppIconAssets.Favicon);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, AppIconAssets.Favicon[..4]);
    }

    [Fact]
    public void Logo_IsTheUnchanged256PixelPngFrame()
    {
        byte[] png = AppIconAssets.LogoPng;
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(256, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(256, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));

        byte[] icon = AppIconAssets.Favicon;
        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        for (int index = 0; index < frameCount; index++)
        {
            ReadOnlySpan<byte> entry = icon.AsSpan(6 + index * 16, 16);
            if (entry[0] != 0 || entry[1] != 0) continue;
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            Assert.Equal(icon.AsSpan(offset, length).ToArray(), png);
            return;
        }

        Assert.Fail("ICO 中缺少 256 px 图标帧。");
    }
}
