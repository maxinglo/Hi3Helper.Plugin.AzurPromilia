[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArchivePath,
    [string] $UpdateDirectory,
    [version] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$staging = $null
if ($UpdateDirectory) {
    $destination = [IO.Path]::GetFullPath($UpdateDirectory)
    if (Test-Path -LiteralPath $destination) { throw "Update directory already exists: $destination" }
    $staging = "$destination.$([Guid]::NewGuid().ToString('N')).tmp"
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
}

$zip = $null
try {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName
        if ($name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]*\z' -or $name.EndsWith('.') -or -not $entries.TryAdd($name, $entry)) {
            throw "Invalid or duplicate package entry: $name"
        }
    }
    $manifestEntry = $entries['manifest.json']
    if (-not $manifestEntry -or $manifestEntry.FullName -cne 'manifest.json') { throw 'Missing package manifest.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }

    if ($manifest.MainLibraryName -cne 'AzurPromilia.dll' -or $manifest.PluginStandardVersion -cne '0.1.6.2' -or
        $entries.Count -ne @($manifest.Assets).Count + 1) {
        throw 'Unexpected plugin manifest.'
    }
    $manifestVersion = [version]$manifest.PluginVersion
    $normalizedVersion = [version]::new($manifestVersion.Major, $manifestVersion.Minor, $manifestVersion.Build, [Math]::Max(0, $manifestVersion.Revision))
    if ($ExpectedVersion) {
        $ExpectedVersion = [version]::new($ExpectedVersion.Major, $ExpectedVersion.Minor, $ExpectedVersion.Build, [Math]::Max(0, $ExpectedVersion.Revision))
    }
    if ($ExpectedVersion -and $normalizedVersion -ne $ExpectedVersion) {
        throw "The installer contains plugin version $($manifest.PluginVersion), expected $ExpectedVersion."
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $manifest.Assets) {
        $name = [string]$asset.FilePath
        if (-not $seen.Add($name) -or $name -eq 'manifest.json' -or -not $entries.ContainsKey($name)) {
            throw "Invalid or missing manifest asset: $name"
        }
        $entry = $entries[$name]
        if ($entry.FullName -cne $name -or $entry.Length -ne $asset.Size) {
            throw "Package path or size mismatch: $name"
        }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToBase64String([Security.Cryptography.MD5]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -cne $asset.FileHash) { throw "Package checksum mismatch: $name" }
    }
    if (-not $seen.Contains($manifest.MainLibraryName)) { throw 'The manifest does not list the plugin DLL.' }

    if ($staging) {
        foreach ($entry in $entries.Values) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $entry.FullName))
        }
        [IO.Directory]::Move($staging, $destination)
    }
    Write-Host "Verified Collapse import paths and hashes for plugin $($manifest.PluginVersion)."
}
finally {
    if ($zip) { $zip.Dispose() }
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        Get-ChildItem -LiteralPath $staging -File | Remove-Item -Force
        [IO.Directory]::Delete($staging)
    }
}
