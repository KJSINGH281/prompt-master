"""
Run a full backtest of SignalForgeNQ on real NQ data.

Usage:
    python run_backtest.py
    python run_backtest.py --interval 5m  --period 60d
    python run_backtest.py --interval 1h  --period 730d
    python run_backtest.py --no-plot
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import pandas as pd
from backtesting import Backtest

from data_loader import fetch_nq
from signal_forge_nq import SignalForgeNQStrategy

RESULTS = Path(__file__).parent / "results"
RESULTS.mkdir(exist_ok=True)


def main():
    ap = argparse.ArgumentParser(description="Backtest SignalForgeNQ on NQ futures.")
    ap.add_argument("--symbol", default="NQ=F",
                    help="Yahoo symbol. NQ=F (default), MNQ=F, ES=F, ...")
    ap.add_argument("--interval", default="15m",
                    help="Bar interval. 5m / 15m / 30m / 60m / 1d (default 15m).")
    ap.add_argument("--period", default="60d",
                    help="Lookback. 60d for intraday, 730d for hourly, max for daily.")
    ap.add_argument("--cash", type=float, default=100_000)
    ap.add_argument("--commission", type=float, default=0.0,
                    help="Per-trade commission as fraction of trade value (default 0).")
    ap.add_argument("--no-plot", action="store_true")
    args = ap.parse_args()

    # Some strategy presets you can flip ----------------------------------
    SignalForgeNQStrategy.require_all = True
    SignalForgeNQStrategy.use_sma = True
    SignalForgeNQStrategy.use_ema = False
    SignalForgeNQStrategy.use_adx = False
    SignalForgeNQStrategy.use_macd = False
    SignalForgeNQStrategy.sl_mult = 1.5
    SignalForgeNQStrategy.tp_mult = 2.5
    SignalForgeNQStrategy.use_be = True
    SignalForgeNQStrategy.use_trail = False
    # ---------------------------------------------------------------------

    print(f"\n=== SignalForgeNQ backtest ===")
    print(f"Symbol={args.symbol}  Interval={args.interval}  Period={args.period}")
    df = fetch_nq(args.symbol, args.interval, args.period)
    print(f"Loaded {len(df):,} bars  {df.index[0]} -> {df.index[-1]}\n")

    bt = Backtest(
        df,
        SignalForgeNQStrategy,
        cash=args.cash,
        commission=args.commission,
        finalize_trades=True,
        exclusive_orders=True,
    )
    stats = bt.run()

    # Pretty print stats (drop verbose internals)
    print(stats)

    # Save trade log + summary JSON for later inspection
    summary = {
        k: (str(v) if isinstance(v, pd.Timestamp) else v)
        for k, v in stats.items()
        if k != "_trades" and k != "_equity_curve" and k != "_strategy"
    }
    tag = f"{args.symbol.replace('=','_')}_{args.interval}_{args.period}"
    (RESULTS / f"{tag}_summary.json").write_text(json.dumps(summary, indent=2, default=str))
    stats._trades.to_csv(RESULTS / f"{tag}_trades.csv", index=False)
    stats._equity_curve.to_csv(RESULTS / f"{tag}_equity.csv")

    print(f"\nSaved to {RESULTS}/")

    if not args.no_plot:
        try:
            bt.plot(filename=str(RESULTS / f"{tag}_plot.html"), open_browser=False)
            print(f"Plot     : {RESULTS / f'{tag}_plot.html'}")
        except Exception as e:
            print(f"[warn] Plot disabled: {e}")


if __name__ == "__main__":
    main()
