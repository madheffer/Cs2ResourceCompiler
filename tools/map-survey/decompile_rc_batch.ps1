<#
.SYNOPSIS
  Decompile maps with s2c and compile the decompiles with Valve's resourcecompiler (CS2 closed).

.DESCRIPTION
  For each map: `s2c decompile-map` of game/csgo_addons/<addon>/maps/<map>.vpk into
  content/csgo_addons/<addon>/maps/s2c_dec_<map>.vmap (its prefab maps beside, under
  maps/prefabs/<map>/), then resourcecompiler on it (-world -fshallow by default, as the
  ze_hold_em_p check in docs/DECOMPILE.md), then MapDecompilerTests.ResourceCompilerOfTheDecompile_AgainstValve
  (DECOMPILE_RC) on the two packages. Logs go to -Out.

  resourcecompiler needs CS2 closed (memory: cs2-open-work), so the script stops when cs2.exe runs.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\map-survey\decompile_rc_batch.ps1 -Maps s2c_pinst7,s2c_prefabprobe8
#>
[CmdletBinding()]
param(
  [string[]]$Maps = @("s2c_pinst7", "s2c_prefabprobe8", "s2c_prefabprobe4", "atixref", "s2c_prefabinst"),
  [string]$Cs2Root = "D:\Steam\steamapps\common\Counter-Strike Global Offensive",
  [string]$Addon = "s2c_rc_probe",
  [string]$RcArgs = "-world -fshallow",
  [string]$Out = (Join-Path $env:TEMP "decompile_rc")
)
$ErrorActionPreference = 'Stop'
if (Get-Process cs2 -ErrorAction SilentlyContinue) { throw "cs2.exe is running: resourcecompiler only with CS2 closed" }

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$rc = Join-Path $Cs2Root "game\bin\win64\resourcecompiler.exe"
$content = Join-Path $Cs2Root "content\csgo_addons\$Addon"
$game = Join-Path $Cs2Root "game\csgo_addons\$Addon"
New-Item -ItemType Directory -Force $Out | Out-Null
$env:CS2_DIR = $Cs2Root

dotnet build (Join-Path $repo "src\Source2.Compiler.Cli") -c Release -v q -nologo | Out-Null
dotnet build (Join-Path $repo "tests\Source2.Compiler.Tests") -c Release -v q -nologo | Out-Null

foreach ($map in $Maps) {
  $dec = "s2c_dec_$map"
  $vmap = Join-Path $content "maps\$dec.vmap"
  Write-Host "== $map" -ForegroundColor Cyan
  dotnet run --project (Join-Path $repo "src\Source2.Compiler.Cli") -c Release --no-build -- `
    decompile-map (Join-Path $game "maps\$map.vpk") -o $vmap *> (Join-Path $Out "$map.decompile.log")
  if ($LASTEXITCODE -ne 0) { Write-Warning "$map`: decompile failed (see $Out\$map.decompile.log)"; continue }
  & $rc -nop4 -f -i $vmap $RcArgs.Split(' ') *> (Join-Path $Out "$map.rc.log")
  Write-Host "  rc exit $LASTEXITCODE"
  $env:DECOMPILE_RC = "$(Join-Path $game "maps\$map.vpk")|$(Join-Path $game "maps\$dec.vpk")"
  dotnet test (Join-Path $repo "tests\Source2.Compiler.Tests") -c Release --no-build `
    --filter "FullyQualifiedName~ResourceCompilerOfTheDecompile_AgainstValve" --logger "console;verbosity=detailed" *> (Join-Path $Out "$map.compare.log")
  Select-String -Path (Join-Path $Out "$map.compare.log") -Pattern "difference\(s\)|entity models:" | ForEach-Object { "  " + $_.Line.Trim() }
}
Remove-Item Env:DECOMPILE_RC -ErrorAction SilentlyContinue
