<#
.SYNOPSIS
  Decompile maps with s2c and compile the decompiles with Valve's resourcecompiler (CS2 closed).

.DESCRIPTION
  For each map: `s2c decompile-map` of game/csgo_addons/<addon>/maps/<map>.vpk into
  content/csgo_addons/<addon>/maps/s2c_dec_<map>.vmap (its prefab maps beside, under
  maps/prefabs/<map>/), then resourcecompiler on it (-world -fshallow by default, as the
  ze_hold_em_p check in docs/DECOMPILE.md), then MapDecompilerTests.ResourceCompilerOfTheDecompile_AgainstValve
  (DECOMPILE_RC) on the two packages. With -SameOptions the other package is resourcecompiler's
  build of the true source copied to s2c_src_<map>.vmap, under the same options. Logs go to -Out.

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
  [string]$Out = (Join-Path $env:TEMP "decompile_rc"),
  # Compare with resourcecompiler's build of the source under $RcArgs, not the package's own build.
  [switch]$SameOptions,
  # Skip the dotnet builds.
  [switch]$NoBuild,
  # Only compare: the packages of an earlier run, no decompile or resourcecompiler.
  [switch]$CompareOnly
)
$ErrorActionPreference = 'Continue'
if (-not $CompareOnly -and (Get-Process cs2 -ErrorAction SilentlyContinue)) { Write-Error "cs2.exe is running: resourcecompiler only with CS2 closed"; exit 1 }

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$rc = Join-Path $Cs2Root "game\bin\win64\resourcecompiler.exe"
$content = Join-Path $Cs2Root "content\csgo_addons\$Addon"
$game = Join-Path $Cs2Root "game\csgo_addons\$Addon"
New-Item -ItemType Directory -Force $Out | Out-Null
$env:CS2_DIR = $Cs2Root

if (-not $NoBuild) {
  dotnet build (Join-Path $repo "src\Source2.Compiler.Cli") -c Release -v q -nologo | Out-Null
  dotnet build (Join-Path $repo "tests\Source2.Compiler.Tests") -c Release -v q -nologo | Out-Null
}

foreach ($map in $Maps) {
  $dec = "s2c_dec_$map"
  $vmap = Join-Path $content "maps\$dec.vmap"
  Write-Host "== $map" -ForegroundColor Cyan
  $src = "s2c_src_$map"
  if (-not $CompareOnly) {
  dotnet run --project (Join-Path $repo "src\Source2.Compiler.Cli") -c Release --no-build -- `
    decompile-map (Join-Path $game "maps\$map.vpk") -o $vmap *> (Join-Path $Out "$map.decompile.log")
  if ($LASTEXITCODE -ne 0) { Write-Warning "$map`: decompile failed (see $Out\$map.decompile.log)"; continue }
  & $rc -nop4 -i $vmap $RcArgs.Split(' ') *> (Join-Path $Out "$map.rc.log")
  Write-Host "  rc exit $LASTEXITCODE"
  # The true source under the same options, renamed as the decompile is (s2c_src_<map>): the
  # package's own build may be a full one (light step keys) and carries the other map name.
  if ($SameOptions) {
    Copy-Item (Join-Path $content "maps\$map.vmap") (Join-Path $content "maps\$src.vmap") -Force
    & $rc -nop4 -i (Join-Path $content "maps\$src.vmap") $RcArgs.Split(' ') *> (Join-Path $Out "$map.rc_src.log")
    Write-Host "  rc of the source exit $LASTEXITCODE"
  }
  }
  $truth = if ($SameOptions) { Join-Path $game "maps\$src.vpk" } else { Join-Path $game "maps\$map.vpk" }
  # With -SameOptions the map's own package as well: the light step keys its build shipped.
  $env:DECOMPILE_RC = "$truth|$(Join-Path $game "maps\$dec.vpk")" + $(if ($SameOptions) { "|$(Join-Path $game "maps\$map.vpk")" } else { "" })
  dotnet test (Join-Path $repo "tests\Source2.Compiler.Tests") -c Release --no-build `
    --filter "FullyQualifiedName~ResourceCompilerOfTheDecompile_AgainstValve" --logger "console;verbosity=detailed" *> (Join-Path $Out "$map.compare.log")
  Select-String -Path (Join-Path $Out "$map.compare.log") -Pattern "difference\(s\)|entity models:|render models:" | ForEach-Object { "  " + $_.Line.Trim() }
}
Remove-Item Env:DECOMPILE_RC -ErrorAction SilentlyContinue
