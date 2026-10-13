#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
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
 * EntryCount - counts price action entries (1E/2E/3E..., long and short, incl. failed second entries).
 *
 * Long and short counts run independently. Short count: origin = lowest swing, an up leg forms,
 * a bar trading at least 1 tick below the low of the latest leg bar (signal bar) is the next short entry.
 * Long count is the exact mirror. Inside bars are never triggers and are skipped for leg detection;
 * with InsideBarAsSignal (default) an inside bar inside a leg can be the signal bar.
 * Signal bar quality (Rule 5) and stop/risk are computed for every entry but never filter the count.
 *
 * TEST CASES (check in Market Replay, ES 2000 tick):
 *  1. Low -> bullish bars -> break of a low = 1ES -> break of a high -> bullish bar -> break of its low = 2ES
 *  2. The 2ES signal bar breaks no prior low, but its own low is traded -> still 2ES
 *  3. Price trades above the high of the 2ES signal bar -> F2ES
 *  4. Downtrend: 2EL/F2EL form a lower high -> long count resets without a new extreme (R:2L)
 *  5. Uptrend, pullback to the EMA -> short count resets, long count keeps running (ResetScope CounterTrendOnly)
 *  6. No reset -> count continues to 3E/4E; an inside bar is never a trigger, but (InsideBarAsSignal) can be the signal bar
 *     (7.10.26: bar 124 inside bar = 2EL signal bar, triggered by bar 125)
 *  7. Second up leg stays below the first leg high (lower high) -> still 2ES (RequireEqualOrHigherLeg false);
 *     with true -> no 2ES
 *  8. 2EL signal bar with body 30 %, close 95 %, upper wick 5 % -> Rule 5 ok via rejection exception;
 *     body 30 %, close 75 % -> label gray
 *  9. Structural risk 22 ticks with RiskTemplateTicks 16 -> RiskClass PullbackLimit or TooLarge per 49 % rule
 */

#region Shared enums (global namespace so the generated factory code resolves them everywhere)

public enum EntryCountOutsideBarPolicy
{
	CloseDirection,
	BothSides,
	Ignore
}

public enum EntryCountStopMode
{
	SignalAndPrevBar,
	SignalBarOnly
}

public enum EntryCountResetScope
{
	CounterTrendOnly,
	Both
}

public enum EntryCountRiskClass
{
	None = 0,
	Direct = 1,
	PullbackLimit = 2,
	TooLarge = 3
}

public enum EntryCountFailedEntryMode
{
	PriorBarBreak,
	SignalBarBreak
}

public enum EntryCountResetReason
{
	None = 0,
	KeyLevel = 1,
	TwoLegs = 2,
	NewExtreme = 3
}

