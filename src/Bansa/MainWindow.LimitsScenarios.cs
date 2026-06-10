using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using Bansa.Services;
using Bansa.ViewModels;
using Bansa.Views;

// Pin ambiguous types to their WPF variants (System.Drawing brings duplicates via UseWindowsForms).
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Bansa;

public partial class MainWindow
{
    // ── Limits & Scenarios panel ───────────────────────────────────────────────

    private bool _limitsCardsMoved;

    /// <summary>
    /// Relocates the Limit Profiles, Global Cap, Ping Monitor and Scenario editor cards
    /// out of Settings into the Limits &amp; Scenarios panel. Done by re-parenting the live
    /// elements so all their x:Names and event handlers keep working unchanged.
    /// </summary>
    private void ReparentLimitsCards()
    {
        if (_limitsCardsMoved) return;
        _limitsCardsMoved = true;
        // Order here = visual order in the panel, below the fixed Apps-with-limits card.
        MoveCard(CardLimitProfiles);
        MoveCard(CardScenarios);
        MoveCard(CardGlobalCap);
        MoveCard(CardPingMonitor);
        MoveCard(CardConnectionSpeed);
        MoveCard(SpeedPanel);          // Verify limits / speed test card

        void MoveCard(FrameworkElement card)
        {
            if (card.Parent is System.Windows.Controls.Panel p)
            {
                p.Children.Remove(card);
                LimitsHost.Children.Add(card);
            }
        }
    }

    /// <summary>Read-only summary of apps that currently have an up/down limit or are blocked.</summary>
    private void PopulateLimitedApps()
    {
        LimitedAppsList.Items.Clear();

        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in App.Settings.AppUploadLimitsKBs.Keys)   paths.Add(k);
        foreach (var k in App.Settings.AppDownloadLimitsKBs.Keys) paths.Add(k);

        foreach (var path in paths)
        {
            int up   = App.Settings.AppUploadLimitsKBs.TryGetValue(path, out var u) ? u : 0;
            int down = App.Settings.AppDownloadLimitsKBs.TryGetValue(path, out var d) ? d : 0;
            if (up <= 0 && down <= 0) continue;
            LimitedAppsList.Items.Add(BuildLimitedAppRow(path,
                System.IO.Path.GetFileNameWithoutExtension(path), up, down));
        }

