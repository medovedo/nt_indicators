#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

/*
 * DoubleTrap - with-trend "third entry": a 2E WITH the trend fails, the following failed second entry
 * (against the trend) fails as well. Bulls and bears are trapped, the trend resumes.
 *
 * All counting comes from the hosted EntryCount instance. The setup is detected by the EVENT SEQUENCE
 * (long: 2EL event -> F2EL event -> candidate signal bar), never by "count == 3", because EntryCount
 * resets may restart the count between the 2E and the Double Trap.
 *
 * TEST CASES (check in Market Replay, ES 2000 tick):
 * 10. Downtrend, 2ES far above the EMA -> F2ES (bullish, below the EMA) -> bearish bar closes below the EMA -> short DT
 * 11. Uptrend, 2EL not yet at the EMA, counter-trend structure intact -> new low at the EMA, bullish bar closes
 *     above the EMA -> long DT, even if EntryCount reset in between
 * 12. Like 11, but close below the EMA -> no signal (ThirdEntryInvalid)
 * 13. Like 11, but the signal bar fails Rule 5 (close 60 %) -> no signal, DtRejected with reason in the log
 * 14. Flat EMA in a range, breakout with two legs, bearish bar at the EMA -> no signal (range filter)
 * 15. Trend broken and new high, then a perfect long DT -> no signal (exhaustion)
 */

namespace NinjaTrader.NinjaScript.Indicators
{
	public class DoubleTrap : Indicator
	{
		#region State

		private const int StageIdle         = 0;
		private const int StageAwaitF2E     = 1;
		private const int StageAwaitSignal  = 2;
		private const int StagePending      = 3;
		private const int StageDone         = 4;

		private const int ExNormal    = 0;
		private const int ExBroken    = 1;
		private const int ExExhausted = 2;

		private const int MaxScanBars       = 300;
		private const int HostedLabelMaxCount = 3;

		/// <summary>Sequence and context state of one direction (long = trend up, DT buy).</summary>
		private sealed class DtSide
		{
			public bool IsLong;

			// Last 1E reported by EntryCount (needed for condition 3 and weak criterion d).
			public double FirstSignalExt = double.NaN;
			public int FirstTriggerBar = -1;
			public int LastSeenSecondBar = -1;

			// Sequence 2E -> F2E -> signal bar
			public int Stage;
			public int SecondTriggerBar;
			public int SecondSignalBar;
			public double SecondSigHigh;
			public double SecondSigLow;
			public double SeqFirstExt;
			public double PullStartExt;
			public int PullStartBar;
			public bool Weak;
			public int F2Bar;
			public double CorrExt;              // extreme of the correction since the 2E trigger
			public int CorrExtBar;
			public bool CounterBreakSinceCorr;  // a bar broke the prior bar against the correction after CorrExtBar

			// Pending signal
			public int SigBar;
			public double SigHigh;
			public double SigLow;
			public double Entry;
			public double Stop;

			// Exhaustion (trend break followed by a new trend extreme)
			public int ExState;
			public int RunLen;
			public double RunExt;
			public double BrokenExt;
			public int RunSinceEx;
		}

		#endregion

		#region Fields

		private EntryCount ec;
		private EMA ema;
		private DtSide longSide;
		private DtSide shortSide;
		private SignalBarSettings signalSettings;
		private RiskSettings riskSettings;
		private PaDiagnosticsLog diag;
		private SessionIterator sessionIterator;
		private DateTime tradingDay;
		private SimpleFont labelFont;
		private SimpleFont smallFont;
		private int rowHeight;
		private double halfTick;
		private DayBarNumbering barNumbering;

		private Series<int> signal;
		private Series<double> entryPrice;
		private Series<double> stopPrice;
		private Series<double> limitPrice;
		private Series<int> riskTicks;
		private Series<int> riskClass;
		private Series<bool> twoLegVisual;
		private Series<bool> weak2E;

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                 = "Double Trap: a with-trend 2E fails, the following failed second entry fails too. Uses the event sequence of EntryCount.";
				Name                        = "DoubleTrap";
				Calculate                   = Calculate.OnBarClose;
				IsOverlay                   = true;
				DisplayInDataBox            = false;
				DrawOnPricePanel            = true;
				PaintPriceMarkers           = false;
				IsSuspendedWhileInactive    = false;
				ScaleJustification          = ScaleJustification.Right;
				BarsRequiredToPlot          = 0;

				// Shared (passed to EntryCount)
				EmaPeriod                   = 21;
				KeyLevelToleranceTicks      = 2;
				SwingStrength               = 2;
				UseSwingSR                  = false;
				SrLookbackSwings            = 5;
				SrToleranceTicks            = 2;
				UseTrendlines               = false;
				OutsideBarPolicy            = EntryCountOutsideBarPolicy.BothSides;
				EmaSlopeLookback            = 5;
				MinEmaSlopeTicks            = 2;

				// Signal bar (Rule 5)
				MinBodyPct                  = 45;
				MinClosePct                 = 70;
				MaxBreakoutWickPct          = 25;
				UseRejectionException       = true;
				RejectionMinClosePct        = 90;
				RejectionMaxWickPct         = 10;

				// Stop / risk
				StopMode                    = EntryCountStopMode.SignalAndPrevBar;
				RiskTemplateTicks           = 16;
				RiskToleranceTicks          = 2;
				MaxPullbackPct              = 49;

				// Hosted EntryCount counting
				RequireEqualOrHigherLeg     = false;
				ResetOnKeyLevel             = true;
				ResetScope                  = EntryCountResetScope.CounterTrendOnly;
				ResetAfterTwoLegs           = true;
				ResetOnNewExtreme           = true;
				FailedEntryMode             = EntryCountFailedEntryMode.PriorBarBreak;
				InsideBarAsSignal           = true;

				// Double Trap
				DtEmaToleranceTicks         = 2;
				DtMaxBarsAfterF2E           = 10;
				DtAllowF2EBarAsSignal       = true;
				BlockTooLarge               = false;

				// Weak 2E
				UseWeakEmaGap               = true;
				EmaGapTicks                 = 4;
				UseWeakCongestion           = true;
				CongestionLookback          = 6;
				CongestionOverlapPct        = 60;
				CongestionRangeTicks        = 12;
				WeakIfQualityFails          = true;
				UseWeakCounterTrend         = true;
				RequireWeak2E               = false;

				// Context filters
				RangeLookback               = 30;
				RangeCrossCount             = 4;
				UseRule6Filter              = false;
				Rule6Lookback               = 10;
				Rule6MinBars                = 4;
				Rule6MaxSpanTicks           = 26;
				Rule6MinEfficiencyPct       = 5;
				UseExhaustionFilter         = true;
				ExhaustionMinTrendBars      = 20;

				// Display
				ShowEntryCountLabels        = false;
				ShowFirstEntries            = true;
				ShowWeak2EMarker            = true;
				ShowInvalidThirdEntry       = true;
				ShowEntryStopLines          = true;
				ShowDtRiskTicks             = false;
				MarkTrigger                 = true;
				LabelFontSize               = 11;
				BarNumberClearance          = 32;
				EnableAlerts                = true;

				// Colors
				LongColor                   = Brushes.LimeGreen;
				ShortColor                  = Brushes.Red;
				QualityFailColor            = Brushes.Gray;
				WeakColor                   = Brushes.DarkOrange;
				InvalidColor                = Brushes.DimGray;
				StopLineColor               = Brushes.IndianRed;
				TriggerColor                = Brushes.Gold;
				FailedLongColor             = Brushes.Green;
				FailedShortColor            = Brushes.Red;

