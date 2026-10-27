# MoonSweepersMcp v0.6.13 one-shot deploy (run on the game PC when the operator gives the go).
# v0.6.13 = the anti-spin release: pure-pursuit steering, curvature/arrival
# speed profile, distance-closure-only progress, anti-orbit winding watchdog
# + RECOVER mode (reverse counter-lock, early exit at |ang| < 45 deg),
# waypoint pass detection. Staged DLL MD5: 7d6b317c4a2e6dcecb9a6adab947ce15.
# Discipline: kill game -> copy staged DLL -> MD5 verify -> relaunch -> log check.
# AFTER relaunch the operator must rejoin the lobby and board the truck by hand.
$ErrorActionPreference = "Stop"
$game = "C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers"
$src  = Join-Path $env:USERPROFILE "Desktop\MoonSweepers-Rebuild\mcp-plugin\dist\MoonSweepersMcp-v0.6.13.dll"
$dst  = "$game\BepInEx\plugins\MoonSweepersMcp.dll"
taskkill /im MoonSweepers.exe /f 2>$null
Start-Sleep -Seconds 3
Copy-Item $src $dst -Force
$a = (Get-FileHash $src -Algorithm MD5).Hash
$b = (Get-FileHash $dst -Algorithm MD5).Hash
if ($a -ne $b) { throw "MD5 mismatch: $a vs $b" }
Write-Output "DLL verified: $b"
schtasks /change /tn MoonMods-Launch /enable | Out-Null
schtasks /run /tn MoonMods-Launch | Out-Null
Start-Sleep -Seconds 45
Select-String -Path "$game\BepInEx\LogOutput.log" -Pattern "MoonSweepers MCP|MCP listening|bot input hook" | Select-Object -Last 8 | ForEach-Object { $_.Line }
