-- 最近開いたアーカイブの一覧(GitHub の Issue #27)。アーカイブを最後にビューアで開いた日時(UNIX秒)。
-- NULL は、まだ開いていない、または履歴を消した。last_viewed_at(閲覧位置を保存した日時)とは別に持つ。
-- 既存の行は、閲覧位置を保存した日時を引き継ぐ(この版へ更新した直後も、一覧が空にならないように)。
ALTER TABLE archives ADD COLUMN last_opened_at INTEGER;
UPDATE archives SET last_opened_at = last_viewed_at WHERE last_viewed_at IS NOT NULL;
