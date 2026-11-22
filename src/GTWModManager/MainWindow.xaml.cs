using System.Windows;

namespace GTWModManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closed += (_, _) => _vm.Dispose();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && _vm.HasGame)
            await _vm.InstallFilesAsync(files);
    }

    // PasswordBox can't be data-bound; hand the key to the view model and clear the box.
    private async void SaveNexusKey_Click(object sender, RoutedEventArgs e)
    {
        var key = NexusKeyBox.Password;
        NexusKeyBox.Clear();
        if (!string.IsNullOrWhiteSpace(key))
            await _vm.SaveNexusKeyAsync(key);
    }
}
