# NQ Order-Flow Strategy — Phase 2 (Data and Order Flow) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the raw Rithmic tick stream into trustworthy order-flow data: normalized trades, 1m and 5m footprint bars, a session profile, rolling tape windows, and an immutable `OrderFlowSnapshot`. The data is backfilled at startup. A per-bar debug log lets the owner check the numbers against Quantower's own footprint and volume profile. **Still no orders.**

**Architecture:** As in Phase 1, all logic is Quantower-free "pure core", source-linked into the xUnit project:
- `Data/`: ticks, bars, tape, profile.
- `OrderFlow/`: features.
- `Telemetry/`: bounded log queue.

One `MarketDataPipeline` owns every mutable accumulator behind a single lock. Callers only ever get immutable snapshots. Only `NQOrderFlowStrategy.cs` and a new `QuantowerMarketData.cs` adapter touch the Quantower API.

**Tech Stack:** C# latest, net10.0, Quantower v1.146.18 `TradingPlatform.BusinessLayer`, xUnit.

**Spec:** owner's JSON prompt v1.0, sections `data_pipeline`, `order_flow_engine` and `market_structure_engine.key_levels`. It is not yet committed to the repo; the owner will add it under `docs/plans/`. The Phase 1 plan is `docs/plans/2026-10-01-phase1-skeleton-clock-risk.md`.

## Global Constraints

- All Phase 1 Global Constraints still apply:
  - ET time logic from UTC sources only
  - no hardcoded thresholds: every tunable is a settings-record field fed by an `[InputParameter]`
  - money and prices are `decimal`
  - no exceptions escape handlers
  - fail closed
  - 0 build warnings and all tests green after every task
- Prices are snapped to the symbol tick (`TickNormalizer.RoundToTick`) before entering any bar or profile.
- The exchange aggressor flag is the truth. The tick-rule fallback runs only when the flag is Unknown. Its rate is tracked, with a warning above `FallbackWarnPercent` (default 5).
- Hot-path accumulators are mutable by design. This is a documented exception to the immutability rule, for per-tick performance:
  - It covers `FootprintBar`, `TapeWindow`, `BucketSeries` and `SessionProfile`.
  - They are reachable only through `MarketDataPipeline`, under its lock.
  - Everything handed out is a snapshot: `OrderFlowSnapshot`, `SessionLevels`, `DataHealth` and bar arrays.
- Tick and quote handlers do no blocking I/O. Log lines go through `BoundedQueue<string>` and are drained by the 1-second timer.
- Entries stay impossible: Phase 2 adds no order code. `git grep -n "PlaceOrder\|CancelOrder" -- "*.cs"` must return nothing.

## Review Focus

1. **Backfill → live hand-off.** Live ticks that arrive during backfill must not be lost or double-counted. Only buffered live ticks with `Utc >` the last backfilled tick are replayed. Covered by Task 7 (pipeline) and a Task 8 smoke item.
2. **Session boundaries across DST.** A trade at exactly 18:00 ET starts the new session's CVD and profile. The opening range uses 09:30 ET in both EDT and EST weeks. Tested in Task 5.
3. **Quiet markets.** With no trades, no fake bars are created. Empty 20s buckets are zero-filled so baselines stay honest. Data health turns unhealthy once the last tick is older than `MaxTickAgeSeconds`. Tested in Tasks 4 and 7.
4. **Bad tick vs real gap.** A single wild print with no quote change is dropped. A real jump confirmed by `BadTickConfirmCount` consecutive prints is accepted, so the filter can never lock out the market. Tested in Task 1.
5. **Missing aggressor flags.** The fallback classifies a print at or above the ask as a buy and at or below the bid as a sell. Otherwise it uses the tick rule, then the previous side. Its rate is reported. Tested in Tasks 1 and 7.

---

## File Structure

```
Config/DataSettings.cs              all Phase 2 tunables
Config/SettingsValidator.cs         (modify) blank news → defaults; Validate(DataSettings)
Compliance/SessionClock.cs          (modify) PreviousTradingDate, SessionStartUtc
Data/MarketTypes.cs                 Aggressor, RawTrade, RawQuote, Trade
Data/Stats.cs                       Median, MeanStd
Data/TickNormalizer.cs              sanity filter, rounding, aggressor fallback
Data/FootprintBar.cs                per-bar bid/ask by price, delta, POC, imbalances
Data/FootprintBuilder.cs            1m/5m bar bucketing, capped history
Data/TapeWindow.cs                  rolling N-second trade window
Data/BucketSeries.cs                fixed buckets (20s) with zero-fill, for baselines
Data/SessionProfile.cs              RTH VWAP and bands, value area, OR, overnight, prior day
OrderFlow/OrderFlowFeatures.cs      snapshot computation + absorption check
Data/MarketDataPipeline.cs          the single locked owner of everything above
Telemetry/BoundedQueue.cs           non-blocking bounded queue
QuantowerMarketData.cs              Quantower adapter: Last/Quote mapping, history backfill
NQOrderFlowStrategy.cs              (modify) inputs, wiring, backfill, bar log, gauges
tests/NQOrderFlow.Tests/            one *Tests.cs per new core file; csproj links Data/OrderFlow/Telemetry
docs/API_NOTES.md, docs/ASSUMPTIONS.md (modify)
```

Namespaces follow the folders: `QT_MNQ_Orderflow_Algo.Data`, `.OrderFlow` and `.Telemetry`.

---

### Task 0: Blank recurring-news input falls back to defaults (Phase 1 carry-over)

**Files:** Modify `Config/SettingsValidator.cs`, `NQOrderFlowStrategy.cs`, `tests/NQOrderFlow.Tests/SettingsValidatorTests.cs`

**Interfaces — Produces:** `SettingsValidator.ResolveRecurringNews(string? csv, out IReadOnlyList<TimeSpan> times) : bool`. Blank input gives `SessionSettings.Default().RecurringNewsEt`, any bad element returns `false`, and anything else gives the parsed list.

- [ ] **Step 1: Failing tests.** Append to `SettingsValidatorTests`:
```csharp
    [Fact]
    public void ResolveRecurringNews_Blank_UsesDefaults_NeverEmpty()
    {
        Assert.True(SettingsValidator.ResolveRecurringNews("   ", out var t));
        Assert.Equal(new[] { new TimeSpan(8, 30, 0), new TimeSpan(10, 0, 0) }, t);
    }

    [Fact]
    public void ResolveRecurringNews_Custom_IsParsed()
    {
        Assert.True(SettingsValidator.ResolveRecurringNews("14:00", out var t));
        Assert.Equal(new[] { new TimeSpan(14, 0, 0) }, t);
    }

    [Fact]
    public void ResolveRecurringNews_Bad_Fails() => Assert.False(SettingsValidator.ResolveRecurringNews("8:3x", out _));
```
- [ ] **Step 2:** Run `dotnet test tests/NQOrderFlow.Tests --filter ResolveRecurringNews`. Expected: FAIL (method missing).
- [ ] **Step 3: Implement** in `SettingsValidator`:
```csharp
    /// <summary>Recurring news times. Blank means the spec defaults (never "no blocks"); a bad element fails.</summary>
    public static bool ResolveRecurringNews(string? csv, out IReadOnlyList<TimeSpan> times)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            times = SessionSettings.Default().RecurringNewsEt;
            return true;
        }
        return TryParseTimeCsv(csv, out times);
    }
```
In `NQOrderFlowStrategy.cs`, around line 358, replace `SettingsValidator.TryParseTimeCsv(RecurringNewsTimesEt, out var recurring)` with `SettingsValidator.ResolveRecurringNews(RecurringNewsTimesEt, out var recurring)`. Then log the active list once, in the same place the IST window table is logged: `log.Info($"Recurring news blocks ET: {string.Join(", ", recurring.Select(t => t.ToString(@"hh\:mm")))}")`.
- [ ] **Step 4:** Run `dotnet test tests/NQOrderFlow.Tests` and `dotnet build "QT MNQ Orderflow Algo.csproj" -c Release`. Expected: all pass, 0 warnings.
- [ ] **Step 5:** `git add Config/SettingsValidator.cs NQOrderFlowStrategy.cs tests/NQOrderFlow.Tests/SettingsValidatorTests.cs && git commit -m "fix: blank recurring news input falls back to defaults"`

---

### Task 1: DataSettings, market types, TickNormalizer

**Files:**
- Create: `Config/DataSettings.cs`, `Data/MarketTypes.cs`, `Data/Stats.cs`, `Data/TickNormalizer.cs`, `tests/NQOrderFlow.Tests/TickNormalizerTests.cs`
- Modify: `tests/NQOrderFlow.Tests/NQOrderFlow.Tests.csproj`

**Interfaces — Produces:**
- `DataSettings` record (below). Every later task uses it.
- `enum Aggressor { Unknown, Buy, Sell }`
- `RawTrade(DateTime Utc, decimal Price, decimal Size, Aggressor Side)`
- `RawQuote(DateTime Utc, decimal Bid, decimal Ask)`
- `Trade(DateTime Utc, decimal Price, decimal Size, bool IsBuy, bool UsedFallback)`
- `Stats.Median(IReadOnlyList<decimal>) : decimal` and `Stats.MeanStd(IReadOnlyList<double>) : (double Mean, double StdDev)`
- `TickNormalizer(DataSettings, decimal tickSize)` with:
  - `void OnQuote(RawQuote)`
  - `Trade? OnTrade(RawTrade)`
  - `decimal RoundToTick(decimal)`
  - `long Accepted`, `long Dropped`, `long Fallbacks`
  - `double FallbackPercent`
  - `decimal TickSize`

