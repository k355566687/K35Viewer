using System.Diagnostics;
using K35Viewer.Infrastructure.Uninstall;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>アンインストール用の、実ファイルを使った一時フォルダの一式（GitHub の Issue #24）。実際のフォルダには、触れない。</summary>
internal sealed class UninstallSandbox : IDisposable
{
    public UninstallSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "K35ViewerUninstallTests", Guid.NewGuid().ToString("N"));
        ProgramDirectory = Path.Combine(Root, "Apps", "35Viewer");
        DataDirectory = Path.Combine(Root, "Data", "K35Viewer");
        LibraryDirectory = Path.Combine(Root, "Library");
        _ = Directory.CreateDirectory(ProgramDirectory);
        _ = Directory.CreateDirectory(Path.Combine(ProgramDirectory, "licenses"));
        File.WriteAllText(ExecutablePath, "exe");
        File.WriteAllText(Path.Combine(ProgramDirectory, "K35Viewer.dll"), "dll");
        File.WriteAllText(Path.Combine(ProgramDirectory, "licenses", "MIT.txt"), "license");
        _ = Directory.CreateDirectory(DataDirectory);
        _ = Directory.CreateDirectory(LibraryDirectory);
    }

    public string Root { get; }

    public string ProgramDirectory { get; }

    public string DataDirectory { get; }

    /// <summary>管理フォルダ（アーカイブを置く場所）。消してはいけない。</summary>
    public string LibraryDirectory { get; }

    public string ExecutablePath => Path.Combine(ProgramDirectory, "K35Viewer.exe");

    public string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>データのフォルダに、35Viewer が作る既知のものを一通り置く。</summary>
    public void FillData()
    {
        File.WriteAllText(Path.Combine(DataDirectory, "library.db"), "db");
        File.WriteAllText(Path.Combine(DataDirectory, "library.db-wal"), "wal");
        File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), "{}");
        foreach (var directory in new[] { "thumbnails\\0000", "logs", "temp", "backups", "update-staging\\app" })
        {
            _ = Directory.CreateDirectory(Path.Combine(DataDirectory, directory));
        }

        File.WriteAllText(Path.Combine(DataDirectory, "thumbnails", "0000", "archive-1.jpg"), "jpg");
        File.WriteAllText(Path.Combine(DataDirectory, "logs", "k35viewer-20261110.log"), "log");
        File.WriteAllText(Path.Combine(DataDirectory, "backups", "K35Viewer-backup-20261110-101010.zip"), "zip");
        File.WriteAllText(Path.Combine(DataDirectory, "update-staging", "app", "K35Viewer.exe"), "new");
    }

    /// <summary>管理フォルダに、アーカイブを置く。</summary>
    public string[] FillLibrary()
    {
        var files = new[] { Path.Combine(LibraryDirectory, "a.zip"), Path.Combine(LibraryDirectory, "b.cbz"), Path.Combine(LibraryDirectory, "sub", "c.rar") };
        _ = Directory.CreateDirectory(Path.Combine(LibraryDirectory, "sub"));
        foreach (var file in files)
        {
            File.WriteAllText(file, "archive");
        }

        return files;
    }

    public UninstallPlanner Planner(params string[] protectedDirectories) =>
        new(protectedDirectories, Path.Combine(Root, "NotUsedTemp"));

    public UninstallPlan Plan(IEnumerable<string>? libraries = null, IEnumerable<string>? shortcuts = null, UninstallPlanner? planner = null) =>
        (planner ?? Planner()).Plan(ExecutablePath, DataDirectory, libraries ?? [LibraryDirectory], shortcuts ?? []);

    public void Dispose()
    {
        try
        {
            // 読み取り専用のファイルも、消せるようにする
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // 後始末の失敗は、無視する
        }
    }
}

