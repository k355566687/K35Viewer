namespace K35Viewer.Services;

/// <summary>復元で、利用者に選んでもらう・確かめるダイアログ（設定画面から使う）。</summary>
public interface IBackupDialogs
{
    /// <summary>復元するバックアップのファイルを選んでもらう。キャンセルされた場合は null。</summary>
    string? PickBackupFile(string initialDirectory);

    /// <summary>復元してよいか確認する。<paramref name="createdAt"/> は、バックアップを作った日時。</summary>
    bool ConfirmRestore(DateTime createdAt);
}

/// <summary>アプリを終了して、起動し直す（復元の入れ替えのため）。</summary>
public interface IAppRestarter
{
    /// <summary>新しいプロセスを起動し、このアプリを終了する。起動できなかった場合は false（終了しない）。</summary>
    bool TryRestart();
}