# 画像アーカイブ管理ビューア 内部アーキテクチャ設計書

**文書バージョン:** Ver.0.2.1  
**作成日:** 2026-11-06  
**更新日:** 2026-11-07  
**対象:** MVP

## 1. 目的

本書は、画像アーカイブ管理ビューアのMVPを実装するための内部アーキテクチャを定義する。

主な目的は次のとおり。

- UIとファイル操作を分離する
- ZIP更新、SQLite、サムネイル生成等の責務を明確にする
- 長時間処理をバックグラウンドで安全に実行する
- ファイル操作成功後にのみDBを更新する
- 将来の追加形式対応や画面追加を妨げない構造とする
- MVPで必要以上の抽象化を行わない

## 2. 前提技術

- 言語: C#
- ランタイム: .NET 10
- UI: WPF
- DB: SQLite
- アーカイブ: ZIP / CBZ
- 画像: JPEG / PNG
- ZIP基本操作: `System.IO.Compression`
- 非同期処理: `async` / `await` とキャンセルトークンを基本とする

## 3. 全体構成

MVPでは、論理的に次の4層へ分離する。

```text
Presentation
  Library / Viewer / Dialog / Toast
          |
          v
Application
  Use Case / Service / Session
          |
          v
Domain
  Archive / Folder / OperationResult 等
          |
          v
Infrastructure
  SQLite / ZIP / FileSystem / RecycleBin / ThumbnailCache
```

依存方向は原則として上から下とする。

UIからSQLiteやZIPを直接操作しない。

## 4. Presentation層

### 4.1 役割

- ライブラリ画面の表示とユーザー操作受付
- ビューアの1枚表示・一覧表示
- 確認ダイアログ
- トースト通知
- ViewModelと画面状態の管理

### 4.2 View

MVPでは主に次を持つ。

- `MainWindow`
- `LibraryView`
- `ViewerView`
- `ArchiveChangeConfirmDialog`
- `ArchiveDeleteConfirmDialog`
- `ToastHost`

ライブラリとビューアは別ウィンドウではなく、`MainWindow` 内で切り替える。

### 4.3 ViewModel

#### MainViewModel

- 現在表示する画面の切替
- 全体的なナビゲーション
- 長時間処理状態の連携

#### LibraryViewModel

- フォルダツリー
- アーカイブ一覧
- 検索条件
- ソート条件
- 表示モード
- フォルダ追加
- 再スキャン
- アーカイブの移動、名前変更、削除

#### ViewerViewModel

- 現在のArchive ID
- 現在画像
- 画像一覧
- 現在位置
- 1枚表示 / 一覧表示切替
- 選択状態
- 削除予約
- 拡大・縮小等の表示状態

### 4.4 UIルール

- ViewModelから直接 `File.*`、`Directory.*`、SQLite等を呼ばない。
- ユーザー操作はApplication層のサービスへ依頼する。
- ViewModelは処理結果 `OperationResult` を受け取り、画面状態へ反映する。
- トースト表示は共通の通知サービス経由とする。

## 5. Application層

画面から呼ばれるユースケースを実装する。

### 5.1 LibraryService

ライブラリに関する操作の窓口。

主な責務:

- 登録フォルダ追加
- フォルダツリー取得
- アーカイブ一覧検索
- 検索・ソート条件に応じた一覧取得
- アーカイブを開くための情報取得

想定インターフェース例:

```csharp
Task AddFolderAsync(string path, CancellationToken ct);
Task<IReadOnlyList<FolderNode>> GetFolderTreeAsync(CancellationToken ct);
Task<PagedResult<ArchiveSummary>> QueryArchivesAsync(
    ArchiveQuery query,
    CancellationToken ct);
```

### 5.2 ScanService

フォルダスキャンと差分更新を担当する。

主な責務:

- 初回スキャン
- 手動再スキャン
- 起動時更新チェック
- 登録フォルダ、サブフォルダ、ZIP / CBZの列挙
- Path、Size、LastWriteTimeを用いた差分判定
- 新規・変更・不在項目の反映
- `scan_runs` の開始・終了記録
- 差分があるアーカイブの解析要求

スキャン中でもライブラリの読み取り操作を可能とする。

### 5.3 ArchiveService

アーカイブ自体のファイル管理を担当する。

主な責務:

- 名前変更
- 移動
- 削除
- ZIP再構築
- 元ファイルの再確認
- ファイル操作成功後のDB更新
- 操作履歴作成

ZIP内部の具体的な読み書きはInfrastructure層へ委譲する。

### 5.4 ViewerService

アーカイブ内部の閲覧に必要な処理を担当する。

