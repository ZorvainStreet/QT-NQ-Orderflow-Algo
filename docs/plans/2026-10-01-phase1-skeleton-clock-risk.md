# NQ Order-Flow Strategy — Phase 1 (Skeleton, Clock, Risk Layer) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **On approval:** first copy this plan to `docs/plans/2026-10-01-phase1-skeleton-clock-risk.md` and save the owner's JSON spec as `docs/plans/SPEC_NQ_OrderFlow_LucidFlex25K.json`, then commit both.

**Goal:** A Quantower Strategy that compiles, runs, places **no orders**, and correctly shows the ET/IST clock, session windows, Lucid Flex 25K evaluation compliance state, risk tier, and persisted risk memory in Strategy Runner metrics. All of this is backed by green xUnit tests.

**Architecture:** All logic is "pure core": Quantower-free C# in `Config/`, `Compliance/`, `Persistence/`, compiled into the strategy assembly and also *source-linked* into an xUnit project, so tests never load `TradingPlatform.BusinessLayer.dll`. Only `NQOrderFlowStrategy.cs` touches the Quantower API. The signal layer proposes and `LucidRiskGuard` decides. Phase 1 has no signal layer, so the guard is exercised by tests and reports its state via metrics.

**Tech Stack:** C# latest, net10.0 (matches the existing template csproj), Quantower v1.146.18 `TradingPlatform.BusinessLayer`, xUnit, System.Text.Json, Windows TZ IDs `Eastern Standard Time` / `India Standard Time`.

**Spec:** `docs/plans/SPEC_NQ_OrderFlow_LucidFlex25K.json` (owner's JSON prompt v1.0). Phases 2–6 get separate plans after this one is reviewed.

## Global Constraints

- Allowed roots come from input `AllowedRoots` (CSV, default `"NQ"`). Exact root match, so `MNQ` ≠ `NQ`. Any other root is rejected at OnRun with a clear log line and `Stop()`.
- Quantity is the locked constant `1` (`LucidRiskGuard.LockedQuantity`). It is not an input. Input `MaxContractsAllowed` defaults to 1.
- All trading time logic runs in US Eastern from UTC sources (`Core.Instance.TimeUtils.DateTimeUtcNow` or tick time). Never `DateTime.Now`.
- No hardcoded thresholds in logic. Every tunable is a field of a settings record in `Config/` and is fed by an `[InputParameter]`.
- Money is `decimal`. State updates are immutable record replacement.
- Lucid numbers the owner must confirm are marked `// VERIFY` and listed in `docs/ASSUMPTIONS.md`.
- No exceptions escape event or timer handlers. N consecutive exceptions trip safe mode.
- Corrupt or unreadable state fails **closed** (halted). It never silently resets risk memory.
- Every task ends with `dotnet build` (0 errors, no fixable warnings) and `dotnet test` green.

## Review Focus

1. **DST weeks:** 09:35 ET = 13:35 UTC on Thu 2026-10-29 (EDT) and 14:35 UTC on Thu 2026-11-05 (EST). Same for March 2026. Tested in Task 2.
2. **Trading-day rollover:** PnL buckets by futures trading date (rolls at 18:00 ET). 17:00 ET Thu is still Thursday, and 18:30 ET starts the Friday bucket. Tested in Tasks 2 and 7.
3. **Corrupt or missing `state.json`:** missing means fresh. Corrupt means halted with reason, and the file is preserved as `.corrupt-<ts>`. Tested in Task 8.
4. **First winning day vs. consistency:** a naive `today/total` halts after the first win. The denominator is `max(totalProfit, ProfitTarget)`. Tested in Task 6.
5. **Root confusion:** `MNQZ6` must not pass `NQ`, and `NQZ6` must not pass `MNQ`. Tested in Task 1.

---

## File Structure

```
QT MNQ Orderflow Algo.csproj        modify: Nullable, exclude tests/**
QT MNQ Orderflow Algo.slnx          modify: add test project
NQOrderFlowStrategy.cs              replaces QT_MNQ_Orderflow_Algo.cs; ONLY Quantower-dependent file
Config/StrategySettings.cs          AccountRules, SessionWindow, SessionSettings, RiskSettings
Compliance/ILogSink.cs              logging abstraction for the pure core
Compliance/SymbolRules.cs           root extraction + allow-list
Compliance/SessionClock.cs          ET/IST, windows, weekday, flatten, trading date
Compliance/NewsBlackout.cs          recurring + CSV blackouts
Compliance/RiskGovernor.cs          tiers, per-trade risk check
Compliance/DailyRiskState.cs        daily limits, streaks, cooldowns, giveback
Compliance/EvaluationTracker.cs     EOD MLL ratchet, consistency, target, microscalp
Compliance/LucidRiskGuard.cs        aggregate veto, flags, factory
Persistence/PersistedState.cs       DTOs
Persistence/StateStore.cs           atomic JSON load/save
tests/NQOrderFlow.Tests/            csproj + one *Tests.cs per core file
docs/API_NOTES.md, docs/ASSUMPTIONS.md
```

---

### Task 1: Scaffolding, settings, symbol rules

**Files:**
- Modify: `QT MNQ Orderflow Algo.csproj`, `QT MNQ Orderflow Algo.slnx`
- Create: `Config/StrategySettings.cs`, `Compliance/ILogSink.cs`, `Compliance/SymbolRules.cs`, `tests/NQOrderFlow.Tests/NQOrderFlow.Tests.csproj`, `tests/NQOrderFlow.Tests/SymbolRulesTests.cs`

**Interfaces — Produces:**
- `SymbolRules.ExtractRoot(string) : string`
- `SymbolRules.ParseAllowed(string csv) : IReadOnlySet<string>`
- `SymbolRules.IsAllowed(string root, IReadOnlySet<string>) : bool`
- `ILogSink { Info, Trading, Error }` and `NullLogSink`
- The settings records below

- [ ] **Step 1:** In the strategy csproj's first `<PropertyGroup>` add `<Nullable>enable</Nullable>`. Add:
```xml
<ItemGroup>
  <Compile Remove="tests/**" />
  <None Remove="tests/**" />
</ItemGroup>
```
- [ ] **Step 2:** Create the test project and add `<Project Path="tests/NQOrderFlow.Tests/NQOrderFlow.Tests.csproj" />` to the `.slnx`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Source-link the pure core; never reference TradingPlatform.BusinessLayer here -->
    <Compile Include="..\..\Config\**\*.cs" LinkBase="Core\Config" />
    <Compile Include="..\..\Compliance\**\*.cs" LinkBase="Core\Compliance" />
    <Compile Include="..\..\Persistence\**\*.cs" LinkBase="Core\Persistence" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>
</Project>
```
Core files must declare their own `using`s, because the strategy csproj has no ImplicitUsings. Add `<ImplicitUsings>enable</ImplicitUsings>` to the strategy csproj as well so both compile identically.
- [ ] **Step 3:** `Config/StrategySettings.cs`:
```csharp
namespace QT_MNQ_Orderflow_Algo.Config;

/// <summary>Lucid account rules. Every value is an input; VERIFY on the Lucid dashboard.</summary>
public sealed record AccountRules(
    decimal InitialBalance = 25000m,
    decimal MllDistanceUsd = 1000m,
    decimal MllLockFloorUsd = 25000m,       // VERIFY: level at which the trailing floor stops rising
    decimal ProfitTargetUsd = 1250m,
    decimal TargetBufferUsd = 25m,
    decimal ConsistencyCapPercent = 50m,
    decimal ConsistencyEarlyStopPercent = 45m,
    int MinTradingDays = 2,
    int MaxContractsAllowed = 1);           // VERIFY

public sealed record SessionWindow(string Name, TimeSpan StartEt, TimeSpan EndEt, bool Enabled, int ScoreAdd);

public sealed record SessionSettings(
    IReadOnlyList<SessionWindow> Windows,
    TimeSpan FlattenTimeEt,
    TimeSpan LucidDeadlineEt,
    TimeSpan TradingDayRollEt,               // 18:00 ET futures session roll
    int NewsBeforeMin,
    int NewsAfterMin)
{
    public static SessionSettings Default() => new(
        new[]
        {
            new SessionWindow("NY_AM_KILLZONE", new(9, 35, 0), new(11, 45, 0), true, 0),
            new SessionWindow("NY_PM", new(13, 30, 0), new(15, 30, 0), true, 5),
            new SessionWindow("LONDON_OPEN", new(3, 0, 0), new(5, 0, 0), false, 0),
        },
        new(15, 55, 0), new(16, 45, 0), new(18, 0, 0), 2, 3);
}

public sealed record RiskSettings(
    decimal TierAMinHeadroom = 900m, decimal TierBMinHeadroom = 600m, decimal HaltHeadroomUsd = 350m,
    decimal MaxRiskTierA = 250m, decimal MaxRiskTierB = 200m, decimal MaxRiskTierC = 150m,
    decimal MaxStopPointsTierC = 7.5m,
    int ScoreAddTierB = 5, int ScoreAddTierC = 10,
    decimal MaxRiskPercentOfHeadroom = 25m,
    decimal CommissionPerSideUsd = 2.50m,    // VERIFY with Lucid/Rithmic fee schedule
    decimal SlippageUsd = 5m,
    decimal MaxDailyLossUsd = 350m,
    int MaxConsecutiveLosses = 2,
    int StreakPauseMinutes = 30,
    int MaxTradesPerDay = 4,
    decimal DailyProfitCapUsd = 450m,
    decimal GivebackArmUsd = 200m,
    decimal GivebackPercent = 40m,
    int LossCooldownMinutes = 10,
    int WinCooldownMinutes = 3,
    int MicroscalpHoldSeconds = 5,
    decimal MicroscalpWarnPercent = 20m,
    decimal MicroscalpHaltPercent = 35m,
    int SafeModeExceptionCount = 5);
```
- [ ] **Step 4:** `Compliance/ILogSink.cs`:
```csharp
namespace QT_MNQ_Orderflow_Algo.Compliance;

public interface ILogSink
{
    void Info(string message);
    void Trading(string message);
    void Error(string message);
}

public sealed class NullLogSink : ILogSink
{
    public void Info(string message) { }
    public void Trading(string message) { }
    public void Error(string message) { }
}
```
- [ ] **Step 5: Failing test** `SymbolRulesTests.cs`:
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using Xunit;

public sealed class SymbolRulesTests
{
    [Theory]
    [InlineData("NQZ6", "NQ")]
    [InlineData("NQZ26", "NQ")]
    [InlineData("MNQZ6", "MNQ")]
    [InlineData("/NQ", "NQ")]
    [InlineData("NQ", "NQ")]
    public void ExtractRoot_StripsMonthYearCode(string name, string expected)
        => Assert.Equal(expected, SymbolRules.ExtractRoot(name));

    [Fact]
    public void IsAllowed_IsExactMatch_NotPrefix()
    {
        var nqOnly = SymbolRules.ParseAllowed("NQ");
        Assert.True(SymbolRules.IsAllowed("NQ", nqOnly));
        Assert.False(SymbolRules.IsAllowed("MNQ", nqOnly));
        Assert.False(SymbolRules.IsAllowed("NQ", SymbolRules.ParseAllowed("MNQ")));
    }

    [Fact]
    public void ParseAllowed_TrimsAndUppercases()
        => Assert.True(SymbolRules.IsAllowed("MNQ", SymbolRules.ParseAllowed(" nq , mnq ")));
}
```
- [ ] **Step 6:** Run `dotnet test tests/NQOrderFlow.Tests`. Expected: FAIL, `SymbolRules` not found.
- [ ] **Step 7:** `Compliance/SymbolRules.cs`:
```csharp
using System.Text.RegularExpressions;

namespace QT_MNQ_Orderflow_Algo.Compliance;

public static class SymbolRules
{
    // Root, then futures month code (F G H J K M N Q U V X Z), then 1-2 year digits.
    private static readonly Regex Contract = new(@"^(?<root>[A-Z]+?)(?<month>[FGHJKMNQUVXZ])(?<year>\d{1,2})$", RegexOptions.Compiled);

    public static string ExtractRoot(string symbolName)
    {
        var s = symbolName.Trim().TrimStart('/').ToUpperInvariant();
        var m = Contract.Match(s);
        return m.Success ? m.Groups["root"].Value : s;
    }

    public static IReadOnlySet<string> ParseAllowed(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(r => r.ToUpperInvariant()).ToHashSet();

    public static bool IsAllowed(string root, IReadOnlySet<string> allowed) => allowed.Contains(root.ToUpperInvariant());
}
```
- [ ] **Step 8:** Run `dotnet test tests/NQOrderFlow.Tests` and `dotnet build "QT MNQ Orderflow Algo.csproj"`. Expected: PASS, clean.
- [ ] **Step 9:** `git add -A && git commit -m "chore: scaffold pure core, settings, test project, symbol rules"`

---

### Task 2: SessionClock

**Files:** Create `Compliance/SessionClock.cs`, `tests/NQOrderFlow.Tests/SessionClockTests.cs`

**Interfaces — Produces:** `new SessionClock(SessionSettings)` with these members:
- `Settings`
- `DateTime ToEt(DateTime utc)`, `DateTime ToIst(DateTime utc)`
- `DateOnly TradingDate(DateTime utc)`
- `bool IsEntryWeekday(DateTime utc)`
- `SessionWindow? ActiveWindow(DateTime utc)`
- `bool IsPastFlatten(DateTime utc)`
- `(SessionWindow Window, DateTime OpensUtc)? NextWindowOpen(DateTime utc)`
- `string Stamp(DateTime utc)`

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class SessionClockTests
{
    private readonly SessionClock _clock = new(SessionSettings.Default());
    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Theory] // Thursdays: 2026-10-29 EDT, 2026-11-05 EST, 2026-03-05 EST, 2026-03-12 EDT
    [InlineData(2026, 10, 29, 13, 35)]
    [InlineData(2026, 11, 5, 14, 35)]
    [InlineData(2026, 3, 5, 14, 35)]
    [InlineData(2026, 3, 12, 13, 35)]
    public void NyAmOpens_At0935Et_AcrossDst(int y, int mo, int d, int h, int mi)
    {
        Assert.Equal("NY_AM_KILLZONE", _clock.ActiveWindow(Utc(y, mo, d, h, mi))?.Name);
        Assert.Null(_clock.ActiveWindow(Utc(y, mo, d, h, mi).AddMinutes(-1)));
    }

    [Fact]
    public void Ist_Is0530AheadOfUtc_NoDst()
    {
        Assert.Equal(new TimeSpan(19, 5, 0), _clock.ToIst(Utc(2026, 10, 29, 13, 35)).TimeOfDay);
        Assert.Equal(new TimeSpan(20, 5, 0), _clock.ToIst(Utc(2026, 11, 5, 14, 35)).TimeOfDay);
    }

    [Fact]
    public void NoEntries_FriSatSun_AllowedMonToThu()
    {
        Assert.True(_clock.IsEntryWeekday(Utc(2026, 10, 29, 14, 0)));   // Thu
        Assert.False(_clock.IsEntryWeekday(Utc(2026, 10, 30, 14, 0)));  // Fri
        Assert.False(_clock.IsEntryWeekday(Utc(2026, 11, 1, 14, 0)));   // Sun
        Assert.True(_clock.IsEntryWeekday(Utc(2026, 11, 2, 15, 0)));    // Mon
    }

    [Fact]
    public void LondonDisabledByDefault_LunchAndAfter1530HaveNoWindow()
    {
        Assert.Null(_clock.ActiveWindow(Utc(2026, 10, 29, 7, 30)));   // 03:30 ET
        Assert.Null(_clock.ActiveWindow(Utc(2026, 10, 29, 16, 30)));  // 12:30 ET
        Assert.Null(_clock.ActiveWindow(Utc(2026, 10, 29, 19, 30)));  // 15:30 ET (end exclusive)
    }

    [Fact]
    public void Flatten_From1555Et_UntilRoll()
    {
        Assert.False(_clock.IsPastFlatten(Utc(2026, 10, 29, 19, 54)));
        Assert.True(_clock.IsPastFlatten(Utc(2026, 10, 29, 19, 55)));
        Assert.True(_clock.IsPastFlatten(Utc(2026, 10, 29, 21, 59)));   // 17:59 ET
        Assert.False(_clock.IsPastFlatten(Utc(2026, 10, 29, 22, 0)));   // 18:00 ET new session
    }

    [Fact]
    public void TradingDate_RollsAt1800Et()
    {
        Assert.Equal(new DateOnly(2026, 10, 29), _clock.TradingDate(Utc(2026, 10, 29, 21, 0)));   // 17:00 ET
        Assert.Equal(new DateOnly(2026, 10, 30), _clock.TradingDate(Utc(2026, 10, 29, 22, 30)));  // 18:30 ET
    }

    [Fact]
    public void NextWindowOpen_FromThursdayEvening_IsMondayAm_InEst()
    {
        var next = _clock.NextWindowOpen(Utc(2026, 10, 29, 20, 0));
        Assert.Equal("NY_AM_KILLZONE", next?.Window.Name);
        Assert.Equal(Utc(2026, 11, 2, 14, 35), next?.OpensUtc);
    }
}
```
- [ ] **Step 2:** Run `dotnet test tests/NQOrderFlow.Tests --filter SessionClockTests`. Expected: FAIL.
- [ ] **Step 3:** `Compliance/SessionClock.cs`:
```csharp
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

