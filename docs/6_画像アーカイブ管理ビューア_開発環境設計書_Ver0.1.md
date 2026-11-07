# 画像アーカイブ管理ビューア 開発環境設計書

**文書バージョン:** Ver.0.1  
**作成日:** 2026-11-06  
**対象:** MVP

## 1. 目的

本書は、画像アーカイブ管理ビューアのMVPを実装するための開発環境、採用技術、NuGetパッケージ、ソリューション構成、ビルド設定、テスト方針、ログ方針および実装順序を定義する。

本書で次を確定する。

- 開発に使用するIDEと.NETバージョン
- WPFプロジェクトの基本設定
- SQLiteへのアクセス方法
- MVVM、DI、ログ、テストに使用するライブラリ
- ソースコードと実行時データの配置
- 開発用・配布用ビルドの方針
- MVPを段階的に作る実装順序

## 2. 採用技術の結論

| 区分 | 採用技術 | 方針 |
|---|---|---|
| IDE | Visual Studio 2026 | .NETデスクトップ開発ワークロードを使用 |
| 言語 | C# | Nullable参照型を有効化 |
| ランタイム | .NET 10 LTS | `net10.0-windows` を対象 |
| UI | WPF / XAML | Windows専用デスクトップアプリ |
| UIパターン | MVVM | CommunityToolkit.Mvvmを採用 |
| DI・構成・ログ基盤 | Microsoft.Extensions.Hosting | WPFの起動・終了に統合 |
| DB | SQLite | アプリ内ローカルDB |
| DBアクセス | Microsoft.Data.Sqlite | SQL直書き、パラメーター化クエリ |
| ZIP | System.IO.Compression | ZIP / CBZ読み取り・再構築 |
| 画像 | WPF標準画像API | MVPはJPEG / PNG |
| ログ | Microsoft.Extensions.Logging + Serilog | ローカルファイルへローリング保存 |
| テスト | xUnit | Unit Testと一時ファイルを用いた結合テスト |
| バージョン管理 | Git | GitHubまたは社内Gitリポジトリ |
| パッケージ管理 | NuGet | バージョンをリポジトリ内で固定 |

## 3. 開発環境

### 3.1 必須環境

- Windows 11 x64
- Visual Studio 2026
- .NETデスクトップ開発ワークロード
- .NET 10 SDK
- Git for Windows

Visual Studioでは「WPFアプリケーション」を選び、「WPFアプリケーション (.NET Framework)」は選択しない。

### 3.2 推奨ハードウェア

本アプリ自体は一般的なWindows PCで動作させるが、開発環境では次を推奨する。

- CPU: 4コア以上
- メモリ: 16GB以上
- ストレージ: SSD
- 開発・テスト用空き容量: 20GB以上

ZIP再構築テストでは、元ZIP、一時ZIP、更新前ZIPが一時的に共存するため、実データに近い大容量テストを行う場合は追加の空き容量を確保する。

### 3.3 .NET 10の採用理由

- LTSであり、MVP開発開始時点で長期サポート対象
- WPFの現行.NET実装を利用できる
- C#、非同期処理、DI、ログ、テスト環境を統一できる
- `long` / `Int64`による64bit Unix timeを標準的に扱える

### 3.4 SDKバージョン固定

開発者、CI、将来のビルドでSDK差異を減らすため、ソリューションルートに `global.json` を置く。

初期作成時にインストール済みの.NET 10 SDKを確認し、そのSDK機能バンドを固定する。

例:

```json
{
  "sdk": {
    "version": "10.0.1xx",
    "rollForward": "latestPatch"
  }
}
```

実際の完全なバージョン番号はプロジェクト作成時に `dotnet --list-sdks` で確認して記載する。

## 4. ソリューション構成

MVPでは2プロジェクト構成とする。

