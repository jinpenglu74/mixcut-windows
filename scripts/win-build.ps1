<#
.SYNOPSIS
  MixCut reliable local build / publish -- works around the WPF MarkupCompile hang.

.DESCRIPTION
  On this machine the default dotnet build NON-DETERMINISTICALLY hangs at WPF's
  GenerateTemporaryTargetAssembly (the MarkupCompile temp-assembly step): the main
  dotnet process sits at ~3s CPU forever, MarkupCompile.cache is written then frozen.
  Verified 2026-07-01: rebooting to clear the stale June zombie dotnet process did
  NOT fix it -- so the root cause is not the zombie, it is a flaky interaction between
  MSBuild node reuse / build server and the WPF temp-project build on this box.
  Reliable avoidance = disable MSBuild server + disable node reuse + always clean obj
  + auto-retry once if it still hangs.

  Usage:
    scripts\win-build.ps1            # build to bin\Release
    scripts\win-build.ps1 -Publish   # self-contained publish to publish\ (release / install)

  See CLAUDE.md and memory/wpf-build-hang-fix.md for the full story (Chinese).
#>
param(
  [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$Dotnet = "$env:USERPROFILE\dotnet\dotnet.exe"
$Root   = Split-Path $PSScriptRoot -Parent
$Proj   = Join-Path $Root 'src\MixCut\MixCut.csproj'
$PubDir = Join-Path $Root 'publish'
$ObjRel = Join-Path $Root 'src\MixCut\obj\Release'
$BinRel = Join-Path $Root 'src\MixCut\bin\Release'

# Key switches (env + CLI both needed; env var alone proved insufficient in testing --
# the -nodeReuse:false CLI flag is the load-bearing one).
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE       = '1'

$commonArgs = @(
  '-c','Release','-nologo','-v','minimal',
  '-nodeReuse:false','-p:UseSharedCompilation=false'
)
if ($Publish) {
  $verb = 'publish'
  $dotnetArgs = @($verb, $Proj) + $commonArgs + @(
    '-r','win-x64','--self-contained','true',
    '-p:PublishSingleFile=false','-p:PublishReadyToRun=true','-p:IncludeNativeLibrariesForSelfExtract=true',
    '-o', $PubDir
  )
} else {
  $verb = 'build'
  $dotnetArgs = @($verb, $Proj) + $commonArgs
}

$LogPath = Join-Path $Root ('build_out.txt')

function Invoke-Attempt {
  param([int]$TimeoutSec = 360)
  # Kill leftover dotnet (lock / poisoned node) and any running app first.
  Get-Process MixCut -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
  Get-ChildItem (Split-Path $Proj) -Filter '*wpftmp*' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $ObjRel, $BinRel -ErrorAction SilentlyContinue

  Write-Host "[win-build] dotnet $verb (timeout ${TimeoutSec}s)..." -ForegroundColor Cyan
  # Run in a child job using the call-operator + pipe form (the invocation that builds
  # reliably here; Start-Process -NoNewWindow can itself trigger the temp-project hang).
  # Wait-Job -Timeout gives dependable hang detection (Process.WaitForExit was unreliable).
  $job = Start-Job -ScriptBlock {
    param($dn, $a, $log)
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $env:MSBUILDDISABLENODEREUSE       = '1'
    & $dn @a 2>&1 | Out-File -Encoding utf8 $log
    $LASTEXITCODE
  } -ArgumentList $Dotnet, $dotnetArgs, $LogPath

  if (Wait-Job $job -Timeout $TimeoutSec) {
    $code = @(Receive-Job $job) | Select-Object -Last 1
    Remove-Job $job -Force
    return [int]$code
  }
  Write-Host "[win-build] timeout - likely hang, killing and will retry" -ForegroundColor Yellow
  Stop-Job $job -ErrorAction SilentlyContinue
  Remove-Job $job -Force -ErrorAction SilentlyContinue
  Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  return $null   # null = hang
}

$exit = Invoke-Attempt
if ($null -eq $exit) {
  Write-Host "[win-build] first attempt hung, retrying once..." -ForegroundColor Yellow
  $exit = Invoke-Attempt
}

if ($null -eq $exit) {
  Write-Host "[win-build] hung twice - investigate manually (see memory/wpf-build-hang-fix.md)" -ForegroundColor Red
  exit 1
}
if ($exit -ne 0) {
  Write-Host "[win-build] $verb FAILED exit=$exit" -ForegroundColor Red
  exit $exit
}
$dest = if ($Publish) { " -> $PubDir" } else { '' }
Write-Host "[win-build] $verb OK$dest" -ForegroundColor Green