主な責務:

- ZIP内画像エントリ一覧取得
- 自然順ソート
- JPEG / PNGの読み込み
- 最終閲覧位置の取得・保存
- 前後画像取得

### 5.5 ViewerSession

現在開いているアーカイブの未確定状態をメモリ上で保持する。

主な状態:

```text
ArchiveId
Entries
CurrentEntry
CurrentIndex
SelectedEntries
PendingDeleteEntries
ViewMode
```

`PendingDeleteEntries` はDBへ保存しない。

アーカイブ離脱時にのみ確定または破棄する。

### 5.6 ThumbnailService

サムネイル生成・取得を担当する。

主な責務:

- ライブラリ用表紙サムネイル取得
- キャッシュ有効性確認
- サムネイル生成
- `STALE` サムネイル再生成
- キャッシュファイル消失時の再生成

一覧スクロールをブロックしないよう、生成はバックグラウンドで実行する。

### 5.7 HistoryService

操作結果を `operation_history` へ記録する。

MVPでは履歴閲覧画面を持たないが、履歴記録は実装する。

開始通知は履歴へ保存せず、確定した最終結果のみ保存する。

### 5.8 NotificationService

アプリ内トースト通知の共通窓口。

```csharp
ShowInfo(message);
ShowSuccess(message);
ShowWarning(message);
ShowError(message);
```

スキャンやZIP更新など時間を要する処理は、開始時と完了時に通知する。

## 6. Domain層

純粋なデータ構造とルールを保持する。

### 6.1 主なモデル

#### LibraryFolder

- Id
- Path
- IsEnabled
- AvailabilityStatus

#### Folder

- Id
- LibraryFolderId
- ParentFolderId
- Name
- FullPath
- RelativePath
- Depth

#### Archive

- Id
- FolderId
- FileName
- Extension
- FileSize
- LastWriteTime
- ImageCount
- LastViewEntry
- LastViewIndex
- AnalysisStatus
- AvailabilityStatus

アーカイブの実パスは `Folder.FullPath + Archive.FileName` から生成する。

#### ArchiveEntry

ZIP内の1エントリを表す。

- Name
- FullName
- Size
- CompressedSize
- LastWriteTime
- IsSupportedImage

#### OperationResult

ファイル操作の結果を共通表現する。

```text
OperationType
Status
ArchiveId
Message
AffectedCount
OldPath
NewPath
ErrorCode
ExecutedAt
```

同じ結果をトースト表示と操作履歴記録に利用する。

`OperationResult` はユーザー向けの確定結果を表し、トースト通知と `operation_history` の共通データとして使用する。例外、スタックトレース、一時ファイルパス、内部処理段階等の開発・障害調査情報は `OperationResult` へ詰め込まず、アプリケーションログへ記録する。

### 6.2 時刻

Unix timeを使用する場合は64bit整数とし、C#では `long` (`Int64`) を使用する。

`int` (`Int32`) へ変換しない。

## 7. Infrastructure層

## 7.1 SQLiteRepository

SQLiteへの永続化を担当する。

Repositoryはテーブル単位に過剰分割せず、MVPでは用途別に次を基本とする。

- `LibraryRepository`
- `ArchiveRepository`
- `HistoryRepository`
- `ScanRepository`
- `ThumbnailRepository`

DB接続設定:

```text
foreign_keys = ON
journal_mode = WAL
synchronous = NORMAL
busy_timeout = 5000
```

書き込み処理はアプリ内で1本の書き込みキューへ集約する。

### 7.2 ArchiveReader

ZIP / CBZの読み取りを担当する。

MVPでは `System.IO.Compression.ZipArchive` を利用する。

主な機能:

- エントリ列挙
- JPEG / PNG判定
- エントリStream取得
- アーカイブ検証

### 7.3 ArchiveWriter

ZIP再構築を担当する。

主な責務:

1. 一時ZIP作成
2. 削除対象以外のエントリをコピー
3. 一時ZIPを閉じる
4. 再オープンして読み取り確認
5. 成否をApplication層へ返す

元ZIPの退避・置換判断は `ArchiveService` が統括する。

### 7.4 FileSystemService

Windowsファイルシステム操作を集約する。

主な機能:

- ファイル・フォルダ存在確認
- ディレクトリ列挙
- ファイル情報取得
- 名前変更
- 移動
- エクスプローラーで対象表示
- 一時ファイル名生成

ViewModelやRepositoryから直接ファイル操作しない。

### 7.5 RecycleBinService

Windowsのごみ箱への移動を担当する。

用途:

