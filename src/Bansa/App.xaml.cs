using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Bansa.Services;

namespace Bansa;

public partial class App : Application
{
    /// <summary>
    /// User data (settings, history DB, crash log) lives in %LocalAppData%\Bansa\.
    /// Tool executables placed by the user live next to Bansa.exe in Data\Tools\.
    /// </summary>
    public static string DataFolder  { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bansa");

    public static string ToolsFolder { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "Data", "Tools");

    public static BansaSettings Settings { get; internal set; } = new();

    // ── Single instance ──────────────────────────────────────────────────────
    // Two Bansa processes are actively harmful: the second one's NetworkMonitor.Start()
    // kills the first's ETW kernel session (session names are machine-global), both pulse
    // firewall rules against each other, and whichever exits first tears down the other's
    // rules. Global\ mutex because the ETW session name is global too.
    private const string MutexName = @"Global\Bansa_SingleInstance";
    private Mutex? _singleInstanceMutex;

    /// <summary>
    /// Machine-global window message broadcast by a second instance so the first one can
    /// show/activate its main window. MainWindow handles it in WndProc.
    /// </summary>
    public static readonly int ShowMainWindowMessage =
        RegisterWindowMessage("Bansa.ShowMainWindow");

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another Bansa is already running — ask it to show itself and bow out.
            // Environment.Exit (not Shutdown): Shutdown() races the queued StartupUri
            // navigation, and even a transiently-constructed MainWindow would create a
            // MainViewModel whose ctor starts the ETW session — killing the first
            // instance's session. Nothing is initialized yet, so a hard exit is clean.
            PostMessage(HWND_BROADCAST, ShowMainWindowMessage, IntPtr.Zero, IntPtr.Zero);
            _singleInstanceMutex.Dispose();
            Environment.Exit(0);
        }

        DataFolder  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bansa");
        ToolsFolder = Path.Combine(AppContext.BaseDirectory, "Data", "Tools");

        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(ToolsFolder);

        // One-time migration from the old exe-adjacent Data\ folder
        MigrateOldDataFolder();

        Settings = SettingsManager.Load();

        var theme = Enum.TryParse<AppTheme>(Settings.Theme, true, out var t) ? t : AppTheme.Dark;
        ThemeManager.Apply(theme);

        var domain = Enum.TryParse<AppDomainMode>(Settings.Domain, true, out var dm) ? dm : AppDomainMode.Network;
        DomainManager.Apply(domain);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // Start hardware monitor eagerly on a background thread (async init, no startup delay)
        HardwareMonitor.Start();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        HardwareMonitor.StopInstance();
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Debug("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"Bansa hit an unexpected error:\n\n{e.Exception.Message}\n\nThe app will keep running.",
            "Bansa",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Debug("Fatal (AppDomain)", ex);
    }

    private static void MigrateOldDataFolder()
    {
        var oldDir = Path.Combine(AppContext.BaseDirectory, "Data");
        var newDir = DataFolder;
        if (!Directory.Exists(oldDir) || Directory.Exists(Path.Combine(newDir, "bansa.db")))
            return;

        foreach (var file in new[] { "settings.json", "bansa.db", "crash.log" })
        {
            var src = Path.Combine(oldDir, file);
            var dst = Path.Combine(newDir, file);
            if (File.Exists(src) && !File.Exists(dst))
                try { File.Copy(src, dst); } catch { }
        }
    }
}
