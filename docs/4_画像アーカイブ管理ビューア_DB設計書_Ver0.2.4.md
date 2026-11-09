# 画像アーカイブ管理ビューア DB設計書

**文書バージョン:** Ver.0.2.4  
**作成日:** 2026-11-06  
**更新日:** 2026-11-07  
**対象:** MVP  
**DBMS:** SQLite

## 1. 修正内容

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
| `extension` | TEXT | NO | `zip` または `cbz` |
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
| `created_at` | INTEGER | NO | DB登録日時 UTC |
| `updated_at` | INTEGER | NO | DB更新日時 UTC |

`archives` にはフルパスを重複保持しない。同一フォルダ内のファイル名は大文字・小文字を区別せず一意とする。

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

### 11.2 移動

1. 実ファイルを移動する
2. 移動先の `folder_id` を取得する
3. `archives.folder_id` を更新する
4. 操作履歴を追加する
5. Archive IDとサムネイルを維持する

### 11.3 ZIP再構築

1. 一時ZIPを作成・検証する
2. 元ZIPをごみ箱へ移動する
3. 一時ZIPを正規名へ変更する
4. `archives` のサイズ、更新日時、画像枚数等を更新する
5. `thumbnail_cache.status` を `STALE` にする
6. `operation_type = IMAGE_DELETE` として、削除画像数を含む操作履歴を追加する
7. ZIP再構築の内部処理詳細はアプリケーションログへ記録する
8. サムネイルを非同期で再生成する

### 11.4 削除

1. 実ファイルをごみ箱へ移動する
2. 操作履歴に操作時点のファイル名とパスを保存する
3. `archives` を削除する
4. サムネイル管理行をCASCADEで削除する
5. 操作履歴の `archive_id` はSET NULLで保持する

ごみ箱を利用できず、ユーザーが完全削除を承認した場合は、手順1を完全削除に置き換え、以降は同じ。キャンセルした場合はDBを変更しない。

### 11.5 管理フォルダを管理対象から外す（UC-18）

1. 確認ダイアログで承認を得る
2. トランザクションを開始する
3. `library_folders` の該当行を削除する（`folders`、`archives`、`thumbnail_cache` はCASCADEで削除される）
4. `operation_type = LIBRARY_FOLDER_REMOVE` の履歴を追加する（`archive_id` は NULL、件数を `affected_count` に保存）
5. コミットする
6. サムネイルキャッシュのファイル実体を削除する

実ファイルは変更しない。操作履歴の `archive_id` はSET NULLで保持される。

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

- 読み取れなかったフォルダと、その配下のフォルダ・アーカイブは、不在判定の対象外とする（既存の登録を変更しない）。`scan_runs.status` は `PARTIAL` とする
- 登録フォルダ自体にアクセスできない場合（ネットワークドライブの切断等）は、配下を一切変更せず、`library_folders.availability_status` を `UNAVAILABLE`、`scan_runs.status` を `FAILED` とする。接続が戻ったあとの次回スキャンで通常どおり復帰する

キャンセルまたは予期しない例外でトランザクションがロールバックされた場合は、`RUNNING` のまま残らないよう、別トランザクションで `scan_runs` を `CANCELLED` または `FAILED` に更新する。

### 11.8 一覧の検索クエリ

ライブラリ画面の一覧は、次の方針でSQLiteから取得する（ZIP本体は開かない）。

- **対象**: `archives.availability_status = 'AVAILABLE'` のものだけ。`MISSING` は含めない
- **フォルダ指定**: 選択したフォルダ自身と配下のすべてのフォルダを対象とする。`folders.parent_folder_id` をたどる再帰CTEで対象フォルダ集合を求め、`archives.folder_id` で絞り込む。フォルダ指定なしは全登録フォルダを横断する
- **ファイル名検索**: 入力を空白（全角空白を含む）で区切り、すべての語を含むファイル名に一致させる（AND、部分一致）。語は `LIKE` のパラメーターとして渡し、`%` `_` `\` は文字そのものとして扱うようエスケープする
- **並べ替え**: ファイル名（大文字小文字を区別しない）、更新日時、ファイルサイズ、画像枚数。画像枚数の未解析（NULL）は0枚として扱う。同じ値の行は `archives.id` 順とし、順序を安定させる。昇順・降順を選べる
- **総件数**: 件数表示のため、絞り込み条件に一致する総件数を別クエリで返す。取得範囲（Offset / Limit）は任意

既知の制限:

- SQLiteの `LIKE` と `COLLATE NOCASE` が大文字小文字を同一視するのはASCII文字のみ。全角英字や、アクセント付き文字等の大文字小文字は区別される
- ファイル名の並べ替えは辞書順であり、数字の大小は考慮しない（`book10` が `book2` より前に並ぶ）。自然順での並べ替えは将来の検討事項とする

10,000件（100フォルダ×100件）での測定では、全件取得、並べ替え、検索、フォルダ絞り込みのいずれも、開発機で約70ms以下だった。

## 12. レビュー事項

1. （暫定確定）不在ファイルは物理削除せず、`MISSING` として保持する。一覧・件数には含めない。保持期間の上限や、ユーザーによる削除操作は未定
2. アーカイブ削除時にDB行を物理削除するか、論理削除にするか
3. サムネイルをJPEG固定とするか、JPEG / PNGを使い分けるか
4. `IMAGE_DELETE` の `details_json` へ削除対象画像のエントリ名一覧を保存するか
5. （確定）ごみ箱が使えない場合のバックアップ情報はDBへ持たない。アーカイブ削除は確認のうえ完全削除、ZIP再構築は中止とする
6. ファイル名部分一致検索が10,000件規模で十分な速度かプロトタイプで確認する
