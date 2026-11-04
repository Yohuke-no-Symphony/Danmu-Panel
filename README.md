# Yohuke Danmu Panel

面向 B 站主播的跨平台阅读台。基于 .NET 10、Avalonia 和 Dock，按模块复用 Danmu Neo 的窗口、主题、监听、登录及发布机制。

![](Screenshots/1.png)

## 阅读工作台

- 工作区、弹幕、SC、礼物、上舰五个独立面板，可拖拽、停靠、浮动及保存布局。
- 测试面板默认隐藏，通过「视图 → 测试面板」打开，同样支持停靠和浮动。
- 主窗口可置顶；浮动窗口可独立置顶。
- macOS 可在设置 → 外观中切换系统菜单栏与窗口内菜单，即时生效并自动保存。
- 工作区采用树形列表：右键空白处新建或导入，右键工作区添加房间、打开设置、排序或导出，双击工作区展开或折叠。房间名称和账号在工作区设置中编辑。
- 弹幕点击整行切换置顶，支持同时置顶多条。SC、礼物及上舰点击整行标记已读。
- 消息右键菜单提供对应的置顶、已读和重新翻译操作；加载更早记录位于面板右键菜单，回到顶部也可使用面板标题栏图标。
- 四类消息默认使用 Neo 紧凑模式：缩小字号与行距、隐藏时间；正文及译文左对齐、垂直居中。设置 → 阅读中可关闭紧凑显示，或分别开启弹幕/礼物/上舰与 SC 的自动换行；SC 默认换行，单行长文本悬停滚动。
- 弹幕使用 Neo 的奇偶行底色，随浅色/深色主题切换，置顶或加载历史后按当前顺序交替。
- SC 和上舰未读优先显示；礼物达到设置中的连击累计金额门槛后，未读优先显示。低价礼物仍保留。
- 弹幕的手动置顶消息位于固定区域，过多时独立滚动；SC、礼物和上舰的优先消息排在同一列表顶部，与历史消息一起滚动。
- SC、礼物和上舰以右上角红点标记未读，不占用消息布局空间；三个面板的标题显示当前房间的完整未读条数，右侧“全部已读”按钮可确认该类型的全部历史记录。
- 礼物和上舰使用与 SC 一致的完整彩色边框。礼物按连击累计金额使用六档 SC 色阶，上舰按舰长、提督、总督分色；各档颜色可在配色设置中覆盖。
- 礼物连击支持三种确认策略：同一连击只确认一次、新增数量恢复未读、新增价值再次达到门槛恢复未读。
- 全部消息、译文、置顶和已读状态持续写入 SQLite，重启恢复。列表分页加载；可清空当前房间或全部房间记录。

不同工作区中的同一直播间标签拥有独立阅读状态。清空会删除对应消息和翻译任务，保留账号、设置及布局；共享翻译缓存可单独清理。

向下翻阅时保留已加载的历史消息与阅读位置；回到顶部后恢复跟随新消息。更多历史仍通过面板右键菜单加载。 

工作区分享使用 `.dnworkspace.json`，兼容 Danmu Neo 的工作区名称和房间资料；导入创建独立工作区，不带入账号、消息和阅读状态。分享文件不包含 Cookie 或翻译 API Key。

## 自动翻译与拼音

在设置中填写 API Base URL（通常以 `/v1` 结尾，也接受完整 `/chat/completions` 地址）、API Key、模型名及目标语言，然后开启自动翻译。使用 OpenAI-compatible Chat Completions 协议，不要求使用 OpenAI 托管服务。

- 弹幕和 SC 支持双语或仅译文。请求失败时保留原文，右键可重新翻译。
- 表情保留在原文中，翻译仅发送剩余文字；纯表情不请求。
- 相同正文及翻译配置共享进行中的请求和持久化 LRU 缓存，默认 5,000 条。缓存淘汰不影响已保存消息的译文。
- 支持追加 system prompt、按目标语言维护术语译法。
- 默认实时优先：优先处理 SC 和置顶消息，普通等待队列最多 200 条、超时 30 秒可跳过。可切换为全部排队，重启继续处理未完成任务。
- 默认并发 4，单次请求超时 20 秒；暂时性错误最多额外重试 2 次。
- 用户名和礼物名分别支持本地带声调拼音标注，不调用翻译模型；多音字和罕见姓名读音可能需要人工核对。

