#Requires -Version 5.1
<#
  Night Runners MP - uninstaller
  Removes the mod from every Night Runners install found (alpha and/or Prologue);
  optionally removes MelonLoader as well.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Silent,
    [switch]$RemoveMelonLoader
)

$ErrorActionPreference = 'Stop'
$Games = @(
    @{ Key = 'alpha';    Name = 'Night Runners (private alpha, itch)'; Exe = 'NIGHT-RUNNERS PRIVATE ALPHA.exe' },
    @{ Key = 'prologue'; Name = 'Night Runners Prologue (Steam)';      Exe = 'NIGHT-RUNNERS PROLOGUE.exe' }
)
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }

function Get-GameIn($dir) {
    if (-not $dir) { return $null }
    foreach ($g in $Games) { if (Test-Path (Join-Path $dir $g.Exe)) { return @{ Game = $g; Dir = (Resolve-Path $dir).Path } } }
    return $null
}

function Get-SteamLibraries {
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($reg in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        try { $k = Get-ItemProperty $reg -ErrorAction Stop; foreach ($v in $k.SteamPath, $k.InstallPath) { if ($v) { $libs.Add(($v -replace '/', '\')) } } } catch { }
    }
    foreach ($d in Get-PSDrive -PSProvider FileSystem) { foreach ($sub in 'SteamLibrary', 'Steam', 'Program Files (x86)\Steam', 'Games\Steam') { $libs.Add((Join-Path $d.Root $sub)) } }
    $extra = New-Object System.Collections.Generic.List[string]
    foreach ($l in $libs) {
        $vdf = Join-Path $l 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $extra.Add(($m.Groups[1].Value -replace '\\\\', '\')) } }
    }
    return ($libs + $extra) | Where-Object { $_ } | Select-Object -Unique
}

function Find-Installs {
    $found = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $add = { param($hit) if ($hit -and -not $seen.ContainsKey($hit.Dir)) { $seen[$hit.Dir] = $true; $found.Add($hit) } }
    & $add (Get-GameIn $GameDir)
    if ($GameDir) { return $found.ToArray() }
    & $add (Get-GameIn $Here)
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($base in @((Join-Path $env:APPDATA 'itch\apps'), (Join-Path $env:LOCALAPPDATA 'itch\apps'))) { $roots.Add($base) }
    foreach ($d in Get-PSDrive -PSProvider FileSystem) { foreach ($sub in 'itch', 'itch\apps', 'Games\itch', 'Games') { $roots.Add((Join-Path $d.Root $sub)) } }
    foreach ($lib in Get-SteamLibraries) { $roots.Add((Join-Path $lib 'steamapps\common')) }
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($dir in Get-ChildItem $root -Directory -ErrorAction SilentlyContinue) { & $add (Get-GameIn $dir.FullName) }
    }
    return $found.ToArray()
}

Write-Host ''
Write-Host '  NIGHT RUNNERS MP - uninstaller' -ForegroundColor White
$installs = @(Find-Installs)
if ($installs.Count -eq 0) {
    if ($Silent) { throw "No game folder found. Run: uninstall.ps1 -GameDir 'C:\path\to\the\game'" }
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select your Night Runners game folder'
    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw 'Cancelled.' }
    $hit = Get-GameIn $dlg.SelectedPath
    if (-not $hit) { throw 'That folder does not contain a Night Runners executable.' }
    $installs = @($hit)
}

$alsoML = $RemoveMelonLoader
if (-not $Silent -and -not $alsoML) {
    $a = Read-Host '  Also remove MelonLoader (the mod loader) to restore vanilla games? [y/N]'
    $alsoML = ($a -match '^[yY]')
}

foreach ($i in $installs) {
    $game = $i.Dir
    Write-Host ''
    Ok "$($i.Game.Name): $game"
    $procName = [IO.Path]::GetFileNameWithoutExtension($i.Game.Exe)
    if (Get-Process -Name $procName -ErrorAction SilentlyContinue) { Warn 'game is running - skipped; close it and run again'; continue }
    foreach ($f in 'Mods\NightRunnersMP.dll', 'UserLibs\LiteNetLib.dll') {
        $p = Join-Path $game $f
        if (Test-Path $p) { Remove-Item $p -Force; Ok "removed $f" }
    }
    if ($alsoML) {
        foreach ($p in 'version.dll', 'MelonLoader', 'UserLibs', 'Mods') {
            $full = Join-Path $game $p
            if (Test-Path $full) { Remove-Item $full -Recurse -Force; Ok "removed $p" }
        }
        Warn 'UserData (your config) was kept; delete it by hand if you want.'
    }
}

Write-Host ''
Write-Host '  Done.' -ForegroundColor Green
