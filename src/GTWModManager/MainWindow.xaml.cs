using System.Windows;
using System.Windows.Input;

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

    // Ctrl+F jumps to the search box of the list you're looking at.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        var box = MainTabs.SelectedIndex switch { 0 => ModSearchBox, 1 => CatalogSearchBox, _ => null };
        if (box is null) return;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }

    // Esc clears the search.
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is System.Windows.Controls.TextBox box && box.Text.Length > 0)
        {
            box.Clear();
            e.Handled = true;
        }
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
