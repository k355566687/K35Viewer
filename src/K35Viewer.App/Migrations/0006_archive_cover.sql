-- サムネイルの編集(GitHub の Issue #15)。利用者が選んだ表紙の画像と、切り抜きの範囲。
-- cover_entry が NULL なら、自動(自然順の先頭の画像)。切り抜きは、元の画像の幅・高さに対する割合(0〜1)で、
-- 4つの列がそろって NULL(切り抜かない)か、そろって値がある。画像そのものは持たない(サムネイルはキャッシュのファイル)。
ALTER TABLE archives ADD COLUMN cover_entry TEXT;
ALTER TABLE archives ADD COLUMN cover_crop_x REAL CHECK (cover_crop_x IS NULL OR (cover_crop_x >= 0 AND cover_crop_x < 1));
ALTER TABLE archives ADD COLUMN cover_crop_y REAL CHECK (cover_crop_y IS NULL OR (cover_crop_y >= 0 AND cover_crop_y < 1));
ALTER TABLE archives ADD COLUMN cover_crop_width REAL CHECK (cover_crop_width IS NULL OR (cover_crop_width > 0 AND cover_crop_width <= 1));
ALTER TABLE archives ADD COLUMN cover_crop_height REAL CHECK (cover_crop_height IS NULL OR (cover_crop_height > 0 AND cover_crop_height <= 1));