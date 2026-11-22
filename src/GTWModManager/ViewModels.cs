using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GTWModManager.Core;

namespace GTWModManager;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Func<object?, Task> run, Func<object?, bool>? canRun = null) : ICommand
{
    public RelayCommand(Action run, Func<bool>? canRun = null)
        : this(_ => { run(); return Task.CompletedTask; }, canRun is null ? null : _ => canRun()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canRun?.Invoke(parameter) ?? true;
    public async void Execute(object? parameter) => await run(parameter);
}

public sealed class ModRow(ModInfo info, bool loaderActive) : Observable
{
    public ModInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Version => Info.Version ?? "";
    public string Guids => Info.Guids ?? "";
    public string Location => Info.Location;
    public bool IsStray => Info.Kind == ModKind.Stray;
    public bool CanToggle => !IsStray && !Info.IsMissing;
    public bool IsLoaderActive { get; } = loaderActive;
    public string LoaderText => Names.Of(Info.Loader) + (IsLoaderActive ? "" : " (off)");

    public string KindText => Info.Kind switch
    {
        ModKind.Managed when Info.IsMissing => "Files missing",
        ModKind.Managed => Info.Source switch { "index" => "GitHub", "nexus" => "Nexus Mods", _ => "From file" },
        ModKind.Unmanaged => "Added manually",
        _ => "Not loaded",
    };

    private bool _isEnabled = info.IsEnabled;
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
}

public sealed class CatalogRow(IndexEntry entry) : Observable
{
    public IndexEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Author => Entry.Author ?? "";
    public string Description => Entry.Description ?? "";
    public bool IsNexus => Entry.Nexus is not null;
    public string Tags => string.Join(", ", Entry.Tags);
    public bool HasTags => Entry.Tags.Count > 0;
    public string SourceText => Entry.Nexus is { } n ? $"Nexus Mods #{n.ModId}"
        : Entry.Repo is { } r ? "GitHub " + r : "Direct download";
    public string LoaderText => Entry.LoaderHint is { } l ? Names.Of(l) : "Loader detected on install";

    private string? _installedVersion;
    public string? InstalledVersion
    {
        get => _installedVersion;
        set { if (Set(ref _installedVersion, value)) { Raise(nameof(ActionText)); Raise(nameof(StatusText)); } }
    }

    private string? _latestVersion;
    public string? LatestVersion
    {
        get => _latestVersion;
        set { if (Set(ref _latestVersion, value)) { Raise(nameof(ActionText)); Raise(nameof(StatusText)); } }
    }

    public bool HasUpdate => InstalledVersion is not null && LatestVersion is not null &&
                             !string.Equals(InstalledVersion.TrimStart('v'), LatestVersion.TrimStart('v'), StringComparison.OrdinalIgnoreCase);

    public string ActionText => InstalledVersion is null ? "Install" : HasUpdate ? "Update" : "Reinstall";

    public string StatusText =>
        (InstalledVersion is null ? "Not installed" : $"Installed {InstalledVersion}") +
        (LatestVersion is null ? "" : $" · latest {LatestVersion}");
}

public static class Names
{
    public static string Of(ModLoader loader) => loader == ModLoader.BepInEx ? "BepInEx" : "MelonLoader";
    public static string Of(ModLoader? loader) => loader is { } l ? Of(l) : "no mod loader";
}