/// <summary>アンインストールの前の、安全の確認（GitHub の Issue #24）。</summary>
public sealed class UninstallPlannerTests
{
    [Fact]
    public void 問題がなければ_実行でき_消すものの大きさを数える()
    {
        using var box = new UninstallSandbox();
        box.FillData();

        var plan = box.Plan();

        Assert.True(plan.CanRun);
        Assert.Empty(plan.Blockers);
        Assert.Equal(box.ProgramDirectory, plan.Targets.ProgramDirectory);
        Assert.Equal(box.DataDirectory, plan.Targets.DataDirectory);
        Assert.Equal(3, plan.ProgramFileCount);
        Assert.Equal("exe".Length + "dll".Length + "license".Length, plan.ProgramBytes);
        Assert.True(plan.DataBytes > 0);
    }

    [Fact]
    public void 実行ファイルが_K35Viewer_exe_でなければ_実行できない()
    {
        using var box = new UninstallSandbox();

        var plan = box.Planner().Plan(@"C:\Program Files\dotnet\dotnet.exe", box.DataDirectory, [], []);
        var none = box.Planner().Plan(null, box.DataDirectory, [], []);

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("K35Viewer.exe", StringComparison.Ordinal));
        Assert.False(none.CanRun);
    }

    [Fact]
    public void プログラムのフォルダに_35Viewerの本体がそろっていなければ_実行できない()
    {
        using var box = new UninstallSandbox();
        File.Delete(Path.Combine(box.ProgramDirectory, "K35Viewer.dll"));

        var plan = box.Plan();

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("そろっていません", StringComparison.Ordinal));
    }

    [Fact]
    public void 管理フォルダが_プログラムのフォルダの中にあれば_実行しない()
    {
        using var box = new UninstallSandbox();
        var inside = Path.Combine(box.ProgramDirectory, "books");
        _ = Directory.CreateDirectory(inside);

        var plan = box.Plan([inside]);
        var same = box.Plan([box.ProgramDirectory]);

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains(inside, StringComparison.Ordinal) && b.Contains("プログラムのフォルダ", StringComparison.Ordinal));
        Assert.False(same.CanRun);
    }

    [Fact]
    public void 管理フォルダが_データのフォルダの中にあれば_実行しない()
    {
        using var box = new UninstallSandbox();
        var inside = Path.Combine(box.DataDirectory, "mine");
        _ = Directory.CreateDirectory(inside);

        var plan = box.Plan([inside]);

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("データのフォルダ", StringComparison.Ordinal));
    }

    [Fact]
    public void 管理フォルダが外にあれば_またはプログラムが管理フォルダの中にあっても_実行できる()
    {
        using var box = new UninstallSandbox();
        var elsewhere = box.Plan([box.LibraryDirectory]);
        var parent = box.Plan([box.PathOf("Apps")]); // 管理フォルダの中に、プログラムのフォルダがある（消すのは、プログラムのフォルダだけ）

        Assert.True(elsewhere.CanRun);
        Assert.True(parent.CanRun);
    }

    [Fact]
    public void プログラムのフォルダに_アーカイブらしいファイルがあれば_実行しない()
    {
        using var box = new UninstallSandbox();
        File.WriteAllText(Path.Combine(box.ProgramDirectory, "licenses", "my-book.CBZ"), "x");

        var plan = box.Plan();

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("my-book.CBZ", StringComparison.Ordinal));
    }

    [Fact]
    public void データのフォルダの_backupsにあるZIPは_アーカイブとはみなさない()
    {
        using var box = new UninstallSandbox();
        box.FillData(); // backups の中に、zip がある

        Assert.True(box.Plan().CanRun);
    }

    [Fact]
    public void プログラムのフォルダのファイルが多すぎれば_35Viewerのフォルダではないとみなして_実行しない()
    {
        using var box = new UninstallSandbox();
        var many = Path.Combine(box.ProgramDirectory, "many");
        _ = Directory.CreateDirectory(many);
        for (var i = 0; i <= UninstallPlanner.MaxProgramFiles; i++)
        {
            File.WriteAllText(Path.Combine(many, $"{i}.dat"), string.Empty);
        }

        var plan = box.Plan();

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Blockers, b => b.Contains("多すぎる", StringComparison.Ordinal));
    }

    [Fact]
    public void 消す場所が_大事なフォルダと同じか_それを含んでいれば_実行しない()
    {
        using var box = new UninstallSandbox();
        var sameAsProgram = box.Plan(planner: box.Planner(box.ProgramDirectory));
        var containsProtected = box.Plan(planner: box.Planner(Path.Combine(box.ProgramDirectory, "licenses")));
        var unrelated = box.Plan(planner: box.Planner(box.PathOf("Documents")));

        Assert.False(sameAsProgram.CanRun);
        Assert.False(containsProtected.CanRun);
        Assert.Contains(containsProtected.Blockers, b => b.Contains("大事なフォルダ", StringComparison.Ordinal));
        Assert.True(unrelated.CanRun);
    }

    [Fact]
    public void 控えを置く一時フォルダが_プログラムのフォルダの中にあれば_実行しない()
    {
        using var box = new UninstallSandbox();
        var planner = new UninstallPlanner([], Path.Combine(box.ProgramDirectory, "temp"));

        var plan = box.Plan(planner: planner);

        Assert.False(plan.CanRun);
    }

    [Fact]
    public void バックアップの保存先は_消す場所の中には選べない()
    {
        using var box = new UninstallSandbox();
        var targets = box.Plan().Targets;

        Assert.Null(UninstallPlanner.ValidateBackupFolder(targets, box.PathOf("Downloads")));
        Assert.NotNull(UninstallPlanner.ValidateBackupFolder(targets, box.ProgramDirectory));
        Assert.NotNull(UninstallPlanner.ValidateBackupFolder(targets, Path.Combine(box.ProgramDirectory, "licenses")));
        Assert.NotNull(UninstallPlanner.ValidateBackupFolder(targets, box.DataDirectory));
        Assert.NotNull(UninstallPlanner.ValidateBackupFolder(targets, Path.Combine(box.DataDirectory, "backups")));
        Assert.NotNull(UninstallPlanner.ValidateBackupFolder(targets, "   "));
        Assert.NotNull(UninstallPlanner.ValidateBackupFolder(targets, null));
    }
}

