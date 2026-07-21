; MixCut Windows Inno Setup script (v0.4.0+)
; 用法（在 Windows 构建机）：
;   "C:\Users\mlamp\AppData\Local\Programs\Inno Setup 6\iscc.exe" installer\MixCut.iss
; 输出：installer\out\MixCut-Setup-vX.Y.Z-win-x64.exe

#define MyAppName "MixCut"
#define MyAppVersion "0.14.0"
#define MyAppPublisher "MixCut"
#define MyAppURL "https://github.com/RoshanGH/mixcut-windows"
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

; per-user 安装：不需要管理员权限
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 仅 x64 Windows 10/11
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; 兜底 UI
DisableProgramGroupPage=yes
DisableDirPage=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; 输出
OutputDir=out
OutputBaseFilename=MixCut-Setup-v{#MyAppVersion}-win-x64

; v0.11.0 起：完整自包含包（内置 Whisper + 人声分离模型，装完即用永不下载），
; 通过自建服务器分发、无平台单文件大小限制，故不再分卷 → 单个 setup.exe（对齐 Mac 单 DMG）。
DiskSpanning=no
DiskSliceSize=94371840
SlicesPerDisk=1

; 语言
ShowLanguageDialog=no

; 关闭杀软可能误报的特性
SetupLogging=yes

; 默认使用 English；后续 v0.4.x 可补 ChineseSimplified.isl
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其它任务:"; Flags: unchecked

[Files]
; 内置模型（大文件，已是压缩过的二进制）：单列并用 nocompression —— 免去 lzma2 对 ~1.7GB
; 模型做无谓的慢压缩（装包从几十分钟降到几分钟），装到 {app}\bin 供 App 内置优先查找、永不下载。
Source: "..\publish\bin\ggml-large-v3-turbo.bin"; DestDir: "{app}\bin"; Flags: ignoreversion nocompression
Source: "..\publish\bin\ggml-htdemucs-4s.bin"; DestDir: "{app}\bin"; Flags: ignoreversion nocompression
; self-contained publish 其余全部内容 → {app}（排除上面已单列的两个模型，避免重复打入）
; recursesubdirs 会递归包含所有子目录：
;   bin/         FFmpeg / ffprobe / whisper-cli / 6 个 VC Runtime DLL / vcomp140 / concrt140 / 内置模型
;   Resources/   AI prompt 模板
;   *.dll *.exe  .NET 运行时（self-contained）+ MixCut.exe
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "bin\ggml-large-v3-turbo.bin,bin\ggml-htdemucs-4s.bin"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清掉用户数据可能有争议，这里不删 %APPDATA%\MixCut
; 让用户手动决定是否清理（README 会有说明）

[Code]
const
  PF_XMMI64_INSTRUCTIONS_AVAILABLE = 10;  // SSE2
  PF_SSE3_INSTRUCTIONS_AVAILABLE = 13;
  PF_XSAVE_ENABLED = 17;
  PF_AVX_INSTRUCTIONS_AVAILABLE = 17;       // 注意：Win API 用 17 表示 AVX
  PF_AVX2_INSTRUCTIONS_AVAILABLE = 40;

function IsProcessorFeaturePresent(Feature: DWORD): BOOL;
  external 'IsProcessorFeaturePresent@kernel32.dll stdcall';

function InitializeSetup(): Boolean;
var
  HasAvx2: Boolean;
  Resp: Integer;
begin
  Result := True;

  // 检测 AVX2 —— 内置 whisper-cli 强依赖
  HasAvx2 := IsProcessorFeaturePresent(PF_AVX2_INSTRUCTIONS_AVAILABLE);
  if not HasAvx2 then
  begin
    Resp := MsgBox(
      '检测到当前 CPU 不支持 AVX2 指令集。' + #13#10#13#10 +
      'MixCut 的语音识别（Whisper）功能需要 AVX2，否则会立即崩溃。' + #13#10 +
      '其它功能（导入视频 / AI 切分 / 方案生成 / 导出）仍可正常使用。' + #13#10#13#10 +
      '是否继续安装？',
      mbConfirmation, MB_YESNO);
    if Resp = IDNO then
    begin
      Result := False;
      exit;
    end;
  end;
end;
