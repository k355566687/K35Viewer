namespace K35Viewer.Infrastructure.Settings;

/// <summary>外観（要件定義 24.2）。</summary>
public enum AppTheme
{
    Dark,
    Light,

    /// <summary>Windows の設定（アプリのモード）に合わせる。</summary>
    System,
}

/// <summary>ライブラリ画面の表示方式。</summary>
public enum LibraryViewMode
{
    Detail,
    Thumbnail,
}

/// <summary>ビューアの表示方式。</summary>
public enum ViewerViewMode
{
    /// <summary>1枚表示。</summary>
    SinglePage,

    /// <summary>一覧表示。</summary>
    List,
}

/// <summary>
/// アプリの設定（要件定義 24章）。設定のファイル（settings.json）に保存する。
/// ファイルにない項目は既定値になる（古い版で作ったファイルを読んでも、新しい項目は既定値で動く）。
/// </summary>
public sealed record AppSettings
{
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>削除予約のあるアーカイブから離れるとき、確認する（DLG-01）。false なら確認せずに更新して離れる。</summary>
    public bool ConfirmBeforeLeave { get; init; } = true;

    /// <summary>アーカイブをごみ箱へ移す前に、確認する（DLG-02）。完全削除の確認は、この設定によらず常に行う。</summary>
    public bool ConfirmBeforeDelete { get; init; } = true;

    /// <summary>自動で作ったバックアップを残す数（要件定義 24.5）。範囲は <see cref="MinAutoBackupKeepCount"/> 〜 <see cref="MaxAutoBackupKeepCount"/>。</summary>
    public int AutoBackupKeepCount { get; init; } = DefaultAutoBackupKeepCount;

    public const int MinAutoBackupKeepCount = 1;

    public const int MaxAutoBackupKeepCount = 20;

    public const int DefaultAutoBackupKeepCount = 5;

    public LibraryViewMode LibraryViewMode { get; init; } = LibraryViewMode.Detail;

    public ViewerViewMode ViewerViewMode { get; init; } = ViewerViewMode.SinglePage;

    /// <summary>
    /// ショートカットを作るかを、すでに利用者に聞いた（要件定義 24.8）。聞くのは一度だけ。
    /// 古い版のファイルにはない項目なので、更新した最初の起動で、一度だけ聞く。
    /// </summary>
    public bool ShortcutPromptShown { get; init; }
}