/// <summary>アンインストールの実行（GitHub の Issue #24）。アーカイブには、さわらない。</summary>
public sealed class UninstallExecutorTests
{
    private static UninstallExecutor ExecutorFor(UninstallSandbox box) => new(box.Planner());

    [Fact]
    public void 消すものだけを消し_管理フォルダのアーカイブには_さわらない()
    {
        using var box = new UninstallSandbox();
        box.FillData();
        var archives = box.FillLibrary();
        var shortcut = box.PathOf("StartMenu", "35Viewer.lnk");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        File.WriteAllText(shortcut, "lnk");
        var otherShortcut = box.PathOf("StartMenu", "Other.lnk");
        File.WriteAllText(otherShortcut, "lnk");

        var report = ExecutorFor(box).Execute(box.Plan([box.LibraryDirectory], [shortcut]).Targets);

        Assert.True(report.Succeeded);
        Assert.False(report.Aborted);
        Assert.Equal(1, report.DeletedShortcuts);
        Assert.True(report.ProgramDirectoryRemoved);
        Assert.True(report.DataDirectoryRemoved);
        Assert.Empty(report.LeftoverPaths);
        Assert.False(Directory.Exists(box.ProgramDirectory));
        Assert.False(Directory.Exists(box.DataDirectory));
        Assert.False(File.Exists(shortcut));
        Assert.True(File.Exists(otherShortcut)); // 指定していないショートカットには、さわらない
        Assert.All(archives, file => Assert.True(File.Exists(file), file)); // アーカイブは、そのまま
        Assert.Equal("archive", File.ReadAllText(archives[0]));
    }

