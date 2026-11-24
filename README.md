# 35Viewer

画像アーカイブ（ZIP / CBZ。RAR / 7z は閲覧と ZIP への変換）を、ライブラリとして管理しながら閲覧する Windows 用のビューアです。
複数のフォルダにあるアーカイブを横断して探し、開いて見ながら、不要な画像をその場で削除できます。

現在の版は v0.2.6（試用版）です。v0.2.0 で、設定画面、バックアップと復元、アプリの更新を加え、v0.2.1 で、ビューアから一覧に戻らずに、前後のアーカイブへ移れるようにし、v0.2.2 で、アプリの更新の間の表示を分かりやすくし、v0.2.3 で、RAR / 7z（CBR / CB7）の閲覧と、ZIP への変換を加え、v0.2.4 で、GIF / WebP の画像への対応と、新着の表示を加え、v0.2.5 で、ショートカットの作成、アプリの中からのアンインストール、設定画面のタブ分けを加え、v0.2.6 で、最近開いたアーカイブの一覧、終了時の状態の復元、表紙の選択と切り抜き、マウスの戻るボタンへの対応を加えました。

> **English summary**: 35Viewer is a free, open-source Windows desktop application (WPF / .NET 10) for browsing and managing
> image archives (ZIP / CBZ; RAR / 7z can be viewed and converted to ZIP). It indexes archives across multiple folders, shows them as a searchable library with thumbnails,
> and lets you view the images and remove unwanted ones from an archive safely (the original archive is moved to the Recycle Bin).
> Version 0.2.0 adds a settings window (appearance, confirmations), backup and restore of the app data, and in-app updates.
> Version 0.2.3 adds viewing RAR / 7z (CBR / CB7) archives and converting them to ZIP.
> Version 0.2.4 adds GIF / WebP images and a "new" badge for newly found archives.
> Version 0.2.5 adds Start Menu / Desktop shortcuts, an in-app uninstaller that never touches your archives, and a tabbed settings window.
> Version 0.2.6 adds a "Recently opened" list, restoring the window and list state on start, choosing and cropping the cover image, and the mouse Back button.
> Binaries are built by GitHub Actions from this repository and published as a ZIP on [GitHub Releases](https://github.com/k355566687/K35Viewer/releases).
> See [Code signing policy](CODE_SIGNING.md), [Privacy](#プライバシー--privacy) and [Uninstall](#アンインストール--uninstall).

## ダウンロード

[Releases](https://github.com/k355566687/K35Viewer/releases) から `K35Viewer-v〈バージョン〉-win-x64.zip` をダウンロードし、展開したフォルダの `K35Viewer.exe` を実行してください。インストールは不要で、無料です。

実行ファイルには、まだ署名をしていません。初回の起動時に Windows の警告（SmartScreen）が出ることがあります。そのときは「詳細情報」→「実行」で起動できます（[Code signing policy](#code-signing-policy--コード署名ポリシー)）。

### 更新 / Updating

v0.2.0 以降は、アプリの中から更新できます（[設定画面](#設定バックアップ更新)の「更新を確認」「更新する」）。

v0.1.0 には更新の機能がないため、v0.1.0 から更新する場合は、アプリを終了して、展開したフォルダの中身を、新しい版の ZIP の中身で上書きしてください。ライブラリの情報（`%LOCALAPPDATA%\K35Viewer\`）は、そのまま引き継がれます。

From v0.2.0 on, you can update from inside the app (Settings → "Check for updates" / "Update"). v0.1.0 has no updater: close the app and overwrite the extracted folder with the contents of the new ZIP; your library data is kept.

### アンインストール / Uninstall

**アプリの中から**（v0.2.5 から）: 設定画面（ライブラリ画面の左下の「⚙ 設定」）の「アプリ」のタブの「アンインストール」で、「アンインストール…」を押します。

- **消すもの**: プログラムのフォルダ（展開したフォルダ）、データのフォルダ（`%LOCALAPPDATA%\K35Viewer\`。DB、設定、バックアップ、サムネイル、ログ）、作ったショートカット
- **消さないもの**: アーカイブ（管理フォルダの中のファイル）。管理フォルダが、プログラムやデータのフォルダの中にあるなど、アーカイブを巻き込むおそれがあるときは、実行しません（理由を表示します）
- **バックアップ**: 削除の前に、データのバックアップを、選んだ場所（初めは「ダウンロード」フォルダ）へ保存できます。あとで入れ直したら、設定の「復元…」で戻せます
- 元に戻せません（ごみ箱へは送りません）。「アンインストール」と入力しないと、押せません

**手作業で**: インストーラーはありません。展開したフォルダを削除してください。ライブラリの情報（DB、設定、バックアップ、サムネイル、ログ）も消す場合は、`%LOCALAPPDATA%\K35Viewer\` も削除してください（バックアップも消えます）。アーカイブ自体は削除されません。ショートカットを作っていた場合は、先に、設定画面の「ショートカット」で「削除」を押してください（そのまま展開したフォルダを消すと、ショートカットが残ります）。

In the app (v0.2.5 and later): Settings → "App" tab → "Uninstall…". It removes the program folder, the data folder (`%LOCALAPPDATA%\K35Viewer\`) and the shortcuts it created, and **never touches your archives**: it refuses to run if a library folder lies inside the folders to be deleted, or if archive-like files are found in the program folder. You can save a backup of your data to a folder you choose (the Downloads folder by default) first, and restore it after reinstalling. Deletion is permanent (nothing goes to the Recycle Bin) and you must type a confirmation word. By hand: there is no installer, so delete the extracted folder, and delete `%LOCALAPPDATA%\K35Viewer\` to remove the library data too; delete the shortcuts first from the Settings window if you created any. Your archives are not affected.
## 主な機能

- **ライブラリ**
  - 登録したフォルダ（NAS を含む）の中のアーカイブを、フォルダをまたいで一覧表示
  - 詳細表示とサムネイル表示、ファイル名での検索、並べ替え（ファイル名・更新日時・ファイルサイズ・画像枚数）
  - 1万件以上でも、ZIP を開かずに一覧を表示（SQLite のインデックスとサムネイルのキャッシュ）
  - アーカイブの名前変更・移動・削除（ごみ箱へ）、エクスプローラーで表示
  - 表示方式（詳細 / サムネイル）を覚えて、次の起動でも同じ表示で始める
  - 新しく見つかったアーカイブに「新着」の印を付ける。開くか、「新着を既読に」で外れる。「新着だけ」で絞り込める（v0.2.4）
  - 「最近開いた」で、最近開いたアーカイブ（10件まで）を一覧にして、すぐ開き直せる（v0.2.6）
  - 表紙（サムネイル）にする画像を選び、切り抜ける。ビューアで画像を右クリックして「この画像を表紙にする」「表紙を切り抜く…」。縦横比は自由で、ライブラリの右クリックで自動に戻せる（v0.2.6）
  - ウィンドウの位置と大きさ、一覧の並べ替えと「新着だけ」を、次の起動でも覚えている（v0.2.6）
- **ビューア**
  - 1枚表示（拡大・縮小、ウィンドウに合わせる、原寸、全画面）と一覧表示（表示方式は、次の起動でも覚えている）
  - 前回見ていた画像から開く
  - ↑ / ↓ で、一覧に戻らずに、ライブラリの一覧の並びでの前後のアーカイブへ移る（v0.2.1）
  - マウスの戻るボタンで、ライブラリへ戻る（v0.2.6）
  - 設定で、前回ビューアで開いたまま終了したアーカイブを、次の起動で続きから開ける（v0.2.6。既定はオフ）
- **画像の削除**
  - 不要な画像を「削除予約」し、アーカイブを離れるときに、まとめて ZIP から取り除く
  - 更新前の ZIP は Windows のごみ箱へ退避する。新しい ZIP を作って確かめてから置き換えるため、途中で失敗しても元の ZIP は壊れない

- **RAR / 7z（CBR / CB7）**（v0.2.3）
  - 閲覧（スキャン、サムネイル、ビューア）。形式は、ファイルの中身で判定する
  - ZIP への変換（右クリックメニュー、またはビューアのツールバーから）。閲覧位置とサムネイルを引き継ぎ、元のファイルはごみ箱へ
  - 画像の削除は、ZIP に変換してから
- **設定画面**（v0.2.0）。v0.2.5 から、「全般」「データ」「アプリ」の3つのタブに分け、関係する項目を枠で囲んだ
  - 外観（ダーク / ライト / Windowsの設定に合わせる）。再起動せずに、その場で切り替わる
  - 確認のオン/オフ（アーカイブから離れるとき、アーカイブを削除するとき）
  - サムネイルのキャッシュの大きさの表示と削除
- **ショートカット**（v0.2.5）: スタートメニューとデスクトップに、35Viewer のショートカットを作れる。はじめて起動したときに、作るかを一度だけ確認する（確認なしでは作らない）。設定画面からも、作る・作り直す・削除ができる。タスクバーには、スタートメニューの「35Viewer」を右クリックして「タスクバーにピン留めする」で置く
- **アンインストール**（v0.2.5）: 設定画面から、プログラム、データ、ショートカットを削除できる。アーカイブには、さわらない（上の「アンインストール」を参照）
- **バックアップと復元**（v0.2.0）: アプリのデータを1つのファイルに保存し、その時点に戻せる
- **アプリの更新**（v0.2.0）: 新しい版を確かめ、アプリの中で入れ替える。押したときだけ通信する

対応形式は ZIP / CBZ / RAR / CBR / 7z / CB7 の中の JPEG / PNG / GIF / WebP です（RAR / 7z は閲覧と ZIP への変換だけ。GIF は最初の1コマだけを静止画として扱います。WebP は、Windows の「WebP 画像拡張機能」がない環境では表示できません。暗号化・分割されたアーカイブ、AVIF / HEIC などは未対応）。

## 設定・バックアップ・更新

ライブラリ画面の左下の「⚙ 設定」で、設定画面を開きます（別のウィンドウです。変更は、その場で反映して保存します）。項目は、「全般」「データ」「アプリ」の3つのタブに分かれています（外観・確認・起動時は「全般」、保存場所・キャッシュ・バックアップと復元は「データ」、ショートカット・アプリの更新・アンインストール・情報は「アプリ」）。

### 設定

- **外観**: ダーク（既定）、ライト、Windowsの設定に合わせる
- **確認**: 「アーカイブから離れるとき、削除予約を反映するか確認する」と「アーカイブを削除するとき、確認する」を、それぞれオフにできます。オフにしても、更新できないときの確認と、ごみ箱を使えない場所での完全な削除の確認は、必ず表示します（元に戻せないため）
- **起動時**: 「前回開いていたアーカイブを、続きから開く」（既定はオフ）。オンにすると、ビューアでアーカイブを開いたまま終了したときだけ、次の起動で、そのアーカイブを最後に見ていたページから開きます。ウィンドウの位置と大きさ、一覧の並べ替えは、この設定によらず、常に前回のとおりに戻します
- **ショートカット**: スタートメニューとデスクトップに、35Viewer のショートカットを作ります。はじめて起動したときの確認で選ぶほか、設定画面の「アプリ」のタブからも、作る・作り直す・削除ができます（現在の利用者用の場所に作ります。管理者権限は使いません）。展開したフォルダを移したときは、状態に「リンク先が今の場所と違います」と出るので、「作り直す」で直せます。ショートカットは、アプリの更新のあとも、そのまま使えます。展開した場所が一時的な場所（ZIP の中から直接起動した、など）のときは、はじめての確認を出さず、設定画面から作るときは、確認します
- **データ**: 保存場所とサムネイルのキャッシュの場所を、エクスプローラーで開けます。キャッシュは、大きさを確かめて、削除できます（削除したサムネイルは、順に作り直します。アーカイブ自体は変わりません）。スキャン中とサムネイルの作成中は、削除できません

### バックアップと復元

- **バックアップ**: 「バックアップを作成」で、ライブラリの情報、閲覧位置、操作履歴、設定を、1つの ZIP にまとめます。保存先は `%LOCALAPPDATA%\K35Viewer\backups\` で、「フォルダを開く」から開けます。別の場所へコピーして保管することもできます。サムネイルのキャッシュとログは含みません（作り直せるため）。アーカイブ（画像）自体も含みません
- **自動バックアップ**: 復元と更新の前に、アプリが自動で作ります。新しいものから決まった数（既定は5。設定で1〜20）だけ残して、古いものを削除します。手動で作ったものは、削除しません
- **復元**: 「復元…」でバックアップを選ぶと、確認のあと、今のデータを自動でバックアップして、アプリを再起動し、その時点のデータに戻します。サムネイルは作り直します。今のアプリより新しい版で作ったバックアップは、復元できません。入れ替えに失敗した場合は、元のデータで起動します

### 更新

- 「更新を確認」で、GitHub のリリースに新しい版があるかを確かめます（試用版を含みます）。起動時に、自動では確かめません
- 「更新する」で、ダウンロード、ハッシュ（SHA-256）の確認、データのバックアップのあと、アプリを再起動して入れ替えます。入れ替えに失敗した場合は、元の版に戻します
- アプリのフォルダに書き込めない場合（`C:\Program Files` の中など）や、ダウンロード・確認に失敗した場合は、入れ替えません。「変更点を見る」から、リリースのページを開いて、手で入れ替えてください

In English: the settings window (bottom left of the library screen) lets you choose the appearance, turn the two confirmations on or off, and clear the thumbnail cache. "Create backup" bundles the library data, reading positions, operation history and settings into one ZIP in `%LOCALAPPDATA%\K35Viewer\backups\`; "Restore…" brings it back after a restart (a safety backup of the current data is made first). "Check for updates" / "Update" talk to GitHub only when pressed, verify the download with SHA-256, back up your data, and roll back if the swap fails.

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

ライブラリの情報（SQLite の DB）、設定（`settings.json`）、バックアップ（`backups\`）、サムネイル（`thumbnails\`）、ログ（`logs\`）は、`%LOCALAPPDATA%\K35Viewer\` に保存します。
アーカイブ自体は、削除予約を確定したとき、または名前変更・移動・削除を行ったときだけ変更します。バックアップに、アーカイブ（画像）は含みません。

## 主なキー操作

| 画面 | 操作 | キー |
|---|---|---|
| ライブラリ | 開く / 名前変更 / 削除 | Enter / F2 / Delete |
| ビューア | 前の画像 / 次の画像 | ← / →（PageUp / PageDown、Space でも可） |
| ビューア | 前のアーカイブ / 次のアーカイブ（ライブラリの一覧の並びで、一覧に戻らずに移る） | ↑ / ↓（一覧表示では Ctrl+↑ / Ctrl+↓） |
| ビューア | 先頭 / 末尾 | Home / End |
| ビューア | 拡大 / 縮小 / ウィンドウに合わせる / 原寸 | + / - / 0 / 1 |
| ビューア | 削除予約（もう一度押すと解除） | Delete |
| ビューア | 1枚表示と一覧表示の切替 | G |
| ビューア | 全画面 | F11 |
| ビューア | ライブラリへ戻る | Esc、マウスの戻るボタン |

## 設計書

設計書は [docs](docs/) にあります（日本語）。

| 文書 | 内容 |
|---|---|
| 要件定義 | 目的、機能の要件、MVP の範囲、v0.2.0 で追加した機能 |
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

35Viewer は、利用者が操作しないかぎり、ネットワークを通じて情報を送信しません（利用状況の収集や、起動時の自動の更新確認はありません）。通信するのは、設定画面で「更新を確認」「更新する」を押したときだけです。このとき、新しい版を調べて入手するために、GitHub（`api.github.com`、`github.com`）へ通常の HTTPS の要求を送ります。利用者のデータは送りません。読み書きするのは、利用者が登録したフォルダ（NAS などのネットワーク上のフォルダを含む）の中のファイルと、`%LOCALAPPDATA%\K35Viewer\` に保存する自身のデータ（DB、設定、バックアップ、サムネイル、ログ）だけです。

## 行動規範

このプロジェクトに参加する方は、[行動規範（Contributor Covenant）](CODE_OF_CONDUCT.md) に従ってください。

## ライセンス
[MIT License](LICENSE)

35Viewer は、次のオープンソースの部品を使っています（配布物にも含まれます）。.NET ランタイムと WPF、Microsoft.Extensions、Microsoft.Data.Sqlite、CommunityToolkit.Mvvm、SharpCompress（MIT License）、Serilog、SQLitePCLRaw（Apache License 2.0）、SQLite（パブリックドメイン）。それぞれの著作権とライセンスの全文は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) にあります。配布物（ZIP）では、同じフォルダの `LICENSE.txt`、`THIRD-PARTY-NOTICES.txt` と、`licenses` フォルダの中にあります。

35Viewer uses the open-source components listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which is also included in the release ZIP together with `LICENSE.txt` and the `licenses` folder.
