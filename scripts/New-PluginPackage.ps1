[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $ArchivePath,
    [Parameter(Mandatory)][version] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$archive = [IO.Path]::GetFullPath($ArchivePath)
$manifestPath = Join-Path $root 'manifest.json'
$library = 'AzurPromilia.dll'
$files = @(Get-ChildItem -LiteralPath $root | Sort-Object Name)
foreach ($file in $files) {
    if ($file.PSIsContainer -or $file.Name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]*\z' -or
        $file.Name.EndsWith('.') -or $file.Name -eq 'manifest.json') {
        throw "Unexpected release asset: $($file.Name)"
    }
}
if ($library -cnotin $files.Name) { throw "Missing native plugin: $library" }

Add-Type -Path (Join-Path $PSScriptRoot 'PluginIndexer.cs') -CompilerOptions '/unsafe'
$plugin = [PluginIndexer]::Read((Join-Path $root $library))
if ($plugin.Version -ne $ExpectedVersion) { throw "Native plugin version is $($plugin.Version), expected $ExpectedVersion." }
if ($plugin.UpdateSources[0] -cne 'https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/releases/download/collapse-update' -or
    $plugin.UpdateSources[1] -cne 'https://plugins.freedorm.net/collapse-plugins/azur-promilia/collapse-update') {
    throw 'The native plugin exports unexpected update sources.'
}

$manifest = [ordered]@{
    PluginVersion = $plugin.Version.ToString()
    PluginStandardVersion = $plugin.StandardVersion.ToString()
    PluginCreationDate = $plugin.CreationDate.ToUniversalTime().ToString('O')
    PluginAlternativeIcon = $plugin.Icon
    ManifestDate = [DateTime]::UtcNow.ToString('O')
    MainLibraryName = $library
    MainPluginName = $plugin.Name
    MainPluginAuthor = $plugin.Author
    MainPluginDescription = $plugin.Description
    Assets = @($files | ForEach-Object {
        [ordered]@{
            FilePath = $_.Name
            Size = $_.Length
            FileHash = [Convert]::ToBase64String([Convert]::FromHexString(
                (Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash))
        }
    })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

New-Item -ItemType Directory -Force -Path (Split-Path $archive -Parent) | Out-Null
$temporary = "$archive.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files + @(Get-Item -LiteralPath $manifestPath)) {
            # Collapse compares each ZIP entry name directly with its manifest path.
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $file.Name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }

    & (Join-Path $PSScriptRoot 'Expand-PluginPackage.ps1') -ArchivePath $temporary
    [IO.File]::Move($temporary, $archive, $true)
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
Write-Host "Package: $archive"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $archive).Hash)"
