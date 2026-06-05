using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Bansa.Views;

/// <summary>
/// Reusable themed replacement for MessageBox confirms / info dialogs.
/// Use the static <see cref="Show"/> helper. Pass <c>cancelText: null</c> for a
/// single-button info dialog (e.g. an operation result).
/// </summary>
public partial class ConfirmDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private ConfirmDialog(string title, string message, string confirmText, string? cancelText, bool danger)
    {
        InitializeComponent();
        Title          = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmBtn.Content = confirmText;

        if (cancelText is null)
            CancelBtn.Visibility = Visibility.Collapsed;
        else
            CancelBtn.Content = cancelText;

        if (danger)
        {
            ConfirmBtn.Style = (Style)FindResource("DangerButton");
            AccentBar.Background = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
    }

    /// <summary>
    /// Shows a modal themed dialog centered on the main window.
    /// Returns true when the confirm button is pressed.
    /// </summary>
    public static bool Show(string title, string message,
                            string confirmText = "Confirm", string? cancelText = "Cancel",
                            bool danger = false)
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new ConfirmDialog(title, message, confirmText, cancelText, danger);
        if (owner is not null && owner.IsLoaded && !ReferenceEquals(owner, dlg))
            dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void OnConfirm(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnCancel(object sender, RoutedEventArgs e)  { DialogResult = false; Close(); }
}
