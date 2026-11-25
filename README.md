<div align="center">

# Hi3Helper.Plugin.AzurPromilia

为 [Collapse Launcher](https://collapselauncher.com/) 提供《蓝色星原：旅谣 / Azur Promilia》支持的第三方插件。

**中国大陆 · 日本 · 台港澳**

[下载插件](https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/releases) · [开发构建](https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/actions/workflows/build.yml) · [反馈问题](https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/issues)

<img src="docs/assets/collapse-cn.png" width="85%" alt="蓝色星原：旅谣国服在 Collapse Launcher 中的界面，包含 BETA 标签、官方背景和活动列表" />

</div>

> [!IMPORTANT]
> 使用 Windows x64 和支持插件接口 **0.1.6.2** 的 Collapse Launcher。当前各区服均标记为 **BETA**，已在 Collapse **1.84.8** 验证国服。插件由社区维护，遇到问题请在本仓库反馈。

## 功能

- 获取各区服的官方游戏版本和文件清单，从官方 CDN 下载、安装和更新游戏。
- 支持断点续传、完整性校验，并复用已有的完整游戏文件。
- 识别已有官方客户端和版本，保留游戏内下载的补充资源。
- 从 Collapse 直接启动游戏，账号登录在游戏内完成。
- 显示官方活动、公告、资讯、轮播和社交媒体入口，国服保留官网的活动与公告列表。
- 显示官方背景；国服可选择动态或静态背景。
- 支持 Collapse 的自定义启动参数，可添加 `-force-d3d12` 请求使用 DirectX 12。

三个区服均使用 `-start=azurpromilia_launcher` 启动对应游戏主程序。国服 `1.0.0.30` 已使用原版 SDK 实机启动，并验证 DirectX 12，无须修改 `union_pcsdk.dll`。日本与台港澳已核对官方发行清单、主程序及启动参数；验收范围为清单和样本文件，没有进行海外完整客户端安装或启动。

## 支持区服

| 区服 | 游戏名称 | 主程序 | 官网 |
| --- | --- | --- | --- |
| 中国大陆 | 蓝色星原：旅谣 | `AzurPromilia.exe` | [国服官网](https://azurpromilia.manjuu.com/) |
| 日本 | アズールプロミリア | `AzurPromiliaJP.exe` | [日服官网](https://azurpromilia.jp/) |
| 台港澳 | 藍色星原：旅謠 | `AzurPromilia.exe` | [繁中服官网](https://azurpromilia.jimi-global.com/home/) |

各区服使用独立的官方客户端和游戏目录。已有客户端时，选择包含对应主程序的游戏目录；官方默认子目录分别为 `AzurPromilia_Game`、`AZUPRO_JP` 和 `AzurPromilia_TC_game`。

## 安装插件

1. 从 [Releases](https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/releases) 下载 `AzurPromilia_*.zip`。开发构建可在 [Actions](https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/actions/workflows/build.yml) 的成功运行中获取。
2. 打开 Collapse 的 **设置 → 插件管理**，选择添加 ZIP 或 `manifest.json`，导入插件 ZIP。
3. 重启 Collapse，在游戏列表中选择 **蓝色星原：旅谣** 和对应区服。
4. 安装游戏，或选择已有的游戏安装目录，然后点击 **开始游戏**。

插件采用 Native AOT，无须额外安装 .NET 桌面运行时。首次获取官方发行信息可能需要下载并验证官方启动器安装包，请等待加载完成。

## 插件更新

在 Collapse 的 **设置 → 插件管理** 中检查插件更新，或启用插件自动更新，下载完成后重启 Collapse 应用更新。正式版通过 GitHub Releases 和 `plugins.freedorm.net` 经 ESA 提供的 OSS 镜像分发；预览版可从 GitHub 预发布页面下载 ZIP 手动导入。

插件版本使用三段式，例如 `0.2.0`。曾手动导入 `0.2.0.x` 内部开发包的用户，需要手动导入一次 `0.2.0` ZIP，之后按正式更新渠道接收更新。

## 从源码构建

需要 `global.json` 指定的 .NET SDK、PowerShell 7、Visual Studio C++ 桌面工具链及 Windows SDK。

```powershell
git submodule update --init --recursive
dotnet run --project tests/Hi3Helper.Plugin.AzurPromilia.Tests -c Release -p:Platform=x64
./scripts/Publish.ps1
```

可导入的 ZIP 位于 `artifacts/packages/`。构建脚本会检查 Native AOT 接口、区服预设、版本和包内容。

GitHub Actions 的 **Release Azur Promilia plugin** 工作流在 `preview` 发布预览版，在 `stable` 发布正式版。正式版构建后自动发布 GitHub 更新清单，再将同一安装包和更新文件上传 OSS，并通过 ESA 下载校验。

[验收范围与记录](docs/acceptance.md)

## 许可

本项目采用 [MIT 许可证](LICENSE)，与 Collapse Launcher 和游戏发行方无隶属关系。游戏名称、图像及相关素材归其权利人所有。
