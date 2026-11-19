# 画像アーカイブ管理ビューア DB設計書

**文書バージョン:** Ver.0.2.14  
**作成日:** 2026-11-06  
**更新日:** 2026-11-19  
**対象:** MVP  
**DBMS:** SQLite

## 1. 修正内容
### Ver.0.2.14（最近開いたアーカイブに伴う変更。GitHub の Issue #27）
- `archives` に、`last_opened_at` 列（最後に開いた日時）を加えた。6.3を更新
- 5番目のマイグレーション（`0005_archive_last_opened.sql`）で、列を足し、既存の行には `last_viewed_at` を写す。`schema_version` は5になる。13.4を追加
- 一覧の検索クエリ（11.8）に、「最近開いたものだけ」の取得と、`last_opened_at` の取得を加えた
- 終了時の状態の復元（GitHub の Issue #26）は、設定のファイルに持ち、DB は変えない
### Ver.0.2.13（新着の表示に伴う変更。GitHub の Issue #16）
- `archives` に、`is_new` 列（0 / 1。「新着」の印）を加えた。6.3を更新
- 4番目のマイグレーション（`0004_archive_is_new.sql`）で、列を足す。既存の行は 0。`schema_version` は4になる。13.3を追加
- 一覧の検索クエリ（11.8）に、「新着だけ」の絞り込みと、`is_new` の取得を加えた
### Ver.0.2.12（GIF / WebP の対応に伴う変更。GitHub の Issue #2）
- 対応画像の拡張子に、`.gif` と `.webp` を加えた。6.3を更新
- 3番目のマイグレーション（`0003_requeue_empty_archives.sql`）で、`EMPTY` のアーカイブを `PENDING` に戻し、解析し直す。表の定義は変えない。`schema_version` は3になる。13章を更新
### Ver.0.2.11（RAR / 7z の ZIP への変換。GitHub の Issue #20）
- 操作履歴の種別に `ARCHIVE_CONVERT` を追加した（`operation_type` にはCHECKがないため、マイグレーションは要らない）。6.5を更新
- 変換のDB反映（11.10）を追加した。`schema_version` は2のまま
### Ver.0.2.10（RAR / 7z の閲覧に伴う変更。GitHub の Issue #3）
- `archives.extension` に、`rar`、`cbr`、`7z`、`cb7` を許した。6.3を更新
- 初期DDL（8章）は変更せず、2番目のマイグレーション（`0002_archive_formats.sql`）で表を作り直す。`schema_version` は2になる。13章を追加
### Ver.0.2.9（Phase 9 実装に伴う確定事項）

- 名前変更・移動・削除・管理フォルダの解除のDB反映を確定した。11.1、11.2、11.4、11.5を更新
- スキャンが、列挙のあとに行われたアプリ内の名前変更・移動で記録が変わったアーカイブを、古い列挙結果で「不在」にしないようにした。11.7を更新
- 初期DDLは変更していない（`schema_version` は1のまま）

### Ver.0.2.8（Phase 8 実装に伴う確定事項）

- ZIP再構築（画像の削除）をDBへ反映する規則を確定した。11.3を更新。`archives` のサイズ・更新日時・画像枚数・閲覧位置を更新し、解析待ち（`PENDING`）に戻し、サムネイルを再生成待ち（`STALE`）にし、`IMAGE_DELETE` の操作履歴を追加する（1トランザクション）
- 置き換えの途中で失敗した場合の `IMAGE_DELETE`（`FAILED`）の記録内容を確定した。`new_path` に、更新後のファイルが残っている一時ファイルのパスを入れる。`error_code` は `REPLACE_FAILED`
- 初期DDLは変更していない（`schema_version` は1のまま）

### Ver.0.2.7（Phase 6 実装に伴う確定事項）

- 解析で一時的なI/Oエラー（`IO_ERROR`、`ACCESS_DENIED`）が起きた場合は、その場で短い間隔（200ms、800ms）を空けて最大2回再試行してから `ERROR` とする。11.9を更新。壊れている・暗号化されている・存在しない場合は再試行しない
- 初期DDLは変更していない（`schema_version` は1のまま）

### Ver.0.2.6（Phase 5 実装に伴う確定事項）

- 最後に閲覧した位置（`last_view_entry`、`last_view_index`、`last_viewed_at`）の意味、復元の規則、記録のタイミングを確定した。6.3を更新
- 初期DDLは変更していない（`schema_version` は1のまま）

### Ver.0.2.5（Phase 4 実装に伴う確定事項）

- `thumbnail_cache` のキャッシュキー、ファイルの相対パス、状態の意味を確定した。6.4を更新
- アーカイブの解析（`analysis_status`、画像枚数、表紙）の規則を確定した。6.3を更新
- 解析・サムネイル生成の対象選定、DB反映、キャッシュの無効化と再生成の規則を11.9として追加した
- サムネイルの形式を JPEG 固定（透過は白背景に合成）とした。12章のレビュー事項3を更新
- 初期DDLは変更していない（`schema_version` は1のまま）

### Ver.0.2.4（Phase 3 実装に伴う確定事項）

- 全登録フォルダをまとめたスキャン（再スキャン・起動時チェック）の記録方法を確定した（`scan_runs.library_folder_id` を `NULL`、履歴は1回分）。11.7を更新
- 一覧の検索クエリの方針（フォルダ指定、ファイル名検索、並べ替え、既知の制限）を11.8として追加した
- 初期DDLは変更していない（`schema_version` は1のまま）

### Ver.0.2.3（Phase 2 実装に伴う確定事項）

- `normalized_path` の正規化規則を確定した（フルパス化、末尾の区切り除去、大文字化）。6.1、6.2を更新
- 不在アーカイブは物理削除せず `MISSING` として保持する方針を暫定確定した。12章のレビュー事項1を更新
- スキャン結果をDBへ反映する際の規則（アクセスできなかった範囲を不在にしない、`scan_runs` の閉じ方等）を11.7として追加した
- 初期DDLは変更していない（`schema_version` は1のまま）

### Ver.0.2.2

Ver.0.2の初期DDLにあったスキーマ漏れを修正した。

- `folders.full_path` を初期DDLへ追加
- `folders.normalized_path` を初期DDLへ追加
- `ux_folders_normalized_path` が正常に作成できることを確認
- 操作履歴の画像削除種別を、ユーザー操作を表す `IMAGE_DELETE` に統一
- ZIP再構築は画像削除を実現する内部処理として扱い、`ARCHIVE_REBUILD` を操作履歴種別に使用しない
- `archives` には `full_path` と `normalized_path` を持たせない方針を維持
- アーカイブの実パスは `folders.full_path` と `archives.file_name` から組み立てる

## 2. 目的

本書は、画像アーカイブ管理ビューアのMVPで使用するSQLiteデータベースの論理設計を定義する。

主な目的は次のとおり。

- 複数の登録フォルダとサブフォルダを管理する
- 10,000件以上のZIP / CBZを高速に一覧表示する
- アーカイブの移動・名前変更後も同一のArchive IDを維持する
- サムネイルキャッシュとアーカイブを関連付ける
- 起動時および手動スキャン時の差分判定に必要な情報を保持する
- ファイル変更操作の履歴を保存する

