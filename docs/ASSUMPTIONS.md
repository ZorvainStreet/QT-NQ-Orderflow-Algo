# Assumptions (Phase 1)

- Values to VERIFY on the Lucid dashboard: MLL lock floor 25,000, MaxContractsAllowed 1, commission $2.50/side. Each is marked `// VERIFY` in code and is an input.
- The trading date rolls at 18:00 ET.
- The consistency early-stop denominator is `max(total, target)`.
- Recurring 08:30/10:00 ET news blocks always apply, and FOMC dates come only from the news CSV.
- Root extraction uses a month-code regex on `Symbol.Name` (exact root match, so `MNQ` != `NQ`).
- Phase 1 equity is `Account.Balance`; Phase 5 adds open PnL.
- EOD MLL processing (`OnEndOfDay`) gets wired in Phase 5 via a 16:45 ET balance snapshot. Until then the floor only reflects persisted state.
- `Last.Time` is treated as UTC when computing tick age.
- Metrics use the non-obsolete `OnInitializeMetrics(Meter)` API, which is numeric-only; text status goes to the strategy log on change (see `docs/API_NOTES.md`).

## Owner smoke test

Release build lands in `C:\Quantower\Settings\Scripts\Strategies\QT MNQ Orderflow Algo`. Add "NQ OrderFlow LucidFlex" in Strategy Runner with an NQ front-month on a sim account, then check:

- [ ] Metrics show tier 0 (A), headroom 1000, minutes to next window, and last tick age updating; the log shows the clock stamp and the next window with IST time.
- [ ] An MNQ symbol with `AllowedRoots=NQ` logs a rejection and stops.
- [ ] Stop/start keeps `state.json` (Documents/NQ_OrderFlow/state.json by default).
- [ ] Corrupting `state.json` and restarting shows `manual=True` (Manual re-enable = 1) and the SAFE MODE reason in the log.
- [ ] The log shows the IST window table.
- [ ] An invalid HH:mm input (e.g. `9:5x`) logs the field name and stops.
- [ ] Confirm how gauge names/values render in the metrics panel (new `OnInitializeMetrics` API).
