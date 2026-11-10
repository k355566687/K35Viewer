# 35Viewer の試用版（self-contained x64 のフォルダ配布）を作る（開発環境設計書 17.2）。
# 使い方: リポジトリのルートで  .\scripts\publish.ps1
# 出力先: publish\K35Viewer-win-x64\（.gitignore の対象）。K35Viewer.exe を実行すれば、.NET の導入なしで起動できる。
# 単一ファイル化・トリミング・ReadyToRun は使わない（WPF やリフレクションとの相性の確認が必要なため）。

param(
    [string]$Output = "publish\K35Viewer-win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

if (-not $SkipTests) {
    dotnet test --solution K35Viewer.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "テストが失敗したため、配布物を作りません。" }
}

if (Test-Path $Output) {
    Remove-Item -Recurse -Force $Output
}

dotnet publish src\K35Viewer.App\K35Viewer.App.csproj -c Release -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish が失敗しました。" }

$files = Get-ChildItem $Output -Recurse -File
"{0}: {1} ファイル / {2:N1} MB" -f $Output, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB)
