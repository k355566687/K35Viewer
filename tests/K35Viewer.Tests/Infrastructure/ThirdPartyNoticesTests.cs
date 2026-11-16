using System.Text.Json;

namespace K35Viewer.Tests.Infrastructure;

/// <summary>
/// 第三者の部品の告知（THIRD-PARTY-NOTICES.txt。GitHub の Issue #22）が、アプリに同梱するパッケージを、すべて載せているか。
/// パッケージを追加・更新して、依存が増えたときの書き忘れを防ぐ。
/// </summary>
public class ThirdPartyNoticesTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "K35Viewer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("リポジトリの場所が分かりません");
    }

    /// <summary>
    /// アプリ（K35Viewer）が、直接・間接に使うパッケージ。テストプロジェクトの依存の情報（deps.json）から、
    /// アプリのプロジェクトを起点に、依存をたどって集める（テスト用のパッケージは含まない）。
    /// </summary>
    private static SortedSet<string> AppPackages()
    {
        var depsPath = Path.Combine(AppContext.BaseDirectory, "K35Viewer.Tests.deps.json");
        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var libraries = document.RootElement.GetProperty("libraries");
        var target = document.RootElement.GetProperty("targets").EnumerateObject().First().Value;

        var types = libraries.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("type").GetString());
        var dependencies = target.EnumerateObject().ToDictionary(
            p => p.Name.Split('/')[0],
            p => p.Value.TryGetProperty("dependencies", out var deps)
                ? deps.EnumerateObject().Select(d => d.Name).ToList()
                : [],
            StringComparer.OrdinalIgnoreCase);
        var packageIds = types.Where(t => t.Value == "package").Select(t => t.Key.Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(dependencies["K35Viewer"]);
        while (pending.TryPop(out var name))
        {
            if (!packageIds.Contains(name) || !result.Add(name))
            {
                continue;
            }

            foreach (var next in dependencies.GetValueOrDefault(name) ?? [])
            {
                pending.Push(next);
            }
        }

        return result;
    }

    [Fact]
    public void 告知には_アプリが使う全部のパッケージが載っている()
    {
        var notices = File.ReadAllText(Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.txt"));
        var lines = notices.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var packages = AppPackages();

        Assert.Contains("SharpCompress", packages); // 依存をたどれていることの確認
        Assert.Contains("SQLitePCLRaw.core", packages);
        var missing = packages
            .Where(id => !lines.Any(l => l == id || l.EndsWith(" " + id, StringComparison.Ordinal) || l.Contains(id + " (", StringComparison.Ordinal)))
            .ToList();
        Assert.True(missing.Count == 0, "告知に載っていないパッケージ: " + string.Join(", ", missing));
    }

    [Fact]
    public void 告知には_MITとApacheの全文がある()
    {
        var notices = File.ReadAllText(Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.txt"));

        Assert.Contains("Permission is hereby granted, free of charge", notices, StringComparison.Ordinal);
        Assert.Contains("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION", notices, StringComparison.Ordinal);
        Assert.Contains("END OF TERMS AND CONDITIONS", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void アプリのライセンスと告知は_ビルドの出力にも置かれる()
    {
        // アプリのプロジェクトが出力へ写すファイルは、参照しているテストの出力にも写る
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt")));
    }
}