```text
K35Viewer/
├─ K35Viewer.slnx
├─ global.json
├─ Directory.Build.props
├─ Directory.Packages.props
├─ README.md
├─ docs/
│  ├─ requirements/
│  ├─ design/
│  └─ decisions/
├─ src/
│  └─ K35Viewer.App/
│     ├─ K35Viewer.App.csproj
│     ├─ App.xaml
│     ├─ App.xaml.cs
│     ├─ Views/
│     ├─ ViewModels/
│     ├─ Domain/
│     ├─ Services/
│     ├─ Infrastructure/
│     │  ├─ Database/
│     │  ├─ Archives/
│     │  ├─ FileSystem/
│     │  ├─ Logging/
│     │  └─ Thumbnails/
│     ├─ Resources/
│     └─ Migrations/
└─ tests/
   └─ K35Viewer.Tests/
      ├─ K35Viewer.Tests.csproj
      ├─ Services/
      ├─ Infrastructure/
      ├─ Fixtures/
      └─ TestData/
```

### 4.1 プロジェクトを増やさない理由

MVP段階ではApplication、Domain、Infrastructureをフォルダと名前空間で分離し、別プロジェクトにはしない。

次の条件が発生した場合に分割を再検討する。

- Infrastructureへの参照をコンパイル時に強制的に制限したい
- 複数アプリからDomain / Applicationを共有する
- テストビルド時間または依存関係が管理しにくくなる
- 動画や追加アーカイブ形式等で実装規模が大きくなる

## 5. WPFプロジェクト設定

`K35Viewer.App.csproj` の基本方針は次のとおり。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <PlatformTarget>x64</PlatformTarget>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

### 5.1 x64固定

MVPはx64を対象とする。

理由:

- 10,000件以上のアーカイブと画像サムネイルを扱う
- 32bitプロセスのメモリ制限を避ける
- 2038年問題とは別に、x86特有の制約を増やさない
- Windows 11 x64を主対象とする

ARM64対応はMVP完成後に検討する。

### 5.2 コード品質設定

`Directory.Build.props` で全プロジェクトに共通設定を適用する。

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

開発初期に外部パッケージ由来の警告等で阻害される場合は、警告全体を無効にせず個別に扱う。

## 6. NuGetパッケージ方針

### 6.1 採用パッケージ

| パッケージ | 用途 | 採用理由 |
|---|---|---|
| `CommunityToolkit.Mvvm` | ObservableObject、RelayCommand、AsyncRelayCommand | WPFで使用可能な軽量MVVM Toolkit |
| `Microsoft.Data.Sqlite` | SQLite接続 | 軽量ADO.NETプロバイダー、SQLを直接制御可能 |
| `Microsoft.Extensions.Hosting` | DI、構成、アプリ寿命 | WPF起動・終了とサービス生成を統合 |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | Options設定 | 将来の設定画面と構成ファイルに備える |
| `Serilog.Extensions.Logging` | Microsoft Loggingとの統合 | `ILogger<T>`を維持したままファイル出力 |
| `Serilog.Sinks.File` | ローカルログファイル | 日次・サイズ単位のローリングログ |
| `xunit.v3` | テストフレームワーク（xUnit v3） | Unit Testと結合テスト |
| `xunit.runner.visualstudio` | Visual Studio Test Explorer | IDEからテスト実行 |
| `Microsoft.NET.Test.Sdk` | テスト基盤 | `dotnet test`とTest Explorer |
| `coverlet.collector` | カバレッジ収集 | 任意の品質確認用途 |

### 6.2 採用しないもの

#### Entity Framework Core

MVPでは採用しない。

理由:

- テーブル数とクエリが限定されている
- SQL、インデックス、トランザクション境界を明示的に管理したい
- スキャン時の一括処理と差分更新を直接制御したい

#### Dapper

MVPでは採用しない。

`Microsoft.Data.Sqlite` のコマンドとリーダーで規模的に対応可能と判断する。SQLマッピングが冗長になった場合に再評価する。

#### 画像処理ライブラリ

MVPではImageSharp等を導入せず、WPFの `BitmapDecoder`、`BitmapImage`、`TransformedBitmap` 等でJPEG / PNG表示とサムネイル生成を行う。

EXIF回転、色管理、巨大画像のメモリ消費等で不足が判明した場合のみ追加ライブラリを評価する。

### 6.3 パッケージバージョン管理

`Directory.Packages.props` にパッケージバージョンを集約し、中央管理する。

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.14" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.14" />
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.14" />
    <PackageVersion Include="Serilog.Extensions.Logging" Version="10.0.0" />
    <PackageVersion Include="Serilog.Sinks.File" Version="7.0.0" />
    <PackageVersion Include="xunit.v3" Version="4.0.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
    <PackageVersion Include="coverlet.collector" Version="10.1.0" />
  </ItemGroup>
