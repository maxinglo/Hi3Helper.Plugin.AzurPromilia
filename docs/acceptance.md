# 0.2.0 验收记录

验收日期：2026-11-26。环境：Windows x64，Collapse Launcher 1.84.8。

## 范围

支持中国大陆、日本、台港澳三个区服，均显示 `BETA`。韩国服已移除。插件及发布包使用三段式版本 `0.2.0`，游戏本体版本保留发行方原始格式。

国服复用已有客户端进行实机验收。日本与台港澳按约定核对官方发行清单、游戏主程序及启动参数，并验证样本文件；未下载、安装或运行海外完整客户端。

## 官方发行信息

| 区服 | 本体版本 | 文件数 | 文件总字节 | 主程序 | 启动参数 |
| --- | --- | ---: | ---: | --- | --- |
| 中国大陆 | `1.0.0.30` | 40,457 | 84,840,079,623 | `AzurPromilia.exe` | `-start=azurpromilia_launcher` |
| 日本 | `1.0.2` | 40,299 | 84,158,601,695 | `AzurPromiliaJP.exe` | `-start=azurpromilia_launcher` |
| 台港澳 | `1.0.0.2646520` | 40,753 | 88,382,604,122 | `AzurPromilia.exe` | `-start=azurpromilia_launcher` |

各区服均通过实际官方接口取得本体发行信息及完整文件清单。全新环境的引导流程已验证：下载并验证发行方签名的官方启动器安装包，提取发行配置，不执行安装包。损坏、缺失或区服错误的本机启动器配置会回退到此流程。

已核对的清单 SHA-256：

| 区服 | SHA-256 |
| --- | --- |
| 中国大陆 | `5e107c7047fa51accae2cbd463f8e5a4e9ee5a145842b605b3e298003b0e1d7c` |
| 日本 | `4227596e21986ed9945f167cafd3aa2d441051c648cc509132f109c518a440cf` |
| 台港澳 | `15f151b67596f1fcab7c548345a31e0a896f414ca6753a7f65a10df52abe9e81` |

日本主程序样本为 816,976 字节，官方 CRC-64/XZ 为 `11514229138150300794`，已实算匹配。台港澳清单中的主程序为 815,144 字节，MD5 为 `1c690cda53a2c78e9abefcfe5cc87180`。国服原主程序 MD5 为 `24f247bd7e6448c812525b58844494d7`，与实际文件匹配。

## 国服完整校验、续传与复用安装

实际游戏目录为 `E:\AzurPromilia\AzurPromilia_Game`。通过插件生产代码解析实时清单，完整校验 **40,457 / 40,457** 个文件、**84,840,079,623 / 84,840,079,623** 字节，无缺失目录、无大小或 MD5 不匹配。

真实 CDN 续传样本为 `AzurPromilia_Data/StreamingAssets/.res/default_package/8rlxkx9hazmfy3ek0tnag.r`：

- 首次请求返回 HTTP 200，在写入 81,920 字节后取消。
- 再次请求携带 `Range: bytes=81920-3591904`，收到 HTTP 206 和匹配的 Content-Range。
- 最终文件为 3,591,905 字节，MD5 `7a1c1c36c1d064dcb512e026de4cc9d8`，并与已有安装中的文件逐字节一致。

随后调用实际安装接口接管已有客户端。此步骤禁止网络下载，复用全部游戏文件，完成最终全量复检后写入插件安装记录；安装前后版本均为 `1.0.0.30`，安装接口返回 Completed。

## 国服直接启动与界面

从 Collapse 国服页点击“开始游戏”后出现真实游戏窗口，进程路径与上述目录中的主程序一致。原生插件启动接口也验证了实际进程、运行状态、启动时间和等待游戏退出行为；启动前后没有新增官方启动器进程。

游戏使用原版 `union_pcsdk.dll`，SHA-256 为 `939448f9a0a7a47e6f645d20497a454b43000982406a058965652b53a86fc697`，未应用偏移补丁。传入 `-force-d3d12` 后，实际 Unity Player 日志报告 Direct3D 12、Feature Level 12.1。

官方动态背景、静态背景选项、官网活动与公告列表及 `BETA` 标签已通过界面验收。README 使用的[截图](assets/collapse-cn.png)保留验收时的原图。

## 更新与恢复回归

常规测试和 Native AOT 包检查覆盖：

- MD5 与 CRC-64/XZ 校验、空文件、损坏文件、HTTP Range 续传、错误响应、取消与重试。
- 单 CDN 的损坏前缀重新下载和备用 CDN 切换。
- 已有官方安装无插件记录时，读取原版清单并验证旧文件后更新；同长度但旧散列不匹配的冲突文件不会被替换。
- 插件管理的旧版本更新、下载空间不足、目录冲突及跨区服安装拒绝。
- 安装提交中断后恢复；下次发行发生变化时，按持久化的 MD5/CRC64 记录识别已落盘文件并继续更新。
- 日本官方目录、版本文件及 CRC64 清单识别。
- 宿主初始化等待取消后复用在途的发行请求，避免反复从头下载引导安装包。
- 三个区服预设、对应主程序、OpenBeta 标记、三段式插件版本和原生 ABI。

当前官方未提供高于已安装国服 `1.0.0.30` 的版本，因此跨版本更新采用上述可控旧版／新版清单回归；实机完成的是当前版本全量校验和零下载接管。

## 已知边界

官方启动器的内部配置格式改变时，引导解析可能需要适配；当前仅接受已验证格式和发行方签名。发行请求失败不会把其他地区或未知来源当作本区服发行版本。

当前 Plugin Core 的原生启动／等待封装未把常规取消标识传递至游戏等待任务；未将该项计为通过。此问题不影响上述下载取消续传，也不会主动结束游戏进程。

## 复验

```powershell
# 常规回归，不安装真实客户端。
dotnet run --project tests/Hi3Helper.Plugin.AzurPromilia.Tests -c Release -p:Platform=x64

# 国服实时清单、完整校验及真实 CDN 取消续传，只读已有游戏文件。
dotnet run --project tests/Hi3Helper.Plugin.AzurPromilia.Tests -c Release -p:Platform=x64 -- --china-verify E:\AzurPromilia\AzurPromilia_Game

# 在完整校验成功后接管现有客户端，写入插件安装记录。
dotnet run --project tests/Hi3Helper.Plugin.AzurPromilia.Tests -c Release -p:Platform=x64 -- --china-adopt E:\AzurPromilia\AzurPromilia_Game

# 构建 Native AOT 并验证发布包。
./scripts/Publish.ps1
```

实机验收命令将报告写入本地忽略的 `artifacts/acceptance/china-integrity.json`。原生启动检查项目位于 `tests/Hi3Helper.Plugin.AzurPromilia.NativeAcceptance`，仅显式指定 `--launch` 时才启动游戏。
