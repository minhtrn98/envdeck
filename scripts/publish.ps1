<#
.SYNOPSIS
    Builds release artifacts into artifacts/dist: a portable zip and, when Inno Setup is installed, a per-user installer.

.EXAMPLE
    ./scripts/publish.ps1 -Version 0.2.0
    ./scripts/publish.ps1 -Version 0.2.0 -Runtime win-arm64
#>
param(
    [string] $Version = '0.1.0',
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/publish/$Runtime"
$dist = Join-Path $root 'artifacts/dist'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

# Framework-dependent: the install is ~1 MB and winget pulls in the .NET Desktop Runtime as a dependency.
$publish = @('-c', $Configuration, '-r', $Runtime, '--self-contained', 'false', "-p:Version=$Version", '-o', $out, '--nologo')
foreach ($project in 'src/EnvDeck.App/EnvDeck.App.csproj', 'src/EnvDeck.Cli/EnvDeck.Cli.csproj') {
    dotnet publish (Join-Path $root $project) @publish
    if ($LASTEXITCODE) { throw "dotnet publish $project failed" }
}
Get-ChildItem $out -Filter *.pdb | Remove-Item

$zip = Join-Path $dist "EnvDeck-$Version-$Runtime.zip"
Compress-Archive -Path "$out/*" -DestinationPath $zip -Force
Write-Host "portable: $zip"

$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue)?.Source
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (Test-Path $iscc) {
    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$out" "/DOutputDir=$dist" "/DArch=$($Runtime -replace '^win-')" `
        (Join-Path $root 'packaging/inno/EnvDeck.iss')
    if ($LASTEXITCODE) { throw 'Inno Setup failed' }
    Write-Host "installer: $dist\EnvDeck-$Version-$Runtime-setup.exe"
}
else {
    Write-Warning 'Inno Setup not found, installer skipped (winget install JRSoftware.InnoSetup).'
}

Get-ChildItem $dist -File | Get-FileHash -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash, (Split-Path $_.Path -Leaf) }