## 3. 設計方針

### 3.1 SQLiteを一覧表示の基準とする

ライブラリ画面の通常表示ではファイルシステムやZIP内部を毎回取得せず、SQLiteに保存した情報を使用する。

### 3.2 実ファイルを正とする

SQLiteは実ファイルの索引であり、ZIP / CBZ本体の代替ではない。ファイル操作に失敗した場合は、SQLiteだけを先に更新しない。

### 3.3 アーカイブIDはパスから独立させる

アーカイブには自動採番の内部IDを割り当てる。アプリ内で名前変更、移動、内容更新を行っても同じIDを維持する。

### 3.4 パスの責務

- `library_folders` はユーザーが追加したフォルダのパスを保持する
- `folders` は各実フォルダの `full_path` と `normalized_path` を保持する
- `archives` は `folder_id` と `file_name` を保持する
- アーカイブの実パスは `folders.full_path + archives.file_name` から生成する

### 3.5 サムネイル実体はDBへ格納しない

画像実体はローカルキャッシュへ保存し、SQLiteにはキャッシュキー、生成状態、更新日時等のみ保存する。

### 3.6 時刻

日時はUTCのUnix timeをSQLiteの `INTEGER` として保存する。C#側では必ず `long`、つまり `Int64` として扱い、`Int32` へ変換しない。これにより32bit Unix timeに起因する2038年問題を回避する。

## 4. 論理構成

```text
library_folders
    1
    └── N folders
            1
            └── N archives
                    1
                    ├── 0..1 thumbnail_cache
                    └── N operation_history

scan_runs
    └── スキャン単位の実行結果
```

## 5. テーブル一覧

| テーブル | 用途 |
|---|---|
| `library_folders` | ユーザーが追加した管理対象フォルダ |
| `folders` | サブフォルダを含むフォルダツリー |
| `archives` | ZIP / CBZの索引と閲覧情報 |
| `thumbnail_cache` | サムネイルキャッシュの管理情報 |
| `operation_history` | ファイル変更操作の履歴 |
| `scan_runs` | 初回・手動・起動時スキャンの実行結果 |
| `app_metadata` | DBスキーマバージョン等の内部情報 |

## 6. テーブル定義

### 6.1 library_folders

| カラム | SQLite型 | NULL | 内容 |
|---|---|---:|---|
| `id` | INTEGER | NO | 主キー、自動採番 |
| `path` | TEXT | NO | 登録フォルダの絶対パス |
| `normalized_path` | TEXT | NO | 比較用に正規化したパス（正規化規則は6.2の注記を参照） |
| `display_name` | TEXT | YES | 画面表示名 |
| `is_network` | INTEGER | NO | ネットワークパスなら1 |
| `is_enabled` | INTEGER | NO | スキャン対象なら1。既定値1 |
| `availability_status` | TEXT | NO | `AVAILABLE` / `UNAVAILABLE` / `UNKNOWN` |
| `last_scan_at` | INTEGER | YES | 最終スキャン完了日時 UTC |
| `created_at` | INTEGER | NO | 登録日時 UTC |
| `updated_at` | INTEGER | NO | 更新日時 UTC |

`is_enabled = 0` は、登録を残したまま自動・手動スキャン対象外とする将来用途に使用する。

### 6.2 folders

| カラム | SQLite型 | NULL | 内容 |
|---|---|---:|---|
| `id` | INTEGER | NO | 主キー、自動採番 |
| `library_folder_id` | INTEGER | NO | 所属する登録フォルダID |
| `parent_folder_id` | INTEGER | YES | 親フォルダID |
| `name` | TEXT | NO | フォルダ名 |
| `full_path` | TEXT | NO | 実フォルダの絶対パス |
| `normalized_path` | TEXT | NO | 比較用に正規化した絶対パス（正規化規則は下記参照） |
| `relative_path` | TEXT | NO | 登録フォルダからの相対パス |
| `depth` | INTEGER | NO | 登録フォルダを0とする深度 |
| `availability_status` | TEXT | NO | `AVAILABLE` / `UNAVAILABLE` / `UNKNOWN` |
| `last_seen_scan_id` | INTEGER | YES | 最後に存在確認したScan ID |
| `created_at` | INTEGER | NO | 登録日時 UTC |
| `updated_at` | INTEGER | NO | 更新日時 UTC |

`library_folders.normalized_path` と `folders.normalized_path` は同じ規則で正規化する。Windowsは大文字小文字を区別しないため、次の順で変換した値を保存する。

