<#
  Builds the mod and assembles dist\NightRunnersMP-v<version>.zip (installer + DLLs, no game files).
  Usage:  .\tools\release.ps1            build + package
          .\tools\release.ps1 -NoBuild   package the existing Release build
  Publish afterwards with GitHub CLI:
          gh release create v<version> dist\NightRunnersMP-v<version>.zip --title "Night Runners MP v<version>"
#>
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\NightRunnersMP.csproj'
$xml = [xml](Get-Content $csproj)
$version = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'No <Version> in the csproj' }

# The in-game update check compares MelonInfo's version with the GitHub tag, so both strings must agree.
$core = Get-Content (Join-Path $root 'src\Core.cs') -Raw
if ($core -notmatch 'MelonInfo\([^,]+,\s*"[^"]+",\s*"([^"]+)"') { throw 'Could not find the MelonInfo version in Core.cs' }
if ($Matches[1] -ne $version) { throw "Version mismatch: csproj <Version>$version</Version> vs MelonInfo `"$($Matches[1])`" in Core.cs" }

if (-not $NoBuild) {
    dotnet build $csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}

$pkg = Join-Path $root 'dist\pkg'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force "$pkg\Mods", "$pkg\UserLibs" | Out-Null
Copy-Item (Join-Path $root 'src\bin\Release\NightRunnersMP.dll') "$pkg\Mods\"
Copy-Item (Join-Path $root 'src\bin\Release\LiteNetLib.dll') "$pkg\UserLibs\"
Copy-Item (Join-Path $root 'installer\*') $pkg -Recurse -Force

$zip = Join-Path $root "dist\NightRunnersMP-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$pkg\*" -DestinationPath $zip

Write-Host "Packaged $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)" -ForegroundColor Green
Get-ChildItem $pkg -Recurse -File | ForEach-Object { '  ' + $_.FullName.Substring($pkg.Length + 1) }
$srv = Join-Path $root 'dist\server'
Write-Host "Publish (mod + server binaries; run tools\publish-server.ps1 first):" -ForegroundColor DarkGray
Write-Host "  gh release create v$version `"$zip`" `"$srv\linux-x64\nrmp-server#nrmp-server (Linux x64)`" `"$srv\win-x64\nrmp-server.exe#nrmp-server.exe (Windows x64)`" `"$srv\nrmp-server.service`" --title `"Night Runners MP v$version`" --generate-notes" -ForegroundColor DarkGray
