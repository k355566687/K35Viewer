namespace K35Viewer.Domain;

/// <summary>スキャンの種類(scan_runs.scan_type)。</summary>
public enum ScanType
{
    Initial,
    Manual,
    Startup,
}

public static class ScanTypeExtensions
{
    public static string ToDbValue(this ScanType type) => type switch
    {
        ScanType.Initial => "INITIAL",
        ScanType.Manual => "MANUAL",
        ScanType.Startup => "STARTUP",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static OperationType ToOperationType(this ScanType type) => type switch
    {
        ScanType.Initial => OperationType.ScanInitial,
        ScanType.Manual => OperationType.ScanManual,
        ScanType.Startup => OperationType.ScanStartup,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