1. フルパスへ変換する（`..` や相対指定を解決する）
2. 末尾の区切り文字を除去する（`D:\` のようなドライブ直下・共有ルートは除く）
3. 大文字へ変換する（`ToUpperInvariant`）

例: `d:\archive\books\` → `D:\ARCHIVE\BOOKS`

配下判定は文字列の前方一致ではなく、フォルダ境界で行う（`D:\Books2` は `D:\Books` の配下ではない）。

制約:

- `normalized_path` は一意とする
- 登録フォルダ自体も `folders` に1件登録し、ツリーの起点とする
- `parent_folder_id` は同じ登録フォルダ内のフォルダを参照する

### 6.3 archives

| カラム | SQLite型 | NULL | 内容 |
|---|---|---:|---|
| `id` | INTEGER | NO | Archive ID、主キー |
| `folder_id` | INTEGER | NO | 現在格納されているフォルダID |
| `file_name` | TEXT | NO | 拡張子を含むファイル名 |
| `extension` | TEXT | NO | ファイル名の拡張子（小文字、先頭の点なし）。`zip`、`cbz`、`rar`、`cbr`、`7z`、`cb7` のいずれか（`rar` 以降は schema_version 2 から。13章）。中身の形式ではない（中身の形式は、開くときにファイルの先頭の署名で判定する） |
| `file_size` | INTEGER | NO | ファイルサイズ、バイト |
| `last_write_time` | INTEGER | NO | ファイル更新日時 UTC |
| `image_count` | INTEGER | YES | 対応画像数。未解析時はNULL |
| `first_image_entry` | TEXT | YES | 表紙候補のエントリ名 |
| `last_view_entry` | TEXT | YES | 最後に閲覧したエントリ名 |
| `last_view_index` | INTEGER | YES | 最後に閲覧した並び順位置 |
| `last_viewed_at` | INTEGER | YES | 最終閲覧日時 UTC |
| `analysis_status` | TEXT | NO | `PENDING` / `READY` / `EMPTY` / `BROKEN` / `UNSUPPORTED` / `ERROR` |
| `availability_status` | TEXT | NO | `AVAILABLE` / `MISSING` / `UNAVAILABLE` |
| `error_code` | TEXT | YES | 最終解析エラーコード |
| `error_message` | TEXT | YES | 最終解析エラー概要 |
| `last_seen_scan_id` | INTEGER | YES | 最後に存在確認したScan ID |
| `is_new` | INTEGER | NO | 「新着」の印。0 または 1（CHECK）。既定は 0。schema_version 4 から（13.3） |
| `last_opened_at` | INTEGER | YES | 最後にビューアで開いた日時 UTC。NULL は、開いていない、または履歴を消した。schema_version 5 から（13.4） |
| `created_at` | INTEGER | NO | DB登録日時 UTC |
| `updated_at` | INTEGER | NO | DB更新日時 UTC |

`archives` にはフルパスを重複保持しない。同一フォルダ内のファイル名は大文字・小文字を区別せず一意とする。

#### 新着の印（`is_new`）

スキャンで新しく見つかったアーカイブに付ける印（要件定義 5.4）。`created_at` と `last_viewed_at` からは求めない（はじめてのスキャン分を除けず、「既読にする」も表せないため）。

- **付ける**: スキャンで、新しく登録する行（`ArchiveBatch.InsertAsync`）だけ。1 にする。ただし、次は 0 のまま登録する
  - 管理フォルダのはじめてのスキャン（`library_folders.last_scan_at` が NULL の間）
  - バックアップから復元した直後の、起動時のスキャン（アプリが指定する。1回だけ有効）
- **付けない**: 変更されたアーカイブ（`UpdateChangedAsync`）、不在から戻ったもの、名前変更・移動・ZIPの作り直し・RAR / 7z の変換（どれも同じ行を更新するだけで、`is_new` は変えない）
- **外す**: アーカイブを開いたとき（`ViewerService.OpenAsync` が、開けた時点で 0 にする。`updated_at` は変えない）、または「新着を既読に」（選んだフォルダ以下、または全部を 0 にする）
- 外せなくても（書き込みの失敗）、閲覧は妨げない（警告をログに残す）
- 索引は作らない。「新着だけ」は、全件を読んで絞り込む（11.8 の、ほかの条件と同じ）。10,000件規模での速さは、測っていない（必要になったら、`is_new = 1` の部分索引を足せる）

#### 最後に開いた日時（`last_opened_at`）

最近開いたアーカイブの一覧（要件定義 5.5）に使う。

- **記録**: アーカイブを開けた時点（`ViewerService.OpenAsync`）で、今の日時にする。「新着」の印を外すのと、同じトランザクションで書く。開けなかったときは記録しない。`updated_at` は変えない
- **消す**: 「履歴を消す」で、すべての行を NULL にする。ほかの列（閲覧位置など）は変えない
- **`last_viewed_at` と分けた理由**: `last_viewed_at` は、閲覧位置を保存した日時で、頁を移るたびに変わる。また、画像の削除（ZIPの作り直し）で閲覧位置がなくなると NULL に戻る（11.3）。「最後に開いた」を表すには、開いた時点だけで変わる列がよく、履歴を消しても閲覧位置を残せる
- 名前変更・移動・ZIPの作り直し・RAR / 7z の変換では、変えない（同じ行を更新するため、一覧に残る）
- 書き込めなくても（失敗）、閲覧は妨げない（警告をログに残す）
- 索引は作らない。一覧の取得は、`last_opened_at IS NOT NULL` の行を、新しい順に並べて 10 件取る。10,000件規模での速さは、測っていない（必要になったら、部分索引を足せる）

#### 最後に閲覧した位置

ビューアで閲覧した位置を、次に開いたときに復元するために記録する。

| カラム | 内容 |
|---|---|
| `last_view_entry` | 最後に表示した画像エントリのフルネーム（アーカイブ内のパス） |
| `last_view_index` | 画像エントリを自然順に並べたときの、0始まりの位置 |
| `last_viewed_at` | 記録した日時（UTC）。アーカイブを開いただけの場合も記録する |

- **復元**: 開くときは、`last_view_entry` と同じ名前の画像エントリがあれば、そのエントリを表示する。ない場合は、`last_view_index` が範囲内でも使わず、先頭から開く。アーカイブの内容が変わった後でも、同じ画像を表示するため、位置の番号ではなく名前を基準にする。`last_view_index` は、現時点では復元に使わない参考値である（将来、進捗の表示などに使える）
- **記録のタイミング**: 頁を移動してから約0.8秒後（連続して移動している間は、最後の位置だけ）、ビューアを閉じるとき、アプリを終了するとき。頁をめくるたびにDBへ書き込まない
- 記録するときは、`updated_at` も更新する
- 閲覧位置を保存できなかった場合も、閲覧は続けられる（ログに記録するだけで、ユーザーには通知しない）

#### 解析で決まる項目

`analysis_status`、`image_count`、`first_image_entry`、`error_code`、`error_message` は、ZIPを開く解析（11.9）で決まる。

| `analysis_status` | 意味 | `image_count` | `error_code` |
|---|---|---|---|
| `PENDING` | 未解析。新規登録時、およびサイズ・更新日時が変わったとき | 前回の値を残す | NULL |
| `READY` | 対応画像（JPEG / PNG / GIF / WebP）が1枚以上ある | 画像の数 | NULL |
| `EMPTY` | ZIPとして正常だが、対応画像がない | 0 | NULL |
| `BROKEN` | ZIPとして読めない（破損、途中で切れている、空ファイル等） | NULL | `ARCHIVE_BROKEN` |
| `UNSUPPORTED` | 暗号化されている | NULL | `ARCHIVE_ENCRYPTED` |
| `ERROR` | ファイルを読めなかった（存在しない、アクセス拒否、I/Oエラー）。一時的な可能性がある | NULL | `FILE_NOT_FOUND` / `ACCESS_DENIED` / `IO_ERROR` |

- 対応画像は拡張子 `.jpg` / `.jpeg` / `.png` / `.gif` / `.webp`（大文字小文字を区別しない）のファイルエントリ。フォルダ、`__MACOSX/` 配下、`._` で始まるファイル（macOSのメタデータ）は数えない
- `first_image_entry` は、画像エントリを自然順（連続する数字を数値として比べる。`img2` は `img10` より前）に並べた先頭。表紙の候補であり、実際にサムネイルの元にした画像は `thumbnail_cache.source_entry` が示す

### 6.4 thumbnail_cache

| カラム | SQLite型 | NULL | 内容 |
|---|---|---:|---|
| `archive_id` | INTEGER | NO | 主キー兼外部キー |
| `cache_key` | TEXT | NO | キャッシュキー |
| `relative_path` | TEXT | YES | キャッシュルートからの相対パス |
| `format` | TEXT | YES | `jpg` または `png` |
| `width` | INTEGER | YES | 幅 |
| `height` | INTEGER | YES | 高さ |
| `source_entry` | TEXT | YES | 元画像エントリ名 |
| `source_archive_size` | INTEGER | YES | 生成時のアーカイブサイズ |
| `source_last_write_time` | INTEGER | YES | 生成時の更新日時 |
| `status` | TEXT | NO | `PENDING` / `READY` / `FAILED` / `STALE` |
| `generated_at` | INTEGER | YES | 生成日時 UTC |
| `error_message` | TEXT | YES | 生成失敗概要 |

#### サムネイルの記録と保存先

- `cache_key` は `archive-{Archive ID}`。Archive IDを基準にするため、移動・名前変更でもキャッシュが維持される
- 画像ファイルは `relative_path`（キャッシュルートからの相対パス）に保存する。1フォルダのファイル数が増えすぎないよう、Archive IDを1,000件ごとのフォルダに分ける。形式は `{ID÷1000を4桁0埋め}\archive-{ID}.jpg`（例: ID 12345 は `0012\archive-12345.jpg`）
- 形式は JPEG 固定、長辺300px（元画像より大きくは拡大しない）、品質80。透過のある画像は白背景に合成する。`format` には `jpg` を入れる
- `source_archive_size` / `source_last_write_time` は、生成時点のアーカイブの状態。`archives` の現在の値と異なる場合は、キャッシュが古い（無効）と判断する

| `status` | 意味 | 画面への影響 |
|---|---|---|
| `PENDING` | 生成待ち。現在の実装では書き込まない（`thumbnail_cache` に記録がない状態が、生成待ちを表す）。将来、進行中の状態を記録する場合に使う | 画像がなければ「作成待ち」の枠を表示 |
| `READY` | 生成済み。画像ファイルがある | 画像を表示 |
| `STALE` | アーカイブが変更された、またはファイルが失われた。再生成待ち | ファイルが残っていれば、再生成が終わるまで古い画像を表示し続ける |
| `FAILED` | 表紙候補の画像をすべて読めなかった。`relative_path` はNULL | 「表紙を読めません」の枠を表示。アーカイブが変更されるまで再試行しない |

### 6.5 operation_history

| カラム | SQLite型 | NULL | 内容 |
|---|---|---:|---|
| `id` | INTEGER | NO | 主キー、自動採番 |
| `archive_id` | INTEGER | YES | 対象Archive ID |
| `operation_type` | TEXT | NO | 操作種別 |
| `status` | TEXT | NO | `SUCCESS` / `FAILED` / `PARTIAL` / `CANCELLED` |
| `archive_name` | TEXT | YES | 操作時点のファイル名 |
| `old_path` | TEXT | YES | 変更前パス |
| `new_path` | TEXT | YES | 変更後パス |
| `affected_count` | INTEGER | YES | 削除画像数等 |
| `message` | TEXT | NO | ユーザー向け結果メッセージ |
| `error_code` | TEXT | YES | エラーコード |
| `details_json` | TEXT | YES | 将来拡張用JSON |
| `executed_at` | INTEGER | NO | 結果確定日時 UTC |

#### operation_type

| 値 | 内容 |
|---|---|
| `IMAGE_DELETE` | ZIP / CBZ内の画像削除。内部ではZIP再構築を行う |
| `ARCHIVE_RENAME` | アーカイブ名変更 |
| `ARCHIVE_MOVE` | アーカイブ移動 |
| `ARCHIVE_CONVERT` | RAR / 7z を ZIP に変換（11.10）。`old_path` は元のファイル、`new_path` は変換した ZIP。元のファイルを残した場合（ごみ箱を使えない保存先など）は、通知では警告とし、履歴は成功として記録する |
| `ARCHIVE_DELETE` | アーカイブをごみ箱へ移動。ごみ箱が使えず、ユーザーが承認して完全削除した場合も同じ種別とし、`details_json` に完全削除であることを記録する |
| `LIBRARY_FOLDER_ADD` | 管理フォルダの追加（親フォルダ追加による子の統合を含む） |
| `LIBRARY_FOLDER_REMOVE` | 管理フォルダを管理対象から外す |
| `SCAN_INITIAL` | フォルダ追加後の初回スキャン |
| `SCAN_MANUAL` | 手動再スキャン |
| `SCAN_STARTUP` | 起動時更新チェック |

操作履歴はユーザーが実行した操作を記録する。ZIP再構築は `IMAGE_DELETE` を実現する内部処理であるため、`ARCHIVE_REBUILD` は `operation_type` として使用しない。再構築の開始、検証、元ZIP退避、新ZIP置換等の内部処理はアプリケーションログへ記録する。

### 6.6 scan_runs

| カラム | SQLite型 | NULL | 内容 |
|---|---|---:|---|
| `id` | INTEGER | NO | Scan ID、主キー |
| `scan_type` | TEXT | NO | `INITIAL` / `MANUAL` / `STARTUP` |
| `library_folder_id` | INTEGER | YES | 特定フォルダのみの場合に設定 |
| `status` | TEXT | NO | `RUNNING` / `SUCCESS` / `PARTIAL` / `FAILED` / `CANCELLED` |
| `started_at` | INTEGER | NO | 開始日時 UTC |
| `completed_at` | INTEGER | YES | 完了日時 UTC |
| `discovered_count` | INTEGER | NO | 検出件数 |
| `added_count` | INTEGER | NO | 新規登録件数 |
| `changed_count` | INTEGER | NO | 変更反映件数 |
| `missing_count` | INTEGER | NO | 不在検出件数 |
| `failed_count` | INTEGER | NO | 失敗件数 |
| `message` | TEXT | YES | 結果概要 |

### 6.7 app_metadata

| カラム | SQLite型 | NULL | 内容 |
|---|---|---:|---|
| `key` | TEXT | NO | 主キー |
| `value` | TEXT | NO | 値 |
| `updated_at` | INTEGER | NO | 更新日時 UTC |

## 7. インデックス

`library_folders.normalized_path` と `folders.normalized_path` は、テーブル定義の `UNIQUE` 制約によりSQLiteが一意インデックスを自動作成するため、`CREATE UNIQUE INDEX` は記載しない（二重化を避ける）。

```sql
CREATE INDEX ix_folders_parent
    ON folders(parent_folder_id);