</Project>
```

上記は2026-11-06時点でnuget.orgから確認した最新の安定版（preview / rc / dev を除く）である。

| パッケージ | バージョン | 備考 |
|---|---|---|
| `CommunityToolkit.Mvvm` | 8.4.2 | |
| `Microsoft.Data.Sqlite` | 10.0.14 | .NET 10に合わせたバージョン |
| `Microsoft.Extensions.Hosting` | 10.0.14 | 同上 |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | 10.0.14 | 同上 |
| `Serilog.Extensions.Logging` | 10.0.0 | |
| `Serilog.Sinks.File` | 7.0.0 | 8.0.0系はdev版のみのため7.0.0を採用 |
| `xunit.v3` | 4.0.2 | xUnit v3。パッケージ名の「v3」とバージョン番号は別。テストプロジェクトは実行可能ファイルとして作成される |
| `xunit.runner.visualstudio` | 3.1.5 | |
| `Microsoft.NET.Test.Sdk` | 18.11.0 | |
| `coverlet.collector` | 10.1.0 | |

プロジェクト作成時に、`dotnet list package --outdated` 等で最新の安定版を再確認し、差異があれば更新する。

バージョンに `*`、preview、nightlyは使用しない。

## 7. MVVM方針

### 7.1 CommunityToolkit.Mvvmの使用範囲

- `ObservableObject`
- `[ObservableProperty]`
- `[RelayCommand]`
- `[NotifyCanExecuteChangedFor]`
- `AsyncRelayCommand`

### 7.2 ViewModelの責務

ViewModelは次に限定する。

- 表示状態
- 選択状態
- コマンド
- 入力検証
- Application Serviceの呼び出し
- `OperationResult` のUI反映

ZIP、SQLite、ファイル、ごみ箱を直接操作しない。

### 7.3 UIスレッド

- ObservableCollectionの更新はUIスレッドで行う
- ZIP解析、スキャン、DBアクセス、サムネイル生成はUIスレッドで実行しない
- バックグラウンド結果をまとめてUIへ反映し、項目ごとの過剰なDispatcher呼び出しを避ける

## 8. DI・アプリケーション起動

`Microsoft.Extensions.Hosting` を使用し、WPFの起動・終了とホストの寿命を統合する。

概念例:

```csharp
public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        var builder = Host.CreateApplicationBuilder();
        ConfigureServices(builder.Services);
        _host = builder.Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
