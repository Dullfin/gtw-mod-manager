// End-to-end checks for GTWModManager.Core against a fake game folder.
// Usage: GTWModManager.SmokeTest <work dir> <real game dir> [--online]
using System.Diagnostics;
using System.IO.Compression;
using GTWModManager.Core;

var work = Path.GetFullPath(args[0]);
var realGame = args[1];
bool online = args.Contains("--online");
int failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

void Throws<T>(Action a, string what) where T : Exception
{
    try { a(); Check(false, what + " (no exception)"); }
    catch (T) { Check(true, what); }
}

// ---- fake game folder with BepInEx and a few real plugin DLLs ------------------
if (Directory.Exists(work)) Directory.Delete(work, true);
var game = Path.Combine(work, "Get To Work");
var paths = new GamePaths(game);
Directory.CreateDirectory(Path.Combine(paths.BepInExDir, "core"));
Directory.CreateDirectory(paths.PluginsDir);
File.WriteAllText(paths.Exe, "");
File.WriteAllText(Path.Combine(game, "winhttp.dll"), "");
File.Copy(Path.Combine(realGame, "BepInEx", "core", "BepInEx.dll"), Path.Combine(paths.BepInExDir, "core", "BepInEx.dll"));

// Source DLLs may be enabled, disabled or stray in the real install; take them from wherever they are.
string FindReal(string file) => new[] { "plugins", "plugins_disabled", "" }
    .Select(d => Path.Combine(realGame, "BepInEx", d, file)).First(File.Exists);
File.Copy(FindReal("GrabCounter.dll"), Path.Combine(paths.PluginsDir, "GrabCounter.dll"));
var practiceDll = FindReal("PracticeMod.dll");
File.Copy(practiceDll, Path.Combine(paths.BepInExDir, "PracticeMod.dll")); // stray: BepInEx ignores this
var melonDll = Path.Combine(AppContext.BaseDirectory, "FakeMelonMod.dll");

var svc = new ModService(paths) { CheckGameRunning = false };
Check(svc.LoaderStatus(ModLoader.BepInEx).Installed && svc.ActiveLoader == ModLoader.BepInEx, "BepInEx detected and active");
Check(!svc.LoaderStatus(ModLoader.MelonLoader).Installed, "MelonLoader not installed");

// ---- metadata ----------------------------------------------------------------------
var melonMeta = PluginMetadataReader.Read(melonDll).SingleOrDefault();
Check(melonMeta is { Loader: ModLoader.MelonLoader, Name: "Fake Melon Mod", Version: "1.2.3", Author: "Tester" }, $"MelonInfo read ({melonMeta})");
var bepMeta = PluginMetadataReader.Read(practiceDll).FirstOrDefault();
Check(bepMeta?.Loader == ModLoader.BepInEx, $"BepInPlugin read ({bepMeta?.Guid})");

var mods = svc.Scan();
var grab = mods.Single(m => m.Location == "plugins/GrabCounter.dll");
Check(grab.Kind == ModKind.Unmanaged && grab.IsEnabled && grab.Loader == ModLoader.BepInEx, "unmanaged BepInEx plugin found and enabled");
var stray = mods.Single(m => m.Kind == ModKind.Stray);
Check(stray.Items[0] == "PracticeMod.dll", "stray DLL in BepInEx root detected");

// ---- toggle / stray ------------------------------------------------------------------
svc.SetEnabled(grab, false);
Check(File.Exists(Path.Combine(paths.DisabledPluginsDir, "GrabCounter.dll")), "disable moves to plugins_disabled");
svc.SetEnabled(svc.Scan().Single(m => m.Location == "plugins/GrabCounter.dll"), true);
Check(File.Exists(Path.Combine(paths.PluginsDir, "GrabCounter.dll")), "enable moves it back");
svc.AdoptStray(stray);
Check(File.Exists(Path.Combine(paths.PluginsDir, "PracticeMod.dll")), "stray moved into plugins");

// ---- archive helpers --------------------------------------------------------------------
string MakeZip(string name, params (string Path, string Content)[] files)
{
    var zipPath = Path.Combine(work, name);
    using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
    foreach (var (p, c) in files)
    {
        using var w = new StreamWriter(zip.CreateEntry(p).Open());
        w.Write(c);
    }
    return zipPath;
}

void AddFile(string zipPath, string source, string entryPath)
{
    using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
    zip.CreateEntryFromFile(source, entryPath);
}

