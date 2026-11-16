-- k35:foreign-keys=off
-- RAR / 7z の対応(要件定義 19.3、GitHub の Issue #3)。archives.extension に、'rar' 'cbr' '7z' 'cb7' を許す。
--
-- SQLite は、CHECK 制約をあとから変えられないため、テーブルを作り直す(SQLite 公式の手順):
--   新しい表を作る → 中身を写す → 古い表を消す → 名前を付け替える → 索引を作り直す。
-- 上の行のマーカーで、MigrationRunner は、この移行の間だけ外部キーを切る。
-- (切らずに archives を消すと、ON DELETE CASCADE で、thumbnail_cache の行まで消えてしまう。)
-- 移行のあと、MigrationRunner が foreign_key_check で整合を確かめ、問題があれば、全体を取り消す。

-- 連番(AUTOINCREMENT)の位置を、あとで元に戻すために覚えておく。
-- 戻さないと、消したアーカイブの ID が、再び使われる(ID は、サムネイルのファイル名に使っている)。
CREATE TEMP TABLE k35_archives_seq AS
    SELECT seq FROM sqlite_sequence WHERE name = 'archives';

CREATE TABLE archives_new (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    folder_id INTEGER NOT NULL,
    file_name TEXT NOT NULL,
    extension TEXT NOT NULL
        CHECK (extension IN ('zip', 'cbz', 'rar', 'cbr', '7z', 'cb7')),
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

INSERT INTO archives_new (
    id, folder_id, file_name, extension, file_size, last_write_time, image_count, first_image_entry,
    last_view_entry, last_view_index, last_viewed_at, analysis_status, availability_status,
    error_code, error_message, last_seen_scan_id, created_at, updated_at)
SELECT
    id, folder_id, file_name, extension, file_size, last_write_time, image_count, first_image_entry,
    last_view_entry, last_view_index, last_viewed_at, analysis_status, availability_status,
    error_code, error_message, last_seen_scan_id, created_at, updated_at
FROM archives;

DROP TABLE archives;
ALTER TABLE archives_new RENAME TO archives;

-- 連番の位置を、元と同じにする
DELETE FROM sqlite_sequence WHERE name = 'archives';
INSERT INTO sqlite_sequence (name, seq)
    SELECT 'archives', seq FROM k35_archives_seq WHERE seq IS NOT NULL LIMIT 1;
DROP TABLE k35_archives_seq;

-- archives の索引を作り直す(表を消すと、索引も消えるため。0001 と同じもの)
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
