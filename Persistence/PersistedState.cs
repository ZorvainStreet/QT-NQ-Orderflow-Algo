using QT_MNQ_Orderflow_Algo.Compliance;

namespace QT_MNQ_Orderflow_Algo.Persistence;

public sealed record PersistedState(int SchemaVersion, EvaluationTracker Eval, DailyRiskState Day, DateOnly? LastEodProcessed)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record LoadResult(PersistedState? State, bool Fresh, string? Error);