```

### 8.1 サービス寿命

| 種別 | 寿命案 |
|---|---|
| MainWindow / MainViewModel | Singleton |
| LibraryViewModel | Singleton |
| ViewerViewModel | Singleton |
| ViewerSession | Singleton。ただしアーカイブ離脱時に内容を破棄 |
| LibraryService / ScanService / ArchiveService | Singleton |
| Repository | SingletonまたはTransient。接続自体は操作ごとに作成 |
| SQLite書き込みキュー | Singleton |
| NotificationService | Singleton |
| ThumbnailService | Singleton |

SQLite接続をアプリ全体で1本開きっぱなしにせず、原則として操作単位で接続を開閉する。書き込み順序はキューで制御する。

## 9. SQLite開発方針

### 9.1 接続ライブラリ

`Microsoft.Data.Sqlite` を使用し、SQLはRepository内に集約する。

### 9.2 SQLルール

- 値は必ずパラメーター化する
- 文字列連結でSQL値を組み立てない
- 複数テーブル更新はトランザクションを使用する
- SQL文はRepositoryまたはMigrationsに置き、ViewModelへ書かない
- `long` / `Int64`でUTC Unix timeを読み書きする

### 9.3 DB初期化

アプリ起動時に次を行う。

1. DB保存ディレクトリを作成
2. SQLiteファイルを開く
3. PRAGMA設定を適用
4. `app_metadata.schema_version` を確認
5. 未作成なら初期DDLを適用
6. 古い場合は順番にマイグレーションを適用

### 9.4 マイグレーション

EF Coreを使用しないため、SQLファイルまたはC#埋め込みリソースで管理する。

```text
Migrations/
├─ 0001_initial.sql
├─ 0002_xxx.sql
└─ MigrationRunner.cs
```

1マイグレーションを1トランザクションで実行し、成功後にschema versionを更新する。

## 10. ログ設計

### 10.1 操作履歴

SQLiteの `operation_history` へ保存する。

対象:

- ZIP更新
- 名前変更
- 移動
- 削除
- スキャン最終結果

ユーザー向けの結果を記録する。

### 10.2 アプリケーションログ

`ILogger<T>` を各サービスへ注入し、Serilog経由でファイルへ保存する。

最低限の情報:

- UTC日時
- ログレベル
- 処理名
- Archive ID
- 対象パス
- エラーコード
- 例外メッセージ
- スタックトレース
- 相関IDまたは処理ID

### 10.3 保存方針案

- 日次ローリング
- 1ファイル上限: 20MB
- 保持日数: 30日
- Debugビルドの最低レベル: Debug
- Releaseビルドの最低レベル: Information

ログ閲覧画面はMVP対象外とする。

パス、ファイル名は業務データに当たり得るため、ログを外部へ送信しない。クラウドテレメトリはMVPでは導入しない。

## 11. 実行時データ配置

ユーザーごとのローカルデータは、Windowsのローカルアプリデータ配下へ保存する。

```text
%LOCALAPPDATA%\K35Viewer\
├─ library.db
├─ thumbnails\
├─ logs\
└─ temp\
```

### 11.1 DB

```text
%LOCALAPPDATA%\K35Viewer\library.db
```

### 11.2 サムネイル

```text
%LOCALAPPDATA%\K35Viewer\thumbnails\
```

Archive IDを基準としたファイル名を使用する。

### 11.3 ログ

```text
%LOCALAPPDATA%\K35Viewer\logs\
```

### 11.4 一時ZIP

可能な限り元ZIPと同じフォルダに一時ZIPを作る。

理由:

- 置換時の別ボリュームコピーを避ける
- 元と同じファイルシステム上で処理する
- 空き容量とアクセス権を同じ条件で確認できる

元フォルダへ一時ファイルを作成できない場合、MVPでは更新を中止し、永続データ用の `temp` へ自動退避して続行しない。

## 12. 画像処理方針

### 12.1 対応形式

- JPEG
- PNG

### 12.2 読み込み

ZIPエントリのStreamからWPF画像へ変換する際、ZIP Streamの寿命に依存しないよう、画像を必要なキャッシュオプションで完全ロードしてからStreamを閉じる。

### 12.3 メモリ管理

- 全画像を同時デコードしない
- 現在画像と前後の少数画像のみ保持する
- 一覧サムネイルは表示範囲付近のみ読み込む
- 切替後に不要Bitmapへの参照を解放する
- 大きな画像を原寸のままサムネイルキャッシュへ保持しない

### 12.4 サムネイル形式

MVPのライブラリ用サムネイルはJPEGを基本とする。

透過を維持する必要がある画像やJPEG変換で問題がある場合にPNGを使用できる構造とする。詳細な品質・サイズはプロトタイプ計測で決める。

初期候補:

- 長辺: 300px
- JPEG品質: 80

## 13. ZIP処理方針

### 13.1 読み取り

`System.IO.Compression.ZipArchive` のReadモードを使用する。

### 13.2 再構築

ZIP内エントリの削除は元ZIPを直接更新せず、削除対象以外を新しい一時ZIPへコピーする。

### 13.3 圧縮レベル

MVPでは再構築時に `CompressionLevel.Optimal` を基本候補とする。

ただし、既にJPEG / PNGは圧縮済みであり、圧縮時間に対する容量削減効果が小さい可能性がある。プロトタイプで `NoCompression`、`Fastest`、`Optimal` の処理時間と容量を比較し、初期値を確定する。

### 13.4 メタデータ

可能な範囲で次を維持する。

- エントリ名
- ディレクトリ構造
- エントリ更新日時
- 削除対象外の非画像エントリ

ZIPコメント、外部属性、特殊な追加フィールド等の完全維持はMVPでは保証しない。実データによるスパイクテストを行う。

## 14. Windows連携

### 14.1 ごみ箱

RecycleBinServiceを境界として実装する。

初期スパイクでは次を確認する。

- ローカルファイルをごみ箱へ送れること
- 大容量ZIPで処理結果を取得できること
- UNCパスやネットワークドライブでの挙動
- ユーザーキャンセルと失敗を判別できること

ごみ箱利用不能時に完全削除へ自動切替しない。アーカイブ削除ではユーザー承認時のみ完全削除する。

### 14.2 エクスプローラーで表示

`explorer.exe /select,"対象ファイル"` 相当の処理をFileSystemServiceへ集約する。

パス文字列を安全に引用し、存在確認後に起動する。

## 15. テスト環境

### 15.1 テスト種類

| 種類 | 対象 |
|---|---|
| Unit Test | 自然順ソート、差分判定、ViewerSession、OperationResult |
| Repository Test | 一時SQLite DBを用いたCRUD、トランザクション、64bit時刻 |
| Archive Test | ZIP列挙、画像抽出、再構築、壊れたZIP |
| File Operation Test | 名前変更、移動、同名競合、外部変更検出 |
| UI手動テスト | 仮想化、画像切替、複数選択、トースト、ダイアログ |
| 性能テスト | 10,000件一覧、初回スキャン、差分スキャン、サムネイル |

### 15.2 テストデータ

リポジトリには著作権上問題のない最小データのみ保存する。

```text
TestData/
├─ simple-3-images.zip
├─ nested-folders.zip
├─ no-images.zip
├─ broken.zip
├─ mixed-content.zip
└─ unicode-names.zip
```

大容量・10,000件テストデータは、テスト実行時に生成するか、リポジトリ外のローカルデータを使用する。

### 15.3 ファイル破壊防止

自動テストは必ずテスト専用一時ディレクトリ内で行う。

実ユーザーフォルダ、ごみ箱、NASを通常の自動テストから直接操作しない。ごみ箱連携は明示的な統合テストとして分離する。

## 16. Git運用

### 16.1 ブランチ

MVP初期は複雑なGit Flowを採用せず、次を基本とする。

- `main`: 動作確認済み
- `feature/<topic>`: 機能開発

### 16.2 コミット単位

- DB初期化
- スキャン
- ライブラリ一覧
- ZIP閲覧
- 削除予約
- 再構築

のように、レビュー・切り戻し可能な単位でコミットする。

### 16.3 `.gitignore`

最低限次を除外する。

```text
.vs/
bin/
obj/
TestResults/
coverage/
*.user
*.suo
*.db
*.db-shm
*.db-wal
logs/
thumbnails/
temp/
```

## 17. ビルド・配布方針

### 17.1 開発用

- Debug
- framework-dependent
- x64

### 17.2 MVP試用版

第一候補はself-contained x64とする。

```bash
dotnet publish src/K35Viewer.App \
  -c Release \
  -r win-x64 \
  --self-contained true