- [ ] **Step 1: Link the new folders in the test project.** In `NQOrderFlow.Tests.csproj`, add these next to the existing `<Compile Include>` lines:
```xml
    <Compile Include="..\..\Data\**\*.cs" LinkBase="Core\Data" />
    <Compile Include="..\..\OrderFlow\**\*.cs" LinkBase="Core\OrderFlow" />
    <Compile Include="..\..\Telemetry\**\*.cs" LinkBase="Core\Telemetry" />
```
- [ ] **Step 2: `Config/DataSettings.cs`:**
```csharp
namespace QT_MNQ_Orderflow_Algo.Config;

/// <summary>Phase 2 data and order-flow tunables. Every field is an [InputParameter] in the strategy.</summary>
public sealed record DataSettings(
    int BadTickMaxTicks = 40,
    int BadTickConfirmCount = 3,
    bool DropIdenticalPrints = false,
    double FallbackWarnPercent = 5,
    int MaxTickAgeSeconds = 3,
    int BarHistoryCapacity = 500,
    decimal ImbalanceRatio = 3.0m,
    decimal ImbalanceMinVolume = 12m,
    int StackedLevels = 3,
    int TapeShortSeconds = 5,
    int TapeMidSeconds = 20,
    int TapeLongSeconds = 60,
    int BaselineMinutes = 30,
    decimal AbsorptionVolMultiple = 2.0m,
    int AbsorptionMaxProgressTicks = 3,
    decimal AbsorptionDominancePercent = 65m,
    decimal DeltaFlipMinMultiple = 0.5m,
    double VelocityZLimit = 3.0,
    decimal ValueAreaPercent = 70m,
    int OpeningRangeShortMinutes = 5,
    int OpeningRangeLongMinutes = 15,
    int MedianBarLookback = 30)
{
    public TimeSpan RthOpenEt { get; init; } = new(9, 30, 0);
    public TimeSpan RthCloseEt { get; init; } = new(16, 0, 0);
}
```
- [ ] **Step 3: `Data/MarketTypes.cs` and `Data/Stats.cs`:**
```csharp
namespace QT_MNQ_Orderflow_Algo.Data;

public enum Aggressor { Unknown, Buy, Sell }

/// <summary>A trade print as it arrives from the feed, before any filtering.</summary>
public readonly record struct RawTrade(DateTime Utc, decimal Price, decimal Size, Aggressor Side);

public readonly record struct RawQuote(DateTime Utc, decimal Bid, decimal Ask);

/// <summary>A trade that passed the sanity filter: tick-rounded price and a definite side.</summary>
public readonly record struct Trade(DateTime Utc, decimal Price, decimal Size, bool IsBuy, bool UsedFallback);
```
```csharp
namespace QT_MNQ_Orderflow_Algo.Data;

public static class Stats
{
    public static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    public static (double Mean, double StdDev) MeanStd(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (0, 0);
        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (mean, Math.Sqrt(variance));
    }
}
```
- [ ] **Step 4: Failing tests** in `TickNormalizerTests.cs`:
```csharp
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class TickNormalizerTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static TickNormalizer New(DataSettings? s = null) => new(s ?? new DataSettings(), 0.25m);
    private static RawTrade Raw(int ms, decimal price, decimal size = 1, Aggressor side = Aggressor.Buy)
        => new(T0.AddMilliseconds(ms), price, size, side);

    [Fact]
    public void ExchangeFlag_IsTruth()
    {
        var n = New();
        Assert.False(n.OnTrade(Raw(0, 20000m, side: Aggressor.Sell))!.Value.IsBuy);
        Assert.Equal(0, n.Fallbacks);
    }

    [Fact]
    public void Price_IsSnappedToTick()
        => Assert.Equal(20000.25m, New().OnTrade(Raw(0, 20000.2600001m))!.Value.Price);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSize_IsDropped(int size)
    {
        var n = New();
        Assert.Null(n.OnTrade(Raw(0, 20000m, size)));
        Assert.Equal(1, n.Dropped);
    }

    [Fact]
    public void OutOfOrder_IsDropped_SameTimestamp_IsKept()
    {
        var n = New();
        n.OnTrade(Raw(100, 20000m));
        Assert.NotNull(n.OnTrade(Raw(100, 20000m)));   // same ms, legitimate second print
        Assert.Null(n.OnTrade(Raw(99, 20000m)));
    }

    [Fact]
    public void IdenticalPrints_KeptByDefault_DroppedWhenEnabled()
    {
        var keep = New();
        keep.OnTrade(Raw(0, 20000m));
        Assert.NotNull(keep.OnTrade(Raw(0, 20000m)));
        var drop = New(new DataSettings(DropIdenticalPrints: true));
        drop.OnTrade(Raw(0, 20000m));
        Assert.Null(drop.OnTrade(Raw(0, 20000m)));
    }

    [Fact]
    public void BadTick_WithoutQuoteChange_IsDropped()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        Assert.Null(n.OnTrade(Raw(1, 20011m)));        // 44 ticks > 40
        Assert.NotNull(n.OnTrade(Raw(2, 20000.25m)));
    }

    [Fact]
    public void BadTick_FilterAccepts_AfterConfirmedJump()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        Assert.Null(n.OnTrade(Raw(1, 20011m)));
        Assert.Null(n.OnTrade(Raw(2, 20011m)));
        Assert.NotNull(n.OnTrade(Raw(3, 20011m)));     // 3rd consecutive confirms the jump
    }

    [Fact]
    public void Jump_AfterQuoteChange_IsAccepted()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        n.OnQuote(new RawQuote(T0.AddMilliseconds(1), 20010.75m, 20011m));
        Assert.NotNull(n.OnTrade(Raw(2, 20011m)));
    }

    [Fact]
    public void Fallback_UsesQuoteThenTickRuleThenPreviousSide()
    {
        var n = New();
        n.OnQuote(new RawQuote(T0, 19999.75m, 20000m));
        Assert.True(n.OnTrade(Raw(1, 20000m, side: Aggressor.Unknown))!.Value.IsBuy);        // at ask
        Assert.False(n.OnTrade(Raw(2, 19999.75m, side: Aggressor.Unknown))!.Value.IsBuy);    // at bid
        n.OnQuote(new RawQuote(T0.AddMilliseconds(3), 19999.5m, 20000.5m));
        Assert.True(n.OnTrade(Raw(4, 20000m, side: Aggressor.Unknown))!.Value.IsBuy);        // inside spread, uptick
        Assert.True(n.OnTrade(Raw(5, 20000m, side: Aggressor.Unknown))!.Value.IsBuy);        // unchanged → previous
        Assert.Equal(4, n.Fallbacks);
        Assert.Equal(100.0, n.FallbackPercent);
    }

    [Fact]
    public void CrossedQuote_IsIgnored()
    {
        var n = New();
        n.OnQuote(new RawQuote(T0, 20001m, 20000m));
        n.OnTrade(Raw(1, 20000m));
        Assert.Null(n.OnTrade(Raw(2, 20011m)));         // crossed quote did not count as a quote change
    }

    [Fact]
    public void NonPositiveTickSize_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TickNormalizer(new DataSettings(), 0m));
}
```
- [ ] **Step 5:** Run `dotnet test tests/NQOrderFlow.Tests --filter TickNormalizerTests`. Expected: FAIL (types missing).
- [ ] **Step 6: `Data/TickNormalizer.cs`:**
```csharp
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Data;

/// <summary>
/// Filters and classifies raw prints. The exchange aggressor flag is the truth; the tick rule is the fallback.
/// Not thread-safe: owned by MarketDataPipeline under its lock.
/// </summary>
public sealed class TickNormalizer
{
    private readonly DataSettings _s;
    private RawQuote? _quote;
    private bool _quoteChangedSinceTrade;
    private DateTime _lastTime = DateTime.MinValue;
    private decimal? _lastPrice;
    private bool _lastIsBuy = true;
    private RawTrade? _lastRaw;
    private int _jumpDrops;

    public TickNormalizer(DataSettings settings, decimal tickSize)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize), "Tick size must be positive.");
        _s = settings;
        TickSize = tickSize;
    }

    public decimal TickSize { get; }
    public long Accepted { get; private set; }
    public long Dropped { get; private set; }
    public long Fallbacks { get; private set; }
    public double FallbackPercent => Accepted == 0 ? 0 : 100.0 * Fallbacks / Accepted;

    public decimal RoundToTick(decimal price) => Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;

    public void OnQuote(RawQuote q)
    {
        if (q.Bid <= 0 || q.Ask <= 0 || q.Bid > q.Ask) return;
        _quote = q with { Bid = RoundToTick(q.Bid), Ask = RoundToTick(q.Ask) };
        _quoteChangedSinceTrade = true;
    }

    public Trade? OnTrade(RawTrade raw)
    {
        if (raw.Size <= 0 || raw.Price <= 0 || raw.Utc < _lastTime) return Drop();
        if (_s.DropIdenticalPrints && _lastRaw == raw) return Drop();
        var price = RoundToTick(raw.Price);
        if (IsUnconfirmedJump(price)) return Drop();

        var (isBuy, fallback) = Classify(raw.Side, price);
        _lastTime = raw.Utc;
        _lastPrice = price;
        _lastIsBuy = isBuy;
        _lastRaw = raw;
        _quoteChangedSinceTrade = false;
        _jumpDrops = 0;
        Accepted++;
        if (fallback) Fallbacks++;
        return new Trade(raw.Utc, price, raw.Size, isBuy, fallback);
    }

    private bool IsUnconfirmedJump(decimal price)
    {
        if (_lastPrice is not { } last || _quoteChangedSinceTrade) return false;
        if (Math.Abs(price - last) <= _s.BadTickMaxTicks * TickSize) return false;
        _jumpDrops++;
        return _jumpDrops < _s.BadTickConfirmCount;
    }

    private (bool IsBuy, bool Fallback) Classify(Aggressor side, decimal price)
    {
        if (side == Aggressor.Buy) return (true, false);
        if (side == Aggressor.Sell) return (false, false);
        if (_quote is { } q)
        {
            if (price >= q.Ask) return (true, true);
            if (price <= q.Bid) return (false, true);
        }
        if (_lastPrice is { } last && price != last) return (price > last, true);
        return (_lastIsBuy, true);
    }

    private Trade? Drop()
    {
        Dropped++;
        return null;
    }
}
```
- [ ] **Step 7:** Run the tests. Expected: PASS. Then run `dotnet build "QT MNQ Orderflow Algo.csproj" -c Release`. Expected: 0 warnings.
- [ ] **Step 8:** `git add Config/DataSettings.cs Data/MarketTypes.cs Data/Stats.cs Data/TickNormalizer.cs tests/NQOrderFlow.Tests/TickNormalizerTests.cs tests/NQOrderFlow.Tests/NQOrderFlow.Tests.csproj && git commit -m "feat: tick normalizer with bad-tick filter and aggressor fallback"`

---

### Task 2: FootprintBar

**Files:** Create `Data/FootprintBar.cs`, `tests/NQOrderFlow.Tests/FootprintBarTests.cs`

**Interfaces — Produces:**
- `sealed record PriceLevel(decimal Price, decimal BidVolume, decimal AskVolume)` with `Total`.
- `FootprintBar(DateTime startUtc, decimal tickSize)` with:
  - `void Add(Trade)`
  - bar fields: `StartUtc`, `TickSize`, `Open`, `High`, `Low`, `Close`, `TotalVolume`, `Delta`, `MaxDelta`, `MinDelta`, `TradeCount`
  - `IReadOnlyList<PriceLevel> Levels`, `decimal BidAt(decimal)`, `decimal AskAt(decimal)`, `decimal? Poc`
  - `IReadOnlyList<decimal> BuyImbalances(decimal ratio, decimal minVolume)` and `SellImbalances(...)`
  - `static int LongestStack(IReadOnlyList<decimal> prices, decimal tickSize)`
  - `bool UnfinishedHigh`, `bool UnfinishedLow`

Definitions, from spec `order_flow_engine.footprint`:
- **Buy imbalance at P:** `ask(P) >= ratio × bid(P − 1 tick)` and `ask(P) >= minVolume`. A zero opposite side counts as an infinite ratio.
- **Sell imbalance at P:** `bid(P) >= ratio × ask(P + 1 tick)` and `bid(P) >= minVolume`.
- **POC:** the level with the largest total. On a tie, the lowest price wins, for determinism.
- **Unfinished high:** both bid and ask volume are non-zero at the bar high. The low mirrors this.

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class FootprintBarTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static Trade B(decimal p, decimal size) => new(T0, p, size, true, false);
    private static Trade S(decimal p, decimal size) => new(T0, p, size, false, false);

    private static FootprintBar Bar(params Trade[] trades)
    {
        var bar = new FootprintBar(T0, 0.25m);
        foreach (var t in trades) bar.Add(t);
        return bar;
    }

    [Fact]
    public void Ohlc_Volume_Delta_AndRunningExtremes()
    {
        var bar = Bar(B(100m, 5), S(99.75m, 8), B(100.25m, 2));
        Assert.Equal((100m, 100.25m, 99.75m, 100.25m), (bar.Open, bar.High, bar.Low, bar.Close));
        Assert.Equal(15m, bar.TotalVolume);
        Assert.Equal(-1m, bar.Delta);
        Assert.Equal(5m, bar.MaxDelta);
        Assert.Equal(-3m, bar.MinDelta);
        Assert.Equal(3, bar.TradeCount);
    }

    [Fact]
    public void BidAsk_ArePerPrice()
    {
        var bar = Bar(B(100m, 5), S(100m, 3), B(100m, 1));
        Assert.Equal(6m, bar.AskAt(100m));
        Assert.Equal(3m, bar.BidAt(100m));
        Assert.Equal(0m, bar.AskAt(101m));
    }

    [Fact]
    public void Poc_IsLargestTotal_TieGoesToLowerPrice()
    {
        Assert.Equal(100m, Bar(B(100m, 5), S(100.25m, 3)).Poc);
        Assert.Equal(100m, Bar(B(100m, 4), S(100.25m, 4)).Poc);
        Assert.Null(new FootprintBar(T0, 0.25m).Poc);
    }

    [Fact]
    public void BuyImbalance_IsDiagonal_AskVsBidOneTickBelow()
    {
        // ask 36 @100.25 vs bid 12 @100.00 → 3.0x → imbalance; ask 30 @100.50 vs bid 11 @100.25 → 2.7x → none
        var bar = Bar(S(100m, 12), B(100.25m, 36), S(100.25m, 11), B(100.5m, 30));
        Assert.Equal(new[] { 100.25m }, bar.BuyImbalances(3.0m, 12m));
    }

    [Fact]
    public void BuyImbalance_ZeroOppositeSide_CountsIfAboveMinVolume()
    {
        Assert.Equal(new[] { 100m }, Bar(B(100m, 12)).BuyImbalances(3.0m, 12m));
        Assert.Empty(Bar(B(100m, 11)).BuyImbalances(3.0m, 12m));
    }

    [Fact]
    public void SellImbalance_IsDiagonal_BidVsAskOneTickAbove()
    {
        var bar = Bar(S(100m, 30), B(100.25m, 10));
        Assert.Equal(new[] { 100m }, bar.SellImbalances(3.0m, 12m));
    }

    [Fact]
    public void LongestStack_CountsConsecutiveTicksOnly()
    {
        Assert.Equal(3, FootprintBar.LongestStack(new[] { 100m, 100.25m, 100.5m, 101m }, 0.25m));
        Assert.Equal(0, FootprintBar.LongestStack(Array.Empty<decimal>(), 0.25m));
    }

    [Fact]
    public void UnfinishedAuction_AtHighAndLow()
    {
        var bar = Bar(B(100m, 1), S(100m, 1), B(99m, 1), B(100m, 1));
        Assert.True(bar.UnfinishedHigh);    // 100: both sides traded
        Assert.False(bar.UnfinishedLow);    // 99: only ask
    }
}
```
- [ ] **Step 2:** Run `dotnet test tests/NQOrderFlow.Tests --filter FootprintBarTests`. Expected: FAIL.
- [ ] **Step 3: `Data/FootprintBar.cs`:**
```csharp
namespace QT_MNQ_Orderflow_Algo.Data;