- アーカイブ削除
- ZIP再構築時の更新前ZIP退避

ごみ箱を利用できない保存先の場合は `RECYCLE_BIN_UNAVAILABLE` を返し、自動的な完全削除へは切り替えない。

- アーカイブ削除：UIが確認ダイアログを表示し、ユーザーが承認した場合のみ、`ArchiveService` が完全削除を明示的に要求する。
- ZIP再構築：更新を中止し、元ZIPと削除予約を保持する。

### 7.6 ThumbnailCacheStore

サムネイルファイルの保存・取得・削除を担当する。

DBの `thumbnail_cache` はメタデータを保持し、画像実体はキャッシュディレクトリへ保存する。

## 8. 依存関係

代表的な処理の依存関係は次のとおり。

```text
LibraryViewModel
    -> LibraryService
        -> LibraryRepository
        -> ScanService

ViewerViewModel
    -> ViewerService
        -> ArchiveReader
        -> ArchiveRepository

ViewerViewModel
    -> ArchiveService
        -> ArchiveWriter
        -> FileSystemService
        -> RecycleBinService
        -> ArchiveRepository
        -> HistoryService
        -> NotificationService
```

`ArchiveWriter` からSQLiteを操作しない。

`ArchiveRepository` からファイルを操作しない。

## 9. ZIP閲覧フロー

```text
ユーザーがアーカイブを開く
        |
        v
LibraryViewModel
        |
        v
ViewerService
        |
        +--> ArchiveRepository からArchive取得
        |
        +--> Folder情報から実パス生成
        |
        +--> ArchiveReaderで画像一覧取得
        |
        v
ViewerSession生成
        |
        v
ViewerViewModelへ表示
```

画像データは全件を同時展開せず、現在画像と前後の必要数のみ読み込む。

## 10. 削除予約フロー

```text
Delete
  |
  v
ViewerViewModel
  |
  v
ViewerSession.PendingDeleteEntriesへ追加
  |
  +--> 画面から通常画像として除外
  |
  +--> 削除予定件数を更新
```

この時点ではZIP、SQLite、操作履歴を変更しない。

一覧表示で複数選択された場合も同じ `PendingDeleteEntries` を利用する。

## 11. アーカイブ更新フロー

アーカイブ離脱時に削除予約が存在し、「更新する」が選択された場合の処理。

```text
ViewerViewModel
    |
    v
ArchiveService
    |
    +--> 更新開始トースト
    |
    +--> 元ファイルの存在・Size・LastWriteTime確認
    |
    +--> ArchiveWriterで一時ZIP作成
    |
    +--> 一時ZIP検証
    |
    +--> RecycleBinServiceで元ZIP退避
    |
    +--> FileSystemServiceで一時ZIPを正規名へ変更
    |
    +--> DB書き込みトランザクション
    |      + archives更新
    |      + thumbnail_cacheをSTALE化
    |      + operation_history追加
    |
    +--> 完了トースト
    |
    +--> ThumbnailServiceへ再生成要求
    v
離脱を継続
```

### 11.1 失敗時

- 元ZIPをごみ箱へ移す前の失敗では元ZIPを保持する。
- 一時ZIPは可能なら削除する。
- 元ZIP退避後の失敗は復旧に必要なパスをエラー結果へ含める。
- DB更新前に実ファイル操作が失敗した場合、DBは更新しない。
- DB更新のみ失敗した場合、次回スキャンにより実ファイルから再同期可能とする。

## 12. スキャンフロー

```text
起動 / フォルダ追加 / 再スキャン
        |
        v
ScanService
        |
        +--> 開始トースト
        +--> scan_runs作成
        +--> FileSystemServiceで再帰列挙
        +--> LibraryRepositoryと差分比較
        +--> 新規Archive登録
        +--> 変更Archive解析
        +--> 不在判定
        +--> Folderツリー更新
        +--> scan_runs完了
        +--> operation_history記録
        +--> 完了トースト
```

起動時チェックはライブラリ画面表示後に開始し、通常の閲覧・検索をブロックしない。

## 13. 並行処理

MVPでは複雑な並列実行を避け、次のルールとする。

### 13.1 DB

- 読み取りは必要に応じて並行可能
- 書き込みは単一キューへ直列化

### 13.2 同一アーカイブ

同じArchive IDに対する破壊的操作は同時実行しない。

例:

- 再構築中の名前変更は禁止
- 移動中の削除は禁止
- 更新中の再更新は禁止

### 13.3 スキャンとの競合

MVPでは高度なロック、世代管理、競合解決機構は実装しない。

