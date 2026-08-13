[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$Version = $Version.Trim()
if ($Version.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
    $Version = $Version.Substring(1)
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$') {
    throw "Invalid version '$Version'. Use MAJOR.MINOR.PATCH, for example 2.0.10."
}

$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ProjectPath = Join-Path $RepositoryRoot "ClientRenderer.GUI\ClientRenderer.GUI.csproj"
$PublishDirectory = Join-Path $RepositoryRoot "ClientRenderer.GUI\bin\Release\net9.0\win-x64\publish"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot "release-assets"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot $OutputDirectory
}

if (Test-Path $PublishDirectory) {
    Remove-Item $PublishDirectory -Recurse -Force
}
if (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $PublishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$ReleaseDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

Write-Host "Publishing ClientRenderer.GUI $Version..."
$publishArguments = @(
    "publish",
    $ProjectPath,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "false",
    "--output", $PublishDirectory,
    "--nologo",
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    "-p:ReleaseDate=$ReleaseDate",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$MainExecutable = Join-Path $PublishDirectory "ClientRenderer.GUI.exe"
if (-not (Test-Path $MainExecutable)) {
    throw "The published GUI executable was not found at '$MainExecutable'."
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "The Velopack CLI (vpk) is not installed or is not available on PATH."
}

Write-Host "Packing Velopack release..."
$packArguments = @(
    "pack",
    "--packId", "SosuBot.ClientRenderer",
    "--packVersion", $Version,
    "--packDir", $PublishDirectory,
    "--mainExe", "ClientRenderer.GUI.exe",
    "--outputDir", $OutputDirectory,
    "--channel", "win"
)
& vpk @packArguments
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed with exit code $LASTEXITCODE."
}

$Assets = Get-ChildItem -Path $OutputDirectory -File | Sort-Object Name
if ($Assets.Count -eq 0) {
    throw "Velopack did not produce any release assets in '$OutputDirectory'."
}

$ChecksumLines = foreach ($Asset in $Assets) {
    $Hash = (Get-FileHash -Path $Asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash *$($Asset.Name)"
}
$ChecksumLines | Set-Content -Path (Join-Path $OutputDirectory "SHA256SUMS.txt") -Encoding ascii

Write-Host "Release assets created in $OutputDirectory"
$Assets | ForEach-Object { Write-Host " - $($_.Name)" }