CREATE INDEX ix_folders_library_relative
    ON folders(library_folder_id, relative_path);

CREATE UNIQUE INDEX ux_archives_folder_file_name
    ON archives(folder_id, file_name COLLATE NOCASE);

CREATE INDEX ix_archives_folder
    ON archives(folder_id);

CREATE INDEX ix_archives_file_name
    ON archives(file_name COLLATE NOCASE);

CREATE INDEX ix_archives_modified
    ON archives(last_write_time DESC);

CREATE INDEX ix_archives_size
    ON archives(file_size DESC);

CREATE INDEX ix_archives_image_count
    ON archives(image_count DESC);

CREATE INDEX ix_archives_availability
    ON archives(availability_status);

CREATE INDEX ix_operation_history_executed
    ON operation_history(executed_at DESC);

CREATE INDEX ix_operation_history_archive
    ON operation_history(archive_id, executed_at DESC);

CREATE INDEX ix_scan_runs_started
    ON scan_runs(started_at DESC);
```

## 8. 修正済み初期DDL

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;

CREATE TABLE app_metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE library_folders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL,
    normalized_path TEXT NOT NULL UNIQUE,
    display_name TEXT,
    is_network INTEGER NOT NULL DEFAULT 0
        CHECK (is_network IN (0, 1)),
    is_enabled INTEGER NOT NULL DEFAULT 1
        CHECK (is_enabled IN (0, 1)),
    availability_status TEXT NOT NULL DEFAULT 'UNKNOWN',
    last_scan_at INTEGER,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE scan_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    scan_type TEXT NOT NULL,
    library_folder_id INTEGER,
    status TEXT NOT NULL,
    started_at INTEGER NOT NULL,
    completed_at INTEGER,
    discovered_count INTEGER NOT NULL DEFAULT 0,
    added_count INTEGER NOT NULL DEFAULT 0,
    changed_count INTEGER NOT NULL DEFAULT 0,
    missing_count INTEGER NOT NULL DEFAULT 0,
    failed_count INTEGER NOT NULL DEFAULT 0,
    message TEXT,
    FOREIGN KEY (library_folder_id)
        REFERENCES library_folders(id) ON DELETE SET NULL
);

CREATE TABLE folders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    library_folder_id INTEGER NOT NULL,
    parent_folder_id INTEGER,
    name TEXT NOT NULL,
    full_path TEXT NOT NULL,
    normalized_path TEXT NOT NULL UNIQUE,
    relative_path TEXT NOT NULL,
    depth INTEGER NOT NULL DEFAULT 0 CHECK (depth >= 0),
    availability_status TEXT NOT NULL DEFAULT 'UNKNOWN',
    last_seen_scan_id INTEGER,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    FOREIGN KEY (library_folder_id)
        REFERENCES library_folders(id) ON DELETE CASCADE,
    FOREIGN KEY (parent_folder_id)
        REFERENCES folders(id) ON DELETE CASCADE,
    FOREIGN KEY (last_seen_scan_id)
        REFERENCES scan_runs(id) ON DELETE SET NULL
);

CREATE TABLE archives (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    folder_id INTEGER NOT NULL,
    file_name TEXT NOT NULL,
    extension TEXT NOT NULL CHECK (extension IN ('zip', 'cbz')),
    file_size INTEGER NOT NULL CHECK (file_size >= 0),
    last_write_time INTEGER NOT NULL,
    image_count INTEGER CHECK (image_count IS NULL OR image_count >= 0),
    first_image_entry TEXT,
    last_view_entry TEXT,
    last_view_index INTEGER
        CHECK (last_view_index IS NULL OR last_view_index >= 0),
    last_viewed_at INTEGER,
    analysis_status TEXT NOT NULL DEFAULT 'PENDING',
    availability_status TEXT NOT NULL DEFAULT 'AVAILABLE',
    error_code TEXT,
    error_message TEXT,
    last_seen_scan_id INTEGER,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    FOREIGN KEY (folder_id)
        REFERENCES folders(id) ON DELETE CASCADE,
    FOREIGN KEY (last_seen_scan_id)
        REFERENCES scan_runs(id) ON DELETE SET NULL
);

CREATE TABLE thumbnail_cache (
    archive_id INTEGER PRIMARY KEY,
    cache_key TEXT NOT NULL UNIQUE,
    relative_path TEXT,
    format TEXT CHECK (format IS NULL OR format IN ('jpg', 'png')),
    width INTEGER CHECK (width IS NULL OR width > 0),
    height INTEGER CHECK (height IS NULL OR height > 0),
    source_entry TEXT,
    source_archive_size INTEGER,
    source_last_write_time INTEGER,
    status TEXT NOT NULL DEFAULT 'PENDING',
    generated_at INTEGER,
    error_message TEXT,
    FOREIGN KEY (archive_id)
        REFERENCES archives(id) ON DELETE CASCADE
);

CREATE TABLE operation_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    archive_id INTEGER,
    operation_type TEXT NOT NULL,
    status TEXT NOT NULL,
    archive_name TEXT,
    old_path TEXT,
    new_path TEXT,
    affected_count INTEGER,
    message TEXT NOT NULL,
    error_code TEXT,
    details_json TEXT,
    executed_at INTEGER NOT NULL,
    FOREIGN KEY (archive_id)
        REFERENCES archives(id) ON DELETE SET NULL
);

CREATE INDEX ix_folders_parent
    ON folders(parent_folder_id);

CREATE INDEX ix_folders_library_relative
    ON folders(library_folder_id, relative_path);

CREATE UNIQUE INDEX ux_archives_folder_file_name
    ON archives(folder_id, file_name COLLATE NOCASE);

CREATE INDEX ix_archives_folder
    ON archives(folder_id);

CREATE INDEX ix_archives_file_name
    ON archives(file_name COLLATE NOCASE);

CREATE INDEX ix_archives_modified
    ON archives(last_write_time DESC);

CREATE INDEX ix_archives_size
    ON archives(file_size DESC);

CREATE INDEX ix_archives_image_count
    ON archives(image_count DESC);

CREATE INDEX ix_archives_availability
    ON archives(availability_status);

CREATE INDEX ix_operation_history_executed
    ON operation_history(executed_at DESC);

CREATE INDEX ix_operation_history_archive
    ON operation_history(archive_id, executed_at DESC);

CREATE INDEX ix_scan_runs_started
    ON scan_runs(started_at DESC);
```

