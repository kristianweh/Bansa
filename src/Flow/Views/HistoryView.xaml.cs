using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Flow.Services;
using Flow.ViewModels;
using WinForms = System.Windows.Forms;

namespace Flow.Views;

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
        FromPicker.SelectedDate = DateTime.Today.AddDays(-7);
        ToPicker.SelectedDate = DateTime.Today;
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _history.Dispose();
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
            FileName = $"flow-history-{DateTime.Today:yyyy-MM-dd}.csv",
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
            MessageBox.Show($"Exported {rows.Count()} rows to:\n{dlg.FileName}",
                "Flow — Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Export failed: " + ex.Message, "Flow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Reload()
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
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not load history: " + ex.Message, "Flow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