public sealed record PriceLevel(decimal Price, decimal BidVolume, decimal AskVolume)
{
    public decimal Total => BidVolume + AskVolume;
}

/// <summary>
/// One footprint bar. Mutable while it is the builder's current bar (hot path); never mutated after it closes.
/// Not thread-safe: reachable only through MarketDataPipeline.
/// </summary>
public sealed class FootprintBar
{
    private readonly SortedDictionary<decimal, (decimal Bid, decimal Ask)> _levels = new();

    public FootprintBar(DateTime startUtc, decimal tickSize)
    {
        StartUtc = startUtc;
        TickSize = tickSize;
    }

    public DateTime StartUtc { get; }
    public decimal TickSize { get; }
    public decimal Open { get; private set; }
    public decimal High { get; private set; }
    public decimal Low { get; private set; }
    public decimal Close { get; private set; }
    public decimal TotalVolume { get; private set; }
    public decimal Delta { get; private set; }
    public decimal MaxDelta { get; private set; }
    public decimal MinDelta { get; private set; }
    public int TradeCount { get; private set; }

    public void Add(Trade t)
    {
        if (TradeCount == 0) Open = High = Low = t.Price;
        High = Math.Max(High, t.Price);
        Low = Math.Min(Low, t.Price);
        Close = t.Price;
        var level = _levels.TryGetValue(t.Price, out var v) ? v : (0m, 0m);
        _levels[t.Price] = t.IsBuy ? (level.Bid, level.Ask + t.Size) : (level.Bid + t.Size, level.Ask);
        TotalVolume += t.Size;
        Delta += t.IsBuy ? t.Size : -t.Size;
        MaxDelta = TradeCount == 0 ? Delta : Math.Max(MaxDelta, Delta);
        MinDelta = TradeCount == 0 ? Delta : Math.Min(MinDelta, Delta);
        TradeCount++;
    }

    public IReadOnlyList<PriceLevel> Levels => _levels.Select(kv => new PriceLevel(kv.Key, kv.Value.Bid, kv.Value.Ask)).ToList();
    public decimal BidAt(decimal price) => _levels.TryGetValue(price, out var v) ? v.Bid : 0m;
    public decimal AskAt(decimal price) => _levels.TryGetValue(price, out var v) ? v.Ask : 0m;

    public decimal? Poc
    {
        get
        {
            decimal? best = null;
            decimal bestVol = -1m;
            foreach (var (price, v) in _levels)
            {
                if (v.Bid + v.Ask > bestVol) { best = price; bestVol = v.Bid + v.Ask; }
            }
            return best;
        }
    }

    public IReadOnlyList<decimal> BuyImbalances(decimal ratio, decimal minVolume) =>
        _levels.Where(kv => kv.Value.Ask >= minVolume && kv.Value.Ask >= ratio * BidAt(kv.Key - TickSize))
               .Select(kv => kv.Key).ToList();

    public IReadOnlyList<decimal> SellImbalances(decimal ratio, decimal minVolume) =>
        _levels.Where(kv => kv.Value.Bid >= minVolume && kv.Value.Bid >= ratio * AskAt(kv.Key + TickSize))
               .Select(kv => kv.Key).ToList();

    public static int LongestStack(IReadOnlyList<decimal> prices, decimal tickSize)
    {
        if (prices.Count == 0) return 0;
        var sorted = prices.OrderBy(p => p).ToArray();
        int best = 1, run = 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            run = sorted[i] - sorted[i - 1] == tickSize ? run + 1 : 1;
            best = Math.Max(best, run);
        }
        return best;
    }

    public bool UnfinishedHigh => TradeCount > 0 && BidAt(High) > 0 && AskAt(High) > 0;
    public bool UnfinishedLow => TradeCount > 0 && BidAt(Low) > 0 && AskAt(Low) > 0;
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add Data/FootprintBar.cs tests/NQOrderFlow.Tests/FootprintBarTests.cs && git commit -m "feat: footprint bar with delta, POC, diagonal imbalances, unfinished auctions"`

---

### Task 3: FootprintBuilder

**Files:** Create `Data/FootprintBuilder.cs`, `tests/NQOrderFlow.Tests/FootprintBuilderTests.cs`

**Interfaces:**
- Consumes `FootprintBar` and `Trade`.
- Produces `FootprintBuilder(TimeSpan period, decimal tickSize, int capacity)` with:
  - `FootprintBar? OnTrade(Trade)`, which returns the bar that just closed, or null
  - `FootprintBar? CloseIfElapsed(DateTime nowUtc)`
  - `FootprintBar? Current`
  - `IReadOnlyList<FootprintBar> Closed`, a copy, oldest first, capped at `capacity`
  - `static DateTime BucketStart(DateTime utc, TimeSpan period)`

Bars are aligned to UTC multiples of the period. Because ET offsets are whole hours, whole-minute periods line up with ET minutes.

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class FootprintBuilderTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static Trade At(double seconds, decimal price = 100m) => new(T0.AddSeconds(seconds), price, 1, true, false);

    [Fact]
    public void BucketStart_FloorsToPeriod()
        => Assert.Equal(T0.AddMinutes(5), FootprintBuilder.BucketStart(T0.AddMinutes(7).AddSeconds(3), TimeSpan.FromMinutes(5)));

    [Fact]
    public void NewMinute_ClosesPreviousBar()
    {
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 10);
        Assert.Null(b.OnTrade(At(0)));
        Assert.Null(b.OnTrade(At(59.9)));
        var closed = b.OnTrade(At(60));
        Assert.Equal(T0, closed!.StartUtc);
        Assert.Equal(2m, closed.TotalVolume);
        Assert.Equal(T0.AddMinutes(1), b.Current!.StartUtc);
    }

    [Fact]
    public void QuietMinutes_CreateNoFakeBars()
    {
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 10);
        b.OnTrade(At(0));
        b.OnTrade(At(300));
        Assert.Single(b.Closed);
    }

    [Fact]
    public void CloseIfElapsed_ClosesOnTimeWithoutTrades()
    {
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 10);
        b.OnTrade(At(10));
        Assert.Null(b.CloseIfElapsed(T0.AddSeconds(59)));
        Assert.Equal(T0, b.CloseIfElapsed(T0.AddSeconds(60))!.StartUtc);
        Assert.Null(b.Current);
    }

    [Fact]
    public void History_IsCapped_OldestDropped()
    {
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 2);
        for (int m = 0; m <= 3; m++) b.OnTrade(At(m * 60));
        Assert.Equal(new[] { T0.AddMinutes(1), T0.AddMinutes(2) }, b.Closed.Select(x => x.StartUtc));
    }
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3: `Data/FootprintBuilder.cs`:**
```csharp
namespace QT_MNQ_Orderflow_Algo.Data;

/// <summary>Buckets trades into fixed-period footprint bars. Not thread-safe: owned by MarketDataPipeline.</summary>
public sealed class FootprintBuilder
{
    private readonly TimeSpan _period;
    private readonly decimal _tickSize;
    private readonly int _capacity;
    private readonly List<FootprintBar> _closed = new();

    public FootprintBuilder(TimeSpan period, decimal tickSize, int capacity)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _period = period;
        _tickSize = tickSize;
        _capacity = capacity;
    }

    public FootprintBar? Current { get; private set; }
    public IReadOnlyList<FootprintBar> Closed => _closed.ToArray();

    public static DateTime BucketStart(DateTime utc, TimeSpan period) =>
        new(utc.Ticks - utc.Ticks % period.Ticks, DateTimeKind.Utc);

    public FootprintBar? OnTrade(Trade t)
    {
        var start = BucketStart(t.Utc, _period);
        FootprintBar? closed = null;
        if (Current is not null && start != Current.StartUtc) closed = CloseCurrent();
        Current ??= new FootprintBar(start, _tickSize);
        Current.Add(t);
        return closed;
    }

    public FootprintBar? CloseIfElapsed(DateTime nowUtc) =>
        Current is not null && nowUtc >= Current.StartUtc + _period ? CloseCurrent() : null;

    private FootprintBar CloseCurrent()
    {
        var bar = Current!;
        _closed.Add(bar);
        if (_closed.Count > _capacity) _closed.RemoveAt(0);
        Current = null;
        return bar;
    }
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add Data/FootprintBuilder.cs tests/NQOrderFlow.Tests/FootprintBuilderTests.cs && git commit -m "feat: footprint builder with time-close and capped history"`

---

### Task 4: TapeWindow and BucketSeries

**Files:** Create `Data/TapeWindow.cs`, `Data/BucketSeries.cs`, `tests/NQOrderFlow.Tests/TapeTests.cs`

**Interfaces — Produces:**
- `TapeWindow(TimeSpan length)` with:
  - `void Add(Trade)` and `void Trim(DateTime nowUtc)`
  - `decimal BuyVolume`, `SellVolume`, `Volume`, `Delta`
  - `int Count` and `IReadOnlyList<Trade> Trades`
- `sealed record Bucket(DateTime StartUtc, decimal Volume, decimal Delta, int Trades)`
- `BucketSeries(TimeSpan length, int capacity)` with:
  - `void Add(Trade)` and `void AdvanceTo(DateTime utc)`
  - `IReadOnlyList<Bucket> Closed`: oldest first, gaps zero-filled, capped
  - `decimal MedianVolume`

A trade stays in a window while `nowUtc − t.Utc < length`.

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class TapeTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static Trade At(double s, decimal size, bool buy) => new(T0.AddSeconds(s), 100m, size, buy, false);

    [Fact]
    public void Tape_SumsAndExpires()
    {
        var w = new TapeWindow(TimeSpan.FromSeconds(20));
        w.Add(At(0, 5, true));
        w.Add(At(10, 3, false));
        Assert.Equal((5m, 3m, 2m, 2), (w.BuyVolume, w.SellVolume, w.Delta, w.Count));
        w.Trim(T0.AddSeconds(20));                       // first trade is exactly 20 s old → out
        Assert.Equal((0m, 3m, 1), (w.BuyVolume, w.SellVolume, w.Count));
    }

    [Fact]
    public void Buckets_CloseAndZeroFillGaps()
    {
        var b = new BucketSeries(TimeSpan.FromSeconds(20), 10);
        b.Add(At(1, 4, true));
        b.Add(At(5, 1, false));
        b.Add(At(65, 2, true));                          // skips the 20 s and 40 s buckets
        Assert.Equal(
            new[] { (T0, 5m, 3m, 2), (T0.AddSeconds(20), 0m, 0m, 0), (T0.AddSeconds(40), 0m, 0m, 0) },
            b.Closed.Select(x => (x.StartUtc, x.Volume, x.Delta, x.Trades)));
    }

    [Fact]
    public void Buckets_AreCapped_AndMedianUsesClosedOnly()
    {
        var b = new BucketSeries(TimeSpan.FromSeconds(20), 2);
        b.Add(At(0, 2, true));
        b.Add(At(20, 4, true));
        b.Add(At(40, 6, true));
        b.Add(At(60, 100, true));                        // current, not closed
        Assert.Equal(new[] { 4m, 6m }, b.Closed.Select(x => x.Volume));
        Assert.Equal(5m, b.MedianVolume);
    }

    [Fact]
    public void Buckets_AdvanceTo_ClosesOnTime()
    {
        var b = new BucketSeries(TimeSpan.FromSeconds(20), 10);
        b.Add(At(1, 3, true));
        b.AdvanceTo(T0.AddSeconds(25));
        Assert.Single(b.Closed);
    }
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3: Implement both files:**
```csharp
namespace QT_MNQ_Orderflow_Algo.Data;

