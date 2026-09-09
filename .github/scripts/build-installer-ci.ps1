$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = $env:GITHUB_WORKSPACE
if (-not $Root) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }
Set-Location $Root

$Bin = Join-Path $Root 'src\MixCut\Resources\bin'
$Temp = Join-Path $env:RUNNER_TEMP 'mixcut-deps'
$Publish = Join-Path $Root 'publish'
$InstallerOut = Join-Path $Root 'installer\out-ci'
New-Item -ItemType Directory -Force -Path $Bin,$Temp | Out-Null

Write-Host '=== 1/6 Prepare FFmpeg ==='
$ffZip = Join-Path $Temp 'ffmpeg.zip'
$ffDir = Join-Path $Temp 'ffmpeg'
Invoke-WebRequest -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $ffZip
Expand-Archive $ffZip -DestinationPath $ffDir -Force
$ffmpeg = Get-ChildItem $ffDir -Recurse -Filter ffmpeg.exe | Select-Object -First 1
$ffprobe = Get-ChildItem $ffDir -Recurse -Filter ffprobe.exe | Select-Object -First 1
if (-not $ffmpeg -or -not $ffprobe) { throw 'FFmpeg package missing ffmpeg.exe/ffprobe.exe' }
Copy-Item $ffmpeg.FullName (Join-Path $Bin 'ffmpeg.exe') -Force
Copy-Item $ffprobe.FullName (Join-Path $Bin 'ffprobe.exe') -Force

Write-Host '=== 2/6 Prepare whisper.cpp runtime ==='
$headers = @{ 'User-Agent' = 'MixCut-CI' }
$rel = Invoke-RestMethod -Headers $headers -Uri 'https://api.github.com/repos/ggml-org/whisper.cpp/releases/latest'
$asset = $rel.assets | Where-Object { $_.name -eq 'whisper-bin-x64.zip' } | Select-Object -First 1
if (-not $asset) { throw 'Cannot find whisper-bin-x64.zip' }
$whZip = Join-Path $Temp 'whisper.zip'
$whDir = Join-Path $Temp 'whisper'
Invoke-WebRequest -Headers $headers -Uri $asset.browser_download_url -OutFile $whZip
Expand-Archive $whZip -DestinationPath $whDir -Force
$whCli = Get-ChildItem $whDir -Recurse -Filter whisper-cli.exe | Select-Object -First 1
if (-not $whCli) { throw 'whisper-cli.exe missing' }
Copy-Item $whCli.FullName (Join-Path $Bin 'whisper-cli.exe') -Force
$whDlls = Get-ChildItem $whDir -Recurse -Filter '*.dll'
if (-not $whDlls) { throw 'No whisper runtime DLLs found' }
foreach ($f in $whDlls) { Copy-Item $f.FullName (Join-Path $Bin $f.Name) -Force }

# VC++ runtime files needed on a clean Windows installation.
foreach ($dll in 'vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','concrt140.dll','vcomp140.dll') {
    $src = Join-Path $env:WINDIR "System32\$dll"
    if (-not (Test-Path $src)) {
        $found = Get-ChildItem $env:VCToolsRedistDir -Recurse -Filter $dll -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $src = $found.FullName }
    }
    if (-not $src -or -not (Test-Path $src)) { throw "Cannot locate VC runtime $dll" }
    Copy-Item $src (Join-Path $Bin $dll) -Force
}

# whisper writes normal backend diagnostics to stderr; use cmd so PowerShell does not treat them as terminating errors.
Push-Location $Bin
& cmd.exe /d /c 'whisper-cli.exe --help > whisper-smoke.txt 2>&1'
$whExit = $LASTEXITCODE
if ($whExit -ne 0) {
    Get-Content '.\whisper-smoke.txt' -ErrorAction SilentlyContinue
    Pop-Location
    throw "whisper-cli smoke test failed: $whExit"
}
Remove-Item '.\whisper-smoke.txt' -Force -ErrorAction SilentlyContinue
Pop-Location

Write-Host '=== 3/6 Build demucs.cpp ==='
$demucsRoot = Join-Path $env:RUNNER_TEMP 'demucs.cpp'
if (Test-Path $demucsRoot) { Remove-Item $demucsRoot -Recurse -Force }
git clone --recursive https://github.com/sevagh/demucs.cpp.git $demucsRoot
Set-Location $demucsRoot
git checkout f1206e9adeea103aef4a636b9e62297cf1f8e34e
git submodule update --init --recursive

$cmake = Get-Content 'CMakeLists.txt' -Raw
$cmake = $cmake -replace 'set\(CMAKE_CXX_FLAGS "-Wall -Wextra"\)\r?\n',''
$cmake = $cmake -replace 'set\(CMAKE_CXX_FLAGS_DEBUG "-g -DEIGEN_FAST_MATH=0 -O0"\)\r?\n',''
$cmake = $cmake -replace 'set\(CMAKE_CXX_FLAGS_RELEASE "-Ofast -march=native -fno-unsafe-math-optimizations -freciprocal-math -fno-signed-zeros"\)\r?\n',''
$cmake = $cmake -replace 'set\(CMAKE_CXX_FLAGS_RELEASE "\$\{CMAKE_CXX_FLAGS_RELEASE\} -DNDEBUG"\)\r?\n',''
$flags = @'
project(demucs.cpp)
if(MSVC)
  set(CMAKE_CXX_FLAGS "/W3 /EHsc /bigobj /utf-8")
  set(CMAKE_CXX_FLAGS_DEBUG "/Od /Zi /DEIGEN_FAST_MATH=0")
  set(CMAKE_CXX_FLAGS_RELEASE "/O2 /fp:fast /arch:AVX2 /DNDEBUG")