    [Fact]
    public void データのフォルダに_35Viewerのものではないファイルがあれば_それとフォルダを残す()
    {
        using var box = new UninstallSandbox();
        box.FillData();
        var mine = Path.Combine(box.DataDirectory, "my-notes.txt");
        File.WriteAllText(mine, "notes");

        var report = ExecutorFor(box).Execute(box.Plan().Targets);

        Assert.True(report.Succeeded); // 残したものは、失敗に数えない
        Assert.False(report.DataDirectoryRemoved);
        Assert.Equal([mine], report.LeftoverPaths);
        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(Path.Combine(box.DataDirectory, "library.db")));
        Assert.False(Directory.Exists(Path.Combine(box.DataDirectory, "thumbnails")));
        Assert.False(Directory.Exists(Path.Combine(box.DataDirectory, "backups")));
        Assert.False(Directory.Exists(box.ProgramDirectory));
    }

    [Fact]
    public void 消す直前の確認で問題があれば_何も消さずに中止する()
    {
        using var box = new UninstallSandbox();
        box.FillData();
        var archives = box.FillLibrary();
        // 引き継いだあとに、管理フォルダが、プログラムのフォルダの中に入った
        var books = Path.Combine(box.ProgramDirectory, "books");
        _ = Directory.CreateDirectory(books);
        File.WriteAllText(Path.Combine(books, "x.zip"), "archive");
        var targets = new UninstallTargets(box.ProgramDirectory, box.DataDirectory, [], [books]);

        var report = ExecutorFor(box).Execute(targets);

        Assert.True(report.Aborted);
        Assert.False(report.Succeeded);
        Assert.NotEmpty(report.Blockers);
        Assert.True(File.Exists(Path.Combine(books, "x.zip")));
        Assert.True(File.Exists(box.ExecutablePath));
        Assert.True(File.Exists(Path.Combine(box.DataDirectory, "library.db")));
        Assert.All(archives, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public void リンク_ジャンクション_は_リンクだけを消し_先のファイルには触れない()
    {
        using var box = new UninstallSandbox();
        var outside = box.PathOf("Outside");
        _ = Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.txt");
        File.WriteAllText(precious, "keep me");
        var link = Path.Combine(box.ProgramDirectory, "licenses", "link");
        var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        _ = made.StandardOutput.ReadToEnd();
        made.WaitForExit();
        Assert.True(Directory.Exists(link), "ジャンクションを作れませんでした");

        var report = ExecutorFor(box).Execute(box.Plan().Targets);

        Assert.True(report.Succeeded);
        Assert.False(Directory.Exists(box.ProgramDirectory));
        Assert.True(File.Exists(precious)); // リンクの先は、消えていない
        Assert.Equal("keep me", File.ReadAllText(precious));
    }

    [Fact]
    public void 読み取り専用のファイルも消せる()
    {
        using var box = new UninstallSandbox();
        var file = Path.Combine(box.ProgramDirectory, "licenses", "MIT.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var report = ExecutorFor(box).Execute(box.Plan().Targets);

        Assert.True(report.Succeeded);
        Assert.False(Directory.Exists(box.ProgramDirectory));
    }

    [Fact]
    public void 使用中で消せないものがあっても_残りは消し_消せなかったものを報告する()
    {
        using var box = new UninstallSandbox();
        box.FillData();
        var locked = Path.Combine(box.ProgramDirectory, "licenses", "MIT.txt");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var report = ExecutorFor(box).Execute(box.Plan().Targets);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Failures, f => string.Equals(f.Path, locked, StringComparison.OrdinalIgnoreCase));
        Assert.False(report.ProgramDirectoryRemoved);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(box.ExecutablePath)); // 消せるものは、消えている
        Assert.True(report.DataDirectoryRemoved); // データは、消えている
    }
}