/// <summary>Rolling window of the last N seconds of trades. Not thread-safe: owned by MarketDataPipeline.</summary>
public sealed class TapeWindow
{
    private readonly Queue<Trade> _trades = new();
    private readonly TimeSpan _length;

    public TapeWindow(TimeSpan length) => _length = length;

    public decimal BuyVolume { get; private set; }
    public decimal SellVolume { get; private set; }
    public decimal Volume => BuyVolume + SellVolume;
    public decimal Delta => BuyVolume - SellVolume;
    public int Count => _trades.Count;
    public IReadOnlyList<Trade> Trades => _trades.ToArray();

    public void Add(Trade t)
    {
        _trades.Enqueue(t);
        if (t.IsBuy) BuyVolume += t.Size; else SellVolume += t.Size;
        Trim(t.Utc);
    }

    public void Trim(DateTime nowUtc)
    {
        while (_trades.Count > 0 && nowUtc - _trades.Peek().Utc >= _length)
        {
            var old = _trades.Dequeue();
            if (old.IsBuy) BuyVolume -= old.Size; else SellVolume -= old.Size;
        }
    }
}
```
```csharp
namespace QT_MNQ_Orderflow_Algo.Data;

public sealed record Bucket(DateTime StartUtc, decimal Volume, decimal Delta, int Trades);

/// <summary>Fixed-length time buckets for baselines. Empty intervals are zero-filled so quiet periods count.</summary>
public sealed class BucketSeries
{
    private readonly TimeSpan _length;
    private readonly int _capacity;
    private readonly Queue<Bucket> _closed = new();
    private DateTime? _start;
    private decimal _volume, _delta;
    private int _trades;

    public BucketSeries(TimeSpan length, int capacity)
    {
        if (length <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(length));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _length = length;
        _capacity = capacity;
    }

    public IReadOnlyList<Bucket> Closed => _closed.ToArray();
    public decimal MedianVolume => Stats.Median(_closed.Select(b => b.Volume).ToList());

    public void Add(Trade t)
    {
        AdvanceTo(t.Utc);
        _volume += t.Size;
        _delta += t.IsBuy ? t.Size : -t.Size;
        _trades++;
    }

    public void AdvanceTo(DateTime utc)
    {
        var start = FootprintBuilder.BucketStart(utc, _length);
        if (_start is null) { _start = start; return; }
        if (start <= _start.Value) return;
        Push(new Bucket(_start.Value, _volume, _delta, _trades));
        var empty = (int)Math.Min(_capacity, (start - _start.Value).Ticks / _length.Ticks - 1);
        for (int i = empty; i >= 1; i--) Push(new Bucket(start - TimeSpan.FromTicks(_length.Ticks * i), 0m, 0m, 0));
        _start = start;
        _volume = 0m;
        _delta = 0m;
        _trades = 0;
    }

    private void Push(Bucket b)
    {
        _closed.Enqueue(b);
        while (_closed.Count > _capacity) _closed.Dequeue();
    }
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add Data/TapeWindow.cs Data/BucketSeries.cs tests/NQOrderFlow.Tests/TapeTests.cs && git commit -m "feat: rolling tape window and zero-filled bucket series"`

---

### Task 5: SessionProfile (+ SessionClock helpers)

**Files:**
- Create: `Data/SessionProfile.cs`, `tests/NQOrderFlow.Tests/SessionProfileTests.cs`
- Modify: `Compliance/SessionClock.cs`, `tests/NQOrderFlow.Tests/SessionClockTests.cs`

**Interfaces — Produces:**
- `SessionClock.PreviousTradingDate(DateOnly)`: skips Sat/Sun. Exchange holidays are not modeled (an ASSUMPTION).
- `SessionClock.SessionStartUtc(DateOnly tradingDate)`: the previous calendar day at `TradingDayRollEt` ET, as UTC.
- `sealed record ValueArea(decimal Poc, decimal Vah, decimal Val)` with `static ValueArea? Compute(IReadOnlyDictionary<decimal, decimal> volumeByPrice, decimal percent)`
- `sealed record PriorSession(DateOnly TradingDate, decimal High, decimal Low, decimal Close, ValueArea Value)`
- `sealed record SessionLevels(...)` (fields below) with `decimal? Band(int k)`
- `SessionProfile(SessionClock clock, DataSettings settings)` with `void OnTrade(Trade)`, `SessionLevels Snapshot()` and `PriorSession? Prior`

Rulings (these go into ASSUMPTIONS in Task 8):
- VWAP and the volume profile are anchored at the **RTH open (09:30 ET)**, and the profile covers RTH only (09:30–16:00 ET).
- The prior day's high, low and close, and its POC/VAH/VAL, come from the prior **RTH** session.
- The overnight high/low runs from 18:00 ET to 09:30 ET.
- The opening ranges are the first `OpeningRangeShortMinutes` and `OpeningRangeLongMinutes` after 09:30 ET.
- The value area grows one traded level at a time from the POC toward the larger neighbor (up on ties), until it holds `ValueAreaPercent` of volume.

- [ ] **Step 1: Failing SessionClock tests.** Append to `SessionClockTests`:
```csharp
    [Fact]
    public void PreviousTradingDate_SkipsWeekend()
    {
        Assert.Equal(new DateOnly(2026, 10, 30), _clock.PreviousTradingDate(new DateOnly(2026, 11, 2)));
        Assert.Equal(new DateOnly(2026, 10, 28), _clock.PreviousTradingDate(new DateOnly(2026, 10, 29)));
    }

    [Fact]
    public void SessionStartUtc_IsPreviousDay1800Et_DstAware()
    {
        Assert.Equal(Utc(2026, 10, 28, 22, 0), _clock.SessionStartUtc(new DateOnly(2026, 10, 29)));  // EDT
        Assert.Equal(Utc(2026, 11, 1, 23, 0), _clock.SessionStartUtc(new DateOnly(2026, 11, 2)));    // Sun 18:00 EST
    }
```
- [ ] **Step 2: Failing SessionProfile tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class SessionProfileTests
{
    private static readonly SessionClock Clock = new(SessionSettings.Default());
    private static SessionProfile New() => new(Clock, new DataSettings());
    private static Trade At(DateTime utc, decimal price, decimal size = 1, bool buy = true) => new(utc, price, size, buy, false);
    private static DateTime Edt(int d, int h, int m) => new DateTime(2026, 10, d, h, m, 0, DateTimeKind.Utc).AddHours(4);   // ET → UTC in EDT
    private static DateTime Est(int d, int h, int m) => new DateTime(2026, 11, d, h, m, 0, DateTimeKind.Utc).AddHours(5);   // ET → UTC in EST

    [Fact]
    public void ValueArea_ExpandsFromPocTowardLargerNeighbor()
    {
        var vol = new Dictionary<decimal, decimal> { [99m] = 10, [100m] = 50, [101m] = 30, [102m] = 10 };
        var va = ValueArea.Compute(vol, 70m)!;        // total 100, target 70: 50 → +30 (101) = 80
        Assert.Equal((100m, 101m, 100m), (va.Poc, va.Vah, va.Val));
        Assert.Null(ValueArea.Compute(new Dictionary<decimal, decimal>(), 70m));
    }

    [Fact]
    public void Vwap_AndStdDev_AreVolumeWeighted_RthOnly()
    {
        var p = New();
        p.OnTrade(At(Edt(29, 9, 0), 50m, 100));          // pre-RTH: overnight only
        p.OnTrade(At(Edt(29, 9, 30), 100m, 1));
        p.OnTrade(At(Edt(29, 9, 31), 104m, 3));
        var s = p.Snapshot();
        Assert.Equal(103m, s.Vwap);                       // (100 + 312) / 4
        Assert.Equal(103m + 2 * s.VwapStdDev!.Value, s.Band(2));
        Assert.True(Math.Abs(s.VwapStdDev.Value - 1.7320508m) < 0.0001m);   // sqrt(3)
        Assert.Equal((50m, 50m), (s.OnHigh, s.OnLow));
    }

    [Fact]
    public void OpeningRanges_FirstFiveAndFifteenMinutes_InEdtAndEst()
    {
        foreach (var et in new Func<int, int, DateTime>[] { (h, m) => Edt(29, h, m), (h, m) => Est(5, h, m) })
        {
            var p = New();
            p.OnTrade(At(et(9, 30), 100m));
            p.OnTrade(At(et(9, 34), 102m));
            p.OnTrade(At(et(9, 36), 105m));               // after OR5, inside OR15
            p.OnTrade(At(et(9, 45), 90m));                // after OR15
            var s = p.Snapshot();
            Assert.Equal((102m, 100m), (s.Or5High, s.Or5Low));
            Assert.Equal((105m, 100m), (s.Or15High, s.Or15Low));
            Assert.Equal((105m, 90m), (s.RthHigh, s.RthLow));
        }
    }

    [Fact]
    public void Roll_At1800Et_KeepsPriorRthSummary_AndResetsSession()
    {
        var p = New();
        p.OnTrade(At(Edt(28, 10, 0), 100m, 5));
        p.OnTrade(At(Edt(28, 15, 59), 110m, 1));
        p.OnTrade(At(Edt(28, 18, 0), 120m, 1));           // new session (trading date 29th), overnight
        var s = p.Snapshot();
        Assert.Equal(new DateOnly(2026, 10, 29), s.TradingDate);
        Assert.Null(s.Vwap);
        Assert.Equal((120m, 120m), (s.OnHigh, s.OnLow));
        Assert.Equal(new DateOnly(2026, 10, 28), s.Prior!.TradingDate);
        Assert.Equal((110m, 100m, 110m, 100m), (s.Prior.High, s.Prior.Low, s.Prior.Close, s.Prior.Value.Poc));
    }

    [Fact]
    public void PostRthTrades_AreIgnoredForRthAndOvernight()
    {
        var p = New();
        p.OnTrade(At(Edt(29, 16, 30), 100m));
        var s = p.Snapshot();
        Assert.Null(s.RthHigh);
        Assert.Null(s.OnHigh);
    }
}
```
- [ ] **Step 3:** Run `dotnet test tests/NQOrderFlow.Tests --filter "SessionProfileTests|SessionClockTests"`. Expected: FAIL.
- [ ] **Step 4: Implement.** Add to `SessionClock`:
```csharp
    /// <summary>Previous weekday trading date. Exchange holidays are not modeled (see docs/ASSUMPTIONS.md).</summary>
    public DateOnly PreviousTradingDate(DateOnly tradingDate)
    {
        var d = tradingDate.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    /// <summary>UTC start of a trading date's session: the previous calendar day at the roll time, ET.</summary>
    public DateTime SessionStartUtc(DateOnly tradingDate) =>
        EtToUtc(tradingDate.AddDays(-1).ToDateTime(TimeOnly.FromTimeSpan(Settings.TradingDayRollEt)));
```
Create `Data/SessionProfile.cs`:
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Data;

public sealed record ValueArea(decimal Poc, decimal Vah, decimal Val)
{
    /// <summary>POC (ties → lowest price), then expand one traded level at a time toward the larger neighbor.</summary>
    public static ValueArea? Compute(IReadOnlyDictionary<decimal, decimal> volumeByPrice, decimal percent)
    {
        if (volumeByPrice.Count == 0) return null;
        var prices = volumeByPrice.Keys.OrderBy(p => p).ToArray();
        var total = volumeByPrice.Values.Sum();
        if (total <= 0) return null;
        int poc = 0;
        for (int i = 1; i < prices.Length; i++)
            if (volumeByPrice[prices[i]] > volumeByPrice[prices[poc]]) poc = i;
        int lo = poc, hi = poc;
        decimal acc = volumeByPrice[prices[poc]];
        var target = total * percent / 100m;
        while (acc < target && (lo > 0 || hi < prices.Length - 1))
        {
            var up = hi < prices.Length - 1 ? volumeByPrice[prices[hi + 1]] : -1m;
            var down = lo > 0 ? volumeByPrice[prices[lo - 1]] : -1m;
            if (up >= down) { hi++; acc += up; }
            else { lo--; acc += down; }
        }
        return new ValueArea(prices[poc], prices[hi], prices[lo]);
    }
}

public sealed record PriorSession(DateOnly TradingDate, decimal High, decimal Low, decimal Close, ValueArea Value);

public sealed record SessionLevels(
    DateOnly? TradingDate,
    decimal? Vwap, decimal? VwapStdDev,
    decimal? OnHigh, decimal? OnLow,
    decimal? Or5High, decimal? Or5Low, decimal? Or15High, decimal? Or15Low,
    decimal? RthHigh, decimal? RthLow,
    ValueArea? Developing,
    PriorSession? Prior)
{
    public decimal? Band(int k) => Vwap is { } v && VwapStdDev is { } sd ? v + k * sd : null;
}

/// <summary>
/// RTH-anchored VWAP and volume profile, opening ranges, overnight range and the prior RTH summary.
/// Not thread-safe: owned by MarketDataPipeline.
/// </summary>
public sealed class SessionProfile
{
    private readonly SessionClock _clock;
    private readonly DataSettings _s;
    private readonly Dictionary<decimal, decimal> _rthVolume = new();
    private DateOnly? _date;
    private decimal _sumV, _sumPV;
    private double _sumP2V;
    private decimal? _rthHigh, _rthLow, _rthClose, _onHigh, _onLow, _or5High, _or5Low, _or15High, _or15Low;

