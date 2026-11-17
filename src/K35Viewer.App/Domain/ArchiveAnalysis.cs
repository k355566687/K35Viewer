namespace K35Viewer.Domain;

/// <summary>アーカイブ解析の結果種別(archives.analysis_status のうち、解析で決まるもの)。</summary>
public enum ArchiveAnalysisStatus
{
    /// <summary>対応画像が1枚以上ある。</summary>
    Ready,

    /// <summary>正常なアーカイブだが、対応画像がない。</summary>
    Empty,

    /// <summary>ZIPとして読めない(破損)。</summary>
    Broken,

    /// <summary>暗号化されている等、MVPでは扱えない。</summary>
    Unsupported,

    /// <summary>ファイルを読めなかった(存在しない、アクセス拒否など)。一時的な可能性があり、次回起動時に再試行する。</summary>
    Error,
}

public static class ArchiveAnalysisStatusExtensions
{
    public static string ToDbValue(this ArchiveAnalysisStatus status) => status switch
    {
        ArchiveAnalysisStatus.Ready => "READY",
        ArchiveAnalysisStatus.Empty => "EMPTY",
        ArchiveAnalysisStatus.Broken => "BROKEN",
        ArchiveAnalysisStatus.Unsupported => "UNSUPPORTED",
        ArchiveAnalysisStatus.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>
/// ZIP / CBZ を1回開いて得た情報。ImageCount は対応画像(JPEG / PNG / GIF / WebP)の数。
/// CoverCandidates は自然順で先頭から数枚の画像エントリ名で、表紙サムネイルの元画像の候補(先頭が表紙)。
/// </summary>
public sealed record ArchiveAnalysis(
    ArchiveAnalysisStatus Status,
    int ImageCount,
    IReadOnlyList<string> CoverCandidates,
    string? ErrorCode = null,
    string? ErrorMessage = null);
