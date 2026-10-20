# Rewind — specification

**Rewind the market and trade it again by hand.** A chart in your browser replays past prices (scroll back, go to any date, switch timeframes, step or play forward); you open trades on it as you would in cTrader; and **the real TM** (from the Trader project) runs on Ubuntu, connects to Rewind as if it were a broker, and mirrors, hedges and protects those trades exactly as it does on the demo accounts.

| Item | Value |
|---|---|
| Status | **Step 1 built** (history downloader, 20-Oct-2026); full 24-month download running; steps 2–5 not started (§11) |
| Document version | v0.2 (20-Oct-2026) — history in §15 |
| Owner | Zeeshan |
| Repository | [github.com/ziash/Rewind](https://github.com/ziash/Rewind) (private) — separate from [Trader](https://github.com/ziash/Trader) |
| Runs on | The Ubuntu laptop; browser GUI at `http://localhost:5180`; the broker side at `localhost:5036` |
| Data folder | `~/rewind/` (history, sessions, config, certificate) — outside the repo, separate from `~/tradebot/` |
| Price history | Dukascopy (Swiss bank/broker) free data feed: 24 months, six symbols, bid and ask; downloaded by Rewind's own tool (§4) |

---

## 1. Idea

Testing TM on the demo accounts only works in real time: a trade idea takes hours or days to play out. Rewind lets the owner practise on **any past period**, at any speed, with TM doing its real work:

1. Look at the **D1** chart of, say, March 2025, mark the level where you want to enter.
2. Switch to **M5**, step forward bar by bar until the entry looks right, click **Sell**.
3. TM (running beside it) sees the new position, opens the mirror, places both hedges, removes the stop-loss — all visible as lines on the chart.
4. Keep stepping or playing; hedges fill, stops move, break-even at 1R, 2R/3R — exactly as TM would do it live.
5. At the end, a CSV of the session's trades (R, USD, hedge cycles) for Google Sheets.

Terms:
- **Replay cursor** — the moment in the past the replay is at. Everything after it is hidden (no peeking ahead).
- **Timeframe** — how much time one candle covers: M5 = 5 minutes, H1 = 1 hour, D1 = 1 day.
- **Bid / ask** — the price you can sell at / buy at. The difference is the **spread**.

## 2. Scope

| In (version 1) | Out (for now) |
|---|---|
| Six symbols: AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD | Other symbols (adding one = downloading its history) |
| 24 months of history, bid and ask | Ticks (every single price change) |
| Chart timeframes M5, M15, H1, H4, D1 | M1 chart (data is downloaded anyway, §4; can be added) |
| Market orders by hand, with stop-loss / take-profit; moving them; closing all or part | Manual pending orders (limit / stop) — TM's own stop orders *are* in |
| One simulated account (USD, hedging) | Several accounts, netting accounts |
| Commission per lot | Swap (overnight financing), margin calls |
| TM connects and works unchanged | Any other bot (possible later, same way) |

## 3. How it fits together

```
 Browser (chart, order panel)                         TM (Trader project, unchanged code)
        │  http://localhost:5180                              │  Open API: Protobuf over TLS
        ▼                                                     ▼  localhost:5036
 ┌──────────────────────────── Rewind (one .NET program) ────────────────────────────┐
 │  Web GUI server ── Replay clock ── Simulated broker (account, positions, orders)  │
 │                         │                    │                                    │
 │                  History files         Open API server (speaks cTrader's protocol)│
 └───────────────────────────────────────────────────────────────────────────────────┘
        ▲
 ~/rewind/history/  ← filled once by `rewind-download` from the Dukascopy data feed
```

- **One simulated broker, two doors.** The owner's clicks in the browser and TM's Open API messages act on the *same* account. A position opened in the browser has **no label** (a manual trade, as in cTrader); TM's orders carry TM's labels.
- **TM is not copied into Rewind.** It runs as its own program from the Trader repo, so what is tested is exactly what trades on demo.
- **Rewind does not use Trader's code.** It is a separate repository and solution. Where a small piece is needed (e.g. the CSV format), it is **copied** into Rewind with a note of where it came from, so changes on either side never disturb the other.

## 4. Price history (download, option c)

Rewind's own downloader, **`rewind-download`** (a small C# console program in this repo), fetches Dukascopy's free historical data files and turns them into CSV.

| Item | Value |
|---|---|
| Source | Dukascopy data feed, one compressed file per symbol, side and day: `https://datafeed.dukascopy.com/datafeed/<SYMBOL>/<yyyy>/<MM-1>/<dd>/<BID\|ASK>_candles_min_1.bi5` — month counted from 00 (confirmed: `2026/09/12` is Monday 12-Oct-2026). HTTPS only (plain HTTP is redirected to it) |
| Why M1 files | The feed offers 1-minute, 1-hour and 1-day candle files, not 5-minute ones; M5 and larger are built from M1. Keeping the M1 files costs nothing extra and allows a more exact replay later (§6.3) |
| Format of a file | LZMA-compressed (the 7-Zip method, "lzma alone": 5 property bytes, 8 bytes size, data); inside, 1,440 records of 24 bytes, big-endian: seconds since 00:00 UTC, **open, close, low, high** (whole numbers = price × factor), volume (float). Decoded with the SharpCompress library (MIT licence, NuGet). Price factors: 100,000 (EURUSD, EURGBP), 1,000 (AUDJPY, GBPJPY), 10 (BTCUSD, ETHUSD) — checked against IC Markets' H1 candles of 14-Oct-2026: all six within 0.06 % |
| Symbols | AUDJPY, EURGBP, EURUSD, GBPJPY, BTCUSD, ETHUSD (Dukascopy uses the same names). Forex Saturdays are not downloaded (market closed) |
| Period | 24 months back from the last complete day (first run: 1-Oct-2024 → 19-Oct-2026); re-running adds the missing days |
| Amount | 8,120 files of about 11–15 KB (≈ 100 MB) for 1-Oct-2024 → 19-Oct-2026 |
| Politeness | **One download at a time**, at least 1 s apart (about 2.3 s per file in practice → about 5 hours for the full history). Dukascopy answers "429 Too Many Requests" to bursts (seen while testing); then the downloader waits (the server's Retry-After, else 1 min, doubling up to 15 min, 8 tries). Every file is kept in a raw cache (`~/rewind/history/raw/`), so a re-run never fetches a file twice and resumes after an interruption |
| Clean-up | Minutes in which neither side traded (volume 0) are dropped; gaps are kept as gaps. A file whose records do not fit together (low above open/close, etc.) is refused and not kept |
| Output | `~/rewind/history/<SYMBOL>/<yyyy-MM>.m1.csv.gz` (compressed) and `<yyyy-MM>.m5.csv` — one file per month |
| CSV columns | `time_utc, bid_open, bid_high, bid_low, bid_close, ask_open, ask_high, ask_low, ask_close, volume` (volume = the bid side's, in Dukascopy's units) — same CSV rules as the Trader project (UTF-8, `.` decimals, UTC times as `yyyy-MM-dd HH:mm:ss`, opens in Google Sheets) |
| Check | After each run: a summary per symbol (days, candles, price range, gaps over 1 hour — for forex outside the weekend close —, spread at close: smallest / median / 99 % / largest). Exit code 1 if days are missing |
| Run | `rewind-download` (all), `--from` / `--to` / `--symbols` for a part, `--build-only` to rebuild the CSV files from the cache, `--interval-ms` for the pause. Code: `src/Rewind.Download`; tests: `tests/Rewind.Download.Tests` (two real day files as samples) |

Things to know:
- Dukascopy's prices are **very close to, not identical with**, IC Markets' or Pepperstone's — fine for practising and for testing TM's behaviour. Their spreads also differ; Rewind can instead use **fixed spreads per symbol** (e.g. the medians the Audit Bot measured) — decision in §13.
- **Personal use only:** the downloaded history is never committed to Git or shared (`.gitignore` excludes it) — the only exception is two EURUSD day files (22 KB) kept as test samples in this private repository. Dukascopy's own terms for the feed could not be found online yet (§13).
- The download is the **only** time Rewind talks to the internet.

## 5. The chart (browser GUI)

Built with **Lightweight Charts**, TradingView's free open-source chart library (Apache 2.0 licence; it asks for a TradingView attribution link on the page), served from Rewind itself (no internet needed when running).

| Feature | Detail |
|---|---|
| Candles | Bid candles, as cTrader shows them; a thin line for the current ask (optional) |
| Timeframes | M5, M15, H1, H4, D1 — switching keeps the replay cursor; D1 days start at 00:00 UTC (to be compared with cTrader's D1 in step 2) |
| No peeking | Candles after the replay cursor are not sent to the browser; the last candle of each timeframe is drawn **as far as it has formed** (the D1 candle of today grows bar by bar as you step on M5) |
| Scroll and zoom | Mouse wheel / drag, as in TradingView; scrolling back never moves the cursor |
| Go to | Date and time box (UTC, with New York and London time shown next to it); jumping is allowed only when the account is flat (§8) |
| Replay controls | **Step** (one M5 bar), **Play / Pause**, speed (§6.2), and "step to next day / next London open / next New York open" |
| Levels | Horizontal lines you place (e.g. on D1) stay on every timeframe — the way to carry an entry idea from D1 down to M5 |
| Symbol switch | One symbol on screen at a time; all six move with the same cursor; positions on other symbols keep running |
| Order panel | Buy / Sell at market; lots; optional stop-loss and take-profit (typed in, or dragged on the chart); close a position (all or part); close all |
| On the chart | Every position (entry line, SL, TP) and every pending order, coloured by role: yours, TM's mirror, TM's hedges; label shown on hover |
| Tables | Positions and orders (with label, side, lots, price, SL, TP, open P/L in USD and pips); account: balance, equity, open P/L, commission paid |
| Messages | Broker events (filled, stop hit, cancelled) as a list with replay time; TM's own pop-ups keep coming through TM's notifications on the desktop |

## 6. Replay clock and speed

### 6.1 Two clocks

TM uses the computer's real clock; the replay is somewhere in the past. To keep TM unchanged, **Rewind speaks to TM in real time**: every time it sends TM (position opened, deal filled, spot price) is "now", and when TM asks for past candles (for its default hedge distance: the last 14 daily candles) Rewind moves the request into replay time and the answer back again. TM therefore sees a market that behaves as if it were live.

Consequences:
- TM's **news and Friday-close pop-ups** follow the real clock and do **not** fire for replay dates (open item §13).
- TM's log files show real times; Rewind's session log shows both.

### 6.2 Speed and keeping TM in step

The broker side (fills of TM's stop orders, stop-losses, take-profits) is exact at any speed: Rewind checks every price step itself. But TM's **own** decisions (arming the hedge exit, break-even at 1R, 2R/3R) take up to about 1 second of real time, because TM looks once a second. So:

| Mode | How it plays | TM |
|---|---|---|
| **Exact** (default while any position is open) | Each price step stays at least 1.2 s on screen | Has seen every step: same result as live |
| **Fast** | As fast as the speed setting | May act late — like a fast real market |
| Flat account | Always as fast as the speed setting | Nothing to manage |

Speed setting: 1, 2, 5, 10, 30, 60 M5 bars per second (Fast mode); a step is always possible.

### 6.3 Price path inside a candle

A 5-minute candle only gives open, high, low, close. Rewind walks each M5 bar through **4 price steps**: open → low → high → close for a rising candle, open → high → low → close for a falling one. Ask = bid + that bar's spread. With a small R, several of TM's levels can lie inside one candle, so the order of events inside it is a guess; the M1 files allow an exact mode later (5 × 4 steps per M5 bar).

## 7. The simulated broker

| Item | Rule |
|---|---|
| Account | One account, **USD, hedging** (several positions per symbol, both directions), start balance 5,000 USD, leverage 1:500 (like the IC Markets demo) — values in `~/rewind/config/rewind.json` |
| Symbols | Details (digits, pip size, lot size, volume step, minimum volume) copied once from the IC Markets demo into `config/symbols.json` |
| Market order | Buy at the ask, sell at the bid of the current price step |
| Stop order (TM's hedges) | Buy-stop fills when the ask reaches it, sell-stop when the bid reaches it, **at the order price**; if the price jumped past it between two steps (gap), at the step's price — the slippage of a real jump |
| Stop-loss / take-profit | Same rule, on the bid for longs and the ask for shorts |
| Amend / cancel | Immediate; refused with cTrader's error codes where cTrader would refuse (e.g. a stop-loss on the wrong side of the price: `TRADING_BAD_STOPS`) |
| Commission | Per lot and side, in USD, per symbol (IC Markets Raw: about 3.50 USD per lot per side for forex — **to confirm**, §13); crypto per the broker's rule |
| P/L in USD | Converted with the replay's own prices (e.g. AUDJPY profit in JPY → USD with USDJPY… needs USDJPY history too, or a fixed rate — §13) |
| Not simulated | Swap, margin call / stop-out, requotes |

## 8. Sessions

- A **session** = a starting date, the account, all trades and events, the replay cursor. Saved in `~/rewind/sessions/<name>/` after every change; reopening continues where it stopped (TM, if running, carries on from its own state file).
- **Go to** a date or **start a new session** only when the account is flat (no positions, no orders). This keeps TM's memory consistent: TM sees its trades closed, not vanished.
- **Results** per session: `trades.csv` — one row per owner's trade: symbol, side, lots, entry/exit time (replay) and price, result in USD and R, hedge cycles, break-even reached, 2R warning, 3R close, commission; plus `events.csv` with every broker event.

## 9. TM's side

TM's **code is not changed**. Three small, opt-in changes are needed in the **Trader** project's shared code (`Trader.Core`, Open Code Pieces), done there as a separate step on the owner's go and recorded in Trader's documents; with none of them switched on, every Trader bot works exactly as today:

| # | Change in Trader.Core | Why |
|---|---|---|
| T1 | A third environment **`replay`**: host and port taken from the secrets file, **only `localhost` / `127.0.0.1` accepted**. `demo` stays the default; `live` stays refused | TM must connect to Rewind instead of `demo.ctraderapi.com` |
| T2 | Choose the secrets file with an environment variable (e.g. `TRADER_SECRETS_FILE`); default stays `~/.config/trader/secrets.env` | TM-for-Rewind must never read or rewrite the real tokens, which the running Audit Bot uses |
| T3 | Trust Rewind's own certificate | The cTrader library always encrypts (TLS) and accepts only certificates the system trusts. First choice, **no code**: start TM with `SSL_CERT_FILE` pointing at a file with the system's certificates plus Rewind's (to be verified that .NET on Linux honours it); fallback: T1 accepts exactly Rewind's certificate (pinned) for `replay` only |

On the Rewind side, for TM:
- `~/rewind/tm/secrets.env` — `CTRADER_ENV=replay`, host, port, and **fake** client ID, secret and tokens (Rewind accepts these only; they are worthless anywhere else). Rewind answers token refreshes with new fake tokens, which TM writes back into *this* file.
- `~/rewind/tm/tm.json` — TM's config for Rewind: the Rewind account and the six symbols, same rule values as on demo.
- TM's state and logs then land in `~/tradebot/run/tm/rewind-<account>.json` and `~/tradebot/logs/rewind/…` — kept apart from the demo accounts by the broker name `rewind`.

Start (after T1–T3):

```bash
TRADER_SECRETS_FILE=~/rewind/tm/secrets.env SSL_CERT_FILE=~/rewind/certs/bundle.pem \
  dotnet run --project ~/code/Trader/src/Trader.TM -- --config-dir ~/rewind/tm
```

The TM service on the demo accounts can keep running at the same time: different account, different secrets, different state file.

## 10. Rewind's Open API server

Rewind answers the part of cTrader's Open API that TM uses (Protobuf messages over TLS, Spotware's message definitions, taken from the same NuGet package Trader uses or generated from Spotware's `.proto` files):

| Area | Messages |
|---|---|
| Connection | version, application auth, heartbeat, account list by token, account auth, token refresh, trader (account) details, asset list |
| Symbols | symbols list, symbol by ID (with trading schedule) |
| Prices | subscribe spots → spot events every price step; live trendbar subscription; historical trendbars (D1 for TM's hedge distance) |
| Trading | new order (market, stop), amend order, cancel order, amend position SL/TP, close position (all or part), reconcile |
| Events | execution events (accepted, filled, replaced, cancelled, rejected) with the request's client message ID, deals with close details; order error events; error responses with cTrader's codes |

Behaviour is copied from what the Trader project saw live (e.g. the event order for a market order: accepted, then filled with the deal). Anything TM sends that Rewind does not support is answered with an error and logged, so gaps are visible at once.

## 11. Build steps

Each step on the owner's go, each tested before the next.

| Step | Content | Test |
|---|---|---|
| 1 | `rewind-download`: Dukascopy files → monthly M1 / M5 CSV for the six symbols; summary | **Built 20-Oct-2026** — 8 tests; trial week of EURUSD complete (7,186 minutes, no gaps); prices within 0.06 % of IC Markets' for all six symbols; full download running (log in `~/rewind/logs/`). Owner to glance at a few days in Google Sheets |
| 2 | Chart in the browser: timeframes, scroll, go to, replay cursor, step / play, levels | Owner replays a known week |
| 3 | Simulated broker + order panel: manual trades, SL/TP, P/L, commission, sessions, `trades.csv` | Hand-checked trades (fill prices, stop hits, gaps) |
| 4 | Open API server + certificate; Trader-side T1–T3 (in the Trader repo) | `Trader.ListAccounts` and then TM connect to Rewind; TM takes over a manual trade, hedges fill and re-hedge on replayed prices |
| 5 | Exact mode tuning, results CSV with R values, polishing | A full replayed week with TM, results checked against TM's `actions.csv` |

## 12. Technology

| Part | Choice |
|---|---|
| Language / runtime | C#, .NET 10 LTS (same as Trader), one solution `Rewind.sln` |
| Programs | `Rewind` (web GUI server + simulated broker + Open API server, one process); `rewind-download` (console) |
| Web server | ASP.NET Core (built into .NET); the browser and Rewind talk over a WebSocket (a connection that stays open for live updates) |
| Browser page | Plain HTML / JavaScript + Lightweight Charts (copied into the repo, fixed version) — no build tools |
| Open API side | `TcpListener` + `SslStream` (built into .NET); certificate made once by Rewind, for `localhost`, stored in `~/rewind/certs/` |
| Compression | SharpCompress (MIT licence, NuGet) for LZMA — simpler than copying the 7-Zip LZMA SDK |
| Logging | Serilog, UTC times |
| Tests | xUnit: decoder, M1 → M5, fills and gaps, stop rules, clock translation, message handling |

## 13. Open questions

- **Spreads:** Dukascopy's own spreads, or fixed per-symbol spreads from the Audit Bot's measurements (closer to the owner's brokers)?
- **Commission:** exact IC Markets Raw values per symbol (forex and crypto).
- **USD conversion** of JPY and GBP profits: download USDJPY and GBPUSD as well (two more symbols, conversion only), or a fixed rate per session?
- **Dukascopy's terms of use** for the data feed: not found online; third parties speak of personal use. Rewind keeps the data private (never in Git); ask Dukascopy if it is ever to be shared.
- **News pop-ups in replay:** TM's news / Friday pop-ups follow the real clock. Rewind could show its own news markers on the chart from Trader's `news-releases.json` (copied) instead.
- **D1 candle boundaries:** 00:00 UTC, or 17:00 New York as many brokers draw them? Compare with cTrader in step 2.
- **More precise replay:** use the M1 files as the price path (20 steps per M5 bar) — when the 4-step guess proves too rough.
- **Two accounts** (an IC Markets-like and a Pepperstone-like one, different spreads/commission) — later, if useful.

## 14. Safety

- Rewind never connects to cTrader or any broker; the only outside access is the Dukascopy download.
- Rewind never reads or writes `~/.config/trader/secrets.env` or anything else of the Trader project; Trader's bots only reach Rewind when started with the replay settings of §9.
- TM's `replay` environment accepts only `localhost`, so a mistake cannot send orders anywhere real.

## 15. History

| Version | Date | Change |
|---|---|---|
| v0.1 | 20 Oct 2026 | First draft. Owner's choices: browser app; six symbols; 24 months; 5-minute candles to start; TM runs unchanged on Ubuntu and connects to Rewind like a broker; history downloaded (Dukascopy) by Rewind's own downloader (option c); a completely separate project and repository, copying rather than sharing code with Trader |
| v0.2 | 20 Oct 2026 | Step 1 built: `rewind-download`. Confirmed file format, URL (month from 00) and price factors (checked against IC Markets). Changed: one download at a time (Dukascopy throttles bursts with HTTP 429), HTTPS only, forex Saturdays skipped, SharpCompress instead of a copied LZMA decoder; history is personal use only |
