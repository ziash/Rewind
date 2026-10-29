# Rewind — specification

**Rewind the market and trade it again by hand.** A chart in your browser replays past prices (scroll back, go to any date, switch timeframes, step or play forward); you open trades on it as you would in cTrader; and **the real TM** (from the Trader project) runs on Ubuntu, connects to Rewind as if it were a broker, and mirrors, hedges and protects those trades exactly as it does on the demo accounts.

| Item | Value |
|---|---|
| Status | **Steps 1–6 built** (history downloader 20-Oct-2026; chart, simulated broker and TM connection 21-Oct-2026; exact mode, TM's results, pending orders and trailing stops 22-Oct-2026) — §11 |
| Document version | v0.10 (29-Oct-2026) — history in §15 |
| Owner | Zeeshan |
| Repository | [github.com/ziash/Rewind](https://github.com/ziash/Rewind) (public) — separate from [Trader](https://github.com/ziash/Trader) |
| Runs on | The Ubuntu laptop, or Windows on the same machine (v0.9); browser GUI at `http://localhost:5180`; the broker side at `localhost:5036` |
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
| Market orders by hand, with stop-loss / take-profit; moving them; closing all or part | Stop-limit and market-range orders, order expiry |
| Pending orders by hand: buy-limit, sell-limit, buy-stop, sell-stop, each with stop-loss / take-profit for its position (step 6) | Guaranteed stops |
| Trailing stops (held by the simulated broker, as cTrader's server does) (step 6) | |
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
| Run | `rewind-download` (all), `--from` / `--to` / `--symbols` for a part, `--build-only` to rebuild the CSV files from the cache, `--interval-ms` for the pause. Built into its own folder, `~/rewind/bin/download/`, so rebuilding the repo never disturbs a running download (README). Code: `src/Rewind.Download`; tests: `tests/Rewind.Download.Tests` (two real day files as samples) |

Things to know:
- Dukascopy's prices are **very close to, not identical with**, IC Markets' or Pepperstone's — fine for practising and for testing TM's behaviour. Their spreads also differ; Rewind can instead use **fixed spreads per symbol** (e.g. the medians the Audit Bot measured) — decision in §13.
- **Personal use only:** the downloaded history is never committed to Git or shared (`.gitignore` excludes it) — the only exception is two EURUSD day files (22 KB) kept as test samples in this private repository. Dukascopy's own terms for the feed could not be found online yet (§13).
- The download is the **only** time Rewind talks to the internet.

## 5. The chart (browser GUI)

Built with **Lightweight Charts** v5.2.1, TradingView's free open-source chart library (Apache 2.0 licence; its TradingView logo stays on the chart as the attribution), copied into `src/Rewind/wwwroot/lib/` with its licence and served from Rewind itself (no internet needed when running). Run: `dotnet run --project src/Rewind`, then open `http://localhost:5180`.

| Feature | Detail |
|---|---|
| Candles | Bid candles, as cTrader shows them, and a dotted line at the current ask |
| Timeframes | M5, M15, H1, H4, D1 — switching keeps the replay cursor. M5, M15, H1 follow the UTC clock; **H4 and D1 start at 17:00 New York** like cTrader's (IC Markets checked 21-Oct-2026: D1 and H4 open 21:00 UTC in summer, 22:00 in winter; H4 at 17, 21, 01, 05, 09, 13 New York) |
| No peeking | Candles after the replay cursor are not sent to the browser; the last candle of each timeframe is drawn **as far as it has formed** (the D1 candle of today grows bar by bar as you step on M5) |
| Scroll and zoom | Mouse wheel / drag, as in TradingView; older candles load in pages of 500 when you reach the left edge; scrolling back never moves the cursor |
| Go to | Date and time box (UTC); the replay time is shown in UTC, London and New York. Kept inside the history (from a week after its start) and on 5-minute marks. **Only when the account is flat** (§8); the skips (next day / London / New York) move forward and carry open positions through every price on the way |
| Replay controls | **Step** (the next 5-minute candle of the symbol on screen — a forex weekend is one step; key →), **Play / Pause** (space), speed 1–60 candles per second, **Next day** (17:00 New York), **London open** / **New York open** (next 08:00 local, Monday–Friday). The position is saved in `~/rewind/sessions/default/replay.json` and restored at start |
| Levels | Horizontal lines you place (**+ Level** then click, or Alt+click; optional note) stay on every timeframe — the way to carry an entry idea from D1 down to M5. Removed with ×; saved per symbol in `sessions/default/levels.json`. Dragging a level is not possible (the chart library has no draggable lines); remove and place again |
| Symbol switch | One symbol on screen at a time; all six move with the same cursor; positions on other symbols keep running |
| Order panel | Order type **Market / Limit / Stop**; **SELL / BUY** buttons (greyed while the market is closed) — with Limit or Stop they read SELL LIMIT / BUY STOP etc. and need an **order price** (typed or picked with ⌖); lots; optional stop-loss and take-profit typed in or picked on the chart (⌖), shown as dotted lines before the order; **Trailing stop** box (needs a stop-loss). Open positions: SL / TP / trailing editable in the table, SL / TP **dragged on the chart**; close all or part (lots in the table); **Close all** (asks first). Pending orders: price, SL / TP and trailing editable in the orders table, order line and its SL / TP lines dragged on the chart. No keyboard shortcuts for trading |
| On the chart | For the symbol on screen: each position's entry (grey, "#id side lots" plus the last part of its label, e.g. MIR / HMAIN), SL (red), TP (green), each pending order (orange, "#id buy-limit lots") with its SL / TP (dotted); SL, TP and order lines can be dragged |
| Tables | Positions (ID, symbol, side, lots, entry, SL, trail, TP, price, pips, P/L in USD, label — "manual" for yours) and pending orders (order, symbol, type, lots, price, SL, trail, TP, label, Cancel); account bar: balance, equity, open P/L, commission paid |
| Messages | Broker events (opened, closed, stop-loss / take-profit hit, order placed / moved / filled / cancelled) with replay time, newest first; TM's own pop-ups keep coming through TM's notifications on the desktop |
| Session | Session list and **New…** (name, start date, balance) in the header (§8) |

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
| **Exact** (the default; switch in the footer) | A price step of a symbol **with an open position or pending order** waits until TM has had its turn (rule below); every other price step goes at the speed setting | Has seen every step of its trades: same result as live |
| **Fast** | As fast as the speed setting | May act late — like a fast real market |
| Flat account, or no TM connected | Always as fast as the speed setting | Nothing to manage |

Speed setting: 1, 2, 5, 10, 30, 60 M5 bars per second; a step is always possible.

Exact mode in detail (built in step 5):
- **TM's turn:** after such a price step, Rewind waits at least **1.2 s** (`exactStepSeconds` in `rewind.json`: TM looks once a second, plus a margin) **and** until TM has sent nothing for **0.3 s** (its orders of that look are all answered), but never more than **5 s** per step. A candle of a symbol in a trade therefore takes about 5 s (4 steps); a day with an open trade about 25 minutes; flat stretches go at full speed.
- Applies to **Step** and **Play** alike; **Pause** takes effect at the end of the candle being walked (the remaining steps still wait for TM). The **skips** (next day / London / New York) are not paced: TM may act late there, as in Fast mode.
- While a candle is paced, the page draws it **step by step** (the forming candle grows, the prices, P/L and TM's orders move with it), and the footer shows "Exact: waiting for TM".
- Only symbols with exposure are paced, so TM on EURUSD does not slow down the other five symbols' steps in the same 5 minutes.

### 6.3 Price path inside a candle

A 5-minute candle only gives open, high, low, close. Rewind walks each M5 bar through **4 price steps**: open → low → high → close for a rising candle, open → high → low → close for a falling one. Ask = bid + that bar's spread. With a small R, several of TM's levels can lie inside one candle, so the order of events inside it is a guess; the M1 files allow an exact mode later (5 × 4 steps per M5 bar).

## 7. The simulated broker

| Item | Rule |
|---|---|
| Account | One account per session, **USD, hedging** (several positions per symbol, both directions), start balance 5,000 USD by default (chosen per session), leverage 1:500 shown only — `config/rewind.json` (live copy `~/rewind/config/rewind.json` if present) |
| Symbols | Details copied from the IC Markets demo (21-Oct-2026) into `config/symbols.json`: digits (5 / 3 / 2 for crypto), pip position, lot size (forex 100,000 units, crypto 1 coin), minimum and step 0.01 lot, maximum |
| Market order | Buy at the ask, sell at the bid of the current price step. Refused with `MARKET_CLOSED` when the symbol has had no candle for 10 minutes (forex weekend, holidays) |
| Pending orders | Buy orders watch the ask, sell orders the bid. **Stop** (TM's hedges; the owner's breakout / breakdown entries): buy-stop above the ask, sell-stop below the bid; fills when the price reaches it, **at the order price**; after a gap (the price jumped past it between two steps) at the step's price — worse, the slippage of a real jump. **Limit** (pullback / rally entries): buy-limit below the ask, sell-limit above the bid; fills when the price comes back to it, at the order price, after a gap at the step's price — better ("limit-style": no slippage against you) |
| Pending order's protection | A pending order may carry a stop-loss, a take-profit and the trailing switch; its position gets them at the fill (the stop-loss also counts as its initial stop for R, §8). Stop-loss below / take-profit above the order price for a buy, the other way for a sell |
| Stop-loss / take-profit | Same rule, on the bid for longs and the ask for shorts: a stop-loss after a gap at the step's price (slips), a take-profit after a gap at the step's price (better) |
| Trailing stop | A switch on a position with a stop-loss (with the order, on the position, or over the Open API). The distance from the stop-loss to the price the position closes at (bid for a long, ask for a short) is fixed when trailing is switched on (for an order: at its fill); after each price step without a hit the stop-loss moves to that distance from the price, **only in the position's favour**. Switching it off leaves the stop-loss where it is; a stop-loss change with trailing on fixes a new distance |
| Amend / cancel | Immediate; refused with cTrader's error codes where cTrader would refuse: a stop-loss / take-profit on the wrong side of the price it closes at (or, for a pending order, of its order price), a buy-stop not above the ask / sell-stop not below the bid, or trailing without a stop-loss (`TRADING_BAD_STOPS`); a buy-limit not below the ask / sell-limit not above the bid (`TRADING_BAD_PRICES`); a volume outside minimum / step / maximum (`TRADING_BAD_VOLUME`) |
| Inside a candle | Fills and hits are checked at each of the 4 price steps (§6.3) in order, so a stop-loss visited before the take-profit wins |
| Commission | IC Markets' own figures (read from the demo, 21-Oct-2026): forex **30 USD per million USD traded, on opening and on closing** (≈ 3.30 USD per lot per side on EURUSD); BTCUSD and ETHUSD **none** (spread only) |
| P/L in USD | Converted with the replay's own prices, no extra downloads: GBPUSD = EURUSD ÷ EURGBP, USDJPY = GBPJPY ÷ GBPUSD (mid prices). A fixed rate from `rewind.json` only if one of those symbols has no history |
| Not simulated | Swap, margin call / stop-out, requotes |

## 8. Sessions

- A **session** = a starting date, the account, all trades and events, the replay cursor. Saved in `~/rewind/sessions/<name>/` after every change; reopening continues where it stopped (TM, if running, carries on from its own state file).
- **Go to** a date or **start a new session** only when the account is flat (no positions, no orders). This keeps TM's memory consistent: TM sees its trades closed, not vanished.
- Sessions are listed in the header; **New…** asks for a name (letters, digits, `-`, `_`), a start date (UTC) and a balance. The session in use is remembered in `~/rewind/sessions/current.txt`; each folder holds `replay.json`, `levels.json`, `account.json`, `trades.csv`, `events.csv`.
- **Results** per session, written as things happen: `trades.csv` — one row per closed position or closed part: open / close time (replay), symbol, side, lots, entry, exit, initial stop-loss, pips, **result in R** (against the stop-loss given at opening, so a stop moved further away can lose more than 1R), commission, P/L in USD after commission, close reason (Manual — by the owner in the browser —, Tm — over the Open API, i.e. by TM —, StopLoss, TakeProfit), position ID, label; and `events.csv` with every broker event.
- **TM results** (step 5): `tm-trades.csv` — **one row per leg** (the owner's main, and TM's mirror) once the leg and its hedge are both closed, measured by Rewind from its own fills with **TM's formulas** (tm.md §1, §5): 1R = |H − leg entry|, H = the leg's first hedge order; a leg's loss = its own open P/L + its open hedge + its earlier hedges, per unit of price.

| Column | Meaning |
|---|---|
| `trade` | P + the main's position ID (as in TM's `actions.csv`) |
| `leg` | MAIN or MIR |
| `symbol`, `side`, `lots` | of the leg (lots at opening) |
| `open_time_utc`, `close_time_utc` | replay times |
| `entry`, `hedge_level`, `r_pips` | E, H and 1R in pips |
| `exit` | the leg's close price (volume-weighted if closed in parts) |
| `hedge_cycles` | how many times its hedge filled |
| `hedges_r` | result of all its hedges together, in R |
| `break_even` | `yes` if its stop-loss was moved to its entry (TM's 1R rule) |
| `max_loss_r` | the largest loss the leg reached, in R (TM warns at 2R and closes at 3R) |
| `closed_by` | owner, TM (TM closes a leg only under its 3R rule), stop-loss, take-profit |
| `result_r` | leg + its hedges, in R, before commission |
| `commission_usd`, `pnl_usd` | leg + its hedges, in USD; P/L after commission |

- A **mirror order** of the owner's pending order (TM v1.5, Trader tm.md §3.1) is labelled with the owner's order ID, `TM-xxxxxx-O<orderId>-MIR`; its position becomes the MIR leg of the trade whose main that order opened (trade = P + the main's position ID, as in TM's `actions.csv` after the fill).
- Unfinished legs are remembered in `tm-open.json` in the session folder, so a restart of Rewind continues them (the largest loss reached between the last fill and the restart may be missed). Manual positions TM never touched get no row. Check after a run: hedge cycles, break-even and 3R closes against TM's `actions.csv` and `events.csv` (HedgeFilled, BreakEven, LossWarning, LossLimit).

## 9. TM's side

TM's **code is not changed**. Three small, opt-in changes were planned in the **Trader** project's shared code (`Trader.Core`, Open Code Pieces); with none of them switched on, every Trader bot works exactly as before. **Outcome (21-Oct-2026, Trader commit 8200a9d, open-code-pieces v1.2):** T1 built; T2 already existed; T3 needs no code.

| # | Change in Trader.Core | Why |
|---|---|---|
| T1 | A third environment **`replay`**: `CTRADER_HOST` / `CTRADER_PORT` from the secrets file, **only `localhost` / `127.0.0.1` / `::1` accepted** (checked when reading the file and again when connecting); refused with `demo`; `live` stays refused | TM must connect to Rewind instead of `demo.ctraderapi.com` — **built**, 9 tests |
| T2 | Choose the secrets file with `TRADER_SECRETS_FILE`; default stays `~/.config/trader/secrets.env` | TM-for-Rewind must never read or rewrite the real tokens — **already in Trader** (since Phase 1) |
| T3 | Trust Rewind's own certificate | The cTrader library always encrypts (TLS) and accepts only certificates the system trusts. **First no code:** TM is started with `SSL_CERT_FILE` = `~/rewind/certs/bundle.pem` (the system's certificates plus Rewind's local authority); .NET on Linux honours it — **confirmed**. Windows has no such setting, so **since v0.9** (Trader open-code-pieces v1.5) `CTRADER_CA_FILE` in TM's secrets names `~/rewind/certs/ca.pem` and TM's replay connection trusts that authority only, on Linux and Windows; nothing is installed in the system |

On the Rewind side, for TM (made by Rewind on its first start; never overwritten):
- `~/rewind/tm/secrets.env` (mode 600) — `CTRADER_ENV=replay`, `CTRADER_HOST=localhost`, `CTRADER_PORT=5036`, `CTRADER_CA_FILE` (the full path of `~/rewind/certs/ca.pem`; added to a file made by an older Rewind on its next start), and **fake** client ID, secret and tokens. Rewind checks the client ID and secret, and accepts any token it could have issued (`rewind-…`); they are worthless anywhere else. Rewind answers token refreshes with new fake tokens, which TM writes back into *this* file.
- `~/rewind/tm/tm.json` — TM's config for Rewind: the account **9000001** (broker name `rewind`) and the symbols with history; TM's rule values stay at their defaults (copy any you want to change in here). Never rewritten; Rewind's log says when it misses symbols that have history (add them to `"symbols"`).
- `~/rewind/tm/run-tm.sh` — sets `TRADER_SECRETS_FILE` and `SSL_CERT_FILE` and starts TM from `~/code/Trader` (`TRADER_REPO` overrides) with `--config-dir ~/rewind/tm`.
- `~/rewind/tm/run-tm.ps1` — the same for Windows PowerShell (`TRADER_SECRETS_FILE` only; TM finds the certificate through `CTRADER_CA_FILE`). Windows runs no scripts by default, so it is started with `-ExecutionPolicy Bypass`, which allows that one run and changes no setting.
- `~/rewind/certs/` — `server.pfx` (Rewind's certificate for localhost, mode 600), `ca.pem` (Rewind's local authority; its private key is never stored), `bundle.pem` (system certificates + `ca.pem`, for `SSL_CERT_FILE`; on Windows only `ca.pem`, as there is no system file).
- The header of the page shows **TM on / TM off** (a TM connected to the Open API).
- TM's state and logs then land in `~/tradebot/run/tm/rewind-<account>.json` and `~/tradebot/logs/rewind/…` — kept apart from the demo accounts by the broker name `rewind`.

Start (Rewind running first):

```bash
~/rewind/tm/run-tm.sh
```

On Windows (PowerShell, `~` = `C:\Users\<name>`):

```powershell
powershell -ExecutionPolicy Bypass -File "$HOME\rewind\tm\run-tm.ps1"
```

The TM service on the demo accounts can keep running at the same time: different account, different secrets, different state file.

## 10. Rewind's Open API server

Rewind answers the part of cTrader's Open API that TM uses (Protobuf messages over TLS, Spotware's message definitions, taken from the same NuGet package Trader uses or generated from Spotware's `.proto` files):

| Area | Messages |
|---|---|
| Connection | version, application auth, heartbeat, account list by token, account auth, token refresh, trader (account) details, asset list |
| Symbols | symbols list, symbol by ID (with trading schedule) |
| Prices | subscribe spots → spot events every price step; live trendbar subscription; historical trendbars (D1 for TM's hedge distance) |
| Trading | new order (market, limit, stop; stop-loss / take-profit absolute for pending orders or relative; trailing switch), amend order (stop or limit price, stop-loss / take-profit, trailing), cancel order, amend position SL/TP (with trailing), close position (all or part), reconcile |
| Events | execution events (accepted, filled, replaced, cancelled, rejected) with the request's client message ID, deals with close details; order error events; error responses with cTrader's codes |

Behaviour is copied from what the Trader project saw live (e.g. the event order for a market order: accepted, then filled with the deal). Anything TM sends that Rewind does not support is answered with an error and logged, so gaps are visible at once.

As built (step 4, `src/Rewind/OpenApi`):
- Listens on **127.0.0.1:5036 only**, TLS with Rewind's certificate; heartbeat every 10 s; one account, ID 9000001, hedged, USD, balance from the session.
- Symbol IDs 1–6 in the order of `config/symbols.json` (only those with history); asset IDs USD 1, EUR 2, GBP 3, JPY 4, AUD 5, BTC 6, ETH 7.
- A request's results go back under its clientMsgId: market order → ORDER_ACCEPTED + ORDER_FILLED (order, position, deal); stop or limit order → ORDER_ACCEPTED (with its stop or limit price, stop-loss, take-profit, trailing switch); amend → ORDER_REPLACED; cancel → ORDER_CANCELLED; SL/TP → ORDER_REPLACED with the position; close → ORDER_ACCEPTED + ORDER_FILLED with the deal's close details. A relative stop-loss / take-profit on a market order is placed at that distance from the fill price. Refusals → `ProtoOAOrderErrorEvent` with the code (e.g. `TRADING_BAD_STOPS`).
- Everything else that happens — the owner's clicks in the browser, stop orders, stop-losses and take-profits filled while the replay moves — goes to every connected TM as unsolicited execution events (stop-loss / take-profit closes as `STOP_LOSS_TAKE_PROFIT` orders), so TM reacts at once, as with cTrader.
- A filled pending order is reported with its own type (STOP or LIMIT) and ID; positions and orders carry `trailingStopLoss`. An amend-order request without stop-loss, take-profit or trailing fields keeps them (TM moves its hedges with the price only); an amend-position request without the trailing field switches trailing off. A trailing stop's moves are **not** sent as events: a client sees the new stop-loss in its next reconcile (TM reconciles at least every 30 s and after every order event; open question §13).
- Spot events at every price step (4 per candle) for subscribed symbols, stamped with the real time.
- Order and deal IDs that the simulated broker does not have (market and closing orders, deals) count up from 800,000,000.

## 11. Build steps

Each step on the owner's go, each tested before the next.

| Step | Content | Test |
|---|---|---|
| 1 | `rewind-download`: Dukascopy files → monthly M1 / M5 CSV for the six symbols; summary | **Built 20-Oct-2026** — 8 tests; trial week of EURUSD complete (7,186 minutes, no gaps); prices within 0.06 % of IC Markets' for all six symbols; full download done 21-Oct-2026: 8,120 files, all six symbols 1-Oct-2024 → 19-Oct-2026 (log in `~/rewind/logs/`). Owner to glance at a few days in Google Sheets |
| 2 | Chart in the browser: timeframes, scroll, go to, replay cursor, step / play, levels | **Built 21-Oct-2026** — 14 tests (D1/H4 boundaries summer and winter, no candle after the cursor, forming last candle, paging back, weekend in one step, go-to limits, skip targets across the clock changes); tried in the browser: steps, play at 30/s, go to, skips, D1 / M5, a level carried from D1 to M5, scrolling back. Owner replays a known week |
| 3 | Simulated broker + order panel: manual trades, SL/TP, P/L, commission, sessions, `trades.csv` | **Built 21-Oct-2026** — 10 broker tests (fill at the ask, SL inside a candle at its price and after a gap at the open, SL-before-TP order inside a candle, buy-stop inside a candle and after a gap, refusals, market closed, partial close and JPY profit in USD, flat-only go to, restart); in the browser: buy with SL/TP, SL dragged on the chart, partial close, refusals shown, SL hit while playing, new session refused while a position is open and created when flat. Owner trades a known day |
| 4 | Open API server + certificate; Trader-side T1–T3 (in the Trader repo) | **Built 21-Oct-2026** — 5 end-to-end tests over TLS (wrong secret refused; market order accepted + filled under its ID with the relative stop-loss and real-time opening; refusal as order error; stop order filled for every client while the replay moves, spots at each step; past candles moved into real time). Live: `Trader.ListAccounts` listed account 9000001; **TM unchanged** took over a manual EURUSD sell (mirror, both hedges at the stop-loss, stop-losses removed), then on replayed prices: hedges filled at H, hedge stops at ½R, stops hit, re-hedge, 2R warning; after Close all its state was empty |
| 5 | Exact mode (§6.2), TM results `tm-trades.csv` (§8), polishing | **Built 22-Oct-2026** — 9 tests (waits only after the steps of a symbol in a trade, a pending order counts, no wait when flat / without TM / in Fast mode / on skips, waits for TM's quiet but at most 5 s, the page and TM's orders are not held up by a paced step, pause ends the candle; legs: hedge cycle + owner close + TM close, break-even then stop at entry, hedges moved before a fill change 1R, partial closes averaged, a restart keeps open legs, untouched positions get no row). Live: TM unchanged on **17–21 Mar 2025** (session `step5-week`): 11 trades on EURUSD, GBPJPY and BTCUSD, 45 minutes for the week; TM sent 150 orders, all accepted; **all 22 legs of `tm-trades.csv` match TM's own logs** — hedge cycles (0–5 per leg), break-even, 2R warnings and one 3R close by TM. Polishing: the downloader builds into its own folder (a rebuild had stopped the first run while it built the CSVs); Rewind warns when TM's `tm.json` misses symbols that have history |
| 6 | Pending orders by hand (buy/sell limit and stop) with stop-loss / take-profit, trailing stops; in the browser and over the Open API (§5, §7, §10). Trader side: TM v1.4 (owner, 22-Oct-2026) | **Built 22-Oct-2026** — 8 tests (limits fill at their price or better after a gap; sell-stop; each order type refused on the wrong side; an order's SL / TP go to its position and are kept when it moves; trailing follows only in the trade's favour and stops out; switched on later and off again on a short; a limit order with protection over the Open API, accepted, in the reconcile and filled as LIMIT; trailing set and cleared over the Open API). Live with TM (session `pending-orders`, 18-Mar-2025, EURUSD): a manual sell-limit with SL (trailing) and TP, and a buy-stop left alone by TM while pending; the limit's fill taken over (mirror, hedges at its SL, TP copied); at 1R trailing break-even stops on both legs, which trailed and closed the mirror at +0.37R and the main at +2.61R; a wrong-side buy-limit refused with `TRADING_BAD_PRICES` |

## 12. Technology

| Part | Choice |
|---|---|
| Language / runtime | C#, .NET 10 LTS (same as Trader), one solution `Rewind.sln` |
| Programs | `Rewind` (web GUI server + simulated broker + Open API server, one process); `rewind-download` (console) |
| Web server | ASP.NET Core (built into .NET); the browser and Rewind talk over a WebSocket (a connection that stays open for live updates) |
| Browser page | Plain HTML / JavaScript + Lightweight Charts (copied into the repo, fixed version) — no build tools |
| Open API side | `TcpListener` + `SslStream` (built into .NET); certificate made once by Rewind, for `localhost`, stored in `~/rewind/certs/` |
| Compression | SharpCompress (MIT licence, NuGet) for LZMA — simpler than copying the 7-Zip LZMA SDK |
| Logging | The console log of ASP.NET with UTC times (Serilog if more is needed later) |
| Tests | xUnit: decoder, M1 → M5, fills and gaps, stop rules, clock translation, message handling |

## 13. Open questions

- **Spreads:** Dukascopy's own spreads, or fixed per-symbol spreads from the Audit Bot's measurements (closer to the owner's brokers)?
- **Dukascopy's terms of use** for the data feed: not found online; third parties speak of personal use. Rewind keeps the data private (never in Git); ask Dukascopy if it is ever to be shared.
- **News pop-ups in replay:** TM's news / Friday pop-ups follow the real clock. Rewind could show its own news markers on the chart from Trader's `news-releases.json` (copied) instead.
- **TM's files in ~/tradebot:** TM writes its replay state and logs under Trader's folders (`run/tm/rewind-9000001.json`, `logs/rewind/`), apart from the demo accounts. Accepted for now.
- **More precise replay:** use the M1 files as the price path (20 steps per M5 bar) — when the 4-step guess proves too rough.
- **Two accounts** (an IC Markets-like and a Pepperstone-like one, different spreads/commission) — later, if useful.
- **Trailing stop details at cTrader:** whether cTrader sends an execution event each time a trailing stop moves, and exactly which price it measures the distance from. Rewind sends no event and measures from the closing side's price; check on a demo account (Trader tm.md §12).

## 14. Safety

- Rewind never connects to cTrader or any broker; the only outside access is the Dukascopy download.
- Rewind never reads or writes `~/.config/trader/secrets.env` or anything else of the Trader project; Trader's bots only reach Rewind when started with the replay settings of §9.
- TM's `replay` environment accepts only `localhost`, so a mistake cannot send orders anywhere real.

## 15. History

| Version | Date | Change |
|---|---|---|
| v0.1 | 20 Oct 2026 | First draft. Owner's choices: browser app; six symbols; 24 months; 5-minute candles to start; TM runs unchanged on Ubuntu and connects to Rewind like a broker; history downloaded (Dukascopy) by Rewind's own downloader (option c); a completely separate project and repository, copying rather than sharing code with Trader |
| v0.2 | 20 Oct 2026 | Step 1 built: `rewind-download`. Confirmed file format, URL (month from 00) and price factors (checked against IC Markets). Changed: one download at a time (Dukascopy throttles bursts with HTTP 429), HTTPS only, forex Saturdays skipped, SharpCompress instead of a copied LZMA decoder; history is personal use only |
| v0.3 | 21 Oct 2026 | Step 2 built: the chart (§5). Decided: H4 and D1 start at 17:00 New York as in cTrader (open question closed); a step moves to the next candle of the symbol on screen; replay position and levels saved in `sessions/default/`; ASP.NET console logging instead of Serilog for now |
| v0.4 | 21 Oct 2026 | Step 3 built: the simulated broker (§7), order panel, sessions and results files (§8). Decided: commission and symbol details from the IC Markets demo (open question closed); USD conversion from crosses of the six symbols (open question closed); a market closed after 10 minutes without a candle; R measured against the stop-loss given at opening |
| v0.5 | 21 Oct 2026 | Step 4 built: Rewind's Open API server (§10), certificate and TM's files (§9). Trader side: T1 built (Trader open-code-pieces v1.2), T2 already existed, T3 needs no code (`SSL_CERT_FILE` confirmed). TM ran unchanged on replayed prices |
| v0.6 | 22 Oct 2026 | Step 5 built: exact mode waits for TM only on symbols in a trade and only while TM is connected (1.2 s, then 0.3 s of quiet, at most 5 s per price step; open question closed); TM results per leg in `tm-trades.csv`; trades closed over the Open API marked `Tm`. A replayed week with TM matched TM's own logs leg for leg. Full history of all six symbols in place (1-Oct-2024 → 19-Oct-2026) |
| v0.7 | 22 Oct 2026 | Step 6 built: pending orders by hand — buy/sell limit and stop, each with stop-loss / take-profit for its position — and trailing stops, in the order panel and over the Open API (§2, §5, §7, §10). Limits fill at their price or better (after a gap), stops at their price or worse; wrong-side limits refused with `TRADING_BAD_PRICES`. Tested live with TM v1.4 (trailing break-even stops) |
| v0.8 | 22 Oct 2026 | TM's results (§8): a mirror labelled with the owner's order ID (`O<orderId>-MIR`, TM v1.5 mirror orders) is the MIR leg of the trade that order's position starts. Tested live: TM mirrored a manual sell-stop and buy-limit with orders of the same type and price, followed a move and a cancel, and both sell-stops filled as one trade with both legs in `tm-trades.csv` (session `mirror-orders`) |
| v0.9 | 23 Oct 2026 | Windows (owner: the laptop also boots Windows): TM's secrets name Rewind's certificate authority (`CTRADER_CA_FILE`, T3), so TM trusts Rewind without `SSL_CERT_FILE` (Trader open-code-pieces v1.5); Rewind adds the line to an existing `secrets.env` and also writes `run-tm.ps1`. Not yet run on Windows itself |
| v0.10 | 29 Oct 2026 | Repository made public (owner); no secrets in its history |
