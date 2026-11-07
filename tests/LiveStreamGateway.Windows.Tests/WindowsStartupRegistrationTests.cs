using Microsoft.Win32;

namespace LiveStreamGateway.Tests;

public sealed class WindowsStartupRegistrationTests
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void EnablingWritesQuotedAbsoluteExePathAsString()
    {
        using var scope = new StartupTestScope();
        var registration = new WindowsStartupRegistration(scope.ExePath, scope.ValueName);

        registration.SetEnabled(true);

        using RegistryKey key = OpenRunKey();
        Assert.Equal($"\"{scope.ExePath}\"", key.GetValue(scope.ValueName));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind(scope.ValueName));
        Assert.True(registration.IsEnabled);
    }

    [Theory]
    [InlineData("LiveStreamGateway.exe")]
    [InlineData(@".\LiveStreamGateway.exe")]
    [InlineData(@"C:LiveStreamGateway.exe")]
    public void ConstructorRejectsRelativeExePaths(string exePath)
    {
        using var scope = new StartupTestScope();

        Assert.Throws<ArgumentException>(() => new WindowsStartupRegistration(exePath, scope.ValueName));
        Assert.Null(ReadValue(scope.ValueName));
    }

    [Theory]
    [InlineData("dotnet.exe")]
    [InlineData("DotNet.EXE")]
    public void ConstructorRejectsDotnetHost(string fileName)
    {
        using var scope = new StartupTestScope();
        string exePath = Path.Combine(scope.DirectoryPath, fileName);

        Assert.Throws<ArgumentException>(() => new WindowsStartupRegistration(exePath, scope.ValueName));
        Assert.Null(ReadValue(scope.ValueName));
    }

    [Fact]
    public void EnablingMissingExecutableFailsWithoutWritingRunValue()
    {
        using var scope = new StartupTestScope();
        string missingExePath = Path.Combine(scope.DirectoryPath, "Missing.exe");
        var registration = new WindowsStartupRegistration(missingExePath, scope.ValueName);

        Assert.False(registration.IsEnabled);
        Assert.Throws<FileNotFoundException>(() => registration.SetEnabled(true));
        Assert.Null(ReadValue(scope.ValueName));
    }

    [Fact]
    public void LongPathCanBeReadAndDisabledButCannotBeEnabled()
    {
        using var scope = new StartupTestScope();
        string longExePath = Path.Combine(scope.DirectoryPath, new string('a', 220), "LiveStreamGateway.exe");
        string longCommand = $"\"{longExePath}\"";
        Assert.True(longCommand.Length > 260);

        var registration = new WindowsStartupRegistration(longExePath, scope.ValueName);
        using (RegistryKey key = OpenRunKey(writable: true))
            key.SetValue(scope.ValueName, longCommand, RegistryValueKind.String);

        Assert.True(registration.IsEnabled);
        registration.SetEnabled(false);
        Assert.False(registration.IsEnabled);
        Assert.Null(ReadValue(scope.ValueName));

        Assert.Throws<InvalidOperationException>(() => registration.SetEnabled(true));
        Assert.Null(ReadValue(scope.ValueName));
    }

    [Fact]
    public void EnableAndDisableUpdateTheActualRunValue()
    {
        using var scope = new StartupTestScope();
        var registration = new WindowsStartupRegistration(scope.ExePath, scope.ValueName);

        Assert.False(registration.IsEnabled);
        registration.SetEnabled(true);
        Assert.True(registration.IsEnabled);
        Assert.NotNull(ReadValue(scope.ValueName));

        registration.SetEnabled(false);
        Assert.False(registration.IsEnabled);
        Assert.Null(ReadValue(scope.ValueName));

        // Repeated removal must remain safe when the value no longer exists.
        registration.SetEnabled(false);
        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void IsEnabledRefreshesWhenTheRunValueIsChangedExternally()
    {
        using var scope = new StartupTestScope();
        var registration = new WindowsStartupRegistration(scope.ExePath, scope.ValueName);

        Assert.False(registration.IsEnabled);
        using RegistryKey key = OpenRunKey(writable: true);

        key.SetValue(scope.ValueName, $"\"{scope.ExePath}\"", RegistryValueKind.String);
        Assert.True(registration.IsEnabled);

        key.SetValue(scope.ValueName, $"\"{scope.ExePath}\" --unexpected", RegistryValueKind.String);
        Assert.False(registration.IsEnabled);

        key.SetValue(scope.ValueName, $"\"{scope.ExePath.ToUpperInvariant()}\"", RegistryValueKind.String);
        Assert.True(registration.IsEnabled);

        key.DeleteValue(scope.ValueName);
        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void EnablingAndDisablingDoNotModifyNeighboringRunValues()
    {
        using var scope = new StartupTestScope();
        const string neighborCommand = @"%TEMP%\Unrelated Application.exe";
        using RegistryKey key = OpenRunKey(writable: true);
        key.SetValue(scope.NeighborValueName, neighborCommand, RegistryValueKind.ExpandString);
        var registration = new WindowsStartupRegistration(scope.ExePath, scope.ValueName);

        registration.SetEnabled(true);
        Assert.Equal(neighborCommand, ReadValue(scope.NeighborValueName));
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind(scope.NeighborValueName));

        registration.SetEnabled(false);
        Assert.Equal(neighborCommand, ReadValue(scope.NeighborValueName));
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind(scope.NeighborValueName));
    }

    private static object? ReadValue(string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static RegistryKey OpenRunKey(bool writable = false) =>
        Registry.CurrentUser.OpenSubKey(RunKeyPath, writable)
        ?? (writable
            ? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            : throw new InvalidOperationException("未找到测试使用的当前用户 Run 注册表项。"))
        ?? throw new InvalidOperationException("无法打开测试使用的当前用户 Run 注册表项。");

    private sealed class StartupTestScope : IDisposable
    {
        private const string ValuePrefix = "LSG.TrayTest.";

        internal StartupTestScope()
        {
            string testId = Guid.NewGuid().ToString("N");
            ValueName = ValuePrefix + testId;
            NeighborValueName = ValuePrefix + testId + ".Neighbor";
            DirectoryPath = Path.Combine(Path.GetTempPath(), ValuePrefix + testId, "Folder with spaces");
            Directory.CreateDirectory(DirectoryPath);
            ExePath = Path.GetFullPath(Path.Combine(DirectoryPath, "LiveStreamGateway.exe"));
            // Only existence matters to the helper; this file is never launched.
            File.WriteAllBytes(ExePath, []);
        }

        internal string ValueName { get; }
        internal string NeighborValueName { get; }
        internal string DirectoryPath { get; }
        internal string ExePath { get; }

        public void Dispose()
        {
            // A using declaration compiles to finally, including when an assertion fails.
            // Never delete a key, enumerate existing values, or touch the real application value.
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                key?.DeleteValue(NeighborValueName, throwOnMissingValue: false);
            }
            finally
            {
                string testRoot = Path.GetDirectoryName(DirectoryPath)!;
                if (Directory.Exists(testRoot))
                    Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
