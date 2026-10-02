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
| `Symbol.GetTickCost(double)` | Yes (returns `double`) | Not used in Phase 1 | None |
| `Symbol.ConnectionId` / `Account.ConnectionId` | Yes | Connection check, `MonitoringConnectionsIds` | None |
| `Symbol.NewLast` (`LastHandler(Symbol, Last)`) | Yes | Live trades into the pipeline (Phase 1: tick-age tracking) | None |
| `Last.Time` | Yes (inherited `MessageQuote.Time`, `DateTime`) | Last tick timestamp | None |
| `Account.Balance` | Yes (`double`) | Phase 1 equity | None |
| `StrategyLoggingLevel.Info/Trading/Error` | Yes | Logging / `ILogSink` adapter | None |
| `Strategy.Stop()` | Yes | Fail-fast on bad inputs | None |
| `Strategy.Log(string, StrategyLoggingLevel)` | Yes | All logging | None |
| `Strategy.OnGetMetrics()` | Yes but **[Obsolete]**: "Use OnInitializeMetrics() method to initialize System.Diagnostics.Metrics" | Not used (overriding it caused CS0672/CS0618 in the template) | `protected virtual void OnInitializeMetrics(System.Diagnostics.Metrics.Meter meter)` |
| `StrategyMetricExtensions.Add(List<StrategyMetric>, string, string)` and `Add(..., string, object)` | Yes (both overloads) | Not used: only reachable from the obsolete `OnGetMetrics` | Numeric `Meter.CreateObservableGauge<double>` instruments; text values logged on change |
| `StrategyMetric` | Yes (`Name`, `FormattedValue`) | Not used | See above |
| `Last.Price` / `Last.Size` | Yes (`double`, Price get-only, Size settable) | `QuantowerMarketData.ToRawTrade` | None |
| `Last.AggressorFlag` (`AggressorFlag` enum: `None=0, Buy=1, Sell=2, NotSet=3`) | Yes | Aggressor side; `None`/`NotSet` map to `Aggressor.Unknown` (tick-rule fallback) | None |
| `Symbol.NewQuote` (`QuoteHandler(Symbol symbol, Quote quote)`) | Yes (event) | Live bid/ask into `MarketDataPipeline.OnQuote` | None |
| `Quote.Bid` / `Quote.Ask` (`double`) / `Quote.Time` (`DateTime`, from `MessageQuote`) | Yes | `QuantowerMarketData.ToRawQuote` | None |
| `Symbol.TickSize` | Yes (`double`) | Pipeline tick size (cast to `decimal`) | None |
| `Symbol.GetHistory(HistoryRequestParameters)` returning `HistoricalData` | Yes (also overloads `(Period, DateTime, DateTime)`, `(Period, HistoryType, ...)`, `(HistoryAggregation, DateTime, DateTime)` and `Symbol.GetTickHistory(HistoryType, DateTime, DateTime)`) | Tick backfill | None |
| `HistoryRequestParameters` (`Symbol`, `FromTime`, `ToTime`, `Aggregation`, `CancellationToken`, all settable; parameterless ctor) | Yes. **No `HistoryType` property** (the brief assumed one) | Backfill request | History type goes on the aggregation instead |
| `HistoryAggregationTick(HistoryType historyType)` | Yes. **Ctor takes a `HistoryType`, not a tick count** (the brief assumed `HistoryAggregationTick(1)`) | `new HistoryAggregationTick(HistoryType.Last)` | None |
| `HistoryType.Last` (enum `Bid=0, Ask=1, Midpoint=2, Last=3, BidAsk=4, Mark=5`) | Yes | Trade-tick history | None |
| `HistoricalData` : `IDisposable`, `IEnumerable<IHistoryItem>` | Yes | `using` + `foreach` per chunk | None |
| `HistoryItemLast.TimeLeft` (`DateTime`, from `HistoryItem`) / `.Price` / `.Volume` (`double`) / `.AggressorFlag` | Yes | Historical `RawTrade` | None |

**Metrics design consequence:** `System.Diagnostics.Metrics` instruments carry numbers only. Text metrics from the brief (clock stamp, tier name, active/next window, flags) are emitted as one status log line whenever its content changes. Numeric ones are gauges (tier as 0=A 1=B 2=C 3=Halt; flags as 0/1; "minutes to next window" replaces the formatted countdown). How Quantower 1.146.18 renders gauge names is part of the owner smoke test.

Phase 2 rows re-verified the same way on 2026-10-02.

**Backfill chunking (Phase 2):** `QuantowerMarketData.Backfill` requests tick history from the prior trading session's start (18:00 ET) to now in 1-hour chunks, so at most one hour of ticks is in memory. The order in which `HistoricalData` enumerates items is not documented, so each chunk is filtered to `[start, end)` and stably sorted by `TimeLeft` before it is fed oldest-first; a tick older than the last one fed is skipped. Cancellation (strategy stop) throws `OperationCanceledException` between chunks and between ticks, and warmup is then never marked complete. `HistoryItemLast.TimeLeft` is treated as UTC, the same as `Last.Time`.

## System.Text.Json notes

`EvaluationTracker`, `DailyRiskState` and `PersistedState` round-trip through `System.Text.Json` on .NET 10 with default options (`WriteIndented` only). No fix was needed:

- Positional record constructors are bound by parameter name.
- `ImmutableDictionary<DateOnly, decimal>` deserializes, including `DateOnly` keys.
- Computed get-only properties (`TotalProfit`, `BestDay`, `ConsistencyRatio`, `TradingDays`, `MicroscalpPercent`) are written out but ignored on read, since they have no constructor parameter. They are derived, so the written values are redundant but harmless.

If a future change breaks this (for example a renamed constructor parameter), `StateStoreTests.RoundTrip_PreservesRiskMemory` fails. The fallbacks are `[JsonIgnore]` on the computed members or a DTO using `Dictionary<string, decimal>`.
