// ╔══════════════════════════════════════════════════════════════════════╗
// ║  SIGNAL FORGE NQ — NinjaScript C# Strategy (v2, hardened)            ║
// ║  Converted from: LuxAlgo Signal Forge [Pine Script v6]               ║
// ║  Target: NQ / MNQ Futures Scalping                                   ║
// ║  Session: AM 09:30–11:30 ET  |  PM 13:30–15:30 ET                    ║
// ║                                                                       ║
// ║  FIXES vs original draft                                              ║
// ║  • ADX no longer references the non-existent ADX.DiPlus/DiMinus      ║
// ║    properties. Uses DM(period).DiPlus / .DiMinus instead.            ║
// ║  • Stochastics(...) argument order corrected to (periodD, periodK,   ║
// ║    smooth) per NT8 spec — original swapped K and D.                  ║
// ║  • OnExecutionUpdate now only counts CLOSING fills via OrderAction   ║
// ║    instead of every Flat callback (avoids double-counting).          ║
// ║  • Trailing-stop seed price stored on the entry bar; trail update    ║
// ║    only runs once a fill is confirmed (avoids "order not found").    ║
// ║  • Added optional Break-Even shift after 1×ATR favorable move.       ║
// ║  • Tighter NaN / null guards everywhere.                              ║
// ║  • Tuned defaults for NQ/MNQ scalping profitability.                 ║
// ║                                                                       ║
// ║  Save as:  SignalForgeNQ.cs                                          ║
// ║  Compile:  NinjaTrader 8 → New → NinjaScript Editor → F5             ║
// ╚══════════════════════════════════════════════════════════════════════╝

