#Requires -Version 5.1
<#
  Night Runners MP - uninstaller
  Removes the mod files; optionally removes MelonLoader as well.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Silent,
    [switch]$RemoveMelonLoader
)

$ErrorActionPreference = 'Stop'
$ExeName = 'NIGHT-RUNNERS PRIVATE ALPHA.exe'
$ProcName = 'NIGHT-RUNNERS PRIVATE ALPHA'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }
function Test-GameDir($dir) { return ($dir -and (Test-Path (Join-Path $dir $ExeName))) }

function Find-GameDir {
    if (Test-GameDir $GameDir) { return (Resolve-Path $GameDir).Path }
    if (Test-GameDir $Here) { return $Here }
    $bases = @((Join-Path $env:APPDATA 'itch\apps'), (Join-Path $env:LOCALAPPDATA 'itch\apps'))
    foreach ($d in Get-PSDrive -PSProvider FileSystem) {
        foreach ($sub in 'itch', 'itch\apps', 'Games\itch', 'Games') { $bases += Join-Path $d.Root $sub }
    }
    foreach ($base in $bases) {
        if (Test-Path $base) {
            foreach ($c in Get-ChildItem $base -Directory -ErrorAction SilentlyContinue) {
                if (Test-GameDir $c.FullName) { return $c.FullName }
            }
        }
    }
    if ($Silent) { throw "Game folder not found. Run: uninstall.ps1 -GameDir 'C:\path\to\the\game'" }
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Select your Night Runners game folder (it contains $ExeName)"
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Test-GameDir $dlg.SelectedPath)) { return $dlg.SelectedPath }
    throw 'Game folder not selected.'
}

Write-Host ''
Write-Host '  NIGHT RUNNERS MP - uninstaller' -ForegroundColor White
$game = Find-GameDir
Ok "Game folder: $game"
if (Get-Process -Name $ProcName -ErrorAction SilentlyContinue) { throw 'The game is running. Close it first.' }

foreach ($f in 'Mods\NightRunnersMP.dll', 'UserLibs\LiteNetLib.dll') {
    $p = Join-Path $game $f
    if (Test-Path $p) { Remove-Item $p -Force; Ok "Removed $f" }
}

$alsoML = $RemoveMelonLoader
if (-not $Silent -and -not $alsoML) {
    $a = Read-Host '  Also remove MelonLoader (the mod loader) to restore a vanilla game? [y/N]'
    $alsoML = ($a -match '^[yY]')
}
if ($alsoML) {
    foreach ($p in 'version.dll', 'MelonLoader', 'UserLibs', 'Mods') {
        $full = Join-Path $game $p
        if (Test-Path $full) { Remove-Item $full -Recurse -Force; Ok "Removed $p" }
    }
    Warn 'UserData (your config) was kept; delete it by hand if you want.'
}

Write-Host ''
Write-Host '  Done.' -ForegroundColor Green