```

これにより利用端末へ.NETランタイムを別途導入しなくても実行できる。

単一ファイル化、トリミング、ReadyToRunは初期MVPでは使用しない。

理由:

- WPFやリフレクション利用パッケージとの相性確認が必要
- 起動時間・容量改善より、確実な動作と診断性を優先する

### 17.3 インストーラー

MVP内部試用ではフォルダ配布を基本とする。

正式配布時にMSIXまたはインストーラーを検討する。

## 18. CI方針

リポジトリ環境が決まった時点で、GitHub ActionsまたはAzure Pipelinesを選択する。

MVPで最低限行う処理:

1. `dotnet restore`
2. `dotnet build -c Release`
3. `dotnet test -c Release`
4. テスト結果とカバレッジ出力

Windows固有のWPFビルドと統合テストがあるため、Windows runnerを使用する。

## 19. 実装順序

各段階で実行可能な成果物を作り、次へ進む。

### Phase 0 開発基盤

- ソリューション作成
- WPF起動
- Testプロジェクト作成
- Generic Host、DI、Logging導入
- 実行時ディレクトリ作成

**完了条件:** 空のMainWindowが起動し、ログファイルが生成され、テストが1件成功する。

### Phase 1 DB基盤

- `Microsoft.Data.Sqlite` 導入
- 初期DDL
- MigrationRunner
- Repository基盤
- 64bit時刻テスト

**完了条件:** 初回起動でDBが生成され、再起動で再作成されない。

### Phase 2 フォルダ追加・スキャン

- フォルダ選択
- 再帰列挙
- ZIP / CBZ検出
- フォルダ・アーカイブDB登録
- 開始・完了トースト

**完了条件:** 指定フォルダ配下のZIP / CBZ件数がDBと一致する。

### Phase 3 ライブラリ画面

- フォルダツリー
- アーカイブ詳細一覧
- 検索・ソート
- UI仮想化
- 起動時差分チェック

**完了条件:** 10,000件の生成データで一覧、検索、ソートが実用速度で動く。

### Phase 4 サムネイル

- ZIP先頭画像取得
- サムネイル生成
- ファイルキャッシュ
- 遅延読み込み
- キャッシュ無効化

**完了条件:** 一覧表示時にZIPを毎回開かず、キャッシュから再表示できる。

### Phase 5 ビューア 1枚表示

- ZIP画像エントリ抽出
- 自然順ソート
- 1枚表示
- 前後移動
- フィット・原寸・拡大縮小
- 最終閲覧位置

**完了条件:** JPEG / PNGを含むZIPを連続閲覧できる。

### Phase 6 ビューア 一覧表示

- サムネイルグリッド
- 遅延表示
- 現在位置同期
- 複数選択

**完了条件:** 1枚表示と一覧表示の位置・選択が同期する。

### Phase 7 削除予約

- ViewerSession
- 単一・複数削除予約
- 予約解除
- 離脱確認

**完了条件:** ZIP本体を変更せず、削除予定状態を切り替えられる。

### Phase 8 ZIP再構築

- 一時ZIP作成
- 一時ZIP検証
- キャンセル安全境界
- 外部変更チェック
- ごみ箱退避
- 新ZIP置換

**完了条件:** 複数削除を1回の再構築で反映し、失敗時は元ZIPを保持する。

### Phase 9 ファイル管理・履歴

- 名前変更
- 移動
- アーカイブ削除
- OperationResult
- 操作履歴
- トースト

**完了条件:** アプリ内操作でArchive IDを維持し、履歴が記録される。

### Phase 10 安定化

- 例外処理
- ログ確認
- 壊れたZIP
- NAS切断
- ディスク容量不足
- 10,000件性能検証
- 配布ビルド

**完了条件:** MVP要件とユースケースの主要正常系・異常系を確認できる。

## 20. 最初に行う技術スパイク

本実装前に小さな検証コードで次を確認する。

1. `System.IO.Compression` で実データのZIP / CBZを列挙・再構築できるか
2. JPEG / PNGをZIP StreamからWPF画像へ安全にロードできるか
3. 300MB以上のZIP再構築時間と一時容量
4. `NoCompression` / `Fastest` / `Optimal` の比較
5. Windowsごみ箱へ大容量ファイルを移動できるか
6. UNC / NASでごみ箱が使えない場合の返却結果
7. 10,000件のSQLite部分一致検索速度
8. WPFの一覧仮想化とサムネイル遅延読み込み
9. ログのローリングと例外記録
10. self-contained win-x64配布物の起動

スパイクコードは本体へそのまま持ち込まず、結果を設計判断として記録する。

## 21. 未確定事項・レビュー項目

1. （確定）製品名は「35Viewer」、ソリューション名・プロジェクト名は `K35Viewer` とする。
2. （確定）デフォルト名前空間は `K35Viewer` とする（C#の識別子は数字で始められないため）。
3. Serilogを採用し、日次・20MB・30日保持でよいか。
4. サムネイルの初期値を長辺300px、JPEG品質80としてよいか。
5. ZIP再構築時の圧縮レベルをスパイク結果で確定する方針でよいか。
6. SQLite接続を操作単位で開閉し、書き込みのみ単一キュー化する方針でよいか。
7. x64専用でMVPを開始してよいか。
8. MVP試用版をself-containedのフォルダ配布としてよいか。
9. Gitホスティング先をGitHub、Azure DevOps、社内Gitのどれにするか。
10. CIをMVP開始時から入れるか、Phase 3完了後に入れるか。
