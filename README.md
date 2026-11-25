# Azur Promilia for Collapse Launcher

Shows official Azur Promilia news, events, and community links in Collapse Launcher for mainland China, Japan, Taiwan, and Korea.

| Region | Official launcher |
| --- | --- |
| 中国大陆 · 蓝色星原：旅谣 | [Manjuu](https://azurpromilia.manjuu.com/) |
| 日本 · アズールプロミリア | [Yostar](https://azurpromilia.jp/) |
| 台灣 · 藍色星原：旅謠 | [JIMI](https://azurpromilia.jimi-global.com/home/) |
| 한국 · 아주르 프로밀리아 | [Nexon](https://azurpromilia.nexon.com/main) |

## Installation

1. Download the plugin ZIP from the [GitHub releases](https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/releases).
2. Import the ZIP in Collapse Launcher under **Settings → Plugin Management**, then restart Collapse.
3. Select Azur Promilia and your region. For mainland China, select the existing game folder containing `AzurPromilia.exe` (or its official launcher parent folder). For other regions, select the official launcher installation if available.

For mainland China, **Start Game** launches the installed game directly with `-start=azurpromilia_launcher`, without opening the official launcher or modifying `union_pcsdk.dll`. Collapse tracks the actual game process and waits until it exits. Custom launch arguments are passed to the game; add `-force-d3d12` in Collapse's game settings to request DirectX 12. Stopping Collapse's wait does not terminate the game.

The direct launch was verified with the mainland China game version `1.0.0.30` and its original SDK. Other regions still open a recognized official launcher or the publisher website. Game release discovery, downloading, installation and updates are not yet connected to the publisher's live release feeds; use the publisher's application for these operations until region-specific acceptance is complete.

## Plugin updates

Formal plugin releases are distributed through GitHub Releases and the OSS mirror served by ESA. Check for updates in **Plugin Management** and restart Collapse to apply a downloaded update. Preview releases are available as separate GitHub prereleases for manual import.

## Building

Use Windows x64 with the .NET SDK from `global.json` and the Visual Studio C++ build tools for NativeAOT. Initialize the Core submodule and run the publish script from the repository root.

```powershell
git submodule update --init --recursive
./scripts/Publish.ps1
```

Import the resulting ZIP from `artifacts/packages`.

## License

The plugin source is available under the [MIT License](LICENSE). Azur Promilia and its launchers belong to their respective rights holders.
