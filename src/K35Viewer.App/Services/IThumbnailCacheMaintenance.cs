using System.ComponentModel;

namespace K35Viewer.Services;

/// <summary>
/// サムネイルのキャッシュの大きさの確認と削除（設定画面。要件定義 24.5）。
/// スキャン中・サムネイルの作成中は削除できない。状態が変わったときは、<see cref="INotifyPropertyChanged"/> で知らせる。
/// </summary>
public interface IThumbnailCacheMaintenance : INotifyPropertyChanged
{
    /// <summary>削除できない理由（スキャン中、作成中、他の操作の実行中）。削除できるときは null。</summary>
    string? ClearBlockedReason { get; }

    /// <summary>キャッシュの合計の大きさ（バイト）。</summary>
    Task<long> GetCacheSizeAsync(CancellationToken ct = default);

    /// <summary>
    /// キャッシュを削除し、一覧を読み込み直して、サムネイルの作り直しを始める。
    /// 削除できない状態の場合は何もせず false を返す。
    /// </summary>
    Task<bool> ClearCacheAsync();
}
