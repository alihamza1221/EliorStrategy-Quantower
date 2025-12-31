// Copyright QUANTOWER LLC. © 2017-2023. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace EliorStrategy
{
    /// <summary>
    /// An example of blank indicator. Add your code, compile it and use on the charts in the assigned trading terminal.
    /// Information about API you can find here: http://api.quantower.com
    /// Code samples: https://github.com/Quantower/Examples
    /// </summary>
	public class EliorStrategy : Indicator
    {
    // ---------------- SuperTrend ----------------
    [InputParameter("Source", 0, variants: new object[]
    {
        "Close", PriceType.Close,
        "Open",  PriceType.Open,
        "High",  PriceType.High,
        "Low",   PriceType.Low
    })]
    public PriceType Src { get; set; } = PriceType.Close;

    [InputParameter("SMA Length", 1, 1, 500, 1)]
    public int SmaLen { get; set; } = 25;

    [InputParameter("Factor", 2, 0.05, 20.0, 0.05, 2)]
    public double Factor { get; set; } = 1.8;

    [InputParameter("MAD Length", 3, 1, 500, 1)]
    public int MadLen { get; set; } = 20;


        // ---------------- EMA Filters ----------------
        [InputParameter("Use EMA Filters", 10)]
        public bool UseEMA { get; set; } = true;

        [InputParameter("EMA Length (Chart TF)", 11, 1, 2000, 1)]
        public int EmaTFLen { get; set; } = 200;

        [InputParameter("EMA Length (1H)", 12, 1, 2000, 1)]
        public int Ema1hLen { get; set; } = 50;


        // ---------------- Volatility Filters ----------------
        [InputParameter("Use Vol Filters", 20)]
        public bool UseVOL { get; set; } = true;

        [InputParameter("VolLen (Chart TF)", 21, 1, 2000, 1)]
        public int VolTFLen { get; set; } = 50;

        [InputParameter("VolLen (4H)", 22, 1, 2000, 1)]
        public int Vol4HLen { get; set; } = 50;

        [InputParameter("VolLen (1D)", 23, 1, 2000, 1)]
        public int Vol1DLen { get; set; } = 50;

        

      
        [InputParameter("Chart TF ≥ (ratio)", 26, -100, 10.0, 0.0001, 2)]
        public double ThTF { get; set; } = 0.40;

        [InputParameter("4H ≥ (ratio)", 27, -100, 10.0, 0.0001, 4)]
        public double Th4H { get; set; } = 0.001;

        [InputParameter("1D ≥ (ratio)", 28, -100, 10.0, 0.0001, 2)]
        public double Th1D { get; set; } = 0.20;


        public enum ConfirmMode
        {
            WickHighLow = 0,
            Close = 1,
            BothHighClose = 2
        }

        [InputParameter("Require consecutive bars for 3-bar confirm", 30)]
        public bool RequireConsecutiveBars { get; set; } = false;

        [InputParameter("3-Bar Confirm Based On", 31, variants: new object[]
        {
            "Wick (High/Low)", ConfirmMode.WickHighLow,
            "Close", ConfirmMode.Close,
            "Both (High+Close)", ConfirmMode.BothHighClose
        })]
        public ConfirmMode ConfirmationMode { get; set; } = ConfirmMode.WickHighLow;

        //--------------- Trade direction ----------------
        public enum TradesDirection
        {
            Both = 0,
            Long = 1,
            Short = 2
        }
        private static readonly DateTime ExpiryUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        [InputParameter("Trades Direction", 50, variants: new object[]
        {
            "Both", TradesDirection.Both,
            "Long", TradesDirection.Long,
            "Short", TradesDirection.Short
        })]
        public TradesDirection Direction { get; set; } = TradesDirection.Both;

        // ---------------- Risk / Exits ----------------
        [InputParameter("Use Take Profit %", 60)]
        public bool UseTP { get; set; } = true;

        [InputParameter("TP %", 61, 0.1, 100.0, 0.1, 2)]
        public double TpPct { get; set; } = 1.5;

        [InputParameter("Use Stop Loss %", 62)]
        public bool UseSL { get; set; } = true;

        [InputParameter("SL %", 63, 0.1, 100.0, 0.1, 2)]
        public double SlPct { get; set; } = 0.8;

        [InputParameter("Exit on opposite SuperTrend flip", 64)]
        public bool ExitOnOpp { get; set; } = true;

        public HistoricalData Hd;
        public HistoricalData Hdd1;
        Period Res4H = Period.HOUR4;
        Period Res1D = Period.DAY1;


        Indicator sma;
        Indicator mean;
        double mad = double.NaN;
        double prevMad = double.NaN;

        bool isMadCalReq;


        private double prevST = double.NaN;
        private int prevDir; 

        private struct STOut
        {
            public double ST;
            public int Dir;   // -1 = long regime, +1 = short regime (same as Pine)

        }

        private bool prevStLongNow = false;
        private bool prevStShortNow = false;

        
        private Indicator emaTF;
        private Indicator ema1h;
        HistoricalData Hd1;

        private STOut sTOut;

        //============================= State for confirmation ==========================

        // --- pending sequences
        private bool pendL = false, pendS = false;

        // --- signal levels + bar ids
        private double sigLvlL = double.NaN, sigLvlS = double.NaN;
        private int sigBarL = -1, sigBarS = -1;

        // --- steps
        private bool step1L = false, step2L = false, step1S = false, step2S = false;
        private double step1LvlL = double.NaN, step1LvlS = double.NaN;
        private int step1LBar = -1, step1SBar = -1;

        // --- TF ok flags
        private bool tfOkSinceLong = false, tfOkSinceShort = false;

        // --- Pine bar_index replacement (stable counter)
        private int barCounter = -1;


        private enum SignalSide { Buy, Sell }

        private struct SignalEvent
        {
            public DateTime Time;   // bar time (closed bar)
            public double Price;    // Low for buy, High for sell
            public SignalSide Side;
        }

        private readonly List<SignalEvent> signals = new List<SignalEvent>();
        private double prevUpperBand = double.NaN;
        private double prevLowerBand = double.NaN;

        private double barsSinceStart = 0;

        private enum PosSide { Flat, Long, Short }

        private struct VirtualPos
        {
            public PosSide Side;
            public double EntryPrice;
            public DateTime EntryTime;
            public double Tp;   // NaN if disabled
            public double Sl;   // NaN if disabled
        }

        private VirtualPos vpos;

        private enum ExitReason { TP, SL, OppFlip }

        private struct ExitEvent
        {
            public DateTime Time;
            public double Price;
            public PosSide Side;      // which position was closed
            public ExitReason Reason;
        }

        private readonly List<ExitEvent> exits = new List<ExitEvent>();

        public EliorStrategy()
            : base()
        {
            // Defines indicator's name and description.
            Name = "EliorStrategy";
            Description = "My indicator's annotation";
            AddLineSeries("st up", Color.Violet, 1, LineStyle.Solid);
            AddLineSeries("st down", Color.Green, 1, LineStyle.Solid);
            AddLineSeries("EMA TF", Color.Orange, 1, LineStyle.Solid);




            // By default indicator will be applied on main window of the chart
            SeparateWindow = false;
        }


       


        /// <summary>
        /// This function will be called after creating an indicator as well as after its input params reset or chart (symbol or timeframe) updates.
        /// </summary>
        protected override void OnInit()
        {


            Core.Loggers.Log($"started at : {DateTime.UtcNow} ");
            // Add your initialization code here
            vpos = new VirtualPos { Side = PosSide.Flat, Tp = double.NaN, Sl = double.NaN };
            exits.Clear();

            this.isMadCalReq = true;
            //history 4h
            this.Hd = null;
            DateTime from = DateTime.UtcNow.AddHours(-(Vol4HLen + 1) * 4);
            this.Hd = this.Symbol.GetHistory(Period.HOUR4, this.Symbol.HistoryType, from);


            //history 1d
            this.Hdd1 = null;
            DateTime fromD1 = DateTime.UtcNow.AddDays(-(Vol1DLen + 1));
            this.Hdd1 = this.Symbol.GetHistory(Period.DAY1, this.Symbol.HistoryType, fromD1);

            //history 1h
            this.Hd1 = null;
            DateTime fromH1 = DateTime.UtcNow.AddHours(-(Ema1hLen + 20));
            this.Hd1 = this.Symbol.GetHistory(Period.HOUR1, this.Symbol.HistoryType, fromH1);
            this.ema1h = this.Hd1.BuiltInIndicators.EMA(Ema1hLen, PriceType.Close);


            // sma-indicator 

            this.sma = Core.Indicators.BuiltIn.SMA(SmaLen, Src);
            AddIndicator(this.sma);

            //mean
            this.mean = Core.Indicators.BuiltIn.SMA(MadLen, Src);
            AddIndicator(this.mean);

            //EMA TF
            this.emaTF = Core.Indicators.BuiltIn.EMA(EmaTFLen, PriceType.Close);
            AddIndicator(this.emaTF);

        }

         
        protected double GetHTFRatio(HistoricalData hist, int len)
        {
            // Need len bars + 1 previous-close bar
            if (hist == null || len <= 0 || hist.Count < len + 1)
                return 0.0; // or double.NaN if you prefer Pine-like "na" during warmup
            //Core.Loggers.Log($"hist.Count : {hist.Count} ");
            double sumVol = 0.0;

            // SMA(volTF, len) over bars [0..len-1] using prev close from [i+1]
            for (int i = 0; i < len; i++)
            {
                HistoryItemBar prevbar =(HistoryItemBar) hist[i + 1];
                double prevBarColse = prevbar.Close;

                HistoryItemBar curBar = (HistoryItemBar)hist[i];

                double trValue = TR(prevBarColse,curBar.High, curBar.Low, curBar.Close);
                double vol = VolTF(trValue, curBar.Low);

                sumVol += vol;
            }

            double avg = sumVol / len;

            // _v is the CURRENT bar's volTF (bar 0)
            HistoryItemBar bar1 = (HistoryItemBar)hist[1];
            var prev = bar1.Close;
            HistoryItemBar bar0 = (HistoryItemBar)hist[0];

            double v = VolTF(TR(prev,bar0.High, bar0.Low,bar0.Close),bar0.Low);

            return avg == 0.0 ? 0.0 : (v / avg) - 1.0;
        }
        protected double GetTFRatio(int len)
        {
            // Need len bars + 1 previous-close bar
            if (Count < len + 2)
                return 0.0; // or double.NaN if you prefer Pine-like "na" during warmup
            //Core.Loggers.Log($"hist.Count : {hist.Count} ");
            double sumVol = 0.0;

            for (int i = 1; i <= len; i++)
            {
                var prevBarClose = Close(i + 1);
                
                double trValue = TR(prevBarClose, High(i) , Low(i), Close(i));
                double vol = VolTF(trValue, Low(i));

                sumVol += vol;
            }

            double avg = sumVol / len;
            //Core.Loggers.Log($"Avg VolTF over {len} bars: {avg} ");
            var curClose = Close(1);
            var prev = Close(2);
            double v = VolTF(TR(prev,High(1), Low(1), curClose), Low(1));

            //Core.Loggers.Log($"**Current VolTF: {v}  cur close: {cur.Close}  prev close : {prev.Close}"  );

            return avg == 0.0 ? 0.0 : (v / avg) - 1.0;
        }

        public static double VolTF(double tr, double barLow)
        {
            double denom = Math.Max(Math.Abs(barLow), 1e-10);
            return (tr * 100.0) / denom;
        }


        protected double TR(double prev_close, double barHigh, double barLow, double barClose) 
        {
            double tr = Math.Max(barHigh - barLow,
            Math.Max(Math.Abs(barHigh - prev_close),
                  Math.Abs(barLow - prev_close)));

            return tr;
        }

        /// <summary>
        /// Calculation entry point. This function is called when a price data updates. 
        /// Will be runing under the HistoricalBar mode during history loading. 
        /// Under NewTick during realtime. 
        /// Under NewBar if start of the new bar is required.
        /// </summary>
        /// <param name="args">Provides data of updating reason and incoming price.</param>
        protected override void OnUpdate(UpdateArgs args)
        {
            if (DateTime.UtcNow >= ExpiryUtc)
            {
                // optional: log once (avoid spamming)
                Core.Loggers.Log("EliorStrategy license expired (2025-12-29)."); 
                return;
            }


            SetValue(this.emaTF.GetValue(),2);



                if (args.Reason == UpdateReason.NewBar )
            {
               // Core.Loggers.Log("New bar...");
                if (this.HistoricalData.Count < this.MadLen + 1)
                    return;
                double devSum = 0.0;
                //Core.Loggers.Log($"Calculating MAD over {this.MadLen} bars." );
                for (int i = 1; i <= this.MadLen; i++)
                {
                    var bar = (HistoryItemBar)this.HistoricalData[i];
                    double dev = Math.Abs(bar[this.Src] - this.mean.GetValue(i));
                    if (double.IsNaN(dev)) return;

                    devSum += dev;
                }
                this.prevMad = this.mad;
                this.mad = devSum / this.MadLen;//

                //Core.Loggers.Log($"Final MAD: {this.mad}");
                this.isMadCalReq = false;
                barCounter++;
            }

            if (args.Reason == UpdateReason.HistoricalBar)
            {

                //Core.Loggers.Log("history bar...");

                if (this.HistoricalData.Count < this.MadLen)
                    return;
                double devSum = 0.0;
                //Core.Loggers.Log($"Calculating MAD over {this.MadLen} bars." );
                for (int i = 0; i < this.MadLen; i++)
                {
                    //Core.Loggers.Log($"mean({i})" + this.mean.GetValue(i) + $" count: {Count}");
                    double dev = Math.Abs(Close(i) - this.mean.GetValue(i));
                    if (double.IsNaN(dev)) return;

                    devSum += dev;
                }
                this.prevMad = this.mad;
                this.mad = devSum / this.MadLen;//

                //Core.Loggers.Log($"Final MAD: {this.mad}");
                this.isMadCalReq = false;
                barCounter++;
            }








            if ((args.Reason == UpdateReason.NewBar) || (args.Reason == UpdateReason.HistoricalBar))
            {
                

                double close = Close(1);

                double emaTF_val = emaTF.GetValue(1);

                double ema1H_val = ema1h.GetValue();  // in requirements it was mentioned to use closed candles for ema but in pine it's forming also


                //Core.Loggers.Log($" ematf : {emaTF_val} ema1H_val : {ema1H_val}");
                // Warmup guard
                if (double.IsNaN(emaTF_val) || double.IsNaN(ema1H_val))//
                {
                    prevMad = double.NaN;
                    prevST = double.NaN;

                    //Core.Loggers.Log($"!!!!   EMA values are NaN, skipping this bar. {mad}");
                    mad = double.NaN;

                    return;
                }
                double close1 = Close(1);
                double close2 = Close(2);
                DateTime timenow = Time(1);
                bool isHistorical = false;
                int offsetBands = 1;
                if (args.Reason == UpdateReason.HistoricalBar)
                {
                    close1 = Close(0);
                    close2 = Close(1);
                    timenow = Time(0);
                    isHistorical = true;
                    offsetBands = 0;

                }
                this.sTOut = SmaMadSuperTrend(this.Factor, close1, close2,isHistorical);

                bool stLongNow = sTOut.Dir < 0;   // long regime
                bool stShortNow = sTOut.Dir > 0;   // short regime

                bool longStart = stLongNow && !prevStLongNow;
                bool shortStart = stShortNow && !prevStShortNow;
                //Core.Loggers.Log($"Long start : {longStart} prev st long : {prevStLongNow} stlong : {stLongNow}");

                prevStLongNow = stLongNow;
                prevStShortNow = stShortNow;


                bool longAllowed = this.Direction != TradesDirection.Short;
                bool shortAllowed = this.Direction != TradesDirection.Long;

                CheckVirtualExits(longStart, shortStart, longAllowed, shortAllowed, close1, close2, timenow);

                bool emaGateL = !UseEMA || (close > emaTF_val && close > ema1H_val);
                bool emaGateS = !UseEMA || (close < emaTF_val && close < ema1H_val);
                //Core.Loggers.Log("emaGateL: " + emaGateL + " , emaGateS: " + emaGateS + ", close : "  + close + " , ematf_val :"+ emaTF_val + " , ema1h_val :" + ema1H_val + " long start :" + longStart + " short start : " + shortStart);
                double rTF = GetTFRatio(VolTFLen);
                //Core.Loggers.Log("rTF: " + rTF + " mad :" + this.mad);
                //Core.Loggers.Log($"ST: {sTOut.ST} , Dir: {sTOut.Dir} longstart : {longStart} shortstart : {shortStart} rtf : {rTF} emaGatel: {emaGateL} emagates : {emaGateS} close {close}");
                if (sTOut.Dir < 0)
                    SetValue(sTOut.ST, 1,offsetBands);
                else
                    SetValue(sTOut.ST, 0, offsetBands);
                HandleFlipOnClosedBar(longStart, shortStart, longAllowed, shortAllowed, rTF);
                VolumeAndBarConfirmation(rTF);
                double r4H = this.GetHTFRatio(this.Hd, Vol4HLen);
                double r1D = this.GetHTFRatio(this.Hdd1, Vol1DLen);

                //Core.Loggers.Log("r4H: " + r4H + " , r1D: " + r1D);

                EvaluateFinalGatesAndReset(rTF, r4H, r1D, emaGateL, emaGateS, longAllowed, shortAllowed);
            }


            
            //sma for 1h calculation

            // Core.Loggers.Log($"mad:  {this.mean.GetValue()} ... sma :{this.sma.GetValue()},   sma[1] {this.mean.GetValue(1)}"  );

        }





        private STOut SmaMadSuperTrend(double factor, double close, double prevClose, bool isHistorical = false)
            //
        {
            double sma_;
            if (isHistorical)
                sma_ = this.sma.GetValue();
            else
                sma_ = this.sma.GetValue(1);


            //Core.Loggers.Log("SMA: " + sma_ + " MAD: " + mad + " prevMad: " + prevMad + " close: " + close + " prevClose: " + prevClose + " isHistorical: " + isHistorical);
            if (double.IsNaN(sma_) || double.IsNaN(mad))
                return new STOut { ST = double.NaN, Dir = 1 };

            double upperBand = sma_ + factor * mad;
            double lowerBand = sma_ - factor * mad;
            //Core.Loggers.Log($"mad : {mad} close {close} histroy : {isHistorical}");
            double prevUpper = double.IsNaN(prevUpperBand) ? 0.0 : prevUpperBand; 
            double prevLower = double.IsNaN(prevLowerBand) ? 0.0 : prevLowerBand; 

            upperBand = (upperBand < prevUpper || prevClose > prevUpper) ? upperBand : prevUpper;
            lowerBand = (lowerBand > prevLower || prevClose < prevLower) ? lowerBand : prevLower;

            int dir;
            if (double.IsNaN(prevMad))
                dir = 1;
            else
            {
                bool prevStEqPrevUpper = !double.IsNaN(prevST) && Math.Abs(prevST - prevUpper) < 1e-10;
                if (prevStEqPrevUpper)
                    dir = (close > upperBand) ? -1 : 1;
                else
                    dir = (close < lowerBand) ? 1 : -1;
            }

            double st = (dir == -1) ? lowerBand : upperBand;

            prevUpperBand = upperBand;
            prevLowerBand = lowerBand;
            prevST = st;
            //if (dir != prevDir) Core.Loggers.Log("---------------- Changed -----------");
            prevDir = dir;

            return new STOut { ST = st, Dir = dir };
        }


        private bool BreakAboveLevel(double level, int offsetClosed = 1)
        {
            // closed bar values
            double h = High(offsetClosed);
            double c = Close(offsetClosed);

            switch (this.ConfirmationMode)
            {
                case ConfirmMode.WickHighLow:
                    return h > level;

                case ConfirmMode.Close:
                    return c > level;

                case ConfirmMode.BothHighClose:
                default:
                    return (h > level) && (c > level);
            }
        }
        private void CheckVirtualExits(bool longStart, bool shortStart, bool longAllowed, bool shortAllowed, double h, double l, DateTime t)
        {
            if (vpos.Side == PosSide.Flat)
                return;


            // 1) TP/SL first (like strategy.exit working continuously)
            if (vpos.Side == PosSide.Long)
            {
                if (UseSL && !double.IsNaN(vpos.Sl) && l <= vpos.Sl)
                {
                    CloseVirtual(PosSide.Long, vpos.Sl, t, ExitReason.SL);
                    return;
                }
                if (UseTP && !double.IsNaN(vpos.Tp) && h >= vpos.Tp)
                {
                    CloseVirtual(PosSide.Long, vpos.Tp, t, ExitReason.TP);
                    return;
                }

                // 2) Exit on opposite flip (Pine: exitOnOpp and shortStart and shortAllowed and confirmed)
                if (ExitOnOpp && shortStart && shortAllowed)
                {
                    CloseVirtual(PosSide.Long, Close(1), t, ExitReason.OppFlip);
                    return;
                }
            }
            else if (vpos.Side == PosSide.Short)
            {
                if (UseSL && !double.IsNaN(vpos.Sl) && h >= vpos.Sl)
                {
                    CloseVirtual(PosSide.Short, vpos.Sl, t, ExitReason.SL);
                    return;
                }
                if (UseTP && !double.IsNaN(vpos.Tp) && l <= vpos.Tp)
                {
                    CloseVirtual(PosSide.Short, vpos.Tp, t, ExitReason.TP);
                    return;
                }

                // Pine: exitOnOpp and longStart and longAllowed and confirmed
                if (ExitOnOpp && longStart && longAllowed)
                {
                    CloseVirtual(PosSide.Short, Close(1), t, ExitReason.OppFlip);
                    return;
                }
            }
        }

        private void CloseVirtual(PosSide side, double exitPrice, DateTime time, ExitReason reason)
        {
            exits.Add(new ExitEvent
            {
                Time = time,
                Price = exitPrice,
                Side = side,
                Reason = reason
            });

            vpos = new VirtualPos { Side = PosSide.Flat, Tp = double.NaN, Sl = double.NaN };

            const int maxExits = 50;
            if (exits.Count > maxExits)
                exits.RemoveRange(0, exits.Count - maxExits);
        }

        private bool BreakBelowLevel(double level, int offsetClosed = 1)
        {
            double l = Low(offsetClosed);
            double c = Close(offsetClosed);

            switch (this.ConfirmationMode)
            {
                case ConfirmMode.WickHighLow:
                    return l < level;

                case ConfirmMode.Close:
                    return c < level;

                case ConfirmMode.BothHighClose:
                default:
                    return (l < level) && (c < level);
            }
        }

        private double BasisLong(int offsetClosed = 1)
        {
            // Pine: confirmMode == "Close" ? close : high
            return (this.ConfirmationMode == ConfirmMode.Close)
                ? Close(offsetClosed)
                : High(offsetClosed);
        }

        private double BasisShort(int offsetClosed = 1)
        {
            // Pine: confirmMode == "Close" ? close : low
            return (this.ConfirmationMode == ConfirmMode.Close)
                ? Close(offsetClosed)
                : Low(offsetClosed);
        }

        private void HandleFlipOnClosedBar(
        bool longStart, bool shortStart,
        bool longAllowed, bool shortAllowed,
        double rTF
        )
        {
            //Core.Loggers.Log($"HandleFlipOnClosedBar: longStart={longStart}, shortStart={shortStart}, longAllowed={longAllowed}, shortAllowed={shortAllowed}, rTF={rTF}");
            // Long flip starts a long confirmation sequence and cancels short
            if (longStart && longAllowed)
            {
                // cancel short sequence
                pendS = false;
                sigLvlS = double.NaN;
                sigBarS = -1;
                step1S = step2S = false;
                step1LvlS = double.NaN;
                step1SBar = -1;
                tfOkSinceShort = false;

                // start long sequence
                pendL = true;
                sigLvlL = BasisLong(1);      
                sigBarL = barCounter;       
                step1L = step2L = false;
                step1LvlL = double.NaN;
                step1LBar = -1;
                tfOkSinceLong = (rTF >= ThTF);
            }

            // Short flip starts a short confirmation sequence and cancels long
            if (shortStart && shortAllowed)
            {
                // cancel long sequence
                pendL = false;
                sigLvlL = double.NaN;
                sigBarL = -1;
                step1L = step2L = false;
                step1LvlL = double.NaN;
                step1LBar = -1;
                tfOkSinceLong = false;

                // start short sequence
                pendS = true;
                sigLvlS = BasisShort(1);     // just-closed bar basis [web:90]
                sigBarS = barCounter;        // Pine: bar_index
                step1S = step2S = false;
                step1LvlS = double.NaN;
                step1SBar = -1;
                tfOkSinceShort = (rTF >= ThTF);
            }
        }


        private void VolumeAndBarConfirmation(double rTF)
        {
            // ---------------- Mark TF vol OK once seen after signal ----------------
            if (pendL && sigBarL >= 0 && barCounter > sigBarL && rTF >= ThTF)
                tfOkSinceLong = true;

            if (pendS && sigBarS >= 0 && barCounter > sigBarS && rTF >= ThTF)
                tfOkSinceShort = true;

            // ---------------- 3-bar confirmation (Long) ----------------
            bool isNextAfterSignalL = RequireConsecutiveBars
                ? (barCounter == sigBarL + 1)
                : (barCounter > sigBarL);

            bool isNextAfterStep1L = RequireConsecutiveBars
                ? (barCounter == step1LBar + 1)
                : (barCounter > step1LBar);

            if (pendL && sigBarL >= 0)
            {
                if (!step1L && isNextAfterSignalL && BreakAboveLevel(sigLvlL, offsetClosed: 1))
                {
                    step1L = true;
                    step1LvlL = BasisLong(offsetClosed: 1);
                    step1LBar = barCounter;
                }
                else if (step1L && !step2L && step1LBar >= 0 && isNextAfterStep1L && BreakAboveLevel(step1LvlL, offsetClosed: 1))
                {
                    step2L = true;
                }
            }

            // ---------------- 3-bar confirmation (Short) ----------------
            bool isNextAfterSignalS = RequireConsecutiveBars
                ? (barCounter == sigBarS + 1)
                : (barCounter > sigBarS);

            bool isNextAfterStep1S = RequireConsecutiveBars
                ? (barCounter == step1SBar + 1)
                : (barCounter > step1SBar);

            if (pendS && sigBarS >= 0)
            {
                if (!step1S && isNextAfterSignalS && BreakBelowLevel(sigLvlS, offsetClosed: 1))
                {
                    step1S = true;
                    step1LvlS = BasisShort(offsetClosed: 1);
                    step1SBar = barCounter;
                }
                else if (step1S && !step2S && step1SBar >= 0 && isNextAfterStep1S && BreakBelowLevel(step1LvlS, offsetClosed: 1))
                {
                    step2S = true;
                }
            }
        }


        private void EvaluateFinalGatesAndReset(
            double rTF,     // chart TF ratio
            double r4H,     // 4H ratio
            double r1D,     // 1D ratio
            bool emaGateL,
            bool emaGateS,
            bool longAllowed,
            bool shortAllowed
        )
        {
            //Core.Loggers.Log($"r4h : {r4H}  r1d: {r1D}  rtf : {rTF}");
            // Pine:
            // tfGateL = tfOkSinceLong
            // tfGateS = tfOkSinceShort
            bool tfGateL = tfOkSinceLong;
            bool tfGateS = tfOkSinceShort;

            // Pine:
            // mtfGateL = (r4H >= th4H) and (r1D >= th1D)
            // mtfGateS = (r4H >= th4H) and (r1D >= th1D)
            bool mtfGateL = (r4H >= Th4H) && (r1D >= Th1D);
            bool mtfGateS = (r4H >= Th4H) && (r1D >= Th1D);

            // Pine:
            // readyL = pendL and step2L and (not useVOL or (tfGateL and mtfGateL)) and emaGateL and longAllowed
            // readyS = pendS and step2S and (not useVOL or (tfGateS and mtfGateS)) and emaGateS and shortAllowed
            bool readyL = pendL && step2L && (!UseVOL || (tfGateL && mtfGateL)) && emaGateL && longAllowed;
            bool readyS = pendS && step2S && (!UseVOL || (tfGateS && mtfGateS)) && emaGateS && shortAllowed;
            StoreSignalIfAny(readyL, readyS);
            //Core.Loggers.Log(
            //    $"readyL={readyL} " +
            //    $"pendL={pendL} step2L={step2L} " +
            //    $"useVOL={UseVOL} tfGateL={tfGateL} mtfGateL={mtfGateL} " +
            //    $"emaGateL={emaGateL} longAllowed={longAllowed}"
            //);

            //Core.Loggers.Log(
            //    $"readyS={readyS} " +
            //    $"pendS={pendS} step2S={step2S} " +
            //    $"useVOL={UseVOL} tfGateS={tfGateS} mtfGateS={mtfGateS} " +
            //    $"emaGateS={emaGateS} shortAllowed={shortAllowed}"
            //);  // [web:118][web:325]

            if (readyL)
            {

                OpenVirtual(PosSide.Long, Close(1), Time(1));
                Core.Loggers.Log("long"); 

                // reset long state (exact Pine reset)
                pendL = false;
                sigLvlL = double.NaN;
                sigBarL = -1;
                step1L = false;
                step2L = false;
                step1LvlL = double.NaN;
                step1LBar = -1;
                tfOkSinceLong = false;
            }

            if (readyS)
            {
                OpenVirtual(PosSide.Short, Close(1), Time(1));
                Core.Loggers.Log("short"); 

                // reset short state (exact Pine reset)
                pendS = false;
                sigLvlS = double.NaN;
                sigBarS = -1;
                step1S = false;
                step2S = false;
                step1LvlS = double.NaN;
                step1SBar = -1;
                tfOkSinceShort = false;
            }
        }

        private void OpenVirtual(PosSide side, double entryPrice, DateTime time)
        {
            vpos.Side = side;
            vpos.EntryPrice = entryPrice;
            vpos.EntryTime = time;

            vpos.Tp = double.NaN;
            vpos.Sl = double.NaN;

            if (UseTP)
            {
                vpos.Tp = side == PosSide.Long
                    ? entryPrice * (1.0 + TpPct / 100.0)
                    : entryPrice * (1.0 - TpPct / 100.0);
            }

            if (UseSL)
            {
                vpos.Sl = side == PosSide.Long
                    ? entryPrice * (1.0 - SlPct / 100.0)
                    : entryPrice * (1.0 + SlPct / 100.0);
            }
        }

        private void StoreSignalIfAny(bool readyL, bool readyS)
        {
            // your “confirmed bar” is offset 1 in your codebase
            double barClose = Close(1);
            double barHigh = High(1);
            double barLow = Low(1);
            DateTime timeleft = Time(1);


            if (readyL)
                signals.Add(new SignalEvent { Time = timeleft, Price = barLow, Side = SignalSide.Buy });

            if (readyS)
                signals.Add(new SignalEvent { Time = timeleft, Price = barHigh, Side = SignalSide.Sell });

            // optional: prevent unbounded growth
            const int maxSignals = 20;

            if (signals.Count > maxSignals)
                signals.RemoveRange(0, signals.Count - maxSignals);

        }
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            // Chart canvas
            Graphics g = args.Graphics;

            // Converter for Time/Price <-> X/Y
            var cc = this.CurrentChart?.MainWindow?.CoordinatesConverter; // [web:335]
            if (cc == null || signals.Count == 0)
                return;

            // Optional: only draw what’s inside the visible rectangle
            Rectangle rect = args.Rectangle; // [web:338]

            using var buyBrush = new SolidBrush(Color.Lime);
            using var sellBrush = new SolidBrush(Color.Red);
            using var exitBrush = new SolidBrush(Color.Gold);

            foreach (var e in exits)
            {
                float x = (float)cc.GetChartX(e.Time);
                float y = (float)cc.GetChartY(e.Price);

                if (x < rect.Left - 200 || x > rect.Right + 200)
                    continue;

                // reuse your shapes but different label/color if you want
                if (e.Side == PosSide.Long)
                    DrawSellSignal(g, exitBrush, (int)x, (int)y, 12, 12, "x"); // “close long” marker
                else
                    DrawBuySignal(g, exitBrush, (int)x, (int)y, 12, 12, "x");  // “close short” marker
            }

            //Core.Loggers.Log("len sign :" + signals.Count);
            foreach (var s in signals)
            {

                float x = (float)cc.GetChartX(s.Time);
                float y = (float)cc.GetChartY(s.Price) ;

                    // Skip if off-screen
                    if (x < rect.Left - 200 || x > rect.Right + 200)
                    continue;
                //Core.Loggers.Log($"Drawing signal: {s.Side} at {s.Time}, price {s.Price}, coords ({x}, {y})");
                if (s.Side == SignalSide.Buy)
                    DrawBuySignal(g, buyBrush, (int)x, (int)y, 15, 15);   // below Low via your offset logic
                else
                    DrawSellSignal(g, sellBrush, (int)x, (int)y,15,15); // above High via your offset logic
            }
        }
        protected void DrawBuySignal(Graphics graphics, Brush brush, int x, int y, int size, int offset, string text = "buy")
        {
            var centerX = x + this.CurrentChart.BarsWidth / 2;
            var squareSize = size;
            var triangleHeight = size / 2;

            // Draw triangle pointing up above the square
            var trianglePoints = new[]
            {
            new Point(centerX - squareSize / 2, y + offset), // Bottom left
            new Point(centerX, y + offset - triangleHeight), // Top center
            new Point(centerX + squareSize / 2, y + offset) // Bottom right
        };
            graphics.FillPolygon(brush, trianglePoints);

            // Draw square below the triangle (seamlessly attached)
            var squareY = y + offset;
            graphics.FillRectangle(
                brush,
                centerX - squareSize / 2,
                squareY,
                squareSize,
                squareSize
            );

            // Draw "Buy" text inside the square
            using (var textBrush = new SolidBrush(Color.Black))
            using (var font = new Font("Arial", squareSize / 3, FontStyle.Bold))
            {
                var textSize = graphics.MeasureString(text, font);
                var textX = centerX - (int)(textSize.Width / 2);
                var textY = squareY + (int)((squareSize - textSize.Height) / 2);
                graphics.DrawString(text, font, textBrush, textX, textY);
            }
        }


        protected void DrawSellSignal(Graphics graphics, Brush brush, int x, int y, int size, int offset, string text = "sell")
        {
            var centerX = x + this.CurrentChart.BarsWidth / 2;
            var squareSize = size;
            var triangleHeight = size / 2;

            // Draw square above the triangle
            var squareY = y - offset - squareSize;
            graphics.FillRectangle(
                brush,
                centerX - squareSize / 2,
                squareY,
                squareSize,
                squareSize
            );

            // Draw triangle pointing down below the square (seamlessly attached)
            var trianglePoints = new[]
            {
            new Point(centerX - squareSize / 2, squareY + squareSize), // Top left
            new Point(centerX, squareY + squareSize + triangleHeight), // Bottom center
            new Point(centerX + squareSize / 2, squareY + squareSize) // Top right
            };
            graphics.FillPolygon(brush, trianglePoints);

            // Draw "Sell" text inside the square
            using (var textBrush = new SolidBrush(Color.Black))
            using (var font = new Font("Arial", squareSize / 3, FontStyle.Bold))
            {
                
                var textSize = graphics.MeasureString(text, font);
                var textX = centerX - (int)(textSize.Width / 2);
                var textY = squareY + (int)((squareSize - textSize.Height) / 2);
                graphics.DrawString(text, font, textBrush, textX, textY);
            }
        }


    }
}
