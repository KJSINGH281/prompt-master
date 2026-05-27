"""
Signal Forge NQ — Python port of the NinjaScript strategy for backtesting.

Mirrors the logic of SignalForgeNQ.cs, but runs inside Backtesting.py so we
can validate the rules on real NQ data without NinjaTrader.

Modules supported (toggle via class attributes):
  • SMA crossover     • RSI filter        • MACD crossover
  • Stochastic        • Bollinger (close vs middle)
  • EMA crossover     • Awesome Oscillator (5/34 HL2 SMA)
  • Parabolic SAR     • CCI filter        • ADX + DI direction
  • Custom Supertrend
  • Session filter    • ATR Stop / Target / Trail / Break-even
  • Daily loss / max-trade halt
"""

from __future__ import annotations

import numpy as np
import pandas as pd
from backtesting import Strategy
from backtesting.lib import crossover  # noqa: F401  (kept for downstream tweaks)


# ------------------------------------------------------------------ helpers
def _sma(arr: pd.Series, n: int) -> pd.Series:
    return pd.Series(arr).rolling(n, min_periods=n).mean()


def _ema(arr: pd.Series, n: int) -> pd.Series:
    return pd.Series(arr).ewm(span=n, adjust=False).mean()


def _rsi(close: pd.Series, n: int = 14) -> pd.Series:
    delta = pd.Series(close).diff()
    gain = delta.clip(lower=0).ewm(alpha=1 / n, adjust=False).mean()
    loss = (-delta.clip(upper=0)).ewm(alpha=1 / n, adjust=False).mean()
    rs = gain / loss.replace(0, np.nan)
    return 100 - 100 / (1 + rs)


def _macd(close: pd.Series, fast: int, slow: int, signal: int):
    line = _ema(close, fast) - _ema(close, slow)
    sig = _ema(line, signal)
    return line, sig


def _atr(high, low, close, n: int = 14) -> pd.Series:
    h, l, c = pd.Series(high), pd.Series(low), pd.Series(close)
    tr = pd.concat(
        [h - l, (h - c.shift()).abs(), (l - c.shift()).abs()], axis=1
    ).max(axis=1)
    return tr.ewm(alpha=1 / n, adjust=False).mean()


def _stoch_k(high, low, close, k_period: int, smooth: int) -> pd.Series:
    h, l, c = pd.Series(high), pd.Series(low), pd.Series(close)
    ll = l.rolling(k_period).min()
    hh = h.rolling(k_period).max()
    raw_k = 100 * (c - ll) / (hh - ll).replace(0, np.nan)
    return raw_k.rolling(smooth).mean()


def _bb_middle(close: pd.Series, n: int) -> pd.Series:
    return _sma(close, n)


def _cci(high, low, close, n: int) -> pd.Series:
    tp = (pd.Series(high) + pd.Series(low) + pd.Series(close)) / 3.0
    sma = tp.rolling(n).mean()
    mad = tp.rolling(n).apply(lambda x: np.mean(np.abs(x - x.mean())), raw=False)
    return (tp - sma) / (0.015 * mad.replace(0, np.nan))


def _adx_dm(high, low, close, n: int):
    """Returns (adx, plus_di, minus_di) Wilder-smoothed."""
    h, l, c = pd.Series(high), pd.Series(low), pd.Series(close)
    up_move = h.diff()
    dn_move = -l.diff()
    plus_dm = np.where((up_move > dn_move) & (up_move > 0), up_move, 0.0)
    minus_dm = np.where((dn_move > up_move) & (dn_move > 0), dn_move, 0.0)
    tr = pd.concat([h - l, (h - c.shift()).abs(), (l - c.shift()).abs()], axis=1).max(axis=1)
    atr = tr.ewm(alpha=1 / n, adjust=False).mean()
    plus_di = 100 * pd.Series(plus_dm, index=h.index).ewm(alpha=1 / n, adjust=False).mean() / atr
    minus_di = 100 * pd.Series(minus_dm, index=h.index).ewm(alpha=1 / n, adjust=False).mean() / atr
    dx = 100 * (plus_di - minus_di).abs() / (plus_di + minus_di).replace(0, np.nan)
    adx = dx.ewm(alpha=1 / n, adjust=False).mean()
    return adx, plus_di, minus_di


