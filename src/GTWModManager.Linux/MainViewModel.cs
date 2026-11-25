using System.Collections.ObjectModel;
using System.Diagnostics;
using GTWModManager.Core;

namespace GTWModManager;

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly IUiServices _ui;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ModCatalogClient _catalog = new();
    private ModService? _service;
    private ModIndex _index = new();
    private NexusUser? _nexusUser;
    private CancellationTokenSource? _watchCts;
    private string? _pickedFile;

    public MainViewModel(IUiServices ui)
    {
        _ui = ui;
        _indexUrl = _settings.EffectiveIndexUrl;
        RefreshCommand = new RelayCommand(_ => RunAsync("Refreshing", async () =>
        {
            _catalog.ClearReleaseCache();
            await RefreshAllAsync();
            FlattenOldInstalls();
        }), _ => !IsBusy);
        BrowseGameCommand = new RelayCommand(_ => BrowseGameAsync(), _ => !IsBusy);
        InstallFileCommand = new RelayCommand(_ => InstallFileDialogAsync(), _ => HasGame && !IsBusy);
        InstallLoaderCommand = new RelayCommand(p => RunAsync("Installing mod loader", () => InstallLoaderAndOfferSwitchAsync(ParseLoader(p)!.Value)), _ => HasGame && !IsBusy);
        SwitchLoaderCommand = new RelayCommand(p => RunAsync("Switching mod loader", () => SwitchLoaderAsync(ParseLoader(p))), _ => HasGame && !IsBusy);
        LaunchCommand = new RelayCommand(Launch, () => HasGame);
        OpenModsFolderCommand = new RelayCommand(() => OpenFolder(ActiveModsFolder), () => HasGame);
        ToggleCommand = new RelayCommand(p => RunAsync("Updating", () => ToggleAsync((ModRow)p!)), _ => !IsBusy);
        UninstallCommand = new RelayCommand(p => RunAsync("Removing", () => UninstallAsync((ModRow)p!)), _ => !IsBusy);
        AdoptCommand = new RelayCommand(p => RunAsync("Moving", () => AdoptAsync((ModRow)p!)), _ => !IsBusy);
        InstallCatalogCommand = new RelayCommand(p => RunAsync("Installing", () => InstallFromCatalogAsync((CatalogRow)p!)), _ => HasGame && !IsBusy);
        CheckUpdatesCommand = new RelayCommand(_ => RunAsync("Checking for new versions", CheckLatestVersionsAsync), _ => !IsBusy);
        OpenHomepageCommand = new RelayCommand(p => { OpenUrl(((CatalogRow)p!).Entry.WebsiteUrl); return Task.CompletedTask; });
        SaveIndexUrlCommand = new RelayCommand(_ => RunAsync("Loading mod list", SaveIndexUrlAsync), _ => !IsBusy);
        CancelWaitCommand = new RelayCommand(() => _watchCts?.Cancel());
        PickDownloadedFileCommand = new RelayCommand(_ => PickDownloadedFileAsync());
        RemoveNexusKeyCommand = new RelayCommand(_ => SaveNexusKeyAsync(null), _ => !IsBusy);
        OpenNexusKeysPageCommand = new RelayCommand(() => OpenUrl("https://www.nexusmods.com/settings/api-keys"));
        ClearModFiltersCommand = new RelayCommand(() => { ModSearch = ""; ModFilter = ModFilterOptions[0]; });
        ClearCatalogFiltersCommand = new RelayCommand(() => { CatalogSearch = ""; CatalogFilter = CatalogFilterOptions[0]; CatalogTag = AllTags; });
        UpdateNowCommand = new RelayCommand(_ => RunAsync("Updating GTW Mod Manager", UpdateNowAsync), _ => _availableUpdate is not null && !IsBusy);
        SkipUpdateCommand = new RelayCommand(SkipUpdate);
        DismissUpdateCommand = new RelayCommand(() => UpdateAvailable = false);
        OpenUpdatePageCommand = new RelayCommand(() => OpenUrl(_availableUpdate?.PageUrl ?? UpdateService.ReleasesPage));
        OpenReleasesPageCommand = new RelayCommand(() => OpenUrl(UpdateService.ReleasesPage));
        CheckAppUpdateCommand = new RelayCommand(_ => CheckForAppUpdateAsync(manual: true), _ => !IsBusy);
        CopyLaunchOptionCommand = new RelayCommand(_ => CopyLaunchOptionAsync());

        SetGameDir(_settings.GameDir is { } dir && File.Exists(Path.Combine(dir, GamePaths.ExeName))
            ? GameLocator.RealCase(dir) : GameLocator.Find());
    }

    /// <summary>Every installed mod; <see cref="ModsShown"/> is what the search and filter let through.</summary>
    public ObservableCollection<ModRow> Mods { get; } = [];
    public ObservableCollection<ModRow> ModsShown { get; } = [];
    public ObservableCollection<CatalogRow> Catalog { get; } = [];
    public ObservableCollection<CatalogRow> CatalogShown { get; } = [];

    public RelayCommand RefreshCommand { get; }
    public RelayCommand BrowseGameCommand { get; }
    public RelayCommand InstallFileCommand { get; }
    public RelayCommand InstallLoaderCommand { get; }
    public RelayCommand SwitchLoaderCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand OpenModsFolderCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand AdoptCommand { get; }
    public RelayCommand InstallCatalogCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand OpenHomepageCommand { get; }
    public RelayCommand SaveIndexUrlCommand { get; }
    public RelayCommand CancelWaitCommand { get; }
    public RelayCommand PickDownloadedFileCommand { get; }
    public RelayCommand RemoveNexusKeyCommand { get; }
    public RelayCommand OpenNexusKeysPageCommand { get; }
    public RelayCommand ClearModFiltersCommand { get; }
    public RelayCommand ClearCatalogFiltersCommand { get; }
    public RelayCommand UpdateNowCommand { get; }
    public RelayCommand SkipUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }
    public RelayCommand OpenUpdatePageCommand { get; }
    public RelayCommand OpenReleasesPageCommand { get; }
    public RelayCommand CheckAppUpdateCommand { get; }
    public RelayCommand CopyLaunchOptionCommand { get; }

    // ------------------------------------------------------------ Linux: Proton launch option

    private LaunchOptionStatus _launch = new(LaunchOptionState.NotNeeded, null, true, true);

    public bool ShowLaunchOptionBanner =>
        HasGame && _launch.State is LaunchOptionState.Missing or LaunchOptionState.Partial or LaunchOptionState.Unknown;

    public string LaunchOptionMessage => _launch.State switch
    {
        LaunchOptionState.Missing => "On Linux, Steam needs a launch option before any mod loader can start.",
        LaunchOptionState.Partial => _launch.AllowsBepInEx
            ? "Your launch option lets BepInEx start but not MelonLoader."
            : "Your launch option lets MelonLoader start but not BepInEx.",
        LaunchOptionState.Unknown => "Couldn't read Steam's settings to check the launch option mod loaders need on Linux.",
        _ => "",
    };

    public string LaunchOptionText => SteamLaunchOptions.Recommended;

    private async Task CopyLaunchOptionAsync()
    {
        await _ui.CopyTextAsync(SteamLaunchOptions.Recommended);
        Status = "Copied. In Steam: right-click Get To Work → Properties → General → Launch Options, paste it, then click Refresh here.";
    }

    private void RefreshLaunchOption()
    {
        _launch = SteamLaunchOptions.Check();
        Raise(nameof(ShowLaunchOptionBanner));
        Raise(nameof(LaunchOptionMessage));
    }

    // ------------------------------------------------------------ app updates

    private readonly UpdateService _updater = new();
    private AppRelease? _availableUpdate;

    public string CurrentVersionText =>
        $"Version {UpdateService.CurrentVersion.ToString(3)} ({(Platform.IsLinux ? "Linux" : UpdateService.IsSelfContained ? "standalone" : "needs .NET 9")})";

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }

    private string _updateBannerText = "";
    public string UpdateBannerText { get => _updateBannerText; private set => Set(ref _updateBannerText, value); }

    private string _appUpdateStatus = "";
    public string AppUpdateStatus { get => _appUpdateStatus; private set => Set(ref _appUpdateStatus, value); }

    /// <param name="manual">From the Settings button: reports every outcome and ignores "skip this version".</param>
    private async Task CheckForAppUpdateAsync(bool manual)
    {
        var current = UpdateService.CurrentVersion;
        if (manual) AppUpdateStatus = "Checking for updates...";
        try
        {
            var latest = await _updater.GetLatestAsync();
            if (latest is null)
            {
                AppUpdateStatus = "No published release found. If the GitHub repository is private, updates can't be checked.";
                return;
            }
            if (!UpdateService.IsNewer(latest, current))
            {
                _availableUpdate = null;
                UpdateAvailable = false;
                AppUpdateStatus = $"You're up to date. {latest.Version.ToString(3)} is the latest version.";
                return;
            }

            _availableUpdate = latest;
            AppUpdateStatus = $"Version {latest.Version.ToString(3)} is available.";
            RelayCommand.InvalidateAll();
            if (!manual && _settings.SkippedAppVersion == latest.Version.ToString(3)) return;
            UpdateBannerText = $"GTW Mod Manager {latest.Version.ToString(3)} is available. You have {current.ToString(3)}.";
            UpdateAvailable = true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppUpdateStatus = "Couldn't check for updates: " + e.Message;
        }
    }

    private void SkipUpdate()
    {
        if (_availableUpdate is null) return;
        _settings.SkippedAppVersion = _availableUpdate.Version.ToString(3);
        _settings.Save();
        UpdateAvailable = false;
        Status = $"Skipped version {_settings.SkippedAppVersion}. You can still update from Settings.";
    }

    private async Task UpdateNowAsync()
    {
        var release = _availableUpdate!;
        if (!UpdateService.CanSelfUpdate)
        {
            OpenUrl(release.PageUrl);
            Status = "This copy can't replace itself (it isn't a published single-file build), so the download page was opened.";
            return;
        }

        var exe = Environment.ProcessPath!;
        Status = $"Downloading GTW Mod Manager {release.Version.ToString(3)}...";
        string downloaded;
        try
        {
            downloaded = await _updater.DownloadAsync(release, exe, new Progress<double>(p => Progress = p * 100));
        }
        catch (UnauthorizedAccessException)
        {
            OpenUrl(release.PageUrl);
            throw new InvalidOperationException($"Can't write to {Path.GetDirectoryName(exe)}. The download page has been opened instead; replace the app by hand.");
        }

        UpdateService.SwapExecutable(exe, downloaded);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add(UpdateService.UpdatedFromArg);
        psi.ArgumentList.Add(UpdateService.CurrentVersion.ToString(3));
        Process.Start(psi);
        App.Shutdown();
    }

    /// <summary>Moves single-DLL mods that older versions put in their own folder straight into plugins.</summary>
    private void FlattenOldInstalls()
    {
        if (_service is null || ModService.IsGameRunning()) return;
        try
        {
            var result = _service.FlattenFolderInstalls();
            if (result.Moved.Count > 0)
            {
                RefreshInstalled();
                Status = $"Moved {string.Join(", ", result.Moved)} directly into the plugins folder.";
            }
            if (result.Skipped.Count > 0)
                Status = (result.Moved.Count > 0 ? Status + " " : "") +
                         "Left in their own folder: " + string.Join("; ", result.Skipped) + ". Remove the other copy and click Refresh to move them.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = "Couldn't move older installs into plugins: " + e.Message;
        }
    }

    /// <summary>After an update: remove the old copy of the app and say so.</summary>
    private async Task FinishUpdateAsync()
    {
        if (!UpdateService.CanSelfUpdate) return;
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, UpdateService.UpdatedFromArg);
        if (i >= 0 && i + 1 < args.Length)
            Status = $"Updated from {args[i + 1]} to {UpdateService.CurrentVersion.ToString(3)}.";
        await UpdateService.CleanupAfterUpdateAsync(Environment.ProcessPath!);
    }

    // ------------------------------------------------------------ search and filters

    private static readonly string[] ModFilterOptions =
        ["All mods", "Enabled", "Disabled", "Active loader only", "BepInEx", "MelonLoader", "Needs attention"];

    private static readonly string[] CatalogFilterOptions =
        ["All mods", "Not installed", "Installed", "Updates available", "GitHub", "Nexus Mods", "BepInEx", "MelonLoader"];

    public string[] ModFilters => ModFilterOptions;
    public string[] CatalogFilters => CatalogFilterOptions;

    public const string AllTags = "All tags";
    public ObservableCollection<string> CatalogTags { get; } = [AllTags];

    private string _modSearch = "";
    public string ModSearch { get => _modSearch; set { if (Set(ref _modSearch, value ?? "")) RefreshModsView(); } }

    private string _modFilter = ModFilterOptions[0];
    public string ModFilter { get => _modFilter; set { if (Set(ref _modFilter, value ?? ModFilterOptions[0])) RefreshModsView(); } }

    private string _catalogSearch = "";
    public string CatalogSearch { get => _catalogSearch; set { if (Set(ref _catalogSearch, value ?? "")) RefreshCatalogView(); } }

    private string _catalogFilter = CatalogFilterOptions[0];
    public string CatalogFilter { get => _catalogFilter; set { if (Set(ref _catalogFilter, value ?? CatalogFilterOptions[0])) RefreshCatalogView(); } }

    private string _catalogTag = AllTags;
    public string CatalogTag { get => _catalogTag; set { if (Set(ref _catalogTag, value ?? AllTags)) RefreshCatalogView(); } }

    public string ModCountText => CountText(ModsShown.Count, Mods.Count);
    public string CatalogCountText => CountText(CatalogShown.Count, Catalog.Count);
    public bool NoModsMatch => ModsShown.Count == 0 && Mods.Count > 0;
    public bool NoCatalogMatch => CatalogShown.Count == 0 && Catalog.Count > 0;

    private static string CountText(int shown, int total) =>
        shown == total ? $"{total} mod{(total == 1 ? "" : "s")}" : $"Showing {shown} of {total}";

    /// <summary>Every word typed must appear somewhere in the row's text.</summary>
    private static bool MatchesSearch(string search, params string?[] fields)
    {
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;
        var haystack = string.Join(' ', fields.Where(f => !string.IsNullOrEmpty(f)));
        return terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesMod(ModRow row)
    {
        bool filter = ModFilter switch
        {
            "Enabled" => row.IsEnabled,
            "Disabled" => !row.IsEnabled,
            "Active loader only" => row.IsLoaderActive,
            "BepInEx" => row.Info.Loader == ModLoader.BepInEx,
            "MelonLoader" => row.Info.Loader == ModLoader.MelonLoader,
            "Needs attention" => row.IsStray || row.Info.IsMissing,
            _ => true,
        };
        return filter && MatchesSearch(ModSearch, row.Name, row.Version, row.Guids, row.Info.Author,
            row.LoaderText, row.KindText, row.Location);
    }

    private bool MatchesCatalog(CatalogRow row)
    {
        bool filter = CatalogFilter switch
        {
            "Not installed" => row.InstalledVersion is null,
            "Installed" => row.InstalledVersion is not null,
            "Updates available" => row.HasUpdate,
            "GitHub" => row.Entry.Repo is not null,
            "Nexus Mods" => row.IsNexus,
            "BepInEx" => row.Entry.LoaderHint == ModLoader.BepInEx,
            "MelonLoader" => row.Entry.LoaderHint == ModLoader.MelonLoader,
            _ => true,
        };
        bool tag = CatalogTag == AllTags || row.Entry.Tags.Contains(CatalogTag, StringComparer.OrdinalIgnoreCase);
        return filter && tag && MatchesSearch(CatalogSearch, row.Name, row.Author, row.Description,
            row.Tags, row.SourceText, row.LoaderText);
    }

    private void RefreshModsView()
    {
        Fill(ModsShown, Mods.Where(MatchesMod));
        foreach (var p in new[] { nameof(ModCountText), nameof(NoModsMatch) }) Raise(p);
    }

    private void RefreshCatalogView()
    {
        Fill(CatalogShown, Catalog.Where(MatchesCatalog));
        foreach (var p in new[] { nameof(CatalogCountText), nameof(NoCatalogMatch) }) Raise(p);
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private void RebuildTags()
    {
        // Keep the "All tags" item in place: clearing the whole list would blank the menu's selection.
        var tags = Catalog.SelectMany(c => c.Entry.Tags).Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0).Distinct().Order().ToList();
        if (!tags.Contains(CatalogTag)) CatalogTag = AllTags;
        for (int i = CatalogTags.Count - 1; i >= 1; i--)
            if (!tags.Contains(CatalogTags[i])) CatalogTags.RemoveAt(i);
        for (int i = 0; i < tags.Count; i++)
            if (CatalogTags.Count <= i + 1 || CatalogTags[i + 1] != tags[i]) CatalogTags.Insert(i + 1, tags[i]);
    }

    // ------------------------------------------------------------ bindable state

    private string _gameDir = "";
    public string GameDir { get => _gameDir; private set => Set(ref _gameDir, value); }

    public bool HasGame => _service is not null;

    private LoaderStatus? _bepInEx;
    private LoaderStatus? _melon;
    private ModLoader? _active;

    public bool BepInExInstalled => _bepInEx?.Installed == true;
    public bool MelonInstalled => _melon?.Installed == true;
    public string BepInExVersionText => _bepInEx is { Installed: true } s ? s.Version ?? "" : "Not installed";
    public string MelonVersionText => _melon is { Installed: true } s ? s.Version ?? "" : "Not installed";
    public string BepInExButtonText => (BepInExInstalled ? "Update" : "Install") + " BepInEx";
    public string MelonButtonText => (MelonInstalled ? "Update" : "Install") + " MelonLoader";
    public bool IsVanillaActive => HasGame && _active is null;
    public bool IsBepInExActive => _active == ModLoader.BepInEx;
    public bool IsMelonActive => _active == ModLoader.MelonLoader;

    public string LoaderHint => !HasGame ? ""
        : _active is null ? "The game starts without mods. Your installed mods stay where they are."
        : $"Only {Names.Of(_active)} mods load. Switching keeps each loader's mods installed.";

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) { Raise(nameof(ProgressIndeterminate)); RelayCommand.InvalidateAll(); } }
    }

    private double _progress;
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) Raise(nameof(ProgressIndeterminate)); } }

    /// <summary>Spin while busy until a download reports real progress.</summary>
    public bool ProgressIndeterminate => IsBusy && Progress <= 0;

    private string _indexUrl;
    public string IndexUrl { get => _indexUrl; set => Set(ref _indexUrl, value ?? ""); }

    private bool _isWaitingForDownload;
    public bool IsWaitingForDownload { get => _isWaitingForDownload; private set => Set(ref _isWaitingForDownload, value); }

    private string _waitingText = "";
    public string WaitingText { get => _waitingText; private set => Set(ref _waitingText, value); }

    private string _nexusStatus = "";
    public string NexusStatus { get => _nexusStatus; private set => Set(ref _nexusStatus, value); }

    public string DownloadsFolder => DownloadWatcher.DownloadsFolder;

    public bool IsThemeSystem { get => _settings.Theme is not ("Light" or "Dark"); set { if (value) SetTheme("System"); } }
    public bool IsThemeLight { get => _settings.Theme == "Light"; set { if (value) SetTheme("Light"); } }
    public bool IsThemeDark { get => _settings.Theme == "Dark"; set { if (value) SetTheme("Dark"); } }

    private void SetTheme(string theme)
    {
        if (_settings.Theme == theme) return;
        _settings.Theme = theme;
        _settings.Save();
        App.ApplyTheme(theme);
        foreach (var p in new[] { nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark) }) Raise(p);
    }

    public string ActiveLoaderText => !HasGame ? "" : _active is null ? "Mods off" : Names.Of(_active);

    public string StraysHint =>
        Mods.Any(m => m.IsStray)
            ? "Some DLLs are directly in the BepInEx folder, where BepInEx ignores them. Use \"Move into plugins\" to load them."
            : "";

    public bool HasStrays => StraysHint.Length > 0;

    private string? ActiveModsFolder => _service is null ? null
        : _active == ModLoader.MelonLoader ? _service.Paths.MelonModsDir : _service.Paths.PluginsDir;

    // ------------------------------------------------------------ setup

    public async Task InitializeAsync()
    {
        await RunAsync("Loading", RefreshAllAsync);
        FlattenOldInstalls();
        await FinishUpdateAsync();
        await RefreshNexusUserAsync();
        await CheckForAppUpdateAsync(manual: false);
    }

    private void SetGameDir(string? dir)
    {
        if (dir is not null && new GamePaths(dir) is { IsValidGameDir: true } paths)
        {
            _service = new ModService(paths);
            GameDir = paths.GameDir;
            _settings.GameDir = paths.GameDir;
            _settings.Save();
        }
        else
        {
            _service = null;
            GameDir = "Game not found. Click Game folder and pick the Get To Work folder.";
        }
        Raise(nameof(HasGame));
        RelayCommand.InvalidateAll();
    }

    private async Task BrowseGameAsync()
    {
        var folder = await _ui.PickFolderAsync("Select the Get To Work folder (the one with \"Get To Work.exe\")");
        if (folder is null) return;
        if (!File.Exists(Path.Combine(folder, GamePaths.ExeName)))
        {
            await _ui.ErrorAsync($"\"{GamePaths.ExeName}\" isn't in that folder.");
            return;
        }
        SetGameDir(folder);
        await RunAsync("Loading", RefreshAllAsync);
    }

    // ------------------------------------------------------------ refresh

    private async Task RefreshAllAsync()
    {
        RefreshInstalled();
        try
        {
            _index = await _catalog.LoadIndexAsync(_settings.EffectiveIndexUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            Status = "Couldn't load the community mod list, showing the built-in copy: " + e.Message;
            _index = await _catalog.LoadIndexAsync(null);
        }
        var latest = Catalog.ToDictionary(c => c.Entry.Id, c => c.LatestVersion);
        Catalog.Clear();
        foreach (var entry in _index.Mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            Catalog.Add(new CatalogRow(entry) { LatestVersion = latest.GetValueOrDefault(entry.Id) });
        RebuildTags();
        UpdateInstalledVersions();
    }

    private void RefreshInstalled()
    {
        Mods.Clear();
        if (_service is not null)
        {
            _bepInEx = _service.LoaderStatus(ModLoader.BepInEx);
            _melon = _service.LoaderStatus(ModLoader.MelonLoader);
            _active = _service.ActiveLoader;
            foreach (var mod in _service.Scan()) Mods.Add(new ModRow(mod, mod.Loader == _active));
        }
        else
        {
            _bepInEx = _melon = null;
            _active = null;
        }
        foreach (var p in new[] { nameof(BepInExInstalled), nameof(MelonInstalled),
                     nameof(BepInExButtonText), nameof(MelonButtonText), nameof(ActiveLoaderText),
                     nameof(BepInExVersionText), nameof(MelonVersionText),
                     nameof(IsVanillaActive), nameof(IsBepInExActive), nameof(IsMelonActive), nameof(LoaderHint),
                     nameof(StraysHint), nameof(HasStrays) })
            Raise(p);
        RefreshLaunchOption();
        RefreshModsView();
        UpdateInstalledVersions();
        RelayCommand.InvalidateAll();
    }

    private void UpdateInstalledVersions()
    {
        foreach (var row in Catalog)
        {
            var installed = _service?.Registry.Find(row.Entry.Id);
            row.InstalledVersion = installed is null ? null : installed.Version ?? "?";
        }
        RefreshCatalogView();
    }

    private async Task CheckLatestVersionsAsync()
    {
        int failed = 0, skippedNexus = 0;
        using var nexus = NexusClientOrNull();
        foreach (var row in Catalog)
        {
            try
            {
                if (row.Entry.Nexus is { } n)
                {
                    if (nexus is null) { skippedNexus++; continue; }
                    row.LatestVersion = (await nexus.GetModAsync(n)).Version;
                }
                else
                {
                    row.LatestVersion = (await _catalog.ResolveAsync(row.Entry, fresh: true)).Version;
                }
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or NexusException)
            {
                failed++;
            }
        }
        RefreshCatalogView();
        var updates = Catalog.Count(c => c.HasUpdate);
        Status = (updates == 0 ? "Everything is up to date." : $"{updates} update(s) available.") +
                 (failed > 0 ? $" {failed} mod(s) couldn't be checked." : "") +
                 (skippedNexus > 0 ? $" Add a Nexus API key in Settings to check {skippedNexus} Nexus mod(s)." : "");
    }

    private async Task SaveIndexUrlAsync()
    {
        var url = IndexUrl.Trim();
        _settings.IndexUrl = url.Length == 0 || url == AppSettings.DefaultIndexUrl ? null : url;
        _settings.Save();
        IndexUrl = _settings.EffectiveIndexUrl;
        await RefreshAllAsync();
        Status = $"Loaded {Catalog.Count} community mod(s).";
    }

    // ------------------------------------------------------------ loaders

    private static ModLoader? ParseLoader(object? p) => (p as string) switch
    {
        "BepInEx" => ModLoader.BepInEx,
        "MelonLoader" => ModLoader.MelonLoader,
        _ => null,
    };

    private Task SwitchLoaderAsync(ModLoader? loader)
    {
        try
        {
            if (loader is { } l && !_service!.LoaderStatus(l).Installed)
                throw new InvalidOperationException($"{Names.Of(l)} isn't installed yet. Click Install next to it.");
            _service!.SetActiveLoader(loader);
            Status = loader is null ? "Mods are off. The game starts unmodded." : $"{Names.Of(loader)} is now the active mod loader.";
        }
        finally
        {
            RefreshInstalled(); // also puts the loader picker back if the switch failed
        }
        return Task.CompletedTask;
    }

    private async Task InstallLoaderAndOfferSwitchAsync(ModLoader loader)
    {
        var status = _service!.LoaderStatus(loader);
        if (!await _ui.ConfirmAsync($"{(status.Installed ? "Reinstall" : "Install")} the latest {Names.Of(loader)} from GitHub " +
                                   $"({(loader == ModLoader.BepInEx ? "BepInEx/BepInEx" : "LavaGang/MelonLoader")}) into the game folder?"))
            return;
        await InstallLoaderAsync(loader);
        await OfferSwitchAsync(loader, $"{Names.Of(loader)} is installed.");
    }

    private async Task InstallLoaderAsync(ModLoader loader)
    {
        _service!.EnsureGameNotRunning();
        var download = await LoaderService.ResolveLatestAsync(_catalog, loader);
        Status = $"Downloading {download.FileName}...";
        var file = await _catalog.DownloadAsync(download, new Progress<double>(p => Progress = p * 100));
        try { LoaderService.InstallFromZip(_service.Paths, loader, file); }
        finally { TryDelete(Path.GetDirectoryName(file)!); }
        RefreshInstalled();
        Status = $"Installed {Names.Of(loader)} {download.Version}.";
    }

    /// <summary>Asks to make <paramref name="loader"/> active if it isn't already.</summary>
    private async Task OfferSwitchAsync(ModLoader loader, string why)
    {
        if (_service!.ActiveLoader == loader) return;
        var current = _service.ActiveLoader;
        if (await _ui.ConfirmAsync($"{why}\n\nSwitch the game to {Names.Of(loader)} now? " +
                                   (current is null ? "" : $"Your {Names.Of(current)} mods stay installed and come back when you switch back.")))
        {
            _service.SetActiveLoader(loader);
            Status = $"{Names.Of(loader)} is now the active mod loader.";
        }
        RefreshInstalled();
    }

    /// <summary>Makes sure a loader a mod needs is installed. Returns false if the user declines.</summary>
    private async Task<bool> EnsureLoaderInstalledAsync(ModLoader loader, string modName)
    {
        if (_service!.LoaderStatus(loader).Installed) return true;
        if (!await _ui.ConfirmAsync($"{modName} needs {Names.Of(loader)}, which isn't installed.\n\nDownload and install the latest {Names.Of(loader)} now?"))
            return false;
        await InstallLoaderAsync(loader);
        return true;
    }

    // ------------------------------------------------------------ mod actions

    private Task ToggleAsync(ModRow row)
    {
        try
        {
            _service!.SetEnabled(row.Info, row.IsEnabled);
            Status = $"{row.Name} {(row.IsEnabled ? "enabled" : "disabled")}." +
                     (row.IsLoaderActive ? "" : $" It loads when {Names.Of(row.Info.Loader)} is the active loader.");
        }
        finally
        {
            RefreshInstalled();
        }
        return Task.CompletedTask;
    }

    private async Task UninstallAsync(ModRow row)
    {
        var trashName = Path.Combine("GTWModManager", "trash");
        if (!await _ui.ConfirmAsync($"Remove {row.Name}?\n\nIts files go to {trashName} in the game folder, so you can get them back. Settings files are kept."))
            return;
        var trash = _service!.Uninstall(row.Info);
        RefreshInstalled();
        Status = $"Removed {row.Name}. Files moved to {trash}";
    }

    private Task AdoptAsync(ModRow row)
    {
        _service!.AdoptStray(row.Info);
        RefreshInstalled();
        Status = $"Moved {row.Info.Items[0]} into plugins. It loads next time you start the game with BepInEx.";
        return Task.CompletedTask;
    }

    private async Task InstallFileDialogAsync()
    {
        var files = await _ui.PickModFilesAsync("Install mod from file", multiple: true);
        if (files.Count > 0) await InstallFilesAsync(files);
    }

    public Task InstallFilesAsync(IEnumerable<string> files) => RunAsync("Installing", async () =>
    {
        foreach (var file in files)
        {
            if (!ArchiveContents.SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                Status = $"Skipped {Path.GetFileName(file)}: only .zip, .7z, .rar and .dll are supported.";
                continue;
            }
            var loader = _service!.DetectLoader(file);
            if (!await EnsureLoaderInstalledAsync(loader, Path.GetFileName(file))) continue;
            var mod = await InstallWithConflictPromptAsync(replace => _service.InstallFromFile(file, new InstallRequest(ReplaceUnmanaged: replace)));
            if (mod is null) continue;
            RefreshInstalled();
            Status = $"Installed {mod.Name} {mod.Version} ({Names.Of(mod.Loader)}).";
            await OfferSwitchAsync(mod.Loader, $"{mod.Name} is a {Names.Of(mod.Loader)} mod.");
        }
        RefreshInstalled();
    });

    private async Task InstallFromCatalogAsync(CatalogRow row)
    {
        if (row.InstalledVersion is null &&
            !await _ui.ConfirmAsync($"Install {row.Name} by {(row.Author is { Length: > 0 } a ? a : "unknown")}?\n\nSource: {row.SourceText}\n\n" +
                                    "Mods run with the same access to your PC as the game. Only install mods you trust."))
            return;

        foreach (var depId in row.Entry.Dependencies)
        {
            if (_service!.Registry.Find(depId) is not null) continue;
            var dep = _index.Mods.FirstOrDefault(m => m.Id.Equals(depId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"{row.Name} needs {depId}, which isn't in the mod list.");
            await InstallEntryAsync(dep);
        }
        var mod = await InstallEntryAsync(row.Entry);
        RefreshInstalled();
        if (mod is not null)
            await OfferSwitchAsync(mod.Loader, $"{mod.Name} is a {Names.Of(mod.Loader)} mod.");
    }

    private async Task<InstalledMod?> InstallEntryAsync(IndexEntry entry)
    {
        var hint = entry.LoaderHint;
        if (hint is { } l && !await EnsureLoaderInstalledAsync(l, entry.Name)) return null;

        string file;
        string? version;
        string? tempDir = null;
        string source;

        if (entry.Nexus is not null)
        {
            (file, version, tempDir) = await GetNexusDownloadAsync(entry);
            source = "nexus";
        }
        else
        {
            Status = $"Finding the latest {entry.Name}...";
            // Always ask GitHub again: the author may have published a new version since we last looked.
            var download = await _catalog.ResolveAsync(entry, fresh: true);
            Status = $"Downloading {download.FileName}...";
            file = await _catalog.DownloadAsync(download, new Progress<double>(p => Progress = p * 100));
            tempDir = Path.GetDirectoryName(file);
            version = download.Version;
            source = "index";
        }

        try
        {
            var loader = hint ?? _service!.DetectLoader(file);
            if (hint is null && !await EnsureLoaderInstalledAsync(loader, entry.Name)) return null;
            var mod = await InstallWithConflictPromptAsync(replace => _service!.InstallFromFile(file,
                new InstallRequest(entry.Id, entry.Name, version, entry.Author, source, loader, replace)));
            if (mod is not null)
            {
                Status = $"Installed {mod.Name} {mod.Version} ({Names.Of(mod.Loader)}).";
                if (Catalog.FirstOrDefault(c => c.Entry.Id == entry.Id) is { } row && version is not null) row.LatestVersion = version;
                RefreshCatalogView();
            }
            return mod;
        }
        finally
        {
            if (tempDir is not null) TryDelete(tempDir);
        }
    }

    /// <summary>
    /// Gets a Nexus mod file: straight from the API for Premium keys, otherwise by opening the
    /// mod's Files page and waiting for the browser download to land in the Downloads folder.
    /// </summary>
    private async Task<(string File, string? Version, string? TempDir)> GetNexusDownloadAsync(IndexEntry entry)
    {
        var nx = entry.Nexus!;
        NexusFile? picked = null;

        using (var nexus = NexusClientOrNull())
        {
            if (nexus is not null)
            {
                try
                {
                    Status = $"Looking up {entry.Name} on Nexus Mods...";
                    picked = NexusClient.PickFile(await nexus.GetFilesAsync(nx), nx.File);
                    if (picked is not null && _nexusUser?.IsPremium == true)
                    {
                        var url = await nexus.GetDownloadUrlAsync(nx, picked.FileId);
                        Status = $"Downloading {picked.FileName}...";
                        var path = await _catalog.DownloadAsync(new ResolvedDownload(picked.Version ?? "?", url, picked.FileName, null, null),
                            new Progress<double>(p => Progress = p * 100));
                        return (path, picked.Version, Path.GetDirectoryName(path));
                    }
                }
                catch (NexusException e)
                {
                    Status = e.Message + " Falling back to the browser.";
                }
            }
        }

        // Browser download. Nexus only offers "Manual download" for Get To Work mods.
        var downloads = DownloadWatcher.DownloadsFolder;
        var since = DateTime.Now.AddSeconds(-2);
        _pickedFile = null;
        _watchCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        OpenUrl(NexusClient.FilesPageUrl(nx.Domain, nx.ModId, picked?.FileId));
        WaitingText = $"Download {entry.Name} from the Nexus page that just opened " +
                      $"({(picked is null ? "the main file" : picked.Name)}, \"Manual download\"). " +
                      $"It installs as soon as it lands in {downloads}.";
        IsWaitingForDownload = true;
        try
        {
            var file = await DownloadWatcher.WaitForNewFileAsync(downloads, since, _watchCts.Token);
            return (file, picked?.Version, null);
        }
        catch (OperationCanceledException) when (_pickedFile is not null)
        {
            return (_pickedFile, picked?.Version, null);
        }
        finally
        {
            IsWaitingForDownload = false;
            _watchCts.Dispose();
            _watchCts = null;
        }
    }

    private async Task PickDownloadedFileAsync()
    {
        var files = await _ui.PickModFilesAsync("Choose the downloaded mod file", multiple: false, DownloadWatcher.DownloadsFolder);
        if (files.Count == 0) return;
        _pickedFile = files[0];
        _watchCts?.Cancel();
    }

    private async Task<InstalledMod?> InstallWithConflictPromptAsync(Func<bool, InstalledMod> install)
    {
        try
        {
            return install(false);
        }
        catch (ModConflictException e)
        {
            if (!await _ui.ConfirmAsync(e.Message + $"\n\nReplace them? The old files go to {Path.Combine("GTWModManager", "trash")}.")) return null;
            return install(true);
        }
    }

    // ------------------------------------------------------------ Nexus key

    public Task SaveNexusKeyAsync(string? key) => RunAsync("Checking Nexus key", async () =>
    {
        _settings.SetNexusApiKey(key);
        _settings.Save();
        await RefreshNexusUserAsync();
        if (key is not null && _nexusUser is null && NexusStatus.Length > 0) await _ui.ErrorAsync(NexusStatus);
    });

    private async Task RefreshNexusUserAsync()
    {
        _nexusUser = null;
        using var nexus = NexusClientOrNull();
        if (nexus is null)
        {
            NexusStatus = "No API key. Nexus mods download through your browser.";
            return;
        }
        try
        {
            _nexusUser = await nexus.ValidateAsync();
            NexusStatus = $"Signed in as {_nexusUser.Name} ({(_nexusUser.IsPremium ? "Premium: downloads go straight into the game" : "free account: downloads go through your browser")}).";
        }
        catch (Exception e) when (e is NexusException or HttpRequestException or TaskCanceledException)
        {
            NexusStatus = e.Message;
        }
    }

    private NexusClient? NexusClientOrNull() =>
        _settings.GetNexusApiKey() is { } key ? new NexusClient(key) : null;

    // ------------------------------------------------------------ helpers

    private void Launch()
    {
        var url = $"steam://rungameid/{GamePaths.SteamAppId}";
        try
        {
            Platform.Open(url);
        }
        catch (System.ComponentModel.Win32Exception) when (Platform.IsWindows)
        {
            Process.Start(new ProcessStartInfo(_service!.Paths.Exe) { UseShellExecute = true, WorkingDirectory = _service.Paths.GameDir });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No xdg-open: ask Steam directly.
            var psi = new ProcessStartInfo("steam") { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            Process.Start(psi);
        }
    }

    private async Task RunAsync(string what, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Progress = 0;
        Status = what + "...";
        try
        {
            await action();
            if (Status == what + "...") Status = "Ready.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception e)
        {
            Status = e.Message;
            await _ui.ErrorAsync(e.Message);
            RefreshInstalled();
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private static void OpenFolder(string? dir)
    {
        if (dir is null) return;
        Directory.CreateDirectory(dir);
        Platform.Open(dir);
    }

    private static void OpenUrl(string? url)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            Platform.Open(uri.AbsoluteUri);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _watchCts?.Cancel();
        _catalog.Dispose();
        _updater.Dispose();
    }
}
