# Assumptions (Phase 1 and 2)

- Values to VERIFY on the Lucid dashboard: MLL lock floor 25,000, MaxContractsAllowed 1, commission $2.50/side. Each is marked `// VERIFY` in code and is an input.
- The trading date rolls at 18:00 ET.
- The consistency early-stop denominator is `max(total, target)`.
- Recurring news blocks (default 08:30/10:00 ET, input "Recurring news times ET") always apply, and FOMC dates come only from the news CSV.
- Root extraction uses a month-code regex on `Symbol.Name` (exact root match, so `MNQ` != `NQ`).
- Phase 1 equity is `Account.Balance`; Phase 5 adds open PnL.
- EOD MLL processing (`OnEndOfDay`) gets wired in Phase 5 via a 16:45 ET balance snapshot. Until then the floor only reflects persisted state.
- `Last.Time` is treated as UTC when computing tick age.
- Metrics use the non-obsolete `OnInitializeMetrics(Meter)` API, which is numeric-only; text status goes to the strategy log on change (see `docs/API_NOTES.md`).
- Safe mode (repeated handler exceptions, or an `OnRun` exception) is in-memory only, so a restart clears it. That restart is the explicit manual re-enable. A state load failure is different: it persists by design, because the bad file is kept and the strategy stays fail-closed until the owner fixes or removes it.
- A saved `Day.Date` more than 1 day after the current UTC date is rejected on load (fail closed). Tomorrow is allowed because the trading date rolls at 18:00 ET.

## Phase 2 data

- VWAP and the session profile are RTH-anchored (09:30-16:00 ET, inputs "RTH open/close ET").
- Prior-day high, low, close and the prior value area come from RTH.
- The overnight range runs 18:00-09:30 ET.
- The value area grows one traded level at a time.
- Identical prints are kept (`DropIdenticalPrints` = false), because legitimate same-millisecond prints exist.
- The bad-tick filter uses a confirm count.
- DeltaFlip uses closed 20 s buckets.
- Exchange holidays are not modeled in `PreviousTradingDate`. On the day after a holiday, "prior day" levels come from the holiday-shortened or older session.
- Exhaustion, DeltaDivergence and TrappedTraders are deferred to Phase 3.
- DOM tracking is deferred (feature flag in Phase 4+).
- Backfill starts at the prior trading session's start. Live ticks that arrive during backfill are buffered (max 200,000) and replayed only if newer than the last backfilled tick; on buffer overflow or backfill failure, warmup is never marked complete (data stays unhealthy until restart). Live quotes are ignored while backfilling.
- `HistoryItemLast.TimeLeft` is treated as UTC, like `Last.Time`.
- Bar close grace: the timer closes a 1m/5m bar only once `now - BarCloseGraceMs` (default 1500 ms) has passed the bar end, so trades stamped just before the boundary but delivered slightly late still land in the bar and in CVD/profile. Trades that still arrive after the bar closed are dropped from the bars and counted (`late_trades_1m`); `BucketSeries` also drops a trade older than its current bucket.
- Future-skew guard: on the live path a print stamped more than `MaxFutureSkewSeconds` (default 5) ahead of wall-clock UTC is rejected before the normalizer and counted (`future_ticks`), so it cannot push the monotonic-time filter forward and lock out the feed. Health is also unhealthy if the last accepted tick is more than that far ahead of now. Backfill and replay use the unchecked path.
- `BadTickConfirmCount` must be >= 2 (1 would disable the jump filter). Non-finite (NaN/Infinity) double inputs stop the strategy at start with the field name logged.

## Owner smoke test

Release build lands in `C:\Quantower\Settings\Scripts\Strategies\QT MNQ Orderflow Algo`. Add "NQ OrderFlow LucidFlex" in Strategy Runner with an NQ front-month on a sim account, then check:

- [ ] Metrics show tier 0 (A), headroom 1000, minutes to next window, and last tick age updating; the log shows the clock stamp and the next window with IST time.
- [ ] An MNQ symbol with `AllowedRoots=NQ` logs a rejection and stops.
- [ ] Stop/start keeps `state.json` (Documents/NQ_OrderFlow/state.json by default).
- [ ] Corrupting `state.json` and restarting shows `manual=True` (Manual re-enable = 1) and the SAFE MODE reason in the log.
- [ ] The log shows the IST window table.
- [ ] Tick age stays < ~2 s on a live feed, and the first-tick log line shows `Last.Time` Kind (expect Utc).
- [ ] An invalid HH:mm input (e.g. `9:5x`) logs the field name and stops.
- [ ] Confirm how gauge names/values render in the metrics panel (new `OnInitializeMetrics` API).
- [ ] After start, the log shows "Backfill done … replayed N live ticks" and `data_warm` becomes 1.
- [ ] Pick three closed 1m bars in the RTH session. Compare `V`, `Δ`, `POC`, `H` and `L` from the `BAR` log with Quantower's Cluster/Footprint chart for the same bars. Expect an exact volume match; delta should match within the fallback rate.
- [ ] Compare `VWAP` and `dPOC/VAH/VAL` with Quantower's VWAP and Volume Profile indicators set to an RTH session. Expect a close match; value-area differences can come from the algorithm.
- [ ] `fallback_pct` stays below 5% on Rithmic. If it is higher, the aggressor mapping is wrong.
- [ ] Disconnect the network for 10 s. `data_healthy` goes to 0, then back to 1 when ticks resume.
- [ ] `late_trades_1m` and `future_ticks` stay ~0 on the live feed.
