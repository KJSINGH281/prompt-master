# SignalForgeNQ — Python Backtesting Harness

Standalone Python backtesting setup for the **SignalForgeNQ** strategy. Validates
strategy logic on real NQ futures data **without NinjaTrader**, so we can iterate
on signal rules before fighting NT8 setup again.

## Why this exists

The NinjaScript version (`SignalForgeNQ.cs`) is the real production strategy, but
NT8's "can't enable" issues are environmental (account selection, data feed, license)
and stop us from validating whether the *logic itself* is profitable. This harness
runs the same logic in Python in seconds, with proper stats and an HTML chart.

## Stack

| Tool | Why |
|---|---|
| [Backtesting.py](https://github.com/kernc/backtesting.py) 0.6.x | Single-asset focused, minimal API, fast, beautiful HTML reports |
| [yfinance](https://github.com/ranaroussi/yfinance) 0.2.x | Free NQ futures data (`NQ=F` continuous front-month) |
| pandas / numpy | Indicator math |
| bokeh | Backtesting.py's plot output |

## Setup

```bash
cd backtest
pip install -r requirements.txt
```

Python 3.10+ recommended. No API keys needed — Yahoo Finance data is free.

## Run a backtest

```bash
python run_backtest.py                              # default 15m, last 60 days
python run_backtest.py --interval 5m  --period 60d  # higher resolution
python run_backtest.py --interval 1h  --period 730d # ~2 years of hourly data
python run_backtest.py --no-plot                    # skip HTML chart
```

Outputs land in `results/`:
- `*_summary.json` — full stat block (Sharpe, PF, drawdown, etc.)
- `*_trades.csv` — every trade with entry/exit/PnL
- `*_equity.csv` — full equity curve
- `*_plot.html` — interactive Bokeh chart with trade markers

## Run a parameter sweep

```bash
python run_optimize.py --maximize "Sharpe Ratio"
python run_optimize.py --maximize "Profit Factor" --interval 5m
python run_optimize.py --maximize "Equity Final [$]"
```

Sweeps SMA fast/slow, SL/TP multipliers, and break-even trigger over a sensible
grid. Results saved as a heatmap CSV.

## Files

| File | Purpose |
|---|---|
| `data_loader.py` | Pulls/caches NQ futures bars from Yahoo Finance |
| `signal_forge_nq.py` | Strategy port — same logic as `SignalForgeNQ.cs` |
| `run_backtest.py` | Single-config backtest with full stats + chart |
| `run_optimize.py` | Parameter grid sweep |
| `results/` | Outputs (gitignored) |
| `data/` | Cached OHLCV CSVs (gitignored) |

## Strategy logic mirrors the NinjaScript version

- 11 indicator modules (SMA / RSI / MACD / Stoch / BB / EMA / AO / SAR / CCI / ADX+DM / Supertrend)
- AND mode (all enabled modules must agree) or OR mode (any triggers)
- Edge-detected entries (signal transition only)
- ATR-based SL / TP, optional trailing stop, optional break-even shift
- Kill-zone session filter: AM 09:30–11:30 ET, PM 13:30–15:30 ET
- Daily loss limit + max trade count halt
- Opposing-signal exits

## Toggling signal modules

Edit class attributes in `signal_forge_nq.py` or override at the top of
`run_backtest.py`. Examples:

```python
SignalForgeNQStrategy.use_sma  = True
SignalForgeNQStrategy.use_ema  = True
SignalForgeNQStrategy.use_adx  = True
SignalForgeNQStrategy.adx_threshold = 25
SignalForgeNQStrategy.require_all = True   # AND
SignalForgeNQStrategy.sl_mult = 1.5
SignalForgeNQStrategy.tp_mult = 2.5
```

## Data limits to be aware of

Yahoo Finance intraday window per request:
- 1m   → last 7 days
- 5m   → last 60 days
- 15m  → last 60 days
- 30m  → last 60 days
- 60m  → last ~2 years
- 1d   → full history

For longer intraday backtests, swap the data source for a paid feed (Polygon,
Databento, IB historical, NinjaTrader Continuum) and update `data_loader.py`.

## Workflow

```
1. Iterate on strategy rules in Python   ← logic-validation loop (seconds)
2. Once stats look good, port back to NinjaScript  (already done in SignalForgeNQ.cs)
3. Live-fire in NT8 Strategy Analyzer     ← execution-validation loop
4. Sim101 paper trade                     ← realtime fills
5. Live trade                             ← only after 1-4 are clean
```
