using System.Windows;

namespace GTWModManager;

public partial class App : Application
{
    public App()
    {
        // Show unexpected errors instead of closing the app.
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "GTW Mod Manager: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
