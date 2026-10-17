# Builds Ferry for distribution: the app (Ferry.exe) and the command line (ferry.exe), each a single
# self-contained file, so they run straight from a USB stick on PCs without .NET installed.
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$out = Join-Path $root "dist\Ferry-$version"

if (-not $SkipTests) {
    Write-Host 'Running tests…'
    dotnet test (Join-Path $root 'tests\Ferry.Core.Tests\Ferry.Core.Tests.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }
}

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
$common = @(
    '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--nologo',
    '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none', '-p:GenerateDocumentationFile=false', '-o', $out
)
foreach ($project in 'src\Ferry.App\Ferry.App.csproj', 'src\Ferry.Cli\Ferry.Cli.csproj') {
    Write-Host "Publishing $project…"
    dotnet publish (Join-Path $root $project) @common
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}
Copy-Item (Join-Path $root 'README.md') $out -ErrorAction SilentlyContinue
# The user and IT guides (with their screenshots) travel with the executables; the README links to them there too.
Copy-Item (Join-Path $root 'docs') $out -Recurse -Force -ErrorAction SilentlyContinue

Get-ChildItem $out | Select-Object Name, @{ n = 'Size'; e = { '{0:N1} MB' -f ($_.Length / 1MB) } } | Format-Table -AutoSize
Write-Host "Ready: $out"
