using System.Text.Json;
using System.Text.Json.Serialization;

namespace K35Viewer.Infrastructure.Settings;

/// <summary>設定のファイルを読んだ結果。</summary>
/// <param name="Settings">読んだ設定。ファイルがない、または読めない場合は既定値。</param>
/// <param name="WasCorrupted">ファイルはあったが読めなかった（壊れていた）。壊れたファイルは、別の名前で残す。</param>
public sealed record SettingsLoadResult(AppSettings Settings, bool WasCorrupted);

/// <summary>
/// 設定のファイル（JSON）の読み書き。書き込みは、一時ファイルへ書いてから置き換える
/// （書き込みの途中で止まっても、元のファイルが壊れないようにする）。
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = path;

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            return new SettingsLoadResult(new AppSettings(), WasCorrupted: false);
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            return settings is null
                ? Corrupted()
                : new SettingsLoadResult(settings, WasCorrupted: false);
        }
        catch (JsonException)
        {
            return Corrupted();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    // 壊れたファイルは、原因を調べられるよう「settings.json.broken」として残し、既定値で動く
    private SettingsLoadResult Corrupted()
    {
        try
        {
            File.Move(FilePath, FilePath + ".broken", overwrite: true);
        }
        catch (IOException)
        {
            // 残せなくても、既定値で動く（次に保存すると、上書きされる）
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new SettingsLoadResult(new AppSettings(), WasCorrupted: true);
    }
}
