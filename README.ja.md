# Clawd for Orca

[English](README.md) · [한국어](README.ko.md) · **日本語** · [简体中文](README.zh-CN.md)

Clawd は、[Orca](https://www.onorca.dev) で実行中のコーディングエージェントを見守る macOS・Windows 用
デスクトップペットです。エージェントが承認や回答を待つと画面にカードで知らせ、Orca を開かずにその場で承認、返信、
ターミナル操作まで行えます。

https://github.com/user-attachments/assets/1c0bdd06-f9ae-479c-bc65-6bfe3ec750a1

> **非公式プロジェクトです。** Anthropic および Stably AI(Orca)とは関係ありません。Claude、Claude Code、
> Clawd は Anthropic の商標であり、Orca は Stably AI の製品です。

## Clawd の特長

エージェントが作業している間、ほかの作業に集中できます。承認や回答が必要になると Clawd が画面にカードを
表示するため、別の作業中でもリクエストを見逃しません。

カードからは Orca を開かずに承認や回答ができ、エージェントの進行状況やターミナルも同じ場所で確認できます。
承認ボタンだけで終わる一般的な通知アプリとの違いです。

macOS では Swift、Windows では WinUI 3 で作られたネイティブアプリです。Mac 版のダウンロードサイズは
3.7 MB、待機中の CPU 使用率は約 1% です。

## ダウンロード

### macOS

[GitHub Releases](https://github.com/joonseo1227/clawd-for-orca/releases) から `Clawd-1.0.0-macOS.dmg` を
ダウンロードして開き、`Clawd.app` をアプリケーションフォルダにドラッグしてください。Apple の公証済みです。

macOS 15 以降の Apple silicon Mac と [Orca](https://www.onorca.dev) が必要です。

![チャットのポップオーバーを開いたデスクトップ上の Clawd](docs/images/overview-popover-en.png)

### Windows

[GitHub Releases](https://github.com/joonseo1227/clawd-for-orca/releases) から `Clawd-1.0.0-Windows-x64.msi` を
ダウンロードして実行してください。管理者権限なしで現在のユーザーにインストールされ、スタートメニューに追加されます。
インストーラーにはコード署名がないため、SmartScreen が確認を求めることがあります。

x64 版 Windows 11 と Windows 用 [Orca](https://www.onorca.dev) が必要です。セットアップと既知の制限は
[Windows/README.md](Windows/README.md)(英語)を参照してください。

![Windows のデスクトップでチャットを開いた Clawd](docs/images/overview-windows-en.png)

## 主な機能

- **通知カード:** エージェントが承認や回答を待つとカードで知らせ、確認するまで再通知します。
- **チャット:** エージェントを状態別に表示し、Claude Code の作業過程をステップごとに表示します。
- **権限の承認:** 承認リクエストをボタンで表示し、ワンクリック(⌘1〜⌘3)で応答できます。
- **返信:** チャットに入力したメッセージはエージェントのターミナルに直接送られます。
- **リアルタイムターミナル:** Orca と接続すると、エージェントのターミナルを色や入力も含めてそのまま使えます。
- **ドラッグ&ドロップ:** Clawd やチャットにファイルをドロップすると、パスを送信します。
- **メニューバー、ショートカット(⌃⌥J)、システム通知:** 通知から直接返信することもできます。

| macOS | Windows |
| :---: | :---: |
| <img src="docs/images/overview-card-en.png" width="420" alt="macOS で「Permission needed」カードを掲げる Clawd"> | <img src="docs/images/overview-windows-card-en.png" width="420" alt="Windows で「Permission needed」カードを掲げる Clawd"> |

アプリは英語と韓国語に対応し、システムの言語設定に従います。

## 使い方

| 操作 | 動作 |
| --- | --- |
| Clawd またはメニューバーのアイコンをクリック | チャットを開きます。待機中のエージェントがあれば先に表示します。 |
| ⌃⌥J | どこからでもチャットを開閉します。 |
| ⌘1〜⌘3 | 表示中の権限リクエストに応答します。 |
| ⌘T | チャットとターミナルを切り替えます。 |
| Clawd をダブルクリック | 最も対応が必要なエージェントを Orca で開きます。 |

Windows では ⌘ の代わりに Ctrl、⌃⌥J の代わりに Ctrl+Alt+J を使い、メニューバーのアイコンの代わりに
通知領域のアイコンをクリックします。

### リアルタイムターミナルの接続

1. Orca の左サイドバーで **Orca Mobile** を開きます。
2. QR コードを作成し、**Copy pairing code** をクリックします。
3. Clawd の設定でリアルタイムターミナルの横にある **Pair…** をクリックし、コードを貼り付けます。

## アップデート

Clawd は 1 日に 1 回、GitHub で新しいバージョンを確認します。macOS ではカードとメニューでお知らせし、
インストールするかどうかは自分で選べます。Windows ではアップデートをバックグラウンドでダウンロードし、
Clawd を再起動してインストールするか確認します。設定から今すぐ確認したり、自動確認をオフにしたりできます。

## プライバシー

Clawd は同じコンピューターで実行中の Orca と通信します。それ以外の通信は 1 日 1 回のアップデート確認だけで、
GitHub から Clawd のリリース情報を取得するのみです。あなたやエージェントに関する情報は送信しません。ペアリング
情報は macOS のキーチェーンまたは Windows の資格情報マネージャーにのみ保存されます。

## ソースからビルド

```sh
git clone https://github.com/joonseo1227/clawd-for-orca.git
cd clawd-for-orca/macOS
./build.sh --install
```

Mac 版のビルドには Xcode 26 以降が必要です。Windows 版は [Windows/README.md](Windows/README.md)(英語)を
参照してください。テスト、署名、動作の仕組みは [CONTRIBUTING.md](CONTRIBUTING.md)(英語)にまとめています。

## ライセンス

[GPL-3.0](LICENSE) © 2026 joonseo1227。コードは自由に使用、変更、共有できますが、このコードをもとに配布する
プログラムもソースとともに GPL-3.0 で公開する必要があります。ライセンスは上記の商標には適用されず、
[`macOS/Sources/Sprite.swift`](macOS/Sources/Sprite.swift) に描かれた Clawd のキャラクターは Anthropic に
帰属します。
