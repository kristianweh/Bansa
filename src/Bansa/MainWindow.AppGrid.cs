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
    // ────────── App grid column sorting (keeps SortPriority as locked primary key) ──────────

    private void OnAppGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;   // suppress default behaviour

        // Toggle direction on the clicked column
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        // Clear all column sort glyphs, then set only the clicked one
        foreach (var col in AppGrid.Columns) col.SortDirection = null;
        e.Column.SortDirection = dir;

        // Re-apply: SortPriority (locked) + user column
        var lv = (ListCollectionView)Vm.AppsView;
        using (lv.DeferRefresh())
        {
            Vm.AppsView.SortDescriptions.Clear();
            Vm.AppsView.SortDescriptions.Add(new SortDescription(
                nameof(AppRowViewModel.SortPriority), ListSortDirection.Ascending));
            if (!string.IsNullOrEmpty(e.Column.SortMemberPath))
                Vm.AppsView.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, dir));
        }

        App.Settings.AppSortMemberPath = e.Column.SortMemberPath ?? "";
        App.Settings.AppSortDescending = dir == ListSortDirection.Descending;
        SettingsManager.Save(App.Settings);
    }

    // ── Column width + visibility persistence ─────────────────────────────────

    private void SaveAppGridColumns()
    {
        var widths  = App.Settings.AppGridColumnWidths;
        var hidden  = App.Settings.AppGridHiddenColumns;
        widths.Clear();
        hidden.Clear();
        foreach (var col in AppGrid.Columns)
        {
            var key = col.Header as string;
            if (string.IsNullOrEmpty(key)) continue;
            if (col.ActualWidth > 0) widths[key] = col.ActualWidth;
            if (col.Visibility == Visibility.Collapsed) hidden.Add(key);
        }
        SettingsManager.Save(App.Settings);
    }

    private void RestoreAppGridColumns()
    {
        var widths = App.Settings.AppGridColumnWidths;
        var hidden = App.Settings.AppGridHiddenColumns;
        foreach (var col in AppGrid.Columns)
        {
            var key = col.Header as string;
            if (string.IsNullOrEmpty(key)) continue;
            if (widths.TryGetValue(key, out double w) && w > 0)
                col.Width = new DataGridLength(w);
            col.Visibility = hidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ── Right-click column header → show/hide columns ────────────────────────

    private void OnAppGridHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        _contextApp = row?.DataContext as AppRowViewModel;

        var header = FindVisualAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column == null) return;

        e.Handled = true;

        var menu = new ContextMenu();
        foreach (var col in AppGrid.Columns)
        {
            var label = col.Header as string;
            if (string.IsNullOrEmpty(label)) continue;
            var captured = col;
            var item = new MenuItem
            {
                Header      = label,
                IsCheckable = true,
                IsChecked   = col.Visibility == Visibility.Visible,
            };
            item.Click += (_, _) =>
            {
                captured.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveAppGridColumns();
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void RestoreAppGridSort()
    {
        var path = App.Settings.AppSortMemberPath;
        var dir  = App.Settings.AppSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        foreach (var col in AppGrid.Columns)
            col.SortDirection = col.SortMemberPath == path ? dir : (ListSortDirection?)null;

        if (!string.IsNullOrEmpty(path))
        {
            var lv = (ListCollectionView)Vm.AppsView;
            using (lv.DeferRefresh())
            {
                Vm.AppsView.SortDescriptions.Clear();
                Vm.AppsView.SortDescriptions.Add(new SortDescription(
                    nameof(AppRowViewModel.SortPriority), ListSortDirection.Ascending));
                Vm.AppsView.SortDescriptions.Add(new SortDescription(path, dir));
            }
        }
    }

    // ────────── Context menu / processes ──────────

    private AppRowViewModel? _contextApp;

    private AppRowViewModel? Selected => _contextApp ?? AppGrid.SelectedItem as AppRowViewModel;

    private void OnDashAppGridRowRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        _contextApp = row?.DataContext as AppRowViewModel;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) Vm.PinAppCommand.Execute(Selected); }
    private void OnUnpinClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) Vm.UnpinAppCommand.Execute(Selected); }

    private void OnBlockClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) _ = Vm.BlockCommand.ExecuteAsync(Selected); }
    private void OnUnblockClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) _ = Vm.UnblockCommand.ExecuteAsync(Selected); }
    private void OnSetLimitClick(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        var dlg = new SetLimitWindow(Selected.Name, Selected.UploadLimitKbps, Selected.DownloadLimitKbps,
                                     App.Settings.ConnectionUploadMbps, App.Settings.ConnectionDownloadMbps,
                                     Selected.HasUdpConnections) { Owner = this };
        if (dlg.ShowDialog() == true)
            _ = Vm.ApplyLimitsCommand.ExecuteAsync((Selected, dlg.UploadKbps, dlg.DownloadKbps));
    }

    private void OnClearLimitsClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not null)
            _ = Vm.ApplyLimitsCommand.ExecuteAsync((Selected, 0, 0));
    }
    private void OnShowPidsClick(object sender, RoutedEventArgs e) => ShowPidsFor(Selected);
    private void OnAppDoubleClick(object sender, MouseButtonEventArgs e) => ShowAppDetail(Selected);

    private void ShowAppDetail(AppRowViewModel? app)
    {
        if (app is null) return;
        var w = new AppDetailWindow(app) { Owner = this };
        w.Show();
    }

    private void ShowPidsFor(AppRowViewModel? app)
    {
        if (app is null) return;
        if (app.Processes.Count == 0)
        {
            Views.ConfirmDialog.Show("No active processes", "This app has no active processes right now.", confirmText: "OK", cancelText: null);
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"{app.Name}  —  {app.Processes.Count} process(es):");
        sb.AppendLine();
        foreach (var p in app.Processes.OrderByDescending(p => p.BytesInPerSec + p.BytesOutPerSec))
        {
            sb.AppendLine($"  PID {p.Pid}");
            sb.AppendLine($"    ↓ {p.DownRate}   ↑ {p.UpRate}");
            sb.AppendLine($"    Connections: {p.ConnectionCount}");
            if (!string.IsNullOrEmpty(p.ImagePath))
                sb.AppendLine($"    {p.ImagePath}");
            sb.AppendLine();
        }
        Views.ConfirmDialog.Show($"Processes for {app.Name}", sb.ToString(), confirmText: "OK", cancelText: null);
    }


    // ────────── Column auto-size on separator double-click (Excel / Sheets style) ──────────

    /// <summary>
    /// Fires on PreviewMouseDoubleClick for both DataGrids.  When the user double-clicks the
    /// right (or left) gripper of a column header the column is sized to fit its widest visible
    /// cell — the same behaviour as double-clicking a column separator in Excel or Google Sheets.
    ///
    /// Implementation: set Width=Auto so WPF measures the content, force a layout pass to get
    /// the measured ActualWidth, then lock it back to a fixed pixel value so the column doesn't
    /// keep auto-resizing as data changes.
    /// </summary>
    private void OnColumnHeaderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        // Only react when the pointer is inside a DataGridColumnHeader
        var header = FindVisualAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column == null) return;

        // Grippers in the Controls.xaml template are Width="8".
        // Right gripper (pos.X near right edge) → resize this column.
        // Left gripper  (pos.X near left edge)  → resize the preceding column.
        var pos = e.GetPosition(header);
        DataGridColumn? target = null;

        if (pos.X >= header.ActualWidth - 8)
        {
            target = header.Column;
        }
        else if (pos.X <= 8)
        {
            var idx = grid.Columns.IndexOf(header.Column);
            if (idx > 0) target = grid.Columns[idx - 1];
        }

        if (target == null || !target.CanUserResize) return;

        // Two-step: Auto → measure → lock to pixels.
        target.Width = DataGridLength.Auto;
        grid.UpdateLayout();
        target.Width = new DataGridLength(target.ActualWidth);
        e.Handled = true;   // prevent OnAppDoubleClick from also firing
    }

    /// <summary>Walks the visual tree upward looking for an ancestor of type <typeparamref name="T"/>.</summary>
    private static T? FindVisualAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T result) return result;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }


}
