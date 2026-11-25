[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?\z')][string] $Version
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_REF -cnotin @('refs/heads/stable', 'refs/heads/preview') -or -not $env:GH_TOKEN) {
    throw 'This script publishes from a GitHub Actions stable or preview run.'
}

$tag = "v$Version"
$preview = $env:GITHUB_REF -ceq 'refs/heads/preview'
if ($preview) { $tag += '-preview' }

$ref = "refs/tags/$tag"
git show-ref --verify --quiet $ref
$tagExists = $LASTEXITCODE -eq 0
if ($tagExists) {
    $tagCommit = git rev-parse "$ref^{}"
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $env:GITHUB_SHA) {
        throw "The existing $tag tag points to a different commit."
    }
}
elseif ($LASTEXITCODE -ne 1) { throw "Unable to check tag $tag." }

$releaseApi = "https://api.github.com/repos/$env:GITHUB_REPOSITORY/releases/tags/$tag"
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json' }
$null = Invoke-RestMethod -Uri $releaseApi -Headers $headers -TimeoutSec 30 -SkipHttpErrorCheck -StatusCodeVariable status
if ($status -eq 200) {
    if (-not $tagExists) { throw "The existing $tag release has no tag in the checkout." }
    # Keep the published bytes on retries, including the manifest timestamp.
    ./scripts/Get-GitHubInstaller.ps1 -Version $Version -Preview:$preview | Out-Host
    return
}
if ($status -ne 404) { throw "Could not look up GitHub Release $tag (HTTP $status)." }

./scripts/Publish.ps1 -Version $Version
$archive = "artifacts/packages/AzurPromilia_${Version}_Release.zip"
$arguments = @('release', 'create', $tag, $archive, '--repo', $env:GITHUB_REPOSITORY, '--target', $env:GITHUB_SHA,
    '--title', "Azur Promilia Plugin $tag", '--generate-notes')
if ($preview) { $arguments += '--prerelease' }
else { $arguments += '--latest' }
if ($tagExists) { $arguments += '--verify-tag' }
gh @arguments
if ($LASTEXITCODE -ne 0) { throw "GitHub Release publication failed for $tag." }
