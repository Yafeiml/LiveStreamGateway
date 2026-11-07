#if WINDOWS_TRAY
using System.Drawing;
using System.Windows.Forms;

namespace LiveStreamGateway;

/// <summary>EXE 和托盘共用同一份多尺寸图标，不依赖发布目录中的外部文件。</summary>
internal static class WindowsAppIcon
{
    internal const string ResourceName = "LiveStreamGateway.AppIcon.ico";

    internal static Icon LoadForTray()
    {
        using Stream resource = typeof(WindowsAppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("程序内嵌图标缺失，请重新下载完整的 Windows 发布包。");
        // 由系统小图标尺寸选择 ICO 帧，兼顾托盘与 Windows 缩放设置。
        using var source = new Icon(resource, SystemInformation.SmallIconSize);
        return (Icon)source.Clone();
    }
}
#endif