else()
  set(CMAKE_CXX_FLAGS "-Wall -Wextra")
  set(CMAKE_CXX_FLAGS_DEBUG "-g -DEIGEN_FAST_MATH=0 -O0")
  set(CMAKE_CXX_FLAGS_RELEASE "-O3 -march=x86-64-v3 -DNDEBUG")
endif()
'@
$cmake = $cmake -replace 'project\(demucs\.cpp\)', $flags.Trim()
Set-Content 'CMakeLists.txt' $cmake -Encoding UTF8

$cli = Get-Content 'cli-apps\demucs.cpp' -Raw
$cli = $cli.Replace('write_audio_file(target_waveform, p_target);','write_audio_file(target_waveform, p_target.string());')
Set-Content 'cli-apps\demucs.cpp' $cli -Encoding UTF8

New-Item -ItemType Directory -Force -Path build | Out-Null
Set-Location build
cmake -G Ninja -DCMAKE_BUILD_TYPE=Release -DUSE_OPENBLAS=OFF -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL ..
if ($LASTEXITCODE -ne 0) { throw "demucs cmake configure failed: $LASTEXITCODE" }
cmake --build . --target demucs.cpp.main --config Release
if ($LASTEXITCODE -ne 0) { throw "demucs build failed: $LASTEXITCODE" }
$demucs = Get-ChildItem . -Recurse -Filter 'demucs.cpp.main.exe' | Select-Object -First 1
if (-not $demucs) { throw 'demucs.cpp.main.exe was not produced' }
Copy-Item $demucs.FullName (Join-Path $Bin 'demucs.exe') -Force
Set-Location $Root

Write-Host '=== 4/6 Verify native components and publish WPF app ==='
$required = @(
    'ffmpeg.exe','ffprobe.exe','whisper-cli.exe','demucs.exe',
    'vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','concrt140.dll','vcomp140.dll'
)
$missing = $required | Where-Object { -not (Test-Path (Join-Path $Bin $_)) }
if ($missing) { throw ('Missing native components: ' + ($missing -join ', ')) }
Get-ChildItem $Bin | Sort-Object Name | Format-Table Name,Length -AutoSize

Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item 'src\MixCut\obj\Release' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'src\MixCut\bin\Release' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $Publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet restore 'src\MixCut\MixCut.csproj' --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed: $LASTEXITCODE" }
dotnet publish 'src\MixCut\MixCut.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -nodeReuse:false -p:UseSharedCompilation=false -o publish -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
if (-not (Test-Path 'publish\MixCut.exe')) { throw 'publish\MixCut.exe missing' }
if (-not (Test-Path 'publish\bin\ffmpeg.exe')) { throw 'publish\bin\ffmpeg.exe missing' }
if (-not (Test-Path 'publish\bin\demucs.exe')) { throw 'publish\bin\demucs.exe missing' }

Write-Host '=== 5/6 Build Inno Setup installer ==='
$iss = @'
#define MyAppName "MixCut"
#define MyAppVersion "0.15.0"
#define MyAppPublisher "MixCut"
#define MyAppURL "https://github.com/jinpenglu74/mixcut-windows"
#define MyAppExeName "MixCut.exe"

[Setup]
AppId={{B7C8F2E0-5B3A-4D1A-9E4F-3C2A1B0E5D6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
DisableDirPage=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=out-ci
OutputBaseFilename=MixCut-Setup-V1.0.0-SourceBaseline-win-x64
DiskSpanning=no
ShowLanguageDialog=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其它任务:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  PF_AVX2_INSTRUCTIONS_AVAILABLE = 40;
function IsProcessorFeaturePresent(Feature: DWORD): BOOL;
  external 'IsProcessorFeaturePresent@kernel32.dll stdcall';
function InitializeSetup(): Boolean;
var HasAvx2: Boolean; Resp: Integer;
begin
  Result := True;
  HasAvx2 := IsProcessorFeaturePresent(PF_AVX2_INSTRUCTIONS_AVAILABLE);
  if not HasAvx2 then begin
    Resp := MsgBox('检测到当前 CPU 不支持 AVX2 指令集。' + #13#10#13#10 +
      'MixCut 的语音识别（Whisper）功能需要 AVX2，否则会立即崩溃。' + #13#10 +
      '其它功能（导入视频 / AI 切分 / 方案生成 / 导出）仍可正常使用。' + #13#10#13#10 +
      '是否继续安装？', mbConfirmation, MB_YESNO);
    if Resp = IDNO then begin Result := False; exit; end;
  end;
end;
'@
$issPath = Join-Path $Root 'installer\MixCut-CI.iss'
Set-Content $issPath $iss -Encoding UTF8
Remove-Item $InstallerOut -Recurse -Force -ErrorAction SilentlyContinue
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' $issPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed: $LASTEXITCODE" }
$exe = Get-ChildItem $InstallerOut -Filter '*.exe' | Select-Object -First 1
if (-not $exe) { throw 'Installer EXE was not produced' }

Write-Host '=== 6/6 Installer ready ==='
Write-Host "Installer: $($exe.FullName)"
Write-Host "Size MB: $([math]::Round($exe.Length / 1MB, 2))"
