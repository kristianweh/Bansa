using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Flow.Services;

namespace Flow;

public partial class App : Application
{
    /// <summary>
    /// True when a "portable" marker file sits next to the exe, OR --portable was passed.
    /// In portable mode everything lives in .\Data\ so the whole folder can be copied/moved.
    /// </summary>
    public static bool IsPortable { get; private set; }

    public static string DataFolder { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flow");

    public static FlowSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Portable-mode detection ───────────────────────────────────────────
        // Priority 1: --portable command-line flag
        // Priority 2: a file named "portable" (any case, no extension required) next to the exe
        var exeDir = AppContext.BaseDirectory;
        var hasFlag = e.Args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase));
        var hasMarker = File.Exists(Path.Combine(exeDir, "portable")) ||
                        File.Exists(Path.Combine(exeDir, "portable.txt")) ||
                        File.Exists(Path.Combine(exeDir, ".portable"));
        if (hasFlag || hasMarker)
        {
            IsPortable   = true;
            DataFolder   = Path.Combine(exeDir, "Data");
        }

        Directory.CreateDirectory(DataFolder);

        Settings = SettingsManager.Load();

        var theme = Enum.TryParse<AppTheme>(Settings.Theme, true, out var t) ? t : AppTheme.Dark;
        ThemeManager.Apply(theme);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // Start hardware monitor eagerly on a background thread (async init, no startup delay)
        HardwareMonitor.Start();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        HardwareMonitor.StopInstance();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Flow hit an unexpected error:\n\n{e.Exception.Message}\n\nThe app will keep running.",
            "Flow",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(DataFolder, "crash.log"),
                    $"[{DateTime.Now:O}] {ex}\n");
            }
            catch { /* best effort */ }
        }
    }
}