スキャン結果をSQLiteへ反映する直前に、対象ファイルの存在、Size、LastWriteTimeを再取得する。スキャン開始時または解析時に取得した値と異なる場合、そのファイルのスキャン結果は破棄し、次回の起動時更新チェックまたは再スキャンの対象とする。

アプリ内のファイル操作が成功した場合は、その操作結果によるDB更新を優先する。スキャン側は確認済みの古い情報で上書きしない。

## 14. キャンセル方針

### キャンセル可能

- フォルダスキャン
- 起動時差分チェック
- サムネイル生成待ち

### ZIP更新のキャンセル境界

次の処理まではキャンセル可能とする。

- 一時ZIP生成
- 一時ZIPの検証

元ZIPをごみ箱へ退避する処理を開始した時点を安全境界とし、それ以降はキャンセル不可とする。

安全境界を越えた後は、新ZIPへの置換、DB更新、履歴記録まで処理を最後まで完了させる。これにより、元ZIPのみがごみ箱にあり、一時ZIPだけが残る中途半端な状態を避ける。

## 15. エラー処理

Application層のサービスは、ユーザー操作で想定される失敗を例外のままUIへ投げず、原則 `OperationResult` 等へ変換する。

例:

- `FILE_NOT_FOUND`
- `ACCESS_DENIED`
- `DISK_FULL`
- `ARCHIVE_BROKEN`
- `ARCHIVE_CHANGED_EXTERNALLY`
- `RECYCLE_BIN_UNAVAILABLE`
- `TARGET_ALREADY_EXISTS`
- `DATABASE_ERROR`

予期しない例外は共通例外ハンドラで捕捉し、ログへ記録する。

## 16. ログ

操作履歴と開発・障害調査ログは分離する。

### 操作履歴

ユーザーが後日確認する対象。

- ZIP更新
- 名前変更
- 移動
- 削除
- スキャン最終結果

SQLiteの `operation_history` へ保存する。

### アプリケーションログ

障害調査用。

- 例外
- スタックトレース
- SQLiteエラー
- ZIP処理エラー
- スキャン詳細

アプリケーションログの記録はMVPから実装し、ローカルログファイルへ保存する。ログ閲覧画面はMVP対象外とし、将来仕様とする。

最低限、日時、ログレベル、処理名、Archive ID、対象パス、エラーコード、例外メッセージ、スタックトレースを記録できる構造とする。保存形式、保存期間、最大ファイルサイズおよびローテーション方式は実装設計で確定する。

## 17. DIと生成管理

各Service、Repository、Infrastructure依存はコンストラクタ注入を基本とする。

例:

```csharp
ArchiveService(
    IArchiveWriter archiveWriter,
    IFileSystemService fileSystem,
    IRecycleBinService recycleBin,
    IArchiveRepository archiveRepository,
    IHistoryService historyService)
```

ただしMVPではインターフェースを無制限に作らず、次の条件に該当する境界を優先して抽象化する。

- OSや外部資源に依存する
- 単体テストで置き換えたい
- 将来実装を差し替える可能性が高い

例:

- FileSystem
- RecycleBin
- ZIP Reader / Writer
- SQLite Repository
- Notification

## 18. ソリューション構成案

MVPではプロジェクトを細分化しすぎず、まず2プロジェクト構成を推奨する。

```text
K35Viewer.sln

K35Viewer.App
  Views/
  ViewModels/
  Services/
  Domain/
  Infrastructure/

K35Viewer.Tests
  Services/
  Infrastructure/
```

規模が大きくなった時点で、必要に応じてCore / Infrastructure等へ分割する。

フォルダ分離だけでも層の依存関係は維持する。

## 19. MVPのテスト重点箇所

特に次を自動テストまたは結合テスト対象とする。

1. ZIP内画像の自然順ソート
2. JPEG / PNGエントリ抽出
3. 削除予約状態の追加・解除
4. 複数削除を1回のZIP再構築で反映
5. 再構築失敗時に元ZIPが残ること
6. 外部変更されたZIPを上書きしないこと
7. 名前変更後もArchive IDが維持されること
8. フォルダ移動後も配下Archiveのパスを正しく組み立てられること
9. 起動時差分チェックでNASアクセス不能を削除扱いしないこと
10. SQLiteの64bit時刻が2038年以降でも扱えること

## 20. MVP対象外

- RAR / 7z
- WebP / GIF
- PDF
- 動画
- Susie Plug-in
- 操作履歴閲覧画面
- 設定画面
- 更新確定後のアプリ独自Undo
- 高度な重複検出
- ファイル内容による高度な同一性追跡
- 複数端末間のDB共有

## 21. レビュー結果