#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	#region Shared building blocks (used by EntryCount and DoubleTrap)

	/// <summary>Immutable copy of one bar, so signal bars can be evaluated later without bar lookups.</summary>
	public struct PaBarSnapshot
	{
		public int Index;
		public double Open;
		public double High;
		public double Low;
		public double Close;
		public double PrevHigh;
		public double PrevLow;
		public bool HasPrev;
		public double Ema;
	}

	public sealed class SignalBarSettings
	{
		public double MinBodyPct;
		public double MinClosePct;
		public double MaxBreakoutWickPct;
		public bool UseRejectionException;
		public double RejectionMinClosePct;
		public double RejectionMaxWickPct;
	}

	public struct SignalBarResult
	{
		public bool Ok;
		public bool IsRejectionBar;
		public double BodyPct;
		public double ClosePct;
		public double WickPct;
		public string Reason;
	}

	/// <summary>Rule 5: signal bar quality, measured relative to the bar range.</summary>
	public static class SignalBarQuality
	{
		private const double Eps = 1e-6;

		public static SignalBarResult Evaluate(bool isLong, double open, double high, double low, double close, SignalBarSettings s)
		{
			SignalBarResult r = new SignalBarResult();
			double range = high - low;
			if (range <= 0)
			{
				r.BodyPct  = double.NaN;
				r.ClosePct = double.NaN;
				r.WickPct  = double.NaN;
				r.Reason   = "zero range";
				return r;
			}

			r.BodyPct  = Math.Abs(close - open) / range * 100.0;
			r.ClosePct = (isLong ? close - low : high - close) / range * 100.0;
			r.WickPct  = (isLong ? high - close : close - low) / range * 100.0;

			bool directionOk = isLong ? close > open : close < open;
			bool bodyOk      = r.BodyPct >= s.MinBodyPct - Eps;
			bool closeOk     = r.ClosePct >= s.MinClosePct - Eps;
			bool wickOk      = r.WickPct <= s.MaxBreakoutWickPct + Eps;

			if (directionOk && bodyOk && closeOk && wickOk)
			{
				r.Ok     = true;
				r.Reason = "ok";
				return r;
			}

			// ASSUMPTION: the rejection exception ignores the body completely, including its direction.
			if (s.UseRejectionException
				&& r.ClosePct >= s.RejectionMinClosePct - Eps
				&& r.WickPct <= s.RejectionMaxWickPct + Eps)
			{
				r.Ok             = true;
				r.IsRejectionBar = true;
				r.Reason         = "ok (rejection bar)";
				return r;
			}

			if (!directionOk)
				r.Reason = isLong ? "bar not bullish" : "bar not bearish";
			else if (!bodyOk)
				r.Reason = string.Format(CultureInfo.InvariantCulture, "body {0:0}% < {1:0}%", r.BodyPct, s.MinBodyPct);
			else if (!closeOk)
				r.Reason = string.Format(CultureInfo.InvariantCulture, "close {0:0}% < {1:0}%", r.ClosePct, s.MinClosePct);
			else
				r.Reason = string.Format(CultureInfo.InvariantCulture, "wick {0:0}% > {1:0}%", r.WickPct, s.MaxBreakoutWickPct);
			return r;
		}
	}

	public sealed class RiskSettings
	{
		public EntryCountStopMode StopMode;
		public int RiskTemplateTicks;
		public int RiskToleranceTicks;
		public double MaxPullbackPct;
	}

	public struct RiskResult
	{
		public double Trigger;
		public double Stop;
		public double LimitPrice;
		public int RiskTicks;
		public EntryCountRiskClass RiskClass;
	}

	/// <summary>Trigger, structural stop and risk class (compatible with the Sniper strategy).</summary>
	public static class StopRiskCalculator
	{
		public static RiskResult Compute(bool isLong, PaBarSnapshot sig, double tickSize, RiskSettings s)
		{
			RiskResult r = new RiskResult();
			bool usePrev = s.StopMode == EntryCountStopMode.SignalAndPrevBar && sig.HasPrev;
			double structureTicks;

			if (isLong)
			{
				double structLow = usePrev ? Math.Min(sig.Low, sig.PrevLow) : sig.Low;
				r.Trigger      = sig.High + tickSize;
				r.Stop         = structLow - tickSize;
				structureTicks = (sig.High - structLow) / tickSize;
			}
			else
			{
				double structHigh = usePrev ? Math.Max(sig.High, sig.PrevHigh) : sig.High;
				r.Trigger      = sig.Low - tickSize;
				r.Stop         = structHigh + tickSize;
				structureTicks = (structHigh - sig.Low) / tickSize;
			}

			r.RiskTicks = (int)Math.Round(Math.Abs(r.Trigger - r.Stop) / tickSize);
			int maxRisk = Math.Max(1, s.RiskTemplateTicks - 1);

			if (r.RiskTicks <= maxRisk + s.RiskToleranceTicks)
			{
				r.RiskClass = EntryCountRiskClass.Direct;
			}
			else
			{
				double neededTicks = r.RiskTicks - maxRisk;
				if (structureTicks > 0 && neededTicks <= s.MaxPullbackPct / 100.0 * structureTicks + 1e-9)
				{
					r.RiskClass  = EntryCountRiskClass.PullbackLimit;
					r.LimitPrice = isLong ? r.Stop + maxRisk * tickSize : r.Stop - maxRisk * tickSize;
				}
				else
				{
					r.RiskClass = EntryCountRiskClass.TooLarge;
				}
			}
			return r;
		}

		public static string ClassSuffix(EntryCountRiskClass c)
		{
			switch (c)
			{
				case EntryCountRiskClass.PullbackLimit: return " L";
				case EntryCountRiskClass.TooLarge:      return " X";
				default:                                return string.Empty;
			}
		}
	}

	/// <summary>
	/// Vertical label rows around a bar. NinjaTrader text hangs DOWN from its anchor and a positive pixel offset moves it UP.
	/// Row 0 is closest to the bar. Below the low, rows start after a clearance reserved for the bar numbers of
	/// BarNumberToggleIndicator (drawn 15 px below the low).
	/// </summary>
	public static class PaLabelLayout
	{
		public static int RowHeight(int fontSize)
		{
			return (int)Math.Ceiling(fontSize * 1.7);
		}

		public static int Offset(bool belowLow, int row, int rowHeight, int belowClearance)
		{
			return belowLow ? -(belowClearance + row * rowHeight) : (row + 1) * rowHeight;
		}
	}

	/// <summary>
	/// Bar numbers as shown by BarNumberToggleIndicator: start at 1, restart on every new calendar date of the bar time stamp.
	/// </summary>
	public sealed class DayBarNumbering
	{
		private readonly List<int> numbers = new List<int>(8192);

		/// <summary>Call once per bar (CurrentBar ascending). Returns the bar number of the current bar.</summary>
		public int Update(int currentBar, DateTime time0, DateTime time1)
		{
			int number;
			if (currentBar == 0)
				number = 1;
			else if (time0.Date != time1.Date)
				number = 1;
			else
				number = Get(currentBar - 1) + 1;

			if (currentBar < numbers.Count)
				numbers[currentBar] = number;
			else
				numbers.Add(number);
			return number;
		}

		/// <summary>Bar number of an absolute bar index (CurrentBar), 0 if unknown.</summary>
		public int Get(int barIndex)
		{
			return barIndex >= 0 && barIndex < numbers.Count ? numbers[barIndex] : 0;
		}
	}

	/// <summary>Per-bar context for diagnostic lines (all values of the current bar).</summary>
	public struct PaBarContext
	{
		public int BarNo;
		public DateTime BarEnd;
		public DateTime BarStart;
		public double Open;
		public double High;
		public double Low;
		public double Close;
		public double Ema;
		public string BarType;
		public int Trend;
		public bool KeyLevel;
		public int LongCount;
		public bool LongInLeg;
		public int LongCandidateBarNo;
		public int LongOriginBarNo;
		public string LongStateText;     // overrides the Leg/Pullback text when set
		public int ShortCount;
		public bool ShortInLeg;
		public int ShortCandidateBarNo;
		public int ShortOriginBarNo;
		public string ShortStateText;
	}

	/// <summary>
	/// Diagnostic lines for the Output tab and the optional CSV export (one file per trading day,
	/// overwritten on reload, buffered writer, ';' separated, de-DE decimal comma).
	/// </summary>
	public sealed class PaDiagnosticsLog
	{
		public const string Header = "BarNo;BarEndTime;BarStartTime;Indicator;Event;Direction;Count;Open;High;Low;Close;BarType;Trend;KeyLevel;LongCount;LongState;LongOriginBarNo;ShortCount;ShortState;ShortOriginBarNo;SignalBarNo;SignalH;SignalL;SignalO;SignalC;EMA;DistEmaTicks;BodyPct;ClosePct;WickPct;QualityOk;Trigger;Stop;RiskTicks;RiskClass;Reason";

		/// <summary>Current bar context, set by the indicator once per bar before any line is formatted.</summary>
		public PaBarContext Context;

		private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
		private readonly StringBuilder sb = new StringBuilder(320);
		private readonly string indicatorName;
		private readonly string instrumentName;
		private StreamWriter writer;
		private DateTime fileDay = DateTime.MinValue;
		private bool hasUnflushedLines;

		public string LastError { get; private set; }

		public PaDiagnosticsLog(string indicatorName, string instrumentName)
		{
			this.indicatorName = indicatorName;
			StringBuilder name = new StringBuilder(instrumentName ?? "Unknown");
			foreach (char c in Path.GetInvalidFileNameChars())
				name.Replace(c, '_');
			name.Replace(' ', '_');
			this.instrumentName = name.ToString();
		}

		/// <summary>
		/// Event line. quality: -1 = not applicable, 0 = failed, 1 = ok. NaN values and bar numbers &lt;= 0 are written as empty fields.
		/// The count/state columns are only filled in the per-bar line (end-of-bar state).
		/// </summary>
		public string Format(string evt, string direction, int count, int signalBarNo,
			double sigHigh, double sigLow, double sigOpen, double sigClose, double ema, double distEmaTicks,
			double bodyPct, double closePct, double wickPct, int quality, double trigger, double stop,
			int riskTicks, EntryCountRiskClass riskClass, string reason)
		{
			AppendPrefix(evt, direction, count, false);
			AppendInt(signalBarNo);
			AppendNumber(sigHigh, "0.00##");
			AppendNumber(sigLow, "0.00##");
			AppendNumber(sigOpen, "0.00##");
			AppendNumber(sigClose, "0.00##");
			AppendNumber(ema, "0.00##");
			AppendNumber(distEmaTicks, "0.#");
			AppendNumber(bodyPct, "0");
			AppendNumber(closePct, "0");
			AppendNumber(wickPct, "0");
			if (quality >= 0) sb.Append(quality == 1 ? "true" : "false");
			sb.Append(';');
			AppendNumber(trigger, "0.00##");
			AppendNumber(stop, "0.00##");
			if (riskTicks > 0) sb.Append(riskTicks);
			sb.Append(';');
			if (riskClass != EntryCountRiskClass.None) sb.Append(riskClass);
			sb.Append(';');
			if (!string.IsNullOrEmpty(reason)) sb.Append(reason.Replace(';', ','));
			return sb.ToString();
		}

		/// <summary>One line per bar with OHLC, classification and the end-of-bar count state.</summary>
		public string FormatBar()
		{
			AppendPrefix("Bar", string.Empty, 0, true);
			sb.Append(';');                          // SignalBarNo
			sb.Append(";;;;");                       // SignalH, SignalL, SignalO, SignalC
			AppendNumber(Context.Ema, "0.00##");
			sb.Append(";;;;;;;;;");                  // DistEmaTicks .. RiskClass
			return sb.ToString();
		}

		private void AppendPrefix(string evt, string direction, int count, bool withCounts)
		{
			sb.Clear();
			AppendInt(Context.BarNo);
			sb.Append(Context.BarEnd.ToString("dd.MM.yyyy HH:mm:ss", De)).Append(';');
			sb.Append(Context.BarStart.ToString("dd.MM.yyyy HH:mm:ss", De)).Append(';');
			sb.Append(indicatorName).Append(';');
			sb.Append(evt).Append(';');
			sb.Append(direction).Append(';');
			if (count > 0) sb.Append(count);
			sb.Append(';');
			AppendNumber(Context.Open, "0.00##");
			AppendNumber(Context.High, "0.00##");
			AppendNumber(Context.Low, "0.00##");
			AppendNumber(Context.Close, "0.00##");
			sb.Append(Context.BarType).Append(';');
			sb.Append(Context.Trend).Append(';');
			sb.Append(Context.KeyLevel ? "true" : "false").Append(';');
			if (withCounts)
			{
				sb.Append(Context.LongCount).Append(';');
				AppendState(Context.LongStateText, Context.LongInLeg, Context.LongCandidateBarNo);
				AppendInt(Context.LongOriginBarNo);
				sb.Append(Context.ShortCount).Append(';');
				AppendState(Context.ShortStateText, Context.ShortInLeg, Context.ShortCandidateBarNo);
				AppendInt(Context.ShortOriginBarNo);
			}
			else
			{
				sb.Append(";;;;;;");
			}
		}

		private void AppendState(string text, bool inLeg, int candidateBarNo)
		{
			if (text != null)
				sb.Append(text);
			else if (inLeg)
				sb.Append("Leg(signal bar ").Append(candidateBarNo).Append(')');
			else
				sb.Append("Pullback");
			sb.Append(';');
		}

		private void AppendInt(int value)
		{
			if (value > 0)
				sb.Append(value);
			sb.Append(';');
		}

		private void AppendNumber(double value, string format)
		{
			if (!double.IsNaN(value) && !double.IsInfinity(value))
				sb.Append(value.ToString(format, De));
			sb.Append(';');
		}

		public void WriteCsv(DateTime tradingDay, string line)
		{
			if (tradingDay.Date != fileDay)
				Open(tradingDay.Date);
			if (writer == null)
				return;
			writer.WriteLine(line);
			hasUnflushedLines = true;
		}

		private void Open(DateTime day)
		{
			Close();
			fileDay = day;
			string baseName = indicatorName + "_" + instrumentName + "_" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
			for (int attempt = 0; attempt < 5 && writer == null; attempt++)
			{
				string path = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, baseName + (attempt == 0 ? string.Empty : "_" + attempt) + ".csv");
				try
				{
					// FileMode.Create: the file is overwritten on every reload of the indicator.
					FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
					writer = new StreamWriter(stream, new UTF8Encoding(true), 65536);
					writer.WriteLine(Header);
					LastError = null;
				}
				catch (IOException ex)
				{
					// File locked (e.g. open in Excel or used by a second instance): try a suffixed name.
					LastError = ex.Message;
				}
				catch (UnauthorizedAccessException ex)
				{
					LastError = ex.Message;
					break;
				}
			}
		}

		public void Flush()
		{
			if (writer != null && hasUnflushedLines)
			{
				writer.Flush();
				hasUnflushedLines = false;
			}
		}

		public void Close()
		{
			if (writer == null)
				return;
			try
			{
				writer.Flush();
				writer.Dispose();
			}
			catch (IOException)
			{
			}
			writer = null;
			hasUnflushedLines = false;
		}
	}

	#endregion

	public class EntryCount : Indicator
	{
		#region State classes

		/// <summary>Fixed-capacity ring buffer of confirmed swing points (no allocations per bar).</summary>
		private sealed class SwingRing
		{
			public const int Capacity = 32;
			private readonly double[] prices = new double[Capacity];
			private readonly int[] bars = new int[Capacity];
			private int head;
			public int Count;

			public void Add(double price, int bar)
			{
				head = (head + 1) % Capacity;
				prices[head] = price;
				bars[head] = bar;
				if (Count < Capacity)
					Count++;
			}

			/// <summary>i = 0 is the newest swing.</summary>
			public double Price(int i) { return prices[(head - i + Capacity) % Capacity]; }
			public int Bar(int i) { return bars[(head - i + Capacity) % Capacity]; }
			public void Clear() { Count = 0; head = 0; }
		}

		/// <summary>
		/// Counting state machine of one direction.
		/// Short: Pullback(waiting for up leg) -> InLeg(up leg) -> nES trigger -> Pullback -> InLeg -> ... ; F2ES watch after 2ES.
		/// Long is the mirror (leg = down leg, trigger = break of a leg bar high).
		/// </summary>
		private sealed class CountState
		{
			public bool IsLong;
			public int Count;
			public bool InLeg;
			public double OriginPrice;
			public int OriginBar;
			public PaBarSnapshot Candidate;
			public double LegExtreme;          // low of the down leg (long count) / high of the up leg (short count)
			public double PrevLegExtreme;
			public bool HasPrevLeg;

			public bool F2Armed;               // raw event watch, independent of count resets
			public double F2Level;             // 2E signal low (long) / high (short)
			public int F2ArmBar;
			public double F2RunExtreme;        // highest high (long) / lowest low (short) since the 2E trigger
			public int F2RunExtremeBar;

			public bool Paused;                // tuning EC-11/EC-13: count paused until a close on its side of the EMA
			public double F2OriginPrice;       // tuning EC-12: origin of the count the 2E came from
		}

		#endregion

		#region Fields

		private EMA ema;
		private CountState longState;
		private CountState shortState;
		private SwingRing swingHighs;
		private SwingRing swingLows;
		private SignalBarSettings signalSettings;
		private RiskSettings riskSettings;
		private PaDiagnosticsLog diag;
		private SessionIterator sessionIterator;
		private DateTime tradingDay;
		private SimpleFont labelFont;
		private SimpleFont smallFont;
		private int rowHeight;

		private double halfTick;
		private double refHigh;
		private double refLow;
		private PaBarSnapshot currentSnap;
		private DayBarNumbering barNumbering;
		private int currentBarNo;
		private bool currentUp;
		private bool currentDown;
		private bool currentOutsideUpOnly;    // outside bar counted as up only (CloseDirection)
		private bool currentOutsideDownOnly;
		private int currentTrend;

		// Intrabar order from the 1-tick series: which side of the prior bar was broken first.
		private const int TickSeries = 1;
		private bool tickTrackReady;
		private double tickRefHigh;
		private double tickRefLow;
		private long tickSeq;
		private long tickFirstUp;
		private long tickFirstDown;
		private int tickCountInBar;
		private bool currentHighFirst;
		private string currentOrderSource;

		private Series<int> longCount;
		private Series<int> shortCount;
		private Series<int> longEntryEvent;
		private Series<int> shortEntryEvent;
		private Series<bool> failedSecondLong;
		private Series<bool> failedSecondShort;
		private Series<double> longSignalHigh;
		private Series<double> longSignalLow;
		private Series<double> shortSignalHigh;
		private Series<double> shortSignalLow;
		private Series<int> longSignalBar;
		private Series<int> shortSignalBar;
		private Series<bool> longSignalQualityOk;
		private Series<bool> shortSignalQualityOk;
		private Series<double> longStop;
		private Series<double> shortStop;
		private Series<int> longRiskTicks;
		private Series<int> shortRiskTicks;
		private Series<int> longRiskClass;
		private Series<int> shortRiskClass;
		private Series<int> longResetReason;
		private Series<int> shortResetReason;
		private Series<bool> emaTouch;
		private Series<int> trendState;
		private Series<int> lastSecondLongBar;
		private Series<int> lastSecondShortBar;
		private Series<double> longEntryOriginPrice;
		private Series<int> longEntryOriginBar;
		private Series<double> shortEntryOriginPrice;
		private Series<int> shortEntryOriginBar;

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description                 = "Counts price action entries (1E/2E/3E..., long and short) including failed second entries.";
				Name                        = "EntryCount";
				Calculate                   = Calculate.OnBarClose;
				IsOverlay                   = true;
				DisplayInDataBox            = false;
				DrawOnPricePanel            = true;
				PaintPriceMarkers           = false;
				IsSuspendedWhileInactive    = false;
				ScaleJustification          = ScaleJustification.Right;
				BarsRequiredToPlot          = 0;

				// Shared
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

				// Counting / reset
				RequireEqualOrHigherLeg     = false;
				ResetOnKeyLevel             = false;    // tuning CHANGELOG Nr. 6 (C# before: true)
				ResetScope                  = EntryCountResetScope.CounterTrendOnly;
				ResetAfterTwoLegs           = true;
				ResetOnNewExtreme           = true;
				FailedEntryMode             = EntryCountFailedEntryMode.PriorBarBreak;
				InsideBarAsSignal           = true;

				// Tuning (CHANGELOG_Tuning.md; DoubleTrap switches them off for its hosted instance)
				PauseCountAfterCounterTrendF2E = false; // EC-11, Nr. 1/2 (replaced by EC-13)
				F2EWatchUntil2EOrigin       = true;     // EC-12, Nr. 5
				CountOnlyFromEmaSide        = true;     // EC-13, Nr. 7

				// Display
				ShowLabels                  = true;
				MaxLabeledCount             = 3;
				ShowLongCount               = true;
				ShowShortCount              = true;
				ShowFirstEntries            = true;
				ShowFailedEntries           = true;
				ShowCountOrigin             = true;
				ShowQualityColoring         = true;
				ShowRiskTicks               = false;
				LabelFontSize               = 10;
				BarNumberClearance          = 32;
				EnableAlerts                = false;

				// Colors
				LongColor                   = Brushes.DodgerBlue;
				ShortColor                  = Brushes.Crimson;
				QualityFailColor            = Brushes.Gray;
				FailedLongColor             = Brushes.Green;
				FailedShortColor            = Brushes.Red;
				OriginColor                 = Brushes.Goldenrod;

				// Diagnostics
				DebugLog                    = false;
				ExportCsv                   = false;
				CsvIncludeBars              = true;
			}
			else if (State == State.Configure)
			{
				// The structural stop looks one bar beyond the signal bar; counting itself needs no history.
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;

				// 1-tick series: tells which side of the prior bar an outside bar traded first (same-bar F2E).
				// A host (DoubleTrap) must add the same series in its own Configure state.
				AddDataSeries(BarsPeriodType.Tick, 1);
			}
			else if (State == State.DataLoaded)
			{
				ema = EMA(Close, EmaPeriod);

				longState  = new CountState { IsLong = true };
				shortState = new CountState { IsLong = false };
				swingHighs = new SwingRing();
				swingLows  = new SwingRing();
				barNumbering = new DayBarNumbering();

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

				longCount             = new Series<int>(this);
				shortCount            = new Series<int>(this);
				longEntryEvent        = new Series<int>(this);
				shortEntryEvent       = new Series<int>(this);
				failedSecondLong      = new Series<bool>(this);
				failedSecondShort     = new Series<bool>(this);
				longSignalHigh        = new Series<double>(this);
				longSignalLow         = new Series<double>(this);
				shortSignalHigh       = new Series<double>(this);
				shortSignalLow        = new Series<double>(this);
				longSignalBar         = new Series<int>(this);
				shortSignalBar        = new Series<int>(this);
				longSignalQualityOk   = new Series<bool>(this);
				shortSignalQualityOk  = new Series<bool>(this);
				longStop              = new Series<double>(this);
				shortStop             = new Series<double>(this);
				longRiskTicks         = new Series<int>(this);
				shortRiskTicks        = new Series<int>(this);
				longRiskClass         = new Series<int>(this);
				shortRiskClass        = new Series<int>(this);
				longResetReason       = new Series<int>(this);
				shortResetReason      = new Series<int>(this);
				emaTouch              = new Series<bool>(this);
				trendState            = new Series<int>(this);
				lastSecondLongBar     = new Series<int>(this);
				lastSecondShortBar    = new Series<int>(this);
				longEntryOriginPrice  = new Series<double>(this);
				longEntryOriginBar    = new Series<int>(this);
				shortEntryOriginPrice = new Series<double>(this);
				shortEntryOriginBar   = new Series<int>(this);

				halfTick  = TickSize * 0.5;
				labelFont = new SimpleFont("Arial", LabelFontSize) { Bold = true };
				smallFont = new SimpleFont("Arial", Math.Max(7, LabelFontSize - 2));
				rowHeight = PaLabelLayout.RowHeight(LabelFontSize);

				if (DebugLog || ExportCsv)
					diag = new PaDiagnosticsLog("EntryCount", Instrument != null ? Instrument.FullName : "Unknown");
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

		public override string DisplayName
		{
			get { return Name; }
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress == TickSeries)
			{
				TrackIntrabarTick();
				return;
			}
			if (BarsInProgress != 0)
				return;

			ResolveIntrabarOrder();

			if (sessionIterator != null && (CurrentBar == 0 || Bars.IsFirstBarOfSession))
			{
				sessionIterator.GetNextSession(Time[0], true);
				tradingDay = sessionIterator.ActualTradingDayExchange;
			}

			double ema0 = ema[0];
			currentTrend = ComputeTrend(ema0);
			trendState[0] = currentTrend;
			currentBarNo = barNumbering.Update(CurrentBar, Time[0], CurrentBar > 0 ? Time[1] : Time[0]);

			DetectSwings();
			bool keyContact = ComputeKeyLevelContact(ema0, currentTrend);
			emaTouch[0] = keyContact;

			CarryForwardSeries();

			currentSnap = Snapshot(ema0);
			BeginDiagnosticsBar(ema0, keyContact);

			if (CurrentBar == 0)
			{
				InitState(longState);
				InitState(shortState);
				refHigh = High[0];
				refLow  = Low[0];
				WriteCountSeries();
				EndDiagnosticsBar(BarTypeFirst);
				return;
			}

			// Bar classification against the DIRECT previous bar (two inside bars: the second is measured at the first).
			bool inside = High[0] <= High[1] + halfTick && Low[0] >= Low[1] - halfTick;
			bool processed = false;
			string barType = BarTypeInside;
			currentUp = false;
			currentDown = false;
			currentOutsideUpOnly = false;
			currentOutsideDownOnly = false;

			if (!inside)
			{
				// Leg breaks are measured against the last non-inside bar (inside bars are skipped in leg detection).
				bool rawUp   = High[0] > refHigh + halfTick;
				bool rawDown = Low[0] < refLow - halfTick;
				processed = true;
				barType = rawUp ? BarTypeUp : rawDown ? BarTypeDown : BarTypeNoBreak;

				if (rawUp && rawDown)
				{
					// Outside bars: OutsideBarPolicy decides which side counts (default BothSides, user rule: a break is a break).
					switch (OutsideBarPolicy)
					{
						case EntryCountOutsideBarPolicy.CloseDirection:
							bool upSide = Close[0] > Open[0] || (Close[0] == Open[0] && Close[0] >= (High[0] + Low[0]) * 0.5);
							rawUp   = upSide;
							rawDown = !upSide;
							barType = upSide ? BarTypeOutsideUp : BarTypeOutsideDown;
							currentOutsideUpOnly   = upSide;
							currentOutsideDownOnly = !upSide;
							break;
						case EntryCountOutsideBarPolicy.BothSides:
							barType = BarTypeOutsideBoth;
							break;
						case EntryCountOutsideBarPolicy.Ignore:
							processed = false;
							barType = BarTypeOutsideIgnored;
							break;
					}
				}

				if (processed)
				{
					currentUp   = rawUp;
					currentDown = rawDown;
				}
			}
			if (diag != null)
				diag.Context.BarType = barType;

			UpdateF2RunExtreme(longState);
			UpdateF2RunExtreme(shortState);

			EndPause(longState);        // tuning EC-11/EC-13
			EndPause(shortState);

			ApplyKeyLevelReset(longState, keyContact);
			ApplyKeyLevelReset(shortState, keyContact);

			if (processed)
			{
				RunMachine(longState);
				RunMachine(shortState);
				refHigh = High[0];
				refLow  = Low[0];
			}
			else if (inside && InsideBarAsSignal)
			{
				TakeInsideBarAsCandidate(longState);
				TakeInsideBarAsCandidate(shortState);
			}

			if (!inside)
			{
				CheckFailedSecond(longState);
				CheckFailedSecond(shortState);
			}

			ApplyExtremeAndKeyLevelResets(longState, keyContact);
			ApplyExtremeAndKeyLevelResets(shortState, keyContact);

			WriteCountSeries();
			EndDiagnosticsBar(barType);

			if (diag != null && State == State.Realtime)
				diag.Flush();
		}

		#region Intrabar order

		/// <summary>1-tick series: records the first tick beyond the high and beyond the low of the last closed bar.</summary>
		private void TrackIntrabarTick()
		{
			if (!tickTrackReady)
				return;
			tickSeq++;
			tickCountInBar++;
			double price = Closes[TickSeries][0];
			if (tickFirstUp == 0 && price > tickRefHigh + halfTick)
				tickFirstUp = tickSeq;
			if (tickFirstDown == 0 && price < tickRefLow - halfTick)
				tickFirstDown = tickSeq;
		}

		/// <summary>
		/// Decides for the bar that just closed whether its break of the prior high came before its break of the prior low.
		/// Ticks with the closing timestamp of the bar are processed after it, so a side the ticks have not seen yet was
		/// traded last. Without tick data: a red bar makes its high first, a green bar its low; doji: extreme nearer the open.
		/// </summary>
		private void ResolveIntrabarOrder()
		{
			currentHighFirst   = Close[0] < Open[0] || (Close[0] == Open[0] && High[0] - Open[0] <= Open[0] - Low[0]);
			currentOrderSource = "bar color";
			if (CurrentBar > 0 && tickTrackReady && tickCountInBar > 0 && (tickFirstUp > 0 || tickFirstDown > 0))
			{
				currentHighFirst   = tickFirstDown == 0 || (tickFirstUp > 0 && tickFirstUp < tickFirstDown);
				currentOrderSource = "ticks";
			}

			tickRefHigh    = High[0];
			tickRefLow     = Low[0];
			tickFirstUp    = 0;
			tickFirstDown  = 0;
			tickCountInBar = 0;
			tickTrackReady = true;
		}

		#endregion

		#region Diagnostics bar context

		private const string BarTypeFirst          = "First";
		private const string BarTypeInside         = "Inside";
		private const string BarTypeUp             = "Up";
		private const string BarTypeDown           = "Down";
		private const string BarTypeNoBreak        = "NoBreak";
		private const string BarTypeOutsideUp      = "OutsideUp";
		private const string BarTypeOutsideDown    = "OutsideDown";
		private const string BarTypeOutsideBoth    = "OutsideBoth";
		private const string BarTypeOutsideIgnored = "OutsideIgnored";

		private void BeginDiagnosticsBar(double ema0, bool keyContact)
		{
			if (diag == null)
				return;
			diag.Context = new PaBarContext
			{
				BarNo    = currentBarNo,
				BarEnd   = Time[0],
				BarStart = CurrentBar > 0 ? Time[1] : Time[0],
				Open     = Open[0],
				High     = High[0],
				Low      = Low[0],
				Close    = Close[0],
				Ema      = ema0,
				BarType  = BarTypeFirst,
				Trend    = currentTrend,
				KeyLevel = keyContact
			};
		}

		/// <summary>Fills the end-of-bar count state and writes the per-bar CSV line.</summary>
		private void EndDiagnosticsBar(string barType)
		{
			if (diag == null)
				return;
			diag.Context.BarType             = barType;
			diag.Context.LongCount           = longState.Count;
			diag.Context.LongInLeg           = longState.InLeg;
			diag.Context.LongCandidateBarNo  = barNumbering.Get(longState.Candidate.Index);
			diag.Context.LongOriginBarNo     = barNumbering.Get(longState.OriginBar);
			diag.Context.ShortCount          = shortState.Count;
			diag.Context.ShortInLeg          = shortState.InLeg;
			diag.Context.ShortCandidateBarNo = barNumbering.Get(shortState.Candidate.Index);
			diag.Context.ShortOriginBarNo    = barNumbering.Get(shortState.OriginBar);
			if (longState.Paused)
				diag.Context.LongStateText = "Paused";
			if (shortState.Paused)
				diag.Context.ShortStateText = "Paused";

			if (ExportCsv && CsvIncludeBars)
				diag.WriteCsv(tradingDay, diag.FormatBar());
		}

		#endregion

		#region Counting

		private void InitState(CountState c)
		{
			c.Count        = 0;
			c.InLeg        = false;
			c.OriginPrice  = c.IsLong ? High[0] : Low[0];
			c.OriginBar    = CurrentBar;
			c.HasPrevLeg   = false;
			c.F2Armed      = false;
			PauseNewCountBeyondEma(c);      // tuning EC-13
		}

		private bool LegMove(CountState c)    { return c.IsLong ? currentDown : currentUp; }

		private void StartLeg(CountState c)
		{
			c.InLeg      = true;
			c.Candidate  = currentSnap;
			c.LegExtreme = c.IsLong ? Low[0] : High[0];
		}

		private void RunMachine(CountState c)
		{
			if (c.Paused)                   // tuning EC-11/EC-13
				return;
			if (!c.InLeg)
			{
				// Pullback (or waiting at the origin): a bar breaking the reference bar in leg direction starts a new leg.
				if (LegMove(c))
					StartLeg(c);
				return;
			}

			// Trigger = break of the signal bar candidate by at least 1 tick. The candidate is usually the reference bar,
			// but may be an inside bar (InsideBarAsSignal). An outside bar of the reference bar only triggers on the
			// side selected by OutsideBarPolicy.
			bool candidateBreak = c.IsLong ? High[0] > c.Candidate.High + halfTick : Low[0] < c.Candidate.Low - halfTick;
			bool blockedByOutsidePolicy = c.IsLong ? currentOutsideDownOnly : currentOutsideUpOnly;
			if (candidateBreak && !blockedByOutsidePolicy)
			{
				bool legTooShort = RequireEqualOrHigherLeg && c.Count >= 1 && c.HasPrevLeg
					&& (c.IsLong ? c.LegExtreme > c.PrevLegExtreme + halfTick : c.LegExtreme < c.PrevLegExtreme - halfTick);

				c.InLeg = false;
				if (legTooShort)
				{
					// Strict ABC: the leg did not reach the extreme of the prior leg, the pullback simply continues.
					LogEvent("Entry", c.IsLong, c.Count + 1, c.Candidate, true, "skipped: leg did not reach prior leg extreme (RequireEqualOrHigherLeg)");
					return;
				}
				FireEntry(c);
				return;
			}

			// Leg continues: the latest bar is the new signal bar candidate.
			c.Candidate  = currentSnap;
			c.LegExtreme = c.IsLong ? Math.Min(c.LegExtreme, Low[0]) : Math.Max(c.LegExtreme, High[0]);
		}

		/// <summary>
		/// Inside bar during a leg (InsideBarAsSignal, user rule overriding the spec): it becomes the signal bar
		/// candidate. It is never a trigger and is still skipped for leg detection (reference bar unchanged).
		/// </summary>
		private void TakeInsideBarAsCandidate(CountState c)
		{
			if (c.InLeg && !c.Paused)       // tuning EC-11/EC-13: no candidate while paused
				c.Candidate = currentSnap;
		}

		private void FireEntry(CountState c)
		{
			c.Count++;
			c.PrevLegExtreme = c.LegExtreme;
			c.HasPrevLeg     = true;

			PaBarSnapshot sig = c.Candidate;
			SignalBarResult q = SignalBarQuality.Evaluate(c.IsLong, sig.Open, sig.High, sig.Low, sig.Close, signalSettings);
			RiskResult risk   = StopRiskCalculator.Compute(c.IsLong, sig, TickSize, riskSettings);

			if (c.IsLong)
			{
				longEntryEvent[0]       = c.Count;
				longSignalHigh[0]       = sig.High;
				longSignalLow[0]        = sig.Low;
				longSignalBar[0]        = sig.Index;
				longSignalQualityOk[0]  = q.Ok;
				longStop[0]             = risk.Stop;
				longRiskTicks[0]        = risk.RiskTicks;
				longRiskClass[0]        = (int)risk.RiskClass;
				longEntryOriginPrice[0] = c.OriginPrice;
				longEntryOriginBar[0]   = c.OriginBar;
			}
			else
			{
				shortEntryEvent[0]       = c.Count;
				shortSignalHigh[0]       = sig.High;
				shortSignalLow[0]        = sig.Low;
				shortSignalBar[0]        = sig.Index;
				shortSignalQualityOk[0]  = q.Ok;
				shortStop[0]             = risk.Stop;
				shortRiskTicks[0]        = risk.RiskTicks;
				shortRiskClass[0]        = (int)risk.RiskClass;
				shortEntryOriginPrice[0] = c.OriginPrice;
				shortEntryOriginBar[0]   = c.OriginBar;
			}

			if (c.Count == 2)
			{
				c.F2Armed         = true;
				c.F2Level         = c.IsLong ? sig.Low : sig.High;
				c.F2OriginPrice   = c.OriginPrice;      // tuning EC-12
				c.F2ArmBar        = CurrentBar;
				c.F2RunExtreme    = c.IsLong ? High[0] : Low[0];
				c.F2RunExtremeBar = CurrentBar;
				if (c.IsLong)
					lastSecondLongBar[0] = CurrentBar;
				else
					lastSecondShortBar[0] = CurrentBar;
			}
			else if (c.Count > 2)
			{
				// A third entry ends the failed-second-entry watch of the preceding 2E.
				c.F2Armed = false;
			}

			DrawEntryLabel(c.IsLong, c.Count, sig, q.Ok, risk);

			if (diag != null)
				LogEntry(c.IsLong, c.Count, sig, q, risk);

			if (EnableAlerts && State == State.Realtime)
				Alert("EntryCount" + (c.IsLong ? "L" : "S"), Priority.Medium,
					string.Format(CultureInfo.InvariantCulture, "EntryCount: {0}E{1} {2}T", c.Count, c.IsLong ? "L" : "S", risk.RiskTicks),
					NinjaTrader.Core.Globals.InstallDir + @"sounds\Alert2.wav", 5, Brushes.Black, c.IsLong ? LongColor : ShortColor);
		}

		private void UpdateF2RunExtreme(CountState c)
		{
			if (!c.F2Armed)
				return;
			if (c.IsLong ? High[0] > c.F2RunExtreme : Low[0] < c.F2RunExtreme)
			{
				c.F2RunExtreme    = c.IsLong ? High[0] : Low[0];
				c.F2RunExtremeBar = CurrentBar;
			}
		}

		private void CheckFailedSecond(CountState c)
		{
			if (!c.F2Armed || CurrentBar < c.F2ArmBar)
				return;

			// Same-bar F2E (user rule): an outside bar that triggers the 2E and also trades 1 tick beyond the opposite
			// extreme of the signal bar is entry and exit at once, so the 2E fails on its own trigger bar.
			// Only if the entry side was traded first (ResolveIntrabarOrder: tick data, else bar color).
			bool triggerBar = CurrentBar == c.F2ArmBar;
			if (triggerBar && c.IsLong != currentHighFirst)
			{
				if (diag != null && (c.IsLong ? Low[0] < c.F2Level - halfTick : High[0] > c.F2Level + halfTick))
					LogEvent("F2E", c.IsLong, 2, currentSnap, false, string.Format(CultureInfo.InvariantCulture,
						"no same-bar F2E: signal bar {0} {1} traded before the 2E trigger ({2})", c.IsLong ? "low" : "high", c.F2Level, currentOrderSource));
				return;
			}

			// PriorBarBreak: the 2E fails as soon as a later bar trades at least 1 tick beyond the DIRECT prior bar,
			// even if that prior bar is an inside bar (user rule, overrides the spec definition).
			// SignalBarBreak: the 2E fails only when the extreme of the 2E signal bar is traded.
			bool priorBarMode = FailedEntryMode == EntryCountFailedEntryMode.PriorBarBreak && !triggerBar;
			bool failed = priorBarMode
				? (c.IsLong ? Low[0] < Low[1] - halfTick : High[0] > High[1] + halfTick)
				: (c.IsLong ? Low[0] < c.F2Level - halfTick : High[0] > c.F2Level + halfTick);
			if (!failed)
				return;

			c.F2Armed = false;
			if (c.IsLong)
				failedSecondLong[0] = true;
			else
				failedSecondShort[0] = true;

			if (ShowLabels && ShowFailedEntries && (c.IsLong ? ShowLongCount : ShowShortCount))
				Draw.Text(this, (c.IsLong ? "EC_F2L_" : "EC_F2S_") + CurrentBar, false, "F", 0,
					c.IsLong ? Low[0] : High[0], LabelOffset(c.IsLong, 1), c.IsLong ? FailedLongColor : FailedShortColor, labelFont, TextAlignment.Center,
					Brushes.Transparent, Brushes.Transparent, 0);

			if (diag != null)
				LogEvent("F2E", c.IsLong, 2, currentSnap, false, priorBarMode
					? string.Format(CultureInfo.InvariantCulture, "2E{0} failed: prior bar {1} {2} (bar {3}) traded", c.IsLong ? "L" : "S",
						c.IsLong ? "low" : "high", c.IsLong ? Low[1] : High[1], barNumbering.Get(CurrentBar - 1))
					: string.Format(CultureInfo.InvariantCulture, "2E{0} failed{1}: signal bar {2} {3} traded", c.IsLong ? "L" : "S",
						triggerBar ? " on its trigger bar (" + currentOrderSource + ")" : "", c.IsLong ? "low" : "high", c.F2Level));

			if (EnableAlerts && State == State.Realtime)
				Alert("EntryCountF2" + (c.IsLong ? "L" : "S"), Priority.Medium, "EntryCount: " + (c.IsLong ? "F2EL" : "F2ES"),
					NinjaTrader.Core.Globals.InstallDir + @"sounds\Alert2.wav", 5, Brushes.Black, c.IsLong ? FailedLongColor : FailedShortColor);

			// Reset b): the two-legged pullback is complete. New origin = swing extreme formed by the 2E/F2E.
			// ASSUMPTION: "completed 2E/F2E" means the F2E event; a successful 2E keeps counting (3E...).
			if (ResetAfterTwoLegs)
				ResetCount(c, EntryCountResetReason.TwoLegs, c.F2RunExtreme, c.F2RunExtremeBar);

			StartPause(c);                  // tuning EC-11/EC-13
		}

		private void ApplyExtremeAndKeyLevelResets(CountState c, bool keyContact)
		{
			if (c.Paused)                   // tuning EC-11/EC-13: no resets while paused
				return;

			// Reset d) ASSUMPTION: a new extreme beyond the origin moves the origin (the 2E, if any, has succeeded).
			if (ResetOnNewExtreme && (c.IsLong ? High[0] > c.OriginPrice + halfTick : Low[0] < c.OriginPrice - halfTick))
			{
				// tuning EC-12: the F2E watch only ends beyond the origin of the 2E's count, not beyond an origin
				// that a key level reset moved after the 2E
				if (!F2EWatchUntil2EOrigin || (c.IsLong ? High[0] > c.F2OriginPrice + halfTick : Low[0] < c.F2OriginPrice - halfTick))
					c.F2Armed = false;
				ResetCount(c, EntryCountResetReason.NewExtreme, c.IsLong ? High[0] : Low[0], CurrentBar);
			}

			// Reset a)/c) ASSUMPTION: CounterTrendOnly resets only the count against the EMA trend
			// (uptrend: short count), which makes a) and c) identical. Flat EMA: no reset. Both: both counts.
			if (!ResetOnKeyLevel || !keyContact)
				return;

			bool applies = ResetScope == EntryCountResetScope.Both
				|| (c.IsLong ? currentTrend == -1 : currentTrend == 1);
			if (!applies)
				return;

			// The reset on a new contact already ran before the count (ApplyKeyLevelReset).
			if (c.Count == 0 && !c.InLeg && (c.IsLong ? High[0] > c.OriginPrice : Low[0] < c.OriginPrice))
			{
				// Still waiting at the key level: just extend the origin to the extreme of the touch.
				c.OriginPrice = c.IsLong ? High[0] : Low[0];
				c.OriginBar   = CurrentBar;
			}
		}

		/// <summary>
		/// Reset a)/c) on a NEW key level contact, applied BEFORE the bar is counted (user rule, 9.10.26 bars 107-109):
		/// the reset clears the count up to the contact, an entry triggered on the contact bar is the first of the
		/// new count. The running leg is kept, so its signal bar can still trigger on this bar.
		/// </summary>
		private void ApplyKeyLevelReset(CountState c, bool keyContact)
		{
			// ASSUMPTION: CounterTrendOnly resets only the count against the EMA trend
			// (uptrend: short count), which makes a) and c) identical. Flat EMA: no reset. Both: both counts.
			if (!ResetOnKeyLevel || !keyContact || c.Paused)     // tuning EC-11/EC-13: not while paused
				return;

			bool applies = ResetScope == EntryCountResetScope.Both
				|| (c.IsLong ? currentTrend == -1 : currentTrend == 1);
			if (!applies)
				return;

			// Only a NEW contact resets (prior bar without key level contact). A trend change while price
			// already sits at the key level does not reset the count (user rule).
			bool newContact = CurrentBar == 0 || !emaTouch[1];
			if (newContact && (c.Count > 0 || c.InLeg))
				ResetCount(c, EntryCountResetReason.KeyLevel, c.IsLong ? High[0] : Low[0], CurrentBar, true);
		}

		private void ResetCount(CountState c, EntryCountResetReason reason, double originPrice, int originBar, bool keepLeg = false)
		{
			bool meaningful = c.Count > 0 || reason == EntryCountResetReason.TwoLegs;
			int countBefore = c.Count;

			c.Count       = 0;
			c.OriginPrice = originPrice;
			c.OriginBar   = originBar;
			c.HasPrevLeg  = false;

			// The bar that caused the reset may already be the first bar of the new leg.
			if (keepLeg)
			{
				// Reset before counting: the running leg (and its signal bar) stays.
			}
			else if (LegMove(c))
				StartLeg(c);
			else
				c.InLeg = false;

			if (!meaningful)
			{
				if (reason != EntryCountResetReason.TwoLegs)
					PauseNewCountBeyondEma(c);      // tuning EC-13 (after an F2E: StartPause)
				return;
			}

			Series<int> reasonSeries = c.IsLong ? longResetReason : shortResetReason;
			if (reasonSeries[0] == 0)
				reasonSeries[0] = (int)reason;

			if (ShowLabels && ShowCountOrigin && (c.IsLong ? ShowLongCount : ShowShortCount))
			{
				int barsAgo = Math.Max(0, CurrentBar - originBar);
				string tag = (c.IsLong ? "EC_OL_" : "EC_OS_") + CurrentBar;
				Draw.Dot(this, tag, false, barsAgo, originPrice, OriginColor);
				Draw.Text(this, tag + "_T", false, ResetShortText(reason), barsAgo, originPrice, LabelOffset(!c.IsLong, 2),
					OriginColor, smallFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}

			if (diag != null)
				LogEvent("Reset", c.IsLong, countBefore, currentSnap, false,
					string.Format(CultureInfo.InvariantCulture, "{0}; new origin {1} @bar {2}", reason, originPrice, barNumbering.Get(originBar)));
			if (reason != EntryCountResetReason.TwoLegs)
				PauseNewCountBeyondEma(c);          // tuning EC-13 (after an F2E: StartPause)
		}

		#region Tuning EC-11 / EC-13 (CHANGELOG_Tuning.md Nr. 1, 7)

		/// <summary>
		/// After an F2E the count of that direction pauses until a bar closes on its side of the EMA (EndPause):
		/// EC-11 after an F2E against the EMA trend (F2ES in an uptrend, F2EL in a downtrend),
		/// EC-13 after an F2E closing beyond the EMA (F2ES above it, F2EL below it).
		/// </summary>
		private void StartPause(CountState c)
		{
			if (c.Paused)
				return;
			string lsl = c.IsLong ? "L" : "S";
			if (PauseCountAfterCounterTrendF2E && currentTrend == (c.IsLong ? -1 : 1))
				Pause(c, "F2E" + lsl + " against the " + (c.IsLong ? "downtrend" : "uptrend"));
			else if (CountOnlyFromEmaSide && BeyondEma(c))
				Pause(c, "F2E" + lsl + " with close " + (c.IsLong ? "below" : "above") + " the EMA");
		}

		private void Pause(CountState c, string why)
		{
			c.Paused  = true;
			c.InLeg   = false;
			c.F2Armed = false;
			if (diag != null)
				LogEvent("CountPaused", c.IsLong, 0, currentSnap, false,
					why + ": " + (c.IsLong ? "long" : "short") + " count paused until a close " + (c.IsLong ? "above" : "below") + " the EMA");
		}

		/// <summary>First close on its side of the EMA ends the pause: the count restarts like on the first bar (origin = this bar).</summary>
		private void EndPause(CountState c)
		{
			if (!c.Paused)
				return;
			double ema0 = ema[0];
			if (!(c.IsLong ? Close[0] > ema0 : Close[0] < ema0))
				return;
			c.Paused      = false;
			c.Count       = 0;
			c.InLeg       = false;
			c.HasPrevLeg  = false;
			c.OriginPrice = c.IsLong ? High[0] : Low[0];
			c.OriginBar   = CurrentBar;
			if (diag != null)
				LogEvent("CountResumed", c.IsLong, 0, currentSnap, false, string.Format(CultureInfo.InvariantCulture,
					"close {0} {1} EMA {2}; new origin {3} @bar {4}", Close[0], c.IsLong ? "above" : "below", Math.Round(ema0, 4), c.OriginPrice, currentBarNo));
		}

		/// <summary>EC-13: the current bar closes on the wrong side of the EMA for this direction (short: above, long: below).</summary>
		private bool BeyondEma(CountState c)
		{
			return c.IsLong ? Close[0] < ema[0] : Close[0] > ema[0];
		}

		/// <summary>
		/// EC-13: a count only starts on its side of the EMA. A count that would start from 0 on a bar closing beyond
		/// the EMA (first bar, reset d/a/c) pauses until a close back on its side (EndPause). A running count is not
		/// affected; it goes on through the EMA to its F2E (StartPause).
		/// </summary>
		private void PauseNewCountBeyondEma(CountState c)
		{
			if (!CountOnlyFromEmaSide || c.Paused || !BeyondEma(c))
				return;
			Pause(c, "count would start with close " + (c.IsLong ? "below" : "above") + " the EMA");
		}

		#endregion

		private static string ResetShortText(EntryCountResetReason reason)
		{
			switch (reason)
			{
				case EntryCountResetReason.KeyLevel:   return "R:KL";
				case EntryCountResetReason.TwoLegs:    return "R:2L";
				case EntryCountResetReason.NewExtreme: return "R:NX";
				default:                               return "R";
			}
		}

		#endregion

		#region Trend, swings, key levels

		private int ComputeTrend(double ema0)
		{
			if (CurrentBar < EmaSlopeLookback)
				return 0;
			double slopeTicks = (ema0 - ema[EmaSlopeLookback]) / TickSize;
			if (slopeTicks >= MinEmaSlopeTicks - 1e-9)
				return 1;
			if (slopeTicks <= -MinEmaSlopeTicks + 1e-9)
				return -1;
			return 0;
		}

		private void DetectSwings()
		{
			int n = SwingStrength;
			if (CurrentBar < 2 * n)
				return;

			bool isHigh = true;
			bool isLow  = true;
			double h = High[n];
			double l = Low[n];
			for (int k = 1; k <= n && (isHigh || isLow); k++)
			{
				// Left side strict, right side allows equal values (the first of equal extremes is the swing).
				if (!(h > High[n + k] && h >= High[n - k])) isHigh = false;
				if (!(l < Low[n + k] && l <= Low[n - k]))   isLow = false;
			}
			if (isHigh)
				swingHighs.Add(h, CurrentBar - n);
			if (isLow)
				swingLows.Add(l, CurrentBar - n);
		}

		private bool ComputeKeyLevelContact(double ema0, int trend)
		{
			double tol = KeyLevelToleranceTicks * TickSize;
			if (Low[0] - tol <= ema0 && High[0] + tol >= ema0)
				return true;

			if (UseSwingSR)
			{
				double srTol = SrToleranceTicks * TickSize;
				int nh = Math.Min(SrLookbackSwings, swingHighs.Count);
				for (int i = 0; i < nh; i++)
				{
					double p = swingHighs.Price(i);
					if (Low[0] - srTol <= p && High[0] + srTol >= p)
						return true;
				}
				int nl = Math.Min(SrLookbackSwings, swingLows.Count);
				for (int i = 0; i < nl; i++)
				{
					double p = swingLows.Price(i);
					if (Low[0] - srTol <= p && High[0] + srTol >= p)
						return true;
				}
			}

			// ASSUMPTION: trend line = line through the last two confirmed swings in trend direction
			// (uptrend: rising swing lows, downtrend: falling swing highs), projected to the current bar.
			if (UseTrendlines && trend != 0)
			{
				SwingRing ring = trend > 0 ? swingLows : swingHighs;
				if (ring.Count >= 2)
				{
					double p2 = ring.Price(0), p1 = ring.Price(1);
					int b2 = ring.Bar(0), b1 = ring.Bar(1);
					bool valid = b2 > b1 && (trend > 0 ? p2 > p1 : p2 < p1);
					if (valid)
					{
						double line = p1 + (p2 - p1) * (CurrentBar - b1) / (double)(b2 - b1);
						if (Low[0] - tol <= line && High[0] + tol >= line)
							return true;
					}
				}
			}
			return false;
		}

		#endregion

		#region Series helpers

		private PaBarSnapshot Snapshot(double ema0)
		{
			PaBarSnapshot s = new PaBarSnapshot();
			s.Index   = CurrentBar;
			s.Open    = Open[0];
			s.High    = High[0];
			s.Low     = Low[0];
			s.Close   = Close[0];
			s.Ema     = ema0;
			s.HasPrev = CurrentBar > 0;
			s.PrevHigh = s.HasPrev ? High[1] : High[0];
			s.PrevLow  = s.HasPrev ? Low[1] : Low[0];
			return s;
		}

		private void CarryForwardSeries()
		{
			longEntryEvent[0]    = 0;
			shortEntryEvent[0]   = 0;
			failedSecondLong[0]  = false;
			failedSecondShort[0] = false;
			longResetReason[0]   = 0;
			shortResetReason[0]  = 0;

			if (CurrentBar == 0)
			{
				longSignalHigh[0]        = 0;
				longSignalLow[0]         = 0;
				shortSignalHigh[0]       = 0;
				shortSignalLow[0]        = 0;
				longSignalBar[0]         = -1;
				shortSignalBar[0]        = -1;
				longSignalQualityOk[0]   = false;
				shortSignalQualityOk[0]  = false;
				longStop[0]              = 0;
				shortStop[0]             = 0;
				longRiskTicks[0]         = 0;
				shortRiskTicks[0]        = 0;
				longRiskClass[0]         = 0;
				shortRiskClass[0]        = 0;
				lastSecondLongBar[0]     = -1;
				lastSecondShortBar[0]    = -1;
				longEntryOriginPrice[0]  = 0;
				longEntryOriginBar[0]    = -1;
				shortEntryOriginPrice[0] = 0;
				shortEntryOriginBar[0]   = -1;
				return;
			}

			longSignalHigh[0]        = longSignalHigh[1];
			longSignalLow[0]         = longSignalLow[1];
			shortSignalHigh[0]       = shortSignalHigh[1];
			shortSignalLow[0]        = shortSignalLow[1];
			longSignalBar[0]         = longSignalBar[1];
			shortSignalBar[0]        = shortSignalBar[1];
			longSignalQualityOk[0]   = longSignalQualityOk[1];
			shortSignalQualityOk[0]  = shortSignalQualityOk[1];
			longStop[0]              = longStop[1];
			shortStop[0]             = shortStop[1];
			longRiskTicks[0]         = longRiskTicks[1];
			shortRiskTicks[0]        = shortRiskTicks[1];
			longRiskClass[0]         = longRiskClass[1];
			shortRiskClass[0]        = shortRiskClass[1];
			lastSecondLongBar[0]     = lastSecondLongBar[1];
			lastSecondShortBar[0]    = lastSecondShortBar[1];
			longEntryOriginPrice[0]  = longEntryOriginPrice[1];
			longEntryOriginBar[0]    = longEntryOriginBar[1];
			shortEntryOriginPrice[0] = shortEntryOriginPrice[1];
			shortEntryOriginBar[0]   = shortEntryOriginBar[1];
		}

		private void WriteCountSeries()
		{
			longCount[0]  = longState.Count;
			shortCount[0] = shortState.Count;
		}

		#endregion

		#region Drawing and diagnostics

		private int LabelOffset(bool belowLow, int row)
		{
			return PaLabelLayout.Offset(belowLow, row, rowHeight, BarNumberClearance);
		}

		private void DrawEntryLabel(bool isLong, int count, PaBarSnapshot sig, bool qualityOk, RiskResult risk)
		{
			if (!ShowLabels || count > MaxLabeledCount || (count == 1 && !ShowFirstEntries) || !(isLong ? ShowLongCount : ShowShortCount))
				return;

			string text = count.ToString(CultureInfo.InvariantCulture) + (isLong ? "EL" : "ES");
			if (ShowRiskTicks)
				text += " " + risk.RiskTicks.ToString(CultureInfo.InvariantCulture) + "T" + StopRiskCalculator.ClassSuffix(risk.RiskClass);

			Brush brush = ShowQualityColoring && !qualityOk ? QualityFailColor : (isLong ? LongColor : ShortColor);
			int barsAgo = Math.Max(0, CurrentBar - sig.Index);
			Draw.Text(this, (isLong ? "EC_L_" : "EC_S_") + CurrentBar, false, text, barsAgo,
				isLong ? sig.Low : sig.High, LabelOffset(isLong, 0), brush, labelFont, TextAlignment.Center,
				Brushes.Transparent, Brushes.Transparent, 0);
		}

		private void LogEntry(bool isLong, int count, PaBarSnapshot sig, SignalBarResult q, RiskResult risk)
		{
			double dist = (isLong ? sig.Low - sig.Ema : sig.Ema - sig.High) / TickSize;
			string line = diag.Format("Entry", isLong ? "Long" : "Short", count, barNumbering.Get(sig.Index),
				sig.High, sig.Low, sig.Open, sig.Close, sig.Ema, dist, q.BodyPct, q.ClosePct, q.WickPct, q.Ok ? 1 : 0,
				risk.Trigger, risk.Stop, risk.RiskTicks, risk.RiskClass, "quality " + q.Reason);
			Emit(line);
		}

		private void LogEvent(string evt, bool isLong, int count, PaBarSnapshot bar, bool withSignal, string reason)
		{
			if (diag == null)
				return;
			string line;
			if (withSignal)
			{
				double dist = (isLong ? bar.Low - bar.Ema : bar.Ema - bar.High) / TickSize;
				line = diag.Format(evt, isLong ? "Long" : "Short", count, barNumbering.Get(bar.Index),
					bar.High, bar.Low, bar.Open, bar.Close, bar.Ema, dist, double.NaN, double.NaN, double.NaN, -1,
					double.NaN, double.NaN, 0, EntryCountRiskClass.None, reason);
			}
			else
			{
				line = diag.Format(evt, isLong ? "Long" : "Short", count, 0,
					double.NaN, double.NaN, double.NaN, double.NaN, bar.Ema, double.NaN, double.NaN, double.NaN, double.NaN, -1,
					double.NaN, double.NaN, 0, EntryCountRiskClass.None, reason);
			}
			Emit(line);
		}

		private void Emit(string line)
		{
			if (DebugLog)
				Print(line);
			if (ExportCsv)
			{
				diag.WriteCsv(tradingDay, line);
				if (diag.LastError != null && DebugLog)
					Print("EntryCount CSV: " + diag.LastError);
			}
		}

		#endregion

		#region Public series

		[Browsable(false)] [XmlIgnore] public Series<int> LongCount { get { Update(); return longCount; } }
		[Browsable(false)] [XmlIgnore] public Series<int> ShortCount { get { Update(); return shortCount; } }
		[Browsable(false)] [XmlIgnore] public Series<int> LongEntryEvent { get { Update(); return longEntryEvent; } }
		[Browsable(false)] [XmlIgnore] public Series<int> ShortEntryEvent { get { Update(); return shortEntryEvent; } }
		[Browsable(false)] [XmlIgnore] public Series<bool> FailedSecondLong { get { Update(); return failedSecondLong; } }
		[Browsable(false)] [XmlIgnore] public Series<bool> FailedSecondShort { get { Update(); return failedSecondShort; } }
		[Browsable(false)] [XmlIgnore] public Series<double> LongSignalHigh { get { Update(); return longSignalHigh; } }
		[Browsable(false)] [XmlIgnore] public Series<double> LongSignalLow { get { Update(); return longSignalLow; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ShortSignalHigh { get { Update(); return shortSignalHigh; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ShortSignalLow { get { Update(); return shortSignalLow; } }
		/// <summary>CurrentBar index of the signal bar of the last long entry (-1 = none).</summary>
		[Browsable(false)] [XmlIgnore] public Series<int> LongSignalBar { get { Update(); return longSignalBar; } }
		[Browsable(false)] [XmlIgnore] public Series<int> ShortSignalBar { get { Update(); return shortSignalBar; } }
		[Browsable(false)] [XmlIgnore] public Series<bool> LongSignalQualityOk { get { Update(); return longSignalQualityOk; } }
		[Browsable(false)] [XmlIgnore] public Series<bool> ShortSignalQualityOk { get { Update(); return shortSignalQualityOk; } }
		[Browsable(false)] [XmlIgnore] public Series<double> LongStop { get { Update(); return longStop; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ShortStop { get { Update(); return shortStop; } }
		[Browsable(false)] [XmlIgnore] public Series<int> LongRiskTicks { get { Update(); return longRiskTicks; } }
		[Browsable(false)] [XmlIgnore] public Series<int> ShortRiskTicks { get { Update(); return shortRiskTicks; } }
		/// <summary>EntryCountRiskClass as int.</summary>
		[Browsable(false)] [XmlIgnore] public Series<int> LongRiskClass { get { Update(); return longRiskClass; } }
		[Browsable(false)] [XmlIgnore] public Series<int> ShortRiskClass { get { Update(); return shortRiskClass; } }
		/// <summary>EntryCountResetReason as int, 0 = no reset on this bar.</summary>
		[Browsable(false)] [XmlIgnore] public Series<int> LongResetReason { get { Update(); return longResetReason; } }
		[Browsable(false)] [XmlIgnore] public Series<int> ShortResetReason { get { Update(); return shortResetReason; } }
		/// <summary>Key level contact (EMA, optional S/R and trend line) on this bar.</summary>
		[Browsable(false)] [XmlIgnore] public Series<bool> EmaTouch { get { Update(); return emaTouch; } }
		/// <summary>+1 up, 0 flat, -1 down (EMA slope).</summary>
		[Browsable(false)] [XmlIgnore] public Series<int> TrendState { get { Update(); return trendState; } }
		/// <summary>Raw event log independent of resets: CurrentBar of the last 2EL trigger (-1 = none).</summary>
		[Browsable(false)] [XmlIgnore] public Series<int> LastSecondLongBar { get { Update(); return lastSecondLongBar; } }
		[Browsable(false)] [XmlIgnore] public Series<int> LastSecondShortBar { get { Update(); return lastSecondShortBar; } }
		/// <summary>Origin (start of the pullback) of the count in which the last long entry fired.</summary>
		[Browsable(false)] [XmlIgnore] public Series<double> LongEntryOriginPrice { get { Update(); return longEntryOriginPrice; } }
		[Browsable(false)] [XmlIgnore] public Series<int> LongEntryOriginBar { get { Update(); return longEntryOriginBar; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ShortEntryOriginPrice { get { Update(); return shortEntryOriginPrice; } }
		[Browsable(false)] [XmlIgnore] public Series<int> ShortEntryOriginBar { get { Update(); return shortEntryOriginBar; } }

		#endregion

		#region Properties

		// 1. Shared
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA period", Description = "EMA(Close) period in bars.", Order = 1, GroupName = "1. Shared")]
		public int EmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Key level tolerance (ticks)", Description = "Max distance in ticks between bar and EMA/trend line that still counts as key level contact.", Order = 2, GroupName = "1. Shared")]
		public int KeyLevelToleranceTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Swing strength (bars)", Description = "Bars on each side needed to confirm a swing high/low (S/R, trend lines, two-leg visual).", Order = 3, GroupName = "1. Shared")]
		public int SwingStrength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use swing S/R", Description = "Confirmed swing highs/lows count as key levels for reset a).", Order = 4, GroupName = "1. Shared")]
		public bool UseSwingSR { get; set; }

		[NinjaScriptProperty]
		[Range(1, 30)]
		[Display(Name = "S/R lookback (swings)", Description = "Number of most recent confirmed swing highs and lows used as S/R levels.", Order = 5, GroupName = "1. Shared")]
		public int SrLookbackSwings { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "S/R tolerance (ticks)", Description = "Max distance in ticks between bar and an S/R level that still counts as contact.", Order = 6, GroupName = "1. Shared")]
		public int SrToleranceTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use trend lines", Description = "ASSUMPTION: line through the last two confirmed swings in trend direction counts as key level.", Order = 7, GroupName = "1. Shared")]
		public bool UseTrendlines { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Outside bar policy", Description = "Which side of an outside bar counts: BothSides (default: high and low break both count, e.g. a sell stop below the signal bar is filled), CloseDirection (only the side of the close), or Ignore (skipped like an inside bar).", Order = 8, GroupName = "1. Shared")]
		public EntryCountOutsideBarPolicy OutsideBarPolicy { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA slope lookback (bars)", Description = "Trend slope = EMA[0] - EMA[lookback], in bars.", Order = 9, GroupName = "1. Shared")]
		public int EmaSlopeLookback { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Min EMA slope (ticks)", Description = "Slope in ticks over the lookback needed for up (>=) or down (<= -value) trend; otherwise flat.", Order = 10, GroupName = "1. Shared")]
		public double MinEmaSlopeTicks { get; set; }

		// 2. Signal bar (Rule 5)
		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Min body (%)", Description = "Minimum body in % of the bar range.", Order = 1, GroupName = "2. Signal bar (Rule 5)")]
		public double MinBodyPct { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Min close (%)", Description = "Close position in % of the range, measured from the low (long) or high (short).", Order = 2, GroupName = "2. Signal bar (Rule 5)")]
		public double MinClosePct { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Max breakout wick (%)", Description = "Maximum wick on the breakout side (upper wick long, lower wick short) in % of the range.", Order = 3, GroupName = "2. Signal bar (Rule 5)")]
		public double MaxBreakoutWickPct { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use rejection exception", Description = "A bar with a very strong close and tiny breakout wick is valid regardless of its body.", Order = 4, GroupName = "2. Signal bar (Rule 5)")]
		public bool UseRejectionException { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Rejection min close (%)", Description = "Close position in % of the range required for the rejection exception.", Order = 5, GroupName = "2. Signal bar (Rule 5)")]
		public double RejectionMinClosePct { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Rejection max wick (%)", Description = "Maximum breakout wick in % of the range for the rejection exception.", Order = 6, GroupName = "2. Signal bar (Rule 5)")]
		public double RejectionMaxWickPct { get; set; }

		// 3. Stop / risk
		[NinjaScriptProperty]
		[Display(Name = "Stop mode", Description = "SignalAndPrevBar: stop beyond the extreme of signal bar and the bar before it. SignalBarOnly: beyond the signal bar.", Order = 1, GroupName = "3. Stop / risk")]
		public EntryCountStopMode StopMode { get; set; }

		[NinjaScriptProperty]
		[Range(2, int.MaxValue)]
		[Display(Name = "Risk template (ticks)", Description = "ATM template size in ticks; max risk = template - 1 tick.", Order = 2, GroupName = "3. Stop / risk")]
		public int RiskTemplateTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Risk tolerance (ticks)", Description = "Ticks the risk may exceed max risk and still count as Direct.", Order = 3, GroupName = "3. Stop / risk")]
		public int RiskToleranceTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Max pullback (%)", Description = "Max retracement in % of the signal structure for a PullbackLimit entry; beyond that the risk is TooLarge.", Order = 4, GroupName = "3. Stop / risk")]
		public double MaxPullbackPct { get; set; }

		// 4. Counting / reset
		[NinjaScriptProperty]
		[Display(Name = "Require equal or higher leg", Description = "Strict ABC: the second leg must reach or exceed the extreme of the first leg to count.", Order = 1, GroupName = "4. Counting / reset")]
		public bool RequireEqualOrHigherLeg { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Reset on key level (a, c)", Description = "Pullback to a key level (EMA, optional S/R, trend line) resets the count(s) selected by Reset scope.", Order = 2, GroupName = "4. Counting / reset")]
		public bool ResetOnKeyLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Reset scope", Description = "ASSUMPTION: CounterTrendOnly resets only the count against the EMA trend (flat: none). Both resets both counts.", Order = 3, GroupName = "4. Counting / reset")]
		public EntryCountResetScope ResetScope { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Reset after two legs (b)", Description = "After an F2E the count resets at the swing extreme formed by the 2E/F2E, without waiting for a new extreme.", Order = 4, GroupName = "4. Counting / reset")]
		public bool ResetAfterTwoLegs { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Reset on new extreme (d)", Description = "ASSUMPTION: a new extreme beyond the origin moves the origin and restarts the count.", Order = 5, GroupName = "4. Counting / reset")]
		public bool ResetOnNewExtreme { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Failed entry mode", Description = "PriorBarBreak: a 2E fails as soon as a later bar trades 1 tick beyond the direct prior bar (also if that is an inside bar). SignalBarBreak: only when the 2E signal bar extreme is traded.", Order = 6, GroupName = "4. Counting / reset")]
		public EntryCountFailedEntryMode FailedEntryMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Inside bar as signal bar", Description = "An inside bar during a leg becomes the signal bar; a later bar breaking its high (long) / low (short) by 1 tick triggers the entry. Inside bars are still skipped for leg detection and are never triggers.", Order = 7, GroupName = "4. Counting / reset")]
		public bool InsideBarAsSignal { get; set; }

		[Display(Name = "Pause counter-trend count after F2E (EC-11)", Description = "Tuning EC-11: an F2ES in an uptrend pauses the short count until a close below the EMA (F2EL in a downtrend: long count until a close above the EMA). Replaced by EC-13.", Order = 8, GroupName = "4. Counting / reset")]
		public bool PauseCountAfterCounterTrendF2E { get; set; }

		[Display(Name = "F2E watch until 2E origin (EC-12)", Description = "Tuning EC-12: a new extreme (reset d) ends the F2E watch only beyond the origin of the count the 2E came from, not beyond an origin a key level reset moved after the 2E.", Order = 9, GroupName = "4. Counting / reset")]
		public bool F2EWatchUntil2EOrigin { get; set; }

		[Display(Name = "Count only from EMA side (EC-13)", Description = "Tuning EC-13: a short count starts only with a close below the EMA (long: above). A short count that started below the EMA runs on above it until its F2ES; an F2ES closing above the EMA pauses it until a close below the EMA (long mirrored).", Order = 10, GroupName = "4. Counting / reset")]
		public bool CountOnlyFromEmaSide { get; set; }

		// 5. Display
		[NinjaScriptProperty]
		[Display(Name = "Show labels (master)", Description = "Master switch for all chart output (false = invisible, e.g. when hosted by DoubleTrap).", Order = 1, GroupName = "5. Display")]
		public bool ShowLabels { get; set; }

		[Range(1, 99)]
		[Display(Name = "Max labeled count", Description = "Entries up to this number (1E, 2E, ...) get a label.", Order = 2, GroupName = "5. Display")]
		public int MaxLabeledCount { get; set; }

		[Display(Name = "Show long count", Description = "Labels 1EL/2EL/... below the signal bar.", Order = 3, GroupName = "5. Display")]
		public bool ShowLongCount { get; set; }

		[Display(Name = "Show short count", Description = "Labels 1ES/2ES/... above the signal bar.", Order = 4, GroupName = "5. Display")]
		public bool ShowShortCount { get; set; }

		[Display(Name = "Show first entries", Description = "Show the 1EL/1ES labels. Counting is not affected.", Order = 5, GroupName = "5. Display")]
		public bool ShowFirstEntries { get; set; }

		[Display(Name = "Show failed entries", Description = "Green 'F' (F2EL) below / red 'F' (F2ES) above the triggering bar.", Order = 6, GroupName = "5. Display")]
		public bool ShowFailedEntries { get; set; }

		[Display(Name = "Show count origin", Description = "Dot at the new count origin plus short reset reason (R:KL key level, R:2L two legs, R:NX new extreme).", Order = 7, GroupName = "5. Display")]
		public bool ShowCountOrigin { get; set; }

		[Display(Name = "Show quality coloring", Description = "Label in quality fail color if the signal bar fails Rule 5.", Order = 8, GroupName = "5. Display")]
		public bool ShowQualityColoring { get; set; }

		[Display(Name = "Show risk ticks", Description = "Risk in ticks next to the label, e.g. '2ES 15T', '21T L' = PullbackLimit, '30T X' = TooLarge.", Order = 9, GroupName = "5. Display")]
		public bool ShowRiskTicks { get; set; }

		[Range(6, 30)]
		[Display(Name = "Label font size", Description = "Font size of the labels in points.", Order = 10, GroupName = "5. Display")]
		public int LabelFontSize { get; set; }

		[Range(0, 200)]
		[Display(Name = "Bar number clearance (px)", Description = "Free space in pixels below the low before the first label row, so labels do not cover the bar numbers of BarNumberToggleIndicator. 0 = labels directly below the bar.", Order = 12, GroupName = "5. Display")]
		public int BarNumberClearance { get; set; }

		[Display(Name = "Enable alerts", Description = "Alert with sound on new entries and F2E (realtime only).", Order = 11, GroupName = "5. Display")]
		public bool EnableAlerts { get; set; }

		// 6. Colors
		[XmlIgnore]
		[Display(Name = "Long color", Description = "Color of long labels.", Order = 1, GroupName = "6. Colors")]
		public Brush LongColor { get; set; }

		[Browsable(false)]
		public string LongColorSerialize
		{
			get { return Serialize.BrushToString(LongColor); }
			set { LongColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Short color", Description = "Color of short labels.", Order = 2, GroupName = "6. Colors")]
		public Brush ShortColor { get; set; }

		[Browsable(false)]
		public string ShortColorSerialize
		{
			get { return Serialize.BrushToString(ShortColor); }
			set { ShortColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Quality fail color", Description = "Label color when the signal bar fails Rule 5.", Order = 3, GroupName = "6. Colors")]
		public Brush QualityFailColor { get; set; }

		[Browsable(false)]
		public string QualityFailColorSerialize
		{
			get { return Serialize.BrushToString(QualityFailColor); }
			set { QualityFailColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Failed long color", Description = "Color of the 'F' marker of a failed second long entry (F2EL).", Order = 4, GroupName = "6. Colors")]
		public Brush FailedLongColor { get; set; }

		[Browsable(false)]
		public string FailedLongColorSerialize
		{
			get { return Serialize.BrushToString(FailedLongColor); }
			set { FailedLongColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Failed short color", Description = "Color of the 'F' marker of a failed second short entry (F2ES).", Order = 5, GroupName = "6. Colors")]
		public Brush FailedShortColor { get; set; }

		[Browsable(false)]
		public string FailedShortColorSerialize
		{
			get { return Serialize.BrushToString(FailedShortColor); }
			set { FailedShortColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Origin color", Description = "Color of the count origin dot and reset text.", Order = 6, GroupName = "6. Colors")]
		public Brush OriginColor { get; set; }

		[Browsable(false)]
		public string OriginColorSerialize
		{
			get { return Serialize.BrushToString(OriginColor); }
			set { OriginColor = Serialize.StringToBrush(value); }
		}

		// 7. Diagnostics
		[Display(Name = "Debug log", Description = "Print one ';'-separated line per event (Entry, F2E, Reset) to the Output tab.", Order = 1, GroupName = "7. Diagnostics")]
		public bool DebugLog { get; set; }

		[Display(Name = "Export CSV", Description = "Write the same lines to Documents\\NinjaTrader 8\\EntryCount_<Instrument>_<yyyyMMdd>.csv (one file per trading day, overwritten on reload).", Order = 2, GroupName = "7. Diagnostics")]
		public bool ExportCsv { get; set; }

		[Display(Name = "CSV: one line per bar", Description = "Adds a 'Bar' line for every bar to the CSV (bar number starting at 1 per day, OHLC, bar type, trend, key level, count and state of both counts). Not printed to the Output tab.", Order = 3, GroupName = "7. Diagnostics")]
		public bool CsvIncludeBars { get; set; }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private EntryCount[] cacheEntryCount;
		public EntryCount EntryCount(int emaPeriod, int keyLevelToleranceTicks, int swingStrength, bool useSwingSR, int srLookbackSwings, int srToleranceTicks, bool useTrendlines, EntryCountOutsideBarPolicy outsideBarPolicy, int emaSlopeLookback, double minEmaSlopeTicks, double minBodyPct, double minClosePct, double maxBreakoutWickPct, bool useRejectionException, double rejectionMinClosePct, double rejectionMaxWickPct, EntryCountStopMode stopMode, int riskTemplateTicks, int riskToleranceTicks, double maxPullbackPct, bool requireEqualOrHigherLeg, bool resetOnKeyLevel, EntryCountResetScope resetScope, bool resetAfterTwoLegs, bool resetOnNewExtreme, EntryCountFailedEntryMode failedEntryMode, bool insideBarAsSignal, bool showLabels)
		{
			return EntryCount(Input, emaPeriod, keyLevelToleranceTicks, swingStrength, useSwingSR, srLookbackSwings, srToleranceTicks, useTrendlines, outsideBarPolicy, emaSlopeLookback, minEmaSlopeTicks, minBodyPct, minClosePct, maxBreakoutWickPct, useRejectionException, rejectionMinClosePct, rejectionMaxWickPct, stopMode, riskTemplateTicks, riskToleranceTicks, maxPullbackPct, requireEqualOrHigherLeg, resetOnKeyLevel, resetScope, resetAfterTwoLegs, resetOnNewExtreme, failedEntryMode, insideBarAsSignal, showLabels);
		}

		public EntryCount EntryCount(ISeries<double> input, int emaPeriod, int keyLevelToleranceTicks, int swingStrength, bool useSwingSR, int srLookbackSwings, int srToleranceTicks, bool useTrendlines, EntryCountOutsideBarPolicy outsideBarPolicy, int emaSlopeLookback, double minEmaSlopeTicks, double minBodyPct, double minClosePct, double maxBreakoutWickPct, bool useRejectionException, double rejectionMinClosePct, double rejectionMaxWickPct, EntryCountStopMode stopMode, int riskTemplateTicks, int riskToleranceTicks, double maxPullbackPct, bool requireEqualOrHigherLeg, bool resetOnKeyLevel, EntryCountResetScope resetScope, bool resetAfterTwoLegs, bool resetOnNewExtreme, EntryCountFailedEntryMode failedEntryMode, bool insideBarAsSignal, bool showLabels)
		{
			if (cacheEntryCount != null)
				for (int idx = 0; idx < cacheEntryCount.Length; idx++)
					if (cacheEntryCount[idx] != null && cacheEntryCount[idx].EmaPeriod == emaPeriod && cacheEntryCount[idx].KeyLevelToleranceTicks == keyLevelToleranceTicks && cacheEntryCount[idx].SwingStrength == swingStrength && cacheEntryCount[idx].UseSwingSR == useSwingSR && cacheEntryCount[idx].SrLookbackSwings == srLookbackSwings && cacheEntryCount[idx].SrToleranceTicks == srToleranceTicks && cacheEntryCount[idx].UseTrendlines == useTrendlines && cacheEntryCount[idx].OutsideBarPolicy == outsideBarPolicy && cacheEntryCount[idx].EmaSlopeLookback == emaSlopeLookback && cacheEntryCount[idx].MinEmaSlopeTicks == minEmaSlopeTicks && cacheEntryCount[idx].MinBodyPct == minBodyPct && cacheEntryCount[idx].MinClosePct == minClosePct && cacheEntryCount[idx].MaxBreakoutWickPct == maxBreakoutWickPct && cacheEntryCount[idx].UseRejectionException == useRejectionException && cacheEntryCount[idx].RejectionMinClosePct == rejectionMinClosePct && cacheEntryCount[idx].RejectionMaxWickPct == rejectionMaxWickPct && cacheEntryCount[idx].StopMode == stopMode && cacheEntryCount[idx].RiskTemplateTicks == riskTemplateTicks && cacheEntryCount[idx].RiskToleranceTicks == riskToleranceTicks && cacheEntryCount[idx].MaxPullbackPct == maxPullbackPct && cacheEntryCount[idx].RequireEqualOrHigherLeg == requireEqualOrHigherLeg && cacheEntryCount[idx].ResetOnKeyLevel == resetOnKeyLevel && cacheEntryCount[idx].ResetScope == resetScope && cacheEntryCount[idx].ResetAfterTwoLegs == resetAfterTwoLegs && cacheEntryCount[idx].ResetOnNewExtreme == resetOnNewExtreme && cacheEntryCount[idx].FailedEntryMode == failedEntryMode && cacheEntryCount[idx].InsideBarAsSignal == insideBarAsSignal && cacheEntryCount[idx].ShowLabels == showLabels && cacheEntryCount[idx].EqualsInput(input))
						return cacheEntryCount[idx];
			return CacheIndicator<EntryCount>(new EntryCount(){ EmaPeriod = emaPeriod, KeyLevelToleranceTicks = keyLevelToleranceTicks, SwingStrength = swingStrength, UseSwingSR = useSwingSR, SrLookbackSwings = srLookbackSwings, SrToleranceTicks = srToleranceTicks, UseTrendlines = useTrendlines, OutsideBarPolicy = outsideBarPolicy, EmaSlopeLookback = emaSlopeLookback, MinEmaSlopeTicks = minEmaSlopeTicks, MinBodyPct = minBodyPct, MinClosePct = minClosePct, MaxBreakoutWickPct = maxBreakoutWickPct, UseRejectionException = useRejectionException, RejectionMinClosePct = rejectionMinClosePct, RejectionMaxWickPct = rejectionMaxWickPct, StopMode = stopMode, RiskTemplateTicks = riskTemplateTicks, RiskToleranceTicks = riskToleranceTicks, MaxPullbackPct = maxPullbackPct, RequireEqualOrHigherLeg = requireEqualOrHigherLeg, ResetOnKeyLevel = resetOnKeyLevel, ResetScope = resetScope, ResetAfterTwoLegs = resetAfterTwoLegs, ResetOnNewExtreme = resetOnNewExtreme, FailedEntryMode = failedEntryMode, InsideBarAsSignal = insideBarAsSignal, ShowLabels = showLabels }, input, ref cacheEntryCount);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EntryCount EntryCount(int emaPeriod, int keyLevelToleranceTicks, int swingStrength, bool useSwingSR, int srLookbackSwings, int srToleranceTicks, bool useTrendlines, EntryCountOutsideBarPolicy outsideBarPolicy, int emaSlopeLookback, double minEmaSlopeTicks, double minBodyPct, double minClosePct, double maxBreakoutWickPct, bool useRejectionException, double rejectionMinClosePct, double rejectionMaxWickPct, EntryCountStopMode stopMode, int riskTemplateTicks, int riskToleranceTicks, double maxPullbackPct, bool requireEqualOrHigherLeg, bool resetOnKeyLevel, EntryCountResetScope resetScope, bool resetAfterTwoLegs, bool resetOnNewExtreme, EntryCountFailedEntryMode failedEntryMode, bool insideBarAsSignal, bool showLabels)
		{
			return indicator.EntryCount(Input, emaPeriod, keyLevelToleranceTicks, swingStrength, useSwingSR, srLookbackSwings, srToleranceTicks, useTrendlines, outsideBarPolicy, emaSlopeLookback, minEmaSlopeTicks, minBodyPct, minClosePct, maxBreakoutWickPct, useRejectionException, rejectionMinClosePct, rejectionMaxWickPct, stopMode, riskTemplateTicks, riskToleranceTicks, maxPullbackPct, requireEqualOrHigherLeg, resetOnKeyLevel, resetScope, resetAfterTwoLegs, resetOnNewExtreme, failedEntryMode, insideBarAsSignal, showLabels);
		}

		public Indicators.EntryCount EntryCount(ISeries<double> input , int emaPeriod, int keyLevelToleranceTicks, int swingStrength, bool useSwingSR, int srLookbackSwings, int srToleranceTicks, bool useTrendlines, EntryCountOutsideBarPolicy outsideBarPolicy, int emaSlopeLookback, double minEmaSlopeTicks, double minBodyPct, double minClosePct, double maxBreakoutWickPct, bool useRejectionException, double rejectionMinClosePct, double rejectionMaxWickPct, EntryCountStopMode stopMode, int riskTemplateTicks, int riskToleranceTicks, double maxPullbackPct, bool requireEqualOrHigherLeg, bool resetOnKeyLevel, EntryCountResetScope resetScope, bool resetAfterTwoLegs, bool resetOnNewExtreme, EntryCountFailedEntryMode failedEntryMode, bool insideBarAsSignal, bool showLabels)
		{
			return indicator.EntryCount(input, emaPeriod, keyLevelToleranceTicks, swingStrength, useSwingSR, srLookbackSwings, srToleranceTicks, useTrendlines, outsideBarPolicy, emaSlopeLookback, minEmaSlopeTicks, minBodyPct, minClosePct, maxBreakoutWickPct, useRejectionException, rejectionMinClosePct, rejectionMaxWickPct, stopMode, riskTemplateTicks, riskToleranceTicks, maxPullbackPct, requireEqualOrHigherLeg, resetOnKeyLevel, resetScope, resetAfterTwoLegs, resetOnNewExtreme, failedEntryMode, insideBarAsSignal, showLabels);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EntryCount EntryCount(int emaPeriod, int keyLevelToleranceTicks, int swingStrength, bool useSwingSR, int srLookbackSwings, int srToleranceTicks, bool useTrendlines, EntryCountOutsideBarPolicy outsideBarPolicy, int emaSlopeLookback, double minEmaSlopeTicks, double minBodyPct, double minClosePct, double maxBreakoutWickPct, bool useRejectionException, double rejectionMinClosePct, double rejectionMaxWickPct, EntryCountStopMode stopMode, int riskTemplateTicks, int riskToleranceTicks, double maxPullbackPct, bool requireEqualOrHigherLeg, bool resetOnKeyLevel, EntryCountResetScope resetScope, bool resetAfterTwoLegs, bool resetOnNewExtreme, EntryCountFailedEntryMode failedEntryMode, bool insideBarAsSignal, bool showLabels)
		{
			return indicator.EntryCount(Input, emaPeriod, keyLevelToleranceTicks, swingStrength, useSwingSR, srLookbackSwings, srToleranceTicks, useTrendlines, outsideBarPolicy, emaSlopeLookback, minEmaSlopeTicks, minBodyPct, minClosePct, maxBreakoutWickPct, useRejectionException, rejectionMinClosePct, rejectionMaxWickPct, stopMode, riskTemplateTicks, riskToleranceTicks, maxPullbackPct, requireEqualOrHigherLeg, resetOnKeyLevel, resetScope, resetAfterTwoLegs, resetOnNewExtreme, failedEntryMode, insideBarAsSignal, showLabels);
		}

		public Indicators.EntryCount EntryCount(ISeries<double> input , int emaPeriod, int keyLevelToleranceTicks, int swingStrength, bool useSwingSR, int srLookbackSwings, int srToleranceTicks, bool useTrendlines, EntryCountOutsideBarPolicy outsideBarPolicy, int emaSlopeLookback, double minEmaSlopeTicks, double minBodyPct, double minClosePct, double maxBreakoutWickPct, bool useRejectionException, double rejectionMinClosePct, double rejectionMaxWickPct, EntryCountStopMode stopMode, int riskTemplateTicks, int riskToleranceTicks, double maxPullbackPct, bool requireEqualOrHigherLeg, bool resetOnKeyLevel, EntryCountResetScope resetScope, bool resetAfterTwoLegs, bool resetOnNewExtreme, EntryCountFailedEntryMode failedEntryMode, bool insideBarAsSignal, bool showLabels)
		{
			return indicator.EntryCount(input, emaPeriod, keyLevelToleranceTicks, swingStrength, useSwingSR, srLookbackSwings, srToleranceTicks, useTrendlines, outsideBarPolicy, emaSlopeLookback, minEmaSlopeTicks, minBodyPct, minClosePct, maxBreakoutWickPct, useRejectionException, rejectionMinClosePct, rejectionMaxWickPct, stopMode, riskTemplateTicks, riskToleranceTicks, maxPullbackPct, requireEqualOrHigherLeg, resetOnKeyLevel, resetScope, resetAfterTwoLegs, resetOnNewExtreme, failedEntryMode, insideBarAsSignal, showLabels);
		}
	}
}

#endregion
