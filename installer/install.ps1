#Requires -Version 5.1
<#
  Night Runners MP - installer
  Finds the game, installs MelonLoader if needed, copies the mod, writes the config.
  Usage:  Install.bat            (interactive)
          install.ps1 -Silent    (no prompts; for scripted use)
          install.ps1 -GameDir "D:\itch\night-runners-private-alpha"
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$ExeName = 'NIGHT-RUNNERS PRIVATE ALPHA.exe'
$ProcName = 'NIGHT-RUNNERS PRIVATE ALPHA'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModFiles = @('Mods\NightRunnersMP.dll', 'UserLibs\LiteNetLib.dll')

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "    $m" -ForegroundColor Yellow }

function Test-GameDir($dir) { return ($dir -and (Test-Path (Join-Path $dir $ExeName))) }

function Find-GameDir {
    if (Test-GameDir $GameDir) { return (Resolve-Path $GameDir).Path }

    # 1. The installer may have been extracted straight into the game folder.
    if (Test-GameDir $Here) { return $Here }

    # 2. Typical itch.io library layouts on every drive.
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($d in Get-PSDrive -PSProvider FileSystem) {
        foreach ($sub in 'itch', 'itch\apps', 'Games\itch', 'Games') {
            $base = Join-Path $d.Root $sub
            if (Test-Path $base) {
                Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | ForEach-Object { $candidates.Add($_.FullName) }
            }
        }
    }
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'itch\apps\night-runners-private-alpha'))
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'itch\apps\night-runners'))
    foreach ($c in $candidates) { if (Test-GameDir $c) { return $c } }

    if ($Silent) { throw "Game folder not found. Run: install.ps1 -GameDir 'C:\path\to\the\game'" }

    Warn "Could not find the game automatically."
    Warn "Please pick the folder that contains '$ExeName'."
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Select your Night Runners game folder (it contains $ExeName)"
    $dlg.ShowNewFolderButton = $false
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Test-GameDir $dlg.SelectedPath)) {
        return $dlg.SelectedPath
    }
    throw "That folder does not contain '$ExeName'. Installation cancelled."
}

Write-Host ''
Write-Host '  NIGHT RUNNERS MP - installer' -ForegroundColor White
Write-Host '  ----------------------------' -ForegroundColor DarkGray

foreach ($f in $ModFiles) {
    if (-not (Test-Path (Join-Path $Here $f))) { throw "'$f' is missing next to the installer. Extract the whole zip first." }
}

Step 'Locating the game'
$game = Find-GameDir
Ok "Game folder: $game"

if (Get-Process -Name $ProcName -ErrorAction SilentlyContinue) {
    throw 'The game is running. Close it and run the installer again.'
}

Step 'Checking MelonLoader (mod loader)'
$mlOk = (Test-Path (Join-Path $game 'version.dll')) -and (Test-Path (Join-Path $game 'MelonLoader\net6\MelonLoader.dll'))
if ($mlOk) {
    Ok 'MelonLoader is already installed'
} else {
    Step 'Downloading MelonLoader from GitHub'
    $rel = Invoke-RestMethod 'https://api.github.com/repos/LavaGang/MelonLoader/releases/latest' -Headers @{ 'User-Agent' = 'NightRunnersMP-installer' }
    $asset = $rel.assets | Where-Object { $_.name -eq 'MelonLoader.x64.zip' } | Select-Object -First 1
    if (-not $asset) { throw 'Could not find MelonLoader.x64.zip in the latest MelonLoader release.' }
    $tmp = Join-Path $env:TEMP 'MelonLoader.x64.zip'
    Invoke-WebRequest $asset.browser_download_url -OutFile $tmp -UseBasicParsing
    Expand-Archive $tmp -DestinationPath $game -Force
    Remove-Item $tmp -Force
    Ok "MelonLoader $($rel.tag_name) installed"
}

Step 'Installing the mod files'
foreach ($f in $ModFiles) {
    $dst = Join-Path $game $f
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item (Join-Path $Here $f) $dst -Force
    Ok $f
}

Step 'Writing the config'
$cfgDir = Join-Path $game 'UserData'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$cfg = Join-Path $cfgDir 'MelonPreferences.cfg'
$hasSection = (Test-Path $cfg) -and (Select-String -Path $cfg -Pattern '^\[NightRunnersMP\]' -Quiet)
if ($hasSection) {
    Ok 'Config already has a [NightRunnersMP] section - kept as is'
} else {
    $name = 'Runner'
    $addr = '127.0.0.1'
    if (-not $Silent) {
        $n = Read-Host '    Your player name, shown above your car [Runner]'
        if ($n) { $name = $n.Trim() }
        $a = Read-Host "    Host address to connect to (leave empty if you don't know it yet)"
        if ($a) { $addr = $a.Trim() }
    }
    $template = Get-Content (Join-Path $Here 'MelonPreferences.template.cfg') -Raw
    $template = $template.Replace('__NAME__', $name).Replace('__ADDR__', $addr)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $prefix = ''
    if (Test-Path $cfg) { $prefix = "`r`n" }
    [IO.File]::AppendAllText($cfg, $prefix + $template, $utf8NoBom)
    Ok "Config written: $cfg"
}

Write-Host ''
Write-Host '  Installed!' -ForegroundColor Green
Write-Host ''
Write-Host '  In game:  F12 connect to the host   F11 host yourself   F8 disconnect' -ForegroundColor White
Write-Host '            F6 traffic on/off (host decides)   F7 hide/show the panel   F9 status' -ForegroundColor White
Write-Host "  Config:   $cfg" -ForegroundColor DarkGray
Write-Host '  The FIRST launch takes a few minutes while MelonLoader prepares files - wait for the main menu.' -ForegroundColor Yellow
Write-Host ''

if (-not $Silent) {
    $launch = Read-Host '  Launch the game now? [Y/n]'
    if ($launch -notmatch '^[nN]') {
        Start-Process (Join-Path $game $ExeName) -WorkingDirectory $game
    }
}