    public SessionProfile(SessionClock clock, DataSettings settings)
    {
        _clock = clock;
        _s = settings;
    }

    public PriorSession? Prior { get; private set; }

    public void OnTrade(Trade t)
    {
        var tradingDate = _clock.TradingDate(t.Utc);
        if (_date != tradingDate) Roll(tradingDate);
        var tod = _clock.ToEt(t.Utc).TimeOfDay;
        if (tod >= _s.RthOpenEt && tod < _s.RthCloseEt) AddRth(t, (tod - _s.RthOpenEt).TotalMinutes);
        else if (tod < _s.RthOpenEt || tod >= _clock.Settings.TradingDayRollEt)
        {
            _onHigh = Max(_onHigh, t.Price);
            _onLow = Min(_onLow, t.Price);
        }
    }

    public SessionLevels Snapshot()
    {
        decimal? vwap = _sumV > 0 ? _sumPV / _sumV : null;
        decimal? sd = null;
        if (vwap is { } v)
        {
            var variance = _sumP2V / (double)_sumV - (double)v * (double)v;
            sd = (decimal)Math.Sqrt(Math.Max(0, variance));
        }
        return new SessionLevels(_date, vwap, sd, _onHigh, _onLow, _or5High, _or5Low, _or15High, _or15Low,
            _rthHigh, _rthLow, ValueArea.Compute(_rthVolume, _s.ValueAreaPercent), Prior);
    }

    private void AddRth(Trade t, double minutesSinceOpen)
    {
        _rthVolume[t.Price] = (_rthVolume.TryGetValue(t.Price, out var v) ? v : 0m) + t.Size;
        _sumV += t.Size;
        _sumPV += t.Price * t.Size;
        _sumP2V += (double)t.Price * (double)t.Price * (double)t.Size;
        _rthHigh = Max(_rthHigh, t.Price);
        _rthLow = Min(_rthLow, t.Price);
        _rthClose = t.Price;
        if (minutesSinceOpen < _s.OpeningRangeShortMinutes) { _or5High = Max(_or5High, t.Price); _or5Low = Min(_or5Low, t.Price); }
        if (minutesSinceOpen < _s.OpeningRangeLongMinutes) { _or15High = Max(_or15High, t.Price); _or15Low = Min(_or15Low, t.Price); }
    }

    private void Roll(DateOnly tradingDate)
    {
        if (_date is { } prev && _sumV > 0 && ValueArea.Compute(_rthVolume, _s.ValueAreaPercent) is { } va)
            Prior = new PriorSession(prev, _rthHigh!.Value, _rthLow!.Value, _rthClose!.Value, va);
        _date = tradingDate;
        _rthVolume.Clear();
        _sumV = 0m;
        _sumPV = 0m;
        _sumP2V = 0;
        _rthHigh = _rthLow = _rthClose = _onHigh = _onLow = _or5High = _or5Low = _or15High = _or15Low = null;
    }

    private static decimal Max(decimal? a, decimal b) => a is { } x ? Math.Max(x, b) : b;
    private static decimal Min(decimal? a, decimal b) => a is { } x ? Math.Min(x, b) : b;
}
```
- [ ] **Step 5:** Run the tests. Expected: PASS. Check the VWAP math by hand:
  - mean of {100×1, 104×3} = 412/4 = 103
  - E[p²] = (10000 + 3×10816)/4 = 10612
  - variance = 10612 − 10609 = 3, so sd = √3
- [ ] **Step 6:** `git add Data/SessionProfile.cs Compliance/SessionClock.cs tests/NQOrderFlow.Tests/SessionProfileTests.cs tests/NQOrderFlow.Tests/SessionClockTests.cs && git commit -m "feat: session profile with RTH VWAP bands, value area, opening and overnight ranges"`

---

### Task 6: OrderFlowFeatures

**Files:** Create `OrderFlow/OrderFlowFeatures.cs`, `tests/NQOrderFlow.Tests/OrderFlowFeaturesTests.cs`

**Interfaces:**
- **Consumes:** `FootprintBar`, `Bucket`, `Trade`, `ValueArea`, `DataSettings`, `Stats`.
- **Produces:**
  - `enum FlowContext { None, InitiativeBuy, InitiativeSell, ResponsiveBuy, ResponsiveSell }`
  - `sealed record FeatureInputs(DateTime Utc, IReadOnlyList<FootprintBar> Bars1m, IReadOnlyList<Bucket> Buckets, int TapeMidCount, decimal SessionCvd, decimal? LastPrice, ValueArea? Developing, DataSettings Settings, decimal TickSize)`
  - `sealed record OrderFlowSnapshot(...)` (fields in the code below)
  - `sealed record AbsorptionResult(bool Detected, decimal Score, decimal AggressiveVolume, decimal DominancePercent)` with `static None`
  - `static OrderFlowSnapshot OrderFlowFeatures.Compute(FeatureInputs)`
  - `static AbsorptionResult OrderFlowFeatures.Absorption(IReadOnlyList<Trade> midWindow, decimal medianBucketVolume, decimal level, bool bullish, DataSettings s, decimal tickSize)`

**Feature definitions:**
- **NormalizedBarDelta:** the last closed 1m delta ÷ the median volume of the last `MedianBarLookback` bars, clamped to [−1, 1].
- **CvdSlope5m / CvdSlope15m:** the mean delta of the last 5 or 15 closed 1m bars.
- **StackedBuyLevels / StackedSellLevels:** the longest diagonal-imbalance stack across the last 2 closed 1m bars.
- **DeltaFlip** (ruling: uses closed mid-window buckets, so it is deterministic): ±1 when two things hold:
  - The last closed bucket's delta sign is the opposite of the previous non-zero bucket's.
  - |delta| ≥ `DeltaFlipMinMultiple` × the median bucket volume.

  Otherwise 0.
- **VelocityZ:** (trades in the current mid window − mean trades per closed bucket) ÷ std. The spike flag is set when z > `VelocityZLimit`.
- **FlowContext:** looks at the last 5 closed 1m bars and the developing value area.
  - Initiative: the 5-minute price change and delta agree, and price is beyond VAH (buy) or VAL (sell).
  - Responsive: delta opposes the price change; the side follows the delta.
  - Otherwise: None.
- **Absorption** at a level the caller supplies, measured over the last mid window. All of these must hold:
  - Aggressive volume on the absorbed side ≥ `AbsorptionVolMultiple` × the median bucket volume.
  - That side is ≥ `AbsorptionDominancePercent` of the window's volume.
  - Price traded within `AbsorptionMaxProgressTicks` of the level and got no further.

  Bullish means selling was absorbed at a low. Score = min(1, aggressive ÷ (multiple × median)).
- **Deferred to Phase 3** (they need pivots and levels): Exhaustion, DeltaDivergence and TrappedTraders. This is noted in ASSUMPTIONS.

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using QT_MNQ_Orderflow_Algo.OrderFlow;
using Xunit;

public sealed class OrderFlowFeaturesTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DataSettings S = new();

    private static FootprintBar Bar(int minute, decimal open, decimal close, decimal buy, decimal sell)
    {
        var bar = new FootprintBar(T0.AddMinutes(minute), 0.25m);
        bar.Add(new Trade(T0.AddMinutes(minute), open, buy, true, false));
        bar.Add(new Trade(T0.AddMinutes(minute).AddSeconds(30), close, sell, false, false));
        return bar;
    }

    private static FeatureInputs Inputs(IReadOnlyList<FootprintBar>? bars = null, IReadOnlyList<Bucket>? buckets = null,
        int tapeCount = 0, decimal? last = null, ValueArea? va = null) =>
        new(T0, bars ?? Array.Empty<FootprintBar>(), buckets ?? Array.Empty<Bucket>(), tapeCount, 0m, last, va, S, 0.25m);

    [Fact]
    public void Empty_IsNeutral()
    {
        var s = OrderFlowFeatures.Compute(Inputs());
        Assert.Equal((0m, 0m, 0, 0, 0.0), (s.LastBarDelta, s.NormalizedBarDelta, s.StackedBuyLevels, s.DeltaFlip, s.VelocityZ));
        Assert.Equal(FlowContext.None, s.Context);
    }

    [Fact]
    public void NormalizedDelta_IsClampedByMedianBarVolume()
    {
        var bars = new[] { Bar(0, 100m, 100m, 10, 10), Bar(1, 100m, 100m, 40, 0) };   // volumes 20, 40 → median 30
        var s = OrderFlowFeatures.Compute(Inputs(bars));
        Assert.Equal(40m, s.LastBarDelta);
        Assert.Equal(1m, s.NormalizedBarDelta);                                     // 40/30 clamped
    }

    [Fact]
    public void CvdSlope_IsMeanBarDelta()
    {
        var bars = Enumerable.Range(0, 5).Select(m => Bar(m, 100m, 100m, 10, 4)).ToArray();   // delta +6 each
        Assert.Equal(6m, OrderFlowFeatures.Compute(Inputs(bars)).CvdSlope5m);
    }

    [Fact]
    public void DeltaFlip_RequiresOppositeSignAndSize()
    {
        var buckets = new[] { new Bucket(T0, 10m, -6m, 5), new Bucket(T0.AddSeconds(20), 10m, 6m, 5) };   // median 10, min 5
        Assert.Equal(1, OrderFlowFeatures.Compute(Inputs(buckets: buckets)).DeltaFlip);
        var small = new[] { buckets[0], new Bucket(T0.AddSeconds(20), 10m, 4m, 5) };
        Assert.Equal(0, OrderFlowFeatures.Compute(Inputs(buckets: small)).DeltaFlip);
    }

    [Fact]
    public void VelocityZ_ComparesCurrentWindowToBucketBaseline()
    {
        var buckets = new[] { new Bucket(T0, 1m, 0m, 4), new Bucket(T0.AddSeconds(20), 1m, 0m, 6) };    // mean 5, sd 1
        var s = OrderFlowFeatures.Compute(Inputs(buckets: buckets, tapeCount: 9));
        Assert.Equal(4.0, s.VelocityZ, 6);
        Assert.True(s.VelocitySpike);
    }

    [Fact]
    public void Context_InitiativeBuy_AboveValueWithAgreeingDelta()
    {
        var bars = Enumerable.Range(0, 5).Select(m => Bar(m, 100m + m, 100m + m, 10, 2)).ToArray();
        var s = OrderFlowFeatures.Compute(Inputs(bars, last: 105m, va: new ValueArea(100m, 102m, 98m)));
        Assert.Equal(FlowContext.InitiativeBuy, s.Context);
    }

    [Fact]
    public void Context_Responsive_WhenDeltaOpposesMove()
    {
        var bars = Enumerable.Range(0, 5).Select(m => Bar(m, 100m + m, 100m + m, 2, 10)).ToArray();
        var s = OrderFlowFeatures.Compute(Inputs(bars, last: 105m, va: new ValueArea(100m, 102m, 98m)));
        Assert.Equal(FlowContext.ResponsiveSell, s.Context);
    }

    [Fact]
    public void Absorption_Bullish_HeavySellingHeldAtLevel()
    {
        var trades = new[]
        {
            new Trade(T0, 100m, 18m, false, false), new Trade(T0.AddSeconds(5), 99.75m, 5m, false, false),
            new Trade(T0.AddSeconds(9), 100m, 4m, true, false),
        };
        var r = OrderFlowFeatures.Absorption(trades, 10m, 100m, bullish: true, S, 0.25m);   // sell 23 ≥ 20; 85%; low 99.75 within 3 ticks
        Assert.True(r.Detected);
        Assert.Equal(1m, r.Score);
        Assert.False(OrderFlowFeatures.Absorption(trades, 10m, 102m, bullish: true, S, 0.25m).Detected);  // never traded near 102
    }

    [Fact]
    public void Absorption_FailsWhenPriceBrokeThrough()
    {
        var trades = new[] { new Trade(T0, 100m, 15m, false, false), new Trade(T0.AddSeconds(5), 99m, 10m, false, false) };
        Assert.False(OrderFlowFeatures.Absorption(trades, 10m, 100m, bullish: true, S, 0.25m).Detected);   // 4 ticks through
    }
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3: `OrderFlow/OrderFlowFeatures.cs`:**
```csharp
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;

namespace QT_MNQ_Orderflow_Algo.OrderFlow;

public enum FlowContext { None, InitiativeBuy, InitiativeSell, ResponsiveBuy, ResponsiveSell }

public sealed record FeatureInputs(
    DateTime Utc,
    IReadOnlyList<FootprintBar> Bars1m,
    IReadOnlyList<Bucket> Buckets,
    int TapeMidCount,
    decimal SessionCvd,
    decimal? LastPrice,
    ValueArea? Developing,
    DataSettings Settings,
    decimal TickSize);

/// <summary>Immutable order-flow state at one instant. Journaled with every proposal from Phase 4 on.</summary>
public sealed record OrderFlowSnapshot(
    DateTime Utc,
    decimal LastBarDelta,
    decimal NormalizedBarDelta,
    decimal SessionCvd,
    decimal CvdSlope5m,
    decimal CvdSlope15m,
    int StackedBuyLevels,
    int StackedSellLevels,
    bool UnfinishedHigh,
    bool UnfinishedLow,
    int DeltaFlip,
    double VelocityZ,
    bool VelocitySpike,
    FlowContext Context);

public sealed record AbsorptionResult(bool Detected, decimal Score, decimal AggressiveVolume, decimal DominancePercent)
{
    public static AbsorptionResult None { get; } = new(false, 0m, 0m, 0m);
}

public static class OrderFlowFeatures
{
    private const int StackLookbackBars = 2;
    private const int ContextBars = 5;
    private const int SlopeShortBars = 5;
    private const int SlopeLongBars = 15;