// ---- BepInEx layouts (unchanged behaviour) ----------------------------------------------
var full = MakeZip("full.zip", ("Cool-1.0/BepInEx/plugins/Cool/readme.txt", "hi"), ("Cool-1.0/BepInEx/config/cool.cfg", "a=1"), ("Cool-1.0/winhttp.dll", "evil"), ("Cool-1.0/BepInEx/core/BepInEx.dll", "evil"));
AddFile(full, practiceDll, "Cool-1.0/BepInEx/plugins/Cool/Cool.dll");
var cool = svc.InstallFromFile(full, new InstallRequest("test.cool", "Cool", "1.0"));
Check(cool.Loader == ModLoader.BepInEx && File.Exists(Path.Combine(paths.PluginsDir, "Cool", "Cool.dll")), "BepInEx zip: plugin extracted");
Check(File.Exists(Path.Combine(paths.BepInExDir, "config", "cool.cfg")), "BepInEx zip: config extracted");
Check(new FileInfo(Path.Combine(game, "winhttp.dll")).Length == 0 && new FileInfo(Path.Combine(paths.BepInExDir, "core", "BepInEx.dll")).Length > 100, "loader files in mod zips are never written");

var rel = MakeZip("rel.zip", ("plugins/RelMod.dll", "not a real dll"), ("patchers/RelPatcher.dll", "x"));
var relMod = svc.InstallFromFile(rel, new InstallRequest("test.rel", "Rel"));
Check(relMod.Items.Contains("plugins/RelMod.dll") && relMod.Items.Contains("patchers/RelPatcher.dll"), "relative layout: plugin + patcher items");
svc.SetEnabled(svc.Scan().Single(m => m.Id == "test.rel"), false);
File.WriteAllText(Path.Combine(paths.BepInExDir, "config", "cool.cfg"), "a=2");
svc.InstallFromFile(rel, new InstallRequest("test.rel", "Rel", "2.0"));
var relNow = svc.Scan().Single(m => m.Id == "test.rel");
Check(!relNow.IsEnabled && relNow.Version == "2.0", "update keeps disabled state and new version");
svc.InstallFromFile(full, new InstallRequest("test.cool", "Cool", "1.1"));
Check(File.ReadAllText(Path.Combine(paths.BepInExDir, "config", "cool.cfg")) == "a=2", "update keeps user's config");

Directory.CreateDirectory(Path.Combine(paths.PluginsDir, "Clash"));
var clash = MakeZip("clash.zip", ("plugins/Clash/x.txt", "x"));
Throws<ModConflictException>(() => svc.InstallFromFile(clash, new InstallRequest("test.clash", "Clash")), "clash with unmanaged folder is reported");
svc.InstallFromFile(clash, new InstallRequest("test.clash", "Clash", ReplaceUnmanaged: true));
Check(File.Exists(Path.Combine(paths.PluginsDir, "Clash", "x.txt")), "replace unmanaged works");

var slip = MakeZip("slip.zip", ("plugins/../../evil.txt", "x"), ("plugins/ok.txt", "x"));
try { svc.InstallFromFile(slip, new InstallRequest("test.slip", "Slip")); } catch (InvalidDataException) { }
Check(!File.Exists(Path.Combine(game, "evil.txt")) && !File.Exists(Path.Combine(paths.BepInExDir, "evil.txt")), "zip slip blocked");

var trash = svc.Uninstall(svc.Scan().Single(m => m.Id == "test.cool"));
Check(!Directory.Exists(Path.Combine(paths.PluginsDir, "Cool")) && trash.StartsWith(paths.TrashDir), "uninstall moves files to GTWModManager\\trash");
Check(File.Exists(Path.Combine(paths.BepInExDir, "config", "cool.cfg")), "uninstall keeps config");

// ---- MelonLoader: fake install, detection, layouts --------------------------------------
Directory.CreateDirectory(Path.Combine(game, "MelonLoader", "net35"));
File.Copy(melonDll, Path.Combine(game, "MelonLoader", "net35", "MelonLoader.dll"));
File.WriteAllText(Path.Combine(game, "version.dll") + LoaderService.DisabledProxySuffix, "proxy");
Check(svc.LoaderStatus(ModLoader.MelonLoader) is { Installed: true, Active: false }, "MelonLoader detected, inactive");

