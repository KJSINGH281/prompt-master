"""
Parameter sweep for SignalForgeNQ.

By default sweeps SL multiplier, TP multiplier, and SMA periods.
Backtesting.py runs all combos and ranks by an objective you choose.

Usage:
    python run_optimize.py
    python run_optimize.py --maximize Sharpe
    python run_optimize.py --maximize "Profit Factor"  --interval 5m
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from backtesting import Backtest

from data_loader import fetch_nq
from signal_forge_nq import SignalForgeNQStrategy

RESULTS = Path(__file__).parent / "results"
RESULTS.mkdir(exist_ok=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--symbol", default="NQ=F")
    ap.add_argument("--interval", default="15m")
    ap.add_argument("--period", default="60d")
    ap.add_argument("--cash", type=float, default=100_000)
    ap.add_argument("--maximize", default="Sharpe Ratio",
                    help='Stat key to maximize. Examples: "Sharpe Ratio", '
                         '"Profit Factor", "Equity Final [$]", "SQN".')
    ap.add_argument("--method", default="grid", choices=["grid", "skopt"])
    args = ap.parse_args()

    df = fetch_nq(args.symbol, args.interval, args.period)
    print(f"Optimizing on {len(df):,} bars of {args.symbol} {args.interval}...")

    bt = Backtest(df, SignalForgeNQStrategy,
                  cash=args.cash, commission=0.0,
                  finalize_trades=True, exclusive_orders=True)

    grid = dict(
        sma_fast=range(5, 21, 2),
        sma_slow=range(20, 61, 5),
        sl_mult=[1.0, 1.5, 2.0, 2.5, 3.0],
        tp_mult=[1.5, 2.0, 2.5, 3.0, 4.0, 5.0],
        be_trigger=[0.5, 1.0, 1.5],
        # Logical constraint: fast must be strictly less than slow
        constraint=lambda p: p.sma_fast < p.sma_slow,
    )

    stats, heatmap = bt.optimize(
        **grid,
        maximize=args.maximize,
        return_heatmap=True,
        method=args.method,
        max_tries=300,
    )

    print("\n=== Optimization result ===")
    print(stats)

    tag = f"{args.symbol.replace('=','_')}_{args.interval}_{args.period}_optim"
    summary = {k: v for k, v in stats.items()
               if k not in ("_trades", "_equity_curve", "_strategy")}
    (RESULTS / f"{tag}_summary.json").write_text(
        json.dumps(summary, indent=2, default=str))
    heatmap.to_csv(RESULTS / f"{tag}_heatmap.csv")
    print(f"Saved -> {RESULTS}/{tag}_*")


if __name__ == "__main__":
    main()
