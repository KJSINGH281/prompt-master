"""
Data loader for NQ futures.

Pulls continuous front-month NQ futures from Yahoo Finance via yfinance.
Caches to local CSV so repeat backtests are instant.

yfinance intraday limits (per Yahoo's policy at time of writing):
  - 1m  : last  7 days
  - 5m  : last 60 days
  - 15m : last 60 days
  - 30m : last 60 days
  - 60m : last ~730 days
  - 1d  : full history
"""

from __future__ import annotations

import os
from pathlib import Path

import pandas as pd
import yfinance as yf

DATA_DIR = Path(__file__).parent / "data"
DATA_DIR.mkdir(exist_ok=True)


def fetch_nq(symbol: str = "NQ=F",
             interval: str = "15m",
             period: str = "60d",
             cache: bool = True) -> pd.DataFrame:
    """
    Download NQ futures bars and return a clean OHLCV DataFrame indexed by datetime.

    Backtesting.py wants columns named exactly: Open, High, Low, Close, Volume.
    """
    cache_path = DATA_DIR / f"{symbol.replace('=','_')}_{interval}_{period}.csv"
    if cache and cache_path.exists():
        df = pd.read_csv(cache_path, index_col=0, parse_dates=True)
        return _clean(df)

    print(f"[data_loader] Downloading {symbol} interval={interval} period={period} ...")
    df = yf.download(
        tickers=symbol,
        interval=interval,
        period=period,
        auto_adjust=False,
        progress=False,
    )
    if df is None or df.empty:
        raise RuntimeError(
            f"No data returned for {symbol} interval={interval} period={period}. "
            f"Check yfinance limits or your network connection."
        )

    # yfinance >=0.2 returns a MultiIndex when multiple tickers; flatten if so
    if isinstance(df.columns, pd.MultiIndex):
        df.columns = df.columns.get_level_values(0)

    df = _clean(df)
    if cache:
        df.to_csv(cache_path)
        print(f"[data_loader] Cached -> {cache_path}")
    return df


def _clean(df: pd.DataFrame) -> pd.DataFrame:
    keep = ["Open", "High", "Low", "Close", "Volume"]
    df = df[[c for c in keep if c in df.columns]].copy()
    df = df.dropna()
    df.index = pd.to_datetime(df.index)
    # Backtesting.py expects a sorted, unique, tz-naive index
    if df.index.tz is not None:
        df.index = df.index.tz_convert("US/Eastern").tz_localize(None)
    df = df[~df.index.duplicated(keep="last")].sort_index()
    return df


if __name__ == "__main__":
    bars = fetch_nq()
    print(bars.head())
    print(f"\nRows: {len(bars):,}   Range: {bars.index[0]} -> {bars.index[-1]}")
