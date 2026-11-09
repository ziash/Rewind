# Rewind

**Rewind the market and trade it again by hand — with TM managing your trades.**

A chart in the browser replays past prices: scroll back, go to any date, switch between D1, H4, H1, M15 and M5, step or play forward. You open trades on it as you would in cTrader. The **TM bot** from the [Trader](https://github.com/ziash/Trader) project runs beside it, connects to Rewind as if it were a broker, and mirrors, hedges and protects those trades exactly as it does on the demo accounts.

| Item | Value |
|---|---|
| Status | **Steps 1–6 built** — history downloader, chart, simulated broker with order panel and sessions, TM trading on it, exact mode and TM's results per leg, pending orders and trailing stops |
| Specification | [docs/spec.md](docs/spec.md) — the single source of truth (goal, design, build steps, open questions, history) |
| Price history | Dukascopy free data feed: about 25 months, bid and ask, 19 symbols: Rewind's first six (replay and TM practice) and 13 more for Trader's Scout Lens; downloaded by Rewind's own tool. What is on disk, symbol by symbol: [spec §4.1](docs/spec.md) |
| Runs on | Ubuntu or Windows, .NET 10; GUI at `http://localhost:5180` in the browser; broker side for TM at `localhost:5036` |
| Data | `~/rewind/` (history, sessions, config) — outside the repo |
| Related | [Trader](https://github.com/ziash/Trader) — the trading bots. Rewind shares no code with it (small pieces are copied) and changes nothing there except three opt-in settings (spec §9), done in Trader on the owner's go |

## How it works

```
Browser chart + order panel  ──►  Rewind (replay clock + simulated broker)  ◄──  TM (unchanged, from Trader)
                                         ▲
                     ~/rewind/history/ ──┘  (filled once by rewind-download from Dukascopy)
```

## History download (step 1)

```bash
dotnet build src/Rewind.Download -c Release -o ~/rewind/bin/download   # its own copy: rebuilding the repo cannot disturb a running download
~/rewind/bin/download/rewind-download              # 24 months, all 19 symbols (resumes; a re-run fetches missing days)
~/rewind/bin/download/rewind-download --build-only # rebuild the CSV files from the downloaded files
~/rewind/bin/download/rewind-download --help
```

Output: `~/rewind/history/<SYMBOL>/<yyyy-MM>.m5.csv` (and `.m1.csv.gz`), opens in Google Sheets. Personal use only — never committed (except two small test sample files).

## Scout's orders in R (step 7, `rewind-lab`)

For Scout Lens in the Trader project (spec §7.1). Normally run through Trader's `scripts/lens-replay.sh`; by hand:

```bash
dotnet build src/Rewind.Lab -c Release -o ~/rewind/bin/lab
~/rewind/bin/lab/rewind-lab candles --symbols EURUSD          # → ~/rewind/lab/candles/EURUSD-H1.csv, -H4, -D1
~/rewind/bin/lab/rewind-lab outcomes ~/tradebot/lens/orders/*.orders.csv   # → ~/rewind/lab/outcomes.csv
```

## The chart (step 2)

```bash
dotnet run --project src/Rewind          # then open http://localhost:5180
```

Symbol and timeframe at the top, replay time in UTC / London / New York, **Go to** a date, **Session** (and **New…**). Bottom: **Step** (→ key), **Play / Pause** (space), speed, **Next day**, **London open**, **New York open**. Right: the **order panel** (Market / Limit / Stop, SELL / BUY, order price, lots, SL, trailing stop, TP — ⌖ picks a price on the chart), levels (**+ Level** then click the chart, or Alt+click), messages. Under the chart: balance, equity, open P/L, commission, positions and orders; drag SL / TP lines on the chart to move them. Nothing after the replay time is ever shown.

Each session keeps its own account and results in `~/rewind/sessions/<name>/` (`trades.csv`, `events.csv` open in Google Sheets). Go to a date or change session only with no open positions.

## TM on Rewind (step 4)

With Rewind running, start the real TM from the Trader repo against it:

```bash
~/rewind/tm/run-tm.sh          # Ctrl+C stops it; the header shows "TM on" while it is connected
```

On Windows, in PowerShell (`~\rewind` is `C:\Users\<name>\rewind`; the Trader repo is expected in `~\code\Trader`):

```powershell
powershell -ExecutionPolicy Bypass -File "$HOME\rewind\tm\run-tm.ps1"
```

Rewind made these scripts, TM's fake secrets (with `CTRADER_CA_FILE`, through which TM trusts Rewind's certificate; nothing is installed in the system) and its `tm.json` in `~/rewind/tm/`, and its TLS certificate in `~/rewind/certs/`, on first start (if Rewind's log says `tm.json` misses symbols, add them to its `"symbols"`). Open a trade in the browser and TM mirrors and hedges it like on the demo accounts.

**Exact mode (step 5)** — the **Exact** button in the footer, on by default: while TM is connected, each price step of a symbol with an open position or order waits until TM has had its turn (about 5 s per candle; the footer says "Exact: waiting for TM"), so TM acts as it would live at any speed. Flat stretches and other symbols go at full speed; the skips are never paced. Off = Fast: TM may act late.

**TM's results** — `tm-trades.csv` in the session folder: one row per leg (your main, TM's mirror) once it and its hedge are closed — 1R, hedge cycles, break-even, the largest loss in R (TM warns at 2R, closes at 3R), who closed it, result in R and USD (spec §8).

## Build steps

1. History downloader (`rewind-download`)
2. Chart: timeframes, scroll, go to, step / play, levels
3. Simulated broker and order panel (manual trades, sessions, results CSV)
4. Open API server so TM can connect (plus three opt-in settings in Trader)
5. Exact mode with TM, TM's results per leg (`tm-trades.csv`)
6. Pending orders by hand (buy/sell limit and stop, with SL / TP) and trailing stops

Details and status: [docs/spec.md §11](docs/spec.md#11-build-steps).
