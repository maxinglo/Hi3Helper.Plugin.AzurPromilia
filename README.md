# Azur Promilia for Collapse Launcher

Adds support for the mainland China and Japan editions of Azur Promilia to Collapse Launcher.

| Region | Official launcher |
| --- | --- |
| 中国大陆 · 蓝色星原：旅谣 | [Manjuu](https://azurpromilia.manjuu.com/) |
| 日本 · アズールプロミリア | [Yostar](https://azurpromilia.jp/) |

## Installation

1. Download the plugin ZIP from the [GitHub releases](https://github.com/maxinglo/Hi3Helper.Plugin.AzurPromilia/releases).
2. Import the ZIP in Collapse Launcher under **Settings → Plugin Management**, then restart Collapse.
3. Select Azur Promilia and your region. If Collapse asks for an install folder, select the folder that contains the official launcher installation, including any launcher version folders.

Collapse opens the matching official launcher. Use that launcher to install, update, or repair the game and sign in. If the official launcher is missing, the plugin opens the publisher's download page.

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