// A bare MelonLoader DLL is detected from its metadata even though BepInEx is active.
var bareMelonZip = MakeZip("melon-bare.zip", ("README.txt", "hi"), ("maps/level.g2wmap", "map"));
AddFile(bareMelonZip, melonDll, "FakeMelonMod.dll");
Check(svc.DetectLoader(bareMelonZip) == ModLoader.MelonLoader, "bare zip detected as MelonLoader from DLL metadata");
var melonMod = svc.InstallFromFile(bareMelonZip);
Check(melonMod.Loader == ModLoader.MelonLoader && melonMod.Id == "Fake Melon Mod" && melonMod.Version == "1.2.3" && melonMod.Author == "Tester", $"MelonLoader mod registered ({melonMod.Id} {melonMod.Version})");
Check(File.Exists(Path.Combine(game, "Mods", "FakeMelonMod.dll")), "MelonLoader DLL goes directly into Mods/");
Check(File.Exists(Path.Combine(game, "UserData", "melon-bare", "maps", "level.g2wmap")), "other files go to UserData/<mod>/");

// Structured MelonLoader zip.
var structured = MakeZip("melon-structured.zip", ("Pkg/UserLibs/Shared.dll", "lib"), ("Pkg/UserData/Thing.cfg", "x=1"), ("Pkg/MelonLoader/evil.dll", "evil"), ("Pkg/version.dll", "evil"));
AddFile(structured, melonDll, "Pkg/Mods/Thing.dll");
var thing = svc.InstallFromFile(structured, new InstallRequest("test.thing", "Thing", Loader: ModLoader.MelonLoader));
Check(thing.Items.SequenceEqual(["Mods/Thing.dll"]) && thing.ExtraFiles.Contains("UserLibs/Shared.dll"), "structured MelonLoader zip: items " + string.Join(",", thing.Items));
Check(!File.Exists(Path.Combine(game, "MelonLoader", "evil.dll")) && !File.Exists(Path.Combine(game, "version.dll")), "MelonLoader core and proxy in mod zips are never written");

// Toggling a MelonLoader mod.
var thingInfo = svc.Scan().Single(m => m.Id == "test.thing");
Check(thingInfo.Loader == ModLoader.MelonLoader && thingInfo.IsEnabled, "scan lists MelonLoader mod");
svc.SetEnabled(thingInfo, false);
Check(File.Exists(Path.Combine(game, "Mods_disabled", "Thing.dll")), "MelonLoader disable moves to Mods_disabled");
svc.SetEnabled(svc.Scan().Single(m => m.Id == "test.thing"), true);

// Uninstall keeps UserData, removes UserLibs nobody else uses.
svc.Uninstall(svc.Scan().Single(m => m.Id == "test.thing"));
Check(File.Exists(Path.Combine(game, "UserData", "Thing.cfg")) && !File.Exists(Path.Combine(game, "UserLibs", "Shared.dll")), "MelonLoader uninstall keeps UserData, removes UserLibs");

// ---- switching loaders ---------------------------------------------------------------------
svc.SetActiveLoader(ModLoader.MelonLoader);
Check(svc.ActiveLoader == ModLoader.MelonLoader && File.Exists(Path.Combine(game, "version.dll")) &&
      !File.Exists(Path.Combine(game, "winhttp.dll")) && File.Exists(Path.Combine(game, "winhttp.dll" + LoaderService.DisabledProxySuffix)),
      "switch to MelonLoader parks winhttp.dll");
svc.SetActiveLoader(null);
Check(svc.ActiveLoader is null && !File.Exists(Path.Combine(game, "version.dll")) && !File.Exists(Path.Combine(game, "winhttp.dll")), "no-mods mode parks both");
svc.SetActiveLoader(ModLoader.BepInEx);
Check(svc.ActiveLoader == ModLoader.BepInEx && !File.Exists(Path.Combine(game, "version.dll")), "switch back to BepInEx");
Check(svc.Scan().Any(m => m.Id == "Fake Melon Mod") && File.Exists(Path.Combine(paths.PluginsDir, "GrabCounter.dll")), "both loaders' mods stay installed across switches");

