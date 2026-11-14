using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using K35Viewer.Infrastructure.Thumbnails;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>1件のアーカイブの解析・サムネイル生成が終わったことの通知。画面の表示を更新するために使う。</summary>
public sealed record ArchiveProcessed(
    long ArchiveId,
    string AnalysisStatus,
    int? ImageCount,
    string? ThumbnailPath,
    string? ThumbnailStatus);

public sealed record ThumbnailProgress(int Completed, int Total);

/// <summary>ProcessPendingAsync の結果。Total は対象件数、Processed は処理を終えた件数。</summary>
public sealed record ThumbnailRunSummary(int Total, int Processed, int Failed);

/// <summary>
/// アーカイブの解析(画像枚数・表紙の特定)と、ライブラリ用サムネイルの生成・キャッシュ管理。
/// 一覧の表示はキャッシュだけで行い、ZIPを開くのはここでの生成時だけに限る。
/// 生成はバックグラウンドで、同時に <see cref="Parallelism"/> 件まで行う。
/// </summary>
public sealed partial class ThumbnailService(
    IArchiveReader reader,
    ThumbnailGenerator generator,
    ThumbnailCacheStore cache,
    FileSystemService fileSystem,
    ArchiveRepository archives,
    ThumbnailRepository thumbnails,
    DatabaseWriter writer,
    ILogger<ThumbnailService> logger,
    IReadOnlyList<TimeSpan>? retryDelays = null)
{
    /// <summary>
    /// 一時的な読み取りエラー（他のプログラム、特にウイルス対策ソフトがZIPをスキャンしている最中の共有違反や、
    /// ネットワークの瞬断など）のときに、続けて再試行するまでの待ち時間。この回数だけ再試行する。
    /// </summary>
    public static IReadOnlyList<TimeSpan> DefaultRetryDelays { get; } =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)];

    private readonly IReadOnlyList<TimeSpan> _retryDelays = retryDelays ?? DefaultRetryDelays;

    /// <summary>表紙として読み込む画像1枚の上限サイズ。これを超える画像は表紙にしない。</summary>
    public const long MaxImageBytes = 64L * 1024 * 1024;

    /// <summary>同時に処理するアーカイブ数。CPUとディスク(NAS)への負荷を抑えるため、少なめにする。</summary>
    public const int Parallelism = 2;

    private readonly object _lock = new();
    private readonly HashSet<long> _errorsThisSession = [];
    private int _running;

    /// <summary>1件の処理が終わるたびに、処理を行っているスレッドから発行される。</summary>
    public event EventHandler<ArchiveProcessed>? ArchiveProcessed;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// 解析またはサムネイル生成が必要なアーカイブをすべて処理する(UC-02 手順6〜7、キャッシュの再生成)。
    /// 既に実行中の場合は何もせず null を返す。
    /// ファイルが失われたサムネイルは、先に再生成待ちに戻す。
    /// </summary>
    public async Task<ThumbnailRunSummary?> ProcessPendingAsync(
        IProgress<ThumbnailProgress>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            await RepairMissingFilesAsync(ct);

            // 前回ファイルを読めなかった(ERROR)ものは、起動ごとに1回だけ再試行する
            var ids = (await archives.GetPendingProcessingIdsAsync(ct))
                .Where(id => !WasErrorThisSession(id))
                .ToList();

            var completed = 0;
            var failed = 0;
            progress?.Report(new ThumbnailProgress(0, ids.Count));

            await Parallel.ForEachAsync(
                ids,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                async (id, token) =>
                {
                    try
                    {
                        await ProcessAsync(id, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 1件の失敗で全体を止めない
                        Interlocked.Increment(ref failed);
                        LogProcessFailed(logger, ex, id);
                    }

                    // 件数の加算は、進捗の報告先があるかどうかに関わらず必ず行う
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new ThumbnailProgress(done, ids.Count));
                });

            LogRunCompleted(logger, ids.Count, failed);
            return new ThumbnailRunSummary(ids.Count, completed, failed);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>キャッシュに保存しているサムネイルの合計の大きさ（バイト）。</summary>
    public Task<long> GetCacheSizeAsync(CancellationToken ct = default) =>
        Task.Run(cache.GetTotalBytes, ct);

    /// <summary>
    /// サムネイルのキャッシュをすべて削除する（要件定義 24.5）。記録（DB）を消してからファイルを消す。
    /// 作り直しは <see cref="ProcessPendingAsync"/> が行う（記録のないアーカイブが対象になる）。
    /// サムネイルの作成中は実行せず、false を返す。
    /// </summary>
    public async Task<bool> ClearCacheAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            await writer.ExecuteAsync(
                async (connection, transaction, token) => await thumbnails.DeleteAllAsync(connection, transaction, token),
                ct);
            var deleted = await Task.Run(cache.Clear, ct);
            LogCacheCleared(logger, deleted);
            return true;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// 1件のアーカイブを解析し、サムネイルを生成する。対象が見つからない、またはファイルがDBの記録と
    /// 食い違う(外部で変更された)場合は、何も更新せず null を返す(次回のスキャンで反映される)。
    /// </summary>
    public async Task<ArchiveProcessed?> ProcessAsync(long archiveId, CancellationToken ct = default)
    {
        var target = await archives.GetProcessingTargetAsync(archiveId, ct);
        if (target is null)
        {
            return null;
        }

        // 解析の前にファイルの状態を確認する。DBの記録と違う場合、その内容はスキャンが反映するまで扱わない
        var state = fileSystem.TryGetFileState(target.FullPath);
        if (state is { } current
            && (current.FileSize != target.FileSize || current.LastWriteTime != target.LastWriteTime))
        {
            LogSkippedChanged(logger, archiveId);
            return null;
        }

        var work = state is null
            ? new ProcessingResult(
                new ArchiveAnalysis(ArchiveAnalysisStatus.Error, 0, [], "FILE_NOT_FOUND", "ファイルが見つかりません"),
                null)
            : await Task.Run(() => AnalyzeAndRender(target, ct), ct);

        var result = await PersistAsync(target, work, ct);
        if (result is not null)
        {
            if (work.Analysis.Status == ArchiveAnalysisStatus.Error)
            {
                lock (_lock)
                {
                    _errorsThisSession.Add(archiveId);
                }
            }

            ArchiveProcessed?.Invoke(this, result);
        }

        return result;
    }

    /// <summary>ZIPを開いて解析し、READYなら表紙をサムネイルにする。DBには触れない(CPU・ディスクを使う部分)。</summary>
    private ProcessingResult AnalyzeAndRender(ArchiveProcessingTarget target, CancellationToken ct)
    {
        var analysis = AnalyzeWithRetry(target, ct);
        if (analysis.Status != ArchiveAnalysisStatus.Ready)
        {
            return new ProcessingResult(analysis, null);
        }

        // 先頭の画像が壊れている場合は、次の画像を表紙にする
        string? lastError = null;
        foreach (var entry in analysis.CoverCandidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = reader.ReadEntry(target.FullPath, entry, MaxImageBytes, ct);
                var thumbnail = generator.Generate(data);
                return new ProcessingResult(analysis, new RenderedCover(entry, thumbnail));
            }
            catch (Exception ex) when (ex is ThumbnailGenerationException or InvalidDataException or IOException
                or NotSupportedException or UnauthorizedAccessException)
            {
                lastError = ex.Message;
                LogCoverFailed(logger, target.Id, entry, ex.Message);
            }
        }

        return new ProcessingResult(analysis, null, lastError ?? "表紙にできる画像がありません");
    }

    /// <summary>
    /// アーカイブを解析する。ファイルを読めなかった原因が一時的なもの（I/Oエラー、アクセス拒否）の場合は、
    /// 短い間隔で再試行する。壊れている・暗号化されている・存在しない等、待っても変わらないものは再試行しない。
    /// </summary>
    private ArchiveAnalysis AnalyzeWithRetry(ArchiveProcessingTarget target, CancellationToken ct)
    {
        var analysis = reader.Analyze(target.FullPath, ct);
        foreach (var delay in _retryDelays)
        {
            if (!IsTransientError(analysis))
            {
                break;
            }

            LogRetrying(logger, target.Id, analysis.ErrorCode, delay.TotalMilliseconds);
            if (ct.WaitHandle.WaitOne(delay))
            {
                ct.ThrowIfCancellationRequested();
            }

            analysis = reader.Analyze(target.FullPath, ct);
        }

        return analysis;
    }

    private static bool IsTransientError(ArchiveAnalysis analysis) =>
        analysis.Status == ArchiveAnalysisStatus.Error && analysis.ErrorCode is "IO_ERROR" or "ACCESS_DENIED";

    /// <summary>解析結果とサムネイルをDBへ反映する(ファイルの保存 → 1トランザクション)。</summary>
    private async Task<ArchiveProcessed?> PersistAsync(
        ArchiveProcessingTarget target, ProcessingResult work, CancellationToken ct)
    {
        var analysis = work.Analysis;
        var isReady = analysis.Status == ArchiveAnalysisStatus.Ready;

        // 画像ファイルは先に保存する。DBの更新に失敗しても、残るのは次回上書きされるファイルだけ
        string? savedPath = null;
        if (work.Cover is { } cover)
        {
            savedPath = cache.Save(target.Id, cover.Thumbnail.Data);
        }

        string? staleFile = null;
        var applied = await writer.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                var now = UnixTime.Now();
                var updated = await archives.UpdateAnalysisAsync(
                    connection,
                    transaction,
                    target,
                    analysis.Status,
                    isReady || analysis.Status == ArchiveAnalysisStatus.Empty ? analysis.ImageCount : null,
                    isReady ? analysis.CoverCandidates[0] : null,
                    analysis.ErrorCode,
                    analysis.ErrorMessage,
                    now,
                    token);
                if (!updated)
                {
                    return false; // 解析中にアーカイブが変更された。スキャンが反映した状態を優先する
                }

                if (!isReady)
                {
                    // 画像として扱えないアーカイブには、サムネイルを持たせない
                    staleFile = await thumbnails.DeleteAsync(connection, transaction, target.Id, token);
                }
                else if (work.Cover is { } rendered && savedPath is not null)
                {
                    await thumbnails.UpsertReadyAsync(
                        connection,
                        transaction,
                        target.Id,
                        savedPath,
                        rendered.Thumbnail.Width,
                        rendered.Thumbnail.Height,
                        rendered.Entry,
                        target.FileSize,
                        target.LastWriteTime,
                        now,
                        token);
                }
                else
                {
                    await thumbnails.UpsertFailedAsync(
                        connection,
                        transaction,
                        target.Id,
                        target.FileSize,
                        target.LastWriteTime,
                        work.CoverError ?? "サムネイルを作成できません",
                        now,
                        token);
                    staleFile = ThumbnailCacheStore.GetRelativePath(target.Id);
                }

                return true;
            },
            ct);

        if (!applied)
        {
            LogSkippedChanged(logger, target.Id);
            return null;
        }

        if (staleFile is not null)
        {
            cache.Delete(staleFile);
        }

        var thumbnailStatus = !isReady ? null : savedPath is not null ? "READY" : "FAILED";
        return new ArchiveProcessed(
            target.Id,
            analysis.Status.ToDbValue(),
            isReady || analysis.Status == ArchiveAnalysisStatus.Empty ? analysis.ImageCount : null,
            savedPath,
            thumbnailStatus);
    }

    /// <summary>キャッシュのファイルが失われているサムネイルを、再生成待ち(STALE)に戻す。</summary>
    private async Task RepairMissingFilesAsync(CancellationToken ct)
    {
        var ready = await thumbnails.GetReadyAsync(ct);
        var missing = ready.Where(t => !cache.Exists(t.RelativePath)).Select(t => t.ArchiveId).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        await writer.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                foreach (var id in missing)
                {
                    await thumbnails.MarkStaleAsync(connection, transaction, id, token);
                }
            },
            ct);
        LogRepaired(logger, missing.Count);
    }

    private bool WasErrorThisSession(long id)
    {
        lock (_lock)
        {
            return _errorsThisSession.Contains(id);
        }
    }

    private sealed record RenderedCover(string Entry, GeneratedThumbnail Thumbnail);

    private sealed record ProcessingResult(ArchiveAnalysis Analysis, RenderedCover? Cover, string? CoverError = null);

    [LoggerMessage(Level = LogLevel.Information, Message = "サムネイル生成が終了しました。対象={Total} 失敗={Failed}")]
    private static partial void LogRunCompleted(ILogger logger, int total, int failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "サムネイルのキャッシュを削除しました。削除したファイル={Count}")]
    private static partial void LogCacheCleared(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "ファイルが失われたサムネイルを再生成待ちにしました。件数={Count}")]
    private static partial void LogRepaired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "アーカイブを一時的に読めなかったため、再試行します。ArchiveId={Id} ErrorCode={Code} 待ち={DelayMs}ms")]
    private static partial void LogRetrying(ILogger logger, long id, string? code, double delayMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "アーカイブが記録と異なる(外部で変更された)ため、処理を見送りました。ArchiveId={Id}")]
    private static partial void LogSkippedChanged(ILogger logger, long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "表紙候補の画像を読み込めませんでした。ArchiveId={Id} Entry={Entry} Reason={Reason}")]
    private static partial void LogCoverFailed(ILogger logger, long id, string entry, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "アーカイブの処理に失敗しました。ArchiveId={Id}")]
    private static partial void LogProcessFailed(ILogger logger, Exception exception, long id);
}
