<#
  Publishes the dedicated server as single-file, self-contained binaries (no .NET install needed on the VPS).
  Output: dist\server\linux-x64\nrmp-server  and  dist\server\win-x64\nrmp-server.exe
  Usage:  .\tools\publish-server.ps1            (both platforms)
          .\tools\publish-server.ps1 -Linux     (linux only)
#>
param([switch]$Linux, [switch]$Windows)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'server\NightRunnersMP.Server.csproj'
$rids = @()
if ($Linux) { $rids += 'linux-x64' }
if ($Windows) { $rids += 'win-x64' }
if ($rids.Count -eq 0) { $rids = @('linux-x64', 'win-x64') }

foreach ($rid in $rids) {
    $out = Join-Path $root "dist\server\$rid"
    dotnet publish $proj -c Release -r $rid -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    Get-ChildItem $out -File | Where-Object { $_.Name -like 'nrmp-server*' -and $_.Extension -ne '.pdb' } |
        ForEach-Object { Write-Host ("{0,-12} {1}  ({2:N1} MB)" -f $rid, $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
}
Copy-Item (Join-Path $root 'server\deploy\*') (Join-Path $root 'dist\server\') -Force
Write-Host "Deploy files (systemd unit, README) copied to dist\server\" -ForegroundColor DarkGray
