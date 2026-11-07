using System.Buffers.Binary;

namespace LiveStreamGateway;

/// <summary>Web 与 Windows 共用内嵌 ICO；网页标识直接使用其中的 PNG 帧，不重复维护图案。</summary>
internal static class AppIconAssets
{
    internal const string ResourceName = "LiveStreamGateway.AppIcon.ico";
    internal static readonly byte[] Favicon = LoadFavicon();
    internal static readonly byte[] LogoPng = ReadLargestPngFrame(Favicon);

    private static byte[] LoadFavicon()
    {
        using Stream resource = typeof(AppIconAssets).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("程序内嵌图标缺失。");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] ReadLargestPngFrame(byte[] icon)
    {
        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        int bestSize = 0;
        byte[]? largest = null;
        for (int index = 0; index < frameCount; index++)
        {
            ReadOnlySpan<byte> entry = icon.AsSpan(6 + index * 16, 16);
            int size = entry[0] == 0 ? 256 : entry[0];
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]));
            ReadOnlySpan<byte> frame = icon.AsSpan(offset, length);
            if (size <= bestSize || !frame.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                continue;
            bestSize = size;
            largest = frame.ToArray();
        }

        return largest ?? throw new InvalidOperationException("内嵌图标中缺少 PNG 图像。");
    }
}
