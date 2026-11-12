<p align="center">
  <img src="Sources/Yohuke.DanmuPanel/Assets/favicon.png" width="112" alt="Yohuke Danmu Panel icon" />
</p>

<h1 align="center">Yohuke Danmu Panel</h1>

<p align="center">A cross-platform live-chat reading desk for Bilibili streamers</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja-JP.md">日本語</a>
</p>

<p align="center">
  <a href="#highlights">Highlights</a> · <a href="#development-quick-start">Development Quick Start</a>
</p>

![Yohuke Danmu Panel workspace](Screenshots/1.png)

## Highlights

- **Focused reading**: dedicated panels for chat, Super Chats, gifts, and membership purchases, with pinned chat and unread tracking.
- **Your layout**: dock, split, float, and save panel layouts; keep the main window or individual floating windows on top.
- **Multiple rooms**: organize workspaces and live rooms with independent reading states; import and export Danmu Neo workspace files.
- **Translation and pinyin**: translate chat and Super Chats through an OpenAI-compatible API, with glossaries and caching; generate local pinyin for usernames, gift names, and message content. Chat and Super Chat content have independent toggles. All annotations share character-aligned ruby (default) or a separate pinyin line; hover over a Chinese character to highlight its syllable. Text in chat, Super Chat, gift, and membership panels scales together from 50% to 200% (default 100%).
- **Ready to resume**: SQLite preserves messages, translations, pins, and read states. Includes light/dark themes and English, Chinese, and Japanese interfaces.
- **Update checks**: checks for new official releases after startup. Settings → Updates provides a startup toggle and manual check; update prompts open the GitHub release page for downloads.
- **Settings backup and sharing**: Settings → Data imports and exports `.dnsettings.json` files containing appearance, color schemes, reading, translation, glossaries and update preferences, including the full API key. Accounts, workspaces, layouts and message history stay separate. Confirmed imports take effect immediately and close the settings window.

## Development Quick Start

Install the **.NET 10 SDK**, then run from the repository root:

```sh
dotnet restore Sources/Yohuke.DanmuPanel.sln
dotnet run --project Sources/Yohuke.DanmuPanel/Yohuke.DanmuPanel.csproj
```

Run tests for the module you change, for example the shell window:

```sh
dotnet test Sources/Yohuke.DanmuPanel.Tests/Yohuke.DanmuPanel.Tests.csproj --filter FullyQualifiedName~Windowing.ShellWindowTests
```

## Credits

The character in the icon was illustrated by @hshjbbnd. The character is [椎名菜羽](https://space.bilibili.com/623441612).

## License

Based on Danmu Neo and licensed under [GPL-3.0](LICENSE). Third-party components retain their respective licenses.

## Building from source

Initialize the shared Bilibili core before opening `Sources/Yohuke.DanmuPanel.sln` or running `release.ps1`.

```sh
git submodule update --init --recursive
```
