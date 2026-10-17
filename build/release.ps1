# Makes a release of the committed code: builds and tests it with publish.ps1, zips the result, writes SHA-256
# checksums, and tags the commit v<version>. Uploading the zip anywhere (GitHub, a file share) is a separate step.
# To release a new version, raise <Version> in Directory.Build.props and add it to CHANGELOG.md first.
param(
    [switch]$NoTag
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$tag = "v$version"

Push-Location $root
try {
    # A release has to be reproducible from its tag, so it's built only from committed code.
    if (git status --porcelain) { throw 'There are uncommitted changes. Commit them first: a release must match a commit.' }
    if (git tag --list $tag) { throw "$tag already exists. Raise <Version> in Directory.Build.props for a new release." }
    if (-not (Select-String -Path CHANGELOG.md -Pattern "^## $([regex]::Escape($version))\b" -Quiet)) {
        throw "CHANGELOG.md has no '## $version' section yet."
    }

    & (Join-Path $PSScriptRoot 'publish.ps1')

    $folder = Join-Path $root "dist\Ferry-$version"
    $zip = Join-Path $root "dist\Ferry-$version-win-x64.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Write-Host 'Zipping…'
    # Zipping the folder itself, so unzipping gives a tidy Ferry-<version> folder rather than loose files.
    Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal

    $sums = Join-Path $root "dist\Ferry-$version-SHA256SUMS.txt"
    @($zip, (Join-Path $folder 'Ferry.exe'), (Join-Path $folder 'ferry-cli.exe')) | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf)
    } | Set-Content $sums -Encoding ascii

    if (-not $NoTag) {
        git tag -a $tag -m "Ferry $version"
        if ($LASTEXITCODE -ne 0) { throw "Couldn't create the tag $tag." }
    }

    Write-Host ''
    Write-Host "Release $version ($(git rev-parse --short HEAD)$(if (-not $NoTag) { ", tagged $tag" }))"
    Get-Item $zip, $sums | Select-Object Name, @{ n = 'Size'; e = { '{0:N1} MB' -f ($_.Length / 1MB) } } | Format-Table -AutoSize
}
finally {
    Pop-Location
}