        bool any = LimitedAppsList.Items.Count > 0;
        LimitedAppsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement BuildLimitedAppRow(string path, string name, int up, int down)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nm = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("TextBrush") };
        Grid.SetColumn(nm, 0);
        var upTb = new TextBlock { Text = up > 0 ? up.ToString() : "—", HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("RobotoMonoFamily"),
            Foreground = (Brush)FindResource(up > 0 ? "ChartUpBrush" : "MutedTextBrush") };
        Grid.SetColumn(upTb, 1);
        var dnTb = new TextBlock { Text = down > 0 ? down.ToString() : "—", HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("RobotoMonoFamily"),
            Foreground = (Brush)FindResource(down > 0 ? "ChartDownBrush" : "MutedTextBrush") };
        Grid.SetColumn(dnTb, 2);

        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var editBtn = new System.Windows.Controls.Button { Content = "Edit", Padding = new Thickness(10, 3, 10, 3), FontSize = 11, Cursor = Cursors.Hand };
        editBtn.Click += (_, _) => EditLimitInline(path, name, up, down);
        var clearBtn = new System.Windows.Controls.Button { Content = "Clear", Padding = new Thickness(10, 3, 10, 3), FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand };
        clearBtn.Click += async (_, _) => { await Vm.SetLimitByPathAsync(path, 0, 0); PopulateLimitedApps(); };
        actions.Children.Add(editBtn); actions.Children.Add(clearBtn);
        Grid.SetColumn(actions, 3);

        g.Children.Add(nm); g.Children.Add(upTb); g.Children.Add(dnTb); g.Children.Add(actions);
        return g;
    }

    private async void EditLimitInline(string path, string name, int up, int down)
    {
        var dlg = new SetLimitWindow(name, up, down,
                                     App.Settings.ConnectionUploadMbps, App.Settings.ConnectionDownloadMbps,
                                     false) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            await Vm.SetLimitByPathAsync(path, dlg.UploadKbps, dlg.DownloadKbps);
            PopulateLimitedApps();
        }
    }


    // ────────── Scenarios ──────────

    private void OnScenarioBtnClick(object sender, RoutedEventArgs e)
        => _ = Vm.ToggleScenarioCommand.ExecuteAsync(null);

    private void OnGlobalCapBtnClick(object sender, MouseButtonEventArgs e) => ToggleGlobalCap();

    /// <summary>
    /// Toggles the global upload cap. If the user is enabling it but no value is
    /// configured yet (0 = no cap), there's nothing to apply — so instead of flipping
    /// on a no-op cap, send them to Limits &amp; Scenarios and focus the cap value box.
    /// </summary>
    private void ToggleGlobalCap()
    {
        // With no cap value configured the toggle has nothing to apply, so it's not
        // toggleable — instead it opens Limits & Scenarios → Global upload cap to set one.
        if (Vm.GlobalUploadCapKBs <= 0)
        {
            NavigateToLimitsCard(CardGlobalCap);
            Dispatcher.BeginInvoke(() =>
            {
                GlobalCapValueBox.Focus();
                GlobalCapValueBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
        Vm.IsGlobalUploadCapEnabled = !Vm.IsGlobalUploadCapEnabled;
    }

    // ── Scenarios profile editor (Settings tab) ──────────────────────────────

    // Exposed for DataTemplate bindings via RelativeSource AncestorType=Window
    public IReadOnlyList<LimitProfile> AvailableLimitProfiles => App.Settings.LimitProfiles;

    private class ScenarioProfileRow
    {
        public string ExePath    { get; set; } = "";
        public string AppName    { get; set; } = "";
        public string UploadKBs  { get; set; } = "0";
        public string DownloadKBs{ get; set; } = "0";
    }

    private class AppSearchEntry
    {
        public string Name      { get; set; } = "";
        public string ImagePath { get; set; } = "";
    }

    private List<AppSearchEntry> _allScenarioApps = new();
    private AppSearchEntry? _selectedScenarioApp;
    private bool _suppressSearchUpdate;

    private async void RefreshScenarioUI()
    {
        // Snapshot UI-thread collections before the background task
        var networkApps = Vm.Apps
            .Where(a => !string.IsNullOrEmpty(a.ImagePath))
            .Select(a => (name: System.IO.Path.GetFileNameWithoutExtension(a.ImagePath), path: a.ImagePath))
            .ToList();
        var profileApps = App.Settings.ScenarioProfiles
            .Select(kv => (
                name: !string.IsNullOrEmpty(kv.Value.AppName)
                    ? kv.Value.AppName
                    : System.IO.Path.GetFileNameWithoutExtension(kv.Key),
                path: kv.Key))
            .ToList();
        var limitApps = App.Settings.AppUploadLimitsKBs.Keys
            .Concat(App.Settings.AppDownloadLimitsKBs.Keys)
            .Select(p => (name: System.IO.Path.GetFileNameWithoutExtension(p), path: p))
            .ToList();

        // Reset search UI immediately
        _suppressSearchUpdate = true;
        ScenarioSearchBox.Text = "";
        _suppressSearchUpdate = false;
        ScenarioClearBtn.Visibility = Visibility.Collapsed;
        _selectedScenarioApp = null;
        ScenarioSearchPopup.IsOpen = false;

        // Enumerate ALL running processes in background (MainModule.FileName can be slow)
        _allScenarioApps = await Task.Run(() =>
        {
            var dict = new Dictionary<string, AppSearchEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path)) continue;
                    var name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrEmpty(name)) continue;
                    dict.TryAdd(name, new AppSearchEntry { Name = name, ImagePath = path });
                }
                catch { }
                finally { proc.Dispose(); }
            }

            // Merge apps from network monitor, profiles and per-app limits
            foreach (var (name, path) in networkApps.Concat(profileApps).Concat(limitApps))
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path))
                    dict.TryAdd(name, new AppSearchEntry { Name = name, ImagePath = path });

            return dict.Values
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        RefreshScenarioProfilesList();
    }

    private void OnScenarioSearchGotFocus(object sender, RoutedEventArgs e)
    {
        ScenarioResultsList.ItemsSource = _allScenarioApps;
        if (_allScenarioApps.Count > 0)
            ScenarioSearchPopup.IsOpen = true;
    }

    private void OnScenarioSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchUpdate) return;
        var text = ScenarioSearchBox.Text ?? "";
        ScenarioClearBtn.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var filtered = string.IsNullOrWhiteSpace(text)
            ? _allScenarioApps
            : _allScenarioApps.Where(a => a.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        ScenarioResultsList.ItemsSource = filtered;
        ScenarioSearchPopup.IsOpen = filtered.Count > 0;
    }

    private void OnScenarioResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ScenarioResultsList.SelectedItem is not AppSearchEntry app) return;
        _selectedScenarioApp = app;
        _suppressSearchUpdate = true;
        ScenarioSearchBox.Text = app.Name;
        _suppressSearchUpdate = false;
        ScenarioResultsList.SelectedIndex = -1;
        ScenarioSearchPopup.IsOpen = false;
    }

    private void OnScenarioSearchClear(object sender, RoutedEventArgs e)
    {
        _suppressSearchUpdate = true;
        ScenarioSearchBox.Text = "";
        _suppressSearchUpdate = false;
        _selectedScenarioApp = null;
        ScenarioClearBtn.Visibility = Visibility.Collapsed;
        ScenarioResultsList.ItemsSource = _allScenarioApps;
        ScenarioSearchPopup.IsOpen = _allScenarioApps.Count > 0;
        ScenarioSearchBox.Focus();
    }

    private void RefreshScenarioProfilesList()
    {
        var profiles = App.Settings.ScenarioProfiles;
        var rows = profiles.Select(kv => new ScenarioProfileRow
        {
            ExePath     = kv.Key,
            AppName     = string.IsNullOrEmpty(kv.Value.AppName)
                            ? System.IO.Path.GetFileNameWithoutExtension(kv.Key)
                            : kv.Value.AppName,
            UploadKBs   = kv.Value.UploadKBs.ToString(),
            DownloadKBs = kv.Value.DownloadKBs.ToString(),
        }).ToList();
        ScenarioProfilesControl.ItemsSource = rows;
        ScenarioEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnScenarioQuickProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo) return;
        if (combo.SelectedItem is not LimitProfile profile) return;
        if (combo.Tag is not string path) return;
        if (!App.Settings.ScenarioProfiles.TryGetValue(path, out var entry)) return;
        entry.UploadKBs   = profile.UploadKbps;
        entry.DownloadKBs = profile.DownloadKbps;
        if (Vm.IsScenarioActive) Vm.ApplyScenarioAppLimits(path, entry);
        SettingsManager.Save(App.Settings);
        RefreshScenarioProfilesList();
    }

    private void OnAddScenarioApp(object sender, RoutedEventArgs e)
    {
        // Use item selected from popup; fall back to exact name match if the user typed one in
        AppSearchEntry? selected = _selectedScenarioApp
            ?? _allScenarioApps.FirstOrDefault(a =>
                   a.Name.Equals(ScenarioSearchBox.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
        if (selected == null || string.IsNullOrEmpty(selected.ImagePath)) return;

        var key = selected.ImagePath.ToLowerInvariant();
        if (!App.Settings.ScenarioProfiles.ContainsKey(key))
        {
            App.Settings.ScenarioProfiles[key] = new ScenarioEntry
            {
                AppName     = selected.Name,
                UploadKBs   = 0,
                DownloadKBs = 0,
            };
            SettingsManager.Save(App.Settings);
        }

        ScenarioSearchBox.Text = "";
        _selectedScenarioApp = null;
        RefreshScenarioProfilesList();
    }

    private void OnRemoveScenarioProfile(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;
        App.Settings.ScenarioProfiles.Remove(path);
        if (Vm.IsScenarioActive) Vm.ClearScenarioAppLimits(path);
        SettingsManager.Save(App.Settings);
        RefreshScenarioProfilesList();
    }

    private void OnScenarioUploadLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string path) return;
        if (!int.TryParse(tb.Text.Trim(), out int kbs) || kbs < 0) { kbs = 0; tb.Text = "0"; }
        if (!App.Settings.ScenarioProfiles.TryGetValue(path, out var entry)) return;
        entry.UploadKBs = kbs;
        if (Vm.IsScenarioActive) Vm.ApplyScenarioAppLimits(path, entry);
        SettingsManager.Save(App.Settings);
    }

    private void OnScenarioDownloadLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string path) return;
        if (!int.TryParse(tb.Text.Trim(), out int kbs) || kbs < 0) { kbs = 0; tb.Text = "0"; }
        if (!App.Settings.ScenarioProfiles.TryGetValue(path, out var entry)) return;
        entry.DownloadKBs = kbs;
        if (Vm.IsScenarioActive) Vm.ApplyScenarioAppLimits(path, entry);
        SettingsManager.Save(App.Settings);
    }

    // ────────── Global cap ──────────

    private void OnClearGlobalCap(object sender, RoutedEventArgs e)
    {
        Vm.GlobalUploadCapKBs = 0;
        // Untick the master switch — its change handler removes the cap across both layers.
        Vm.IsGlobalUploadCapEnabled = false;
    }

    private void OnOpenNetworkConnections(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ncpa.cpl")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }


}