// ---- .7z install through Windows tar -----------------------------------------------------------
var stage = Path.Combine(work, "stage7z");
Directory.CreateDirectory(Path.Combine(stage, "SevenMod"));
File.Copy(practiceDll, Path.Combine(stage, "SevenMod", "SevenMod.dll"));
var sevenZip = Path.Combine(work, "seven.7z");
var tar = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"), $"--format 7zip -cf \"{sevenZip}\" SevenMod") { WorkingDirectory = stage, UseShellExecute = false })!;
tar.WaitForExit();
var seven = svc.InstallFromFile(sevenZip, new InstallRequest("test.seven", "Seven"));
Check(seven.Loader == ModLoader.BepInEx && File.Exists(Path.Combine(paths.PluginsDir, "SevenMod.dll")) && seven.Items.SequenceEqual(["plugins/SevenMod.dll"]),
      ".7z extracted and installed directly into plugins: " + string.Join(",", seven.Items));

// ---- plain-DLL downloads go straight into plugins; downloads with assets keep a folder ------------
var plainDll = Path.Combine(work, "Plain.dll");
File.Copy(practiceDll, plainDll);
var plain = svc.InstallFromFile(plainDll, new InstallRequest("test.plain", "Plain"));
Check(plain.Items.SequenceEqual(["plugins/Plain.dll"]) && File.Exists(Path.Combine(paths.PluginsDir, "Plain.dll")), "single .dll download goes directly into plugins");
var dllsZip = MakeZip("dlls.zip", ("Two-1.0/README.md", "docs"), ("Two-1.0/LICENSE", "mit"));
AddFile(dllsZip, practiceDll, "Two-1.0/TwoMain.dll");
AddFile(dllsZip, practiceDll, "Two-1.0/TwoHelper.dll");
var two = svc.InstallFromFile(dllsZip, new InstallRequest("test.two", "Two"));
Check(two.Items.OrderBy(i => i).SequenceEqual(["plugins/TwoHelper.dll", "plugins/TwoMain.dll"]) && !File.Exists(Path.Combine(paths.PluginsDir, "README.md")),
      "zip of DLLs + readme: DLLs directly in plugins, docs skipped: " + string.Join(",", two.Items));
var assetsZip = MakeZip("assets.zip", ("Art/README.md", "docs"), ("Art/sprites/hat.png", "png"));
AddFile(assetsZip, practiceDll, "Art/Art.dll");
var art = svc.InstallFromFile(assetsZip, new InstallRequest("test.art", "Art"));
Check(art.Items.SequenceEqual(["plugins/test.art"]) && File.Exists(Path.Combine(paths.PluginsDir, "test.art", "sprites", "hat.png")),
      "zip with asset files keeps its own folder: " + string.Join(",", art.Items));
svc.SetEnabled(svc.Scan().Single(m => m.Id == "test.two"), false);
Check(File.Exists(Path.Combine(paths.DisabledPluginsDir, "TwoMain.dll")) && File.Exists(Path.Combine(paths.DisabledPluginsDir, "TwoHelper.dll")), "disabling a multi-DLL mod moves all its DLLs");

// ---- moving installs made by older versions out of their folders ---------------------------------
Directory.CreateDirectory(Path.Combine(paths.PluginsDir, "old.mod"));
File.Copy(practiceDll, Path.Combine(paths.PluginsDir, "old.mod", "OldMod.dll"));
File.WriteAllText(Path.Combine(paths.PluginsDir, "old.mod", "README.md"), "docs");
Directory.CreateDirectory(Path.Combine(paths.DisabledPluginsDir, "old.off"));
File.Copy(practiceDll, Path.Combine(paths.DisabledPluginsDir, "old.off", "OldOff.dll"));
Directory.CreateDirectory(Path.Combine(paths.PluginsDir, "old.clash"));
File.Copy(practiceDll, Path.Combine(paths.PluginsDir, "old.clash", "Plain.dll")); // Plain.dll is already in plugins
svc.Registry.Mods.Add(new InstalledMod { Id = "old.mod", Name = "Old Mod", Items = ["plugins/old.mod"] });
svc.Registry.Mods.Add(new InstalledMod { Id = "old.off", Name = "Old Off", Items = ["plugins/old.off"] });
svc.Registry.Mods.Add(new InstalledMod { Id = "old.clash", Name = "Old Clash", Items = ["plugins/old.clash"] });
svc.Registry.Save();
var flat = svc.FlattenFolderInstalls();
Check(flat.Moved.OrderBy(n => n).SequenceEqual(["Old Mod", "Old Off"]), "flatten: moves old folder installs: " + string.Join(", ", flat.Moved));
Check(File.Exists(Path.Combine(paths.PluginsDir, "OldMod.dll")) && !Directory.Exists(Path.Combine(paths.PluginsDir, "old.mod")), "flatten: enabled mod's DLL now directly in plugins, folder gone");
Check(File.Exists(Path.Combine(paths.DisabledPluginsDir, "OldOff.dll")) && svc.Scan().Single(m => m.Id == "old.off") is { IsEnabled: false, IsMissing: false }, "flatten: disabled mod stays disabled");
Check(svc.Registry.Find("old.mod")!.Items.SequenceEqual(["plugins/OldMod.dll"]), "flatten: registry updated");
Check(flat.Skipped.Count == 1 && flat.Skipped[0].Contains("Plain.dll") && Directory.Exists(Path.Combine(paths.PluginsDir, "old.clash")), "flatten: skips a mod whose DLL would overwrite another file");
Check(svc.Registry.Find("test.art")!.Items.SequenceEqual(["plugins/test.art"]) && Directory.Exists(Path.Combine(paths.PluginsDir, "test.art")), "flatten: leaves mods with asset files in their folder");