    public static OrderFlowSnapshot Compute(FeatureInputs x)
    {
        var s = x.Settings;
        var bars = x.Bars1m;
        var last = bars.Count > 0 ? bars[^1] : null;
        var medianBarVolume = Stats.Median(Tail(bars, s.MedianBarLookback).Select(b => b.TotalVolume).ToList());
        var lastDelta = last?.Delta ?? 0m;
        var normalized = medianBarVolume > 0 ? Math.Clamp(lastDelta / medianBarVolume, -1m, 1m) : 0m;
        var recent = Tail(bars, StackLookbackBars);
        var (velocityZ, spike) = Velocity(x);
        return new OrderFlowSnapshot(
            x.Utc, lastDelta, normalized, x.SessionCvd,
            MeanDelta(bars, SlopeShortBars), MeanDelta(bars, SlopeLongBars),
            recent.Count == 0 ? 0 : recent.Max(b => FootprintBar.LongestStack(b.BuyImbalances(s.ImbalanceRatio, s.ImbalanceMinVolume), x.TickSize)),
            recent.Count == 0 ? 0 : recent.Max(b => FootprintBar.LongestStack(b.SellImbalances(s.ImbalanceRatio, s.ImbalanceMinVolume), x.TickSize)),
            last?.UnfinishedHigh ?? false, last?.UnfinishedLow ?? false,
            DeltaFlip(x.Buckets, s), velocityZ, spike, Context(x));
    }

    public static AbsorptionResult Absorption(IReadOnlyList<Trade> midWindow, decimal medianBucketVolume, decimal level,
        bool bullish, DataSettings s, decimal tickSize)
    {
        if (midWindow.Count == 0 || medianBucketVolume <= 0) return AbsorptionResult.None;
        var buy = midWindow.Where(t => t.IsBuy).Sum(t => t.Size);
        var sell = midWindow.Where(t => !t.IsBuy).Sum(t => t.Size);
        var aggressive = bullish ? sell : buy;
        var dominance = aggressive / (buy + sell) * 100m;
        var tolerance = s.AbsorptionMaxProgressTicks * tickSize;
        var low = midWindow.Min(t => t.Price);
        var high = midWindow.Max(t => t.Price);
        var touchedAndHeld = bullish
            ? low <= level + tolerance && low >= level - tolerance
            : high >= level - tolerance && high <= level + tolerance;
        var required = s.AbsorptionVolMultiple * medianBucketVolume;
        var detected = touchedAndHeld && aggressive >= required && dominance >= s.AbsorptionDominancePercent;
        return new AbsorptionResult(detected, Math.Min(1m, aggressive / required), aggressive, dominance);
    }

    private static int DeltaFlip(IReadOnlyList<Bucket> buckets, DataSettings s)
    {
        if (buckets.Count < 2) return 0;
        var current = buckets[^1];
        var previous = buckets.Take(buckets.Count - 1).LastOrDefault(b => b.Delta != 0);
        if (previous is null || Math.Sign(current.Delta) != -Math.Sign(previous.Delta)) return 0;
        var median = Stats.Median(buckets.Select(b => b.Volume).ToList());
        return median > 0 && Math.Abs(current.Delta) >= s.DeltaFlipMinMultiple * median ? Math.Sign(current.Delta) : 0;
    }

    private static (double Z, bool Spike) Velocity(FeatureInputs x)
    {
        var (mean, sd) = Stats.MeanStd(x.Buckets.Select(b => (double)b.Trades).ToList());
        var z = sd > 0 ? (x.TapeMidCount - mean) / sd : 0;
        return (z, z > x.Settings.VelocityZLimit);
    }

    private static FlowContext Context(FeatureInputs x)
    {
        var window = Tail(x.Bars1m, ContextBars);
        if (window.Count < ContextBars || x.LastPrice is not { } price) return FlowContext.None;
        var change = price - window[0].Open;
        var delta = window.Sum(b => b.Delta);
        if (change == 0 || delta == 0) return FlowContext.None;
        if (Math.Sign(change) != Math.Sign(delta)) return delta > 0 ? FlowContext.ResponsiveBuy : FlowContext.ResponsiveSell;
        if (x.Developing is not { } va) return FlowContext.None;
        if (change > 0 && price > va.Vah) return FlowContext.InitiativeBuy;
        if (change < 0 && price < va.Val) return FlowContext.InitiativeSell;
        return FlowContext.None;
    }

    private static decimal MeanDelta(IReadOnlyList<FootprintBar> bars, int n)
    {
        var tail = Tail(bars, n);
        return tail.Count == 0 ? 0m : tail.Sum(b => b.Delta) / tail.Count;
    }

    private static IReadOnlyList<T> Tail<T>(IReadOnlyList<T> items, int n) =>
        items.Skip(Math.Max(0, items.Count - n)).ToList();
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS. Hand checks:
  - **DeltaFlip:** the median of {10, 10} is 10, so the minimum is 5. A delta of 6 passes; 4 does not.
  - **Velocity:** counts {4, 6} give mean 5 and population sd 1, so a tape count of 9 gives z = 4.
  - **Context:** the first open is 100 and the last price is 105, so the change is +5. Delta is +8 per bar (+40 total). 105 > VAH 102, so the result is InitiativeBuy.
- [ ] **Step 5:** `git add OrderFlow/OrderFlowFeatures.cs tests/NQOrderFlow.Tests/OrderFlowFeaturesTests.cs && git commit -m "feat: order-flow snapshot features and absorption check"`

---

### Task 7: MarketDataPipeline + BoundedQueue

**Files:** Create `Data/MarketDataPipeline.cs`, `Telemetry/BoundedQueue.cs`, `tests/NQOrderFlow.Tests/MarketDataPipelineTests.cs`, `tests/NQOrderFlow.Tests/BoundedQueueTests.cs`

**Interfaces:**
- **Consumes:** everything from Tasks 1–6, plus `SessionClock`.
- **Produces:**
  - `sealed record DataHealth(bool WarmupComplete, double LastTickAgeSeconds, double FallbackPercent, long Accepted, long Dropped, bool FallbackWarning, bool IsHealthy)`
  - `MarketDataPipeline(DataSettings, SessionClock, decimal tickSize)`. Every public member takes one lock:
    - `void OnQuote(RawQuote)`
    - `FootprintBar? OnTrade(RawTrade)`: returns the closed 1m bar, or null
    - `FootprintBar? OnTimer(DateTime nowUtc)`: closes elapsed bars, advances buckets and trims tapes. Must **not** be called during backfill.
    - `OrderFlowSnapshot Snapshot(DateTime nowUtc)`
    - `SessionLevels Levels()`
    - `AbsorptionResult Absorption(decimal level, bool bullish)`
    - `DataHealth Health(DateTime nowUtc)`
    - `void MarkWarmupComplete()`
    - `DateTime? LastTradeUtc`
    - `IReadOnlyList<FootprintBar> Bars1m()` and `Bars5m()`: arrays of closed bars
  - `BoundedQueue<T>(int capacity)` with `bool TryEnqueue(T)`, `IReadOnlyList<T> Drain(int max)`, `long Dropped` and `int Count`. Lock-free (ConcurrentQueue plus Interlocked).
- Session CVD resets when `SessionClock.TradingDate` changes, which happens at 18:00 ET.

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class MarketDataPipelineTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);   // 10:00 EDT
    private static MarketDataPipeline New() => new(new DataSettings(), new SessionClock(SessionSettings.Default()), 0.25m);
    private static RawTrade Raw(double s, decimal price, decimal size = 1, Aggressor side = Aggressor.Buy)
        => new(T0.AddSeconds(s), price, size, side);

    [Fact]
    public void Trades_FlowIntoBarsCvdAndProfile()
    {
        var p = New();
        p.OnTrade(Raw(0, 100m, 3));
        p.OnTrade(Raw(10, 100.25m, 1, Aggressor.Sell));
        var closed = p.OnTrade(Raw(60, 100m, 2));
        Assert.Equal(2m, closed!.Delta);
        Assert.Equal(4m, p.Snapshot(T0.AddSeconds(60)).SessionCvd);
        Assert.NotNull(p.Levels().Vwap);
        Assert.Single(p.Bars1m());
    }

    [Fact]
    public void Cvd_ResetsAtTradingDayRoll()
    {
        var p = New();
        p.OnTrade(new RawTrade(new DateTime(2026, 10, 29, 21, 59, 0, DateTimeKind.Utc), 100m, 5, Aggressor.Buy));  // 17:59 ET
        p.OnTrade(new RawTrade(new DateTime(2026, 10, 29, 22, 0, 0, DateTimeKind.Utc), 100m, 2, Aggressor.Sell));  // 18:00 ET
        Assert.Equal(-2m, p.Snapshot(new DateTime(2026, 10, 29, 22, 0, 1, DateTimeKind.Utc)).SessionCvd);
    }

    [Fact]
    public void Health_NeedsWarmupAndFreshTicks()
    {
        var p = New();
        p.OnTrade(Raw(0, 100m));
        Assert.False(p.Health(T0.AddSeconds(1)).IsHealthy);         // not warm
        p.MarkWarmupComplete();
        Assert.True(p.Health(T0.AddSeconds(3)).IsHealthy);
        Assert.False(p.Health(T0.AddSeconds(3.5)).IsHealthy);       // stale > 3 s
        Assert.False(New().Health(T0).IsHealthy);                   // never ticked
    }

    [Fact]
    public void Health_FlagsHighFallbackRate()
    {
        var p = New();
        p.OnTrade(Raw(0, 100m, side: Aggressor.Unknown));
        Assert.True(p.Health(T0).FallbackWarning);
    }

    [Fact]
    public void OnTimer_ClosesQuietMinute()
    {
        var p = New();
        p.OnTrade(Raw(5, 100m));
        Assert.NotNull(p.OnTimer(T0.AddSeconds(61)));
    }

    [Fact]
    public void ReplayedOlderTick_IsNotDoubleCounted()
    {
        var p = New();
        p.OnTrade(Raw(10, 100m, 4));
        p.OnTrade(Raw(9, 100m, 4));                                 // older replay → dropped
        Assert.Equal(4m, p.Snapshot(T0.AddSeconds(10)).SessionCvd);
    }
}
```
```csharp
using QT_MNQ_Orderflow_Algo.Telemetry;
using Xunit;

