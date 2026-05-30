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
    /// Flow is always self-contained: everything lives in a "Data" subfolder
    /// next to Flow.exe so the whole folder can be moved or deleted cleanly.
    /// </summary>
    public static string DataFolder { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "Data");

    public static FlowSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        var exeDir = AppContext.BaseDirectory;
        DataFolder = Path.Combine(exeDir, "Data");

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