## 9. DDL整合性確認

修正版DDLは次の点で本文と整合する。

- `folders.full_path` が存在する
- `folders.normalized_path` が存在する
- `folders.normalized_path` の一意性は `UNIQUE` 制約で保証される（自動作成される一意インデックスを利用する）
- `archives` に冗長なフルパス列はない
- `ux_archives_folder_file_name` により、同一フォルダ内の同名ファイルを防止する
- 画像削除の操作履歴種別が `IMAGE_DELETE` に統一されている
- 64bit Unix timeを格納できる

## 10. SQLite設定

アプリ起動時に次を設定する。

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
```

DB書き込みは単一キューへ集約し、複数のバックグラウンド処理が同時に書き込まない構成とする。

## 11. 主なトランザクション方針

### 11.1 名前変更

1. 実ファイルを名前変更する
2. 成功後にDBトランザクションを開始する
3. `archives.file_name` と `updated_at` を更新する
4. `operation_history` を追加する
5. コミットする

Phase 9 の実装で、次を確定した。

- 拡張子を変えた場合（.zip ⇔ .cbz）は、`archives.extension` も更新する。サイズ・更新日時は名前の変更では変わらないため、更新しない（解析・サムネイルはそのまま）
- 名前変更の前の検証で断った場合（使えない名前、同名のファイルがある、名前が変わらない）は、ファイルもDBも変更せず、履歴にも記録しない（通知のみ）
- ファイルの名前変更に失敗した場合（使用中、アクセス拒否、ファイルがない）は、DBを変更せず、`ARCHIVE_RENAME` / `FAILED` の履歴を記録する
- ファイルの名前変更のあとにDBの更新に失敗した場合は、ファイル名を元に戻す（DBとファイルが食い違わないようにする）

### 11.2 移動

1. 実ファイルを移動する
2. 移動先の `folder_id` を取得する
3. `archives.folder_id` を更新する
4. 操作履歴を追加する
5. Archive IDとサムネイルを維持する

Phase 9 の実装で、次を確定した。

- 移動先は、管理フォルダの中のフォルダに限る（`library_folders` のいずれかの配下）
- 移動先のフォルダが `folders` に未登録の場合（新しく作ったフォルダなど）は、管理フォルダの起点から移動先までの途中のフォルダを含めて、同じトランザクションで登録する（`relative_path` と `depth` は、スキャンで登録する場合と同じ）
- 同じボリューム内はファイルの名前の変更で移動する。異なるボリューム間は、移動先のフォルダに一時ファイルとしてコピーし、大きさを確認して正式な名前にしてから、元のファイルを削除する。コピーは更新日時を引き継ぐため、通常はサイズ・更新日時は変わらない。変わった場合だけ、その値に更新し、`analysis_status` を `PENDING`、`thumbnail_cache.status` を `STALE` にする
- 元のファイルを削除できなかった場合は、移動先を正として記録し、`ARCHIVE_MOVE` / `PARTIAL` の履歴を記録して、元のファイルの手動での削除を促す
- ファイルの移動に失敗した場合は、DBを変更せず、`ARCHIVE_MOVE` / `FAILED` の履歴を記録する。移動のあとにDBの更新に失敗した場合は、次回のスキャンが実ファイルから同期する（この場合、移動先のファイルは新規として登録される）

### 11.3 ZIP再構築

1. 一時ZIPを作成・検証する
2. 元ZIPをごみ箱へ移動する
3. 一時ZIPを正規名へ変更する
4. `archives` のサイズ、更新日時、画像枚数等を更新する
5. `thumbnail_cache.status` を `STALE` にする
6. `operation_type = IMAGE_DELETE` として、削除画像数を含む操作履歴を追加する
7. ZIP再構築の内部処理詳細はアプリケーションログへ記録する
8. サムネイルを非同期で再生成する

Phase 8 の実装で、手順4〜6を次のとおり確定した（1トランザクション。ファイルの置き換えが済んだあとに行う）。

- `archives`: `file_size` / `last_write_time` を新しいファイルの値にする。`image_count` を削除後の画像枚数にし、`first_image_entry` は `NULL`、`analysis_status` は `PENDING`、`error_code` / `error_message` は `NULL`、`availability_status` は `AVAILABLE`（再構築の途中でスキャンが `MISSING` にしていた場合も有効に戻す）にする。Archive ID は変わらない
- 最後に閲覧した位置（`last_view_entry` / `last_view_index` / `last_viewed_at`）: 更新前に見ていた画像を、更新後のアーカイブでの位置にする。その画像が削除された場合は、次の（なければ前の）残った画像。残った画像がなければ `NULL`
- `thumbnail_cache`: `status` を `STALE` にする。古い画像は、再生成されるまで表示を続ける。`PENDING` になった解析が次のサムネイル処理で行われ、表紙とサムネイルが作り直される（11.9）
- `operation_history`: `IMAGE_DELETE`、`SUCCESS`、`affected_count` に削除した画像の数、`old_path` と `new_path` に同じアーカイブのパス
- DB更新だけが失敗した場合（ファイルは置き換え済み）は、次回のスキャンが実ファイルから同期する。このときサイズ・更新日時が変わっているため、解析待ち・サムネイル再生成待ちになる

### 11.4 削除

1. 実ファイルをごみ箱へ移動する
2. 操作履歴に操作時点のファイル名とパスを保存する
3. `archives` を削除する
4. サムネイル管理行をCASCADEで削除する
5. 操作履歴の `archive_id` はSET NULLで保持する

ごみ箱を利用できず、ユーザーが完全削除を承認した場合は、手順1を完全削除に置き換え、以降は同じ。キャンセルした場合はDBを変更しない。

Phase 9 の実装で、次を確定した。

- 手順3は、`archives` の行を削除する（不在 `MISSING` にはしない。ユーザーが削除したアーカイブのため）。`thumbnail_cache` はCASCADEで削除される
- 手順2の履歴は、同じトランザクションで、`archives` の削除より先に記録する。`archive_id` は NULL で記録する（直後に行が削除されるため）。`archive_name` と `old_path` に、削除したアーカイブのファイル名とパスを残す。完全削除の場合も同じ種別（`ARCHIVE_DELETE`）で、`message` に完全削除であることを含める（`details_json` はMVPでは使わない）
- DBの更新のあとに、サムネイル画像のファイルを削除する
- ごみ箱へ移動できない場合・ファイルを削除できない場合は、DBを変更せず、`ARCHIVE_DELETE` / `FAILED` の履歴を記録する。削除のあとにDBの更新に失敗した場合は、次回のスキャンが不在として扱う

### 11.5 管理フォルダを管理対象から外す（UC-18）

1. 確認ダイアログで承認を得る
2. トランザクションを開始する
3. `library_folders` の該当行を削除する（`folders`、`archives`、`thumbnail_cache` はCASCADEで削除される）
4. `operation_type = LIBRARY_FOLDER_REMOVE` の履歴を追加する（`archive_id` は NULL、件数を `affected_count` に保存）
5. コミットする
6. サムネイルキャッシュのファイル実体を削除する

実ファイルは変更しない。操作履歴の `archive_id` はSET NULLで保持される。

Phase 9 の実装で、手順4の件数は、配下のアーカイブの登録件数（不在 `MISSING` を含む）とした。履歴の `old_path` に管理フォルダのパスを残す。手順6のサムネイル画像のパスは、手順3の削除の前に、同じトランザクションで取得しておく。

### 11.6 親フォルダ追加時の統合（UC-01）

既存の登録フォルダの親を追加する場合、1トランザクションで次を行う。

1. 親フォルダを `library_folders` に追加し、`folders` に起点を登録する
2. 既存の子の登録フォルダに属する `folders` の `library_folder_id`、`parent_folder_id`、`relative_path`、`depth` を親の配下になるよう更新する
3. 既存の子の `library_folders` 行を削除する（`folders` を付け替え済みのため、配下の `archives` は削除されない）
4. `LIBRARY_FOLDER_ADD` の履歴を追加する
5. コミット後に親フォルダを再スキャンする

`archives.id`、サムネイル、最終閲覧位置は維持される。手順3の前に付け替えが完了していないと、CASCADEで配下が消えるため、順序を守ること。

### 11.7 スキャン結果の反映

スキャンは「ファイル列挙（DBへ書き込まない）」と「差分のDB反映（書き込みキュー経由の1トランザクション）」の2段階で行う。

1. 開始時に `scan_runs` を `RUNNING` で作成する（独立したトランザクション）。1つの管理フォルダを対象とするスキャンは `library_folder_id` に設定し、再スキャン・起動時チェックのように全登録フォルダ（有効なもの）をまとめて対象とするスキャンは `NULL` とする。全体スキャンでも `scan_runs` と操作履歴は1回分だけ作る
2. 管理フォルダごとに、配下を列挙する。読み取れなかったフォルダは「確認不能」として記録し、スキップして続行する
3. 管理フォルダごとに、差分を1トランザクションで反映する。サイズまたは更新日時が異なれば変更、DBにないファイルは新規とする
   - 変更: `analysis_status` を `PENDING` に戻し、`thumbnail_cache.status` を `STALE` にする。Archive IDは維持する
   - 新規・変更のファイルは、書き込み直前にサイズと更新日時を再取得して照合する。一致しなければそのファイルの結果を破棄し、`failed_count` に計上して次回スキャンに委ねる
   - 不在から戻ったファイルは、同じArchive IDで `AVAILABLE` に戻し、変更として計上する
4. 確認できた範囲で見つからなかったアーカイブは `MISSING`、フォルダは `UNAVAILABLE` にする（物理削除しない）
5. 管理フォルダの反映と同じトランザクションで、`library_folders.last_scan_at` と `availability_status` を更新する
6. すべての管理フォルダの処理後、`scan_runs` の完了と `operation_history` への最終結果の記録を、1つのトランザクションで行う（開始は記録しない）。`scan_runs.status` は、全管理フォルダにアクセスできなければ `FAILED`、一部でも確認できなかった範囲があれば `PARTIAL`、それ以外は `SUCCESS` とする。アクセスできなかった管理フォルダは `failed_count` に1件として計上する

複数の管理フォルダを処理する途中でキャンセルされた場合、処理済みの管理フォルダの反映は残り、`scan_runs` は `CANCELLED` として、そこまでの件数を記録する。

**アクセスできなかった範囲を不在にしない。**

**アプリ内の操作の結果を優先する**（Phase 9 で追加）: 列挙とDB反映の間に、アプリ内で名前変更・移動が行われると、DBの記録は新しい場所を指し、列挙結果は古い場所を指す。手順4で不在にする前に、DBの記録どおりの場所にファイルがあるかを確認し、あれば不在にしない（古い場所のファイルは既にないため、手順3の照合で新規にもならない）。

- 読み取れなかったフォルダと、その配下のフォルダ・アーカイブは、不在判定の対象外とする（既存の登録を変更しない）。`scan_runs.status` は `PARTIAL` とする
- 登録フォルダ自体にアクセスできない場合（ネットワークドライブの切断等）は、配下を一切変更せず、`library_folders.availability_status` を `UNAVAILABLE`、`scan_runs.status` を `FAILED` とする。接続が戻ったあとの次回スキャンで通常どおり復帰する

キャンセルまたは予期しない例外でトランザクションがロールバックされた場合は、`RUNNING` のまま残らないよう、別トランザクションで `scan_runs` を `CANCELLED` または `FAILED` に更新する。

### 11.8 一覧の検索クエリ

ライブラリ画面の一覧は、次の方針でSQLiteから取得する（ZIP本体は開かない）。

- **対象**: `archives.availability_status = 'AVAILABLE'` のものだけ。`MISSING` は含めない
- **フォルダ指定**: 選択したフォルダ自身と配下のすべてのフォルダを対象とする。`folders.parent_folder_id` をたどる再帰CTEで対象フォルダ集合を求め、`archives.folder_id` で絞り込む。フォルダ指定なしは全登録フォルダを横断する
- **ファイル名検索**: 入力を空白（全角空白を含む）で区切り、すべての語を含むファイル名に一致させる（AND、部分一致）。語は `LIKE` のパラメーターとして渡し、`%` `_` `\` は文字そのものとして扱うようエスケープする
- **並べ替え**: ファイル名（大文字小文字を区別しない）、更新日時、ファイルサイズ、画像枚数。画像枚数の未解析（NULL）は0枚として扱う。同じ値の行は `archives.id` 順とし、順序を安定させる。昇順・降順を選べる
- **新着だけ**: 条件に `archives.is_new = 1` を加える（ほかの条件と AND）。一覧には、各行の `is_new` を含める
- **最近開いたものだけ**（`ArchiveQuery.RecentOnly`）: 条件に `archives.last_opened_at IS NOT NULL` を加え、並べ替えは指定によらず `last_opened_at DESC`（同じ値は `archives.id` 順）にする。最近開いたアーカイブの一覧は、フォルダ・検索・新着の条件をつけずに、Limit 10 で取る。一覧には、各行の `last_opened_at` を含める
- **総件数**: 件数表示のため、絞り込み条件に一致する総件数を別クエリで返す。取得範囲（Offset / Limit）は任意

既知の制限:

- SQLiteの `LIKE` と `COLLATE NOCASE` が大文字小文字を同一視するのはASCII文字のみ。全角英字や、アクセント付き文字等の大文字小文字は区別される
- ファイル名の並べ替えは辞書順であり、数字の大小は考慮しない（`book10` が `book2` より前に並ぶ）。自然順での並べ替えは将来の検討事項とする

10,000件（100フォルダ×100件）での測定では、全件取得、並べ替え、検索、フォルダ絞り込みのいずれも、開発機で約70ms以下だった。

### 11.9 解析とサムネイル生成

ZIPを開くのは、この処理と、ビューアで閲覧するときだけに限る。一覧の表示では開かない（11.8の検索結果に、サムネイルのファイルの場所と状態を含める）。

**対象の選定**（有効な `AVAILABLE` のアーカイブのみ）:

- `analysis_status` が `PENDING` のもの（新規、または変更されたもの）
- `analysis_status` が `ERROR` のもの。ただし、アプリの起動ごとに1回だけ再試行する（同じ起動中は再試行しない）
- `analysis_status` が `READY` で、サムネイルがない、`PENDING` / `STALE`、または生成時のサイズ・更新日時が現在と異なるもの
- `FAILED` のサムネイルは、アーカイブが変更されるまで対象にしない
- 解析で `IO_ERROR` / `ACCESS_DENIED` が発生した場合は、`ERROR` と記録する前に、その場で200ms・800ms後の最大2回（計3回）再試行する。ネットワークの瞬断や、他のプログラムによる一時的なロックで、一覧全体が `ERROR` になるのを避けるため

**処理**（アーカイブごと。同時に2件まで）:

1. DBの記録とファイルの現在のサイズ・更新日時を照合する。異なる場合は、外部で変更されたものとして処理せず見送る（次回のスキャンが反映する）。ファイルが存在しない場合は `ERROR`（`FILE_NOT_FOUND`）とする
2. ZIPを1回開き、解析する。`READY` なら、表紙候補（自然順の先頭から最大3枚）を順に試し、最初に読めた画像からサムネイルを作る。1枚あたりの読み込みは64MBまで。先頭の画像が壊れていても、次の画像で代用できる
3. サムネイル画像のファイルを保存する（一時ファイルへ書いてから置き換える）
4. 1つのトランザクションで、`archives` の解析結果と `thumbnail_cache` を更新する。`archives` の更新は、解析開始時点のサイズ・更新日時が現在の記録と一致する場合に限る（解析中にスキャンが更新した場合は、古い内容の結果で上書きしない）
5. `READY` 以外になった場合は、`thumbnail_cache` の記録と画像ファイルを削除する

**キャッシュの無効化と再生成**:

- スキャンでアーカイブの変更を検出すると、`analysis_status` を `PENDING`、`thumbnail_cache.status` を `STALE` にする（11.7）。古い画像は再生成が終わるまで表示に使う
- 起動時の確認で、`READY` のサムネイルの画像ファイルが存在するか調べ、失われていれば `STALE` に戻して再生成する。確認はファイルの存在だけで、ZIPは開かない

開発機での測定値（参考）: 生成済みの2,000件の起動時の確認は約84ms（10,000件では約0.4秒と推定）。生成の速度は、1600×1200のJPEG3枚入りのアーカイブで約67件/秒で、10,000件では約2.5分と見込まれる。

### 11.10 ZIPへの変換（GitHub の Issue #20）
RAR / 7z を ZIP に変換したとき、同じアーカイブ（Archive ID）の記録を、変換した ZIP のものにする。中身の画像（名前と内容）は変わらないため、閲覧位置（`last_view_entry`、`last_view_index`）と、解析の結果（`image_count`、`first_image_entry`、`analysis_status`）は、そのまま残す。

1. 変換した ZIP を、確かめてから、同じフォルダに置く（元のファイルは、まだ残っている）
2. 1つのトランザクションで、次を行う
   - `archives` の `file_name`、`extension`、`file_size`、`last_write_time` を、変換した ZIP の値にする（`availability_status` は `AVAILABLE`）
   - `thumbnail_cache` が、変換の前のファイルから作った最新のもの（`status = 'READY'` で、`source_archive_size` と `source_last_write_time` が、変換の前の値と一致する）なら、その2つを、変換した ZIP の値に付け替える。作り直しの対象（11.9）にしないため
   - `ARCHIVE_CONVERT` の履歴を追加する
3. 元のファイルを、ごみ箱へ移す

- 2 が失敗したら、置いた ZIP を消して、元の状態に戻す（元のファイルは、まだ変えていない）
- 元のファイルを残した場合（ごみ箱を使えない、移せなかった）は、DBには、変換した ZIP だけが残る。元のファイルは、次のスキャンで、新しいアーカイブ（新しいID）として登録される
## 12. レビュー事項

1. （暫定確定）不在ファイルは物理削除せず、`MISSING` として保持する。一覧・件数には含めない。保持期間の上限や、ユーザーによる削除操作は未定
2. アーカイブ削除時にDB行を物理削除するか、論理削除にするか
3. （確定）サムネイルはJPEG固定とする。透過は白背景に合成する。長辺300px、品質80で、10,000件規模の生成速度と表示に問題がなかった
4. `IMAGE_DELETE` の `details_json` へ削除対象画像のエントリ名一覧を保存するか
5. （確定）ごみ箱が使えない場合のバックアップ情報はDBへ持たない。アーカイブ削除は確認のうえ完全削除、ZIP再構築は中止とする
6. ファイル名部分一致検索が10,000件規模で十分な速度かプロトタイプで確認する
## 13. スキーマの移行
マイグレーションは、アプリに埋め込んだSQL（`Migrations/000N_*.sql`）を番号の順に適用する（`MigrationRunner`）。1つずつトランザクションで行い、成功したら `app_metadata.schema_version` をその番号にする。失敗したら、そのマイグレーションを取り消す。DBの `schema_version` がアプリの知っている番号より新しい場合は、開かない（新しい版で作ったDBを、古い版で壊さないため。復元でも、新しい版で作ったバックアップは受け付けない）。

| schema_version | ファイル | 内容 |
|---:|---|---|
| 1 | `0001_initial.sql` | 初期DDL（8章） |
| 2 | `0002_archive_formats.sql` | `archives.extension` のCHECKに、`rar`、`cbr`、`7z`、`cb7` を加える（GitHub の Issue #3） |
| 3 | `0003_requeue_empty_archives.sql` | `analysis_status = 'EMPTY'` の行を `PENDING` に戻す（GitHub の Issue #2） |
| 4 | `0004_archive_is_new.sql` | `archives.is_new`（0 / 1、既定 0）を加える（GitHub の Issue #16） |
| 5 | `0005_archive_last_opened.sql` | `archives.last_opened_at`（NULL 可）を加え、既存の行に `last_viewed_at` を写す（GitHub の Issue #27） |

### 13.1 表の作り直し（0002）
SQLiteは、CHECK制約をあとから変更できないため、SQLite公式の手順で `archives` を作り直す。

1. `archives` の連番（`sqlite_sequence`）の位置を、一時表に覚えておく
2. 新しいCHECKの `archives_new` を作り、全行を、IDを含めてそのまま写す
3. 古い `archives` を消し、`archives_new` を `archives` に名前を変える
4. 連番の位置を、覚えておいた値に戻す（戻さないと、消したアーカイブのIDが再び使われる。IDはサムネイルのファイル名に使っている）
5. `archives` の索引を作り直す（表を消すと、索引も消えるため。0001と同じもの）

外部キーは、この間だけ切る。切らずに `archives` を消すと、`ON DELETE CASCADE` で `thumbnail_cache` の行まで消えるため。

- マイグレーションのファイルの先頭に `-- k35:foreign-keys=off` と書くと、`MigrationRunner` が、トランザクションの前に `PRAGMA foreign_keys = OFF` にし、終わったあと（成功・失敗に関わらず）ONに戻す。外部キーの切り替えは、トランザクションの中では効かないため
- コミットの前に `PRAGMA foreign_key_check` で整合を確かめ、違反があれば、違反した表を示して例外にし、全体を取り消す
- 移行の前後で、`archives` の行、ID、`thumbnail_cache` などの子の表の行、連番の位置が変わらないことを、テストで確かめている

### 13.2 画像なしの解析し直し（0003）
GIF / WebP に対応する前に「画像なし（`EMPTY`）」と解析されたアーカイブは、GIF / WebP だけが入っていた可能性がある。`EMPTY` は、サイズと更新日時が変わらない限り、再び解析の対象にならない（`GetPendingProcessingIdsAsync` は `PENDING` と `ERROR`、および `READY` でサムネイルが古いものを拾う）。そのため、移行で一度だけ `PENDING` に戻す。

```sql
UPDATE archives
   SET analysis_status = 'PENDING', error_code = NULL, error_message = NULL
 WHERE analysis_status = 'EMPTY';