public sealed class BoundedQueueTests
{
    [Fact]
    public void DropsWhenFull_AndDrainsInOrder()
    {
        var q = new BoundedQueue<int>(2);
        Assert.True(q.TryEnqueue(1));
        Assert.True(q.TryEnqueue(2));
        Assert.False(q.TryEnqueue(3));
        Assert.Equal(1, q.Dropped);
        Assert.Equal(new[] { 1 }, q.Drain(1));
        Assert.True(q.TryEnqueue(4));
        Assert.Equal(new[] { 2, 4 }, q.Drain(10));
        Assert.Equal(0, q.Count);
    }
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3: Implement:**
```csharp
using System.Collections.Concurrent;

namespace QT_MNQ_Orderflow_Algo.Telemetry;

/// <summary>Lock-free bounded queue: producers never block; items beyond capacity are counted and dropped.</summary>
public sealed class BoundedQueue<T>
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly int _capacity;
    private int _count;
    private long _dropped;

    public BoundedQueue(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    public int Count => Volatile.Read(ref _count);

    public bool TryEnqueue(T item)
    {
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);
            return false;
        }
        _items.Enqueue(item);
        return true;
    }

    public IReadOnlyList<T> Drain(int max)
    {
        var drained = new List<T>();
        while (drained.Count < max && _items.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            drained.Add(item);
        }
        return drained;
    }
}
```
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.OrderFlow;

namespace QT_MNQ_Orderflow_Algo.Data;

public sealed record DataHealth(
    bool WarmupComplete, double LastTickAgeSeconds, double FallbackPercent,
    long Accepted, long Dropped, bool FallbackWarning, bool IsHealthy);

/// <summary>
/// Single owner of all market-data accumulators. Every public member takes one lock; callers get snapshots only.
/// OnTimer must not run during backfill: historic ticks would land in buckets advanced to wall-clock time.
/// </summary>
public sealed class MarketDataPipeline
{
    private readonly object _gate = new();
    private readonly DataSettings _s;
    private readonly SessionClock _clock;
    private readonly TickNormalizer _normalizer;
    private readonly FootprintBuilder _bars1m, _bars5m;
    private readonly TapeWindow _tapeShort, _tapeMid, _tapeLong;
    private readonly BucketSeries _buckets;
    private readonly SessionProfile _profile;
    private decimal _sessionCvd;
    private DateOnly? _cvdDate;
    private DateTime? _lastTradeUtc;
    private decimal? _lastPrice;
    private bool _warm;

    public MarketDataPipeline(DataSettings settings, SessionClock clock, decimal tickSize)
    {
        _s = settings;
        _clock = clock;
        _normalizer = new TickNormalizer(settings, tickSize);
        _bars1m = new FootprintBuilder(TimeSpan.FromMinutes(1), tickSize, settings.BarHistoryCapacity);
        _bars5m = new FootprintBuilder(TimeSpan.FromMinutes(5), tickSize, settings.BarHistoryCapacity);
        _tapeShort = new TapeWindow(TimeSpan.FromSeconds(settings.TapeShortSeconds));
        _tapeMid = new TapeWindow(TimeSpan.FromSeconds(settings.TapeMidSeconds));
        _tapeLong = new TapeWindow(TimeSpan.FromSeconds(settings.TapeLongSeconds));
        _buckets = new BucketSeries(TimeSpan.FromSeconds(settings.TapeMidSeconds),
            Math.Max(1, settings.BaselineMinutes * 60 / settings.TapeMidSeconds));
        _profile = new SessionProfile(clock, settings);
    }

    public DateTime? LastTradeUtc { get { lock (_gate) return _lastTradeUtc; } }

    public void OnQuote(RawQuote q) { lock (_gate) _normalizer.OnQuote(q); }

    public FootprintBar? OnTrade(RawTrade raw)
    {
        lock (_gate)
        {
            if (_normalizer.OnTrade(raw) is not { } t) return null;
            var tradingDate = _clock.TradingDate(t.Utc);
            if (_cvdDate != tradingDate) { _cvdDate = tradingDate; _sessionCvd = 0m; }
            _sessionCvd += t.IsBuy ? t.Size : -t.Size;
            _lastTradeUtc = t.Utc;
            _lastPrice = t.Price;
            _tapeShort.Add(t);
            _tapeMid.Add(t);
            _tapeLong.Add(t);
            _buckets.Add(t);
            _profile.OnTrade(t);
            _bars5m.OnTrade(t);
            return _bars1m.OnTrade(t);
        }
    }

    public FootprintBar? OnTimer(DateTime nowUtc)
    {
        lock (_gate)
        {
            _bars5m.CloseIfElapsed(nowUtc);
            _buckets.AdvanceTo(nowUtc);
            _tapeShort.Trim(nowUtc);
            _tapeMid.Trim(nowUtc);
            _tapeLong.Trim(nowUtc);
            return _bars1m.CloseIfElapsed(nowUtc);
        }
    }

    public OrderFlowSnapshot Snapshot(DateTime nowUtc)
    {
        lock (_gate)
        {
            return OrderFlowFeatures.Compute(new FeatureInputs(nowUtc, _bars1m.Closed, _buckets.Closed, _tapeMid.Count,
                _sessionCvd, _lastPrice, _profile.Snapshot().Developing, _s, _normalizer.TickSize));
        }
    }

    public SessionLevels Levels() { lock (_gate) return _profile.Snapshot(); }

    public AbsorptionResult Absorption(decimal level, bool bullish)
    {
        lock (_gate)
            return OrderFlowFeatures.Absorption(_tapeMid.Trades, _buckets.MedianVolume, level, bullish, _s, _normalizer.TickSize);
    }

    public DataHealth Health(DateTime nowUtc)
    {
        lock (_gate)
        {
            var age = _lastTradeUtc is { } last ? (nowUtc - last).TotalSeconds : double.PositiveInfinity;
            var fallbackWarning = _normalizer.FallbackPercent > _s.FallbackWarnPercent;
            return new DataHealth(_warm, age, _normalizer.FallbackPercent, _normalizer.Accepted, _normalizer.Dropped,
                fallbackWarning, _warm && age <= _s.MaxTickAgeSeconds);
        }
    }

    public void MarkWarmupComplete() { lock (_gate) _warm = true; }
    public IReadOnlyList<FootprintBar> Bars1m() { lock (_gate) return _bars1m.Closed; }
    public IReadOnlyList<FootprintBar> Bars5m() { lock (_gate) return _bars5m.Closed; }
}
```
- [ ] **Step 4:** Run `dotnet test tests/NQOrderFlow.Tests`. Expected: all pass.
- [ ] **Step 5:** `git add Data/MarketDataPipeline.cs Telemetry/BoundedQueue.cs tests/NQOrderFlow.Tests/MarketDataPipelineTests.cs tests/NQOrderFlow.Tests/BoundedQueueTests.cs && git commit -m "feat: locked market-data pipeline with health and bounded log queue"`

---

### Task 8: Quantower wiring — live feed, backfill, bar log, inputs, docs

**Files:**
- Create: `QuantowerMarketData.cs`
- Modify: `NQOrderFlowStrategy.cs`, `Config/SettingsValidator.cs`, `tests/NQOrderFlow.Tests/SettingsValidatorTests.cs`, `docs/API_NOTES.md`, `docs/ASSUMPTIONS.md`

**Interfaces:**
- **Consumes:** `MarketDataPipeline`, `BoundedQueue<T>`, `DataSettings`, `SessionClock.PreviousTradingDate/SessionStartUtc`, `SettingsValidator`.
- **Produces:** `SettingsValidator.Validate(DataSettings) : string?`, which returns the first error, or null.

- [ ] **Step 1: Verify the API by reflection.** Load `C:\Quantower\TradingPlatform\v1.146.18\bin\TradingPlatform.BusinessLayer.dll` with `MetadataLoadContext`, the same method Phase 1 used. Do this in a throwaway project under `%TEMP%`, never in the repo. Confirm the following and record them in the `docs/API_NOTES.md` table:
  - `Last.Price`, `Last.Size`, and the aggressor member on `Last`: its name and enum values. The code assumes `AggressorFlag` with `Buy`/`Sell`.
  - `Symbol.NewQuote` and its delegate signature; `Quote.Bid`, `Quote.Ask` and `Quote.Time`.
  - The tick-history API. The code assumes:
    - `Symbol.GetHistory(HistoryRequestParameters)`, with `FromTime`, `ToTime`, `HistoryType.Last`, a 1-tick aggregation and a `CancellationToken`
    - it returns an enumerable, disposable `HistoricalData`
    - the items are `HistoryItemLast`, with `TimeLeft`, `Price`, `Volume` and an aggressor member
  - `Symbol.TickSize`.

  Wherever the real names differ, use them in the code below.
- [ ] **Step 2: Failing validator tests.** Append to `SettingsValidatorTests`:
```csharp
    [Fact]
    public void DataSettings_DefaultsAreValid() => Assert.Null(SettingsValidator.Validate(new DataSettings()));

    [Theory]
    [MemberData(nameof(BadDataSettings))]
    public void DataSettings_BadValues_AreRejected(DataSettings s) => Assert.NotNull(SettingsValidator.Validate(s));

    public static IEnumerable<object[]> BadDataSettings() => new[]
    {
        new object[] { new DataSettings(ImbalanceRatio: 0.9m) },
        new object[] { new DataSettings(StackedLevels: 0) },
        new object[] { new DataSettings(TapeShortSeconds: 20, TapeMidSeconds: 20) },
        new object[] { new DataSettings(OpeningRangeShortMinutes: 15, OpeningRangeLongMinutes: 15) },
        new object[] { new DataSettings(ValueAreaPercent: 0m) },
        new object[] { new DataSettings(AbsorptionDominancePercent: 101m) },
        new object[] { new DataSettings(BaselineMinutes: 0) },
        new object[] { new DataSettings() with { RthOpenEt = new(16, 0, 0) } },
    };
```
- [ ] **Step 3: Implement** `SettingsValidator.Validate(DataSettings s)`:
```csharp
    public static string? Validate(DataSettings s)
    {
        if (s.BadTickMaxTicks < 1 || s.BadTickConfirmCount < 1) return "Bad-tick settings must be >= 1";
        if (s.MaxTickAgeSeconds < 1 || s.BarHistoryCapacity < 20) return "MaxTickAgeSeconds >= 1 and BarHistoryCapacity >= 20 required";
        if (s.ImbalanceRatio < 1m || s.ImbalanceMinVolume < 0m || s.StackedLevels < 1) return "Imbalance settings out of range";
        if (!(0 < s.TapeShortSeconds && s.TapeShortSeconds < s.TapeMidSeconds && s.TapeMidSeconds < s.TapeLongSeconds))
            return "Tape windows must satisfy 0 < short < mid < long";
        if (s.BaselineMinutes < 1 || s.MedianBarLookback < 1) return "Baseline and lookback must be >= 1";
        if (s.AbsorptionVolMultiple <= 0m || s.AbsorptionMaxProgressTicks < 0 || s.DeltaFlipMinMultiple < 0m) return "Absorption/flip settings out of range";
        if (s.AbsorptionDominancePercent is < 0m or > 100m || s.ValueAreaPercent is <= 0m or > 100m) return "Percent settings out of range";
        if (s.FallbackWarnPercent is < 0 or > 100 || s.VelocityZLimit <= 0) return "Fallback/velocity settings out of range";
        if (!(0 < s.OpeningRangeShortMinutes && s.OpeningRangeShortMinutes < s.OpeningRangeLongMinutes)) return "Opening ranges must satisfy 0 < short < long";
        if (s.RthOpenEt >= s.RthCloseEt) return "RthOpenEt must be before RthCloseEt";
        return null;
    }
```
Run `dotnet test tests/NQOrderFlow.Tests --filter SettingsValidatorTests`. Expected: PASS.
- [ ] **Step 4: Create `QuantowerMarketData.cs`.** Adjust the member names to match the Step 1 findings.
```csharp
using QT_MNQ_Orderflow_Algo.Data;
using TradingPlatform.BusinessLayer;

namespace QT_MNQ_Orderflow_Algo;

/// <summary>The only Quantower-facing market-data code: maps feed objects to pure-core types and loads tick history.</summary>
internal static class QuantowerMarketData
{
    private static readonly TimeSpan BackfillChunk = TimeSpan.FromHours(1);

