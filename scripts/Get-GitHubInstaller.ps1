[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?\z')][string] $Version,
    [switch] $Preview
)

$ErrorActionPreference = 'Stop'
if (-not $env:GH_TOKEN -or -not $env:GITHUB_REPOSITORY) { throw 'GitHub Actions repository and token are required.' }
$tag = "v$Version"
if ($Preview) { $tag += '-preview' }
$name = "AzurPromilia_${Version}_Release.zip"
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json' }
$release = Invoke-RestMethod "https://api.github.com/repos/$env:GITHUB_REPOSITORY/releases/tags/$tag" -Headers $headers -TimeoutSec 30
if ($release.draft -or $release.prerelease -ne $Preview.IsPresent -or @($release.assets).Count -ne 1 -or
    $release.assets[0].name -cne $name -or $release.assets[0].state -cne 'uploaded') {
    throw "GitHub Release $tag must contain the expected installer ZIP as its only asset."
}
$asset = $release.assets[0]
$directory = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/packages'
gh release download $tag --repo $env:GITHUB_REPOSITORY --pattern $name --dir $directory | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Could not download the GitHub installer from $tag." }
$installer = Get-Item -LiteralPath (Join-Path $directory $name)
$digest = 'sha256:' + (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($installer.Length -ne $asset.size -or $digest -cne $asset.digest) {
    throw "The downloaded installer differs from its GitHub asset metadata for $tag."
}
$parsed = [version]$Version
$expected = [version]::new($parsed.Major, $parsed.Minor, $parsed.Build, [Math]::Max(0, $parsed.Revision))
& (Join-Path $PSScriptRoot 'Expand-PluginPackage.ps1') -ArchivePath $installer.FullName -ExpectedVersion $expected
return $installer.FullName
