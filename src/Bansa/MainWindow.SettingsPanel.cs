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
    // ── Clear hotkey ──────────────────────────────────────────────────────────

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey) CancelHotkeyCapture();
        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, _hotKeyId); } catch { }
        _hotkeyVk = 0;
        App.Settings.HotkeyVirtualKey = 0;
        SettingsManager.Save(App.Settings);
        HotkeyLabel.Text = "None";
        Vm.StatusText = "Global hotkey cleared.";
    }

    // ── Ping target management ────────────────────────────────────────────────

    // Lightweight display wrapper so the ComboBox shows "Label (IP)" while
    // code can still extract the raw IP/hostname.
    private sealed class PingTargetItem
    {
        public string Target  { get; }
        public string Display { get; }
        public PingTargetItem(string target, string? label)
        {
            Target  = target;
            Display = string.IsNullOrEmpty(label) ? target : $"{label}  ({target})";
        }
        public override string ToString() => Display;
    }

    private PingTargetItem? SelectedPingItem =>
        PingTargetCombo.SelectedItem as PingTargetItem;

    private void PopulatePingTargetCombo()
    {
        PingTargetCombo.SelectionChanged -= OnPingTargetSelectionChanged;
        PingTargetCombo.Items.Clear();
        foreach (var t in App.Settings.PingTargets)
        {
            App.Settings.PingTargetLabels.TryGetValue(t, out var lbl);
            PingTargetCombo.Items.Add(new PingTargetItem(t, lbl));
        }
        var idx = App.Settings.PingTargets
            .FindIndex(t => string.Equals(t, App.Settings.PingTarget, StringComparison.OrdinalIgnoreCase));
        PingTargetCombo.SelectedIndex = idx >= 0 ? idx : (App.Settings.PingTargets.Count > 0 ? 0 : -1);
        PingTargetCombo.SelectionChanged += OnPingTargetSelectionChanged;
        SyncPingLabelBox();
    }

    private void SyncPingLabelBox()
    {
        var item = SelectedPingItem;
        if (item != null && App.Settings.PingTargetLabels.TryGetValue(item.Target, out var lbl))
            PingLabelBox.Text = lbl;
        else
            PingLabelBox.Text = "";
    }

    private void OnPingTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncPingLabelBox();
    }

    private void OnSetPingTarget(object sender, RoutedEventArgs e)
    {
        var item = SelectedPingItem;
        if (item is null) return;
        Vm.ChangePingTarget(item.Target);
        // Repopulate so active-target indicator updates
        PopulatePingTargetCombo();
        Vm.StatusText = $"Ping target set to {item.Display}.";
    }

    private void OnAddPingTarget(object sender, RoutedEventArgs e)
    {
        var target = NewPingTargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target)) return;
        if (!App.Settings.PingTargets.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            App.Settings.PingTargets.Add(target);
            SettingsManager.Save(App.Settings);
        }
        Vm.ChangePingTarget(target);
        NewPingTargetBox.Text = "";
        PopulatePingTargetCombo();
        Vm.StatusText = $"Added and switched to {target}.";
    }

    private void OnRemovePingTarget(object sender, RoutedEventArgs e)
    {
        var item = SelectedPingItem;
        if (item is null) return;
        App.Settings.PingTargets.Remove(item.Target);
        App.Settings.PingTargetLabels.Remove(item.Target);
        if (string.Equals(App.Settings.PingTarget, item.Target, StringComparison.OrdinalIgnoreCase)
            && App.Settings.PingTargets.Count > 0)
            Vm.ChangePingTarget(App.Settings.PingTargets[0]);
        SettingsManager.Save(App.Settings);
        PopulatePingTargetCombo();
        Vm.StatusText = $"Removed ping target {item.Target}.";
    }

    private void OnSavePingLabel(object sender, RoutedEventArgs e)
    {
        var item = SelectedPingItem;
        if (item is null) return;
        var label = PingLabelBox.Text.Trim();
        if (string.IsNullOrEmpty(label))
            App.Settings.PingTargetLabels.Remove(item.Target);
        else
            App.Settings.PingTargetLabels[item.Target] = label;
        SettingsManager.Save(App.Settings);
        PopulatePingTargetCombo();   // refresh display text
        Vm.NotifyPingDisplayLabel();
        Vm.StatusText = string.IsNullOrEmpty(label)
            ? $"Label cleared for {item.Target}."
            : $"Label \"{label}\" saved for {item.Target}.";
    }

    private void OnDualScaleClick(object sender, RoutedEventArgs e)
    {
        _dualScale = DualScaleBtn.IsChecked == true;
        App.Settings.DualScale = _dualScale;
        SettingsManager.Save(App.Settings);
        DrawMainChart(_chartHistory);
    }

    // ── Global hotkey rebinding ────────────────────────────────────────────────

    private void OnChangeHotkeyClick(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyCapturing.Visibility = Visibility.Visible;
        ChangeHotkeyBtn.IsEnabled = false;
        Focus(); // window-level focus lets OnPreviewKeyDown intercept the next key
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Never intercept PrintScreen — let the OS / Snipping Tool handle it.
        if (e.Key is Key.PrintScreen or Key.Snapshot)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        if (_capturingHotkey)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { CancelHotkeyCapture(); return; }
            // Skip bare modifier keys
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                     or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, _hotKeyId);
            if (RegisterHotKey(hwnd, _hotKeyId, MOD_CONTROL | MOD_SHIFT, vk))
            {
                _hotkeyVk = vk;
                App.Settings.HotkeyVirtualKey = (int)vk;
                SettingsManager.Save(App.Settings);
                HotkeyLabel.Text = $"Ctrl+Shift+{key}";
                Vm.StatusText = $"Hotkey changed to Ctrl+Shift+{key}.";
            }
            else
            {
                RegisterHotKey(hwnd, _hotKeyId, MOD_CONTROL | MOD_SHIFT, _hotkeyVk);
                Vm.StatusText = "Couldn't bind that key — try a different one.";
            }
            CancelHotkeyCapture();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void CancelHotkeyCapture()
    {
        _capturingHotkey = false;
        HotkeyCapturing.Visibility = Visibility.Collapsed;
        ChangeHotkeyBtn.IsEnabled = true;
    }


    // ────────── Settings tabs ──────────

    private void OnSettingsTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        // Settings is two tabs since the network cards moved to Limits & Scenarios;
        // SettingsTabNetwork/SettingsTabScenario remain as empty (re-parented) containers.
        SettingsTabGeneral.Visibility    = tag == "general"    ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabNetwork.Visibility    = Visibility.Collapsed;
        SettingsTabAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabScenario.Visibility   = Visibility.Collapsed;
    }

    // ── Connection speed ──────────────────────────────────────────────────────

    private void RefreshConnectionSpeedUI()
    {
        bool gbps = App.Settings.ConnectionSpeedUnitGbps;
        SpeedUnitGbpsRadio.IsChecked = gbps;
        SpeedUnitMbpsRadio.IsChecked = !gbps;
        if (gbps)
        {
            ConnectionUploadBox.Text   = (App.Settings.ConnectionUploadMbps   / 1000.0).ToString("0.###");
            ConnectionDownloadBox.Text = (App.Settings.ConnectionDownloadMbps / 1000.0).ToString("0.###");
            SpeedUnitLabel.Text = "Gbps";
        }
        else
        {
            ConnectionUploadBox.Text   = App.Settings.ConnectionUploadMbps.ToString();
            ConnectionDownloadBox.Text = App.Settings.ConnectionDownloadMbps.ToString();
            SpeedUnitLabel.Text = "Mbps";
        }
        ProfilesList.ItemsSource = App.Settings.LimitProfiles;
    }

    private void OnSpeedUnitChanged(object sender, RoutedEventArgs e)
    {
        if (SpeedUnitGbpsRadio is null) return;
        App.Settings.ConnectionSpeedUnitGbps = SpeedUnitGbpsRadio.IsChecked == true;
        SettingsManager.Save(App.Settings);
        RefreshConnectionSpeedUI();
    }

    private void OnSaveConnectionSpeed(object sender, RoutedEventArgs e)
    {
        bool gbps = App.Settings.ConnectionSpeedUnitGbps;
        if (gbps)
        {
            if (double.TryParse(ConnectionUploadBox.Text.Trim(),   out double u) && u >= 0)
                App.Settings.ConnectionUploadMbps   = (int)(u * 1000);
            if (double.TryParse(ConnectionDownloadBox.Text.Trim(), out double d) && d >= 0)
                App.Settings.ConnectionDownloadMbps = (int)(d * 1000);
        }
        else
        {
            if (int.TryParse(ConnectionUploadBox.Text.Trim(),   out int u) && u >= 0)
                App.Settings.ConnectionUploadMbps   = u;
            if (int.TryParse(ConnectionDownloadBox.Text.Trim(), out int d) && d >= 0)
                App.Settings.ConnectionDownloadMbps = d;
        }
        SettingsManager.Save(App.Settings);
        Vm.StatusText = "Connection speed saved.";
    }

    // ── Limit profiles ────────────────────────────────────────────────────────

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        string name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!int.TryParse(ProfileUpBox.Text,   out int up)   || up   < 0) up   = 0;
        if (!int.TryParse(ProfileDownBox.Text, out int down) || down < 0) down = 0;
        App.Settings.LimitProfiles.Add(new Services.LimitProfile { Name = name, UploadKbps = up, DownloadKbps = down });
        SettingsManager.Save(App.Settings);
        ProfileNameBox.Text  = "";
        ProfileUpBox.Text    = "0";
        ProfileDownBox.Text  = "0";
        RefreshProfilesList();
    }

    private void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Services.LimitProfile profile) return;
        var dlg = new Views.EditProfileDialog(profile.Name, profile.UploadKbps, profile.DownloadKbps)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
        {
            profile.Name         = dlg.ProfileName;
            profile.UploadKbps   = dlg.UploadKbps;
            profile.DownloadKbps = dlg.DownloadKbps;
            SettingsManager.Save(App.Settings);
            RefreshProfilesList();
        }
    }

    private void OnRemoveProfile(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is string name)
        {
            App.Settings.LimitProfiles.RemoveAll(p => p.Name == name);
            SettingsManager.Save(App.Settings);
            RefreshProfilesList();
        }
    }

    private void RefreshProfilesList()
    {
        ProfilesList.ItemsSource = null;
        ProfilesList.ItemsSource = App.Settings.LimitProfiles;
    }


    // ────────── Settings ──────────

    private void OnThemeToggleClick(object sender, RoutedEventArgs e) => Vm.ToggleThemeCommand.Execute(null);

    private void ApplyBrandImages(AppTheme theme)
    {
        var logoUri = theme == AppTheme.Dark
            ? new Uri("pack://application:,,,/Bansa;component/Resources/bansa-dark.png")
            : new Uri("pack://application:,,,/Bansa;component/Resources/bansa-light.png");
        BrandLogo.Source = new System.Windows.Media.Imaging.BitmapImage(logoUri);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/Bansa;component/Resources/Icon_bansa-dark.ico"));
    }
    private void OnUnitBytesClick(object sender, RoutedEventArgs e) => Vm.UseBitsUnit = false;
    private void OnUnitBitsClick(object sender, RoutedEventArgs e)  => Vm.UseBitsUnit = true;

    private void PopulateSwatches(ItemsControl host, string currentHex, Action<string> onPick)
        => PopulateSwatches(host, _palette, currentHex, onPick);

    private void PopulateSwatches(ItemsControl host, IReadOnlyList<string> palette, string currentHex, Action<string> onPick)
    {
        host.Items.Clear();
        foreach (var hex in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var border = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(string.Equals(hex, currentHex, StringComparison.OrdinalIgnoreCase) ? 3 : 1),
                BorderBrush = new SolidColorBrush(Colors.White),
                Cursor = Cursors.Hand,
                Tag = hex,
            };
            border.MouseLeftButtonDown += (_, _) =>
            {
                onPick(hex);
                foreach (var child in host.Items.OfType<Border>())
                    child.BorderThickness = new Thickness(string.Equals((string)child.Tag!, hex, StringComparison.OrdinalIgnoreCase) ? 3 : 1);
            };
            host.Items.Add(border);
        }
    }

    // ── Temperature bands (stepped color) ──────────────────────────────────────

    /// <summary>Populates the 3 band swatch rows + threshold boxes + range labels from settings.</summary>
    private void SetupTempBandSwatches()
    {
        PopulateSwatches(TempCoolBandSwatches, _tempPalette, App.Settings.TempBandCoolColorHex, hex => Vm.SetTempCoolBand(hex));
        PopulateSwatches(TempWarmBandSwatches, _tempPalette, App.Settings.TempBandWarmColorHex, hex => Vm.SetTempWarmBand(hex));
        PopulateSwatches(TempHotBandSwatches,  _tempPalette, App.Settings.TempBandHotColorHex,  hex => Vm.SetTempHotBand(hex));
        TempWarmThreshBox.Text = App.Settings.TempWarmThresholdC.ToString();
        TempHotThreshBox.Text  = App.Settings.TempHotThresholdC.ToString();
        UpdateTempBandLabels();
    }

    /// <summary>Validates and persists the warm/hot thresholds, then refreshes the range labels.</summary>
    private void OnTempThresholdChanged(object sender, RoutedEventArgs e)
    {
        int warm = App.Settings.TempWarmThresholdC;
        int hot  = App.Settings.TempHotThresholdC;
        if (int.TryParse(TempWarmThreshBox.Text, out int w)) warm = Math.Clamp(w, 0, 120);
        if (int.TryParse(TempHotThreshBox.Text,  out int h)) hot  = Math.Clamp(h, 0, 121);
        if (hot <= warm) hot = Math.Min(warm + 1, 121);   // hot must sit above warm

        App.Settings.TempWarmThresholdC = warm;
        App.Settings.TempHotThresholdC  = hot;
        SettingsManager.Save(App.Settings);

        // Reflect any clamping back into the boxes + labels.
        TempWarmThreshBox.Text = warm.ToString();
        TempHotThreshBox.Text  = hot.ToString();
        UpdateTempBandLabels();
    }

    private void UpdateTempBandLabels()
    {
        int warm = App.Settings.TempWarmThresholdC;
        int hot  = App.Settings.TempHotThresholdC;
        TempCoolBandLabel.Text = $"Cool — below {warm}°C";
        TempWarmBandLabel.Text = $"Warm — {warm}–{hot - 1}°C";
        TempHotBandLabel.Text  = $"Hot — {hot}°C and above";
    }

    // Persist a domain's dominant color; live-refresh the accent if that domain is active.
    private void SetDomainColor(AppDomainMode mode, string hex)
    {
        if (mode == AppDomainMode.Network) App.Settings.NetworkColorHex = hex;
        else                               App.Settings.HardwareColorHex = hex;
        SettingsManager.Save(App.Settings);
        if (DomainManager.Current == mode) DomainManager.Apply(mode);
    }


    // ────────── Behaviour toggles ──────────

    private void InitBehaviourToggles()
    {
        MinimizeOnCloseToggle.IsChecked  = App.Settings.MinimizeOnClose;
        StartMinimizedToggle.IsChecked   = App.Settings.StartMinimizedToTray;
        ShowTrayIconToggle.IsChecked     = App.Settings.ShowTrayIcon;
    }

    private void OnMinimizeOnCloseToggleClick(object sender, RoutedEventArgs e)
    {
        App.Settings.MinimizeOnClose = MinimizeOnCloseToggle.IsChecked == true;
        SettingsManager.Save(App.Settings);
    }

    private void OnStartMinimizedToggleClick(object sender, RoutedEventArgs e)
    {
        App.Settings.StartMinimizedToTray = StartMinimizedToggle.IsChecked == true;
        SettingsManager.Save(App.Settings);
    }

    private void OnShowTrayIconToggleClick(object sender, RoutedEventArgs e)
    {
        App.Settings.ShowTrayIcon = ShowTrayIconToggle.IsChecked == true;
        SettingsManager.Save(App.Settings);
    }

    // ────────── Auto-start ──────────

    private void InitAutoStartToggle()
    {
        bool enabled = AutoStartManager.IsEnabled();
        AutoStartToggle.IsChecked = enabled;
        AutoStartStatusLabel.Text = enabled
            ? "Enabled — Bansa will launch at Windows login"
            : "Disabled — Bansa won't start automatically";
    }

    private async void OnAutoStartToggleClick(object sender, RoutedEventArgs e)
    {
        bool enable = AutoStartToggle.IsChecked == true;
        AutoStartToggle.IsEnabled = false;
        AutoStartStatusLabel.Text = enable ? "Enabling…" : "Disabling…";

        bool ok = await AutoStartManager.SetAsync(enable);

        AutoStartToggle.IsEnabled = true;
        if (ok)
        {
            AutoStartStatusLabel.Text = enable
                ? "Enabled — Bansa will launch at Windows login"
                : "Disabled — Bansa won't start automatically";
            Vm.StatusText = enable ? "Auto-start enabled." : "Auto-start disabled.";
        }
        else
        {
            // Revert the toggle — the operation failed (likely needs admin, or schtasks error)
            AutoStartToggle.IsChecked = !enable;
            AutoStartStatusLabel.Text = "Failed — run Bansa as administrator to change this setting";
            Vm.StatusText = "Auto-start change failed. Try running Bansa as administrator.";
        }
    }

    // ────────── Update check (Settings → General version footer) ──────────

    private string _latestReleaseUrl = "";

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckUpdatesBtn.IsEnabled = false;
        OpenReleaseBtn.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text     = "Checking…";

        var r = await UpdateChecker.CheckAsync();

        CheckUpdatesBtn.IsEnabled = true;
        if (!r.Success)
        {
            UpdateStatusText.Text = "Couldn't reach GitHub (" + r.Error + ")";
        }
        else if (r.UpdateAvailable)
        {
            UpdateStatusText.Text = $"{r.LatestTag} is available";
            _latestReleaseUrl     = r.ReleaseUrl;
            OpenReleaseBtn.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatusText.Text = "You're up to date";
        }
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_latestReleaseUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_latestReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Debug("Open release page", ex); }
    }

    // ────────── Settings export / import ──────────

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.SaveFileDialog
        {
            Title    = "Export Bansa settings",
            Filter   = "JSON files (*.json)|*.json",
            FileName = $"bansa-settings-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                App.Settings,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(dlg.FileName, json);
            Vm.StatusText = $"Settings exported to {System.IO.Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Show("Export failed", ex.Message, confirmText: "OK", cancelText: null);
        }
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title  = "Import Bansa settings",
            Filter = "JSON files (*.json)|*.json",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        BansaSettings imported;
        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            imported = System.Text.Json.JsonSerializer.Deserialize<BansaSettings>(json)
                       ?? throw new InvalidOperationException("Empty or null settings file.");
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Show("Could not read settings file", ex.Message, confirmText: "OK", cancelText: null);
            return;
        }

        var ok = Views.ConfirmDialog.Show(
            "Import settings?",
            "This will replace all current settings — limits, profiles, colors, and preferences.",
            confirmText: "Replace settings", cancelText: "Cancel", danger: true);
        if (!ok) return;

        App.Settings = imported;
        SettingsManager.Save(App.Settings);
        ApplyImportedSettings();
        Vm.StatusText = "Settings imported successfully.";
    }

    private void ApplyImportedSettings()
    {
        // Units
        bool useBits = App.Settings.RateUnit.Equals("Bits", StringComparison.OrdinalIgnoreCase);
        Vm.UseBitsUnit      = useBits;
        UnitBitsRadio.IsChecked  = useBits;
        UnitBytesRadio.IsChecked = !useBits;

        // Theme
        bool dark = App.Settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        if (Vm.IsDarkTheme != dark) Vm.IsDarkTheme = dark;

        // Accent is owned by DomainManager (per-domain dominant color) — re-assert it
        DomainManager.Apply(DomainManager.Current);

        // Colors
        Vm.SetDownColor(App.Settings.DownColorHex);
        Vm.SetUpColor(App.Settings.UpColorHex);
        Vm.SetCpuColor(App.Settings.CpuColorHex);
        Vm.SetGpuColor(App.Settings.GpuColorHex);
        Vm.SetRamColor(App.Settings.RamColorHex);
        PopulateSwatches(DownGraphSwatches, App.Settings.DownColorHex,     hex => Vm.SetDownColor(hex));
        PopulateSwatches(UpGraphSwatches,   App.Settings.UpColorHex,       hex => Vm.SetUpColor(hex));
        PopulateSwatches(CpuColorSwatches,  App.Settings.CpuColorHex,      hex => Vm.SetCpuColor(hex));
        PopulateSwatches(GpuColorSwatches,  App.Settings.GpuColorHex,      hex => Vm.SetGpuColor(hex));
        PopulateSwatches(RamColorSwatches,  App.Settings.RamColorHex,      hex => Vm.SetRamColor(hex));
        SetupTempBandSwatches();
        PopulateSwatches(PingGoodSwatches,  App.Settings.PingGoodColorHex, hex => Vm.SetPingGoodColor(hex));
        PopulateSwatches(PingBadSwatches,   App.Settings.PingBadColorHex,  hex => Vm.SetPingBadColor(hex));
        PopulateSwatches(NetworkAccentSwatches,  _domainPalette, App.Settings.NetworkColorHex,  hex => SetDomainColor(AppDomainMode.Network, hex));
        PopulateSwatches(HardwareAccentSwatches, _domainPalette, App.Settings.HardwareColorHex, hex => SetDomainColor(AppDomainMode.Hardware, hex));

        // Behavior toggles
        InitBehaviourToggles();

        // Dual-scale chart toggle
        _dualScale = App.Settings.DualScale;
        DualScaleBtn.IsChecked = _dualScale;

        // Ping targets
        PopulatePingTargetCombo();

        // Connection speed (refreshed next time user opens Network tab)
        // Columns + sort
        RestoreAppGridSort();
        RestoreAppGridColumns();
    }


}
