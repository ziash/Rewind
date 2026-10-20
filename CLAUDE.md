# CLAUDE.md — Rewind project

Standing instructions for Claude Code in this repository. Read this first, then `README.md` and `docs/spec.md`.

## What this project is

**Rewind**: a bar-replay trading simulator in the browser (past prices, timeframes, step / play) with a simulated broker that speaks cTrader's Open API, so the owner can trade the past by hand while the **real TM bot** from the Trader project (`~/code/Trader`, github.com/ziash/Trader) manages the trades. Spec: `docs/spec.md` (single source of truth).

## Working rules

1. **Code only when the owner says so.** Spec discussions do not authorise coding. Build the step asked for (spec §11), not the next ones.
2. **Spec-driven.** Any change in decisions, scope or design goes into `docs/spec.md`: bump its version in the header table, add a row to its history (§15), update the open questions (§13). Commit messages like `rewind v0.2: <what changed>`. Code comments point at spec sections, e.g. `(spec §6.3)`.
3. **Define difficult terms.** The owner is a telecom core-network engineer, not a C# developer: explain technical terms (C#, .NET, trading, networking) briefly in plain words.
4. **Explain before large changes**, in a few lines.
5. **Short, direct answers.**

## Separation from Trader — never break these

- Rewind is a **separate project**: no project references to Trader's code. If a piece of Trader is needed, **copy** it into Rewind with a comment naming its origin (file and commit).
- Never read, write or print `~/.config/trader/secrets.env` or other Trader secrets. Rewind's TM settings live in `~/rewind/tm/` and contain only fake values.
- Never change files in `~/code/Trader` or `~/tradebot/` from this project. The Trader-side changes of spec §9 (T1–T3) are made in the Trader repository, under its own rules (its CLAUDE.md), on the owner's go.
- Do not stop, restart or redeploy Trader's services (`trader-auditbot`, `trader-tm`, `trader-auditreport`).
- Rewind never connects to cTrader or any broker. The only outside access is the Dukascopy download (spec §4): at most 4 requests at a time, resume, no re-downloading.

## Tech stack and conventions

- C#, .NET 10 LTS, nullable on, warnings as errors where practical; one solution `Rewind.sln`; source in `src/`, tests in `tests/`.
- Serilog with UTC timestamps. CSV files follow the Trader CSV rules (UTF-8, comma, `.` decimals, UTC `yyyy-MM-dd HH:mm:ss`, `_utc` / unit suffixes in column names).
- Runtime data in `~/rewind/` (`history/`, `sessions/`, `config/`, `certs/`, `tm/`); reference copies of config files in the repo's `config/`.
- Browser page: plain HTML/JS, Lightweight Charts copied into the repo at a fixed version (keep its attribution).

## Before finishing a task

- `dotnet build` passes; tests pass.
- Commit and `git push` (the owner works from GitHub `main`).