// ---- Nexus file picking (no network) ---------------------------------------------------------
var nexusFiles = new List<NexusFile>
{
    new() { FileId = 1, Name = "Old", FileName = "Mod-1.0.zip", Category = "OLD_VERSION", Uploaded = 100 },
    new() { FileId = 2, Name = "BepInEx", FileName = "BepInEx_win_x64_5.4.23.2.zip", Category = "MAIN", Uploaded = 300 },
    new() { FileId = 3, Name = "Main", FileName = "Mod-2.0.zip", Category = "MAIN", Uploaded = 200 },
    new() { FileId = 4, Name = "Extra", FileName = "Extra.zip", Category = "OPTIONAL", Uploaded = 400 },
};
Check(NexusClient.PickFile(nexusFiles, null)?.FileId == 3, "Nexus: newest main file, skipping bundled loaders and old versions");
Check(NexusClient.PickFile(nexusFiles, "Extra*")?.FileId == 4, "Nexus: file pattern");

// ---- Downloads watcher -----------------------------------------------------------------------
var fakeDownloads = Path.Combine(work, "downloads");
Directory.CreateDirectory(fakeDownloads);
File.WriteAllText(Path.Combine(fakeDownloads, "old.zip"), "old");
File.SetCreationTime(Path.Combine(fakeDownloads, "old.zip"), DateTime.Now.AddHours(-1));
File.SetLastWriteTime(Path.Combine(fakeDownloads, "old.zip"), DateTime.Now.AddHours(-1));
var since = DateTime.Now.AddSeconds(-1);
using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
{
    var wait = DownloadWatcher.WaitForNewFileAsync(fakeDownloads, since, cts.Token);
    await Task.Delay(1500);
    File.WriteAllText(Path.Combine(fakeDownloads, "Mod-2.0.zip.crdownload"), "partial");
    await Task.Delay(1500);
    File.Move(Path.Combine(fakeDownloads, "Mod-2.0.zip.crdownload"), Path.Combine(fakeDownloads, "Mod-2.0.zip"));
    var got = await wait;
    Check(Path.GetFileName(got) == "Mod-2.0.zip", "Downloads watcher picks up the finished file only: " + Path.GetFileName(got));
}

// ---- app self-update (no network) ---------------------------------------------------------------
Check(UpdateService.TryParseVersion("v0.4.0", out var v040) && v040 == new Version(0, 4, 0), "update: parses tag v0.4.0");
Check(UpdateService.TryParseVersion("1.2", out var v12) && v12 == new Version(1, 2, 0), "update: parses tag 1.2 as 1.2.0");
Check(!UpdateService.TryParseVersion("latest", out _), "update: rejects non-version tags");
var rel040 = new AppRelease(new Version(0, 4, 0), "v0.4.0", "", "", "", null, null);
Check(UpdateService.IsNewer(rel040, new Version(0, 3, 0)) && !UpdateService.IsNewer(rel040, new Version(0, 4, 0)), "update: version comparison");
var releaseAssets = new List<AppReleaseAsset>
{
    new("SHA256SUMS.txt", "u", 1),
    new("GTWModManager-0.4.0-win-x64-needs-dotnet9.exe", "u", 1),
    new("GTWModManager-0.4.0-win-x64.exe", "u", 1),
};
Check(UpdateService.PickExe(releaseAssets, selfContained: true)?.Name == "GTWModManager-0.4.0-win-x64.exe", "update: standalone build picks the standalone exe");
Check(UpdateService.PickExe(releaseAssets, selfContained: false)?.Name == "GTWModManager-0.4.0-win-x64-needs-dotnet9.exe", "update: .NET 9 build picks the small exe");

