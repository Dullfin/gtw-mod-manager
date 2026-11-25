using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace GTWModManager;

public partial class MainWindow : Window, IUiServices
{
    private readonly MainViewModel _vm;
    private readonly TabControl MainTabs;
    private readonly TextBox ModSearchBox;
    private readonly TextBox CatalogSearchBox;
    private readonly TextBox NexusKeyBox;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        MainTabs = this.FindControl<TabControl>(nameof(MainTabs))!;
        ModSearchBox = this.FindControl<TextBox>(nameof(ModSearchBox))!;
        CatalogSearchBox = this.FindControl<TextBox>(nameof(CatalogSearchBox))!;
        NexusKeyBox = this.FindControl<TextBox>(nameof(NexusKeyBox))!;
        _vm = new MainViewModel(this);
        DataContext = _vm;
        Opened += async (_, _) => await _vm.InitializeAsync();
        Closed += (_, _) => _vm.Dispose();
        KeyDown += Window_KeyDown;
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);
    }

    // ------------------------------------------------------------ keyboard

    /// <summary>Ctrl+F jumps to the search box of the list you're looking at; Esc clears it.</summary>
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var box = MainTabs.SelectedIndex switch { 0 => ModSearchBox, 1 => CatalogSearchBox, _ => null };
            if (box is null) return;
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FocusManager?.GetFocusedElement() is TextBox { Text.Length: > 0 } focused
                 && (focused == ModSearchBox || focused == CatalogSearchBox))
        {
            focused.Text = "";
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ drag and drop

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count > 0 && _vm.HasGame)
            await _vm.InstallFilesAsync(paths);
    }

    // ------------------------------------------------------------ Nexus key

    private async void SaveNexusKey_Click(object? sender, RoutedEventArgs e)
    {
        var key = NexusKeyBox.Text;
        NexusKeyBox.Text = "";
        if (!string.IsNullOrWhiteSpace(key))
            await _vm.SaveNexusKeyAsync(key);
    }

    // ------------------------------------------------------------ IUiServices

    public Task<bool> ConfirmAsync(string message) => MessageDialog.ShowAsync(this, message, confirm: true);

    public async Task ErrorAsync(string message) => await MessageDialog.ShowAsync(this, message, confirm: false);

    public async Task<IReadOnlyList<string>> PickModFilesAsync(string title, bool multiple, string? startFolder = null)
    {
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = [new FilePickerFileType("Mods (.zip, .7z, .rar, .dll)") { Patterns = ["*.zip", "*.7z", "*.rar", "*.dll"] }],
        };
        if (startFolder is not null && Directory.Exists(startFolder))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startFolder);
        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