				// Diagnostics
				DebugLog                    = false;
				ExportCsv                   = false;
				CsvIncludeBars              = true;
			}
			else if (State == State.Configure)
			{
				// The hosted EntryCount adds a 1-tick series (intrabar order); a host must add the same series.
				AddDataSeries(BarsPeriodType.Tick, 1);
			}
			else if (State == State.DataLoaded)
			{
				ema = EMA(Close, EmaPeriod);

				// Shared parameters are passed through; the hosted instance never draws (labels are drawn here if wanted).
				ec = EntryCount(EmaPeriod, KeyLevelToleranceTicks, SwingStrength, UseSwingSR, SrLookbackSwings, SrToleranceTicks,
					UseTrendlines, OutsideBarPolicy, EmaSlopeLookback, MinEmaSlopeTicks,
					MinBodyPct, MinClosePct, MaxBreakoutWickPct, UseRejectionException, RejectionMinClosePct, RejectionMaxWickPct,
					StopMode, RiskTemplateTicks, RiskToleranceTicks, MaxPullbackPct,
					RequireEqualOrHigherLeg, ResetOnKeyLevel, ResetScope, ResetAfterTwoLegs, ResetOnNewExtreme, FailedEntryMode, InsideBarAsSignal,
					false);

				barNumbering = new DayBarNumbering();
				longSide  = new DtSide { IsLong = true };
				shortSide = new DtSide { IsLong = false };

				signalSettings = new SignalBarSettings
				{
					MinBodyPct            = MinBodyPct,
					MinClosePct           = MinClosePct,
					MaxBreakoutWickPct    = MaxBreakoutWickPct,
					UseRejectionException = UseRejectionException,
					RejectionMinClosePct  = RejectionMinClosePct,
					RejectionMaxWickPct   = RejectionMaxWickPct
				};
				riskSettings = new RiskSettings
				{
					StopMode           = StopMode,
					RiskTemplateTicks  = RiskTemplateTicks,
					RiskToleranceTicks = RiskToleranceTicks,
					MaxPullbackPct     = MaxPullbackPct
				};

				signal       = new Series<int>(this);
				entryPrice   = new Series<double>(this);
				stopPrice    = new Series<double>(this);
				limitPrice   = new Series<double>(this);
				riskTicks    = new Series<int>(this);
				riskClass    = new Series<int>(this);
				twoLegVisual = new Series<bool>(this);
				weak2E       = new Series<bool>(this);

				halfTick  = TickSize * 0.5;
				labelFont = new SimpleFont("Arial", LabelFontSize) { Bold = true };
				smallFont = new SimpleFont("Arial", Math.Max(7, LabelFontSize - 2));
				rowHeight = PaLabelLayout.RowHeight(LabelFontSize);

				if (DebugLog || ExportCsv)
					diag = new PaDiagnosticsLog("DoubleTrap", Instrument != null ? Instrument.FullName : "Unknown");
				if (ExportCsv)
					sessionIterator = new SessionIterator(Bars);
			}
			else if (State == State.Realtime)
			{
				if (diag != null)
					diag.Flush();
			}
			else if (State == State.Terminated)
			{
				if (diag != null)
				{
					diag.Close();
					diag = null;
				}
			}
		}

		protected override void OnBarUpdate()
		{
			// The hosted indicator must be processed before its series are read (also its 1-tick updates).
			ec.Update();
			if (BarsInProgress != 0)
				return;

			if (sessionIterator != null && (CurrentBar == 0 || Bars.IsFirstBarOfSession))
			{
				sessionIterator.GetNextSession(Time[0], true);
				tradingDay = sessionIterator.ActualTradingDayExchange;
			}

			signal[0]       = 0;
			entryPrice[0]   = 0;
			stopPrice[0]    = 0;
			limitPrice[0]   = 0;
			riskTicks[0]    = 0;
			riskClass[0]    = 0;
			twoLegVisual[0] = false;
			weak2E[0]       = false;

			int barNo = barNumbering.Update(CurrentBar, Time[0], CurrentBar > 0 ? Time[1] : Time[0]);
			if (CurrentBar < 2)
				return;

			double ema0 = ema[0];
			int trend   = ec.TrendState[0];
			bool inside = High[0] <= High[1] + halfTick && Low[0] >= Low[1] - halfTick;

			if (diag != null)
			{
				diag.Context = new PaBarContext
				{
					BarNo    = barNo,
					BarEnd   = Time[0],
					BarStart = Time[1],
					Open     = Open[0],
					High     = High[0],
					Low      = Low[0],
					Close    = Close[0],
					Ema      = ema0,
					BarType  = inside ? "Inside" : string.Empty,
					Trend    = trend,
					KeyLevel = ec.EmaTouch[0]
				};
			}

			UpdateExhaustion(longSide, ema0, trend);
			UpdateExhaustion(shortSide, ema0, trend);

			if (ShowEntryCountLabels)
				DrawHostedLabels();

			ProcessSide(longSide, ema0, trend, inside);
			ProcessSide(shortSide, ema0, trend, inside);

			if (diag != null)
			{
				// Per-bar line: counts from EntryCount, state columns show the DT sequence stage.
				diag.Context.LongCount      = ec.LongCount[0];
				diag.Context.ShortCount     = ec.ShortCount[0];
				diag.Context.LongStateText  = StageText(longSide);
				diag.Context.ShortStateText = StageText(shortSide);
				if (ExportCsv && CsvIncludeBars)
					diag.WriteCsv(tradingDay, diag.FormatBar());
				if (State == State.Realtime)
					diag.Flush();
			}
		}

		private static string StageText(DtSide s)
		{
			switch (s.Stage)
			{
				case StageAwaitF2E:    return "DT: 2E seen, waiting for F2E";
				case StageAwaitSignal: return "DT: F2E seen, waiting for signal bar";
				case StagePending:     return "DT: signal pending";
				case StageDone:        return "DT: triggered";
				default:               return "DT: idle";
			}
		}

		#region Sequence

		private void ProcessSide(DtSide s, double ema0, int trend, bool inside)
		{
			bool isLong = s.IsLong;

			int entryEvent = isLong ? ec.LongEntryEvent[0] : ec.ShortEntryEvent[0];
			if (entryEvent == 1)
			{
				s.FirstSignalExt  = isLong ? ec.LongSignalLow[0] : ec.ShortSignalHigh[0];
				s.FirstTriggerBar = CurrentBar;
			}

			// Pending signal from an earlier bar: entry triggered, or cancelled by a new correction extreme.
			if (s.Stage == StagePending && CurrentBar > s.SigBar)
			{
				if (isLong ? High[0] >= s.Entry - halfTick : Low[0] <= s.Entry + halfTick)
				{
					s.Stage = StageDone;
					if (MarkTrigger)
						Draw.Diamond(this, "DT_TRG_" + CurrentBar, false, 0, s.Entry, TriggerColor);
					ExtendLines(s);
					LogDt("DtSignal", isLong, 3, double.NaN, null, s.Entry, s.Stop, 0, EntryCountRiskClass.None,
						string.Format(CultureInfo.InvariantCulture, "entry triggered (signal bar {0})", barNumbering.Get(s.SigBar)));
				}
				else if (isLong ? Low[0] < s.SigLow - halfTick : High[0] > s.SigHigh + halfTick)
				{
					s.Stage = StageAwaitSignal;
					LogDt("DtRejected", isLong, 3, ema0, null, double.NaN, double.NaN, 0, EntryCountRiskClass.None,
						"pending signal cancelled: new correction extreme before trigger");
				}
				else
				{
					ExtendLines(s);
				}
			}

			// A new 2E (raw event log, independent of resets) starts a new sequence.
			int lastSecond = isLong ? ec.LastSecondLongBar[0] : ec.LastSecondShortBar[0];
			if (lastSecond == CurrentBar && lastSecond != s.LastSeenSecondBar)
			{
				s.LastSeenSecondBar = lastSecond;
				StartSequence(s, trend);

				// Same-bar F2E: the outside bar that triggered the 2E already failed it (entry and exit at once).
				if (s.Stage == StageAwaitF2E && (isLong ? ec.FailedSecondLong[0] : ec.FailedSecondShort[0]))
				{
					s.Stage = StageAwaitSignal;
					s.F2Bar = CurrentBar;
					LogDt("DtCandidate", isLong, 3, ema0, null, double.NaN, double.NaN, 0, EntryCountRiskClass.None,
						isLong ? "sequence 2EL -> F2EL complete on the 2E bar, waiting for signal bar" : "sequence 2ES -> F2ES complete on the 2E bar, waiting for signal bar");
				}
				return;
			}

			if (s.Stage == StageIdle || s.Stage == StageDone)
				return;

			// Expiry: new trend extreme beyond the start of the pullback, or too many bars after the F2E.
			if (isLong ? High[0] > s.PullStartExt + halfTick : Low[0] < s.PullStartExt - halfTick)
			{
				ExpireSequence(s, ema0, isLong ? "sequence expired: new high above pullback start" : "sequence expired: new low below pullback start");
				return;
			}
			if (s.Stage >= StageAwaitSignal && CurrentBar - s.F2Bar > DtMaxBarsAfterF2E)
			{
				ExpireSequence(s, ema0, string.Format(CultureInfo.InvariantCulture, "sequence expired: {0} bars after F2E > {1}", CurrentBar - s.F2Bar, DtMaxBarsAfterF2E));
				return;
			}

			// Correction extreme since the 2E trigger and whether a counter move started after it.
			if (isLong ? Low[0] < s.CorrExt - halfTick : High[0] > s.CorrExt + halfTick)
			{
				s.CorrExt = isLong ? Low[0] : High[0];
				s.CorrExtBar = CurrentBar;
				s.CounterBreakSinceCorr = false;
			}
			else if (!inside && (isLong ? High[0] > High[1] + halfTick : Low[0] < Low[1] - halfTick))
			{
				s.CounterBreakSinceCorr = true;
			}

			if (s.Stage == StageAwaitF2E && (isLong ? ec.FailedSecondLong[0] : ec.FailedSecondShort[0]))
			{
				s.Stage = StageAwaitSignal;
				s.F2Bar = CurrentBar;
				LogDt("DtCandidate", isLong, 3, ema0, null, double.NaN, double.NaN, 0, EntryCountRiskClass.None,
					isLong ? "sequence 2EL -> F2EL complete, waiting for signal bar" : "sequence 2ES -> F2ES complete, waiting for signal bar");
			}

			if (s.Stage == StageAwaitSignal && (DtAllowF2EBarAsSignal || CurrentBar > s.F2Bar))
				EvaluateCandidate(s, ema0, trend, inside);
		}

		private void StartSequence(DtSide s, int trend)
		{
			bool isLong = s.IsLong;
			int sigBar  = isLong ? ec.LongSignalBar[0] : ec.ShortSignalBar[0];
			int sigAgo  = CurrentBar - sigBar;

			s.Stage = StageIdle;
			if (sigBar < 1 || sigAgo > 250)
				return;

			s.SecondTriggerBar = CurrentBar;
			s.SecondSignalBar  = sigBar;
			s.SecondSigHigh    = isLong ? ec.LongSignalHigh[0] : ec.ShortSignalHigh[0];
			s.SecondSigLow     = isLong ? ec.LongSignalLow[0] : ec.ShortSignalLow[0];
			s.PullStartExt     = isLong ? ec.LongEntryOriginPrice[0] : ec.ShortEntryOriginPrice[0];
			s.PullStartBar     = isLong ? ec.LongEntryOriginBar[0] : ec.ShortEntryOriginBar[0];

			// The 1E of the same count must lie between the pullback start and this 2E.
			s.SeqFirstExt = s.FirstTriggerBar > s.PullStartBar && s.FirstTriggerBar < CurrentBar ? s.FirstSignalExt : double.NaN;

			s.CorrExt               = isLong ? Low[0] : High[0];
			s.CorrExtBar            = CurrentBar;
			s.CounterBreakSinceCorr = false;

			double sigOpen   = Open[sigAgo];
			double sigClose  = Close[sigAgo];
			double emaAtSig  = ema[sigAgo];

			// ASSUMPTION: "with the trend" is checked at the 2E bar; at the signal bar the EMA must not slope against it.
			if (trend != (isLong ? 1 : -1))
			{
				LogDt("DtRejected", isLong, 2, emaAtSig, null, double.NaN, double.NaN, 0, EntryCountRiskClass.None,
					string.Format(CultureInfo.InvariantCulture, "2E{0} not with trend (trend state {1})", isLong ? "L" : "S", trend),
					s.SecondSigHigh, s.SecondSigLow, sigOpen, sigClose, sigBar);
				return;
			}

			s.Stage = StageAwaitF2E;
			string weakReasons = EvaluateWeak2E(s, sigAgo, emaAtSig);
			s.Weak = weakReasons != null;

			if (s.Weak)
			{
				weak2E[0] = true;
				if (ShowWeak2EMarker)
					Draw.Text(this, "DT_W_" + CurrentBar, false, "DT?", sigAgo, isLong ? s.SecondSigLow : s.SecondSigHigh,
						LabelOffset(isLong, 2), WeakColor, smallFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
				LogDt("Weak2E", isLong, 2, emaAtSig, null, double.NaN, double.NaN, 0, EntryCountRiskClass.None,
					weakReasons, s.SecondSigHigh, s.SecondSigLow, sigOpen, sigClose, sigBar);
			}
		}

		/// <summary>Returns null if the 2E is not weak, otherwise the criteria that matched. ASSUMPTION: any enabled criterion makes it weak.</summary>
		private string EvaluateWeak2E(DtSide s, int sigAgo, double emaAtSig)
		{
			bool isLong = s.IsLong;
			string reasons = null;

			// a) gap between the 2E signal bar and the EMA
			if (UseWeakEmaGap)
			{
				double gap = (isLong ? s.SecondSigLow - emaAtSig : emaAtSig - s.SecondSigHigh) / TickSize;
				if (gap > EmaGapTicks)
					reasons = AppendReason(reasons, string.Format(CultureInfo.InvariantCulture, "ema gap {0:0}T > {1}T", gap, EmaGapTicks));
			}

			// b) congestion over the last bars (ASSUMPTION: overlap = average overlap of consecutive bars in % of the smaller range)
			if (UseWeakCongestion)
			{
				int n = Math.Min(CongestionLookback, CurrentBar);
				if (n >= 2)
				{
					double hi = High[0], lo = Low[0], overlapSum = 0;
					for (int k = 0; k < n - 1; k++)
					{
						hi = Math.Max(hi, High[k + 1]);
						lo = Math.Min(lo, Low[k + 1]);
						double ov = Math.Min(High[k], High[k + 1]) - Math.Max(Low[k], Low[k + 1]);
						double minRange = Math.Min(High[k] - Low[k], High[k + 1] - Low[k + 1]);
						overlapSum += ov <= 0 ? 0 : (minRange <= 0 ? 100 : Math.Min(100, ov / minRange * 100));
					}
					double avgOverlap = overlapSum / (n - 1);
					double spanTicks  = (hi - lo) / TickSize;
					if (avgOverlap >= CongestionOverlapPct)
						reasons = AppendReason(reasons, string.Format(CultureInfo.InvariantCulture, "congestion overlap {0:0}% >= {1:0}%", avgOverlap, CongestionOverlapPct));
					else if (spanTicks < CongestionRangeTicks)
						reasons = AppendReason(reasons, string.Format(CultureInfo.InvariantCulture, "congestion span {0:0}T < {1}T", spanTicks, CongestionRangeTicks));
				}
			}

			// c) wrong signal bar direction or Rule 5 failed
			if (WeakIfQualityFails && !(isLong ? ec.LongSignalQualityOk[0] : ec.ShortSignalQualityOk[0]))
				reasons = AppendReason(reasons, "signal bar fails Rule 5");

			// d) short-term counter-trend structure intact: since the pullback swing (bounce after the 1E)
			//    no close beyond that swing. ASSUMPTION: swing = extreme between 1E trigger and 2E signal bar.
			if (UseWeakCounterTrend && !double.IsNaN(s.SeqFirstExt))
			{
				int from = Math.Max(s.FirstTriggerBar, CurrentBar - MaxScanBars);
				int to   = s.SecondSignalBar;
				if (from <= to)
				{
					double swing = isLong ? double.MinValue : double.MaxValue;
					int swingBar = from;
					for (int i = from; i <= to; i++)
					{
						double v = isLong ? High.GetValueAt(i) : Low.GetValueAt(i);
						if (isLong ? v > swing : v < swing)
						{
							swing = v;
							swingBar = i;
						}
					}
					bool intact = true;
					for (int i = swingBar; i <= CurrentBar && intact; i++)
					{
						double c = Close.GetValueAt(i);
						if (isLong ? c > swing : c < swing)
							intact = false;
					}
					if (intact)
						reasons = AppendReason(reasons, string.Format(CultureInfo.InvariantCulture, "counter-trend structure intact (swing {0})", swing));
				}
			}
			return reasons;
		}

		private static string AppendReason(string current, string add)
		{
			return current == null ? add : current + ", " + add;
		}

		private void ExpireSequence(DtSide s, double ema0, string reason)
		{
			s.Stage = StageIdle;
			LogDt("DtRejected", s.IsLong, 3, ema0, null, double.NaN, double.NaN, 0, EntryCountRiskClass.None, reason);
		}

		private void EvaluateCandidate(DtSide s, double ema0, int trend, bool inside)
		{
			bool isLong = s.IsLong;
			string reason = null;
			bool thirdEntryInvalid = false;
			bool structureOk = false;

			// Context filters
			if (trend == (isLong ? -1 : 1))
				reason = isLong ? "trend: EMA slopes down" : "trend: EMA slopes up";

			if (reason == null && trend == 0)
			{
				int crosses = CountEmaCrosses();
				if (crosses >= RangeCrossCount)
					reason = string.Format(CultureInfo.InvariantCulture, "range filter: {0} EMA crosses", crosses);
			}

			if (reason == null && UseRule6Filter)
				reason = Rule6Reason();

			if (reason == null && UseExhaustionFilter && s.ExState == ExExhausted)
				reason = isLong ? "exhaustion: new high after trend break" : "exhaustion: new low after trend break";

			// Condition 3: new correction extreme beyond the 1E and 2E signal bars, and bar t belongs to that leg
			if (reason == null)
			{
				if (!double.IsNaN(s.SeqFirstExt) && !(isLong ? s.CorrExt < s.SeqFirstExt - halfTick : s.CorrExt > s.SeqFirstExt + halfTick))
					reason = string.Format(CultureInfo.InvariantCulture, "no new {0} beyond 1E{1} signal bar ({2})", isLong ? "low" : "high", isLong ? "L" : "S", s.SeqFirstExt);
				else if (!(isLong ? s.CorrExt < s.SecondSigLow - halfTick : s.CorrExt > s.SecondSigHigh + halfTick))
					reason = string.Format(CultureInfo.InvariantCulture, "no new {0} beyond 2E{1} signal bar", isLong ? "low" : "high", isLong ? "L" : "S");
				else if (s.CorrExtBar != CurrentBar && s.CounterBreakSinceCorr)
					reason = "bar is not in the extreme leg of the correction";
				else if (inside)
					reason = "inside bar";
				else
					structureOk = true;
			}

			PaBarSnapshot bar = new PaBarSnapshot
			{
				Index    = CurrentBar,
				Open     = Open[0],
				High     = High[0],
				Low      = Low[0],
				Close    = Close[0],
				PrevHigh = High[1],
				PrevLow  = Low[1],
				HasPrev  = true,
				Ema      = ema0
			};
			SignalBarResult q = SignalBarQuality.Evaluate(isLong, bar.Open, bar.High, bar.Low, bar.Close, signalSettings);
			RiskResult risk   = StopRiskCalculator.Compute(isLong, bar, TickSize, riskSettings);
			double distTicks  = (isLong ? bar.Low - ema0 : ema0 - bar.High) / TickSize;

			if (structureOk)
				LogDt("DtCandidate", isLong, 3, ema0, q, risk.Trigger, risk.Stop, risk.RiskTicks, risk.RiskClass, "bar passes context and structure");

			// Condition 4: proximity to the EMA (touching or piercing is fine)
			if (reason == null && distTicks > DtEmaToleranceTicks + 1e-9)
				reason = string.Format(CultureInfo.InvariantCulture, "ema distance {0:0}T > {1}T", distTicks, DtEmaToleranceTicks);

			// Condition 5: close on the trend side of the EMA
			if (reason == null && (isLong ? bar.Close <= ema0 : bar.Close >= ema0))
			{
				reason = isLong ? "close not above EMA" : "close not below EMA";
				thirdEntryInvalid = true;
			}

			// Condition 6: Rule 5 signal bar quality
			if (reason == null && !q.Ok)
				reason = "quality: " + q.Reason;

			if (reason == null && RequireWeak2E && !s.Weak)
				reason = "2E not weak (RequireWeak2E)";

			if (reason == null && BlockTooLarge && risk.RiskClass == EntryCountRiskClass.TooLarge)
				reason = string.Format(CultureInfo.InvariantCulture, "risk too large: {0}T", risk.RiskTicks);

			if (reason != null)
			{
				if (thirdEntryInvalid)
				{
					if (ShowInvalidThirdEntry)
						Draw.Text(this, "DT_INV_" + CurrentBar, false, "DT x", 0, isLong ? bar.Low : bar.High, LabelOffset(isLong, 2),
							InvalidColor, smallFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
					LogDt("ThirdEntryInvalid", isLong, 3, ema0, q, risk.Trigger, risk.Stop, risk.RiskTicks, risk.RiskClass, reason);
				}
				else
				{
					LogDt("DtRejected", isLong, 3, ema0, q, risk.Trigger, risk.Stop, risk.RiskTicks, risk.RiskClass, reason);
				}
				return;
			}

			FireSignal(s, bar, q, risk);
		}

		private void FireSignal(DtSide s, PaBarSnapshot bar, SignalBarResult q, RiskResult risk)
		{
			bool isLong = s.IsLong;
			bool twoLegs = HasTwoLegVisual(s);

			signal[0]       = isLong ? 1 : -1;
			entryPrice[0]   = risk.Trigger;
			stopPrice[0]    = risk.Stop;
			limitPrice[0]   = risk.RiskClass == EntryCountRiskClass.PullbackLimit ? risk.LimitPrice : 0;
			riskTicks[0]    = risk.RiskTicks;
			riskClass[0]    = (int)risk.RiskClass;
			twoLegVisual[0] = twoLegs;
			weak2E[0]       = s.Weak;

			s.Stage   = StagePending;
			s.SigBar  = CurrentBar;
			s.SigHigh = bar.High;
			s.SigLow  = bar.Low;
			s.Entry   = risk.Trigger;
			s.Stop    = risk.Stop;

			Brush brush = isLong ? LongColor : ShortColor;
			string tag  = "DT_" + (isLong ? "L_" : "S_") + CurrentBar;
			string text = "DT";
			if (ShowDtRiskTicks)
				text += " " + risk.RiskTicks.ToString(CultureInfo.InvariantCulture) + "T" + StopRiskCalculator.ClassSuffix(risk.RiskClass);

			// Arrow drawn as text so it sits in the label grid (a price-based arrow would cover the bar numbers).
			Draw.Text(this, tag, false, (isLong ? "\u25B2 " : "\u25BC ") + text, 0, isLong ? bar.Low : bar.High, LabelOffset(isLong, 2),
				brush, labelFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			ExtendLines(s);

			string reason = string.Format(CultureInfo.InvariantCulture, "DT {0}; weak2E={1}; twoLegVisual={2}; quality {3}",
				isLong ? "long" : "short", s.Weak, twoLegs, q.Reason);
			LogDt("DtSignal", isLong, 3, bar.Ema, q, risk.Trigger, risk.Stop, risk.RiskTicks, risk.RiskClass, reason);

			if (EnableAlerts && State == State.Realtime)
				Alert("DoubleTrap" + (isLong ? "L" : "S"), Priority.High,
					string.Format(CultureInfo.InvariantCulture, "DoubleTrap {0}: entry {1} stop {2} risk {3}T {4}",
						isLong ? "LONG" : "SHORT", risk.Trigger, risk.Stop, risk.RiskTicks, risk.RiskClass),
					NinjaTrader.Core.Globals.InstallDir + @"sounds\Alert1.wav", 10, Brushes.Black, brush);
		}

		private void ExtendLines(DtSide s)
		{
			if (!ShowEntryStopLines)
				return;
			int startAgo = CurrentBar - s.SigBar + 1;
			if (startAgo > CurrentBar)
				return;
			Draw.Line(this, "DT_E_" + s.SigBar, false, startAgo, s.Entry, 0, s.Entry, s.IsLong ? LongColor : ShortColor, DashStyleHelper.Solid, 2);
			Draw.Line(this, "DT_SL_" + s.SigBar, false, startAgo, s.Stop, 0, s.Stop, StopLineColor, DashStyleHelper.Dash, 1);
		}

		#endregion

		#region Context filters

		private int CountEmaCrosses()
		{
			int n = Math.Min(RangeLookback, CurrentBar - 1);
			int crosses = 0;
			for (int k = 0; k < n; k++)
			{
				bool above     = Close[k] > ema[k];
				bool prevAbove = Close[k + 1] > ema[k + 1];
				if (above != prevAbove)
					crosses++;
			}
			return crosses;
		}

		private string Rule6Reason()
		{
			int n = Math.Min(Rule6Lookback, CurrentBar + 1);
			if (n <= Rule6MinBars)
				return null;

			double hi = double.MinValue, lo = double.MaxValue, sumRange = 0;
			for (int k = 0; k < n; k++)
			{
				hi = Math.Max(hi, High[k]);
				lo = Math.Min(lo, Low[k]);
				sumRange += High[k] - Low[k];
			}
			double spanTicks = (hi - lo) / TickSize;
			if (spanTicks <= Rule6MaxSpanTicks)
				return string.Format(CultureInfo.InvariantCulture, "rule6: span {0:0}T <= {1}T", spanTicks, Rule6MaxSpanTicks);

			double efficiency = sumRange > 0 ? Math.Abs(Close[0] - Open[n - 1]) / sumRange * 100.0 : 0;
			if (efficiency <= Rule6MinEfficiencyPct)
				return string.Format(CultureInfo.InvariantCulture, "rule6: efficiency {0:0.#}% <= {1:0.#}%", efficiency, Rule6MinEfficiencyPct);
			return null;
		}

		/// <summary>
		/// ASSUMPTION (exhaustion): a trend break is the first close on the wrong side of the EMA after at least
		/// ExhaustionMinTrendBars consecutive closes on the trend side. A later new extreme beyond the trend extreme
		/// marks the trend as exhausted. Cleared when the EMA slopes against the direction or after a fresh run of
		/// ExhaustionMinTrendBars closes on the trend side.
		/// </summary>
		private void UpdateExhaustion(DtSide s, double ema0, int trend)
		{
			bool isLong   = s.IsLong;
			bool trendSide = isLong ? Close[0] > ema0 : Close[0] < ema0;
			double ext    = isLong ? High[0] : Low[0];

			int previousRun = s.RunLen;
			double previousRunExt = s.RunExt;
			if (trendSide)
			{
				s.RunExt = s.RunLen == 0 ? ext : (isLong ? Math.Max(s.RunExt, ext) : Math.Min(s.RunExt, ext));
				s.RunLen++;
			}
			else
			{
				s.RunLen = 0;
			}

			if (trend == (isLong ? -1 : 1))
			{
				s.ExState = ExNormal;
				return;
			}

			switch (s.ExState)
			{
				case ExNormal:
					if (!trendSide && previousRun >= ExhaustionMinTrendBars)
					{
						s.ExState   = ExBroken;
						s.BrokenExt = isLong ? Math.Max(previousRunExt, ext) : Math.Min(previousRunExt, ext);
					}
					break;
				case ExBroken:
					if (isLong ? High[0] > s.BrokenExt + halfTick : Low[0] < s.BrokenExt - halfTick)
					{
						s.ExState    = ExExhausted;
						s.RunSinceEx = trendSide ? 1 : 0;
					}
					break;
				case ExExhausted:
					s.RunSinceEx = trendSide ? s.RunSinceEx + 1 : 0;
					if (s.RunSinceEx >= ExhaustionMinTrendBars)
						s.ExState = ExNormal;
					break;
			}
		}

		/// <summary>
		/// Display flag: the correction from the trend extreme to bar t shows two clear legs, i.e. a confirmed
		/// swing (SwingStrength) against the correction that lies inside the pullback, preceded by a confirmed
		/// swing in correction direction that the final leg reached or exceeded.
		/// </summary>
		private bool HasTwoLegVisual(DtSide s)
		{
			bool isLong = s.IsLong;
			int n = SwingStrength;
			int first = Math.Max(s.PullStartBar, CurrentBar - MaxScanBars);
			int last  = CurrentBar;
			bool haveFirstLeg = false;

			for (int i = first + n; i <= last - n; i++)
			{
				bool swingLow = true, swingHigh = true;
				double lo = Low.GetValueAt(i), hi = High.GetValueAt(i);
				for (int k = 1; k <= n; k++)
				{
					if (!(lo < Low.GetValueAt(i - k) && lo <= Low.GetValueAt(i + k))) swingLow = false;
					if (!(hi > High.GetValueAt(i - k) && hi >= High.GetValueAt(i + k))) swingHigh = false;
				}

				// Long: first leg down ends at a swing low, second leg starts at a lower swing high.
				bool legEnd   = isLong ? swingLow : swingHigh;
				bool legStart = isLong ? swingHigh : swingLow;
				double legEndPrice   = isLong ? lo : hi;
				double legStartPrice = isLong ? hi : lo;

				if (legEnd && (isLong ? legEndPrice >= s.CorrExt - halfTick : legEndPrice <= s.CorrExt + halfTick))
					haveFirstLeg = true;
				else if (legStart && haveFirstLeg && (isLong ? legStartPrice < s.PullStartExt : legStartPrice > s.PullStartExt))
					return true;
			}
			return false;
		}

		#endregion

		#region Drawing and diagnostics

		private int LabelOffset(bool belowLow, int row)
		{
			return PaLabelLayout.Offset(belowLow, row, rowHeight, BarNumberClearance);
		}

		private void DrawHostedLabels()
		{
			int le = ec.LongEntryEvent[0];
			if (le > 0 && le <= HostedLabelMaxCount && (le > 1 || ShowFirstEntries))
			{
				int ago = CurrentBar - ec.LongSignalBar[0];
				if (ago >= 0 && ago <= CurrentBar)
					Draw.Text(this, "DT_ECL_" + CurrentBar, false, le.ToString(CultureInfo.InvariantCulture) + "EL", ago, ec.LongSignalLow[0], LabelOffset(true, 0),
						ec.LongSignalQualityOk[0] ? LongColor : QualityFailColor, smallFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}
			int se = ec.ShortEntryEvent[0];
			if (se > 0 && se <= HostedLabelMaxCount && (se > 1 || ShowFirstEntries))
			{
				int ago = CurrentBar - ec.ShortSignalBar[0];
				if (ago >= 0 && ago <= CurrentBar)
					Draw.Text(this, "DT_ECS_" + CurrentBar, false, se.ToString(CultureInfo.InvariantCulture) + "ES", ago, ec.ShortSignalHigh[0], LabelOffset(false, 0),
						ec.ShortSignalQualityOk[0] ? ShortColor : QualityFailColor, smallFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}
			if (ec.FailedSecondLong[0])
				Draw.Text(this, "DT_ECF2L_" + CurrentBar, false, "F", 0, Low[0], LabelOffset(true, 1), FailedLongColor, smallFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			if (ec.FailedSecondShort[0])
				Draw.Text(this, "DT_ECF2S_" + CurrentBar, false, "F", 0, High[0], LabelOffset(false, 1), FailedShortColor, smallFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
		}

		/// <summary>Log line for the current bar (signal fields = current bar unless explicit signal values are given).</summary>
		private void LogDt(string evt, bool isLong, int count, double emaValue, SignalBarResult? q,
			double trigger, double stop, int riskTicksValue, EntryCountRiskClass cls, string reason,
			double sigHigh = double.NaN, double sigLow = double.NaN, double sigOpen = double.NaN, double sigClose = double.NaN,
			int sigBarIndex = -1)
		{
			if (diag == null)
				return;

			bool explicitSignal = !double.IsNaN(sigHigh);
			double h = explicitSignal ? sigHigh : High[0];
			double l = explicitSignal ? sigLow : Low[0];
			double o = explicitSignal ? sigOpen : Open[0];
			double c = explicitSignal ? sigClose : Close[0];
			double dist = double.IsNaN(emaValue) ? double.NaN : (isLong ? l - emaValue : emaValue - h) / TickSize;
			int sigBarNo = barNumbering.Get(sigBarIndex >= 0 ? sigBarIndex : CurrentBar);

			// Count column: 2 for 2E related events, 3 for DT events (technically the third entry).
			string line = diag.Format(evt, isLong ? "Long" : "Short", count, sigBarNo, h, l, o, c, emaValue, dist,
				q.HasValue ? q.Value.BodyPct : double.NaN,
				q.HasValue ? q.Value.ClosePct : double.NaN,
				q.HasValue ? q.Value.WickPct : double.NaN,
				q.HasValue ? (q.Value.Ok ? 1 : 0) : -1,
				trigger, stop, riskTicksValue, cls, reason);

			if (DebugLog)
				Print(line);
			if (ExportCsv)
			{
				diag.WriteCsv(tradingDay, line);
				if (diag.LastError != null && DebugLog)
					Print("DoubleTrap CSV: " + diag.LastError);
			}
		}

		#endregion

		#region Public series

		/// <summary>+1 long DT, -1 short DT, 0 none (set at the close of the signal bar).</summary>
		[Browsable(false)] [XmlIgnore] public Series<int> Signal { get { Update(); return signal; } }
		[Browsable(false)] [XmlIgnore] public Series<double> EntryPrice { get { Update(); return entryPrice; } }
		[Browsable(false)] [XmlIgnore] public Series<double> StopPrice { get { Update(); return stopPrice; } }
		/// <summary>Limit price for RiskClass PullbackLimit, otherwise 0.</summary>
		[Browsable(false)] [XmlIgnore] public Series<double> LimitPrice { get { Update(); return limitPrice; } }
		[Browsable(false)] [XmlIgnore] public Series<int> RiskTicks { get { Update(); return riskTicks; } }
		/// <summary>EntryCountRiskClass as int.</summary>
		[Browsable(false)] [XmlIgnore] public Series<int> RiskClass { get { Update(); return riskClass; } }
		[Browsable(false)] [XmlIgnore] public Series<bool> TwoLegVisual { get { Update(); return twoLegVisual; } }
		/// <summary>True on the bar of a weak 2E and on a DT signal bar whose 2E was weak.</summary>
		[Browsable(false)] [XmlIgnore] public Series<bool> Weak2E { get { Update(); return weak2E; } }

		#endregion

		#region Properties

		// 1. Shared (passed to EntryCount)
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA period", Description = "EMA(Close) period in bars.", Order = 1, GroupName = "01. Shared (passed to EntryCount)")]
		public int EmaPeriod { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Key level tolerance (ticks)", Description = "Max distance in ticks between bar and EMA/trend line that still counts as key level contact (EntryCount resets).", Order = 2, GroupName = "01. Shared (passed to EntryCount)")]
		public int KeyLevelToleranceTicks { get; set; }

		[Range(1, 20)]
		[Display(Name = "Swing strength (bars)", Description = "Bars on each side needed to confirm a swing (S/R, trend lines, two-leg visual).", Order = 3, GroupName = "01. Shared (passed to EntryCount)")]
		public int SwingStrength { get; set; }

		[Display(Name = "Use swing S/R", Description = "Confirmed swing highs/lows count as key levels for EntryCount reset a).", Order = 4, GroupName = "01. Shared (passed to EntryCount)")]
		public bool UseSwingSR { get; set; }

		[Range(1, 30)]
		[Display(Name = "S/R lookback (swings)", Description = "Number of most recent confirmed swing highs and lows used as S/R levels.", Order = 5, GroupName = "01. Shared (passed to EntryCount)")]
		public int SrLookbackSwings { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "S/R tolerance (ticks)", Description = "Max distance in ticks between bar and an S/R level that still counts as contact.", Order = 6, GroupName = "01. Shared (passed to EntryCount)")]
		public int SrToleranceTicks { get; set; }

		[Display(Name = "Use trend lines", Description = "ASSUMPTION: line through the last two confirmed swings in trend direction counts as key level.", Order = 7, GroupName = "01. Shared (passed to EntryCount)")]
		public bool UseTrendlines { get; set; }

		[Display(Name = "Outside bar policy", Description = "Which side of an outside bar counts: BothSides (default: high and low break both count), CloseDirection (only the side of the close), or Ignore.", Order = 8, GroupName = "01. Shared (passed to EntryCount)")]
		public EntryCountOutsideBarPolicy OutsideBarPolicy { get; set; }

		[Range(1, 200)]
		[Display(Name = "EMA slope lookback (bars)", Description = "Trend slope = EMA[0] - EMA[lookback], in bars.", Order = 9, GroupName = "01. Shared (passed to EntryCount)")]
		public int EmaSlopeLookback { get; set; }

		[Range(0, double.MaxValue)]
		[Display(Name = "Min EMA slope (ticks)", Description = "Slope in ticks over the lookback for up (>=) or down (<= -value) trend; otherwise flat.", Order = 10, GroupName = "01. Shared (passed to EntryCount)")]
		public double MinEmaSlopeTicks { get; set; }

		// 2. Signal bar (Rule 5)
		[Range(0, 100)]
		[Display(Name = "Min body (%)", Description = "Minimum body in % of the bar range.", Order = 1, GroupName = "02. Signal bar (Rule 5)")]
		public double MinBodyPct { get; set; }

		[Range(0, 100)]
		[Display(Name = "Min close (%)", Description = "Close position in % of the range, measured from the low (long) or high (short).", Order = 2, GroupName = "02. Signal bar (Rule 5)")]
		public double MinClosePct { get; set; }

		[Range(0, 100)]
		[Display(Name = "Max breakout wick (%)", Description = "Maximum wick on the breakout side in % of the range.", Order = 3, GroupName = "02. Signal bar (Rule 5)")]
		public double MaxBreakoutWickPct { get; set; }

		[Display(Name = "Use rejection exception", Description = "A bar with a very strong close and tiny breakout wick is valid regardless of its body.", Order = 4, GroupName = "02. Signal bar (Rule 5)")]
		public bool UseRejectionException { get; set; }

		[Range(0, 100)]
		[Display(Name = "Rejection min close (%)", Description = "Close position in % of the range for the rejection exception.", Order = 5, GroupName = "02. Signal bar (Rule 5)")]
		public double RejectionMinClosePct { get; set; }

		[Range(0, 100)]
		[Display(Name = "Rejection max wick (%)", Description = "Maximum breakout wick in % of the range for the rejection exception.", Order = 6, GroupName = "02. Signal bar (Rule 5)")]
		public double RejectionMaxWickPct { get; set; }

		// 3. Stop / risk
		[Display(Name = "Stop mode", Description = "SignalAndPrevBar: stop beyond signal bar and the bar before it. SignalBarOnly: beyond the signal bar.", Order = 1, GroupName = "03. Stop / risk")]
		public EntryCountStopMode StopMode { get; set; }

		[Range(2, int.MaxValue)]
		[Display(Name = "Risk template (ticks)", Description = "ATM template size in ticks; max risk = template - 1 tick.", Order = 2, GroupName = "03. Stop / risk")]
		public int RiskTemplateTicks { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Risk tolerance (ticks)", Description = "Ticks the risk may exceed max risk and still count as Direct.", Order = 3, GroupName = "03. Stop / risk")]
		public int RiskToleranceTicks { get; set; }

		[Range(0, 100)]
		[Display(Name = "Max pullback (%)", Description = "Max retracement in % of the signal structure for a PullbackLimit entry.", Order = 4, GroupName = "03. Stop / risk")]
		public double MaxPullbackPct { get; set; }

		[Display(Name = "Block too large", Description = "No DT signal if the RiskClass is TooLarge (reason in the diagnostic log).", Order = 5, GroupName = "03. Stop / risk")]
		public bool BlockTooLarge { get; set; }

		// 4. Hosted EntryCount
		[Display(Name = "Require equal or higher leg", Description = "EntryCount strict ABC: the second leg must reach the extreme of the first leg.", Order = 1, GroupName = "04. EntryCount (hosted)")]
		public bool RequireEqualOrHigherLeg { get; set; }

		[Display(Name = "Reset on key level (a, c)", Description = "EntryCount: pullback to a key level resets the count(s) selected by Reset scope.", Order = 2, GroupName = "04. EntryCount (hosted)")]
		public bool ResetOnKeyLevel { get; set; }

		[Display(Name = "Reset scope", Description = "EntryCount: CounterTrendOnly or Both.", Order = 3, GroupName = "04. EntryCount (hosted)")]
		public EntryCountResetScope ResetScope { get; set; }

		[Display(Name = "Reset after two legs (b)", Description = "EntryCount: reset after an F2E at the swing extreme formed by the 2E/F2E.", Order = 4, GroupName = "04. EntryCount (hosted)")]
		public bool ResetAfterTwoLegs { get; set; }

		[Display(Name = "Reset on new extreme (d)", Description = "EntryCount: a new extreme beyond the origin restarts the count.", Order = 5, GroupName = "04. EntryCount (hosted)")]
		public bool ResetOnNewExtreme { get; set; }

		[Display(Name = "Failed entry mode", Description = "EntryCount: PriorBarBreak = a 2E fails as soon as a later bar trades 1 tick beyond the direct prior bar (also if that is an inside bar). SignalBarBreak = only when the 2E signal bar extreme is traded.", Order = 6, GroupName = "04. EntryCount (hosted)")]
		public EntryCountFailedEntryMode FailedEntryMode { get; set; }

		[Display(Name = "Inside bar as signal bar", Description = "EntryCount: an inside bar during a leg becomes the signal bar (never a trigger, still skipped for leg detection).", Order = 7, GroupName = "04. EntryCount (hosted)")]
		public bool InsideBarAsSignal { get; set; }

		// 5. Double Trap
		[Range(0, int.MaxValue)]
		[Display(Name = "DT EMA tolerance (ticks)", Description = "Max distance in ticks between the signal bar extreme (low long / high short) and the EMA; touching or piercing is always ok.", Order = 1, GroupName = "05. Double Trap")]
		public int DtEmaToleranceTicks { get; set; }

		[Range(1, 200)]
		[Display(Name = "Max bars after F2E (bars)", Description = "The sequence expires this many bars after the F2E without a signal.", Order = 2, GroupName = "05. Double Trap")]
		public int DtMaxBarsAfterF2E { get; set; }

		[Display(Name = "Allow F2E bar as signal bar", Description = "ASSUMPTION: the bar that triggers the F2E may itself be the DT signal bar (e.g. reversal bar).", Order = 3, GroupName = "05. Double Trap")]
		public bool DtAllowF2EBarAsSignal { get; set; }

		// 6. Weak 2E
		[Display(Name = "Weak: EMA gap", Description = "a) 2E signal bar more than EMA gap ticks away from the EMA.", Order = 1, GroupName = "06. Weak 2E (DT expected)")]
		public bool UseWeakEmaGap { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "EMA gap (ticks)", Description = "Gap in ticks between 2E signal bar and EMA for criterion a).", Order = 2, GroupName = "06. Weak 2E (DT expected)")]
		public int EmaGapTicks { get; set; }

		[Display(Name = "Weak: congestion", Description = "b) congestion before the 2E (overlap or small span).", Order = 3, GroupName = "06. Weak 2E (DT expected)")]
		public bool UseWeakCongestion { get; set; }

		[Range(2, 100)]
		[Display(Name = "Congestion lookback (bars)", Description = "Bars ending at the 2E bar used for criterion b).", Order = 4, GroupName = "06. Weak 2E (DT expected)")]
		public int CongestionLookback { get; set; }

		[Range(0, 100)]
		[Display(Name = "Congestion overlap (%)", Description = "Average overlap of consecutive bars in % of the smaller bar range that counts as congestion.", Order = 5, GroupName = "06. Weak 2E (DT expected)")]
		public double CongestionOverlapPct { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Congestion range (ticks)", Description = "Total span in ticks below which the lookback counts as congestion.", Order = 6, GroupName = "06. Weak 2E (DT expected)")]
		public int CongestionRangeTicks { get; set; }

		[Display(Name = "Weak if quality fails", Description = "c) 2E signal bar in wrong direction or failing Rule 5.", Order = 7, GroupName = "06. Weak 2E (DT expected)")]
		public bool WeakIfQualityFails { get; set; }

		[Display(Name = "Weak: counter-trend structure", Description = "d) no close beyond the last pullback swing since that swing (counter-trend structure intact).", Order = 8, GroupName = "06. Weak 2E (DT expected)")]
		public bool UseWeakCounterTrend { get; set; }

		[Display(Name = "Require weak 2E", Description = "Only signal a DT if its 2E was weak.", Order = 9, GroupName = "06. Weak 2E (DT expected)")]
		public bool RequireWeak2E { get; set; }

		// 7. Context filters
		[Range(2, 250)]
		[Display(Name = "Range lookback (bars)", Description = "Bars in which EMA crosses are counted for the trading range filter.", Order = 1, GroupName = "07. Context filters")]
		public int RangeLookback { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "Range cross count", Description = "Flat EMA plus at least this many close crosses of the EMA = trading range, no signal.", Order = 2, GroupName = "07. Context filters")]
		public int RangeCrossCount { get; set; }

		[Display(Name = "Use Rule 6 filter", Description = "No signal in tight or inefficient markets (span or efficiency over the Rule 6 lookback).", Order = 3, GroupName = "07. Context filters")]
		public bool UseRule6Filter { get; set; }

		[Range(2, 250)]
		[Display(Name = "Rule 6 lookback (bars)", Description = "Bars used for the Rule 6 span and efficiency.", Order = 4, GroupName = "07. Context filters")]
		public int Rule6Lookback { get; set; }

		[Range(1, 250)]
		[Display(Name = "Rule 6 min bars (bars)", Description = "Rule 6 only applies if more than this many bars are available.", Order = 5, GroupName = "07. Context filters")]
		public int Rule6MinBars { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Rule 6 max span (ticks)", Description = "Span High-Low over the lookback at or below this many ticks blocks signals.", Order = 6, GroupName = "07. Context filters")]
		public int Rule6MaxSpanTicks { get; set; }

		[Range(0, 100)]
		[Display(Name = "Rule 6 min efficiency (%)", Description = "|Close[0] - Open[lookback]| / sum(High-Low) at or below this % blocks signals.", Order = 7, GroupName = "07. Context filters")]
		public double Rule6MinEfficiencyPct { get; set; }

		[Display(Name = "Use exhaustion filter", Description = "ASSUMPTION: no DT after a trend break (close beyond EMA) followed by a new trend extreme.", Order = 8, GroupName = "07. Context filters")]
		public bool UseExhaustionFilter { get; set; }

		[Range(1, 500)]
		[Display(Name = "Exhaustion min trend bars (bars)", Description = "Consecutive closes on the trend side of the EMA that define an established trend (break detection and clearing).", Order = 9, GroupName = "07. Context filters")]
		public int ExhaustionMinTrendBars { get; set; }

		// 8. Display
		[Display(Name = "Show EntryCount labels", Description = "Draw the 1E/2E/3E and F2E labels of the hosted EntryCount.", Order = 1, GroupName = "08. Display")]
		public bool ShowEntryCountLabels { get; set; }

		[Display(Name = "Show first entries", Description = "With EntryCount labels on: also show the 1EL/1ES labels. Counting is not affected.", Order = 1, GroupName = "08. Display")]
		public bool ShowFirstEntries { get; set; }

		[Display(Name = "Show weak 2E marker", Description = "'DT?' at the signal bar of a weak 2E.", Order = 2, GroupName = "08. Display")]
		public bool ShowWeak2EMarker { get; set; }

		[Display(Name = "Show invalid third entry", Description = "'DT x' when everything fits except the close on the trend side of the EMA.", Order = 3, GroupName = "08. Display")]
		public bool ShowInvalidThirdEntry { get; set; }

		[Display(Name = "Show entry/stop lines", Description = "Entry and stop lines while the DT signal is pending.", Order = 4, GroupName = "08. Display")]
		public bool ShowEntryStopLines { get; set; }

		[Display(Name = "Show DT risk ticks", Description = "Risk in ticks next to the DT label (L = PullbackLimit, X = TooLarge).", Order = 5, GroupName = "08. Display")]
		public bool ShowDtRiskTicks { get; set; }

		[Display(Name = "Mark trigger", Description = "Diamond at the entry price when the DT entry is actually triggered.", Order = 6, GroupName = "08. Display")]
		public bool MarkTrigger { get; set; }

		[Range(6, 30)]
		[Display(Name = "Label font size", Description = "Font size of the DT label in points.", Order = 7, GroupName = "08. Display")]
		public int LabelFontSize { get; set; }

		[Range(0, 200)]
		[Display(Name = "Bar number clearance (px)", Description = "Free space in pixels below the low before the first label row, so labels do not cover the bar numbers of BarNumberToggleIndicator. Rows: EntryCount label, F, DT marker.", Order = 10, GroupName = "08. Display")]
		public int BarNumberClearance { get; set; }

		[Display(Name = "Enable alerts", Description = "Alert with sound on a new DT signal (realtime only).", Order = 8, GroupName = "08. Display")]
		public bool EnableAlerts { get; set; }

		// 9. Colors
		[XmlIgnore]
		[Display(Name = "Long color", Description = "Long DT arrow, label and entry line.", Order = 1, GroupName = "09. Colors")]
		public Brush LongColor { get; set; }

		[Browsable(false)]
		public string LongColorSerialize
		{
			get { return Serialize.BrushToString(LongColor); }
			set { LongColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Short color", Description = "Short DT arrow, label and entry line.", Order = 2, GroupName = "09. Colors")]
		public Brush ShortColor { get; set; }

		[Browsable(false)]
		public string ShortColorSerialize
		{
			get { return Serialize.BrushToString(ShortColor); }
			set { ShortColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Quality fail color", Description = "EntryCount labels whose signal bar fails Rule 5.", Order = 3, GroupName = "09. Colors")]
		public Brush QualityFailColor { get; set; }

		[Browsable(false)]
		public string QualityFailColorSerialize
		{
			get { return Serialize.BrushToString(QualityFailColor); }
			set { QualityFailColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Weak 2E color", Description = "'DT?' marker.", Order = 4, GroupName = "09. Colors")]
		public Brush WeakColor { get; set; }

		[Browsable(false)]
		public string WeakColorSerialize
		{
			get { return Serialize.BrushToString(WeakColor); }
			set { WeakColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Invalid color", Description = "'DT x' marker (ThirdEntryInvalid).", Order = 5, GroupName = "09. Colors")]
		public Brush InvalidColor { get; set; }

		[Browsable(false)]
		public string InvalidColorSerialize
		{
			get { return Serialize.BrushToString(InvalidColor); }
			set { InvalidColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Stop line color", Description = "Stop line while the DT signal is pending.", Order = 6, GroupName = "09. Colors")]
		public Brush StopLineColor { get; set; }

		[Browsable(false)]
		public string StopLineColorSerialize
		{
			get { return Serialize.BrushToString(StopLineColor); }
			set { StopLineColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Trigger color", Description = "Trigger marker when the DT entry is filled.", Order = 7, GroupName = "09. Colors")]
		public Brush TriggerColor { get; set; }

		[Browsable(false)]
		public string TriggerColorSerialize
		{
			get { return Serialize.BrushToString(TriggerColor); }
			set { TriggerColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Failed long color", Description = "Green 'F' marker of a failed second long entry (F2EL), with EntryCount labels on.", Order = 8, GroupName = "09. Colors")]
		public Brush FailedLongColor { get; set; }

		[Browsable(false)]
		public string FailedLongColorSerialize
		{
			get { return Serialize.BrushToString(FailedLongColor); }
			set { FailedLongColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Failed short color", Description = "Red 'F' marker of a failed second short entry (F2ES), with EntryCount labels on.", Order = 9, GroupName = "09. Colors")]
		public Brush FailedShortColor { get; set; }

		[Browsable(false)]
		public string FailedShortColorSerialize
		{
			get { return Serialize.BrushToString(FailedShortColor); }
			set { FailedShortColor = Serialize.StringToBrush(value); }
		}

		// 10. Diagnostics
		[Display(Name = "Debug log", Description = "Print one ';'-separated line per event (Weak2E, DtCandidate, DtSignal, DtRejected, ThirdEntryInvalid) to the Output tab.", Order = 1, GroupName = "10. Diagnostics")]
		public bool DebugLog { get; set; }

		[Display(Name = "Export CSV", Description = "Write the same lines to Documents\\NinjaTrader 8\\DoubleTrap_<Instrument>_<yyyyMMdd>.csv (one file per trading day, overwritten on reload).", Order = 2, GroupName = "10. Diagnostics")]
		public bool ExportCsv { get; set; }

		[Display(Name = "CSV: one line per bar", Description = "Adds a 'Bar' line for every bar to the CSV (bar number starting at 1 per day, OHLC, trend, key level, EntryCount counts and DT sequence stage). Not printed to the Output tab.", Order = 3, GroupName = "10. Diagnostics")]
		public bool CsvIncludeBars { get; set; }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private DoubleTrap[] cacheDoubleTrap;
		public DoubleTrap DoubleTrap()
		{
			return DoubleTrap(Input);
		}

		public DoubleTrap DoubleTrap(ISeries<double> input)
		{
			if (cacheDoubleTrap != null)
				for (int idx = 0; idx < cacheDoubleTrap.Length; idx++)
					if (cacheDoubleTrap[idx] != null &&  cacheDoubleTrap[idx].EqualsInput(input))
						return cacheDoubleTrap[idx];
			return CacheIndicator<DoubleTrap>(new DoubleTrap(), input, ref cacheDoubleTrap);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.DoubleTrap DoubleTrap()
		{
			return indicator.DoubleTrap(Input);
		}

		public Indicators.DoubleTrap DoubleTrap(ISeries<double> input )
		{
			return indicator.DoubleTrap(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.DoubleTrap DoubleTrap()
		{
			return indicator.DoubleTrap(Input);
		}

		public Indicators.DoubleTrap DoubleTrap(ISeries<double> input )
		{
			return indicator.DoubleTrap(input);
		}
	}
}

#endregion