    public static RawTrade ToRawTrade(Last last) =>
        new(AsUtc(last.Time), (decimal)last.Price, (decimal)last.Size, Map(last.AggressorFlag));

    public static RawQuote ToRawQuote(Quote quote) => new(AsUtc(quote.Time), (decimal)quote.Bid, (decimal)quote.Ask);

    /// <summary>Feeds tick history oldest-first, one hour at a time to bound memory. Returns the last tick time fed.</summary>
    public static DateTime? Backfill(Symbol symbol, DateTime fromUtc, DateTime toUtc, Action<RawTrade> sink, CancellationToken ct)
    {
        DateTime? last = null;
        for (var start = fromUtc; start < toUtc && !ct.IsCancellationRequested; start += BackfillChunk)
        {
            var end = start + BackfillChunk < toUtc ? start + BackfillChunk : toUtc;
            using var history = symbol.GetHistory(new HistoryRequestParameters
            {
                Symbol = symbol,
                FromTime = start,
                ToTime = end,
                Aggregation = new HistoryAggregationTick(1),
                HistoryType = HistoryType.Last,
                CancellationToken = ct,
            });
            foreach (var item in history)
            {
                if (item is not HistoryItemLast h) continue;
                var trade = new RawTrade(AsUtc(h.TimeLeft), (decimal)h.Price, (decimal)h.Volume, Map(h.AggressorFlag));
                sink(trade);
                last = trade.Utc;
            }
        }
        return last;
    }

    private static Aggressor Map(AggressorFlag flag) => flag switch
    {
        AggressorFlag.Buy => Aggressor.Buy,
        AggressorFlag.Sell => Aggressor.Sell,
        _ => Aggressor.Unknown,
    };

    private static DateTime AsUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
```
- [ ] **Step 5: Wire it into `NQOrderFlowStrategy.cs`:**
  1. **Inputs.** Add one `[InputParameter]` per `DataSettings` field, plus `RthOpenEt` and `RthCloseEt` as validated HH:mm strings.
     - Sort indexes 400–499; defaults equal to the record defaults.
     - Add `BuildDataSettings()`, which maps every input into the record.
     - In `RunCore`, after the existing validation, call `SettingsValidator.Validate(dataSettings)`. If it returns an error, log it and call `Stop()`.
  2. **Fields:**
```csharp
    private const int LogDrainPerTick = 50;
    private const int LiveBufferCapacity = 200_000;
    private const int LogQueueCapacity = 1_000;
    private MarketDataPipeline? _pipeline;
    private readonly object _feedGate = new();
    private readonly Queue<RawTrade> _liveBuffer = new();
    private bool _backfilling;
    private bool _liveOverflow;
    private int _quoteErrors;
    private BoundedQueue<string> _logQueue = new(LogQueueCapacity);
    private CancellationTokenSource? _backfillCts;
```
  At the top of `OnRun`, next to the existing resets, reset these:
  - `_pipeline = null`
  - `_backfilling = false`
  - `_liveOverflow = false`
  - `_quoteErrors = 0`
  - `_liveBuffer.Clear()`, under `_feedGate`
  - `_logQueue = new(LogQueueCapacity)`
  3. **In `RunCore`, after the guard is set up.** This replaces the Phase 1 `symbol.NewLast += OnNewLast` line:
```csharp
        _pipeline = new MarketDataPipeline(dataSettings, _clock, (decimal)symbol.TickSize);
        _backfilling = true;
        symbol.NewLast += OnNewLast;
        symbol.NewQuote += OnNewQuote;
        var now = NowUtc();
        var fromUtc = _clock.SessionStartUtc(_clock.PreviousTradingDate(_clock.TradingDate(now)));
        _backfillCts = new CancellationTokenSource();
        var token = _backfillCts.Token;
        var sym = symbol;
        _ = Task.Run(() => RunBackfill(sym, fromUtc, now, token), token);
```
  4. **Backfill and hand-off:**
```csharp
    private void RunBackfill(Symbol sym, DateTime fromUtc, DateTime toUtc, CancellationToken token)
    {
        try
        {
            var pipeline = _pipeline!;
            var lastBackfilled = QuantowerMarketData.Backfill(sym, fromUtc, toUtc, raw => pipeline.OnTrade(raw), token);
            lock (_feedGate)
            {
                var replayed = 0;
                while (_liveBuffer.Count > 0)
                {
                    var raw = _liveBuffer.Dequeue();
                    if (lastBackfilled is null || raw.Utc > lastBackfilled.Value) { pipeline.OnTrade(raw); replayed++; }
                }
                _backfilling = false;
                if (_liveOverflow)
                {
                    _logQueue.TryEnqueue("Live buffer overflowed during backfill: data stays unhealthy. Restart the strategy.");
                    return;
                }
                pipeline.MarkWarmupComplete();
                _logQueue.TryEnqueue($"Backfill done {_clock!.Stamp(fromUtc)} → {_clock.Stamp(toUtc)}; last {lastBackfilled:O}; replayed {replayed} live ticks");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logQueue.TryEnqueue($"Backfill FAILED: {ex.Message}. Data stays unhealthy (warmup incomplete).");
            lock (_feedGate) { _backfilling = false; }
        }
    }

    private void BufferLive(RawTrade raw)
    {
        if (_liveBuffer.Count < LiveBufferCapacity) _liveBuffer.Enqueue(raw);
        else _liveOverflow = true;
    }
```
  5. **Handlers.** Replace `OnNewLast`, and add `OnNewQuote`. Each goes through `Guarded` with its own counter:
```csharp
    private void OnNewLast(Symbol s, Last last) => Guarded(() =>
    {
        var raw = QuantowerMarketData.ToRawTrade(last);
        FootprintBar? closed;
        lock (_feedGate)
        {
            if (_backfilling) { BufferLive(raw); return; }
            closed = _pipeline?.OnTrade(raw);
        }
        if (closed is not null) EnqueueBarLog(closed);
    }, ref _tickErrors);

    private void OnNewQuote(Symbol s, Quote quote) => Guarded(() => _pipeline?.OnQuote(QuantowerMarketData.ToRawQuote(quote)), ref _quoteErrors);
```
  Keep the existing log of `Last.Time` Kind on the first tick. Tick age now comes from `_pipeline.Health(now).LastTickAgeSeconds`, which replaces the Phase 1 `_lastTickUtcTicks` tracking.
  6. **`TimerTick`:**
     - When `!_backfilling` (read under `_feedGate`), call `_pipeline.OnTimer(now)` and pass any closed bar to `EnqueueBarLog`.
     - Every tick, drain up to `LogDrainPerTick` lines from `_logQueue` into `Log(..., StrategyLoggingLevel.Info)`.
     - On a health transition (healthy ↔ unhealthy, or the fallback warning raised), log once at Trading level.
  7. **`EnqueueBarLog(FootprintBar b)`** enqueues one line per closed 1m bar, so the owner can compare it against Quantower's footprint and volume profile:
```csharp
    private void EnqueueBarLog(FootprintBar b)
    {
        var lv = _pipeline!.Levels();
        var s = _pipeline.Snapshot(b.StartUtc.AddMinutes(1));
        _logQueue.TryEnqueue(
            $"BAR {_clock!.ToEt(b.StartUtc):HH:mm} ET O {b.Open} H {b.High} L {b.Low} C {b.Close} V {b.TotalVolume} " +
            $"Δ {b.Delta} (max {b.MaxDelta} min {b.MinDelta}) POC {b.Poc} stackB {s.StackedBuyLevels} stackS {s.StackedSellLevels} " +
            $"| VWAP {lv.Vwap:F2} σ {lv.VwapStdDev:F2} dPOC {lv.Developing?.Poc} VAH {lv.Developing?.Vah} VAL {lv.Developing?.Val} " +
            $"| CVD {s.SessionCvd} flip {s.DeltaFlip} velZ {s.VelocityZ:F1} ctx {s.Context}");
    }
```
  8. **`OnStop`:**
     - Cancel and dispose `_backfillCts`.
     - Unsubscribe both events: `symbol.NewLast -= OnNewLast` and `symbol.NewQuote -= OnNewQuote`.
     - Drain the remaining `_logQueue` lines into `Log`.
     - Keep the Phase 1 order: stop the timer first, then save state.
  9. **Gauges.** Add these to `OnInitializeMetrics`, wrapped the same way as the existing ones:
     - `data_warm` (0/1)
     - `data_healthy` (0/1)
     - `last_tick_age_s`
     - `fallback_pct`
     - `ticks_accepted`
     - `ticks_dropped`
     - `session_cvd`
     - `vwap`
     - `last_bar_delta`
     - `log_dropped`: `_logQueue.Dropped`
- [ ] **Step 6: Docs.**
  - In `docs/API_NOTES.md`, add one row per member verified in Step 1, plus a note on the backfill chunking.
  - In `docs/ASSUMPTIONS.md`, add a "Phase 2 data" section listing these rulings:
    - RTH-anchored VWAP and profile (09:30–16:00 ET).
    - Prior-day high, low and close and the prior value area come from RTH.
    - Overnight range runs 18:00–09:30 ET.
    - The value area grows one traded level at a time.
    - Identical prints are kept (`DropIdenticalPrints` = false), because legitimate same-millisecond prints exist.
    - The bad-tick filter uses a confirm count.
    - DeltaFlip uses closed 20s buckets.
    - Exchange holidays are not modeled in `PreviousTradingDate`. On the day after a holiday, "prior day" levels come from the holiday-shortened or older session.
    - Exhaustion, DeltaDivergence and TrappedTraders are deferred to Phase 3.
    - DOM tracking is deferred (feature flag in Phase 4+).
  - Add these items to the "Owner smoke test" checklist:
    - [ ] After start, the log shows "Backfill done … replayed N live ticks" and `data_warm` becomes 1.
    - [ ] Pick three closed 1m bars in the RTH session. Compare `V`, `Δ`, `POC`, `H` and `L` from the `BAR` log with Quantower's Cluster/Footprint chart for the same bars. Expect an exact volume match; delta should match within the fallback rate.
    - [ ] Compare `VWAP` and `dPOC/VAH/VAL` with Quantower's VWAP and Volume Profile indicators set to an RTH session. Expect a close match; value-area differences can come from the algorithm.
    - [ ] `fallback_pct` stays below 5% on Rithmic. If it is higher, the aggressor mapping is wrong.
    - [ ] Disconnect the network for 10 s. `data_healthy` goes to 0, then back to 1 when ticks resume.
- [ ] **Step 7: Build and verify.**
  - `dotnet build "QT MNQ Orderflow Algo.csproj" -c Release`. Expected: 0 warnings, 0 errors.
  - `dotnet test tests/NQOrderFlow.Tests`. Expected: all pass.
  - `git grep -n "PlaceOrder\|CancelOrder" -- "*.cs"`. Expected: nothing.
- [ ] **Step 8:** `git add QuantowerMarketData.cs NQOrderFlowStrategy.cs Config/SettingsValidator.cs tests/NQOrderFlow.Tests/SettingsValidatorTests.cs docs/API_NOTES.md docs/ASSUMPTIONS.md && git commit -m "feat: Phase 2 live feed, tick backfill, bar debug log, data inputs and gauges"`

---

## Verification (end of Phase 2)

1. `dotnet test tests/NQOrderFlow.Tests`: all green. The Phase 1 suite of 113 tests is unchanged except for Task 0's tests.
2. `dotnet build "QT MNQ Orderflow Algo.csproj" -c Release`: 0 warnings, 0 errors.
3. `git grep -n "PlaceOrder\|CancelOrder" -- "*.cs"`: empty.
4. The owner smoke items from Task 8 Step 6 pass in Quantower on a sim account with live Rithmic data during RTH.

## Deferred to later plans
- **Phase 3:**
  - SwingEngine: pivots, BOS/CHoCH, bias.
  - LevelMap: PDH/PDL/ONH/ONL/OR/VWAP bands/prior value, clustered and scored, built from `SessionLevels`.
  - Regime classifier.
  - Exhaustion, DeltaDivergence and TrappedTraders, on top of pivots.
- **Phase 4:** setups, Scorer, proposal journal (paper mode), DomTracker (feature-flagged).
- **Phase 5:** execution, trade management, kill switches, EOD wiring.
- **Phase 6:** validation, VPS setup, tuning guide.
