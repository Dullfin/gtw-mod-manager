<#
.SYNOPSIS
    Builds the two release exes and SHA256SUMS.txt, and optionally publishes a GitHub release.

.DESCRIPTION
    The app's built-in updater looks for these exact file names, so always build releases with this script:
      GTWModManager-<version>-win-x64.exe                 standalone, bundles .NET
      GTWModManager-<version>-win-x64-needs-dotnet9.exe   needs the .NET 9 Desktop Runtime
      SHA256SUMS.txt
    The version comes from <Version> in src/GTWModManager/GTWModManager.csproj. Bump it there first.

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
$project = Join-Path $root 'src\GTWModManager\GTWModManager.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }

$out = Join-Path $root 'publish'
$release = Join-Path $out 'release'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

Write-Host "Building GTW Mod Manager $version"
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $out 'standalone')
if ($LASTEXITCODE) { throw 'Standalone build failed' }
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o (Join-Path $out 'framework')
if ($LASTEXITCODE) { throw 'Framework-dependent build failed' }

New-Item -ItemType Directory -Force $release | Out-Null
$standalone = "GTWModManager-$version-win-x64.exe"
$framework = "GTWModManager-$version-win-x64-needs-dotnet9.exe"
Copy-Item (Join-Path $out 'standalone\GTWModManager.exe') (Join-Path $release $standalone)
Copy-Item (Join-Path $out 'framework\GTWModManager.exe') (Join-Path $release $framework)
$sums = Join-Path $release 'SHA256SUMS.txt'
Get-ChildItem $release -Filter *.exe | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" } |
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
    gh release create $tag --repo Dullfin/gtw-mod-manager --title "GTW Mod Manager $version" @notes `
        (Join-Path $release $standalone) (Join-Path $release $framework) $sums 2>&1 | Out-Host
    if ($LASTEXITCODE) { throw "Couldn't create the GitHub release" }
}
