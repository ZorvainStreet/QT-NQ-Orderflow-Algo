# API Notes

Verified by reflection (MetadataLoadContext) against `C:\Quantower\TradingPlatform\v1.146.18\bin\TradingPlatform.BusinessLayer.dll` on 2026-10-01.

## Quantower API verification (v1.146.18)

| Member | Exists in 1.146.18 | Used for | Substitute |
|---|---|---|---|
| `InputParameterAttribute(string name = null, int sortIndex = 0, double minimum = int.MinValue, double maximum = int.MaxValue, double increment = 0.01, int decimalPlaces = 2, params object[] variants = null)` | Yes (single ctor, all optional) | Every input field (`string`, `bool`, `double`, `int`, `Symbol`, `Account`) | None needed; `(name, sort)` and `(name, sort, min, max, inc, dp)` forms both bind |
| `Core.Instance.TimeUtils.DateTimeUtcNow` | Yes (`DateTime`) | `NowUtc()`, the only clock source | None |
| `Core.GetSymbol(BusinessObjectInfo)` / `Symbol.CreateInfo()` | Yes | Re-resolve the input symbol in `OnRun` | None |
| `Symbol.Name` | Yes (`string`) | Root extraction via `SymbolRules.ExtractRoot` | None |
| `Symbol.Root` | Yes (`string`) | Not used (month-code regex on `Name` is the documented rule) | Could replace regex later |
| `Symbol.TickSize` | Yes (`double`) | Not used in Phase 1 | None |
| `Symbol.GetTickCost(double)` | Yes (returns `double`) | Not used in Phase 1 | None |
| `Symbol.ConnectionId` / `Account.ConnectionId` | Yes | Connection check, `MonitoringConnectionsIds` | None |
| `Symbol.NewLast` (`LastHandler(Symbol, Last)`) | Yes | Tick-age tracking | None |
| `Last.Time` | Yes (inherited `MessageQuote.Time`, `DateTime`) | Last tick timestamp | None |
| `Account.Balance` | Yes (`double`) | Phase 1 equity | None |
| `StrategyLoggingLevel.Info/Trading/Error` | Yes | Logging / `ILogSink` adapter | None |
| `Strategy.Stop()` | Yes | Fail-fast on bad inputs | None |
| `Strategy.Log(string, StrategyLoggingLevel)` | Yes | All logging | None |
| `Strategy.OnGetMetrics()` | Yes but **[Obsolete]**: "Use OnInitializeMetrics() method to initialize System.Diagnostics.Metrics" | Not used (overriding it caused CS0672/CS0618 in the template) | `protected virtual void OnInitializeMetrics(System.Diagnostics.Metrics.Meter meter)` |
| `StrategyMetricExtensions.Add(List<StrategyMetric>, string, string)` and `Add(..., string, object)` | Yes (both overloads) | Not used: only reachable from the obsolete `OnGetMetrics` | Numeric `Meter.CreateObservableGauge<double>` instruments; text values logged on change |
| `StrategyMetric` | Yes (`Name`, `FormattedValue`) | Not used | See above |

**Metrics design consequence:** `System.Diagnostics.Metrics` instruments carry numbers only. Text metrics from the brief (clock stamp, tier name, active/next window, flags) are emitted as one status log line whenever its content changes. Numeric ones are gauges (tier as 0=A 1=B 2=C 3=Halt; flags as 0/1; "minutes to next window" replaces the formatted countdown). How Quantower 1.146.18 renders gauge names is part of the owner smoke test.

## System.Text.Json notes

`EvaluationTracker`, `DailyRiskState` and `PersistedState` round-trip through `System.Text.Json` on .NET 10 with default options (`WriteIndented` only). No fix was needed:

- Positional record constructors are bound by parameter name.
- `ImmutableDictionary<DateOnly, decimal>` deserializes, including `DateOnly` keys.
- Computed get-only properties (`TotalProfit`, `BestDay`, `ConsistencyRatio`, `TradingDays`, `MicroscalpPercent`) are written out but ignored on read, since they have no constructor parameter. They are derived, so the written values are redundant but harmless.

If a future change breaks this (for example a renamed constructor parameter), `StateStoreTests.RoundTrip_PreservesRiskMemory` fails. The fallbacks are `[JsonIgnore]` on the computed members or a DTO using `Dictionary<string, decimal>`.
