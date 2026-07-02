# MixCut Windows · AI 广告视频混剪工具

> Windows 桌面版 · C# + WPF + .NET 8 · 本地 AI 驱动 · 对齐 macOS 原版

MixCut 面向广告投放团队：**导入广告素材 → AI 按语义切分分镜 → 智能排列组合生成多条差异化广告 → AI 配音 + 烧录字幕 → 一键批量出片**。视频内容与 API Key 全部本地处理，AI 调用只发送结构化文本、不上传视频。

本项目是 [macOS 原生版 MixCut](https://github.com/RoshanGH/mixed_cut) 的 Windows 移植版，功能完整对齐。

最新版本：**v0.9.0**

---

## 下载与安装

| 渠道 | 链接 |
|------|------|
| GitHub（海外） | [github.com/RoshanGH/mixcut-windows/releases](https://github.com/RoshanGH/mixcut-windows/releases) |
| Gitee（国内推荐） | [gitee.com/jinxiushanhehao/mixcut-windows/releases](https://gitee.com/jinxiushanhehao/mixcut-windows/releases) |
| macOS 版 | [github.com/RoshanGH/mixed_cut/releases](https://github.com/RoshanGH/mixed_cut/releases) |

**安装包（推荐）** —— 安装包分成 3 个文件（受单文件大小限制拆分）：

1. 下载 `MixCut-Setup-vX.Y.Z-win-x64.exe`、`...-1.bin`、`...-2.bin` **三个文件**
2. 把三个文件放到**同一个目录**
3. 双击 `MixCut-Setup-vX.Y.Z-win-x64.exe` 安装

**绿色版** —— 下载 `MixCut-vX.Y.Z-win-x64.zip` 解压，双击 `MixCut.exe` 即用（GitHub 提供）。

> **装上即跑**：安装包已自带 .NET 8 运行时、VC++ 运行库、FFmpeg / Whisper 等全部依赖。**无需**预装 .NET Desktop Runtime、VC++ Redistributable 或任何视频编解码器。首次启动若被 SmartScreen 拦截，点「更多信息 → 仍要运行」即可。

---

## 功能亮点

### 🤖 AI 智能流水线
- **AI 语义切分** —— 自动识别 11 种语义类型（噱头引入 / 痛点 / 产品方案 / 效果展示 / 信任背书 / 价格对比 / 活动福利 / 行动号召 / 产品定位 / 产品使用教育 / 过渡）
- **混合语音识别** —— 内置 whisper.cpp（`ggml-large-v3-turbo`，离线切分镜时间戳）+ 阿里 Paraformer 逐分镜精识别（短音频更准、自带标点），导入时自动完成
- **AI 混剪方案** —— 两步生成：① AI 出策略（风格 / 受众 / 叙事结构）② AI 按策略排列分镜组合，一键生成多条差异化广告
- **多 AI 提供商** —— 千问 / MiniMax / DeepSeek / Claude 原生 / 国内转发网关（Claude·Gemini·OpenAI）/ 自定义 OpenAI 兼容

### 🎙️ AI 配音（v0.5+）
- **台词裂变** —— 给每个（非「保留原声」）分镜 AI 改写出多套差异化台词（默认 2 套，可调 1–5）
- **逐分镜声音克隆** —— 用视频里的原声克隆音色（阿里百炼 qwen3-tts-vc）合成配音；**每个分镜单独克隆**，谁在说话就克隆谁的音色（广告常「开头换人带货」，逐分镜克隆避免串音串性别）
- **人声 / 背景乐分离** —— 内置 demucs.cpp 分离人声与 BGM；配音成片自动保留原视频的背景音乐、只替换人声
- **配音组合导出** —— 每个分镜可有多套配音变体，按笛卡尔积批量出片，一次产出大量差异化成片
- **应用内试听 + 导出配音** —— 生成的配音可在应用内直接试听，也可单独导出音频文件

### 🎬 烧录字幕（v0.9.0）
- **字号可调 + 所见即所得** —— 分镜卡片上直接拖滑条调字幕大小（占画面宽 3%–8.5%，随分辨率自适应，小分辨率素材不再字幕盖脸）；画面上实时预览新字幕的大小与位置，**导出就是预览的样子**
- **字幕处理三选一** —— 直接烧录 / 模糊虚化 / 纯色遮挡，遮挡框可拖拽定位、一键应用到同视频所有分镜
- **贴字圆角底衬 + 标点转空格** —— 底衬贴合文字（不再一整条黑带）；标点自动转空格、但保留价格 / 时间里的数字（「9.9 元」「8:00」不会被拆坏）

### 📦 视频处理与导出
- **自带 FFmpeg 解码栈** —— 预览、缩略图、导出**同源**走内置 FFmpeg，HEVC / iPhone「高效」格式无需系统编解码器也能预览，从根上杜绝「能预览不能导 / 能导不能预览」
- **硬件加速** —— 启动探测编码器，优先 NVIDIA NVENC / Intel QSV / AMD AMF，CPU 兜底；4K 导出软/硬解智能选择
- **批量导出（含配音变体）** —— 多选分镜批量出片，除原片外还一并导出每个分镜的全部配音变体（原版 + 各配音版一次出齐）
- **第一帧不黑屏** —— 帧精确切片（trim + setpts），封面/首帧立即有画面
- **视频全局共享** —— 同一视频（SHA-256 哈希）跨项目共享，不重复分析

### ✨ 体验
- **剪映式逐帧预览** —— 点击缩略图即时播放；调分镜起止边界（±1 帧）时画面实时跟到那一帧
- **MVVM 数据驱动 + 真虚拟化** —— 1000+ 分镜也能瞬时加载，滚动不卡顿
- **可点击撤销** —— 删除类操作弹出「撤销」按钮，与 Ctrl+Z 同一条恢复路径
- **人话报错** —— AI 免费额度用完、欠费、未开通模型权限、限流等分别给准确中文提示 + 下一步指引，绝不把英文报错码甩给用户
- **应用启动恢复上次项目** + **Toast / Skeleton / InlineBanner** 完整反馈体系

### 🛡️ 容错与稳定性
- **崩溃捕获** —— 三层 unhandled exception hook（Dispatcher / AppDomain / TaskScheduler），窗口不神秘消失
- **进程不孤儿** —— 所有 whisper / ffmpeg / demucs 子进程挂 Job Object，主进程退出一起死
- **状态自愈** —— 强退后下次启动自动重置卡在「分析中」「生成中」的状态
- **断点续传** —— 模型下载支持 HTTP Range，断网续传；依赖走国内可访问镜像源
- **AI JSON 多层防御** —— 解析失败自动重试 + 落盘原始响应供排查

---

## 系统要求

- **Windows 10（1809 / 17763 及以上）或 Windows 11**，x64
- 首次使用语音识别时下载 Whisper 模型约 1.5 GB（仅一次）
- AI 配音 / AI 切分需配置 API Key 并联网
- 可选：NVIDIA 显卡（NVENC）/ Intel 核显（QSV）/ AMD 显卡（AMF）加速导出
- **无需**预装 .NET Runtime、VC++ Redistributable 或系统编解码器（安装包已自带）

---

## 配置 AI Key

在 **设置 → AI 配置** 填入 API Key：

| 提供商 | 说明 |
|--------|------|
| **千问 (Qwen)** | 阿里通义千问；**AI 配音（声音克隆 + 合成）目前依赖千问 / 阿里百炼** |
| **MiniMax** | MiniMax M2 系列 |
| **DeepSeek** | DeepSeek-V4 系列 |
| **Claude** | Anthropic 官方 API |
| **国内转发网关** | 转发到 Claude / Gemini / OpenAI 三平台之一 |
| **自定义** | 任意 OpenAI 兼容 API（自填地址 + 模型名） |

> AI 切分 / 混剪方案可用上述任一提供商；**AI 配音**需要千问（阿里百炼）并开通 `qwen-voice-enrollment`（声音克隆）与 `qwen3-tts-vc`（音色合成）。免费额度用完时应用会提示去控制台开通付费或换用仍有额度的模型。

---

## 开发者构建

代码在 Mac 上编写，构建 / 运行 / 测试在 Windows 机器上进行（详见 [CLAUDE.md](./CLAUDE.md)）。

```powershell
# 1. 克隆
git clone https://github.com/RoshanGH/mixcut-windows.git
cd mixcut-windows

# 2. 把 Windows 版内置二进制放入 src/MixCut/Resources/bin/
#    ffmpeg.exe / ffprobe.exe / whisper-cli.exe / demucs.exe + 依赖 DLL
#    + 6 个 VC Runtime DLL + vcomp140.dll（详见 CLAUDE.md）

# 3. 编译（本仓库有 WPF 编译偶发卡死的规避脚本，优先用它）
powershell -ExecutionPolicy Bypass -File scripts\win-build.ps1            # build
powershell -ExecutionPolicy Bypass -File scripts\win-build.ps1 -Publish   # self-contained publish

# 或直接跑（语法/类型检查也可在 Mac 上 dotnet build -p:EnableWindowsTargeting=true）
dotnet run --project src/MixCut/MixCut.csproj

# 打安装包（需本机装 Inno Setup 6）
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

技术架构、跨机器开发流程、商业 toC 标准、自我验证铁律、兼容性总纲详见 [CLAUDE.md](./CLAUDE.md)。

---

## 故障排查

### 首次启动

| 症状 | 原因 | 解决 |
|---|---|---|
| 「Windows 已保护你的电脑」蓝色弹窗 | SmartScreen 拦截未签名应用 | 点「**更多信息**」→「**仍要运行**」，以后不再弹 |
| 双击装好的 MixCut 没反应 / 一闪而过 | 启动期崩溃 | 查日志 `%APPDATA%\MixCut\logs\mixcut-YYYYMMDD.log`，搜 `[FTL]` / `[ERR]` |
| 杀软（360 / 腾讯管家 / Defender）拦截 `whisper-cli.exe` / `ffmpeg.exe` / `demucs.exe` | 未签名 EXE 被误报 | 把 MixCut 安装目录加入杀软白名单 |

### 功能错误

| 症状 | 含义 | 解决 |
|---|---|---|
| 语音识别失败 `ExitCode=-1073741515`（STATUS_DLL_NOT_FOUND） | VC++ 运行库缺失 | 用最新安装包（已自带 6 个 VC Runtime DLL + vcomp140），别用过旧版本 |
| 语音识别失败 `ExitCode=-1073741795`（非法指令） | CPU 不支持 AVX2 | 老 CPU（Intel < Haswell 2013 / AMD < Excavator 2015）跑不了内置 whisper；其它功能仍可用 |
| AI 分析 / 配音报「免费额度已用完」 | 所选模型免费额度耗尽 | 去阿里百炼控制台开通付费、或关闭「仅用免费额度」、或换用 qwen-flash / qwen-turbo |
| AI 报「未开通模型权限」 | 该 Key 没开通对应模型 | 到控制台开通对应模型；配音克隆需 `qwen-voice-enrollment` + `qwen3-tts-vc` |
| 配音音色/性别不对 | 早期版本用整片前 6 秒克隆 | 升级到 v0.7+（逐分镜克隆），重新「改写配音」即可 |

### 一键收集诊断信息

把这段 PowerShell 复制到任意 PowerShell 窗口跑（无需管理员）：

```powershell
$log = Get-ChildItem $env:APPDATA\MixCut\logs -Filter "mixcut-*.log" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $log.FullName |
    Select-String -Pattern "EnvDiag|VcRuntimeDiag|CpuDiag|HwProbe|DubDiag|\[WRN\]|\[ERR\]|\[FTL\]"
```

若 `[EnvDiag] pass=False`，对照上表 1 分钟自助定位。

---

## 致谢

- [whisper.cpp](https://github.com/ggerganov/whisper.cpp) —— 本地语音识别
- [demucs](https://github.com/facebookresearch/demucs) / demucs.cpp —— 人声与背景乐分离
- [FFmpeg](https://ffmpeg.org/) —— 视频处理与解码底座
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) —— MVVM 框架
- [NAudio](https://github.com/naudio/NAudio) —— 音频播放
- [VirtualizingWrapPanel](https://github.com/sbaeumlisberger/VirtualizingWrapPanel) —— WPF 真虚拟化
- [Serilog](https://serilog.net/) —— 结构化日志

## License

待定（暂时保留所有权利，后续会补 LICENSE 文件）。
