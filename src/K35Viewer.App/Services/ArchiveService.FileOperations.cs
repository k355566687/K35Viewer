using System.Globalization;
using K35Viewer.Domain;
using K35Viewer.Infrastructure.Archives;
using K35Viewer.Infrastructure.Database;
using K35Viewer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace K35Viewer.Services;

/// <summary>入力されたファイル名の検証結果。<see cref="Error"/> が null なら有効で、<see cref="FileName"/> が正規化した名前。</summary>
public sealed record FileNameValidation(string? FileName, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// アーカイブ自体の名前変更・移動・削除・エクスプローラーでの表示（UC-12〜15）。
/// 実ファイルの操作が成功してから、DBの更新と履歴の記録を1トランザクションで行う（DB設計書 11.1 / 11.2 / 11.4）。
/// 操作の前の検証で断る場合（同名がある、移動先が不正など）は、通知だけを行い、履歴には記録しない。
/// ファイル操作に失敗した場合は、通知と履歴の両方に失敗を反映する（ユースケース定義書 6.2）。
/// </summary>
public sealed partial class ArchiveService
{
    private const int MaxFileNameLength = 255;


    /// <summary>テスト用。true なら、同じボリューム内の移動でも、異なるボリューム間と同じ「コピーして確認し、元を削除する」手順にする。</summary>
    internal bool AlwaysCopyOnMove { get; init; }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // ---- 名前の検証 ----

    /// <summary>
    /// 入力された新しいファイル名を検証して正規化する。前後の空白は除く。
    /// 拡張子が .zip / .cbz でなければ、元の拡張子を付ける（拡張子を省略して入力できるように）。
    /// </summary>
    public static FileNameValidation ValidateFileName(string currentFileName, string? input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFileName);

        var name = (input ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return new FileNameValidation(null, "ファイル名を入力してください");
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return new FileNameValidation(null, "ファイル名に使用できない文字が含まれています（\\ / : * ? \" < > |）");
        }

        if (name.EndsWith('.'))
        {
            return new FileNameValidation(null, "ファイル名の最後に「.」は使えません");
        }

        if (!ArchiveFormats.SupportedExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            var originalExtension = Path.GetExtension(currentFileName);
            name += ArchiveFormats.IsSupportedExtension(originalExtension) ? originalExtension : ".zip";
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (baseName.Length == 0 || baseName.Trim().Length == 0)
        {
            return new FileNameValidation(null, "ファイル名を入力してください");
        }

        // 「CON.zip」のように、拡張子を除いた部分が予約語のものは、Windowsでは使えない
        if (ReservedNames.Contains(baseName.Split('.')[0]))
        {
            return new FileNameValidation(null, "Windowsで予約されている名前のため、使えません");
        }

        if (name.Length > MaxFileNameLength)
        {
            return new FileNameValidation(null, string.Create(CultureInfo.CurrentCulture, $"ファイル名は{MaxFileNameLength}文字以内にしてください"));
        }

        return new FileNameValidation(name, null);
    }

    /// <summary>
    /// 名前変更の入力に問題があれば、その説明を返す（入力ダイアログに表示する）。問題がなければ null。
    /// 変更後の名前が同じフォルダの別のファイルと重なる場合も含む（大文字小文字だけの変更は、同じファイルなので問題ない）。
    /// </summary>
    public string? GetRenameError(string folderPath, string currentFileName, string? input)
    {
        var validation = ValidateFileName(currentFileName, input);
        if (!validation.IsValid)
        {
            return validation.Error;
        }

        var newName = validation.FileName!;
        if (!string.Equals(newName, currentFileName, StringComparison.OrdinalIgnoreCase)
            && fileSystem.FileExists(Path.Combine(folderPath, newName)))
        {
            return "同じ名前のファイルが既にあります";
        }

        return null;
    }

    // ---- 名前変更（UC-12） ----

    /// <summary>アーカイブの名前を変更する。Archive IDとサムネイル、閲覧位置は維持される。</summary>
    public async Task<OperationResult> RenameAsync(long archiveId, string? newFileName, CancellationToken ct = default)
    {
        if (await FindAsync(archiveId, ct) is not { } info)
        {
            return NotFoundResult(OperationType.ArchiveRename);
        }

        var validation = ValidateFileName(info.FileName, newFileName);
        if (!validation.IsValid)
        {
            return Reject(OperationType.ArchiveRename, "INVALID_NAME", validation.Error!, info);
        }

        var newName = validation.FileName!;
        var source = info.FullPath;
        var destination = Path.Combine(info.FolderPath, newName);
        if (string.Equals(newName, info.FileName, StringComparison.Ordinal))
        {
            return Reject(OperationType.ArchiveRename, "NO_CHANGE", "名前が変わっていません", info);
        }

        if (!string.Equals(newName, info.FileName, StringComparison.OrdinalIgnoreCase) && fileSystem.FileExists(destination))
        {
            return Reject(OperationType.ArchiveRename, "NAME_CONFLICT", Format($"同じ名前のファイルが既にあります: {newName}"), info);
        }

        if (fileSystem.TryGetFileState(source) is null)
        {
            return await FailAsync(
                OperationType.ArchiveRename, "FILE_NOT_FOUND", MissingFileMessage(info.FileName), info, source, destination);
        }

        try
        {
            await Task.Run(() => fileSystem.MoveFile(source, destination), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileOperationFailed(logger, ex, "名前変更", archiveId);
            return await FailAsync(
                OperationType.ArchiveRename,
                ex is UnauthorizedAccessException ? "ACCESS_DENIED" : "IO_ERROR",
                Format($"「{info.FileName}」の名前を変更できませんでした。他のプログラムが使用中の可能性があります。ファイルは変更していません"),
                info,
                source,
                destination);
        }

        var message = Format($"「{info.FileName}」を「{newName}」に名前変更しました");
        var result = new OperationResult(
            OperationType.ArchiveRename,
            OperationStatus.Success,
            message,
            archiveId,
            newName,
            1,
            source,
            destination);
        var extension = ArchiveFormats.ToDatabaseExtension(Path.GetExtension(newName));
        try
        {
            await databaseWriter.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    await archives.RenameAsync(connection, transaction, archiveId, newName, extension, UnixTime.Now(), token);
                    await history.InsertAsync(connection, transaction, result, token);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // DBを更新できなかった。ファイル名とDBが食い違わないよう、ファイル名を元に戻す
            LogRecordFailed(logger, ex, archiveId);
            var restored = TryRestore(destination, source);
            return await FailAsync(
                OperationType.ArchiveRename,
                "DATABASE_ERROR",
                restored
                    ? Format($"「{info.FileName}」の名前を変更できませんでした（記録の更新に失敗しました）。ファイルは変更していません")
                    : Format($"「{newName}」に名前を変更しましたが、記録の更新に失敗しました。ライブラリを再スキャンしてください"),
                info,
                source,
                destination);
        }

        notifications.ShowSuccess(message);
        return result;
    }

    // ---- 移動（UC-13） ----

    /// <summary>
    /// アーカイブを別のフォルダへ移動する。移動先は管理フォルダの中に限る（外へ出すと、ライブラリから見えなくなるため）。
    /// 同じボリューム内は名前の変更で移動する。異なるボリューム間は、移動先へコピーし、コピーを確認してから元のファイルを削除する。
    /// </summary>
    public async Task<OperationResult> MoveAsync(long archiveId, string destinationDirectory, CancellationToken ct = default)
    {
        if (await FindAsync(archiveId, ct) is not { } info)
        {
            return NotFoundResult(OperationType.ArchiveMove);
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory) || !fileSystem.CanAccessDirectory(destinationDirectory))
        {
            return Reject(OperationType.ArchiveMove, "DESTINATION_NOT_ACCESSIBLE", "移動先のフォルダにアクセスできません", info);
        }

        var destinationFolder = Path.GetFullPath(destinationDirectory);
        var destinationNormalized = PathNormalizer.Normalize(destinationFolder);
        if (string.Equals(destinationNormalized, PathNormalizer.Normalize(info.FolderPath), StringComparison.Ordinal))
        {
            return Reject(OperationType.ArchiveMove, "SAME_FOLDER", "すでにそのフォルダにあります", info);
        }

        var owner = (await libraries.GetLibraryFoldersAsync(ct))
            .FirstOrDefault(l => PathNormalizer.IsSameOrDescendant(destinationNormalized, l.NormalizedPath));
        if (owner is null)
        {
            return Reject(
                OperationType.ArchiveMove,
                "OUTSIDE_LIBRARY",
                "移動先は、管理フォルダの中のフォルダを選んでください（管理フォルダの外へ移動すると、ライブラリから見えなくなります）",
                info);
        }

        var source = info.FullPath;
        var destination = Path.Combine(destinationFolder, info.FileName);
        if (fileSystem.FileExists(destination))
        {
            return Reject(
                OperationType.ArchiveMove, "NAME_CONFLICT", Format($"移動先に同じ名前のファイルが既にあります: {info.FileName}"), info);
        }

        if (fileSystem.TryGetFileState(source) is not { } before)
        {
            return await FailAsync(
                OperationType.ArchiveMove, "FILE_NOT_FOUND", MissingFileMessage(info.FileName), info, source, destination);
        }

        var sourceRemoved = true;
        try
        {
            sourceRemoved = await Task.Run(() => TransferFile(source, destination, before.FileSize), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileOperationFailed(logger, ex, "移動", archiveId);
            return await FailAsync(
                OperationType.ArchiveMove,
                ex is UnauthorizedAccessException ? "ACCESS_DENIED" : ex is InvalidDataException ? "VERIFY_FAILED" : "IO_ERROR",
                Format($"「{info.FileName}」を移動できませんでした。元のファイルは変更していません"),
                info,
                source,
                destination);
        }

        var after = fileSystem.TryGetFileState(destination);
        (long FileSize, long LastWriteTime)? changed = after is { } state && state != before ? state : null;
        var removedNote = sourceRemoved ? string.Empty : "（元のファイルを削除できなかったため、手動で削除してください）";
        var message = Format($"「{info.FileName}」を移動しました: {destinationFolder}{removedNote}");
        var result = new OperationResult(
            OperationType.ArchiveMove,
            sourceRemoved ? OperationStatus.Success : OperationStatus.Partial,
            message,
            archiveId,
            info.FileName,
            1,
            source,
            destination);
        try
        {
            await databaseWriter.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    var now = UnixTime.Now();
                    var folderId = await EnsureFolderAsync(connection, transaction, owner, destinationFolder, now, token);
                    await archives.MoveAsync(connection, transaction, archiveId, folderId, changed, now, token);
                    if (changed is not null)
                    {
                        await thumbnails.MarkStaleAsync(connection, transaction, archiveId, token);
                    }

                    await history.InsertAsync(connection, transaction, result, token);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // ファイルは移動済み。次回のスキャンが、実ファイルから状態を同期する（Archive IDは引き継がれない）
            LogRecordFailed(logger, ex, archiveId);
            var failure = Format($"「{info.FileName}」を移動しましたが、記録の更新に失敗しました。ライブラリを再スキャンしてください");
            notifications.ShowWarning(failure);
            return new OperationResult(
                OperationType.ArchiveMove, OperationStatus.Partial, failure, archiveId, info.FileName, 1, source, destination, "DATABASE_ERROR");
        }

        if (sourceRemoved)
        {
            notifications.ShowSuccess(message);
        }
        else
        {
            notifications.ShowWarning(message);
        }

        return result;
    }

    /// <summary>
    /// ファイルを移動先へ置く。戻り値は、元のファイルがなくなったか（コピーして移した場合に、元を削除できなければ false）。
    /// </summary>
    private bool TransferFile(string source, string destination, long expectedSize)
    {
        if (!AlwaysCopyOnMove && fileSystem.IsSameVolume(source, destination))
        {
            fileSystem.MoveFile(source, destination);
            return true;
        }

        // 異なるボリューム: 移動先のフォルダに一時ファイルとしてコピーし、大きさを確認してから、正式な名前にする
        var temp = fileSystem.CreateTempPath(destination);
        try
        {
            fileSystem.CopyFile(source, temp);
            if (fileSystem.TryGetFileState(temp) is not { } copied || copied.FileSize != expectedSize)
            {
                throw new InvalidDataException("コピーしたファイルの大きさが元と一致しません");
            }

            fileSystem.MoveFile(temp, destination);
        }
        catch
        {
            _ = fileSystem.TryDelete(temp);
            throw;
        }

        // コピーを確認できてから、元のファイルを削除する
        return fileSystem.TryDelete(source);
    }

    /// <summary>移動先のフォルダのDB上の行を返す。まだ登録されていないフォルダ（新しく作った等）は、管理フォルダからの経路ぶんを作る。</summary>
    private async Task<long> EnsureFolderAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        LibraryFolder owner,
        string folderPath,
        long now,
        CancellationToken ct)
    {
        var normalized = PathNormalizer.Normalize(folderPath);
        if (await libraries.FindFolderIdAsync(connection, transaction, normalized, ct) is { } existing)
        {
            return existing;
        }

        var parentId = await libraries.FindFolderIdAsync(connection, transaction, owner.NormalizedPath, ct)
            ?? throw new InvalidOperationException("管理フォルダの起点が登録されていません");
        var segments = Path.GetRelativePath(owner.Path, folderPath)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var currentPath = owner.Path;
        for (var i = 0; i < segments.Length; i++)
        {
            currentPath = Path.Combine(currentPath, segments[i]);
            var currentNormalized = PathNormalizer.Normalize(currentPath);
            parentId = await libraries.FindFolderIdAsync(connection, transaction, currentNormalized, ct)
                ?? await libraries.InsertFolderAsync(
                    connection,
                    transaction,
                    owner.Id,
                    parentId,
                    segments[i],
                    currentPath,
                    currentNormalized,
                    string.Join(Path.DirectorySeparatorChar, segments.Take(i + 1)),
                    i + 1,
                    now,
                    ct);
        }

        return parentId;
    }

    // ---- 削除（UC-14） ----

    /// <summary>
    /// ファイルをごみ箱へ移動できない理由（できる見込みなら null）。ネットワークドライブ等では、
    /// 呼び出し側が「完全に削除しますか」とユーザーに確認し、承認された場合だけ <c>permanent: true</c> で削除する。
    /// </summary>
    public string? GetRecycleBlockReason(string fullPath) => recycleBin.CheckAvailability(fullPath);

    /// <summary>
    /// アーカイブを削除する。通常はWindowsのごみ箱へ移動する。<paramref name="permanent"/> が true の場合は、ごみ箱を使わずに
    /// 完全に削除する（ユーザーが承認した場合に限る。ごみ箱を使えないときに自動で切り替えることはない）。
    /// 実ファイルを削除できてから、DBから登録を外し（サムネイルの管理行も削除）、サムネイル画像を削除する。
    /// </summary>
    public async Task<OperationResult> DeleteAsync(long archiveId, bool permanent, CancellationToken ct = default)
    {
        if (await FindAsync(archiveId, ct) is not { } info)
        {
            return NotFoundResult(OperationType.ArchiveDelete);
        }

        var path = info.FullPath;
        if (!fileSystem.FileExists(path))
        {
            return await FailAsync(
                OperationType.ArchiveDelete, "FILE_NOT_FOUND", MissingFileMessage(info.FileName), info, path, null);
        }

        if (permanent)
        {
            try
            {
                await Task.Run(() => fileSystem.DeleteFile(path), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFileOperationFailed(logger, ex, "完全削除", archiveId);
                return await FailAsync(
                    OperationType.ArchiveDelete,
                    ex is UnauthorizedAccessException ? "ACCESS_DENIED" : "IO_ERROR",
                    Format($"「{info.FileName}」を削除できませんでした。他のプログラムが使用中の可能性があります"),
                    info,
                    path,
                    null);
            }
        }
        else
        {
            var moved = await Task.Run(() => recycleBin.MoveToRecycleBin(path), ct);
            if (!moved.Moved)
            {
                LogRecycleFailed(logger, archiveId, moved.Status, moved.Message);
                return moved.Status == RecycleBinStatus.Unavailable
                    ? await FailAsync(
                        OperationType.ArchiveDelete,
                        "RECYCLE_BIN_UNAVAILABLE",
                        Format($"「{info.FileName}」をごみ箱へ移動できません（{moved.Message}）。ファイルは削除していません"),
                        info,
                        path,
                        null)
                    : await FailAsync(
                        OperationType.ArchiveDelete,
                        "RECYCLE_FAILED",
                        Format($"「{info.FileName}」をごみ箱へ移動できませんでした。他のプログラムが使用中の可能性があります。ファイルは削除していません"),
                        info,
                        path,
                        null);
            }
        }

        var message = permanent
            ? Format($"「{info.FileName}」を完全に削除しました")
            : Format($"「{info.FileName}」をごみ箱へ移動しました");
        var result = new OperationResult(
            OperationType.ArchiveDelete, OperationStatus.Success, message, null, info.FileName, 1, path, null);
        string? thumbnailPath = null;
        try
        {
            await databaseWriter.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    await history.InsertAsync(connection, transaction, result, token);
                    thumbnailPath = await thumbnails.DeleteAsync(connection, transaction, archiveId, token);
                    await archives.DeleteAsync(connection, transaction, archiveId, token);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // ファイルは削除済み。次回のスキャンが、不在のアーカイブとして扱う
            LogRecordFailed(logger, ex, archiveId);
            var failure = Format($"「{info.FileName}」を削除しましたが、記録の更新に失敗しました。ライブラリを再スキャンしてください");
            notifications.ShowWarning(failure);
            return new OperationResult(
                OperationType.ArchiveDelete, OperationStatus.Partial, failure, null, info.FileName, 1, path, null, "DATABASE_ERROR");
        }

        if (thumbnailPath is not null)
        {
            thumbnailCache.Delete(thumbnailPath);
        }

        notifications.ShowSuccess(message);
        return result;
    }

    // ---- エクスプローラーで表示（UC-15） ----

    /// <summary>
    /// アーカイブのファイルを選択した状態で、エクスプローラーを開く。ファイルが存在しない場合は開かず、再スキャンを促す。
    /// </summary>
    public async Task<bool> ShowInExplorerAsync(long archiveId, CancellationToken ct = default)
    {
        var info = await archives.GetOpenInfoAsync(archiveId, ct);
        if (info is null)
        {
            notifications.ShowWarning("アーカイブが見つかりません。ライブラリを再スキャンしてください");
            return false;
        }

        if (!fileSystem.FileExists(info.FullPath))
        {
            notifications.ShowWarning(MissingFileMessage(info.FileName));
            return false;
        }

        try
        {
            explorer.Reveal(info.FullPath);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            LogFileOperationFailed(logger, ex, "エクスプローラーで表示", archiveId);
            notifications.ShowError("エクスプローラーを開けませんでした");
            return false;
        }
    }

    // ---- 共通 ----

    private async Task<ArchiveOpenInfo?> FindAsync(long archiveId, CancellationToken ct)
    {
        var info = await archives.GetOpenInfoAsync(archiveId, ct);
        return info is not null && string.Equals(info.AvailabilityStatus, "AVAILABLE", StringComparison.Ordinal)
            ? info
            : null;
    }

    private OperationResult NotFoundResult(OperationType type)
    {
        const string message = "アーカイブが見つかりません。ライブラリを再スキャンしてください";
        notifications.ShowWarning(message);
        return new OperationResult(type, OperationStatus.Failed, message, ErrorCode: "ARCHIVE_NOT_FOUND");
    }

    /// <summary>操作の前の検証で断る。通知だけを行い、履歴には記録しない（ファイルには何もしていないため）。</summary>
    private OperationResult Reject(OperationType type, string code, string message, ArchiveOpenInfo info)
    {
        notifications.ShowWarning(message);
        return new OperationResult(type, OperationStatus.Failed, message, info.Id, info.FileName, ErrorCode: code);
    }

    /// <summary>ファイル操作の失敗。通知と履歴の両方に記録する。</summary>
    private async Task<OperationResult> FailAsync(
        OperationType type, string code, string message, ArchiveOpenInfo info, string? oldPath, string? newPath)
    {
        notifications.ShowError(message);
        var result = new OperationResult(
            type, OperationStatus.Failed, message, info.Id, info.FileName, 0, oldPath, newPath, code);
        try
        {
            await databaseWriter.ExecuteAsync(
                (connection, transaction, token) => history.InsertAsync(connection, transaction, result, token),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogRecordFailed(logger, ex, info.Id);
        }

        return result;
    }

    private bool TryRestore(string from, string to)
    {
        try
        {
            fileSystem.MoveFile(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string MissingFileMessage(string fileName) =>
        Format($"「{fileName}」が見つかりません。ライブラリを再スキャンしてください");

    [LoggerMessage(Level = LogLevel.Warning, Message = "ファイル操作に失敗しました。Operation={Operation} ArchiveId={Id}")]
    private static partial void LogFileOperationFailed(ILogger logger, Exception exception, string operation, long id);
}
