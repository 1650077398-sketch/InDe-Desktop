# InDe 星空处理 · Windows 桌面版

用图形界面在本地运行 InDeConv、InDeStars 和 InDeNoise。支持 RGB TIFF 和 PNG 原图预览，星点锐化、星晕调节、去星、无星层降噪及重新合成。可分别调整亮度和色彩降噪。

## 项目缘由

本项目起源于在本机安装 InDe-Suite 时遇到 PixInsight 无法正常载入模块的情况。如果你也遇到类似问题，可以通过这个 Windows 桌面程序直接调用原项目的 InDe 后端，继续完成反卷积、去星和降噪，无需在 PixInsight 中载入模块。

### 实时预览 API 兼容问题

本地尝试为 PixInsight 的 API 186 环境重编译原生模块时，构建日志记录了两处接口差异。

- 原模块的实时预览回调使用 `Image`，本机 PCL 的 `RequiresRealTimePreviewUpdate` 和 `GenerateRealTimePreview` 接收的是 `UInt16Image`。函数签名对不上，编译器报 `C3668`。
- 原模块请求的 `InterfaceFeature::RealTimeF32Images` 在本机 PCL 头文件中没有定义，编译器报 `C2039`。

按本机 PCL 接口调整实时预览代码后，兼容构建生成了 `InDe-pxm.dll`。这一步验证了编译通过。桌面版则直接运行原项目的 InDe 后端，图像预览和处理都在独立窗口内完成，无需依赖 PixInsight 的实时预览回调。

## 与 PixInsight 的 RC Astro 三件套对照

本桌面版通过 InDe-Suite 实现 RC Astro 三件套常用的三类功能。

| 功能 | 本程序使用的 InDe 模型 | RC Astro 对应插件 |
| --- | --- | --- |
| 反卷积、星点锐化和星晕调节 | InDeConv | [BlurXTerminator](https://www.rc-astro.com/blurxterminator-ml5/) |
| 去星并输出独立星点层 | InDeStars | [StarXTerminator](https://www.rc-astro.com/software/sxt/) |
| 降噪，支持亮度与色彩分别调整 | InDeNoise | [NoiseXTerminator](https://www.rc-astro.com/noisexterminator-2-ai3-user-manual-pixinsight/) |

本程序的模型及推理由 InDe-Suite 提供。参数数值与处理结果以各自实现为准。

## 下载和使用

1. 从 [GitHub Releases](https://github.com/1650077398-sketch/InDe-Desktop/releases/latest) 下载 `InDe-Desktop-v1.0.0-win-x64.zip`，完整解压。
2. 双击 `InDeDesktop.exe`。首次使用时，点击 **下载官方后端**。程序从原作者的 GitHub Release 下载约 405 MB 的压缩包，验证 SHA-256，再将其中的 `InDeBackend` 安装到当前用户目录，无需管理员权限。
3. 选取或拖入 8 位或 16 位 RGB TIFF／PNG。检查右侧原图预览，设置参数后点击 **开始处理**。输出文件夹包含 16 位 RGB 合成 TIFF、无星层、星点层及对比预览。

适用于 Windows 10／11 x64，使用系统的 .NET Framework 4.8。处理走本机的 InDe 后端，PixInsight 和 Codex 都无需安装。首次下载完成后可离线处理。

如果已经有官方后端，程序会查找同目录的 `InDeBackend` 以及当前用户的标准安装位置，也可以手动点击 **选择后端**。如果自动下载失败，可从 [原作者的 Windows 发行页](https://github.com/Indigo2233/InDe-Suite/releases/tag/v1.0.3-local) 下载 `InDe-1.0.3-local-windows-x64.zip`，按同一页面的 `SHA256SUMS-local.txt` 校验后解压，将程序指向其中含有 `runtime`、`engine` 和 `tools` 的 `InDeBackend` 文件夹。

## 源码和构建

在 Windows 的 PowerShell 中运行 `.\build.ps1`。脚本使用系统自带的 .NET Framework C# 编译器，在 `dist` 目录生成便携 ZIP 和 SHA-256 校验文件。

## 原作者与项目来源

本仓库是桌面前端和后端调用工具。反卷积、去星、降噪模型及推理后端来自 **Indigo2233 的 [InDe-Suite](https://github.com/Indigo2233/InDe-Suite)**。模型、Python 运行时及其依赖由原项目发行，相关授权和分发说明以原项目为准。桌面程序从原作者的官方发行页获取模型，GitHub 桌面版下载包只包含前端和调用脚本。感谢原作者提供模型与后端。
