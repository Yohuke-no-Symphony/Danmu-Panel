<p align="center">
  <img src="Sources/Yohuke.DanmuPanel/Assets/favicon.png" width="112" alt="Yohuke Danmu Panel アイコン" />
</p>

<h1 align="center">Yohuke Danmu Panel</h1>

<p align="center">Bilibili 配信者向けのクロスプラットフォーム弾幕リーダー</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja-JP.md">日本語</a>
</p>

<p align="center">
  <a href="#主な機能">主な機能</a> · <a href="#開発クイックスタート">開発クイックスタート</a>
</p>

![Yohuke Danmu Panel のワークスペース](Screenshots/1.png)

## 主な機能

- **読みやすい表示**：弾幕、スーパーチャット、ギフト、大航海の加入通知を専用パネルに表示。弾幕のピン留めと未読管理に対応。
- **自由なレイアウト**：パネルのドッキング、分割、フローティングとレイアウト保存に対応。メインウィンドウと各フローティングウィンドウを最前面に固定できます。
- **複数ルームの管理**：ワークスペースごとに配信ルームを整理し、閲覧状態を個別に保存。Danmu Neo のワークスペースファイルも読み書きできます。
- **翻訳とピンイン**：OpenAI 互換 API で弾幕とスーパーチャットを翻訳。用語集とキャッシュに対応し、ユーザー名・ギフト名・メッセージ本文のピンインはローカルで生成します。弾幕とスーパーチャットの本文は個別に切り替え、すべての注音で文字ごとのルビ（初期設定）と文章全体のピンイン行を選択できます。漢字にマウスを合わせると対応する音節が強調されます。弾幕・スーパーチャット・ギフト・メンバーシップの各パネルの文字は、まとめて50%〜200%に拡大・縮小できます（初期設定100%）。
- **続きから再開**：SQLite にメッセージ、訳文、ピン留めと既読状態を保存。ライト／ダークテーマと英語・中国語・日本語の UI を備えています。
- **更新の確認**：起動後に新しい正式リリースを確認します。設定 → 更新で自動確認の切り替えと手動確認ができ、通知から GitHub のリリースページを開いてダウンロードできます。
- **設定のバックアップと共有**：設定 → データ管理から `.dnsettings.json` を読み書きできます。外観、配色、閲覧、翻訳、用語集、更新の設定と完全な API キーを保存します。アカウント、ワークスペース、レイアウト、メッセージ履歴は別に管理されます。インポートを確認すると設定がすぐに反映され、設定ウィンドウが閉じます。

## 開発クイックスタート

**.NET 10 SDK** をインストールし、リポジトリのルートで実行します。

```sh
dotnet restore Sources/Yohuke.DanmuPanel.sln
dotnet run --project Sources/Yohuke.DanmuPanel/Yohuke.DanmuPanel.csproj
```

変更したモジュールに絞ってテストします。ウィンドウの例：

```sh
dotnet test Sources/Yohuke.DanmuPanel.Tests/Yohuke.DanmuPanel.Tests.csproj --filter FullyQualifiedName~Windowing.ShellWindowTests
```

## クレジット

アイコンのキャラクターイラストは @hshjbbnd が描いたものです。キャラクターは[椎名菜羽](https://space.bilibili.com/623441612)です。

## ライセンス

Danmu Neo をベースとし、[GPL-3.0](LICENSE) で公開しています。サードパーティ製コンポーネントにはそれぞれのライセンスが適用されます。