/// <summary>消す処理を引き継ぐ控えの準備（GitHub の Issue #24）。</summary>
public sealed class UninstallHandoffTests
{
    [Fact]
    public void 控えは_プログラムの一式の写しと予定のファイルで_消す場所の外にある()
    {
        using var box = new UninstallSandbox();
        var targets = box.Plan([box.LibraryDirectory], [box.PathOf("x.lnk")]).Targets;
        var temporary = box.PathOf("Temp");

        var info = UninstallHandoff.Prepare(targets, temporary);

        Assert.StartsWith(temporary, info.CopyDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(UninstallHandoff.CopyDirectoryPrefix, Path.GetFileName(info.CopyDirectory), StringComparison.Ordinal);
        Assert.True(File.Exists(info.ExecutablePath));
        Assert.Equal("K35Viewer.exe", Path.GetFileName(info.ExecutablePath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(info.ExecutablePath)!, "licenses", "MIT.txt")));
        Assert.True(File.Exists(box.ExecutablePath)); // 元のものは、そのまま
        var read = UninstallHandoff.ReadPlan(info.PlanFile)!;
        Assert.Equal(targets.ProgramDirectory, read.ProgramDirectory);
        Assert.Equal(targets.DataDirectory, read.DataDirectory);
        Assert.Equal(targets.ShortcutFiles, read.ShortcutFiles);
        Assert.Equal(targets.LibraryFolders, read.LibraryFolders);
    }

    [Fact]
    public void 控えの場所が_消す場所の中になる場合は_準備しない()
    {
        using var box = new UninstallSandbox();
        var targets = box.Plan().Targets;

        _ = Assert.Throws<InvalidOperationException>(() => UninstallHandoff.Prepare(targets, Path.Combine(box.ProgramDirectory, "licenses")));
        _ = Assert.Throws<InvalidOperationException>(() => UninstallHandoff.Prepare(targets, box.DataDirectory));
    }

    [Fact]
    public void 起動の引数は_予定のファイルと_終了を待つプロセスを渡し_読み取れる()
    {
        var arguments = UninstallHandoff.ArgumentsFor(@"C:\Temp\K35Viewer-uninstall-x\plan.json", 1234);

        Assert.Contains("--uninstall \"C:\\Temp\\K35Viewer-uninstall-x\\plan.json\"", arguments, StringComparison.Ordinal);
        Assert.Contains("--wait-for-exit 1234", arguments, StringComparison.Ordinal);
        Assert.Equal(@"C:\a\plan.json", UninstallHandoff.ParsePlanFile(["--uninstall", @"C:\a\plan.json", "--wait-for-exit", "5"]));
        Assert.Null(UninstallHandoff.ParsePlanFile(["--wait-for-exit", "5"]));
        Assert.Null(UninstallHandoff.ParsePlanFile(["--uninstall"]));
    }

    [Fact]
    public void 予定のファイルが読めなければ_nullを返す()
    {
        using var box = new UninstallSandbox();
        var broken = box.PathOf("plan.json");
        File.WriteAllText(broken, "{ not json");

        Assert.Null(UninstallHandoff.ReadPlan(broken));
        Assert.Null(UninstallHandoff.ReadPlan(box.PathOf("missing.json")));
    }

    [Fact]
    public void 控えの後始末は_決まった名前と場所のフォルダだけを対象にする()
    {
        using var box = new UninstallSandbox();
        var temporary = box.PathOf("Temp");
        _ = Directory.CreateDirectory(temporary);
        var wrongName = Path.Combine(temporary, "Something");
        var wrongPlace = Path.Combine(box.Root, UninstallHandoff.CopyDirectoryPrefix + "abc");
        var unsafeName = Path.Combine(temporary, UninstallHandoff.CopyDirectoryPrefix + "a&b");
        foreach (var directory in new[] { wrongName, wrongPlace, unsafeName })
        {
            _ = Directory.CreateDirectory(directory);
        }

        Assert.False(UninstallHandoff.TryScheduleCleanup(wrongName, temporary));
        Assert.False(UninstallHandoff.TryScheduleCleanup(wrongPlace, temporary));
        Assert.False(UninstallHandoff.TryScheduleCleanup(unsafeName, temporary)); // cmd で特別な意味を持つ文字
        Assert.True(Directory.Exists(wrongName));

        UninstallHandoff.TryDeleteCopy(wrongName); // 決まった先頭の名前でなければ、消さない
        Assert.True(Directory.Exists(wrongName));
        UninstallHandoff.TryDeleteCopy(wrongPlace);
        Assert.False(Directory.Exists(wrongPlace)); // 決まった先頭の名前なら、消す（準備に失敗したときの後始末。場所は問わない）
    }
}
