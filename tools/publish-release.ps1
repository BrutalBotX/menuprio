# Builds MenuPrio and publishes a GitHub release with the exe and a zip.
#
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Version 1.2.0 -Notes "custom notes"
#   powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -SkipBuild
#
# Reuses the credentials git already has for origin (git credential fill),
# so there is nothing extra to log into.

param(
    [string]$Version = "",
    [string]$Notes = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    if (-not $SkipBuild) {
        & cmd /c build.cmd | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "build failed" }
    }

    if (-not $Version) {
        $m = Select-String -Path "src\AssemblyInfo.cs" -Pattern 'AssemblyInformationalVersion\("([^"]+)"\)'
        if (-not $m) { throw "could not read the version from src\AssemblyInfo.cs" }
        $Version = $m.Matches[0].Groups[1].Value
    }
    $tag = "v$Version"

    # ---- package ----
    $dist = Join-Path $root "dist"
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist "MenuPrio-$Version-win.zip"
    $stage = Join-Path $dist "stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item "MenuPrio.exe" $stage
    Copy-Item "README.md", "LICENSE" $stage
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
    Remove-Item $stage -Recurse -Force

    # ---- repo + credentials ----
    $remote = git remote get-url origin
    if ($remote -notmatch 'github\.com[:/](?<slug>[^/]+/[^/]+?)(\.git)?$') {
        throw "origin is not a GitHub URL: $remote"
    }
    $slug = $Matches['slug']

    $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $user = (($cred | Select-String '^username=').Line -replace '^username=', '')
    $pass = (($cred | Select-String '^password=').Line -replace '^password=', '')
    if (-not $user -or -not $pass) { throw "no stored GitHub credentials (git credential fill returned nothing)" }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${user}:${pass}"))
    $auth = @{ Authorization = "Basic $b64"; 'User-Agent' = 'menuprio-release' }
    Remove-Variable pass, cred

    if (-not $Notes) {
        $log = (git log --pretty=format:"- %s" -10) -join "`n"
        $Notes = "MenuPrio $Version`n`nChanges since the last release:`n`n$log`n`nAssets: **MenuPrio.exe** (single file, no install) and a zip with the exe + docs."
    }

    # ---- release ----
    $release = $null
    try {
        $body = @{ tag_name = $tag; name = $tag; body = $Notes; draft = $false; prerelease = $false } | ConvertTo-Json
        $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$slug/releases" `
            -Headers $auth -Body $body -ContentType 'application/json'
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -eq 422) {
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/releases/tags/$tag" -Headers $auth
            Write-Host "release $tag already exists, uploading assets to it"
        }
        else {
            throw
        }
    }

    # ---- assets (replace if present) ----
    $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/releases/$($release.id)/assets" -Headers $auth

    foreach ($file in @("MenuPrio.exe", $zip)) {
        $name = Split-Path $file -Leaf

        foreach ($old in @($existing) | Where-Object { $_.name -eq $name }) {
            Invoke-RestMethod -Method Delete -Uri $old.url -Headers $auth | Out-Null
            Write-Host "removed old asset $name"
        }

        $uploadUrl = "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=$name"
        $headers = @{
            Authorization  = $auth['Authorization']
            'User-Agent'   = 'menuprio-release'
            'Content-Type' = 'application/octet-stream'
        }
        $asset = Invoke-RestMethod -Method Post -Uri $uploadUrl -Headers $headers -InFile $file
        Write-Host ("uploaded {0} ({1:N0} bytes) -> {2}" -f $name, (Get-Item $file).Length, $asset.browser_download_url)
    }

    Write-Host ""
    Write-Host "release: $($release.html_url)"
}
finally {
    Pop-Location
}
