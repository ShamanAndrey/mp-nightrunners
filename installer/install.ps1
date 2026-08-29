#Requires -Version 5.1
<#
  Night Runners MP - installer
  Finds the game(s) - the itch private alpha and/or the Steam Prologue - installs MelonLoader if
  needed, copies the mod, writes the config.
  Usage:  Install.bat                      (interactive)
          install.ps1 -Silent              (no prompts; installs to every game found)
          install.ps1 -GameDir "D:\path"   (a specific install)
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Games = @(
    @{ Key = 'alpha';    Name = 'Night Runners (private alpha, itch)'; Exe = 'NIGHT-RUNNERS PRIVATE ALPHA.exe' },
    @{ Key = 'prologue'; Name = 'Night Runners Prologue (Steam)';      Exe = 'NIGHT-RUNNERS PROLOGUE.exe' }
)
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModFiles = @('Mods\NightRunnersMP.dll', 'UserLibs\LiteNetLib.dll')

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
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
        try {
            $k = Get-ItemProperty $reg -ErrorAction Stop
            foreach ($v in $k.SteamPath, $k.InstallPath) { if ($v) { $libs.Add(($v -replace '/', '\')) } }
        } catch { }
    }
    foreach ($d in Get-PSDrive -PSProvider FileSystem) {
        foreach ($sub in 'SteamLibrary', 'Steam', 'Program Files (x86)\Steam', 'Games\Steam') { $libs.Add((Join-Path $d.Root $sub)) }
    }
    # Every library knows the others via libraryfolders.vdf
    $extra = New-Object System.Collections.Generic.List[string]
    foreach ($l in $libs) {
        $vdf = Join-Path $l 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $extra.Add(($m.Groups[1].Value -replace '\\\\', '\')) }
        }
    }
    return ($libs + $extra) | Where-Object { $_ } | Select-Object -Unique
}

function Find-Installs {
    $found = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $add = { param($hit) if ($hit -and -not $seen.ContainsKey($hit.Dir)) { $seen[$hit.Dir] = $true; $found.Add($hit) } }

    & $add (Get-GameIn $GameDir)
    if ($GameDir -and $found.Count -eq 0) { throw "No Night Runners executable in '$GameDir'." }
    if ($GameDir) { return $found.ToArray() }

    & $add (Get-GameIn $Here)          # zip extracted straight into a game folder

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

function Install-To($hit) {
    $game = $hit.Dir
    $exe = $hit.Game.Exe
    Write-Host ''
    Step "$($hit.Game.Name)"
    Ok "Folder: $game"

    $procName = [IO.Path]::GetFileNameWithoutExtension($exe)
    if (Get-Process -Name $procName -ErrorAction SilentlyContinue) { throw "The game is running ($exe). Close it and run the installer again." }

    $mlOk = (Test-Path (Join-Path $game 'version.dll')) -and (Test-Path (Join-Path $game 'MelonLoader\net6\MelonLoader.dll'))
    if ($mlOk) {
        Ok 'MelonLoader is already installed'
    } else {
        Step 'Downloading MelonLoader (mod loader) from GitHub'
        $rel = Invoke-RestMethod 'https://api.github.com/repos/LavaGang/MelonLoader/releases/latest' -Headers @{ 'User-Agent' = 'NightRunnersMP-installer' }
        $asset = $rel.assets | Where-Object { $_.name -eq 'MelonLoader.x64.zip' } | Select-Object -First 1
        if (-not $asset) { throw 'Could not find MelonLoader.x64.zip in the latest MelonLoader release.' }
        $tmp = Join-Path $env:TEMP 'MelonLoader.x64.zip'
        Invoke-WebRequest $asset.browser_download_url -OutFile $tmp -UseBasicParsing
        Expand-Archive $tmp -DestinationPath $game -Force
        Remove-Item $tmp -Force
        Ok "MelonLoader $($rel.tag_name) installed"
    }

    foreach ($f in $ModFiles) {
        $dst = Join-Path $game $f
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item (Join-Path $Here $f) $dst -Force
        Ok "installed $f"
    }

    $cfgDir = Join-Path $game 'UserData'
    New-Item -ItemType Directory -Force $cfgDir | Out-Null
    $cfg = Join-Path $cfgDir 'MelonPreferences.cfg'
    $hasSection = (Test-Path $cfg) -and (Select-String -Path $cfg -Pattern '^\[NightRunnersMP\]' -Quiet)
    if ($hasSection) {
        Ok 'config already has a [NightRunnersMP] section - kept as is'
    } else {
        $template = Get-Content (Join-Path $Here 'MelonPreferences.template.cfg') -Raw
        $template = $template.Replace('__NAME__', $script:PlayerName).Replace('__ADDR__', $script:HostAddr)
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        $prefix = ''
        if (Test-Path $cfg) { $prefix = "`r`n" }
        [IO.File]::AppendAllText($cfg, $prefix + $template, $utf8NoBom)
        Ok "config written: $cfg"
    }
    return $game
}

# ---------------------------------------------------------------------------------------------

Write-Host ''
Write-Host '  NIGHT RUNNERS MP - installer' -ForegroundColor White
Write-Host '  ----------------------------' -ForegroundColor DarkGray

foreach ($f in $ModFiles) {
    if (-not (Test-Path (Join-Path $Here $f))) { throw "'$f' is missing next to the installer. Extract the whole zip first." }
}

Step 'Looking for Night Runners installs (itch + Steam)'
$installs = @(Find-Installs)
if ($installs.Count -eq 0) {
    if ($Silent) { throw "No game folder found. Run: install.ps1 -GameDir 'C:\path\to\the\game'" }
    Warn 'Could not find the game automatically. Pick the folder that contains the game .exe'
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select your Night Runners game folder (contains NIGHT-RUNNERS ... .exe)'
    $dlg.ShowNewFolderButton = $false
    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw 'Installation cancelled.' }
    $hit = Get-GameIn $dlg.SelectedPath
    if (-not $hit) { throw 'That folder does not contain a Night Runners executable.' }
    $installs = @($hit)
}
foreach ($i in $installs) { Ok "found: $($i.Game.Name)  ->  $($i.Dir)" }

if ($installs.Count -gt 1 -and -not $Silent) {
    Write-Host ''
    for ($n = 0; $n -lt $installs.Count; $n++) { Write-Host "    [$($n + 1)] $($installs[$n].Game.Name)" }
    Write-Host "    [A] all of them"
    $pick = Read-Host '    Install to which? [A]'
    if ($pick -match '^\d+$' -and [int]$pick -ge 1 -and [int]$pick -le $installs.Count) { $installs = @($installs[[int]$pick - 1]) }
}

$script:PlayerName = 'Runner'
$script:HostAddr = '127.0.0.1'
if (-not $Silent) {
    $n = Read-Host '    Your player name, shown above your car [Runner]'
    if ($n) { $script:PlayerName = $n.Trim() }
    $a = Read-Host '    Host address to connect to (optional - you can also type it in-game with F12)'
    if ($a) { $script:HostAddr = $a.Trim() }
}

$done = @()
foreach ($i in $installs) { $done += Install-To $i }

Write-Host ''
Write-Host '  Installed!' -ForegroundColor Green
Write-Host ''
Write-Host '  In game:  F12 type the host address + Enter   F11 host yourself   F8 disconnect' -ForegroundColor White
Write-Host '            Enter chat   F5 collisions / F6 traffic (host decides)   F7 hide/show the panel   F9 status' -ForegroundColor White
Write-Host '  The FIRST launch takes a few minutes while MelonLoader prepares files - wait for the main menu.' -ForegroundColor Yellow
Write-Host ''

if (-not $Silent -and $installs.Count -eq 1) {
    $launch = Read-Host '  Launch the game now? [Y/n]'
    if ($launch -notmatch '^[nN]') {
        Start-Process (Join-Path $installs[0].Dir $installs[0].Game.Exe) -WorkingDirectory $installs[0].Dir
    }
}
