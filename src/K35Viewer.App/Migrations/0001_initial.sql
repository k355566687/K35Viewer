-- 初期スキーマ (DB設計書 Ver0.2.2 修正版 8章)
-- PRAGMAは接続ごとに SqliteConnectionFactory が適用するため、ここには含めない。

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
