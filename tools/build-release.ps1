<#
.SYNOPSIS
    Builds the release files for Windows and Linux and SHA256SUMS.txt, and optionally publishes a GitHub release.

.DESCRIPTION
    Each app's built-in updater looks for these exact file names, so always build releases with this script:
      GTWModManager-<version>-win-x64.exe                 Windows app, standalone (bundles .NET)
      GTWModManager-<version>-win-x64-needs-dotnet9.exe   Windows app, needs the .NET 9 Desktop Runtime
      GTWModManager-<version>-linux-x64                   Linux app, standalone
      SHA256SUMS.txt
    The version comes from <Version> in src/Directory.Build.props, shared by both apps. Bump it there first.

.EXAMPLE
    .\tools\build-release.ps1
    Builds into publish\release.

.EXAMPLE
    .\tools\build-release.ps1 -Publish -NotesFile notes.md
    Builds, tags v<version>, pushes the tag and creates the GitHub release with the files attached.
#>
param(
    [switch]$Publish,
    [string]$NotesFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$props = Join-Path $root 'src\Directory.Build.props'
$version = ([xml](Get-Content $props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $props" }
$windowsApp = Join-Path $root 'src\GTWModManager\GTWModManager.csproj'
$linuxApp = Join-Path $root 'src\GTWModManager.Linux\GTWModManager.Linux.csproj'

$out = Join-Path $root 'publish'
$release = Join-Path $out 'release'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
$singleFile = @('-p:PublishSingleFile=true', '-p:DebugType=none')
$bundled = @('--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')

Write-Host "Building GTW Mod Manager $version"
dotnet publish $windowsApp -c Release -r win-x64 @bundled @singleFile -o (Join-Path $out 'standalone')
if ($LASTEXITCODE) { throw 'Windows standalone build failed' }
dotnet publish $windowsApp -c Release -r win-x64 --self-contained false @singleFile -o (Join-Path $out 'framework')
if ($LASTEXITCODE) { throw 'Windows framework-dependent build failed' }
dotnet publish $linuxApp -c Release -r linux-x64 @bundled @singleFile -o (Join-Path $out 'linux')
if ($LASTEXITCODE) { throw 'Linux build failed' }

New-Item -ItemType Directory -Force $release | Out-Null
$files = [ordered]@{
    "GTWModManager-$version-win-x64.exe"               = Join-Path $out 'standalone\GTWModManager.exe'
    "GTWModManager-$version-win-x64-needs-dotnet9.exe" = Join-Path $out 'framework\GTWModManager.exe'
    "GTWModManager-$version-linux-x64"                 = Join-Path $out 'linux\GTWModManager'
}
foreach ($name in $files.Keys) { Copy-Item $files[$name] (Join-Path $release $name) }
$sums = Join-Path $release 'SHA256SUMS.txt'
$files.Keys | ForEach-Object { "$((Get-FileHash (Join-Path $release $_) -Algorithm SHA256).Hash.ToLower())  $_" } |
    Set-Content -Encoding ascii $sums
Get-ChildItem $release | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Out-String | Write-Host

if ($Publish) {
    # git and gh write progress to stderr, which Windows PowerShell 5.1 would treat as a failure
    # under 'Stop'; check exit codes instead.
    $ErrorActionPreference = 'Continue'
    if (git -C $root status --porcelain) { throw 'Commit your changes before publishing a release.' }
    $tag = "v$version"
    if (-not (git -C $root tag --list $tag)) {
        git -C $root tag -a $tag -m "GTW Mod Manager $version" 2>&1 | Out-Host
        if ($LASTEXITCODE) { throw "Couldn't create tag $tag" }
    }
    git -C $root push origin $tag 2>&1 | Out-Host
    if ($LASTEXITCODE) { throw "Couldn't push tag $tag" }
    $notes = if ($NotesFile) { @('--notes-file', $NotesFile) } else { @('--generate-notes') }
    $assets = @($files.Keys | ForEach-Object { Join-Path $release $_ }) + $sums
    gh release create $tag --repo Dullfin/gtw-mod-manager --title "GTW Mod Manager $version" @notes @assets 2>&1 | Out-Host
    if ($LASTEXITCODE) { throw "Couldn't create the GitHub release" }
}
