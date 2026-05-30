using System;
using System.Linq;
using System.Windows;

namespace Bansa.Services;

public enum AppTheme { Dark, Light }

public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action<AppTheme>? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var app = Application.Current;
        if (app is null) return;

        var dictionaries = app.Resources.MergedDictionaries;

        // Remove any previously applied theme dict
        var existing = dictionaries
            .Where(d => d.Source is not null &&
                       (d.Source.OriginalString.Contains("Themes/Dark.xaml") ||
                        d.Source.OriginalString.Contains("Themes/Light.xaml")))
            .ToList();
        foreach (var d in existing) dictionaries.Remove(d);

        // Insert the new theme dict at the top so Controls.xaml's DynamicResource lookups find it
        var uri = theme == AppTheme.Dark
            ? new Uri("Themes/Dark.xaml", UriKind.Relative)
            : new Uri("Themes/Light.xaml", UriKind.Relative);

        dictionaries.Insert(0, new ResourceDictionary { Source = uri });

        ThemeChanged?.Invoke(theme);
    }

    public static void Toggle()
    {
        Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
    }
}