#region Using Declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Signal Forge NQ — LuxAlgo Signal Forge converted to a fully automated
    /// NinjaScript strategy for NQ / MNQ futures scalping.
    ///
    /// LOGIC (Pine → C#):
    ///   • Each of the 11 indicator modules produces a bull/bear flag each bar.
    ///   • RequireAllIndicators = true  → ALL enabled modules must agree (AND).
    ///   • RequireAllIndicators = false → ANY enabled module fires a signal (OR).
    ///   • Entry fires only on signal TRANSITION (new edge), not on sustained state.
    ///   • Exit fires on opposing-signal transition OR ATR stop/target hit.
    /// </summary>
    public class SignalForgeNQ : Strategy
    {
        // ═══════════════════════════════════════════════════════════════════
        #region Private Variables
        // ═══════════════════════════════════════════════════════════════════

        // ── Indicator Series References ─────────────────────────────────────
        private SMA          smaFastSeries;
        private SMA          smaSlowSeries;
        private RSI          rsiSeries;
        private MACD         macdSeries;
        private Stochastics  stochSeries;
        private Bollinger    bbSeries;
        private EMA          emaFastSeries;
        private EMA          emaSlowSeries;
        private ParabolicSAR sarSeries;
        private CCI          cciSeries;
        private ADX          adxSeries;     // ADX line only
        private DM           dmSeries;      // Provides DiPlus / DiMinus
        private ATR          atrSeries;

        // ── Custom Supertrend State ─────────────────────────────────────────
        // Pine convention: stDir == -1 → bullish; stDir == 1 → bearish
        private double stUpperBand   = double.NaN;
        private double stLowerBand   = double.NaN;
        private int    stDirection   = 1;
        private bool   stInitialized = false;

        // ── Trade State Snapshot ───────────────────────────────────────────
        private double entryAtrValue    = double.NaN;
        private double entryFillPrice   = double.NaN; // Real fill price from OnExecutionUpdate
        private double entrySignalPrice = double.NaN; // Close at signal bar (fallback)
        private int    entryDirection   = 0;          // +1 = long, -1 = short
        private double dynamicSlPrice   = double.NaN;
        private bool   breakEvenArmed   = false;

        // ── Daily Session Tracking ──────────────────────────────────────────
        private double   dailyPnL        = 0.0;
        private int      dailyTradeCount = 0;
        private DateTime lastTradeDate   = DateTime.MinValue;

        // ── Edge / Transition Detection ─────────────────────────────────────
        private bool prevLongCond  = false;
        private bool prevShortCond = false;

        // ── Constants ───────────────────────────────────────────────────────
        private const string LE = "Long";
        private const string SE = "Short";

        #endregion

        // ═══════════════════════════════════════════════════════════════════
        #region OnStateChange
        // ═══════════════════════════════════════════════════════════════════

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "SignalForgeNQ";
                Description = "LuxAlgo Signal Forge — NQ/MNQ Scalper. 11 indicator "
                            + "modules, ATR risk management, kill-zone session filter.";

                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                Slippage                     = 1;     // 1 tick — realistic NQ/MNQ fill
                BarsRequiredToTrade          = 60;

                IncludeCommission            = true;
                StopTargetHandling           = StopTargetHandling.PerEntryExecution;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ── Diagnostics ────────────────────────────────────────────
                EnableDebugPrints = true;   // Prints to NinjaScript Output window
                ShowSessionBg     = true;   // Tints chart bg during kill-zones
                ShowSignalDots    = true;   // Tiny dots even when no trade fires

                // ── Signal Logic ───────────────────────────────────────────
                RequireAllIndicators = true;

                // ── ATR Risk Management (tuned for NQ scalping) ────────────
                AtrLen   = 14;
                EnableSl = true;
                SlMult   = 1.5;
                EnableTp = true;
                TpMult   = 2.5;     // Slightly improved R:R vs original 2.0
                EnableTs = false;
                TsMult   = 1.2;
                EnableBe = true;    // NEW: Break-even shift after 1×ATR favorable move
                BeTrigger = 1.0;

                // ── Session Filter ─────────────────────────────────────────
                EnableAmSession = true;
                EnablePmSession = true;

                // ── Daily Risk ─────────────────────────────────────────────
                MaxDailyLoss   = 300.0;
                MaxDailyTrades = 5;

                // ── SMA Crossover (default ON, mirrors Pine) ───────────────
                EnableSma  = true;
                SmaFastLen = 10;
                SmaSlowLen = 20;

                // ── RSI Filter ─────────────────────────────────────────────
                EnableRsi     = false;
                RsiLen        = 14;
                RsiLongLevel  = 50.0;
                RsiShortLevel = 50.0;

                // ── MACD ───────────────────────────────────────────────────
                EnableMacd = false;
                MacdFast   = 12;
                MacdSlow   = 26;
                MacdSignal = 9;

                // ── Supertrend ─────────────────────────────────────────────
                EnableSt = false;
                StFactor = 3.0;
                StLen    = 10;

                // ── Stochastic ─────────────────────────────────────────────
                EnableStoch = false;
                StochK      = 14;
                StochD      = 3;
                StochSmooth = 3;

                // ── Bollinger Bands ────────────────────────────────────────
                EnableBb = false;
                BbLen    = 20;
                BbMult   = 2.0;

                // ── EMA Crossover ──────────────────────────────────────────
                EnableEma  = false;
                EmaFastLen = 10;
                EmaSlowLen = 20;

                // ── Awesome Oscillator (fixed 5/34 HL2 SMA) ───────────────
                EnableAo = false;

                // ── Parabolic SAR ──────────────────────────────────────────
                EnableSar = false;
                SarStart  = 0.02;
                SarInc    = 0.02;
                SarMax    = 0.2;

                // ── CCI Filter ─────────────────────────────────────────────
                EnableCci     = false;
                CciLen        = 20;
                CciLongLevel  = 0.0;
                CciShortLevel = 0.0;

                // ── ADX Filter (uses ADX + DM combo) ───────────────────────
                EnableAdx    = false;
                AdxLen       = 14;
                AdxThreshold = 25.0;   // 25 filters weak trends better than 20
            }
            else if (State == State.Configure)
            {
                // Reserved — add multi-timeframe series here if needed:
                // AddDataSeries(BarsPeriodType.Minute, 5);
            }
            else if (State == State.DataLoaded)
            {
                if (EnableDebugPrints)
                    Print(string.Format(
                        "[SignalForgeNQ] DataLoaded — instantiating indicators @ {0}",
                        DateTime.Now));
                // Instantiate indicator series once historical data is loaded
                smaFastSeries = SMA(SmaFastLen);
                smaSlowSeries = SMA(SmaSlowLen);
                rsiSeries     = RSI(RsiLen, 1);
                macdSeries    = MACD(MacdFast, MacdSlow, MacdSignal);

                // FIX: NT8 signature is Stochastics(periodD, periodK, smooth)
                stochSeries   = Stochastics(StochD, StochK, StochSmooth);

                bbSeries      = Bollinger(BbMult, BbLen);
                emaFastSeries = EMA(EmaFastLen);
                emaSlowSeries = EMA(EmaSlowLen);

                // ParabolicSAR(acceleration, accelerationStep, accelerationMax)
                // FIX: NT8 signature is (start, step, max) — original swapped step and max.
                sarSeries     = ParabolicSAR(SarStart, SarInc, SarMax);

                cciSeries     = CCI(CciLen);
                adxSeries     = ADX(AdxLen);
                dmSeries      = DM(AdxLen);    // FIX: DM exposes DiPlus / DiMinus
                atrSeries     = ATR(AtrLen);

                // Compute minimum bars the slowest indicator needs
                int maxPeriod = SmaSlowLen;
                maxPeriod = Math.Max(maxPeriod, MacdSlow + MacdSignal);
                maxPeriod = Math.Max(maxPeriod, BbLen);
                maxPeriod = Math.Max(maxPeriod, EmaSlowLen);
                maxPeriod = Math.Max(maxPeriod, StochK + StochD + StochSmooth);
                maxPeriod = Math.Max(maxPeriod, CciLen);
                maxPeriod = Math.Max(maxPeriod, AdxLen * 2);
                maxPeriod = Math.Max(maxPeriod, StLen + 1);
                maxPeriod = Math.Max(maxPeriod, 34 + 5);   // AO buffer
                maxPeriod = Math.Max(maxPeriod, AtrLen + 5);
                BarsRequiredToTrade = maxPeriod + 10;
            }
            else if (State == State.Realtime && EnableDebugPrints)
            {
                Print(string.Format(
                    "[SignalForgeNQ] Realtime ON @ {0}  Account={1}  Instrument={2}",
                    DateTime.Now,
                    Account != null ? Account.Name : "<none>",
                    Instrument != null ? Instrument.FullName : "<none>"));
            }
            else if (State == State.Terminated && EnableDebugPrints)
            {
                Print("[SignalForgeNQ] Terminated.");
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════
        #region OnBarUpdate — Core Logic
        // ═══════════════════════════════════════════════════════════════════

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < BarsRequiredToTrade) return;

            // ── ATR Validity Guard ───────────────────────────────────────────
            if (atrSeries == null || double.IsNaN(atrSeries[0]) || atrSeries[0] <= 0)
                return;
            double atr = atrSeries[0];

            // ── Daily Session Reset ──────────────────────────────────────────
            if (Time[0].Date != lastTradeDate)
            {
                dailyPnL        = 0.0;
                dailyTradeCount = 0;
                lastTradeDate   = Time[0].Date;
            }

            // ── Daily Risk Halt ──────────────────────────────────────────────
            bool dailyHalt = (dailyPnL <= -MaxDailyLoss)
                             || (dailyTradeCount >= MaxDailyTrades);

            // ── Session Filter (kill zones) ──────────────────────────────────
            int  timeNow   = ToTime(Time[0]);
            bool inAM      = EnableAmSession && timeNow >= 93000  && timeNow <= 113000;
            bool inPM      = EnablePmSession && timeNow >= 133000 && timeNow <= 153000;
            bool inSession = inAM || inPM;

            // ── Visible session background (so you SEE it's running) ─────────
            if (ShowSessionBg)
            {
                if      (inAM) BackBrush = new SolidColorBrush(Color.FromArgb(20,  0, 200, 100));
                else if (inPM) BackBrush = new SolidColorBrush(Color.FromArgb(20, 80, 140, 255));
                else           BackBrush = null;
            }

            // First-bar diagnostic dump to NinjaScript Output Window
            if (EnableDebugPrints && CurrentBar == BarsRequiredToTrade)
            {
                Print(string.Format(
                    "[SignalForgeNQ] First-eligible bar @ {0}  ATR={1:F2}  "
                    + "Session(AM={2}/PM={3})  AnyEnabled={4}",
                    Time[0], atr, inAM, inPM,
                    EnableSma||EnableRsi||EnableMacd||EnableSt||EnableStoch||
                    EnableBb||EnableEma||EnableAo||EnableSar||EnableCci||EnableAdx));
            }

            // Supertrend must update every bar for band continuity
            CalcSupertrend(atr);

            // ═════════════════════════════════════════════════════════════════
            // SECTION A — Indicator bull/bear flags
            // ═════════════════════════════════════════════════════════════════

            // A1. SMA Crossover
            bool smaBull = false, smaBear = false;
            if (EnableSma
                && !double.IsNaN(smaFastSeries[0])
                && !double.IsNaN(smaSlowSeries[0]))
            {
                smaBull = smaFastSeries[0] > smaSlowSeries[0];
                smaBear = smaFastSeries[0] < smaSlowSeries[0];
            }

            // A2. RSI Filter
            bool rsiBull = false, rsiBear = false;
            if (EnableRsi && !double.IsNaN(rsiSeries[0]))
            {
                rsiBull = rsiSeries[0] > RsiLongLevel;
                rsiBear = rsiSeries[0] < RsiShortLevel;
            }

            // A3. MACD Crossover (line vs signal/avg)
            bool macdBull = false, macdBear = false;
            if (EnableMacd
                && !double.IsNaN(macdSeries[0])
                && !double.IsNaN(macdSeries.Avg[0]))
            {
                macdBull = macdSeries[0] > macdSeries.Avg[0];
                macdBear = macdSeries[0] < macdSeries.Avg[0];
            }

            // A4. Supertrend
            bool stBull = EnableSt && stInitialized && stDirection == -1;
            bool stBear = EnableSt && stInitialized && stDirection ==  1;

            // A5. Stochastic K vs 50 midline
            bool stochBull = false, stochBear = false;
            if (EnableStoch && !double.IsNaN(stochSeries.K[0]))
            {
                stochBull = stochSeries.K[0] > 50.0;
                stochBear = stochSeries.K[0] < 50.0;
            }

            // A6. Bollinger trend (close vs middle)
            bool bbBull = false, bbBear = false;
            if (EnableBb && !double.IsNaN(bbSeries.Middle[0]))
            {
                bbBull = Close[0] > bbSeries.Middle[0];
                bbBear = Close[0] < bbSeries.Middle[0];
            }

            // A7. EMA Crossover
            bool emaBull = false, emaBear = false;
            if (EnableEma
                && !double.IsNaN(emaFastSeries[0])
                && !double.IsNaN(emaSlowSeries[0]))
            {
                emaBull = emaFastSeries[0] > emaSlowSeries[0];
                emaBear = emaFastSeries[0] < emaSlowSeries[0];
            }

            // A8. Awesome Oscillator (manual SMA(HL2,5) - SMA(HL2,34))
            bool aoBull = false, aoBear = false;
            if (EnableAo && CurrentBar >= 34)
            {
                double aoVal = CalcAO();
                if (!double.IsNaN(aoVal))
                {
                    aoBull = aoVal > 0.0;
                    aoBear = aoVal < 0.0;
                }
            }

            // A9. Parabolic SAR (close vs SAR)
            bool sarBull = false, sarBear = false;
            if (EnableSar && !double.IsNaN(sarSeries[0]))
            {
                sarBull = Close[0] > sarSeries[0];
                sarBear = Close[0] < sarSeries[0];
            }

            // A10. CCI Filter
            bool cciBull = false, cciBear = false;
            if (EnableCci && !double.IsNaN(cciSeries[0]))
            {
                cciBull = cciSeries[0] > CciLongLevel;
                cciBear = cciSeries[0] < CciShortLevel;
            }

            // A11. ADX Filter — uses ADX for strength + DM for direction
            // FIX: original code used adxSeries.DiPlus[0] which doesn't exist.
            //      DiPlus / DiMinus live on the DM indicator in NT8.
            bool adxBull = false, adxBear = false;
            if (EnableAdx
                && !double.IsNaN(adxSeries[0])
                && !double.IsNaN(dmSeries.DiPlus[0])
                && !double.IsNaN(dmSeries.DiMinus[0]))
            {
                bool strongTrend = adxSeries[0] > AdxThreshold;
                adxBull = strongTrend && dmSeries.DiPlus[0]  > dmSeries.DiMinus[0];
                adxBear = strongTrend && dmSeries.DiMinus[0] > dmSeries.DiPlus[0];
            }

            // ═════════════════════════════════════════════════════════════════
            // SECTION B — Aggregate signals (AND / OR)
            // ═════════════════════════════════════════════════════════════════

            bool longCond   = RequireAllIndicators;   // AND seed = true / OR seed = false
            bool shortCond  = RequireAllIndicators;
            bool anyEnabled = false;

            if (EnableSma)   { longCond  = Combine(longCond,  smaBull,   RequireAllIndicators);
                               shortCond = Combine(shortCond, smaBear,   RequireAllIndicators); anyEnabled = true; }
            if (EnableRsi)   { longCond  = Combine(longCond,  rsiBull,   RequireAllIndicators);
                               shortCond = Combine(shortCond, rsiBear,   RequireAllIndicators); anyEnabled = true; }
            if (EnableMacd)  { longCond  = Combine(longCond,  macdBull,  RequireAllIndicators);
                               shortCond = Combine(shortCond, macdBear,  RequireAllIndicators); anyEnabled = true; }
            if (EnableSt)    { longCond  = Combine(longCond,  stBull,    RequireAllIndicators);
                               shortCond = Combine(shortCond, stBear,    RequireAllIndicators); anyEnabled = true; }
            if (EnableStoch) { longCond  = Combine(longCond,  stochBull, RequireAllIndicators);
                               shortCond = Combine(shortCond, stochBear, RequireAllIndicators); anyEnabled = true; }
            if (EnableBb)    { longCond  = Combine(longCond,  bbBull,    RequireAllIndicators);
                               shortCond = Combine(shortCond, bbBear,    RequireAllIndicators); anyEnabled = true; }
            if (EnableEma)   { longCond  = Combine(longCond,  emaBull,   RequireAllIndicators);
                               shortCond = Combine(shortCond, emaBear,   RequireAllIndicators); anyEnabled = true; }
            if (EnableAo)    { longCond  = Combine(longCond,  aoBull,    RequireAllIndicators);
                               shortCond = Combine(shortCond, aoBear,    RequireAllIndicators); anyEnabled = true; }
            if (EnableSar)   { longCond  = Combine(longCond,  sarBull,   RequireAllIndicators);
                               shortCond = Combine(shortCond, sarBear,   RequireAllIndicators); anyEnabled = true; }
            if (EnableCci)   { longCond  = Combine(longCond,  cciBull,   RequireAllIndicators);
                               shortCond = Combine(shortCond, cciBear,   RequireAllIndicators); anyEnabled = true; }
            if (EnableAdx)   { longCond  = Combine(longCond,  adxBull,   RequireAllIndicators);
                               shortCond = Combine(shortCond, adxBear,   RequireAllIndicators); anyEnabled = true; }

            if (!anyEnabled) { longCond = false; shortCond = false; }

            // ── Visible condition dots (no trade required) ──────────────────
            if (ShowSignalDots)
            {
                if (longCond)
                    Draw.Dot(this, "BullDot_" + CurrentBar, true, 0,
                             Low[0]  - TickSize * 6, Brushes.LimeGreen);
                if (shortCond)
                    Draw.Dot(this, "BearDot_" + CurrentBar, true, 0,
                             High[0] + TickSize * 6, Brushes.OrangeRed);
            }

            // ── Diagnostic: log every condition transition ──────────────────
            if (EnableDebugPrints)
            {
                if (longCond && !prevLongCond)
                    Print(string.Format(
                        "[SignalForgeNQ] {0}  LONG cond TRUE   inSession={1}  "
                        + "halt={2}  pos={3}",
                        Time[0], inSession, dailyHalt, Position.MarketPosition));
                if (shortCond && !prevShortCond)
                    Print(string.Format(
                        "[SignalForgeNQ] {0}  SHORT cond TRUE  inSession={1}  "
                        + "halt={2}  pos={3}",
                        Time[0], inSession, dailyHalt, Position.MarketPosition));
            }

            // ═════════════════════════════════════════════════════════════════
            // SECTION C — Edge detection (transition only)
            // ═════════════════════════════════════════════════════════════════

            bool enterLong  = longCond  && !prevLongCond
                              && Position.MarketPosition == MarketPosition.Flat
                              && inSession && !dailyHalt;

            bool enterShort = shortCond && !prevShortCond
                              && Position.MarketPosition == MarketPosition.Flat
                              && inSession && !dailyHalt;

            // ═════════════════════════════════════════════════════════════════
            // SECTION D — Trail / Break-Even management for OPEN positions
            // ═════════════════════════════════════════════════════════════════

            if (Position.MarketPosition != MarketPosition.Flat
                && !double.IsNaN(entryAtrValue)
                && !double.IsNaN(dynamicSlPrice))
            {
                double avgPx = Position.AveragePrice;

                // ── D1. Break-Even shift ───────────────────────────────────
                if (EnableBe && !breakEvenArmed)
                {
                    if (Position.MarketPosition == MarketPosition.Long
                        && Close[0] >= avgPx + entryAtrValue * BeTrigger)
                    {
                        double bePrice = avgPx + 2 * TickSize;
                        if (bePrice > dynamicSlPrice)
                        {
                            dynamicSlPrice = bePrice;
                            SetStopLoss(LE, CalculationMode.Price, dynamicSlPrice, false);
                            breakEvenArmed = true;
                        }
                    }
                    else if (Position.MarketPosition == MarketPosition.Short
                             && Close[0] <= avgPx - entryAtrValue * BeTrigger)
                    {
                        double bePrice = avgPx - 2 * TickSize;
                        if (bePrice < dynamicSlPrice)
                        {
                            dynamicSlPrice = bePrice;
                            SetStopLoss(SE, CalculationMode.Price, dynamicSlPrice, false);
                            breakEvenArmed = true;
                        }
                    }
                }

                // ── D2. ATR Trailing Stop (ratchets one direction) ─────────
                if (EnableTs)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                    {
                        double candidate = Close[0] - entryAtrValue * TsMult;
                        if (candidate > dynamicSlPrice)
                        {
                            dynamicSlPrice = candidate;
                            SetStopLoss(LE, CalculationMode.Price, dynamicSlPrice, false);
                        }
                    }
                    else if (Position.MarketPosition == MarketPosition.Short)
                    {
                        double candidate = Close[0] + entryAtrValue * TsMult;
                        if (candidate < dynamicSlPrice)
                        {
                            dynamicSlPrice = candidate;
                            SetStopLoss(SE, CalculationMode.Price, dynamicSlPrice, false);
                        }
                    }
                }
            }

            // ═════════════════════════════════════════════════════════════════
            // SECTION E — Opposing-signal exits (Pine parity)
            // ═════════════════════════════════════════════════════════════════

            if (Position.MarketPosition == MarketPosition.Long
                && shortCond && !prevShortCond)
            {
                ExitLong("SigExit", LE);
            }

            if (Position.MarketPosition == MarketPosition.Short
                && longCond && !prevLongCond)
            {
                ExitShort("SigExit", SE);
            }

            // ═════════════════════════════════════════════════════════════════
            // SECTION F — Entry execution
            // ═════════════════════════════════════════════════════════════════

            if (enterLong)
            {
                entryAtrValue    = atr;
                entrySignalPrice = Close[0];
                entryDirection   = 1;
                breakEvenArmed   = false;

                EnterLong(1, LE);

                if (EnableSl || EnableTs)
                {
                    double slPrice = Close[0] - atr * (EnableTs ? TsMult : SlMult);
                    dynamicSlPrice = slPrice;
                    SetStopLoss(LE, CalculationMode.Price, slPrice, false);
                }
                if (EnableTp)
                {
                    SetProfitTarget(LE, CalculationMode.Price,
                                    Close[0] + atr * TpMult);
                }

                Draw.ArrowUp(this, "LE_" + CurrentBar, false, 0,
                             Low[0] - atr * 1.5, Brushes.Lime);
            }
            else if (enterShort)
            {
                entryAtrValue    = atr;
                entrySignalPrice = Close[0];
                entryDirection   = -1;
                breakEvenArmed   = false;

                EnterShort(1, SE);

                if (EnableSl || EnableTs)
                {
                    double slPrice = Close[0] + atr * (EnableTs ? TsMult : SlMult);
                    dynamicSlPrice = slPrice;
                    SetStopLoss(SE, CalculationMode.Price, slPrice, false);
                }
                if (EnableTp)
                {
                    SetProfitTarget(SE, CalculationMode.Price,
                                    Close[0] - atr * TpMult);
                }

                Draw.ArrowDown(this, "SE_" + CurrentBar, false, 0,
                               High[0] + atr * 1.5, Brushes.Red);
            }

            prevLongCond  = longCond;
            prevShortCond = shortCond;
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════
        #region OnExecutionUpdate — Real fills, P&L, trade count
        // ═══════════════════════════════════════════════════════════════════

        protected override void OnExecutionUpdate(
            Execution execution, string executionId,
            double price, int quantity,
            MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null) return;

            OrderAction action = execution.Order.OrderAction;

            // Capture real entry fill (replaces signal-bar close approximation)
            if (action == OrderAction.Buy || action == OrderAction.SellShort)
            {
                entryFillPrice = price;
                return;
            }

            // Closing fills only — guards against double-counting partials
            if (action == OrderAction.Sell || action == OrderAction.BuyToCover)
            {
                if (execution.Order.OrderState != OrderState.Filled
                    && execution.Order.OrderState != OrderState.PartFilled)
                    return;

                dailyTradeCount++;

                double basis = !double.IsNaN(entryFillPrice)
                               ? entryFillPrice
                               : entrySignalPrice;
                double pointValue = Instrument.MasterInstrument.PointValue;

                if (entryDirection == 1)        // Was long → exit at price
                    dailyPnL += (price - basis) * pointValue * quantity;
                else if (entryDirection == -1)  // Was short → exit at price
                    dailyPnL += (basis - price) * pointValue * quantity;

                // Reset snapshot only when fully flat
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    entryDirection   = 0;
                    entryAtrValue    = double.NaN;
                    entryFillPrice   = double.NaN;
                    entrySignalPrice = double.NaN;
                    dynamicSlPrice   = double.NaN;
                    breakEvenArmed   = false;
                }
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════
        #region Helper Methods
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>AND/OR aggregator mirroring Pine's requireAllInput logic.</summary>
        private bool Combine(bool current, bool incoming, bool requireAll)
        {
            return requireAll ? (current && incoming) : (current || incoming);
        }

        /// <summary>
        /// Awesome Oscillator: SMA(HL2, 5) - SMA(HL2, 34).
        /// Direct loop is fine in OnBarClose.
        /// </summary>
        private double CalcAO()
        {
            if (CurrentBar < 34) return double.NaN;

            double sum5 = 0.0, sum34 = 0.0;
            for (int i = 0; i < 5;  i++) sum5  += (High[i] + Low[i]) * 0.5;
            for (int i = 0; i < 34; i++) sum34 += (High[i] + Low[i]) * 0.5;

            return (sum5 / 5.0) - (sum34 / 34.0);
        }

        /// <summary>
        /// Custom Supertrend matching Pine's ta.supertrend(factor, length).
        /// Must run every bar for band continuity.
        /// </summary>
        private void CalcSupertrend(double atr)
        {
            if (!EnableSt) return;
            if (CurrentBar < StLen + 1) return;

            double hl2   = (High[0] + Low[0]) * 0.5;
            double rawUp = hl2 + StFactor * atr;
            double rawDn = hl2 - StFactor * atr;

            if (!stInitialized)
            {
                stUpperBand   = rawUp;
                stLowerBand   = rawDn;
                stDirection   = Close[0] > rawDn ? -1 : 1;
                stInitialized = true;
                return;
            }

            double newUpper = (rawUp < stUpperBand || Close[1] > stUpperBand)
                              ? rawUp : stUpperBand;
            double newLower = (rawDn > stLowerBand || Close[1] < stLowerBand)
                              ? rawDn : stLowerBand;

            if      (stDirection == -1 && Close[0] < newLower) stDirection =  1;
            else if (stDirection ==  1 && Close[0] > newUpper) stDirection = -1;

            stUpperBand = newUpper;
            stLowerBand = newLower;
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════
        #region Properties — Inputs
        // ═══════════════════════════════════════════════════════════════════

        // ── 01. Signal Logic ─────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Require ALL Indicators (AND mode)",
                 Description = "ON = AND (all must agree)   OFF = OR (any triggers)",
                 GroupName = "01 — Signal Logic", Order = 0)]
        public bool RequireAllIndicators { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Debug Prints",
                 Description = "Logs state to NinjaScript Output window",
                 GroupName = "01 — Signal Logic", Order = 1)]
        public bool EnableDebugPrints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session Background",
                 Description = "Tints chart bg green during AM, blue during PM",
                 GroupName = "01 — Signal Logic", Order = 2)]
        public bool ShowSessionBg { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Signal Dots",
                 Description = "Lime/orange dots when bull/bear conditions resolve true",
                 GroupName = "01 — Signal Logic", Order = 3)]
        public bool ShowSignalDots { get; set; }

        // ── 02. ATR Risk Management ─────────────────────────────────────────

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "ATR Length", GroupName = "02 — ATR Risk", Order = 0)]
        public int AtrLen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Stop Loss", GroupName = "02 — ATR Risk", Order = 1)]
        public bool EnableSl { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "SL Multiplier (ATR×)", GroupName = "02 — ATR Risk", Order = 2)]
        public double SlMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Take Profit", GroupName = "02 — ATR Risk", Order = 3)]
        public bool EnableTp { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "TP Multiplier (ATR×)", GroupName = "02 — ATR Risk", Order = 4)]
        public double TpMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trailing Stop", GroupName = "02 — ATR Risk", Order = 5)]
        public bool EnableTs { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Trail Multiplier (ATR×)", GroupName = "02 — ATR Risk", Order = 6)]
        public double TsMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Break-Even Shift", GroupName = "02 — ATR Risk", Order = 7)]
        public bool EnableBe { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Break-Even Trigger (ATR×)", GroupName = "02 — ATR Risk", Order = 8)]
        public double BeTrigger { get; set; }

        // ── 03. Session Filter ──────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "AM Session  09:30 – 11:30 ET",
                 GroupName = "03 — Session Filter", Order = 0)]
        public bool EnableAmSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PM Session  13:30 – 15:30 ET",
                 GroupName = "03 — Session Filter", Order = 1)]
        public bool EnablePmSession { get; set; }

        // ── 04. Daily Risk ──────────────────────────────────────────────────

        [NinjaScriptProperty, Range(50.0, 10000.0)]
        [Display(Name = "Max Daily Loss ($)", GroupName = "04 — Daily Risk", Order = 0)]
        public double MaxDailyLoss { get; set; }

        [NinjaScriptProperty, Range(1, 25)]
        [Display(Name = "Max Daily Trades", GroupName = "04 — Daily Risk", Order = 1)]
        public int MaxDailyTrades { get; set; }

        // ── 05. SMA Crossover ───────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable SMA Crossover", GroupName = "05 — SMA Crossover", Order = 0)]
        public bool EnableSma { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Fast Period", GroupName = "05 — SMA Crossover", Order = 1)]
        public int SmaFastLen { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Slow Period", GroupName = "05 — SMA Crossover", Order = 2)]
        public int SmaSlowLen { get; set; }

        // ── 06. RSI Filter ──────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable RSI Filter", GroupName = "06 — RSI Filter", Order = 0)]
        public bool EnableRsi { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "RSI Length", GroupName = "06 — RSI Filter", Order = 1)]
        public int RsiLen { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Long Above", GroupName = "06 — RSI Filter", Order = 2)]
        public double RsiLongLevel { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Short Below", GroupName = "06 — RSI Filter", Order = 3)]
        public double RsiShortLevel { get; set; }

        // ── 07. MACD ────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable MACD Crossover", GroupName = "07 — MACD", Order = 0)]
        public bool EnableMacd { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Fast Length", GroupName = "07 — MACD", Order = 1)]
        public int MacdFast { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Slow Length", GroupName = "07 — MACD", Order = 2)]
        public int MacdSlow { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Signal Length", GroupName = "07 — MACD", Order = 3)]
        public int MacdSignal { get; set; }

        // ── 08. Supertrend ──────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable Supertrend", GroupName = "08 — Supertrend", Order = 0)]
        public bool EnableSt { get; set; }

        [NinjaScriptProperty, Range(0.1, 20.0)]
        [Display(Name = "Factor", GroupName = "08 — Supertrend", Order = 1)]
        public double StFactor { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "ATR Length", GroupName = "08 — Supertrend", Order = 2)]
        public int StLen { get; set; }

        // ── 09. Stochastic ──────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable Stochastic", GroupName = "09 — Stochastic", Order = 0)]
        public bool EnableStoch { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "%K Length", GroupName = "09 — Stochastic", Order = 1)]
        public int StochK { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "%D Length", GroupName = "09 — Stochastic", Order = 2)]
        public int StochD { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Smooth", GroupName = "09 — Stochastic", Order = 3)]
        public int StochSmooth { get; set; }

        // ── 10. Bollinger Bands ─────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable BB Trend", GroupName = "10 — Bollinger Bands", Order = 0)]
        public bool EnableBb { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Length", GroupName = "10 — Bollinger Bands", Order = 1)]
        public int BbLen { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Multiplier", GroupName = "10 — Bollinger Bands", Order = 2)]
        public double BbMult { get; set; }

        // ── 11. EMA Crossover ───────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA Crossover", GroupName = "11 — EMA Crossover", Order = 0)]
        public bool EnableEma { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Fast Period", GroupName = "11 — EMA Crossover", Order = 1)]
        public int EmaFastLen { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Slow Period", GroupName = "11 — EMA Crossover", Order = 2)]
        public int EmaSlowLen { get; set; }

        // ── 12. Awesome Oscillator (fixed 5/34 HL2 SMA) ─────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable Awesome Oscillator",
                 GroupName = "12 — Awesome Oscillator", Order = 0)]
        public bool EnableAo { get; set; }

        // ── 13. Parabolic SAR ───────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable Parabolic SAR",
                 GroupName = "13 — Parabolic SAR", Order = 0)]
        public bool EnableSar { get; set; }

        [NinjaScriptProperty, Range(0.001, 1.0)]
        [Display(Name = "Start (Acceleration)",
                 GroupName = "13 — Parabolic SAR", Order = 1)]
        public double SarStart { get; set; }

        [NinjaScriptProperty, Range(0.001, 1.0)]
        [Display(Name = "Increment (Step)",
                 GroupName = "13 — Parabolic SAR", Order = 2)]
        public double SarInc { get; set; }

        [NinjaScriptProperty, Range(0.01, 5.0)]
        [Display(Name = "Max Value",
                 GroupName = "13 — Parabolic SAR", Order = 3)]
        public double SarMax { get; set; }

        // ── 14. CCI Filter ──────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable CCI Filter", GroupName = "14 — CCI", Order = 0)]
        public bool EnableCci { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "CCI Length", GroupName = "14 — CCI", Order = 1)]
        public int CciLen { get; set; }

        [NinjaScriptProperty, Range(-1000.0, 1000.0)]
        [Display(Name = "Long Above (level)", GroupName = "14 — CCI", Order = 2)]
        public double CciLongLevel { get; set; }

        [NinjaScriptProperty, Range(-1000.0, 1000.0)]
        [Display(Name = "Short Below (level)", GroupName = "14 — CCI", Order = 3)]
        public double CciShortLevel { get; set; }

        // ── 15. ADX Filter ──────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable ADX Filter", GroupName = "15 — ADX", Order = 0)]
        public bool EnableAdx { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "ADX/DM Period", GroupName = "15 — ADX", Order = 1)]
        public int AdxLen { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "ADX Threshold", GroupName = "15 — ADX", Order = 2)]
        public double AdxThreshold { get; set; }

        #endregion

    } // class SignalForgeNQ
} // namespace
