using System.Windows;
using GTWModManager.Core;

namespace GTWModManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // App.xaml loads the Fluent theme following Windows; switch to the user's choice if they picked one.
        ApplyTheme(AppSettings.Load().Theme);
        base.OnStartup(e);
    }

    public App()
    {
        // Show unexpected errors instead of closing the app.
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "GTW Mod Manager: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    /// <summary>"System" follows the Windows setting, including when it changes while the app is open.</summary>
    public static void ApplyTheme(string? theme)
    {
#pragma warning disable WPF0001 // ThemeMode is marked experimental in .NET 9
        Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }
}
