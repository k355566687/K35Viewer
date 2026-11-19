# アンインストールの動作確認用の「見本の環境」を、一時フォルダに作って、35Viewer を起動する（GitHub の Issue #24）。
# 本物のアプリとデータには、触れない。見本のアプリは、見本のデータ（K35VIEWER_DATA_ROOT で切り替える）だけを使う。
# 使い方（リポジトリの直下で）:  powershell -ExecutionPolicy Bypass -File scripts\uninstall-sandbox.ps1
#   -Build   : 写す元のフォルダ（既定: Release のビルドの出力）
#   -NoLaunch: 見本を作るだけで、起動しない
param(
    [string]$Build = "src\K35Viewer.App\bin\Release\net10.0-windows",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$repository = Split-Path $PSScriptRoot -Parent
Set-Location $repository

$source = Resolve-Path $Build
if (-not (Test-Path (Join-Path $source "K35Viewer.exe"))) {
    throw "ビルドの出力に K35Viewer.exe がありません: $source （先に dotnet build -c Release を実行してください）"
}

$root = Join-Path $env:TEMP "35Viewer-uninstall-sandbox"
if (Test-Path $root) {
    # 前回の見本が残っている場合は、見本の名前のフォルダだけを、消す
    Remove-Item -LiteralPath $root -Recurse -Force
}

$program = Join-Path $root "Apps\35Viewer"
$data = Join-Path $root "Data\K35Viewer"
$library = Join-Path $root "Library"
New-Item -ItemType Directory -Force $program, $data, $library | Out-Null

Copy-Item (Join-Path $source "*") $program -Recurse -Force

# 設定: 外観は、ダーク。ショートカットの確認は、出さない（見本から、本物のスタートメニューに、作らないため）
Set-Content -Path (Join-Path $data "settings.json") -Value '{ "Theme": "Dark", "ShortcutPromptShown": true }' -Encoding ASCII

# 管理フォルダに置く見本のアーカイブ（アンインストールしても、消えてはいけないもの）
$samples = Join-Path $repository ".agents\sample\library"
if (Test-Path $samples) {
    Get-ChildItem $samples -Filter *.zip -File | Select-Object -First 3 | Copy-Item -Destination $library
}
if (-not (Get-ChildItem $library -File -ErrorAction SilentlyContinue)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $library "sample.zip"
    $archive = [System.IO.Compression.ZipFile]::Open($zip, "Create")
    $entry = $archive.CreateEntry("readme.txt")
    $writer = New-Object System.IO.StreamWriter($entry.Open())
    $writer.Write("sample")
    $writer.Dispose()
    $archive.Dispose()
}

# 35Viewer のものではないファイル（アンインストールしても、残るはず）
Set-Content -Path (Join-Path $data "my-notes.txt") -Value "35Viewer のものではないファイル。データのフォルダに残るはず。" -Encoding UTF8

"見本の環境を作りました: $root"
"  プログラム: $program"
"  データ    : $data"
"  管理フォルダ（登録してください）: $library"
""
"確かめること:"
"  1. 設定 → アプリ → アンインストール… を押す。確認のダイアログに、消す場所（上のプログラムとデータ）が出ること。"
"  2. 管理フォルダ（$library）を登録して、アンインストールしても、アーカイブが残ること。"
"  3. バックアップを保存する場合は、保存先（既定は「ダウンロード」フォルダ。ここでは、$root\Downloads などに変えると、後片付けが楽）に、ファイルができること。"
"  4. 「アンインストール」と入力しないと、ボタンが押せないこと。"
"  5. アンインストールが終わったら、結果のウィンドウが出て、閉じると、プログラムとデータが消えていること（$root の下に、my-notes.txt と Library が残る）。"
"  6. 管理フォルダを、プログラムのフォルダの中（$program\books など）に登録すると、アンインストールできない理由が出ること。"
""

if (-not $NoLaunch) {
    $env:K35VIEWER_DATA_ROOT = $data
    Start-Process (Join-Path $program "K35Viewer.exe") -WorkingDirectory $program
    "35Viewer（見本）を起動しました。"
}
