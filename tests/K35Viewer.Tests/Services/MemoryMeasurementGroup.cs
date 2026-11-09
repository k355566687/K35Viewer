namespace K35Viewer.Tests.Services;

/// <summary>
/// プロセス全体のメモリ使用量を測るテストは、他のテストと並列に動かさない
/// （他のテストのメモリ使用が混ざって、測定が不正確になるため）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasurementGroup
{
    public const string Name = "MemoryMeasurement";
}