/// <summary>All trading time logic in US Eastern. Inputs are UTC; the PC time zone is irrelevant.</summary>
public sealed class SessionClock
{
    private const int MaxLookaheadDays = 8;
    private readonly TimeZoneInfo _et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private readonly TimeZoneInfo _ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    public SessionClock(SessionSettings settings) => Settings = settings;

    public SessionSettings Settings { get; }
    public DateTime ToEt(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), _et);
    public DateTime ToIst(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), _ist);
    public DateTime EtToUtc(DateTime etLocal) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(etLocal, DateTimeKind.Unspecified), _et);

    public DateOnly TradingDate(DateTime utc)
    {
        var et = ToEt(utc);
        var d = DateOnly.FromDateTime(et);
        return et.TimeOfDay >= Settings.TradingDayRollEt ? d.AddDays(1) : d;
    }

    public bool IsEntryWeekday(DateTime utc) => IsEntryDay(ToEt(utc).DayOfWeek);

    public SessionWindow? ActiveWindow(DateTime utc)
    {
        var tod = ToEt(utc).TimeOfDay;
        return Settings.Windows.FirstOrDefault(w => w.Enabled && tod >= w.StartEt && tod < w.EndEt);
    }

    public bool IsPastFlatten(DateTime utc)
    {
        var tod = ToEt(utc).TimeOfDay;
        return tod >= Settings.FlattenTimeEt && tod < Settings.TradingDayRollEt;
    }

    public (SessionWindow Window, DateTime OpensUtc)? NextWindowOpen(DateTime utc)
    {
        var etDate = ToEt(utc).Date;
        for (int day = 0; day < MaxLookaheadDays; day++)
        {
            var date = etDate.AddDays(day);
            if (!IsEntryDay(date.DayOfWeek)) continue;
            foreach (var w in Settings.Windows.Where(w => w.Enabled).OrderBy(w => w.StartEt))
            {
                var openUtc = EtToUtc(date + w.StartEt);
                if (openUtc > AsUtc(utc)) return (w, openUtc);
            }
        }
        return null;
    }

    public string Stamp(DateTime utc) =>
        $"UTC {AsUtc(utc):yyyy-MM-dd HH:mm:ss} | ET {ToEt(utc):HH:mm:ss} | IST {ToIst(utc):HH:mm:ss}";

    private static bool IsEntryDay(DayOfWeek d) =>
        d is DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday;

    private static DateTime AsUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add -A && git commit -m "feat: SessionClock with ET/IST, windows, DST-safe trading date"`

---

### Task 3: News blackouts

**Files:** Create `Compliance/NewsBlackout.cs`, `tests/NQOrderFlow.Tests/NewsBlackoutTests.cs`

**Interfaces:**
- Consumes `SessionClock`.
- Produces `NewsBlackout.Load(string? csvPath, SessionClock clock, int beforeMin, int afterMin, ILogSink log)`, `bool IsBlocked(DateTime utc, out string reason)`, `DateTime? NextEventUtc(DateTime utc)`.
- CSV rows are `yyyy-MM-dd HH:mm,Label` in **ET**, and `#` marks a comment.
- Recurring 08:30 and 10:00 ET (weekdays) **always** apply. The CSV adds events such as FOMC 14:00/14:30, which aren't recurring.

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class NewsBlackoutTests
{
    private readonly SessionClock _clock = new(SessionSettings.Default());
    private static DateTime Utc(int d, int h, int m) => new(2026, 10, d, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void Recurring1000Et_BlocksMinus2ToPlus3()
    {
        var nb = NewsBlackout.Load(null, _clock, 2, 3, new NullLogSink());
        Assert.False(nb.IsBlocked(Utc(29, 13, 57), out _));     // 09:57 ET
        Assert.True(nb.IsBlocked(Utc(29, 13, 58), out var r));  // 09:58 ET
        Assert.Contains("10:00", r);
        Assert.True(nb.IsBlocked(Utc(29, 14, 2), out _));
        Assert.False(nb.IsBlocked(Utc(29, 14, 3), out _));      // T+3 exclusive
    }

    [Fact]
    public void CsvEvent_IsParsedInEt()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "# FOMC\n2026-10-28 14:00,FOMC Statement\n");
        var nb = NewsBlackout.Load(path, _clock, 2, 3, new NullLogSink());
        Assert.True(nb.IsBlocked(Utc(28, 18, 0), out var r));   // 14:00 EDT
        Assert.Contains("FOMC", r);
    }

    [Fact]
    public void MalformedRow_IsSkipped_NotThrown()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "garbage\n2026-10-28 14:00,FOMC\n");
        Assert.True(NewsBlackout.Load(path, _clock, 2, 3, new NullLogSink()).IsBlocked(Utc(28, 18, 0), out _));
    }
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** `Compliance/NewsBlackout.cs`:
```csharp
using System.Globalization;

namespace QT_MNQ_Orderflow_Algo.Compliance;

public sealed class NewsBlackout
{
    private static readonly TimeSpan[] RecurringEt = { new(8, 30, 0), new(10, 0, 0) };
    private readonly SessionClock _clock;
    private readonly TimeSpan _before, _after;
    private readonly IReadOnlyList<(DateTime Utc, string Label)> _events;

    private NewsBlackout(SessionClock clock, int beforeMin, int afterMin, IReadOnlyList<(DateTime, string)> events)
    {
        _clock = clock;
        _before = TimeSpan.FromMinutes(beforeMin);
        _after = TimeSpan.FromMinutes(afterMin);
        _events = events;
    }

    public static NewsBlackout Load(string? csvPath, SessionClock clock, int beforeMin, int afterMin, ILogSink log)
    {
        var events = new List<(DateTime, string)>();
        if (csvPath is null || !File.Exists(csvPath))
        {
            log.Info("NewsBlackout: no CSV, using recurring 08:30/10:00 ET only");
            return new NewsBlackout(clock, beforeMin, afterMin, events);
        }
        foreach (var raw in File.ReadAllLines(csvPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
            if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var et))
            {
                log.Error($"NewsBlackout: skipped malformed row '{line}'");
                continue;
            }
            events.Add((clock.EtToUtc(et), parts.Length > 1 ? parts[1] : "News"));
        }
        log.Info($"NewsBlackout: {events.Count} CSV events + recurring 08:30/10:00 ET");
        return new NewsBlackout(clock, beforeMin, afterMin, events);
    }

    public bool IsBlocked(DateTime utc, out string reason)
    {
        foreach (var (evUtc, label) in Candidates(utc))
        {
            if (utc >= evUtc - _before && utc < evUtc + _after)
            {
                reason = $"News blackout: {label} at {_clock.ToEt(evUtc):HH:mm} ET";
                return true;
            }
        }
        reason = "";
        return false;
    }

    public DateTime? NextEventUtc(DateTime utc) =>
        Candidates(utc).Select(e => e.Utc).Where(t => t > utc).OrderBy(t => t).Cast<DateTime?>().FirstOrDefault();

    private IEnumerable<(DateTime Utc, string Label)> Candidates(DateTime utc)
    {
        foreach (var e in _events) yield return e;
        var etDate = _clock.ToEt(utc).Date;
        for (int d = 0; d <= 1; d++)
        {
            var date = etDate.AddDays(d);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            foreach (var t in RecurringEt) yield return (_clock.EtToUtc(date + t), $"Recurring {t:hh\\:mm}");
        }
    }
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add -A && git commit -m "feat: news blackout with recurring defaults and ET CSV"`

