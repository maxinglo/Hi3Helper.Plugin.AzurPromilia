[CmdletBinding()]
param(
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?\z')][string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/Hi3Helper.Plugin.AzurPromilia/Hi3Helper.Plugin.AzurPromilia.csproj'
if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
}
$parsed = [version]$Version
$nativeVersion = [version]::new($parsed.Major, $parsed.Minor, $parsed.Build, [Math]::Max(0, $parsed.Revision))
$packages = Join-Path $root 'artifacts/packages'
$archive = Join-Path $packages "AzurPromilia_${Version}_Release.zip"
$output = [IO.Directory]::CreateTempSubdirectory('azur-promilia-publish-').FullName

Push-Location $root
try {
    dotnet publish $project -c Release -r win-x64 -p:Platform=x64 -p:RestoreLockedMode=true `
        -p:CopyOutputSymbolsToPublishDirectory=false "-p:Version=$Version" --nologo -o $output
    if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish failed ($LASTEXITCODE)." }

    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    # Index in another process so Windows releases the NativeAOT module before cleanup.
    & $pwsh -NoProfile -File (Join-Path $PSScriptRoot 'New-PluginPackage.ps1') `
        -PublishDirectory $output -ArchivePath $archive -ExpectedVersion $nativeVersion
    if ($LASTEXITCODE -ne 0) { throw "Plugin indexing or packaging failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
    $directory = Get-Item -LiteralPath $output
    $outputPath = $directory.FullName
    if ((Split-Path $outputPath -Parent) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') -or
        (Split-Path $outputPath -Leaf) -notlike 'azur-promilia-publish-*' -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected publish directory: $outputPath"
    }
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
