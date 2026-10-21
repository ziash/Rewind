# Rewind

**Rewind the market and trade it again by hand — with TM managing your trades.**

A chart in the browser replays past prices: scroll back, go to any date, switch between D1, H4, H1, M15 and M5, step or play forward. You open trades on it as you would in cTrader. The **TM bot** from the [Trader](https://github.com/ziash/Trader) project runs beside it, connects to Rewind as if it were a broker, and mirrors, hedges and protects those trades exactly as it does on the demo accounts.

| Item | Value |
|---|---|
| Status | **Steps 1–4 built** — history downloader, chart, simulated broker with order panel and sessions, and TM trading on it. Step 5 not started |
| Specification | [docs/spec.md](docs/spec.md) — the single source of truth (goal, design, build steps, open questions, history) |
| Price history | Dukascopy free data feed: 24 months, AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD, bid and ask; downloaded by Rewind's own tool |
| Runs on | Ubuntu, .NET 10; GUI at `http://localhost:5180` in the browser; broker side for TM at `localhost:5036` |
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
dotnet build -c Release
dotnet src/Rewind.Download/bin/Release/net10.0/rewind-download.dll            # 24 months, six symbols (about 5 h; resumes)
dotnet src/Rewind.Download/bin/Release/net10.0/rewind-download.dll --help
```

Output: `~/rewind/history/<SYMBOL>/<yyyy-MM>.m5.csv` (and `.m1.csv.gz`), opens in Google Sheets. Personal use only — never committed (except two small test sample files).

## The chart (step 2)

```bash
dotnet run --project src/Rewind          # then open http://localhost:5180
```

Symbol and timeframe at the top, replay time in UTC / London / New York, **Go to** a date, **Session** (and **New…**). Bottom: **Step** (→ key), **Play / Pause** (space), speed, **Next day**, **London open**, **New York open**. Right: the **order panel** (SELL / BUY, lots, SL, TP — ⌖ picks a price on the chart), levels (**+ Level** then click the chart, or Alt+click), messages. Under the chart: balance, equity, open P/L, commission, positions and orders; drag SL / TP lines on the chart to move them. Nothing after the replay time is ever shown.

Each session keeps its own account and results in `~/rewind/sessions/<name>/` (`trades.csv`, `events.csv` open in Google Sheets). Go to a date or change session only with no open positions.

## TM on Rewind (step 4)

With Rewind running, start the real TM from the Trader repo against it:

```bash
~/rewind/tm/run-tm.sh          # Ctrl+C stops it; the header shows "TM on" while it is connected
```

Rewind made this script, TM's fake secrets and its `tm.json` in `~/rewind/tm/`, and its TLS certificate in `~/rewind/certs/`, on first start. Open a trade in the browser and TM mirrors and hedges it like on the demo accounts; step or play (slowly — TM looks once a second) to see it manage the hedges.

## Build steps

1. History downloader (`rewind-download`)
2. Chart: timeframes, scroll, go to, step / play, levels
3. Simulated broker and order panel (manual trades, sessions, results CSV)
4. Open API server so TM can connect (plus three opt-in settings in Trader)
5. Exact replay with TM, results in R

Details and status: [docs/spec.md §11](docs/spec.md#11-build-steps).