---

### Task 4: RiskGovernor

**Files:** Create `Compliance/RiskGovernor.cs`, `tests/NQOrderFlow.Tests/RiskGovernorTests.cs`

**Interfaces — Produces:**
- `enum RiskTier { A, B, C, Halt }`
- `sealed record RiskCheck(bool Ok, decimal RiskUsd, RiskTier Tier, string Reason)`
- `RiskGovernor(RiskSettings)` with:
  - `RiskTier TierFor(decimal headroom)`
  - `int ScoreThresholdAdd(RiskTier)`
  - `decimal MaxRiskUsd(RiskTier)`
  - `RiskCheck CheckTrade(decimal stopPoints, decimal pointValue, decimal headroomUsd)`

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class RiskGovernorTests
{
    private readonly RiskGovernor _g = new(new RiskSettings());

    [Theory]
    [InlineData(1000, RiskTier.A)] [InlineData(900, RiskTier.A)]
    [InlineData(899.99, RiskTier.B)] [InlineData(600, RiskTier.B)]
    [InlineData(599, RiskTier.C)] [InlineData(350, RiskTier.C)]
    [InlineData(349.99, RiskTier.Halt)] [InlineData(-5, RiskTier.Halt)]
    public void Tiers_MatchSpecBoundaries(double headroom, RiskTier expected)
        => Assert.Equal(expected, _g.TierFor((decimal)headroom));

    [Fact]
    public void RiskUsd_IncludesRoundTripCommissionAndSlippage()
    {
        var r = _g.CheckTrade(10m, 20m, 1000m);
        Assert.Equal(210m, r.RiskUsd);  // 200 + 2 x 2.50 + 5
        Assert.True(r.Ok);
    }

    [Fact]
    public void TierCap_IsInclusive()
    {
        Assert.True(_g.CheckTrade(12m, 20m, 1000m).Ok);      // 250 == cap
        Assert.False(_g.CheckTrade(12.25m, 20m, 1000m).Ok);  // 255 > cap
    }

    [Fact]
    public void Rejects_AboveQuarterOfHeadroom()
    {
        var r = _g.CheckTrade(8m, 20m, 620m);  // 170 <= B cap 200, but > 25% of 620 = 155
        Assert.False(r.Ok);
        Assert.Contains("headroom", r.Reason);
    }

    [Fact]
    public void TierC_RejectsStopsWiderThan7_5Points()
        => Assert.False(_g.CheckTrade(7.75m, 2m, 500m).Ok);

    [Fact]
    public void Halt_RejectsEverything()
        => Assert.False(_g.CheckTrade(1m, 2m, 300m).Ok);
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** `Compliance/RiskGovernor.cs`:
```csharp
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

public enum RiskTier { A, B, C, Halt }

public sealed record RiskCheck(bool Ok, decimal RiskUsd, RiskTier Tier, string Reason);

/// <summary>Size is fixed, so risk is controlled by skipping wide stops and tightening as headroom shrinks.</summary>
public sealed class RiskGovernor
{
    private readonly RiskSettings _s;
    public RiskGovernor(RiskSettings settings) => _s = settings;

    public RiskTier TierFor(decimal headroom) =>
        headroom >= _s.TierAMinHeadroom ? RiskTier.A :
        headroom >= _s.TierBMinHeadroom ? RiskTier.B :
        headroom >= _s.HaltHeadroomUsd ? RiskTier.C : RiskTier.Halt;

    public int ScoreThresholdAdd(RiskTier t) => t switch
    {
        RiskTier.B => _s.ScoreAddTierB,
        RiskTier.C => _s.ScoreAddTierC,
        _ => 0,
    };

    public decimal MaxRiskUsd(RiskTier t) => t switch
    {
        RiskTier.A => _s.MaxRiskTierA,
        RiskTier.B => _s.MaxRiskTierB,
        RiskTier.C => _s.MaxRiskTierC,
        _ => 0m,
    };

    public RiskCheck CheckTrade(decimal stopPoints, decimal pointValue, decimal headroomUsd)
    {
        var tier = TierFor(headroomUsd);
        var risk = stopPoints * pointValue + 2 * _s.CommissionPerSideUsd + _s.SlippageUsd;
        if (tier == RiskTier.Halt)
            return new(false, risk, tier, $"Tier HALT: headroom {headroomUsd:F0} < {_s.HaltHeadroomUsd:F0}");
        if (tier == RiskTier.C && stopPoints > _s.MaxStopPointsTierC)
            return new(false, risk, tier, $"Tier C: stop {stopPoints} pts > {_s.MaxStopPointsTierC}");
        if (risk > MaxRiskUsd(tier))
            return new(false, risk, tier, $"Risk {risk:F2} > tier {tier} cap {MaxRiskUsd(tier):F0}");
        var pctCap = headroomUsd * _s.MaxRiskPercentOfHeadroom / 100m;
        if (risk > pctCap)
            return new(false, risk, tier, $"Risk {risk:F2} > {_s.MaxRiskPercentOfHeadroom}% of headroom ({pctCap:F2})");
        return new(true, risk, tier, "OK");
    }
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add -A && git commit -m "feat: risk governor tiers and per-trade risk check"`

---

### Task 5: DailyRiskState

**Files:** Create `Compliance/DailyRiskState.cs`, `tests/NQOrderFlow.Tests/DailyRiskStateTests.cs`

**Interfaces — Produces:**
- An immutable record:
  ```
  DailyRiskState(DateOnly Date, decimal RealizedPnl, decimal PeakPnl, int Trades, int ConsecutiveLosses,
                 DateTime? LastCloseUtc, bool LastWasWin, DateTime? StreakPauseUntilUtc,
                 bool PostStreakTradeUsed, string? HaltReason)
  ```
- `static New(DateOnly)`
- `DailyRiskState WithTradeClosed(decimal pnl, DateTime closeUtc, RiskSettings s)`
- `bool CanEnter(DateTime nowUtc, bool isAPlus, RiskSettings s, out string reason)`

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class DailyRiskStateTests
{
    private static readonly RiskSettings S = new();
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static DailyRiskState Fresh() => DailyRiskState.New(new DateOnly(2026, 10, 29));

    [Fact]
    public void Loss_StartsTenMinuteCooldown()
    {
        var s = Fresh().WithTradeClosed(-100m, T0, S);
        Assert.False(s.CanEnter(T0.AddMinutes(9), false, S, out var r));
        Assert.Contains("cooldown", r);
        Assert.True(s.CanEnter(T0.AddMinutes(10), false, S, out _));
    }

    [Fact]
    public void Win_StartsThreeMinuteCooldown()
    {
        var s = Fresh().WithTradeClosed(50m, T0, S);
        Assert.False(s.CanEnter(T0.AddMinutes(2), false, S, out _));
        Assert.True(s.CanEnter(T0.AddMinutes(3), false, S, out _));
    }

    [Fact]
    public void DailyLoss_Halts()
    {
        var s = Fresh().WithTradeClosed(-200m, T0, S).WithTradeClosed(-160m, T0.AddMinutes(20), S);
        Assert.NotNull(s.HaltReason);
        Assert.False(s.CanEnter(T0.AddHours(2), true, S, out _));
    }

    [Fact]
    public void TwoLossStreak_Pauses30Min_ThenOneAPlusOnly()
    {
        var s = Fresh().WithTradeClosed(-50m, T0, S).WithTradeClosed(-50m, T0.AddMinutes(15), S);
        var resume = T0.AddMinutes(45);
        Assert.False(s.CanEnter(resume.AddMinutes(-1), true, S, out _));
        Assert.False(s.CanEnter(resume, false, S, out var r));
        Assert.Contains("A+", r);
        Assert.True(s.CanEnter(resume, true, S, out _));
        var s2 = s.WithTradeClosed(10m, resume.AddMinutes(5), S);
        Assert.False(s2.CanEnter(resume.AddHours(1), true, S, out _));
    }

    [Fact]
    public void ProfitCap_Halts() => Assert.NotNull(Fresh().WithTradeClosed(460m, T0, S).HaltReason);

    [Fact]
    public void Giveback40PercentOfPeakAbove200_Halts()
        => Assert.NotNull(Fresh().WithTradeClosed(300m, T0, S).WithTradeClosed(-120m, T0.AddMinutes(10), S).HaltReason);

    [Fact]
    public void MaxTradesPerDay_Blocks()
    {
        var s = Fresh();
        for (int i = 0; i < 4; i++) s = s.WithTradeClosed(10m, T0.AddMinutes(i * 10), S);
        Assert.False(s.CanEnter(T0.AddHours(3), true, S, out var r));
        Assert.Contains("trades", r);
    }

    [Fact]
    public void WithTradeClosed_DoesNotMutateOriginal()
    {
        var a = Fresh();
        _ = a.WithTradeClosed(-100m, T0, S);
        Assert.Equal(0, a.Trades);
    }
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** `Compliance/DailyRiskState.cs`:
```csharp
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

/// <summary>Self-imposed daily limits. Lucid Flex has no DLL; these protect the $1,000 EOD cushion.</summary>
public sealed record DailyRiskState(
    DateOnly Date, decimal RealizedPnl, decimal PeakPnl, int Trades, int ConsecutiveLosses,
    DateTime? LastCloseUtc, bool LastWasWin, DateTime? StreakPauseUntilUtc, bool PostStreakTradeUsed, string? HaltReason)
{
    public static DailyRiskState New(DateOnly date) => new(date, 0m, 0m, 0, 0, null, false, null, false, null);

    public DailyRiskState WithTradeClosed(decimal pnl, DateTime closeUtc, RiskSettings s)
    {
        var losses = pnl < 0 ? ConsecutiveLosses + 1 : 0;
        var streakHit = StreakPauseUntilUtc is null && pnl < 0 && losses >= s.MaxConsecutiveLosses;
        var next = this with
        {
            RealizedPnl = RealizedPnl + pnl,
            PeakPnl = Math.Max(PeakPnl, RealizedPnl + pnl),
            Trades = Trades + 1,
            ConsecutiveLosses = losses,
            LastCloseUtc = closeUtc,
            LastWasWin = pnl > 0,
            StreakPauseUntilUtc = streakHit ? closeUtc.AddMinutes(s.StreakPauseMinutes) : StreakPauseUntilUtc,
            PostStreakTradeUsed = PostStreakTradeUsed || StreakPauseUntilUtc is not null,
        };
        return next with { HaltReason = HaltReason ?? next.EvaluateHalt(s) };
    }

    public bool CanEnter(DateTime nowUtc, bool isAPlus, RiskSettings s, out string reason)
    {
        reason = Veto(nowUtc, isAPlus, s) ?? "";
        return reason.Length == 0;
    }

    private string? Veto(DateTime nowUtc, bool isAPlus, RiskSettings s)
    {
        if (HaltReason is not null) return $"Day halted: {HaltReason}";
        if (Trades >= s.MaxTradesPerDay) return $"Max trades {s.MaxTradesPerDay} reached";
        if (StreakPauseUntilUtc is { } pause)
        {
            if (nowUtc < pause) return $"Loss-streak pause until {pause:HH:mm}Z";
            if (PostStreakTradeUsed) return "Post-streak trade already used";
            if (!isAPlus) return "Post-streak: A+ only";
        }
        if (LastCloseUtc is { } last)
        {
            var until = last.AddMinutes(LastWasWin ? s.WinCooldownMinutes : s.LossCooldownMinutes);
            if (nowUtc < until) return $"Post-trade cooldown until {until:HH:mm:ss}Z";
        }
        return null;
    }

    private string? EvaluateHalt(RiskSettings s)
    {
        if (RealizedPnl <= -s.MaxDailyLossUsd) return $"Daily loss {RealizedPnl:F2} <= -{s.MaxDailyLossUsd}";
        if (RealizedPnl >= s.DailyProfitCapUsd) return $"Daily profit cap {RealizedPnl:F2} >= {s.DailyProfitCapUsd}";
        if (PeakPnl > s.GivebackArmUsd && RealizedPnl <= PeakPnl * (1 - s.GivebackPercent / 100m))
            return $"Giveback {RealizedPnl:F2} from peak {PeakPnl:F2}";
        return null;
    }
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add -A && git commit -m "feat: daily risk state with limits, streak pause, cooldowns"`

---

### Task 6: EvaluationTracker

**Files:** Create `Compliance/EvaluationTracker.cs`, `tests/NQOrderFlow.Tests/EvaluationTrackerTests.cs`

**Interfaces — Produces:** an immutable record:
```
EvaluationTracker(decimal EodHighBalance, decimal MllFloor, ImmutableDictionary<DateOnly, decimal> DailyPnl,
                  decimal WinProfitTotal, decimal WinProfitShortHold, bool Breached, bool TargetReached)
```
with these members:
- `static Start(AccountRules)`
- `WithEndOfDay(DateOnly, decimal closingBalance, AccountRules)`
- `WithTradeClosed(DateOnly, decimal pnl, double holdSeconds, RiskSettings, AccountRules)`
- `TotalProfit`, `BestDay`, `ConsistencyRatio`, `TradingDays`, `MicroscalpPercent`
- `Headroom(decimal equity)`
- `ConsistencyEarlyStop(DateOnly, AccountRules)`
- `TargetConditionsMet(AccountRules)`

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class EvaluationTrackerTests
{
    private static readonly AccountRules A = new();
    private static readonly RiskSettings R = new();
    private static DateOnly D(int d) => new(2026, 10, d);

    [Fact]
    public void Floor_StartsAtInitialMinusMll() => Assert.Equal(24000m, EvaluationTracker.Start(A).MllFloor);

    [Fact]
    public void Floor_RatchetsUpOnly_AndLocks()
    {
        var t = EvaluationTracker.Start(A).WithEndOfDay(D(26), 25400m, A).WithEndOfDay(D(27), 25100m, A);
        Assert.Equal(24400m, t.MllFloor);
        Assert.Equal(25000m, t.WithEndOfDay(D(28), 26500m, A).MllFloor);
    }

    [Fact]
    public void Breach_WhenEodCloseAtOrBelowFloor()
        => Assert.True(EvaluationTracker.Start(A).WithEndOfDay(D(26), 24000m, A).Breached);

    [Fact]
    public void Consistency_IsBestDayOverTotal()
    {
        var t = EvaluationTracker.Start(A)
            .WithTradeClosed(D(26), 400m, 60, R, A).WithTradeClosed(D(27), 600m, 60, R, A)
            .WithTradeClosed(D(28), -100m, 60, R, A);
        Assert.Equal(900m, t.TotalProfit);
        Assert.Equal(600m / 900m, t.ConsistencyRatio);
        Assert.Equal(3, t.TradingDays);
    }

    [Fact]
    public void EarlyStop_UsesTargetAsMinimumDenominator_SoFirstWinDoesNotHalt()
    {
        var t = EvaluationTracker.Start(A).WithTradeClosed(D(26), 200m, 60, R, A);
        Assert.False(t.ConsistencyEarlyStop(D(26), A));                                         // 200 < 562.5
        Assert.True(t.WithTradeClosed(D(26), 400m, 60, R, A).ConsistencyEarlyStop(D(26), A));  // 600 >= 562.5
    }

    [Fact]
    public void Target_NeedsProfitDaysAndConsistency()
    {
        Assert.False(EvaluationTracker.Start(A).WithTradeClosed(D(26), 1300m, 60, R, A).TargetConditionsMet(A));
        var ok = EvaluationTracker.Start(A)
            .WithTradeClosed(D(26), 450m, 60, R, A).WithTradeClosed(D(27), 450m, 60, R, A)
            .WithTradeClosed(D(28), 400m, 60, R, A);
        Assert.True(ok.TargetConditionsMet(A));
        Assert.True(ok.TargetReached);
    }

    [Fact]
    public void Microscalp_IsShareOfWinningProfitFromHoldsAtOrUnder5s()
    {
        var t = EvaluationTracker.Start(A)
            .WithTradeClosed(D(26), 100m, 4.0, R, A).WithTradeClosed(D(26), 300m, 30, R, A)
            .WithTradeClosed(D(26), -50m, 2, R, A);
        Assert.Equal(25m, t.MicroscalpPercent);
    }
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** `Compliance/EvaluationTracker.cs`:
```csharp
using System.Collections.Immutable;
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

/// <summary>Lucid Flex evaluation memory. Lucid checks MLL at EOD only; intraday protection is the guard's job.</summary>
public sealed record EvaluationTracker(
    decimal EodHighBalance, decimal MllFloor, ImmutableDictionary<DateOnly, decimal> DailyPnl,
    decimal WinProfitTotal, decimal WinProfitShortHold, bool Breached, bool TargetReached)
{
    public static EvaluationTracker Start(AccountRules a) => new(
        a.InitialBalance, a.InitialBalance - a.MllDistanceUsd, ImmutableDictionary<DateOnly, decimal>.Empty, 0m, 0m, false, false);

    public decimal TotalProfit => DailyPnl.Values.Sum();
    public decimal BestDay => DailyPnl.IsEmpty ? 0m : Math.Max(0m, DailyPnl.Values.Max());
    public decimal ConsistencyRatio => TotalProfit <= 0 ? 0m : BestDay / TotalProfit;
    public int TradingDays => DailyPnl.Count;
    public decimal MicroscalpPercent => WinProfitTotal <= 0 ? 0m : WinProfitShortHold / WinProfitTotal * 100m;
    public decimal Headroom(decimal equity) => equity - MllFloor;

    public EvaluationTracker WithEndOfDay(DateOnly date, decimal closingBalance, AccountRules a)
    {
        var high = Math.Max(EodHighBalance, closingBalance);
        var floor = Math.Max(MllFloor, Math.Min(high - a.MllDistanceUsd, a.MllLockFloorUsd));
        return this with { EodHighBalance = high, MllFloor = floor, Breached = Breached || closingBalance <= MllFloor };
    }

    public EvaluationTracker WithTradeClosed(DateOnly date, decimal pnl, double holdSeconds, RiskSettings r, AccountRules a)
    {
        var isWin = pnl > 0;
        var next = this with
        {
            DailyPnl = DailyPnl.SetItem(date, (DailyPnl.TryGetValue(date, out var v) ? v : 0m) + pnl),
            WinProfitTotal = WinProfitTotal + (isWin ? pnl : 0m),
            WinProfitShortHold = WinProfitShortHold + (isWin && holdSeconds <= r.MicroscalpHoldSeconds ? pnl : 0m),
        };
        return next with { TargetReached = TargetReached || next.TargetConditionsMet(a) };
    }

    public bool ConsistencyEarlyStop(DateOnly today, AccountRules a)
    {
        var todayPnl = DailyPnl.TryGetValue(today, out var v) ? v : 0m;
        return todayPnl > 0 && todayPnl >= Math.Max(TotalProfit, a.ProfitTargetUsd) * a.ConsistencyEarlyStopPercent / 100m;
    }

    public bool TargetConditionsMet(AccountRules a) =>
        TotalProfit >= a.ProfitTargetUsd + a.TargetBufferUsd
        && TradingDays >= a.MinTradingDays
        && ConsistencyRatio <= a.ConsistencyCapPercent / 100m;
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add -A && git commit -m "feat: evaluation tracker with EOD MLL ratchet, consistency, microscalp"`

---

### Task 7: LucidRiskGuard

**Files:** Create `Compliance/LucidRiskGuard.cs`, `tests/NQOrderFlow.Tests/LucidRiskGuardTests.cs`

**Interfaces:**
- Consumes Tasks 2–6.
- Produces:
  - `enum Side { Buy, Sell }`
  - `sealed record EntryRequest(DateTime NowUtc, Side Side, int Quantity, decimal StopPoints, decimal PointValue, bool IsAPlus)`
  - `sealed record GuardContext(decimal Equity, int OpenPositionQty, Side? OpenPositionSide, bool DataHealthy, bool Connected)`
  - `sealed record GuardDecision(bool Allowed, string Reason, RiskTier Tier)`
  - `LucidRiskGuard.ForFlexEvaluation(AccountRules, RiskSettings, SessionClock, NewsBlackout, ILogSink, EvaluationTracker? = null, DailyRiskState? = null)`
  - Methods:
    - `GuardDecision CanEnter(EntryRequest, GuardContext)`
    - `void OnTradeClosed(DateTime closeUtc, decimal pnl, double holdSeconds)`
    - `void OnEndOfDay(DateOnly, decimal closingBalance)`
    - `bool ShouldFlattenNow(DateTime)`
    - `RiskTier TierFor(decimal equity)`
    - `void HaltSafeMode(string)`
  - Properties: `IsBreached`, `DailyHaltActive`, `ManualReenableRequired`, `Eval`, `Day`
  - `const int LockedQuantity = 1`
- All public members take `lock (_gate)`.

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class LucidRiskGuardTests
{
    private static readonly DateTime ThuAm = new(2026, 10, 29, 14, 15, 0, DateTimeKind.Utc); // 10:15 ET
    private static readonly GuardContext Flat = new(25000m, 0, null, true, true);

    private static LucidRiskGuard Guard()
    {
        var clock = new SessionClock(SessionSettings.Default());
        return LucidRiskGuard.ForFlexEvaluation(new AccountRules(), new RiskSettings(), clock,
            NewsBlackout.Load(null, clock, 2, 3, new NullLogSink()), new NullLogSink());
    }

    private static EntryRequest Req(DateTime t, Side side = Side.Buy, int qty = 1, decimal stop = 8m) => new(t, side, qty, stop, 20m, false);
    private static DateTime Day(int d) => new(2026, 10, d, 14, 15, 0, DateTimeKind.Utc);

    [Fact] public void Allows_CleanEntryInWindow() => Assert.True(Guard().CanEnter(Req(ThuAm), Flat).Allowed);
    [Fact] public void Rejects_Friday() => Assert.False(Guard().CanEnter(Req(ThuAm.AddDays(1)), Flat).Allowed);
    [Fact] public void Rejects_OutsideWindow() => Assert.False(Guard().CanEnter(Req(ThuAm.AddHours(2)), Flat).Allowed);
    [Fact] public void Rejects_NewsBlackout() => Assert.False(Guard().CanEnter(Req(ThuAm.AddMinutes(-16)), Flat).Allowed);
    [Fact] public void Rejects_QuantityNotOne() => Assert.False(Guard().CanEnter(Req(ThuAm, qty: 2), Flat).Allowed);
    [Fact] public void Rejects_DataUnhealthy() => Assert.False(Guard().CanEnter(Req(ThuAm), Flat with { DataHealthy = false }).Allowed);
    [Fact] public void Rejects_Disconnected() => Assert.False(Guard().CanEnter(Req(ThuAm), Flat with { Connected = false }).Allowed);
    [Fact] public void Rejects_WideStop() => Assert.False(Guard().CanEnter(Req(ThuAm, stop: 15m), Flat).Allowed);
    [Fact] public void Rejects_LowHeadroom() => Assert.False(Guard().CanEnter(Req(ThuAm), Flat with { Equity = 24300m }).Allowed);

    [Fact]
    public void Rejects_Hedge()
    {
        var d = Guard().CanEnter(Req(ThuAm, Side.Sell), Flat with { OpenPositionQty = 1, OpenPositionSide = Side.Buy });
        Assert.False(d.Allowed);
        Assert.Contains("hedge", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_SecondPositionSameSide()
        => Assert.False(Guard().CanEnter(Req(ThuAm), Flat with { OpenPositionQty = 1, OpenPositionSide = Side.Buy }).Allowed);

    [Fact]
    public void Halts_WhenMicroscalpShareOver35Percent()
    {
        var g = Guard();
        g.OnTradeClosed(ThuAm.AddDays(-1), 100m, 3);
        Assert.False(g.CanEnter(Req(ThuAm), Flat with { Equity = 25100m }).Allowed);
    }

    [Fact]
    public void TargetReached_RequiresManualReenable()
    {
        var g = Guard();
        g.OnTradeClosed(Day(26), 445m, 60);
        g.OnTradeClosed(Day(27), 445m, 60);
        g.OnTradeClosed(Day(28), 400m, 60);
        Assert.True(g.ManualReenableRequired);
        Assert.False(g.CanEnter(Req(ThuAm), Flat with { Equity = 26290m }).Allowed);
    }

    [Fact]
    public void NewTradingDate_ResetsDailyState_KeepsEval()
    {
        var g = Guard();
        g.OnTradeClosed(Day(28), -360m, 60);
        Assert.True(g.DailyHaltActive);
        g.CanEnter(Req(ThuAm), Flat with { Equity = 24640m });   // rolls to Thursday
        Assert.False(g.DailyHaltActive);
        Assert.Equal(-360m, g.Eval.TotalProfit);
    }

    [Fact]
    public void ShouldFlatten_At1555Et()
        => Assert.True(Guard().ShouldFlattenNow(new DateTime(2026, 10, 29, 19, 55, 0, DateTimeKind.Utc)));
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** `Compliance/LucidRiskGuard.cs`:
```csharp
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

public enum Side { Buy, Sell }
public sealed record EntryRequest(DateTime NowUtc, Side Side, int Quantity, decimal StopPoints, decimal PointValue, bool IsAPlus);
public sealed record GuardContext(decimal Equity, int OpenPositionQty, Side? OpenPositionSide, bool DataHealthy, bool Connected);
public sealed record GuardDecision(bool Allowed, string Reason, RiskTier Tier);

/// <summary>Veto authority over every entry. Every rejection is logged at Trading level as the audit trail.</summary>
public sealed class LucidRiskGuard
{
    public const int LockedQuantity = 1;

    private readonly object _gate = new();
    private readonly AccountRules _a;
    private readonly RiskSettings _r;
    private readonly SessionClock _clock;
    private readonly NewsBlackout _news;
    private readonly RiskGovernor _gov;
    private readonly ILogSink _log;
    private EvaluationTracker _eval;
    private DailyRiskState _day;
    private string? _safeModeReason;

    private LucidRiskGuard(AccountRules a, RiskSettings r, SessionClock c, NewsBlackout n, ILogSink log, EvaluationTracker e, DailyRiskState d)
    {
        _a = a; _r = r; _clock = c; _news = n; _log = log;
        _gov = new RiskGovernor(r);
        _eval = e; _day = d;
    }

    public static LucidRiskGuard ForFlexEvaluation(AccountRules a, RiskSettings r, SessionClock clock, NewsBlackout news,
        ILogSink log, EvaluationTracker? eval = null, DailyRiskState? day = null)
        => new(a, r, clock, news, log, eval ?? EvaluationTracker.Start(a), day ?? DailyRiskState.New(DateOnly.MinValue));

    public EvaluationTracker Eval { get { lock (_gate) return _eval; } }
    public DailyRiskState Day { get { lock (_gate) return _day; } }
    public bool IsBreached { get { lock (_gate) return _eval.Breached; } }
    public bool DailyHaltActive { get { lock (_gate) return _day.HaltReason is not null; } }
    public bool ManualReenableRequired { get { lock (_gate) return _eval.TargetReached || _safeModeReason is not null; } }

    public RiskTier TierFor(decimal equity) { lock (_gate) return _gov.TierFor(_eval.Headroom(equity)); }

    public void HaltSafeMode(string reason)
    {
        lock (_gate) { _safeModeReason = reason; _log.Trading($"SAFE MODE: {reason}"); }
    }

    public bool ShouldFlattenNow(DateTime utc)
    {
        lock (_gate) return _clock.IsPastFlatten(utc) || _eval.Breached || _safeModeReason is not null;
    }

    public GuardDecision CanEnter(EntryRequest q, GuardContext ctx)
    {
        lock (_gate)
        {
            RollDay(q.NowUtc);
            var tier = _gov.TierFor(_eval.Headroom(ctx.Equity));
            var reason = FirstVeto(q, ctx);
            if (reason is not null) _log.Trading($"VETO [{_clock.Stamp(q.NowUtc)}] {q.Side} x{q.Quantity}: {reason}");
            return new GuardDecision(reason is null, reason ?? "OK", tier);
        }
    }

    public void OnTradeClosed(DateTime closeUtc, decimal pnl, double holdSeconds)
    {
        lock (_gate)
        {
            RollDay(closeUtc);
            _day = _day.WithTradeClosed(pnl, closeUtc, _r);
            _eval = _eval.WithTradeClosed(_day.Date, pnl, holdSeconds, _r, _a);
            if (_eval.MicroscalpPercent >= _r.MicroscalpWarnPercent) _log.Trading($"WARN microscalp share {_eval.MicroscalpPercent:F1}%");
            if (_day.HaltReason is not null) _log.Trading($"DAY HALT: {_day.HaltReason}");
            if (_eval.TargetReached) _log.Trading("TARGET REACHED: trading stopped, manual re-enable required");
        }
    }

    public void OnEndOfDay(DateOnly date, decimal closingBalance)
    {
        lock (_gate)
        {
            _eval = _eval.WithEndOfDay(date, closingBalance, _a);
            _log.Trading($"EOD {date}: close {closingBalance:F2}, floor {_eval.MllFloor:F2}, breached {_eval.Breached}");
        }
    }

    private string? FirstVeto(EntryRequest q, GuardContext ctx)
    {
        if (_safeModeReason is not null) return $"Safe mode: {_safeModeReason}";
        if (_eval.Breached) return "Account breached (EOD MLL)";
        if (_eval.TargetReached) return "Profit target reached: manual re-enable required";
        if (!ctx.Connected) return "Disconnected";
        if (!ctx.DataHealthy) return "Data health failing";
        if (q.Quantity != LockedQuantity) return $"Quantity {q.Quantity} != locked {LockedQuantity}";
        if (ctx.OpenPositionQty != 0)
            return ctx.OpenPositionSide is { } s && s != q.Side ? "Hedge blocked: opposite side of open position" : "Single-position rule";
        if (q.Quantity > _a.MaxContractsAllowed) return $"Exceeds MaxContractsAllowed {_a.MaxContractsAllowed}";
        if (!_clock.IsEntryWeekday(q.NowUtc)) return "No entries Fri/Sat/Sun";
        if (_clock.IsPastFlatten(q.NowUtc)) return "Past flatten time";
        if (_clock.ActiveWindow(q.NowUtc) is null) return "Outside session windows";
        if (_news.IsBlocked(q.NowUtc, out var news)) return news;
        if (_eval.MicroscalpPercent >= _r.MicroscalpHaltPercent)
            return $"Microscalp {_eval.MicroscalpPercent:F1}% >= {_r.MicroscalpHaltPercent}%";
        if (_eval.ConsistencyEarlyStop(_day.Date, _a)) return "Consistency early stop for today";
        if (!_day.CanEnter(q.NowUtc, q.IsAPlus, _r, out var dayReason)) return dayReason;
        var risk = _gov.CheckTrade(q.StopPoints, q.PointValue, _eval.Headroom(ctx.Equity));
        return risk.Ok ? null : risk.Reason;
    }

    private void RollDay(DateTime utc)
    {
        var td = _clock.TradingDate(utc);
        if (td != _day.Date) _day = DailyRiskState.New(td);
    }
}
```
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add -A && git commit -m "feat: LucidRiskGuard aggregate veto with Flex evaluation factory"`

---

### Task 8: Persistence

**Files:** Create `Persistence/PersistedState.cs`, `Persistence/StateStore.cs`, `tests/NQOrderFlow.Tests/StateStoreTests.cs`

**Interfaces — Produces:**
- `sealed record PersistedState(int SchemaVersion, EvaluationTracker Eval, DailyRiskState Day, DateOnly? LastEodProcessed)`
- `sealed record LoadResult(PersistedState? State, bool Fresh, string? Error)`
- `StateStore(string path)` with `LoadResult Load()` and `void Save(PersistedState)`

- [ ] **Step 1: Failing tests:**
```csharp
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Persistence;
using Xunit;

public sealed class StateStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"nqstate-{Guid.NewGuid():N}", "state.json");

    [Fact]
    public void Missing_IsFresh()
    {
        var r = new StateStore(TempPath()).Load();
        Assert.True(r.Fresh);
        Assert.Null(r.Error);
    }

    [Fact]
    public void RoundTrip_PreservesRiskMemory()
    {
        var a = new AccountRules();
        var path = TempPath();
        var eval = EvaluationTracker.Start(a)
            .WithTradeClosed(new DateOnly(2026, 10, 28), 300m, 4, new RiskSettings(), a)
            .WithEndOfDay(new DateOnly(2026, 10, 28), 25300m, a);
        var day = DailyRiskState.New(new DateOnly(2026, 10, 29)) with { HaltReason = "x" };
        new StateStore(path).Save(new PersistedState(1, eval, day, new DateOnly(2026, 10, 28)));

        var loaded = new StateStore(path).Load().State!;
        Assert.Equal(24300m, loaded.Eval.MllFloor);
        Assert.Equal(300m, loaded.Eval.DailyPnl[new DateOnly(2026, 10, 28)]);
        Assert.Equal(100m, loaded.Eval.MicroscalpPercent);
        Assert.Equal("x", loaded.Day.HaltReason);
    }

    [Fact]
    public void Corrupt_ReturnsError_AndPreservesFile()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var r = new StateStore(path).Load();
        Assert.NotNull(r.Error);
        Assert.Null(r.State);
        Assert.False(r.Fresh);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "state.json.corrupt-*"));
    }
}
```
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement:
```csharp
// Persistence/PersistedState.cs
using QT_MNQ_Orderflow_Algo.Compliance;

namespace QT_MNQ_Orderflow_Algo.Persistence;

public sealed record PersistedState(int SchemaVersion, EvaluationTracker Eval, DailyRiskState Day, DateOnly? LastEodProcessed);
public sealed record LoadResult(PersistedState? State, bool Fresh, string? Error);
```
```csharp
// Persistence/StateStore.cs
using System.Text.Json;

namespace QT_MNQ_Orderflow_Algo.Persistence;

/// <summary>Atomic JSON persistence. Unreadable state fails closed: the caller must halt, never reset.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public StateStore(string path) => _path = path;

    public LoadResult Load()
    {
        if (!File.Exists(_path)) return new(null, true, null);
        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_path), Json)
                        ?? throw new JsonException("null document");
            return new(state, false, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
        {
            var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_path, backup, overwrite: false);
            return new(null, false, $"State unreadable ({ex.Message}); preserved as {backup}");
        }
    }

    public void Save(PersistedState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
```
If the round-trip test fails on the `ImmutableDictionary<DateOnly, decimal>` key or the computed properties, mark the computed members of `EvaluationTracker` with `[JsonIgnore]`, or add a JSON DTO with `Dictionary<string, decimal>`. Record the fix in `docs/API_NOTES.md`.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** `git add -A && git commit -m "feat: atomic fail-closed state persistence"`

---

### Task 9: Strategy shell + docs

**Files:**
- Delete: `QT_MNQ_Orderflow_Algo.cs`
- Create: `NQOrderFlowStrategy.cs`, `docs/API_NOTES.md`, `docs/ASSUMPTIONS.md`

**Interfaces:** Consumes all core types. Produces the `NQOrderFlowStrategy : Strategy` class that later phases extend.

- [ ] **Step 1: Verify the API** against the installed v1.146.18 DLL. Use the VS Object Browser on `C:\Quantower\TradingPlatform\v1.146.18\bin\TradingPlatform.BusinessLayer.dll`, or api.quantower.com.
  - Members to check: `InputParameter` constructor overloads and supported field types (string, bool, double, int); `Core.Instance.TimeUtils.DateTimeUtcNow`; `Symbol.Name` / `Symbol.Root`; `Symbol.TickSize`; `Symbol.GetTickCost`; `Account.Balance`; `Last.Time`; `StrategyLoggingLevel.Trading`; `Strategy.Stop()`; `List<StrategyMetric>.Add(string, string)` extension.
  - Write `docs/API_NOTES.md` as a table: `Member | Exists in 1.146.18 | Used for | Substitute`.
  - If a name differs from the code below, use the real one and record it.
- [ ] **Step 2:** Write `NQOrderFlowStrategy.cs`:
```csharp
using System.Threading;
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Persistence;
using TradingPlatform.BusinessLayer;

namespace QT_MNQ_Orderflow_Algo;

/// <summary>Phase 1: lifecycle, inputs, clock, risk layer, persistence, metrics. Places NO orders.</summary>
public sealed class NQOrderFlowStrategy : Strategy
{
    private const int TimerPeriodMs = 1000;
    private static readonly string HhMm = @"hh\:mm";

    [InputParameter("Symbol", 10)] public Symbol? symbol;
    [InputParameter("Account", 20)] public Account? account;
    [InputParameter("Allowed roots (CSV)", 30)] public string AllowedRoots = "NQ";

    [InputParameter("Initial balance", 100)] public double InitialBalance = 25000;
    [InputParameter("MLL distance USD", 110)] public double MllDistanceUsd = 1000;
    [InputParameter("MLL lock floor USD (VERIFY)", 120)] public double MllLockFloorUsd = 25000;
    [InputParameter("Profit target USD", 130)] public double ProfitTargetUsd = 1250;
    [InputParameter("Consistency cap %", 140)] public double ConsistencyCapPercent = 50;
    [InputParameter("Consistency early stop %", 145)] public double ConsistencyEarlyStopPercent = 45;
    [InputParameter("Max contracts allowed (VERIFY)", 150, 1, 10, 1, 0)] public int MaxContractsAllowed = 1;

    [InputParameter("NY AM start ET", 200)] public string NyAmStart = "09:35";
    [InputParameter("NY AM end ET", 210)] public string NyAmEnd = "11:45";
    [InputParameter("NY PM enabled", 220)] public bool NyPmEnabled = true;
    [InputParameter("NY PM start ET", 230)] public string NyPmStart = "13:30";
    [InputParameter("NY PM end ET", 240)] public string NyPmEnd = "15:30";
    [InputParameter("London enabled", 250)] public bool LondonEnabled = false;
    [InputParameter("London start ET", 252)] public string LondonStart = "03:00";
    [InputParameter("London end ET", 254)] public string LondonEnd = "05:00";
    [InputParameter("Flatten time ET", 260)] public string FlattenTimeEt = "15:55";
    [InputParameter("News CSV path", 270)] public string NewsCsvPath = "";
    [InputParameter("News block before (min)", 272, 0, 60, 1, 0)] public int NewsBeforeMin = 2;
    [InputParameter("News block after (min)", 274, 0, 60, 1, 0)] public int NewsAfterMin = 3;

    [InputParameter("Max risk tier A USD", 300)] public double MaxRiskTierA = 250;
    [InputParameter("Max risk tier B USD", 305)] public double MaxRiskTierB = 200;
    [InputParameter("Max risk tier C USD", 310)] public double MaxRiskTierC = 150;
    [InputParameter("Halt headroom USD", 315)] public double HaltHeadroomUsd = 350;
    [InputParameter("Max daily loss USD", 320)] public double MaxDailyLossUsd = 350;
    [InputParameter("Max consecutive losses", 325, 1, 10, 1, 0)] public int MaxConsecutiveLosses = 2;
    [InputParameter("Max trades/day", 330, 1, 20, 1, 0)] public int MaxTradesPerDay = 4;
    [InputParameter("Daily profit cap USD", 335)] public double DailyProfitCapUsd = 450;
    [InputParameter("Giveback %", 340)] public double GivebackPercent = 40;
    [InputParameter("Loss cooldown (min)", 345, 0, 120, 1, 0)] public int LossCooldownMinutes = 10;
    [InputParameter("Win cooldown (min)", 350, 0, 120, 1, 0)] public int WinCooldownMinutes = 3;
    [InputParameter("Commission per side USD (VERIFY)", 355)] public double CommissionPerSideUsd = 2.5;
    [InputParameter("Microscalp warn %", 360)] public double MicroscalpWarnPercent = 20;
    [InputParameter("Microscalp halt %", 365)] public double MicroscalpHaltPercent = 35;

    [InputParameter("State path (blank = Documents/NQ_OrderFlow/state.json)", 900)] public string StatePath = "";
    [InputParameter("EMERGENCY FLATTEN", 999)] public bool EmergencyFlatten = false;

    private SessionClock? _clock;
    private LucidRiskGuard? _guard;
    private StateStore? _store;
    private RiskSettings _risk = new();
    private Timer? _timer;
    private int _consecutiveErrors;
    private DateTime _lastTickUtc;

    public override string[] MonitoringConnectionsIds => new[] { symbol?.ConnectionId ?? "" };

    public NQOrderFlowStrategy()
    {
        Name = "NQ OrderFlow LucidFlex";
        Description = "Phase 1 shell: clock + risk layer, no orders";
    }

    protected override void OnRun()
    {
        if (symbol is null || account is null || symbol.ConnectionId != account.ConnectionId)
        {
            Log("Symbol/Account missing or on different connections", StrategyLoggingLevel.Error);
            Stop();
            return;
        }
        symbol = Core.GetSymbol(symbol.CreateInfo());
        var root = SymbolRules.ExtractRoot(symbol.Name);
        if (!SymbolRules.IsAllowed(root, SymbolRules.ParseAllowed(AllowedRoots)))
        {
            Log($"Rejected symbol root '{root}' (allowed: {AllowedRoots})", StrategyLoggingLevel.Error);
            Stop();
            return;
        }
        if (!TryBuildSessionSettings(out var session, out var badField))
        {
            Log($"Invalid HH:mm in input '{badField}'", StrategyLoggingLevel.Error);
            Stop();
            return;
        }

        var log = new QuantowerLogSink(this);
        _risk = BuildRiskSettings();
        _clock = new SessionClock(session);
        var news = NewsBlackout.Load(string.IsNullOrWhiteSpace(NewsCsvPath) ? null : NewsCsvPath, _clock, NewsBeforeMin, NewsAfterMin, log);
        _store = new StateStore(ResolveStatePath());
        var loaded = _store.Load();
        _guard = LucidRiskGuard.ForFlexEvaluation(BuildAccountRules(), _risk, _clock, news, log, loaded.State?.Eval, loaded.State?.Day);
        if (loaded.Error is not null) _guard.HaltSafeMode(loaded.Error);

        LogWindowsInIst(log);
        symbol.NewLast += OnNewLast;
        _timer = new Timer(_ => OnTimer(), null, TimerPeriodMs, TimerPeriodMs);
        Log($"Started {_clock.Stamp(NowUtc())} | Phase 1: NO ORDERS", StrategyLoggingLevel.Trading);
    }

    protected override void OnStop()
    {
        _timer?.Dispose();
        _timer = null;
        if (symbol is not null) symbol.NewLast -= OnNewLast;
        SaveState();
    }

    protected override void OnRemove()
    {
        symbol = null;
        account = null;
    }

    protected override List<StrategyMetric> OnGetMetrics()
    {
        var m = base.OnGetMetrics();
        if (_guard is null || _clock is null || account is null) return m;
        var now = NowUtc();
        var equity = (decimal)account.Balance;
        var eval = _guard.Eval;
        var day = _guard.Day;
        m.Add("Clock", _clock.Stamp(now));
        m.Add("Equity / MLL floor", $"{equity:F2} / {eval.MllFloor:F2}");
        m.Add("Headroom / Tier", $"{eval.Headroom(equity):F2} / {_guard.TierFor(equity)}");
        m.Add("Day PnL / Trades / Loss streak", $"{day.RealizedPnl:F2} / {day.Trades} / {day.ConsecutiveLosses}");
        m.Add("Eval profit / target", $"{eval.TotalProfit:F2} / {ProfitTargetUsd:F0}");
        m.Add("Consistency (best day share)", $"{eval.ConsistencyRatio * 100:F1}%");
        m.Add("Microscalp share", $"{eval.MicroscalpPercent:F1}%");
        m.Add("Flags", $"breached={_guard.IsBreached} dayHalt={_guard.DailyHaltActive} manual={_guard.ManualReenableRequired}");
        m.Add("Active window", _clock.ActiveWindow(now)?.Name ?? "none");
        if (_clock.NextWindowOpen(now) is { } nx)
            m.Add("Next window", $"{nx.Window.Name} in {(nx.OpensUtc - now):d\\.hh\\:mm} (IST {_clock.ToIst(nx.OpensUtc):ddd HH:mm})");
        m.Add("Last tick age (s)", _lastTickUtc == default ? "n/a" : $"{(now - _lastTickUtc).TotalSeconds:F1}");
        return m;
    }

    private void OnNewLast(Symbol s, Last last) => Guarded(() => _lastTickUtc = last.Time);

    private void OnTimer() => Guarded(() =>
    {
        var now = NowUtc();
        if (EmergencyFlatten) Log("EmergencyFlatten is set; Phase 1 holds no positions (Phase 5 wires the flatten)", StrategyLoggingLevel.Trading);
        if (_guard!.ShouldFlattenNow(now)) { /* Phase 5: ExecutionEngine.FlattenAll() */ }
    });

    private void Guarded(Action action)
    {
        try
        {
            action();
            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            Log($"Handler error: {ex}", StrategyLoggingLevel.Error);
            if (++_consecutiveErrors >= _risk.SafeModeExceptionCount) _guard?.HaltSafeMode("Repeated handler exceptions");
        }
    }

    private static DateTime NowUtc() => Core.Instance.TimeUtils.DateTimeUtcNow;

    private void SaveState()
    {
        try
        {
            if (_guard is not null) _store?.Save(new PersistedState(1, _guard.Eval, _guard.Day, null));
        }
        catch (Exception ex)
        {
            Log($"State save failed: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private string ResolveStatePath() => string.IsNullOrWhiteSpace(StatePath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NQ_OrderFlow", "state.json")
        : StatePath;

    private bool TryBuildSessionSettings(out SessionSettings settings, out string badField)
    {
        settings = SessionSettings.Default();
        var fields = new (string Name, string Value)[]
        {
            (nameof(NyAmStart), NyAmStart), (nameof(NyAmEnd), NyAmEnd), (nameof(NyPmStart), NyPmStart),
            (nameof(NyPmEnd), NyPmEnd), (nameof(LondonStart), LondonStart), (nameof(LondonEnd), LondonEnd),
            (nameof(FlattenTimeEt), FlattenTimeEt),
        };
        var parsed = new Dictionary<string, TimeSpan>();
        foreach (var (name, value) in fields)
        {
            if (!TimeSpan.TryParseExact(value, HhMm, null, out var t)) { badField = name; return false; }
            parsed[name] = t;
        }
        settings = settings with
        {
            Windows = new[]
            {
                new SessionWindow("NY_AM_KILLZONE", parsed[nameof(NyAmStart)], parsed[nameof(NyAmEnd)], true, 0),
                new SessionWindow("NY_PM", parsed[nameof(NyPmStart)], parsed[nameof(NyPmEnd)], NyPmEnabled, 5),
                new SessionWindow("LONDON_OPEN", parsed[nameof(LondonStart)], parsed[nameof(LondonEnd)], LondonEnabled, 0),
            },
            FlattenTimeEt = parsed[nameof(FlattenTimeEt)],
            NewsBeforeMin = NewsBeforeMin,
            NewsAfterMin = NewsAfterMin,
        };
        badField = "";
        return true;
    }

    private AccountRules BuildAccountRules() => new()
    {
        InitialBalance = (decimal)InitialBalance,
        MllDistanceUsd = (decimal)MllDistanceUsd,
        MllLockFloorUsd = (decimal)MllLockFloorUsd,
        ProfitTargetUsd = (decimal)ProfitTargetUsd,
        ConsistencyCapPercent = (decimal)ConsistencyCapPercent,
        ConsistencyEarlyStopPercent = (decimal)ConsistencyEarlyStopPercent,
        MaxContractsAllowed = MaxContractsAllowed,
    };

    private RiskSettings BuildRiskSettings() => new()
    {
        MaxRiskTierA = (decimal)MaxRiskTierA,
        MaxRiskTierB = (decimal)MaxRiskTierB,
        MaxRiskTierC = (decimal)MaxRiskTierC,
        HaltHeadroomUsd = (decimal)HaltHeadroomUsd,
        MaxDailyLossUsd = (decimal)MaxDailyLossUsd,
        MaxConsecutiveLosses = MaxConsecutiveLosses,
        MaxTradesPerDay = MaxTradesPerDay,
        DailyProfitCapUsd = (decimal)DailyProfitCapUsd,
        GivebackPercent = (decimal)GivebackPercent,
        LossCooldownMinutes = LossCooldownMinutes,
        WinCooldownMinutes = WinCooldownMinutes,
        CommissionPerSideUsd = (decimal)CommissionPerSideUsd,
        MicroscalpWarnPercent = (decimal)MicroscalpWarnPercent,
        MicroscalpHaltPercent = (decimal)MicroscalpHaltPercent,
    };

    private void LogWindowsInIst(ILogSink log)
    {
        var etToday = _clock!.ToEt(NowUtc()).Date;
        foreach (var w in _clock.Settings.Windows.Where(w => w.Enabled))
        {
            var startUtc = _clock.EtToUtc(etToday + w.StartEt);
            var endUtc = _clock.EtToUtc(etToday + w.EndEt);
            log.Info($"{w.Name}: {w.StartEt:hh\\:mm}-{w.EndEt:hh\\:mm} ET = {_clock.ToIst(startUtc):HH:mm}-{_clock.ToIst(endUtc):HH:mm} IST (today)");
        }
    }

    private sealed class QuantowerLogSink : ILogSink
    {
        private readonly NQOrderFlowStrategy _s;
        public QuantowerLogSink(NQOrderFlowStrategy s) => _s = s;
        public void Info(string m) => _s.Log(m, StrategyLoggingLevel.Info);
        public void Trading(string m) => _s.Log(m, StrategyLoggingLevel.Trading);
        public void Error(string m) => _s.Log(m, StrategyLoggingLevel.Error);
    }
}
```
`AccountRules` and `RiskSettings` are positional records. The object-initializer syntax above works because positional record parameters generate `init` properties.
- [ ] **Step 3:** `dotnet build "QT MNQ Orderflow Algo.csproj" -c Release`. Expected: 0 errors. Fix any warnings that can be fixed.
- [ ] **Step 4:** Write `docs/ASSUMPTIONS.md` with one bullet per assumption:
  - Values to VERIFY on the Lucid dashboard: MLL lock floor 25,000, MaxContractsAllowed 1, commission $2.50/side.
  - The trading date rolls at 18:00 ET.
  - The consistency early-stop denominator is `max(total, target)`.
  - Recurring 08:30/10:00 news blocks always apply, and FOMC dates come only from the CSV.
  - Root extraction uses a month-code regex.
  - Phase 1 equity is `Account.Balance`; Phase 5 adds open PnL.
  - EOD MLL processing (`OnEndOfDay`) gets wired in Phase 5 via a 16:45 ET balance snapshot. Until then the floor only reflects persisted state.
- [ ] **Step 5: Manual smoke test in Quantower:** the Release build lands in `Settings\Scripts\Strategies`. Add it in Strategy Runner with an NQ front-month on a sim account, and check:
  - The metrics show the clock, tier A, headroom 1000, the next window with IST time, and the tick age updating.
  - An MNQ symbol with `AllowedRoots=NQ` logs a rejection and stops.
  - Stop/start keeps `state.json`.
  - Corrupting `state.json` and restarting shows `manual=True` and the SAFE MODE reason.
  - The log shows the IST window table.
- [ ] **Step 6:** Run `dotnet test`: all green. Then `git add -A && git commit -m "feat: Phase 1 strategy shell with metrics, persistence, docs; no orders"`

---

## Verification (end of Phase 1)

1. `dotnet test tests/NQOrderFlow.Tests`: all green.
2. `dotnet build "QT MNQ Orderflow Algo.csproj" -c Release`: 0 errors.
3. The Quantower smoke checklist (Task 9, Step 5) passes.
4. `git grep PlaceOrder` returns nothing, because Phase 1 must not trade.

## Deferred to later plans
- Phase 2: TickNormalizer, Footprint, SessionProfile, OrderFlowFeatures, backfill.
- Phase 3: Swing/LevelMap/Regime.
- Phase 4: setups, Scorer, paper journal.
- Phase 5: ExecutionEngine, TradeManager, flatten/kill switches, EOD snapshot → `OnEndOfDay`, open-PnL equity, alerts.
- Phase 6: tester/walk-forward, VPS_SETUP, TUNING_GUIDE, README.