def _psar(high, low, af_start=0.02, af_step=0.02, af_max=0.2) -> pd.Series:
    h = np.asarray(high, dtype=float)
    l = np.asarray(low, dtype=float)
    n = len(h)
    psar = np.full(n, np.nan)
    bull = True
    af = af_start
    ep = h[0]
    psar[0] = l[0]
    for i in range(1, n):
        prev = psar[i - 1]
        if bull:
            cur = prev + af * (ep - prev)
            cur = min(cur, l[i - 1], l[i - 2] if i >= 2 else l[i - 1])
            if l[i] < cur:
                bull = False
                cur = ep
                ep = l[i]
                af = af_start
            else:
                if h[i] > ep:
                    ep = h[i]
                    af = min(af + af_step, af_max)
        else:
            cur = prev + af * (ep - prev)
            cur = max(cur, h[i - 1], h[i - 2] if i >= 2 else h[i - 1])
            if h[i] > cur:
                bull = True
                cur = ep
                ep = h[i]
                af = af_start
            else:
                if l[i] < ep:
                    ep = l[i]
                    af = min(af + af_step, af_max)
        psar[i] = cur
    return pd.Series(psar)


def _supertrend(high, low, close, length: int, factor: float):
    """Returns (direction Series) where -1 = bullish, +1 = bearish (Pine convention)."""
    atr = _atr(high, low, close, length)
    hl2 = (pd.Series(high) + pd.Series(low)) / 2.0
    raw_up = hl2 + factor * atr
    raw_dn = hl2 - factor * atr
    n = len(close)
    upper = np.full(n, np.nan)
    lower = np.full(n, np.nan)
    direction = np.full(n, 1, dtype=int)
    upper[0], lower[0] = raw_up.iloc[0], raw_dn.iloc[0]
    for i in range(1, n):
        upper[i] = raw_up.iloc[i] if (raw_up.iloc[i] < upper[i - 1] or close[i - 1] > upper[i - 1]) else upper[i - 1]
        lower[i] = raw_dn.iloc[i] if (raw_dn.iloc[i] > lower[i - 1] or close[i - 1] < lower[i - 1]) else lower[i - 1]
        d = direction[i - 1]
        if d == -1 and close[i] < lower[i]:
            d = 1
        elif d == 1 and close[i] > upper[i]:
            d = -1
        direction[i] = d
    return pd.Series(direction)


def _ao(high, low) -> pd.Series:
    hl2 = (pd.Series(high) + pd.Series(low)) / 2.0
    return _sma(hl2, 5) - _sma(hl2, 34)


def _in_session(ts: pd.Timestamp, am: bool, pm: bool) -> bool:
    """09:30-11:30 (AM) and 13:30-15:30 (PM) ET. Index is already tz-naive ET."""
    t = ts.time()
    am_hit = am and (t >= pd.Timestamp("09:30").time()) and (t <= pd.Timestamp("11:30").time())
    pm_hit = pm and (t >= pd.Timestamp("13:30").time()) and (t <= pd.Timestamp("15:30").time())
    return am_hit or pm_hit


