using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>ZIP への変換の前の確認（確認ダイアログに出す内容）。</summary>
/// <param name="NewFileName">変換後の名前（.rar / .7z は .zip、.cbr / .cb7 は .cbz）。</param>
/// <param name="KeepOriginalReason">元のファイルを、ごみ箱へ移せずに残す理由（ネットワークドライブなど）。移せるなら null。</param>
public sealed record ZipConversionPlan(string NewFileName, string? KeepOriginalReason);

/// <summary>
/// RAR / 7z から ZIP への変換（GitHub の Issue #20。内部アーキテクチャ設計書 39章）。
/// </summary>
public sealed partial class ArchiveService
{
    /// <summary>テスト用。変換の処理を差し替える（失敗を再現するため）。</summary>
    internal IZipConverter Converter { get; init; } = new ZipConverter();

    /// <summary>変換後の名前。RAR / CBR → ZIP / CBZ（漫画用の拡張子は、漫画用のまま）。</summary>
    public static string ZipFileNameFor(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return extension.Equals(".cbr", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cb7", StringComparison.OrdinalIgnoreCase)
            ? baseName + ".cbz"
            : baseName + ".zip";
    }

    /// <summary>
    /// 変換できるかを確かめ、変換後の名前と、元のファイルを残す理由を返す。変換できない場合は、理由を <paramref name="error"/> に入れて null。
    /// </summary>
    public ZipConversionPlan? PlanZipConversion(string folderPath, string fileName, out string? error)
    {
        var source = Path.Combine(folderPath, fileName);
        if (fileSystem.TryGetFileState(source) is null)
        {
            error = MissingFileMessage(fileName);
            return null;
        }

        var format = ArchiveFormats.Detect(source);
        if (format is not (ArchiveFormat.Rar or ArchiveFormat.SevenZip))
        {
            error = format == ArchiveFormat.Zip
                ? Format($"「{fileName}」は、すでに ZIP 形式です（名前の拡張子だけが違います）。変換は要りません")
                : Format($"「{fileName}」は、RAR / 7z ではないため、変換できません");
            return null;
        }

        var newName = ZipFileNameFor(fileName);
        if (fileSystem.FileExists(Path.Combine(folderPath, newName)))
        {
            error = Format($"同じ名前のファイルが既にあるため、変換できません: {newName}。どちらかの名前を変えてから、変換してください");
            return null;
        }

        error = null;
        return new ZipConversionPlan(newName, recycleBin.CheckAvailability(source));
    }

    /// <summary>変換できない理由を知らせる（<see cref="PlanZipConversion"/> で断られたとき。画面から呼ぶ）。</summary>
    public void NotifyConversionBlocked(string reason) => notifications.ShowWarning(reason);

    /// <summary>
    /// RAR / 7z を ZIP に変換する。全部のエントリ（画像以外のファイルとフォルダを含む）を、同じ名前で、新しい ZIP へ写し、
    /// 開き直して中身を確かめてから、同じフォルダに置く。Archive ID、閲覧位置、サムネイルは引き継ぐ。
    /// 元のファイルは、ZIP を置いて、記録を更新したあとに、ごみ箱へ移す。ごみ箱を使えない保存先、または移せなかった場合は、
    /// 元のファイルを残す（次のスキャンで、別のアーカイブとして登録される）。
    /// ZIP の作成と確認まではキャンセルでき、それより前の失敗・キャンセルでは、何も変わらない。結果は通知する。
    /// </summary>
    public async Task<OperationResult> ConvertToZipAsync(
        long archiveId, IProgress<ArchiveUpdatePhase>? progress = null, CancellationToken ct = default)
    {
        if (await FindAsync(archiveId, ct) is not { } info)
        {
            return NotFoundResult(OperationType.ArchiveConvert);
        }

        // 同じアーカイブへの更新・変換は同時に行わない
        lock (_gate)
        {
            if (!_updating.Add(archiveId))
            {
                return Reject(
                    OperationType.ArchiveConvert, "UPDATE_IN_PROGRESS", Format($"「{info.FileName}」はすでに更新中です"), info);
            }
        }

        try
        {
            return await ConvertCoreAsync(info, progress, ct);
        }
        finally
        {
            lock (_gate)
            {
                _ = _updating.Remove(archiveId);
            }
        }
    }

    private async Task<OperationResult> ConvertCoreAsync(
        ArchiveOpenInfo info, IProgress<ArchiveUpdatePhase>? progress, CancellationToken ct)
    {
        var source = info.FullPath;
        var tempPath = string.Empty;
        string destination;
        ZipConversionPlan plan;
        (long FileSize, long LastWriteTime) before;
        try
        {
            // ---- 準備: 変換できるか確かめる ----
            Report(progress, ArchiveUpdatePhase.Preparing);
            ct.ThrowIfCancellationRequested();

            if (PlanZipConversion(info.FolderPath, info.FileName, out var error) is not { } planned)
            {
                return Reject(OperationType.ArchiveConvert, "CANNOT_CONVERT", error!, info);
            }

            plan = planned;
            destination = Path.Combine(info.FolderPath, plan.NewFileName);
            if (fileSystem.TryGetFileState(source) is not { } state)
            {
                return Reject(OperationType.ArchiveConvert, "FILE_NOT_FOUND", MissingFileMessage(info.FileName), info);
            }

            before = state;
            var required = await Task.Run(() => Converter.EstimateSize(source, ct), ct) ?? state.FileSize;
            if ((FreeSpaceProvider ?? fileSystem.GetAvailableFreeSpace)(source) is { } free && free < required + FreeSpaceMargin)
            {
                return Reject(
                    OperationType.ArchiveConvert,
                    "DISK_FULL",
                    Format($"ディスクの空き容量が不足しているため、「{info.FileName}」を変換できません。元のアーカイブは変更していません"),
                    info);
            }

            notifications.ShowInfo(Format($"「{info.FileName}」を ZIP に変換しています"));
            LogConvertStarted(logger, info.Id);

            // ---- ZIP の作成と確認（ここまではキャンセルできる） ----
            tempPath = fileSystem.CreateTempPath(source);
            Report(progress, ArchiveUpdatePhase.Rebuilding);
            var converted = await Task.Run(() => Converter.Convert(source, tempPath, ct), ct);
            if (!converted.Succeeded)
            {
                return await ConvertFailedAsync(info, converted, tempPath, destination);
            }

            Report(progress, ArchiveUpdatePhase.Verifying);
            var verified = await Task.Run(() => writer.Verify(tempPath, converted.Entries ?? [], ct), ct);
            if (!verified.Succeeded)
            {
                return await ConvertFailedAsync(info, verified, tempPath, destination);
            }

            // 置く直前に、元のファイルが、その間に変更されていないことを確かめる
            ct.ThrowIfCancellationRequested();
            if (IsChangedExternally(source, new ArchiveFileState(before.FileSize, before.LastWriteTime)))
            {
                _ = TryDeleteTemp(tempPath);
                return Reject(
                    OperationType.ArchiveConvert,
                    "ARCHIVE_CHANGED",
                    Format($"「{info.FileName}」が変換の途中で外部から変更されたため、変換を中止しました。元のアーカイブは変更していません"),
                    info);
            }
        }
        catch (OperationCanceledException)
        {
            _ = TryDeleteTemp(tempPath);
            var message = Format($"「{info.FileName}」の変換をキャンセルしました。元のアーカイブは変更していません");
            notifications.ShowInfo(message);
            return new OperationResult(
                OperationType.ArchiveConvert, OperationStatus.Cancelled, message, info.Id, info.FileName, ErrorCode: "CANCELLED");
        }
        catch (Exception ex)
        {
            LogFileOperationFailed(logger, ex, "ZIP への変換", info.Id);
            _ = TryDeleteTemp(tempPath);
            return await FailAsync(
                OperationType.ArchiveConvert,
                "CONVERT_FAILED",
                Format($"「{info.FileName}」を変換できませんでした。元のアーカイブは変更していません"),
                info,
                source,
                null);
        }

        // ---- ここから先はキャンセルしない ----
        return await PlaceConvertedAsync(info, plan, tempPath, destination, before, progress);
    }

    /// <summary>
    /// 確かめた ZIP を、変換後の名前で置き、記録を更新して、元のファイルをごみ箱へ移す。
    /// 元のファイルを移すのは最後なので、途中で失敗しても、元のファイルは残る。
    /// </summary>
    private async Task<OperationResult> PlaceConvertedAsync(
        ArchiveOpenInfo info,
        ZipConversionPlan plan,
        string tempPath,
        string destination,
        (long FileSize, long LastWriteTime) before,
        IProgress<ArchiveUpdatePhase>? progress)
    {
        var source = info.FullPath;
        Report(progress, ArchiveUpdatePhase.Replacing);

        // 一時ファイルを、変換後の名前にする（ウイルス対策ソフト等が一時的に掴んでいる場合に備えて、少し待って再試行する）
        Exception? placeError = null;
        for (var attempt = 0; attempt <= ReplaceDelays.Count; attempt++)
        {
            try
            {
                fileSystem.MoveFile(tempPath, destination);
                placeError = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                placeError = ex;
                if (attempt < ReplaceDelays.Count)
                {
                    await Task.Delay(ReplaceDelays[attempt]);
                }
            }
        }

        if (placeError is not null)
        {
            LogFileOperationFailed(logger, placeError, "変換した ZIP を置く", info.Id);
            _ = TryDeleteTemp(tempPath);
            return await FailAsync(
                OperationType.ArchiveConvert,
                "PLACE_FAILED",
                Format($"「{info.FileName}」を変換できませんでした（変換した ZIP を置けませんでした）。元のアーカイブは変更していません"),
                info,
                source,
                destination);
        }

        // ---- 記録の更新 ----
        Report(progress, ArchiveUpdatePhase.Finishing);
        var after = fileSystem.TryGetFileState(destination);
        var extension = ArchiveFormats.ToDatabaseExtension(Path.GetExtension(plan.NewFileName));
        try
        {
            var placed = after ?? throw new FileNotFoundException("変換した ZIP を確認できません", destination);
            await databaseWriter.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    await archives.UpdateAfterConvertAsync(
                        connection, transaction, info.Id, plan.NewFileName, extension, before, placed, UnixTime.Now(), token);
                    await history.InsertAsync(
                        connection,
                        transaction,
                        new OperationResult(
                            OperationType.ArchiveConvert,
                            OperationStatus.Success,
                            Format($"「{info.FileName}」を ZIP に変換しました（{plan.NewFileName}）"),
                            info.Id,
                            plan.NewFileName,
                            1,
                            source,
                            destination),
                        token);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 記録を更新できなかった。記録と食い違わないよう、置いた ZIP を消して、元の状態に戻す
            LogRecordFailed(logger, ex, info.Id);
            var removed = fileSystem.TryDelete(destination);
            return await FailAsync(
                OperationType.ArchiveConvert,
                "DATABASE_ERROR",
                removed
                    ? Format($"「{info.FileName}」を変換できませんでした（記録の更新に失敗しました）。元のアーカイブは変更していません")
                    : Format($"「{info.FileName}」を変換できませんでした（記録の更新に失敗しました）。作成した「{plan.NewFileName}」が残っています。ライブラリを再スキャンしてください"),
                info,
                source,
                destination);
        }

        // ---- 元のファイルの片付け ----
        string message;
        var keptOriginal = true;
        if (plan.KeepOriginalReason is { } keepReason)
        {
            message = Format($"「{info.FileName}」を ZIP に変換しました（{plan.NewFileName}）。元のアーカイブは、ごみ箱へ移せないため残しています（{keepReason}）");
        }
        else
        {
            var moved = await Task.Run(() => recycleBin.MoveToRecycleBin(source));
            if (moved.Moved || !File.Exists(source))
            {
                keptOriginal = false;
                message = Format($"「{info.FileName}」を ZIP に変換しました（{plan.NewFileName}）。元のアーカイブは、ごみ箱へ移しました");
            }
            else
            {
                LogRecycleFailed(logger, info.Id, moved.Status, moved.Message);
                message = Format($"「{info.FileName}」を ZIP に変換しました（{plan.NewFileName}）。元のアーカイブは、ごみ箱へ移せなかったため残っています。他のプログラムが使用中の可能性があります");
            }
        }

        // 残した元のファイルは、次のスキャンで、別のアーカイブとして登録される
        if (keptOriginal)
        {
            notifications.ShowWarning(message);
        }
        else
        {
            notifications.ShowSuccess(message);
        }

        LogConverted(logger, info.Id, keptOriginal);
        return new OperationResult(
            OperationType.ArchiveConvert,
            keptOriginal ? OperationStatus.Partial : OperationStatus.Success,
            message,
            info.Id,
            plan.NewFileName,
            1,
            source,
            destination);
    }

    private async Task<OperationResult> ConvertFailedAsync(
        ArchiveOpenInfo info, ArchiveWriteResult result, string tempPath, string destination)
    {
        _ = TryDeleteTemp(tempPath);
        var code = result.ErrorCode ?? "CONVERT_FAILED";
        var reason = code switch
        {
            "DISK_FULL" => "ディスクの空き容量が不足しています",
            "ACCESS_DENIED" => "アクセスが拒否されました",
            "ARCHIVE_BROKEN" => "アーカイブの内容を読み取れません",
            "ARCHIVE_ENCRYPTED" => "暗号化されています",
            "ARCHIVE_MULTIVOLUME" => "分割されています",
            "VERIFY_FAILED" => "作成した ZIP を確認できませんでした",
            _ => "ファイルの読み書きに失敗しました",
        };
        LogRebuildFailed(logger, info.Id, result.ErrorCode, result.ErrorMessage);
        return await FailAsync(
            OperationType.ArchiveConvert,
            code,
            Format($"「{info.FileName}」を変換できませんでした（{reason}）。元のアーカイブは変更していません"),
            info,
            info.FullPath,
            destination);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ZIP への変換を開始します。ArchiveId={Id}")]
    private static partial void LogConvertStarted(ILogger logger, long id);

    [LoggerMessage(Level = LogLevel.Information, Message = "ZIP に変換しました。ArchiveId={Id} 元のファイルを残した={KeptOriginal}")]
    private static partial void LogConverted(ILogger logger, long id, bool keptOriginal);
}