```

- `image_count` は変えない。`PENDING` は前回の値（0）を残す扱い（6.3）で、解析が終わるまで「0」と「作成待ち」が出る
- `EMPTY` にはサムネイルの行がないため、`thumbnail_cache` は触らない
- アプリが起動したあとの、サムネイルの作成待ち（`ThumbnailService.ProcessPendingAsync`）が、そのまま解析し直す。専用の処理は要らない
- 画像のあるアーカイブ（`READY`）は、戻さない。GIF / WebP が混ざっていても、枚数は、ファイルが変わったときに更新される（全アーカイブの再解析が重いため）
- 対応する画像の形式を、今後も増やすときは、同じように、そのための移行を足す
- テスト: `EMPTY` だけが `PENDING` に戻り、`READY`・`BROKEN` の行と、サムネイルの行は変わらない

### 13.3 新着の列の追加（0004）
`archives` に、`ALTER TABLE ... ADD COLUMN is_new INTEGER NOT NULL DEFAULT 0 CHECK (is_new IN (0, 1))` で列を足す。SQLite は、既定値のある NOT NULL の列と CHECK を、表を作り直さずに足せる（0002 のような作り直しや、外部キーを切る印は要らない）。

- 既存の行は 0 になる。この版へ更新した直後に、ライブラリ全体が新着にならない
- 0 と 1 以外は入らない（テストで確かめている）

### 13.4 最後に開いた日時の列の追加（0005）
```sql
ALTER TABLE archives ADD COLUMN last_opened_at INTEGER;
UPDATE archives SET last_opened_at = last_viewed_at WHERE last_viewed_at IS NOT NULL;
```

- NULL を許す列のため、表を作り直さずに足せる（0004 と同じ）
- 既存の行は、`last_viewed_at`（閲覧位置を保存した日時。開いただけでも記録している）を写す。この版へ更新した直後も、最近開いたアーカイブの一覧が空にならない。開いたことのない行は NULL のまま
- テスト: `last_viewed_at` のある行だけに写り、ない行は NULL のまま。行の数は変わらない