# ------------------------------------------------------------------ strategy
class SignalForgeNQStrategy(Strategy):
    """Backtesting.py port of the NinjaScript SignalForgeNQ strategy."""

    # ── Aggregation ────────────────────────────────────────────────────
    require_all = True             # AND mode

    # ── ATR risk ───────────────────────────────────────────────────────
    atr_len = 14
    sl_mult = 1.5
    tp_mult = 2.5
    use_sl = True
    use_tp = True
    use_trail = False
    trail_mult = 1.2
    use_be = True
    be_trigger = 1.0

    # ── Session ────────────────────────────────────────────────────────
    use_am = True
    use_pm = True

    # ── Daily risk ─────────────────────────────────────────────────────
    max_daily_loss = 300.0     # USD per NQ point * point value ~= dollars
    max_daily_trades = 5

    # ── Modules ────────────────────────────────────────────────────────
    use_sma = True;  sma_fast = 10; sma_slow = 20
    use_rsi = False; rsi_len = 14;  rsi_long = 50.0; rsi_short = 50.0
    use_macd = False; macd_fast = 12; macd_slow = 26; macd_signal = 9
    use_st = False;  st_len = 10;  st_factor = 3.0
    use_stoch = False; stoch_k = 14; stoch_d = 3; stoch_smooth = 3
    use_bb = False;  bb_len = 20
    use_ema = False; ema_fast = 10; ema_slow = 20
    use_ao = False
    use_sar = False; sar_start = 0.02; sar_inc = 0.02; sar_max = 0.2
    use_cci = False; cci_len = 20; cci_long = 0.0; cci_short = 0.0
    use_adx = False; adx_len = 14; adx_threshold = 25.0

    # NQ contract sizing — used for daily loss accounting
    point_value = 20.0   # NQ = $20/point ; MNQ = $2/point
    contract_size = 1

    # ──────────────────────────────────────────────────────────────────
    def init(self):
        c, h, l = self.data.Close, self.data.High, self.data.Low

        # Pre-compute everything once via Backtesting.py's I() wrapper so it
        # plays nicely with the engine and shows up on the chart if desired.
        self.sma_f = self.I(_sma, c, self.sma_fast, name="SMAfast", overlay=True)
        self.sma_s = self.I(_sma, c, self.sma_slow, name="SMAslow", overlay=True)
        self.ema_f = self.I(_ema, c, self.ema_fast, name="EMAfast")
        self.ema_s = self.I(_ema, c, self.ema_slow, name="EMAslow")
        self.rsi_v = self.I(_rsi, c, self.rsi_len, name="RSI")
        macd_line, macd_sig = _macd(pd.Series(c), self.macd_fast, self.macd_slow, self.macd_signal)
        self.macd_l = self.I(lambda: macd_line.values, name="MACD")
        self.macd_s = self.I(lambda: macd_sig.values, name="MACDsig")
        self.atr_v = self.I(_atr, h, l, c, self.atr_len, name="ATR")
        self.stoch_kv = self.I(_stoch_k, h, l, c, self.stoch_k, self.stoch_smooth, name="StochK")
        self.bb_mid = self.I(_bb_middle, c, self.bb_len, name="BBmid", overlay=True)
        self.cci_v = self.I(_cci, h, l, c, self.cci_len, name="CCI")
        self.psar_v = self.I(_psar, h, l, self.sar_start, self.sar_inc, self.sar_max, name="PSAR", overlay=True)
        self.ao_v = self.I(_ao, h, l, name="AO")
        adx, di_p, di_m = _adx_dm(pd.Series(h), pd.Series(l), pd.Series(c), self.adx_len)
        self.adx_v = self.I(lambda: adx.values, name="ADX")
        self.di_p = self.I(lambda: di_p.values, name="DI+")
        self.di_m = self.I(lambda: di_m.values, name="DI-")
        self.st_dir = self.I(_supertrend, h, l, c, self.st_len, self.st_factor, name="STdir")

        # State
        self._prev_long = False
        self._prev_short = False
        self._entry_atr = float("nan")
        self._entry_dir = 0
        self._dyn_sl = float("nan")
        self._be_armed = False

        # Daily counters
        self._day = None
        self._daily_pnl = 0.0
        self._daily_trades = 0
        self._equity_at_day_open = self.equity

    # ──────────────────────────────────────────────────────────────────
    def _flags(self):
        i = -1   # current bar
        c, h, l = self.data.Close, self.data.High, self.data.Low

        bulls, bears, any_on = [], [], False

        def add(flag_bull, flag_bear):
            nonlocal any_on
            bulls.append(flag_bull)
            bears.append(flag_bear)
            any_on = True

        if self.use_sma:
            add(self.sma_f[i] > self.sma_s[i], self.sma_f[i] < self.sma_s[i])
        if self.use_rsi:
            add(self.rsi_v[i] > self.rsi_long, self.rsi_v[i] < self.rsi_short)
        if self.use_macd:
            add(self.macd_l[i] > self.macd_s[i], self.macd_l[i] < self.macd_s[i])
        if self.use_st:
            add(self.st_dir[i] == -1, self.st_dir[i] == 1)
        if self.use_stoch:
            add(self.stoch_kv[i] > 50, self.stoch_kv[i] < 50)
        if self.use_bb:
            add(c[i] > self.bb_mid[i], c[i] < self.bb_mid[i])
        if self.use_ema:
            add(self.ema_f[i] > self.ema_s[i], self.ema_f[i] < self.ema_s[i])
        if self.use_ao:
            add(self.ao_v[i] > 0, self.ao_v[i] < 0)
        if self.use_sar:
            add(c[i] > self.psar_v[i], c[i] < self.psar_v[i])
        if self.use_cci:
            add(self.cci_v[i] > self.cci_long, self.cci_v[i] < self.cci_short)
        if self.use_adx:
            strong = self.adx_v[i] > self.adx_threshold
            add(strong and self.di_p[i] > self.di_m[i],
                strong and self.di_m[i] > self.di_p[i])

        if not any_on:
            return False, False
        if self.require_all:
            return all(bulls), all(bears)
        return any(bulls), any(bears)

    # ──────────────────────────────────────────────────────────────────
    def next(self):
        ts: pd.Timestamp = self.data.index[-1]
        price: float = self.data.Close[-1]
        atr: float = self.atr_v[-1]
        if np.isnan(atr) or atr <= 0:
            return

        # daily reset (track equity-based daily P&L for halt)
        day = ts.date()
        if self._day != day:
            self._day = day
            self._daily_pnl = 0.0
            self._daily_trades = 0
            self._equity_at_day_open = self.equity

        # daily realised P&L since session open
        self._daily_pnl = self.equity - self._equity_at_day_open
        halt = (self._daily_pnl <= -self.max_daily_loss
                or self._daily_trades >= self.max_daily_trades)

        in_session = _in_session(ts, self.use_am, self.use_pm)

        long_cond, short_cond = self._flags()
        new_long = long_cond and not self._prev_long
        new_short = short_cond and not self._prev_short

        # ── Manage open positions: break-even + trailing ──────────────
        if self.position and not np.isnan(self._entry_atr):
            if self.position.is_long:
                if self.use_be and not self._be_armed:
                    if price >= self.trades[-1].entry_price + self._entry_atr * self.be_trigger:
                        self.trades[-1].sl = max(
                            self.trades[-1].sl or -np.inf,
                            self.trades[-1].entry_price + 2 * 0.25,  # +2 ticks
                        )
                        self._be_armed = True
                if self.use_trail:
                    candidate = price - self._entry_atr * self.trail_mult
                    if candidate > (self.trades[-1].sl or -np.inf):
                        self.trades[-1].sl = candidate
            elif self.position.is_short:
                if self.use_be and not self._be_armed:
                    if price <= self.trades[-1].entry_price - self._entry_atr * self.be_trigger:
                        self.trades[-1].sl = min(
                            self.trades[-1].sl or np.inf,
                            self.trades[-1].entry_price - 2 * 0.25,
                        )
                        self._be_armed = True
                if self.use_trail:
                    candidate = price + self._entry_atr * self.trail_mult
                    if candidate < (self.trades[-1].sl or np.inf):
                        self.trades[-1].sl = candidate

        # ── Opposing-signal exits (Pine parity) ───────────────────────
        if self.position.is_long and short_cond and not self._prev_short:
            self.position.close()
        if self.position.is_short and long_cond and not self._prev_long:
            self.position.close()

        # ── Entries ───────────────────────────────────────────────────
        can_enter = (not self.position) and in_session and not halt
        if can_enter and new_long:
            sl = price - atr * (self.trail_mult if self.use_trail else self.sl_mult) if (self.use_sl or self.use_trail) else None
            tp = price + atr * self.tp_mult if self.use_tp else None
            self.buy(size=self.contract_size, sl=sl, tp=tp)
            self._entry_atr = atr
            self._entry_dir = 1
            self._dyn_sl = sl
            self._be_armed = False
            self._daily_trades += 1
        elif can_enter and new_short:
            sl = price + atr * (self.trail_mult if self.use_trail else self.sl_mult) if (self.use_sl or self.use_trail) else None
            tp = price - atr * self.tp_mult if self.use_tp else None
            self.sell(size=self.contract_size, sl=sl, tp=tp)
            self._entry_atr = atr
            self._entry_dir = -1
            self._dyn_sl = sl
            self._be_armed = False
            self._daily_trades += 1

        self._prev_long = long_cond
        self._prev_short = short_cond
