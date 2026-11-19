using K35Viewer.Domain;

namespace K35Viewer.Infrastructure.Settings;

/// <summary>
/// メインウィンドウの位置と大きさ（通常の状態のもの）と、最大化していたか。座標は、WPF の単位（1/96 インチ）。
/// </summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized)
{
    /// <summary>これより小さいウィンドウは、復元しない（壊れた値や、最小化したときの値を避ける）。</summary>
    public const double MinSize = 320;

    /// <summary>画面の外に出ていても、タイトルバーをつかんで戻せるように、画面の中に残っていてほしい大きさ。</summary>
    public const double MinVisible = 100;

    /// <summary>
    /// 復元してよいか。値が有限で、大きさが十分にあり、仮想スクリーン（すべてのモニターを含む領域）の中に、
    /// 横 <see cref="MinVisible"/>、縦 <see cref="MinVisible"/> 以上が入っている（モニターを外した、解像度を下げた場合に、画面の外へ出さない）。
    /// </summary>
    public bool IsUsable(double screenLeft, double screenTop, double screenWidth, double screenHeight)
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Width) || !double.IsFinite(Height)
            || Width < MinSize || Height < MinSize)
        {
            return false;
        }

        var overlapWidth = Math.Min(Left + Width, screenLeft + screenWidth) - Math.Max(Left, screenLeft);
        var overlapHeight = Math.Min(Top + Height, screenTop + screenHeight) - Math.Max(Top, screenTop);
        return overlapWidth >= MinVisible && overlapHeight >= MinVisible && Top >= screenTop - 1;
    }
}

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

    // 終了時の状態（GitHub の Issue #26）。次に起動したとき、この状態から始める。
    // 古い版のファイルにはない項目なので、ないときは、これまでどおりの既定値で始まる。

    /// <summary>メインウィンドウの位置と大きさ。null なら、既定の位置と大きさで始める。</summary>
    public WindowPlacement? MainWindowPlacement { get; init; }

    /// <summary>ライブラリの一覧の並べ替え項目。</summary>
    public ArchiveSortKey LibrarySortKey { get; init; } = ArchiveSortKey.FileName;

    /// <summary>ライブラリの一覧を降順で並べるか。</summary>
    public bool LibrarySortDescending { get; init; }

    /// <summary>ライブラリの一覧を、新着だけに絞り込むか。</summary>
    public bool LibraryShowNewOnly { get; init; }

    /// <summary>起動したとき、前回の終了時にビューアで開いていたアーカイブを、続きから開くか。</summary>
    public bool ResumeLastArchive { get; init; }

    /// <summary>前回の終了時に、ビューアで開いていたアーカイブの ID。ライブラリ画面で終了したときは null。</summary>
    public long? LastOpenArchiveId { get; init; }
}
