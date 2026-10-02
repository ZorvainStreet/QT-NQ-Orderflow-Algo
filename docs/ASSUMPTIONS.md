# Assumptions (Phase 1)

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
