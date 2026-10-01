# API Notes

## System.Text.Json notes

`EvaluationTracker`, `DailyRiskState` and `PersistedState` round-trip through `System.Text.Json` on .NET 10 with default options (`WriteIndented` only). No fix was needed:

- Positional record constructors are bound by parameter name.
- `ImmutableDictionary<DateOnly, decimal>` deserializes, including `DateOnly` keys.
- Computed get-only properties (`TotalProfit`, `BestDay`, `ConsistencyRatio`, `TradingDays`, `MicroscalpPercent`) are written out but ignored on read, since they have no constructor parameter. They are derived, so the written values are redundant but harmless.

If a future change breaks this (for example a renamed constructor parameter), `StateStoreTests.RoundTrip_PreservesRiskMemory` fails. The fallbacks are `[JsonIgnore]` on the computed members or a DTO using `Dictionary<string, decimal>`.
