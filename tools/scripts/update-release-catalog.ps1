#requires -Version 7.0
<#
.SYNOPSIS
Creates a public release catalog for embedding and optionally publishes it to the launcher-feed branch.
.PARAMETER InputFile
Optional saved GitHub release-array JSON for offline packaging. Otherwise gh reads releases using the CI token.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Output,
    [string] $Repository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'Yuuuunna/MeitouCoop' }),
    [string] $InputFile = '',
    [switch] $Publish
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository name.' }

function Invoke-GitHubJson {
    param([string] $Endpoint, [string[]] $Options = @(), [switch] $AllowMissing)
    $result = & gh api $Endpoint @Options 2>&1
    if ($LASTEXITCODE -eq 0) { return ($result | Out-String | ConvertFrom-Json -Depth 100 -NoEnumerate) }
    if ($AllowMissing -and (($result | Out-String) -match '\(HTTP 404\)')) { return $null }

    throw "GitHub request failed: $Endpoint. $($result | Out-String)"
}

if ($InputFile) {
    $all = @(Get-Content -LiteralPath $InputFile -Raw | ConvertFrom-Json -Depth 100)
} else {
    $pages = Invoke-GitHubJson "repos/$Repository/releases?per_page=100" -Options @('--paginate', '--slurp')
    $all = @(foreach ($page in $pages) { foreach ($release in $page) { $release } })
}

$releases = @(foreach ($release in $all) {
    if ($release.draft -or $release.tag_name -notmatch '^(coop-v|launcher-v|v[0-9])') { continue }

    [ordered]@{
        draft = $false
        tag_name = $release.tag_name
        name = $release.name
        prerelease = [bool]$release.prerelease
        published_at = $release.published_at
        body = $release.body
        html_url = $release.html_url
        assets = @(foreach ($asset in $release.assets) {
            [ordered]@{ name=$asset.name; browser_download_url=$asset.browser_download_url; size=$asset.size; digest=$asset.digest }
        })
    }
})
$catalog = [ordered]@{
    format = 1
    repository = $Repository
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    releases = $releases
}
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
[IO.File]::WriteAllText($outputPath, ($catalog | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Output "Catalog: $outputPath ($($releases.Count) releases)"
if (-not $Publish) { return }

# Only a data branch is changed; release metadata never commits code or workflows back to master.
$branch = 'launcher-feed'
$reference = Invoke-GitHubJson "repos/$Repository/git/ref/heads/$branch" -AllowMissing
if (-not $reference) {
    $source = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (git rev-parse HEAD).Trim() }
    Invoke-GitHubJson "repos/$Repository/git/refs" -Options @('--method','POST','-f',"ref=refs/heads/$branch",'-f',"sha=$source") | Out-Null
}
$existing = Invoke-GitHubJson "repos/$Repository/contents/releases.json?ref=$branch" -AllowMissing
$request = @{
    message = 'Update public launcher release catalog'
    branch = $branch
    content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($outputPath))
}
if ($existing) { $request.sha = $existing.sha }
$requestPath = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Invoke-GitHubJson "repos/$Repository/contents/releases.json" -Options @('--method','PUT','--input',$requestPath) | Out-Null
} finally {
    Remove-Item -LiteralPath $requestPath
}
Write-Output "Published: https://raw.githubusercontent.com/$Repository/$branch/releases.json"
