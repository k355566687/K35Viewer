-- GIF / WebP の対応(要件定義 19.3、GitHub の Issue #2)。
-- これまで「画像なし(EMPTY)」と解析されたアーカイブは、GIF / WebP だけが入っていた可能性がある。
-- 対応する画像の形式が増えたので、1回だけ解析待ち(PENDING)に戻し、新しい判定で解析し直す
-- (ユースケース定義書 8.1)。EMPTY は、サイズや更新日時が変わらない限り、通常は再解析されないため。
-- 画像枚数は、解析し直すまで、今の値(0)を残す。サムネイルの行は、EMPTY には元々ない。
UPDATE archives
   SET analysis_status = 'PENDING', error_code = NULL, error_message = NULL
 WHERE analysis_status = 'EMPTY';