1. `ViewerService` と `ArchiveService` の責務境界は現案を採用する。
2. `ViewerSession` はViewModel内部ではなくApplication層の状態として保持する。
3. ZIP再構築時の元ZIP退避と新ZIP置換は `ArchiveService` が統括する。
4. DB書き込みは単一キューへ集約し、直列化する。
5. スキャンとアプリ内操作の競合は、MVPではSizeとLastWriteTimeの書き込み直前再確認による簡易対応とする。高度なロックや世代管理は実装しない。
6. サムネイル再生成はZIP更新完了後に非同期実行する。
7. アプリケーションログの記録はMVPから実装する。ログ閲覧画面は将来仕様とする。
8. 2プロジェクト構成で開始し、必要になるまでCore / Infrastructureを別プロジェクト化しない。
9. `OperationResult` はトースト通知と操作履歴の共通データとして使用する。開発・障害調査情報はアプリケーションログへ分離する。
10. ZIP更新は、一時ZIP生成および検証まではキャンセル可能とし、元ZIPのごみ箱退避開始後はキャンセル不可とする。

## 22. Ver.0.2で確定したMVP方針

- 層およびサービスの責務境界はVer.0.1の案を採用する。
- スキャン競合は簡易再確認方式とし、過剰な競合制御を実装しない。
- 操作履歴とアプリケーションログを分離し、両方の記録をMVPから実装する。
- ログ閲覧UIは将来実装とする。
- ZIP更新処理には明確なキャンセル安全境界を設ける。

## 23. Ver.0.2.1で確定した実装方針（Phase 2 フォルダ追加・スキャン）

実装を通じて、次の方針を確定した。上記の各章と異なる場合は、本章を優先する。

### 23.1 DB書き込みキュー

- 書き込みは `DatabaseWriter` を必ず経由し、アプリ内で1つずつ（直列に）実行する。1回の呼び出しが1トランザクションになり、例外時はロールバックする
- 読み取りは `SqliteConnectionFactory` から各自で接続を開いて並行に行う（WALのため書き込みと衝突しない）
- Repositoryの書き込みメソッドは、呼び出し元のトランザクション内で実行する。複数テーブルにまたがる更新（管理フォルダ追加時のフォルダ統合等）を1トランザクションにまとめるため

### 23.2 LibraryServiceとScanServiceの関係

8章の依存関係図では `LibraryService` が `ScanService` を呼ぶ構成だったが、次のとおり変更する。

- `LibraryService.AddFolderAsync` は登録（と親フォルダ追加時の統合）だけを行い、スキャンは実行しない
- 登録に成功（`Added`）した場合、呼び出し側（`LibraryViewModel`）が `ScanService` を起動する。スキャンを待たずにUIを操作できる状態にするため、また登録とスキャンを個別にテストできるようにするため

### 23.3 スキャンの2段階処理

`ScanService` は次の2段階で処理する。詳細な反映規則はDB設計書 11.7を参照。

1. ファイル列挙: `FileSystemService` が書き込みなしで実行する。キャンセル可能
2. 差分反映: 書き込みキュー経由の1トランザクション。`scan_runs` の完了も同じトランザクションで行う

### 23.4 アクセスできない範囲の扱い

13.3の「書き込み直前の簡易再確認」に加え、次を原則とする（テスト重点箇所9の具体化）。

- 読み取れなかったフォルダと、その配下の既存登録は、不在判定の対象外とする
- 登録フォルダ自体にアクセスできない場合は、配下を変更せずスキャンを失敗として終了する
- 再確認（サイズ・更新日時）は、新規・変更として書き込むファイルだけを対象とする（変更のないファイルは再取得しない）

### 23.5 スキャンに含めない処理

スキャンは、ZIPの内容を読む処理（画像枚数、表紙画像の特定、サムネイル生成）を含まない。新規・変更のアーカイブは `analysis_status = PENDING` で登録し、解析とサムネイル生成はPhase 4以降で行う。

### 23.6 通知・履歴

- `NotificationService` はイベントで通知を発行する。バックグラウンドスレッドから呼ばれるため、購読側（トースト表示）でUIスレッドへ切り替える
- スキャンの最終結果は、キャンセル・失敗の場合も `operation_history` へ記録する。このときの書き込みは、呼び出し元のキャンセルトークンに従わない（キャンセル済みでも履歴を残すため）
- 履歴の `operation_type` は、スキャンの種別（`INITIAL` / `MANUAL` / `STARTUP`）に応じて `SCAN_INITIAL` / `SCAN_MANUAL` / `SCAN_STARTUP` とする
