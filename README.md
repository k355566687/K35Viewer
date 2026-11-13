# 35Viewer

画像アーカイブ（ZIP / CBZ）を、ライブラリとして管理しながら閲覧する Windows 用のビューアです。
複数のフォルダにあるアーカイブを横断して探し、開いて見ながら、不要な画像をその場で削除できます。

現在は MVP（最小限の機能をそろえた最初の版）です。

> **English summary**: 35Viewer is a free, open-source Windows desktop application (WPF / .NET 10) for browsing and managing
> image archives (ZIP / CBZ). It indexes archives across multiple folders, shows them as a searchable library with thumbnails,
> and lets you view the images and remove unwanted ones from an archive safely (the original archive is moved to the Recycle Bin).
> Binaries are built by GitHub Actions from this repository and published as a ZIP on [GitHub Releases](https://github.com/k355566687/K35Viewer/releases).
> See [Code signing policy](CODE_SIGNING.md), [Privacy](#プライバシー--privacy) and [Uninstall](#アンインストール--uninstall).

## ダウンロード

[Releases](https://github.com/k355566687/K35Viewer/releases) から `K35Viewer-v〈バージョン〉-win-x64.zip` をダウンロードし、展開したフォルダの `K35Viewer.exe` を実行してください。インストールは不要で、無料です。

実行ファイルには、まだ署名をしていません。初回の起動時に Windows の警告（SmartScreen）が出ることがあります。そのときは「詳細情報」→「実行」で起動できます（[Code signing policy](#code-signing-policy--コード署名ポリシー)）。

### アンインストール / Uninstall

インストーラーはありません。展開したフォルダを削除してください。ライブラリの情報（DB、サムネイル、ログ）も消す場合は、`%LOCALAPPDATA%\K35Viewer\` も削除してください。アーカイブ自体は削除されません。

There is no installer. To uninstall, delete the extracted folder. To also remove the library data (database, thumbnail cache and logs), delete `%LOCALAPPDATA%\K35Viewer\`. Your archives are not affected.

## 主な機能

- **ライブラリ**
  - 登録したフォルダ（NAS を含む）の中のアーカイブを、フォルダをまたいで一覧表示
  - 詳細表示とサムネイル表示、ファイル名での検索、並べ替え（ファイル名・更新日時・ファイルサイズ・画像枚数）
  - 1万件以上でも、ZIP を開かずに一覧を表示（SQLite のインデックスとサムネイルのキャッシュ）
  - アーカイブの名前変更・移動・削除（ごみ箱へ）、エクスプローラーで表示
- **ビューア**
  - 1枚表示（拡大・縮小、ウィンドウに合わせる、原寸、全画面）と一覧表示
  - 前回見ていた画像から開く
- **画像の削除**
  - 不要な画像を「削除予約」し、アーカイブを離れるときに、まとめて ZIP から取り除く
  - 更新前の ZIP は Windows のごみ箱へ退避する。新しい ZIP を作って確かめてから置き換えるため、途中で失敗しても元の ZIP は壊れない

対応形式は ZIP / CBZ の中の JPEG / PNG です（WebP、GIF、RAR、7z などは未対応）。

## 動作環境

- Windows 11 x64

配布用のビルド（下記）は .NET を同梱しているため、.NET のインストールは不要です。

## ビルド

必要なもの:

- .NET 10 SDK（`global.json` で 10.0.403 以降のパッチに固定）
- Visual Studio 2026（任意。コマンドラインだけでもビルドできます）

```powershell
dotnet build K35Viewer.slnx -c Release
```

実行ファイルは `src\K35Viewer.App\bin\Release\net10.0-windows\K35Viewer.exe` にできます。

### テスト

```powershell
dotnet test --solution K35Viewer.slnx -c Release
```

自動テストは一時フォルダの中だけで行い、実際のフォルダやごみ箱には触れません。

### 配布用のビルド

```powershell
.\scripts\publish.ps1
```

テストを実行したあと、`publish\K35Viewer-win-x64\` に self-contained（.NET 同梱）のフォルダを作ります。フォルダごとコピーして `K35Viewer.exe` を実行してください。

## データの保存先

ライブラリの情報（SQLite の DB）、サムネイル、ログは `%LOCALAPPDATA%\K35Viewer\` に保存します。
アーカイブ自体は、削除予約を確定したとき、または名前変更・移動・削除を行ったときだけ変更します。

## 主なキー操作

| 画面 | 操作 | キー |
|---|---|---|
| ライブラリ | 開く / 名前変更 / 削除 | Enter / F2 / Delete |
| ビューア | 前の画像 / 次の画像 | ← / →（PageUp / PageDown、Space でも可） |
| ビューア | 先頭 / 末尾 | Home / End |
| ビューア | 拡大 / 縮小 / ウィンドウに合わせる / 原寸 | + / - / 0 / 1 |
| ビューア | 削除予約（もう一度押すと解除） | Delete |
| ビューア | 1枚表示と一覧表示の切替 | G |
| ビューア | 全画面 | F11 |
| ビューア | ライブラリへ戻る | Esc |

## 設計書

設計書は [docs](docs/) にあります（日本語）。

| 文書 | 内容 |
|---|---|
| 要件定義 | 目的、機能の要件、MVP の範囲 |
| 画面一覧 | 画面とダイアログ、操作、見た目の規則 |
| ユースケース定義書 | 操作ごとの流れと例外 |
| DB設計書 | SQLite のテーブル、検索、スキャン |
| 内部アーキテクチャ設計書 | 層の構成、ZIP 更新の手順と安全境界、並行処理 |
| 開発環境設計書 | 採用技術、ビルド、テスト、実装の順序 |

## Code signing policy / コード署名ポリシー

See [CODE_SIGNING.md](CODE_SIGNING.md).

配布物への署名は、オープンソース向けの無料の署名サービス（SignPath Foundation）に申請する予定です。署名の対象・手順・担当は [CODE_SIGNING.md](CODE_SIGNING.md) にまとめています。現在の配布物には、署名をしていません。

## プライバシー / Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

35Viewer は、ネットワークを通じて情報を送信しません（利用状況の収集、更新の確認、オンラインの機能はありません）。読み書きするのは、利用者が登録したフォルダ（NAS などのネットワーク上のフォルダを含む）の中のファイルと、`%LOCALAPPDATA%\K35Viewer\` に保存する自身のデータ（DB、サムネイル、ログ）だけです。

## 行動規範

このプロジェクトに参加する方は、[行動規範（Contributor Covenant）](CODE_OF_CONDUCT.md) に従ってください。

## ライセンス

[MIT License](LICENSE)