var updDir = Path.Combine(work, "selfupdate");
Directory.CreateDirectory(updDir);
var fakeExe = Path.Combine(updDir, "GTWModManager.exe");
File.WriteAllText(fakeExe, "old version");
var fakeNew = fakeExe + ".update";
File.WriteAllText(fakeNew, "new version");
var newHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fakeNew))).ToLowerInvariant();
UpdateService.VerifyChecksum(fakeNew, $"{newHash}  GTWModManager-0.4.0-win-x64.exe\nabc  other.exe\n", "GTWModManager-0.4.0-win-x64.exe");
Check(true, "update: checksum accepted");
Throws<InvalidDataException>(() => UpdateService.VerifyChecksum(fakeNew, $"{new string('0', 64)}  GTWModManager-0.4.0-win-x64.exe", "GTWModManager-0.4.0-win-x64.exe"), "update: wrong checksum rejected");
Throws<InvalidDataException>(() => UpdateService.VerifyChecksum(fakeNew, "abc  other.exe", "GTWModManager-0.4.0-win-x64.exe"), "update: missing checksum entry rejected");
UpdateService.SwapExecutable(fakeExe, fakeNew);
Check(File.ReadAllText(fakeExe) == "new version" && File.ReadAllText(fakeExe + ".old") == "old version" && !File.Exists(fakeNew), "update: swap puts the new exe in place and keeps the old one");
await UpdateService.CleanupAfterUpdateAsync(fakeExe);
Check(!File.Exists(fakeExe + ".old"), "update: cleanup removes the old exe");

// ---- real game folder: read-only scan -----------------------------------------------------------
var real = new ModService(new GamePaths(realGame)) { CheckGameRunning = false };
Console.WriteLine("\nReal game folder scan (read-only):");
foreach (var m in real.Scan())
    Console.WriteLine($"  [{(m.IsEnabled ? "x" : " ")}] {m.Name,-28} {m.Version,-8} {m.Loader,-11} {m.Kind,-9} {m.Location}");
foreach (var l in new[] { ModLoader.BepInEx, ModLoader.MelonLoader })
    Console.WriteLine("  " + real.LoaderStatus(l));

// ---- built-in list ----------------------------------------------------------------------------------
using var client = new ModCatalogClient();
var builtIn = await client.LoadIndexAsync(null);
Check(builtIn.Mods.Count >= 14 && builtIn.Mods.Count(m => m.Nexus is not null) >= 11, $"built-in list has GitHub and Nexus mods ({builtIn.Mods.Count})");

// ---- online: GitHub mods, MelonLoader download --------------------------------------------------------
if (online)
{
    foreach (var entry in builtIn.Mods.Where(m => m.Repo is not null))
    {
        var dl = await client.ResolveAsync(entry);
        var file = await client.DownloadAsync(dl);
        var installed = svc.InstallFromFile(file, new InstallRequest(entry.Id, entry.Name, dl.Version, entry.Author, "index", entry.LoaderHint));
        Check(installed.Items.Count > 0 && installed.Loader == ModLoader.BepInEx, $"GitHub install {entry.Name} {dl.Version}: {string.Join(", ", installed.Items)}");
    }

    var ml = await LoaderService.ResolveLatestAsync(client, ModLoader.MelonLoader);
    var mlZip = await client.DownloadAsync(ml);
    LoaderService.InstallFromZip(paths, ModLoader.MelonLoader, mlZip);
    var mlStatus = svc.LoaderStatus(ModLoader.MelonLoader);
    Check(mlStatus.Installed && !mlStatus.Active && svc.ActiveLoader == ModLoader.BepInEx, $"real MelonLoader {ml.Version} installed, left off while BepInEx is active ({mlStatus.Version})");
    svc.SetActiveLoader(ModLoader.MelonLoader);
    Check(svc.ActiveLoader == ModLoader.MelonLoader, "switch to real MelonLoader");

    var bep = await LoaderService.ResolveLatestAsync(client, ModLoader.BepInEx);
    Console.WriteLine($"Latest BepInEx 5: {bep.Version} {bep.FileName}");
}

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
