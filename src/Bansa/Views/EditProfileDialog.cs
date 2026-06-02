using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bansa.Views;

/// <summary>Simple modal dialog for renaming a limit profile and changing its KB/s values.</summary>
internal sealed class EditProfileDialog : Window
{
    private readonly TextBox _nameBox;
    private readonly TextBox _upBox;
    private readonly TextBox _downBox;

    public string ProfileName  => _nameBox.Text.Trim();
    public int    UploadKbps   => int.TryParse(_upBox.Text,   out var u) ? Math.Max(0, u) : 0;
    public int    DownloadKbps => int.TryParse(_downBox.Text, out var d) ? Math.Max(0, d) : 0;

    public EditProfileDialog(string name, int upKbps, int downKbps)
    {
        Title  = "Edit Profile";
        Width  = 320;
        SizeToContent = SizeToContent.Height;
        ResizeMode    = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // Apply the app's current theme so the dialog matches the rest of Bansa's UI.
        // SetResourceReference keeps the binding live so theme switches (dark↔light) update it.
        this.SetResourceReference(BackgroundProperty, "BgBrush");
        this.SetResourceReference(ForegroundProperty, "TextBrush");
        this.SetResourceReference(BorderBrushProperty, "BorderBrush");
        BorderThickness = new Thickness(1);

        var grid = new Grid { Margin = new Thickness(20, 20, 20, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // name
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // up
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // down
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); // spacer
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // buttons
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _nameBox = MakeTextBox(name);
        _upBox   = MakeTextBox(upKbps.ToString());
        _downBox = MakeTextBox(downKbps.ToString());

        AddRow(grid, 0, "Name",      _nameBox);
        AddRow(grid, 1, "Up KB/s",   _upBox);
        AddRow(grid, 2, "Down KB/s", _downBox);

        var btnRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        Grid.SetRow(btnRow, 4);
        Grid.SetColumnSpan(btnRow, 2);

        var okBtn = new Button
        {
            Content = "Save", Width = 88, Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true, Padding = new Thickness(0, 8, 0, 8)
        };
        okBtn.SetResourceReference(StyleProperty, "AccentButton");
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ProfileName)) { _nameBox.Focus(); return; }
            DialogResult = true;
        };

        var cancelBtn = new Button
        {
            Content = "Cancel", Width = 88, IsCancel = true,
            Padding = new Thickness(0, 8, 0, 8)
        };

        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        grid.Children.Add(btnRow);

        Content = grid;
        Loaded += (_, _) => _nameBox.Focus();
    }

    /// <summary>Creates a TextBox that picks up the app's implicit TextBox style (themed colours + CornerRadius).</summary>
    private static TextBox MakeTextBox(string text) =>
        new TextBox
        {
            Text   = text,
            Margin = new Thickness(0, 0, 0, 12),
            // Padding is deliberately omitted — the implicit TextBox style sets Padding="10,7"
        };

    private static void AddRow(Grid grid, int row, string label, TextBox box)
    {
        var lbl = new TextBlock
        {
            Text              = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize          = 12,
            Margin            = new Thickness(0, 0, 12, 12),
        };
        lbl.SetResourceReference(ForegroundProperty, "SubtleTextBrush");

        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
        Grid.SetRow(box, row); Grid.SetColumn(box, 1);
        grid.Children.Add(lbl);
        grid.Children.Add(box);
    }
}
