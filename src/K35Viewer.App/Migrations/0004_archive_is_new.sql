-- 新着の表示(GitHub の Issue #16)。スキャンで新しく登録されたアーカイブに、「新着」の印を付けるための列。
-- 1 = 新着。アーカイブを開いたとき、または「既読にする」操作で、0 に戻す。
-- 既存の行は 0（この版へ更新した直後に、ライブラリ全体が新着にならないようにする）。
ALTER TABLE archives ADD COLUMN is_new INTEGER NOT NULL DEFAULT 0 CHECK (is_new IN (0, 1));
