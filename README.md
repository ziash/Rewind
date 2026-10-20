# Rewind

**Rewind the market and trade it again by hand — with TM managing your trades.**

A chart in the browser replays past prices: scroll back, go to any date, switch between D1, H4, H1, M15 and M5, step or play forward. You open trades on it as you would in cTrader. The **TM bot** from the [Trader](https://github.com/ziash/Trader) project runs beside it, connects to Rewind as if it were a broker, and mirrors, hedges and protects those trades exactly as it does on the demo accounts.

| Item | Value |
|---|---|
| Status | **Spec draft** — nothing built yet |
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

## Build steps

1. History downloader (`rewind-download`)
2. Chart: timeframes, scroll, go to, step / play, levels
3. Simulated broker and order panel (manual trades, sessions, results CSV)
4. Open API server so TM can connect (plus three opt-in settings in Trader)
5. Exact replay with TM, results in R

Details and status: [docs/spec.md §11](docs/spec.md#11-build-steps).
