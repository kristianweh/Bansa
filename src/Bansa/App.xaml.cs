using System;
using System.IO;
using System.Linq;
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

    protected override void OnStartup(StartupEventArgs e)
    {
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
            $"Bansa hit an unexpected error:\n\n{e.Exception.Message}\n\nThe app will keep running.",
            "Bansa",
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
