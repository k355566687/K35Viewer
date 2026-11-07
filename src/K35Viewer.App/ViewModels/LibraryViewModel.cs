using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using K35Viewer.Domain;
using K35Viewer.Services;
using Microsoft.Extensions.Logging;

namespace K35Viewer.ViewModels;

/// <summary>登録フォルダ1件の表示用情報。</summary>
public sealed record LibraryFolderItem(long Id, string Path, string Description);

/// <summary>
/// ライブラリ画面のViewModel。Phase 2では管理フォルダの追加と一覧のみを扱う
/// (アーカイブ一覧・検索・ソートはPhase 3)。
/// </summary>
public sealed partial class LibraryViewModel(
    LibraryService library,
    ScanService scan,
    IDialogService dialogs,
    ILogger<LibraryViewModel> logger) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _runningScans;

    public ObservableCollection<LibraryFolderItem> Folders { get; } = [];

    [ObservableProperty]
    private bool _hasFolders;

    [ObservableProperty]
    private bool _isScanning;

    /// <summary>登録フォルダ一覧をDBから読み込み直す。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var summaries = await library.GetLibraryFoldersAsync(_cts.Token);
            Folders.Clear();
            foreach (var summary in summaries)
            {
                Folders.Add(new LibraryFolderItem(summary.Id, summary.Path, Describe(summary)));
            }

            HasFolders = Folders.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // アプリ終了中
        }
        catch (Exception ex)
        {
            LogLoadFailed(logger, ex);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = dialogs.PickFolder();
        if (path is null)
        {
            return;
        }

        try
        {
            var result = await library.AddFolderAsync(path, confirmMerge: false, _cts.Token);
            if (result.Outcome == AddFolderOutcome.MergeRequired)
            {
                if (!dialogs.ConfirmMerge(path, result.MergeTargets))
                {
                    return;
                }

                result = await library.AddFolderAsync(path, confirmMerge: true, _cts.Token);
            }

            if (result.Outcome != AddFolderOutcome.Added)
            {
                return;
            }

            await LoadAsync();

            // スキャンは待たずに開始する。UIはスキャン中も操作できる。
            _ = RunScanAsync(result.LibraryFolderId!.Value);
        }
        catch (OperationCanceledException)
        {
            // アプリ終了中
        }
        catch (Exception ex)
        {
            LogAddFailed(logger, ex, path);
        }
    }

    public void Dispose() => _cts.Dispose();

    private async Task RunScanAsync(long libraryFolderId)
    {
        _runningScans++;
        IsScanning = true;
        try
        {
            await Task.Run(() => scan.ScanAsync(libraryFolderId, ScanType.Initial, _cts.Token), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // アプリ終了によるキャンセル
        }
        catch (Exception ex)
        {
            LogScanFailed(logger, ex, libraryFolderId);
        }
        finally
        {
            _runningScans--;
            IsScanning = _runningScans > 0;
        }

        await LoadAsync();
    }

    private static string Describe(LibraryFolderSummary summary)
    {
        var count = string.Create(CultureInfo.CurrentCulture, $"{summary.ArchiveCount:N0} 件");
        return summary.AvailabilityStatus switch
        {
            "UNAVAILABLE" => count + "(現在アクセスできません)",
            "UNKNOWN" => count + "(スキャン前)",
            _ => count,
        };
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "管理フォルダ一覧の読み込みに失敗しました。")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "管理フォルダの追加に失敗しました。Path={Path}")]
    private static partial void LogAddFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "スキャンに失敗しました。LibraryFolderId={Id}")]
    private static partial void LogScanFailed(ILogger logger, Exception exception, long id);
}
