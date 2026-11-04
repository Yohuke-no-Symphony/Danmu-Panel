<p align="center">
  <img src="Sources/Yohuke.DanmuPanel/Assets/favicon.png" width="112" alt="Yohuke Danmu Panel 图标" />
</p>

<h1 align="center">Yohuke Danmu Panel</h1>

<p align="center">面向 B 站主播的跨平台弹幕阅读台</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja-JP.md">日本語</a>
</p>

<p align="center">
  <a href="#亮点">亮点</a> · <a href="#开发快速开始">开发快速开始</a>
</p>

![Yohuke Danmu Panel 阅读工作台](Screenshots/1.png)

## 亮点

- **专注阅读**：弹幕、SC、礼物与上舰独立展示，支持弹幕置顶和未读管理。
- **自由布局**：面板可停靠、分栏、浮动及保存布局，主窗口与浮动窗口均可独立置顶。
- **多房间管理**：按工作区组织直播间，独立保存阅读状态，支持导入和导出 Danmu Neo 工作区文件。
- **翻译与拼音**：通过 OpenAI-compatible API 翻译弹幕和 SC，支持术语表与缓存；用户名和礼物名可显示本地拼音。
- **随时继续**：SQLite 保存消息、译文、置顶和已读状态，提供浅色／深色主题与中、英、日三语界面。
- **更新检查**：启动后检查新的正式版本；设置 → 更新可关闭启动检查或手动检查，更新提示可跳转 GitHub 发布页下载。

## 开发快速开始

安装 **.NET 10 SDK**，在仓库根目录运行：

```sh
dotnet restore Sources/Yohuke.DanmuPanel.sln
dotnet run --project Sources/Yohuke.DanmuPanel/Yohuke.DanmuPanel.csproj
```

按修改模块筛选测试，例如工作台窗口：

```sh
dotnet test Sources/Yohuke.DanmuPanel.Tests/Yohuke.DanmuPanel.Tests.csproj --filter FullyQualifiedName~Windowing.ShellWindowTests
```

## 许可

基于 Danmu Neo，采用 [GPL-3.0](LICENSE) 许可。第三方组件保留各自许可。
