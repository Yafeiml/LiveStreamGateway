#if WINDOWS_TRAY
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LiveStreamGateway;

/// <summary>托盘只负责交互；Web 宿主和推流仍由原有后台服务管理。</summary>
internal sealed class WindowsTrayHost : IDisposable
{
    private const string InstanceName = @"Local\LiveStreamGateway.WindowsTray";
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _thread;
    private readonly string _instanceName;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TrayApplicationContext? _context;
    private int _disposed;

    private WindowsTrayHost(string webAddress, WindowsStartupRegistration? startup, string instanceName)
    {
        _instanceName = instanceName;
        _thread = new Thread(() => RunTray(webAddress, startup))
        {
            Name = "LiveStreamGateway.Tray",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    internal CancellationToken ShutdownToken => _shutdown.Token;

    internal static WindowsTrayHost? Start(
        string webAddress,
        WindowsStartupRegistration? startup = null,
        string instanceName = InstanceName)
    {
        var host = new WindowsTrayHost(webAddress, startup, instanceName);
        host._thread.Start();
        try
        {
            if (host._ready.Task.GetAwaiter().GetResult())
                return host;
        }
        catch (Exception ex)
        {
            host.Dispose();
            Environment.ExitCode = 1;
            ShowError($"无法启动托盘：\n{ex.Message}");
            return null;
        }

        // 重复双击只打开已经运行的面板，不再启动一套推流进程。
        host.Dispose();
        OpenWeb(webAddress);
        return null;
    }

    private void RunTray(string webAddress, WindowsStartupRegistration? startup)
    {
        Mutex? instance = null;
        bool ownsInstance = false;
        try
        {
            instance = new Mutex(initiallyOwned: false, _instanceName);
            try { ownsInstance = instance.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstance = true; }
            if (!ownsInstance)
            {
                _ready.TrySetResult(false);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using var context = new TrayApplicationContext(webAddress, startup, RequestShutdown);
            _context = context;
            _ready.TrySetResult(true);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            if (!_ready.TrySetException(ex))
            {
                RequestShutdown();
                ShowError($"托盘运行失败，服务将停止：\n{ex.Message}");
            }
        }
        finally
        {
            _context = null;
            if (ownsInstance) instance!.ReleaseMutex();
            instance?.Dispose();
        }
    }

    private void RequestShutdown()
    {
        if (!_shutdown.IsCancellationRequested)
            _shutdown.Cancel();
    }

    internal static void ShowError(string message) =>
        MessageBox.Show(message, "LiveStreamGateway", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static void OpenWeb(string webAddress)
    {
        try { Process.Start(new ProcessStartInfo(webAddress) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError($"无法打开管理面板，请手动访问 {webAddress}\n{ex.Message}"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // 只有宿主完成 StopAsync 和资源释放后，入口才释放托盘。
        _context?.CompleteShutdown();
        if (Thread.CurrentThread != _thread)
            _thread.Join();
        _shutdown.Dispose();
    }

    private sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly Control _dispatcher = new();
        private readonly NotifyIcon _icon;
        private readonly Icon _trayIcon;
        private readonly ContextMenuStrip _menu = new();
        private readonly ToolStripMenuItem _startupItem = new("开机自启");
        private readonly WindowsStartupRegistration _startup;
        private readonly Action _requestShutdown;
        private bool _stopping;

        internal TrayApplicationContext(
            string webAddress,
            WindowsStartupRegistration? startup,
            Action requestShutdown)
        {
            _requestShutdown = requestShutdown;
            // 自启时 Environment.CurrentDirectory 不可靠，始终使用当前 apphost 的绝对路径。
            _startup = startup ?? new WindowsStartupRegistration(Environment.ProcessPath ?? "");
            _ = _dispatcher.Handle;
            var openItem = new ToolStripMenuItem("打开 Web 管理面板");
            openItem.Click += (_, _) => OpenWeb(webAddress);
            _startupItem.Click += (_, _) => ToggleStartup();
            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => BeginShutdown();
            _menu.Items.AddRange([openItem, _startupItem, exitItem]);
            _menu.Opening += (_, _) => RefreshStartupState();
            _trayIcon = WindowsAppIcon.LoadForTray();
            _icon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "LiveStreamGateway",
                ContextMenuStrip = _menu,
                Visible = true
            };
            _icon.DoubleClick += (_, _) => { if (!_stopping) OpenWeb(webAddress); };
        }

        private void RefreshStartupState()
        {
            if (_stopping) return;
            try
            {
                _startupItem.Checked = _startup.IsEnabled;
                _startupItem.Enabled = true;
                _startupItem.ToolTipText = "当前用户登录 Windows 后自动启动网关";
            }
            catch (Exception ex)
            {
                _startupItem.Checked = false;
                _startupItem.Enabled = false;
                _startupItem.ToolTipText = $"无法读取自启设置：{ex.Message}";
            }
        }

        private void ToggleStartup()
        {
            try { _startup.SetEnabled(!_startup.IsEnabled); }
            catch (Exception ex) { ShowError($"无法更改开机自启：\n{ex.Message}"); }
            RefreshStartupState();
        }

        private void BeginShutdown()
        {
            if (_stopping) return;
            _stopping = true;
            _icon.Text = "LiveStreamGateway（正在退出）";
            foreach (ToolStripItem item in _menu.Items) item.Enabled = false;
            _requestShutdown();
        }

        internal void CompleteShutdown()
        {
            try { _dispatcher.BeginInvoke((Action)ExitThread); }
            catch (InvalidOperationException) { /* 消息循环已结束。 */ }
        }

        protected override void ExitThreadCore()
        {
            _requestShutdown();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _trayIcon.Dispose();
                _menu.Dispose();
                _dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
#endif
