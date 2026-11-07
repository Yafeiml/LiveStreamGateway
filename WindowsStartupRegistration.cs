#if WINDOWS_TRAY
using Microsoft.Win32;

namespace LiveStreamGateway;

/// <summary>只管理当前用户的一个登录自启值，读取状态时不会创建注册表项。</summary>
internal sealed class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int MaxRunCommandLength = 260;
    private readonly string _exePath;
    private readonly string _valueName;
    private readonly string _commandLine;

    internal WindowsStartupRegistration(string exePath, string valueName = "LiveStreamGateway")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        if (!Path.IsPathFullyQualified(exePath) ||
            exePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            exePath.Any(char.IsControl) ||
            !Path.GetExtension(exePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("开机自启需要有效的 EXE 绝对路径。", nameof(exePath));
        }

        _exePath = Path.GetFullPath(exePath);
        if (Path.GetFileName(_exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请使用 Windows 发布包中的 LiveStreamGateway.exe 设置开机自启。", nameof(exePath));

        _valueName = valueName;
        // 路径始终整体加引号，含空格的安装目录也不会被当成不同的可执行文件。
        _commandLine = $"\"{_exePath}\"";
    }

    internal bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(_valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                       is string commandLine &&
                   commandLine.Equals(_commandLine, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
            return;
        }

        // 长路径仍允许查询/关闭自启；只在用户尝试启用时检查 Run 的命令长度限制。
        if (_commandLine.Length > MaxRunCommandLength)
            throw new InvalidOperationException("程序路径过长，无法设置开机自启。请将程序移动到较短的目录后重试。");
        if (!File.Exists(_exePath))
            throw new FileNotFoundException("未找到用于开机自启的程序文件。", _exePath);

        using RegistryKey keyToWrite = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("无法打开当前用户的开机自启设置。");
        keyToWrite.SetValue(_valueName, _commandLine, RegistryValueKind.String);
    }
}
#endif
