using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Bansa.Services;
using Bansa.ViewModels;
using WinForms = System.Windows.Forms;

namespace Bansa.Views;

public partial class HistoryView : UserControl
{
    private readonly HistoryStore _history = new();

    public record HistoryRow(string Name, long BytesIn, long BytesOut)
    {
        public string DownText => Format.Bytes(BytesIn);
        public string UpText   => Format.Bytes(BytesOut);
    }

    public record ActivityRow(DateTime When, string App, string Action, string? Detail)
    {
        public string TimeText   => When.ToString("MMM d, HH:mm:ss");
        public string DetailText => Detail ?? "";
        public string ActionText => Action switch
        {
            "blocked"       => "Blocked",
            "unblocked"     => "Unblocked",
            "limit_set"     => "Limit applied",
            "limit_cleared" => "Limit removed",
            "priority_on"   => "High priority ON",
            "priority_off"  => "High priority OFF",
            _               => Action,
        };
    }

    public HistoryView()
    {
        InitializeComponent();
        FromPicker.SelectedDate = DateTime.Today;
        ToPicker.SelectedDate = DateTime.Today;
        QuotaBox.Text = App.Settings.MonthlyQuotaGB > 0 ? App.Settings.MonthlyQuotaGB.ToString() : "0";
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _history.Dispose();
    }

    // ── Monthly usage + budget ────────────────────────────────────────────────

    private void RefreshMonthUsage()
    {
        var monthStartLocal = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var (bytesIn, bytesOut) = _history.GetRangeTotals(monthStartLocal.ToUniversalTime(), DateTime.UtcNow);
        long total = bytesIn + bytesOut;

        int quotaGb = App.Settings.MonthlyQuotaGB;
        MonthText.Text = quotaGb > 0
            ? $"{Format.Bytes(total)} / {quotaGb} GB"
            : Format.Bytes(total);
        MonthText.ToolTip = $"↓ {Format.Bytes(bytesIn)}   ↑ {Format.Bytes(bytesOut)}  (since {monthStartLocal:MMM d})";

        bool over = quotaGb > 0 && total > (long)quotaGb * 1024L * 1024L * 1024L;
        MonthText.SetResourceReference(ForegroundProperty, over ? "DangerBrush" : "TextBrush");
        if (over) MonthText.Text += "  — over budget";
    }

    private void OnQuotaChanged(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(QuotaBox.Text.Trim(), out var gb) || gb < 0) gb = App.Settings.MonthlyQuotaGB;
        gb = Math.Min(gb, 1_000_000);
        QuotaBox.Text = gb.ToString();
        if (gb != App.Settings.MonthlyQuotaGB)
        {
            App.Settings.MonthlyQuotaGB = gb;
            SettingsManager.Save(App.Settings);
        }
        RefreshMonthUsage();
    }

    private void OnQuotaKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnQuotaChanged(sender, e);
    }

    private void OnPresetToday(object sender, RoutedEventArgs e)
    {
        FromPicker.SelectedDate = DateTime.Today;
        ToPicker.SelectedDate = DateTime.Today;
        Reload();
    }
    private void OnPreset7(object sender, RoutedEventArgs e)
    {
        FromPicker.SelectedDate = DateTime.Today.AddDays(-7);
        ToPicker.SelectedDate = DateTime.Today;
        Reload();
    }
    private void OnPreset30(object sender, RoutedEventArgs e)
    {
        FromPicker.SelectedDate = DateTime.Today.AddDays(-30);
        ToPicker.SelectedDate = DateTime.Today;
        Reload();
    }
    private void OnApply(object sender, RoutedEventArgs e) => Reload();

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var rows = HistoryGrid.ItemsSource as IEnumerable<HistoryRow>;
        if (rows is null) return;

        using var dlg = new WinForms.SaveFileDialog
        {
            Title = "Export bandwidth history",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"bansa-history-{DateTime.Today:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;

        try
        {
            using var sw = new StreamWriter(dlg.FileName, append: false, System.Text.Encoding.UTF8);
            sw.WriteLine("App,Downloaded,Uploaded,Downloaded (bytes),Uploaded (bytes)");
            foreach (var r in rows)
                sw.WriteLine($"\"{r.Name.Replace("\"", "\"\"")}\"," +
                             $"\"{r.DownText}\",\"{r.UpText}\"," +
                             $"{r.BytesIn},{r.BytesOut}");
            ConfirmDialog.Show("Export complete", $"Exported {rows.Count()} rows to:\n{dlg.FileName}", confirmText: "OK", cancelText: null);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show("Export failed", ex.Message, confirmText: "OK", cancelText: null);
        }
    }

    public void Reload()
    {
        var from = (FromPicker.SelectedDate ?? DateTime.Today).Date;
        var to   = (ToPicker.SelectedDate ?? DateTime.Today).Date.AddDays(1).AddSeconds(-1);
        try
        {
            var rows = _history.GetTotals(from.ToUniversalTime(), to.ToUniversalTime())
                .Select(r => new HistoryRow(r.Name, r.BytesIn, r.BytesOut))
                .ToList();
            HistoryGrid.ItemsSource = rows;

            long sumIn = rows.Sum(r => r.BytesIn);
            long sumOut = rows.Sum(r => r.BytesOut);
            TotalDownText.Text = Format.Bytes(sumIn);
            TotalUpText.Text = Format.Bytes(sumOut);
            RangeText.Text = $"{from:MMM d} – {(ToPicker.SelectedDate ?? DateTime.Today):MMM d, yyyy}";

            var activityRows = _history.GetActivityLog(from.ToUniversalTime(), to.ToUniversalTime())
                .Select(r => new ActivityRow(r.When, r.App, r.Action, r.Detail))
                .ToList();
            ActivityGrid.ItemsSource = activityRows;

            RefreshMonthUsage();
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show("Could not load history", ex.Message, confirmText: "OK", cancelText: null);
        }
    }
}