界面支持简体中文、英文、日文及跟随系统。界面语言与翻译目标语言独立。

## 登录与数据

设置中的 B 站账号支持内嵌浏览器登录；点击“新增账号”打开登录窗口，手动 Cookie 通过账号区域的右键菜单添加或编辑。账号选择按房间、工作区、全局逐级继承；未配置时使用匿名监听。

数据保存于系统 ApplicationData 下的 `Yohuke.DanmuPanel`，与 Danmu Neo 隔离：

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 外观、阅读、翻译设置及术语库 |
| `accounts.json` | B 站账号 Cookie |
| `workspaces.json` | 工作区与房间资料 |
| `layouts.json` | 当前布局和自定义预设 |
| `messages.db` | 消息、状态、连击与翻译任务 |
| `translation-cache.db` | 可清理的译文复用缓存 |
| `Cache/Emoticons` | 表情图片缓存 |

配置采用原子替换并保留 `.bak` 备份；损坏配置移入 `Corrupt`。Cookie 和 API Key 沿用本地配置保存方式，分享配置前应移除凭据。消息数据库使用 WAL 和同步提交，已提交消息可在应用闪退后恢复；未收到或尚未提交的网络消息无法保证恢复。

## 开发与测试

测试面板向当前选中的房间添加本地弹幕、SC、礼物或上舰消息，直接使用各类消息的现有布局。可填写正文、人工译文、金额、数量及上舰等级，使用标题栏图标或右键菜单添加单条、批量添加（最多 500 条）或清除当前房间测试消息。批量用户名附序号，便于检查滚动位置；测试消息不发送到 B 站。弹幕和 SC 按当前自动翻译设置进入同一翻译队列，复用优先级、缓存和请求合并；填写人工译文时直接显示该译文。清理测试消息及其翻译任务时保留真实消息。

需要 .NET SDK 10。

```sh
dotnet restore Sources/Yohuke.DanmuPanel.sln
dotnet run --project Sources/Yohuke.DanmuPanel/Yohuke.DanmuPanel.csproj
dotnet test Sources/Yohuke.DanmuPanel.sln
```

按模块筛选测试示例：

```sh
dotnet test Sources/Yohuke.DanmuPanel.sln --filter 'FullyQualifiedName~Messages|FullyQualifiedName~Integration'
dotnet test Sources/Yohuke.DanmuPanel.sln --filter FullyQualifiedName~Translation
dotnet test Sources/Yohuke.DanmuPanel.sln --filter FullyQualifiedName~Windowing
```

测试使用临时数据库、模拟 HTTP/监听和 Avalonia Headless。真实 B 站登录、直播消息及平台原生窗口交互需要在桌面环境验收。

## 发布

复用 Neo 的宿主平台分流和自包含打包方式：Windows 生成 x64 NSIS 安装器，macOS 生成 ARM64 App 与 DMG。

```powershell
pwsh -NoProfile -File ./release.ps1 -Clean
```

Windows 需要 NSIS 3.11+，可传 `-NsisPath`。macOS 默认 ad-hoc 签名，可传 `-MacCodeSignIdentity`，脚本不自动提交公证。版本读取项目的 `AssemblyVersion`，也可传 `-Version`。

产物位于 `Artifacts/Publish`。安装器、快捷方式、macOS bundle ID、登录浏览器资料和用户数据均使用独立 Panel 标识。发布过程校验三种语言包及完整发布文件。

发布脚本测试需要 Pester 5.7.1+：

```powershell
Invoke-Pester -Path ./Sources/Yohuke.DanmuPanel.Tests/Packaging -CI
```

## 来源与许可

应用沿用 Danmu Neo 的 GPL-3.0 许可，见 `LICENSE`。窗口、Dock、主题、B 站接入及发布模板来自 Danmu Neo；SQLite、Avalonia、Dock、CommunityToolkit、Lucide 和 csharp-pinyin 等依赖保留各自许可。拼音词库的来源及说明见 csharp-pinyin 上游项目。
