<#
  Builds a Blackbird release, as gscode-installer's docs/release-contract.md describes:

    artifacts\blackbird-X.Y.Z-win-x64.zip   the bundle: bin\modlauncher.exe and bin\steam\steam_api64.dll
    artifacts\blackbird-setup.exe           the installer, with that bundle inside

  .\release.ps1                      build both (refuses uncommitted changes; -AllowDirty for a test build)
  .\release.ps1 -Publish -NotesFile notes.md
                                     also create the GitHub release vX.Y.Z from them; fails if the tag exists

  Needs the .NET 10 SDK and, to publish, the GitHub CLI.
#>
param(
    [string] $Installer = 'J:\Github\gscode-installer',
    [switch] $AllowDirty,
    [switch] $Publish,
    [string] $NotesFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = $PSScriptRoot
$id = 'blackbird'
$project = "$root\Blackbird\Blackbird.csproj"

if (-not $AllowDirty -and (git -C $root status --porcelain --untracked-files=no)) {
    throw 'Uncommitted changes. Commit them, or pass -AllowDirty for a test build.'
}
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
if ($Publish) {
    if (-not $NotesFile -or -not (Test-Path $NotesFile)) { throw '-Publish needs -NotesFile <release notes .md>' }
    if (git -C $root ls-remote --tags origin "refs/tags/$tag") { throw "$tag already exists on GitHub" }
}

$name = "$id-$version-win-x64"
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts $name
$zip = "$stage.zip"
$build = Join-Path $artifacts 'build'
Remove-Item $stage, $zip, $build -Recurse -Force -ErrorAction SilentlyContinue

# Single-file modlauncher.exe (needs the .NET 10 desktop runtime) with steam_api64.dll in a steam\ subfolder.
# Built under artifacts\, so a Blackbird running from its bin\ can't lock the build.
dotnet publish $project -c Release -o "$build\out" --artifacts-path "$build\obj" --nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
New-Item -ItemType Directory "$stage\bin\steam" -Force | Out-Null
Copy-Item "$build\out\modlauncher.exe" "$stage\bin\modlauncher.exe"
Copy-Item "$build\out\steam\steam_api64.dll" "$stage\bin\steam\steam_api64.dll"
Remove-Item $build -Recurse -Force
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

$setup = & "$Installer\scripts\build-setup.ps1" -Bundle $zip
foreach ($f in $zip, $setup) { '{0}  {1:N1} MB' -f $f, ((Get-Item $f).Length / 1MB) }

if ($Publish) {
    gh release create $tag $zip $setup --target (git -C $root rev-parse HEAD) --title "Blackbird $version" --notes-file $NotesFile
    if ($LASTEXITCODE) { throw "gh release create failed ($LASTEXITCODE)" }
}
