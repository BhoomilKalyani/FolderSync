<#
.SYNOPSIS
    Publishes Folder Sync self-contained and compiles the Windows installer.

.DESCRIPTION
    Produces dist\FolderSyncSetup-<version>.exe. The version comes from <Version> in
    FolderSync.csproj, so bump it there to cut a new release.

    Requires: .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Version 1.1.0     # override the version
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishDir = Join-Path $root 'publish'
$distDir    = Join-Path $root 'dist'

function Find-Tool {
    param([string[]]$Candidates, [string]$Name, [string]$Hint)
    foreach ($c in $Candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "$Name not found. $Hint"
}

$dotnet = Find-Tool -Candidates @(
    "$env:ProgramFiles\dotnet\dotnet.exe"
) -Name 'dotnet' -Hint 'Install the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8'

$iscc = Find-Tool -Candidates @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) -Name 'ISCC' -Hint 'Install Inno Setup: winget install JRSoftware.InnoSetup'

# Version: use -Version, else read <Version> from the csproj.
if (-not $Version) {
    $csproj = [xml](Get-Content (Join-Path $root 'FolderSync.csproj'))
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw 'Could not read <Version> from FolderSync.csproj.' }
}

Write-Host "Building Folder Sync $Version" -ForegroundColor Cyan

# Don't package files that a running instance still holds open.
Get-Process FolderSync -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir, $distDir | Out-Null

Write-Host '1/2  Publishing self-contained win-x64...' -ForegroundColor Cyan
$publishArgs = @(
    'publish', (Join-Path $root 'FolderSync.csproj'),
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    "-p:Version=$Version",
    '-p:PublishSingleFile=false',
    '-p:DebugType=none',
    '-o', $publishDir
)
& $dotnet @publishArgs | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$exe = Join-Path $publishDir 'FolderSync.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object -Sum Length).Sum / 1MB), 1)
Write-Host "     published $sizeMb MB" -ForegroundColor DarkGray

Write-Host '2/2  Compiling installer...' -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" (Join-Path $root 'installer\FolderSync.iss') |
    Select-String -Pattern 'Successful|error|warning' | ForEach-Object { "     $($_.Line.Trim())" }
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

$setup = Join-Path $distDir "FolderSyncSetup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer not found at $setup" }
$setupMb = [math]::Round(((Get-Item $setup).Length / 1MB), 1)

Write-Host ''
Write-Host "Done: $setup  ($setupMb MB)" -ForegroundColor Green
Write-Host ''
Write-Host 'To release it so other installs get the update notification:' -ForegroundColor Yellow
Write-Host "  1. git tag v$Version && git push origin v$Version"
Write-Host "  2. Create a GitHub release for tag v$Version and attach the setup .exe above."
