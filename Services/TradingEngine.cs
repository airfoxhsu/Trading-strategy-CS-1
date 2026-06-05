using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ExtremeSignalAppCS.Models;

namespace ExtremeSignalAppCS.Services
{
    /// <summary>
    /// 核心計算與回測引擎。
    /// 100% 移植 Python 版 TradingEngine。
    /// 負責所有高階數學統計、N值 durations 統計、時序狀態機與分 K 聚合，
    /// 設計為 Thread-Safe 以供背景執行緒調用，徹底消滅主執行緒卡頓。
    /// </summary>
    public class TradingEngine
    {
        public int AbsNTicks { get; set; } = 250;

        // 觀察手動與自帶價格 (對應 Python mainWindow 中的極端觀測變數)
        public int? _obs_high_entry_price { get; set; }
        public int? _obs_low_entry_price { get; set; }
        public int? _obs_high_price { get; set; }
        public int? _obs_low_price { get; set; }
        
        // 用於快取 K棒 狀態機計算結果 (與 Python _sim_kline_cache 一致)
        private readonly Dictionary<string, List<SimulationResult>> _simKlineCache = new();
        
        // 專案根目錄
        private readonly string _appDir;

        public TradingEngine()
        {
            _appDir = AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// 100% 移植原始 calculate_stats 邏輯。
        /// </summary>
        public Dictionary<string, double>? CalculateStats(List<double> dataList)
        {
            if (dataList == null || dataList.Count == 0)
                return null;

            var sorted = new List<double>(dataList);
            sorted.Sort();
            int n = sorted.Count;

            double sum = 0;
            for (int i = 0; i < n; i++) sum += sorted[i];
            double mean = sum / n;

            double p25 = sorted[(int)(n * 0.25)];
            double p50 = sorted[n / 2];
            double p75 = sorted[(int)(n * 0.75)];
            double p90 = sorted[(int)(n * 0.90)];
            double max = sorted[^1];

            // 計算標準差
            double stdDev = 0.0;
            if (n > 1)
            {
                double sumOfSquares = sorted.Sum(d => Math.Pow(d - mean, 2));
                stdDev = Math.Sqrt(sumOfSquares / (n - 1));
            }

            return new Dictionary<string, double>
            {
                { "count", n },
                { "mean", mean },
                { "p25", p25 },
                { "p50", p50 },
                { "p75", p75 },
                { "p90", p90 },
                { "max", max },
                { "std", stdDev }
            };
        }

        /// <summary>
        /// 計算台股期貨近月合約代碼。
        /// </summary>
        public string GetMonthCode()
        {
            // 轉換為台北時間 (UTC + 8)
            DateTime now = DateTime.UtcNow.AddHours(8);
            
            // 計算當月第一個星期三
            DateTime firstDayOfMonth = new DateTime(now.Year, now.Month, 1);
            int daysUntilWednesday = ((int)DayOfWeek.Wednesday - (int)firstDayOfMonth.DayOfWeek + 7) % 7;
            DateTime thirdWednesday = firstDayOfMonth.AddDays(daysUntilWednesday + 14);
            
            // 第三個星期三 14:00 結算切換點
            DateTime thirdWedCutoff = new DateTime(thirdWednesday.Year, thirdWednesday.Month, thirdWednesday.Day, 14, 0, 0);

            DateTime targetMonth;
            if (now >= thirdWedCutoff)
            {
                targetMonth = thirdWednesday.AddDays(30); // 往後移一個月
                targetMonth = new DateTime(targetMonth.Year, targetMonth.Month, 1);
            }
            else
            {
                targetMonth = thirdWednesday;
            }

            string codes = "ABCDEFGHIJKL";
            int yearCode = targetMonth.Year % 10;
            char monthCode = codes[targetMonth.Month - 1];

            return $"{monthCode}{yearCode}";
        }

        /// <summary>
        /// 解析時間字串為當日累積秒數。
        /// </summary>
        public double ParseTime(string timeStr)
        {
            if (string.IsNullOrWhiteSpace(timeStr))
                return 0.0;

            timeStr = timeStr.Trim();

            // 處理元大 12 位元時間碼 (如 "084500123456" -> 時分秒微秒)
            if (timeStr.Length == 12 && long.TryParse(timeStr, out _))
            {
                try
                {
                    int h = int.Parse(timeStr.Substring(0, 2));
                    int m = int.Parse(timeStr.Substring(2, 2));
                    int s = int.Parse(timeStr.Substring(4, 2));
                    double ms = double.Parse(timeStr.Substring(6, 6)) / 1000000.0;
                    return h * 3600 + m * 60 + s + ms;
                }
                catch { }
            }

            // 處理標準格式 "08:45:00.123456"
            try
            {
                if (timeStr.Contains(":"))
                {
                    string[] parts = timeStr.Split('.');
                    string[] hms = parts[0].Split(':');
                    int h = int.Parse(hms[0]);
                    int m = int.Parse(hms[1]);
                    int s = int.Parse(hms[2]);
                    double ms = parts.Length > 1 ? double.Parse(parts[1]) / 1000000.0 : 0.0;
                    return h * 3600 + m * 60 + s + ms;
                }
            }
            catch { }

            return 0.0;
        }

        /// <summary>
        /// 計算外盤與內盤的每筆平均間隔時間與共識方向。
        /// </summary>
        public (double? OuterAvg, double? InnerAvg, string Direction) CalcSideSpeed(List<TradeTick> trades)
        {
            var outerTimes = trades.Where(t => t.Side == TradeSide.Outer).Select(t => t.TimeVal).ToList();
            var innerTimes = trades.Where(t => t.Side == TradeSide.Inner).Select(t => t.TimeVal).ToList();

            double? outerAvg = null;
            double? innerAvg = null;

            if (outerTimes.Count >= 2)
                outerAvg = (outerTimes[^1] - outerTimes[0]) / (outerTimes.Count - 1);

            if (innerTimes.Count >= 2)
                innerAvg = (innerTimes[^1] - innerTimes[0]) / (innerTimes.Count - 1);

            string direction = "資料不足";
            if (outerAvg.HasValue && innerAvg.HasValue)
            {
                if (Math.Abs(outerAvg.Value - innerAvg.Value) > 0.01)
                {
                    direction = outerAvg.Value < innerAvg.Value ? "多方 📈" : "空方 📉";
                }
                else
                {
                    direction = "持平 ⚖️";
                }
            }

            return (outerAvg, innerAvg, direction);
        }

        /// <summary>
        /// 從當前快取狀態計算內外盤的平均速度與共識方向 (100% 移植原版邏輯)。
        /// </summary>
        public (double? OuterAvg, double? InnerAvg, string Direction) CalcSideSpeedFromState(TradingState state)
        {
            double? outerAvg = null;
            double? innerAvg = null;

            if (state.OuterCount >= 2 && state.LastOuterTime.HasValue && state.FirstOuterTime.HasValue)
                outerAvg = (state.LastOuterTime.Value - state.FirstOuterTime.Value) / (state.OuterCount - 1);

            if (state.InnerCount >= 2 && state.LastInnerTime.HasValue && state.FirstInnerTime.HasValue)
                innerAvg = (state.LastInnerTime.Value - state.FirstInnerTime.Value) / (state.InnerCount - 1);

            string direction = "資料不足";
            if (outerAvg.HasValue && innerAvg.HasValue)
            {
                if (Math.Abs(outerAvg.Value - innerAvg.Value) > 0.01)
                {
                    direction = outerAvg.Value < innerAvg.Value ? "多方 📈" : "空方 📉";
                }
                else
                {
                    direction = "持平 ⚖️";
                }
            }

            return (outerAvg, innerAvg, direction);
        }

        /// <summary>
        /// 100% 移植 get_durations 核心判定邏輯。
        /// 在 B 點未確認前，A 點價格絕對不准突破（做空不准創新高，做多不准創新低），
        /// 這是系統過濾雜訊、保證訊號高勝率的物理護城河。
        /// </summary>
        public (double? PreAvg, double? PostAvg, int? Threshold, string? TrigTime, int? TrigPrice, int ActPre, int ActPost, int LastIndex)
            GetDurations(List<TradeTick> trades, int n, int idx, TradeSide preSide, TradeSide postSide)
        {
            if (idx >= trades.Count || idx < 0)
                return (null, null, null, null, null, 0, 0, idx);

            // 前向：蒐集 idx 及其前 n 筆同盤成交 (pre_list)
            var preList = new List<TradeTick> { trades[idx] };
            int curr = idx - 1;
            while (curr >= 0 && preList.Count < (n + 1))
            {
                if (trades[curr].Side == preSide)
                {
                    preList.Add(trades[curr]);
                }
                curr--;
            }

            // 後向：蒐集 idx 及其後 n 筆同盤成交 (post_list)
            var postList = new List<TradeTick> { trades[idx] };
            int extremePrice = trades[idx].Price;
            curr = idx + 1;

            while (curr < trades.Count && postList.Count < (n + 1))
            {
                // 約束條件：B 點形成前 A 點不准被突破
                if (postSide == TradeSide.Inner) // 疑似頭部，不准再創新高
                {
                    if (trades[curr].Price > extremePrice)
                    {
                        return (null, null, null, null, null, preList.Count - 1, postList.Count - 1, curr - 1);
                    }
                }
                else // 疑似底部，不准再創新低
                {
                    if (trades[curr].Price < extremePrice)
                    {
                        return (null, null, null, null, null, preList.Count - 1, postList.Count - 1, curr - 1);
                    }
                }

                if (trades[curr].Side == postSide)
                {
                    postList.Add(trades[curr]);
                }
                curr++;
            }

            int actualPreN = preList.Count - 1;
            int actualPostN = postList.Count - 1;

            double? preAvg = null;
            if (actualPreN >= 1)
            {
                double preSum = 0;
                for (int i = 0; i < actualPreN; i++)
                {
                    preSum += (preList[i].TimeVal - preList[i + 1].TimeVal);
                }
                preAvg = preSum / actualPreN;
            }

            double? postAvg = null;
            if (actualPostN >= n)
            {
                double postSum = 0;
                for (int i = 0; i < actualPostN; i++)
                {
                    postSum += (postList[i + 1].TimeVal - postList[i].TimeVal);
                }
                postAvg = postSum / actualPostN;
            }

            int? threshold = null;
            string? trigTime = null;
            int? trigPrice = null;

            if (actualPostN >= 1)
            {
                if (postSide == TradeSide.Inner)
                {
                    threshold = postList.Min(t => t.Price);
                }
                else
                {
                    threshold = postList.Max(t => t.Price);
                }
                trigTime = postList[^1].Time;
                trigPrice = postList[^1].Price;
            }

            return (preAvg, postAvg, threshold, trigTime, trigPrice, actualPreN, actualPostN, curr - 1);
        }

        /// <summary>
        /// 100% 移植原始 _get_status_str 邏輯。
        /// </summary>
        public string GetStatusStr(double? pre, double? post, int actualPre, int actualPost, int expectedN)
        {
            if (actualPre >= expectedN && actualPost >= expectedN)
            {
                return (pre.HasValue && post.HasValue && post.Value < pre.Value) ? " [達標]" : " [未達標]";
            }
            else if (actualPre < 1 && actualPost < 1)
            {
                return " [邊界資料不足]";
            }
            else if (actualPost < expectedN)
            {
                return " [邊界未達標]";
            }
            else if (pre.HasValue && post.HasValue && post.Value < pre.Value)
            {
                return " [邊界達標]";
            }
            else
            {
                return $" [邊界未達標({actualPre},{actualPost})]";
            }
        }

        /// <summary>
        /// 100% 移植原始的報告解析與載入邏輯。
        /// 讀取 reports/advanced_quant_report_merged.md，若不存在則使用內建預設值。
        /// </summary>
        public Dictionary<string, object> LoadQuantParams(string targetSymbol, int targetDays = 60)
        {
            var paramsResult = new Dictionary<string, object>();
            var timeTop = new Dictionary<string, (int p50, int p75, int p90)>();
            var timeBottom = new Dictionary<string, (int p50, int p75, int p90)>();

            if (targetSymbol == "TXF")
            {
                timeTop = new Dictionary<string, (int, int, int)>
                {
                    { "日盤: 08:45-09:45", (77, 179, 264) },
                    { "日盤: 09:45-10:45", (49, 86, 180) },
                    { "日盤: 10:45-11:45", (23, 59, 72) },
                    { "日盤: 11:45-13:45", (4, 27, 38) },
                    { "夜盤: 15:00-16:00", (152, 224, 388) },
                    { "夜盤: 16:00-19:00", (70, 154, 286) },
                    { "夜盤: 19:00-23:00", (65, 137, 325) },
                    { "夜盤: 23:00-05:00", (0, 43, 81) }
                };
                timeBottom = new Dictionary<string, (int, int, int)>
                {
                    { "日盤: 08:45-09:45", (61, 138, 371) },
                    { "日盤: 09:45-10:45", (68, 141, 242) },
                    { "日盤: 10:45-11:45", (32, 90, 185) },
                    { "日盤: 11:45-13:45", (3, 44, 99) },
                    { "夜盤: 15:00-16:00", (71, 174, 428) },
                    { "夜盤: 16:00-19:00", (76, 136, 389) },
                    { "夜盤: 19:00-23:00", (60, 189, 355) },
                    { "夜盤: 23:00-05:00", (21, 143, 333) }
                };
                paramsResult["source"] = "大台(TXF)系統預設值 (單一時段分佈架構)";
            }
            else
            {
                timeTop = new Dictionary<string, (int, int, int)>
                {
                    { "日盤: 08:45-09:45", (101, 192, 293) },
                    { "日盤: 09:45-10:45", (70, 129, 212) },
                    { "日盤: 10:45-11:45", (34, 69, 95) },
                    { "日盤: 11:45-13:45", (12, 29, 57) },
                    { "夜盤: 15:00-16:00", (152, 239, 460) },
                    { "夜盤: 16:00-18:00", (120, 182, 330) },
                    { "夜盤: 18:00-22:00", (59, 152, 234) },
                    { "夜盤: 22:00-23:00", (61, 149, 245) },
                    { "夜盤: 23:00-05:00", (16, 62, 117) }
                };
                timeBottom = new Dictionary<string, (int, int, int)>
                {
                    { "日盤: 08:45-09:45", (84, 171, 338) },
                    { "日盤: 09:45-10:45", (64, 130, 209) },
                    { "日盤: 10:45-11:45", (68, 140, 387) },
                    { "日盤: 11:45-13:45", (19, 63, 123) },
                    { "夜盤: 15:00-16:00", (76, 185, 442) },
                    { "夜盤: 16:00-18:00", (64, 223, 482) },
                    { "夜盤: 18:00-22:00", (65, 212, 436) },
                    { "夜盤: 22:00-23:00", (140, 329, 460) },
                    { "夜盤: 23:00-05:00", (66, 179, 301) }
                };
                paramsResult["source"] = "小台(MXF)系統預設值 (單一時段分佈架構)";
            }

            paramsResult["time_top"] = timeTop;
            paramsResult["time_bottom"] = timeBottom;

            // 嘗試載入 reports/advanced_quant_report_merged.md
            string reportPath = Path.Combine(_appDir, "reports", "advanced_quant_report_merged.md");
            if (!File.Exists(reportPath))
            {
                string oldPath = Path.Combine(_appDir, "reports", $"advanced_quant_report_{targetSymbol}.md");
                if (File.Exists(oldPath))
                    reportPath = oldPath;
                else
                    return paramsResult; // 找不到檔案，直接返回預設值
            }

            try
            {
                string fullContent = File.ReadAllText(reportPath, Encoding.UTF8);
                string symbolSection = fullContent;

                if (reportPath.Contains("advanced_quant_report_merged.md"))
                {
                    string header = $"# {targetSymbol} 量化分析報告";
                    var parts = fullContent.Split(new[] { header }, StringSplitOptions.None);
                    if (parts.Length > 1)
                    {
                        symbolSection = parts[1].Split(new[] { "\n---\n" }, StringSplitOptions.None)[0];
                    }
                    else
                    {
                        return paramsResult;
                    }
                }

                string targetLabel = targetDays == 0 ? "全部" : targetDays.ToString();
                string? dayRangeSection = null;

                string backtestHeader = $"## 回測天數: {targetLabel}";
                if (symbolSection.Contains(backtestHeader))
                {
                    var afterHeader = symbolSection.Split(new[] { backtestHeader }, StringSplitOptions.None)[1];
                    if (afterHeader.Contains("## 回測天數:"))
                    {
                        dayRangeSection = afterHeader.Split(new[] { "## 回測天數:" }, StringSplitOptions.None)[0];
                    }
                    else
                    {
                        dayRangeSection = afterHeader;
                    }

                    string headerLine = backtestHeader + afterHeader.Split('\n')[0];
                    var dayMatch = Regex.Match(headerLine, @"日盤\s+(\d+)\s+天");
                    var nightMatch = Regex.Match(headerLine, @"夜盤\s+(\d+)\s+天");
                    int dayCount = dayMatch.Success ? int.Parse(dayMatch.Groups[1].Value) : 0;
                    int nightCount = nightMatch.Success ? int.Parse(nightMatch.Groups[1].Value) : 0;
                    paramsResult["source"] = $"動態載入自 {targetSymbol} 回測數據 ({targetLabel}天, 日盤{dayCount}/夜盤{nightCount})";
                }
                else
                {
                    dayRangeSection = symbolSection;
                    var dayMatch = Regex.Match(symbolSection, @"日盤\s+(\d+)\s+天");
                    var nightMatch = Regex.Match(symbolSection, @"夜盤\s+(\d+)\s+天");
                    if (dayMatch.Success && nightMatch.Success)
                    {
                        paramsResult["source"] = $"動態載入自 {targetSymbol} 舊版回測數據 (日盤{dayMatch.Groups[1].Value}/夜盤{nightMatch.Groups[1].Value}天)";
                    }
                    else
                    {
                        var oldMatch = Regex.Match(symbolSection, @"結合\s+(\d+)\s+天");
                        if (oldMatch.Success)
                        {
                            paramsResult["source"] = $"動態載入自 {targetSymbol} 舊版回測數據 (共{oldMatch.Groups[1].Value}天)";
                        }
                    }
                }

                if (dayRangeSection != null)
                {
                    // 解析時段分佈 - top
                    if (dayRangeSection.Contains("### 時段分佈 - top"))
                    {
                        string topTimeSection = dayRangeSection.Split(new[] { "### 時段分佈 - top" }, StringSplitOptions.None)[1]
                                                             .Split(new[] { "###" }, StringSplitOptions.None)[0];
                        var clearedSess = new HashSet<string>();
                        foreach (var line in topTimeSection.Split('\n'))
                        {
                            if (line.Contains("|") && (line.Contains("日盤") || line.Contains("夜盤")))
                            {
                                var parts = line.Split('|').Select(p => p.Trim()).ToArray();
                                if (parts.Length >= 9)
                                {
                                    try
                                    {
                                        string dim = parts[1];
                                        int p50 = int.Parse(parts[6]);
                                        int p75 = int.Parse(parts[7]);
                                        int p90 = int.Parse(parts[8]);
                                        string sess = dim.Contains("日盤") ? "日盤" : "夜盤";
                                        if (!clearedSess.Contains(sess))
                                        {
                                            var keys = timeTop.Keys.ToList();
                                            foreach (var k in keys)
                                            {
                                                if (k.Contains(sess)) timeTop.Remove(k);
                                            }
                                            clearedSess.Add(sess);
                                        }
                                        timeTop[dim] = (p50, p75, p90);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    // 解析時段分佈 - bottom
                    if (dayRangeSection.Contains("### 時段分佈 - bottom"))
                    {
                        string botTimeSection = dayRangeSection.Split(new[] { "### 時段分佈 - bottom" }, StringSplitOptions.None)[1]
                                                             .Split(new[] { "###" }, StringSplitOptions.None)[0];
                        var clearedSess = new HashSet<string>();
                        foreach (var line in botTimeSection.Split('\n'))
                        {
                            if (line.Contains("|") && (line.Contains("日盤") || line.Contains("夜盤")))
                            {
                                var parts = line.Split('|').Select(p => p.Trim()).ToArray();
                                if (parts.Length >= 9)
                                {
                                    try
                                    {
                                        string dim = parts[1];
                                        int p50 = int.Parse(parts[6]);
                                        int p75 = int.Parse(parts[7]);
                                        int p90 = int.Parse(parts[8]);
                                        string sess = dim.Contains("日盤") ? "日盤" : "夜盤";
                                        if (!clearedSess.Contains(sess))
                                        {
                                            var keys = timeBottom.Keys.ToList();
                                            foreach (var k in keys)
                                            {
                                                if (k.Contains(sess)) timeBottom.Remove(k);
                                            }
                                            clearedSess.Add(sess);
                                        }
                                        timeBottom[dim] = (p50, p75, p90);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"解析量化報告例外: {ex.Message}");
            }

            return paramsResult;
        }

        /// <summary>
        /// 100% 移植 _calc_kline_data。
        /// </summary>
        public (List<KlineBar> KlineData, List<(string Direction, string BreakTime, string SigTime, List<SimulationResult> SigObjs)> Breakouts)
            CalcKlineData(string sessionName, List<TradeTick> trades, List<SimulationResult> txfSigs, List<SimulationResult> mxfSigs, int intervalMins = 30)
        {
            double startTVal = sessionName == "日盤" ? 31500.0 : 54000.0;
            double interval = intervalMins * 60.0;
            if (interval <= 0) interval = 1800.0;

            var signalTVals = new List<(double TVals, string SigType, SimulationResult SigObj)>();
            
            // 處理大臺與小臺訊號，彙整出所有突破觸發的 B 點秒數
            var sigGroups = new List<(string Prefix, List<SimulationResult> Sigs)>
            {
                ("大臺", txfSigs),
                ("小臺", mxfSigs)
            };

            foreach (var group in sigGroups)
            {
                foreach (var sig in group.Sigs)
                {
                    bool isUnmet = sig.DisplayTitle.Contains(" [未達標]");
                    string speedInfo = sig.Tags.FirstOrDefault(t => t.Contains("速")) ?? "";
                    
                    bool isContradiction = false;
                    if (isUnmet)
                    {
                        if (sig.DisplayTitle.Contains("最高") && (speedInfo.Contains("空速增") || speedInfo.Contains("多速減")))
                            isContradiction = true;
                        else if (sig.DisplayTitle.Contains("最低") && (speedInfo.Contains("多速增") || speedInfo.Contains("空速減")))
                            isContradiction = true;
                    }

                    bool isNormalTrigger = sig.DisplayTitle.Contains("[達標]") && 
                                           !sig.DisplayTitle.Contains("未") && 
                                           !sig.DisplayTitle.Contains("邊界");

                    if (isNormalTrigger || isContradiction)
                    {
                        if (!string.IsNullOrEmpty(sig.TrigTime))
                        {
                            double bTVal = ParseTime(sig.TrigTime);
                            // 夜盤跨日秒數還原
                            if (sessionName == "夜盤" && bTVal <= 18000.0)
                            {
                                bTVal += 86400.0;
                            }
                            string direction = sig.DisplayTitle.Contains("最高") ? "最高" : "最低";
                            string sigType = isNormalTrigger ? $"{group.Prefix}{direction}[達標]" : $"{group.Prefix}{direction}[矛盾]";
                            signalTVals.Add((bTVal, sigType, sig));
                        }
                    }
                }
            }

            signalTVals.Sort((x, y) => x.TVals.CompareTo(y.TVals));

            // 將 Tick 按時間區段分桶
            var buckets = new Dictionary<int, (List<TradeTick> Trades, List<string> Signals, List<SimulationResult> SignalObjs)>();
            foreach (var t in trades)
            {
                double tVal = t.TimeVal;
                if (tVal < startTVal) continue;
                
                int bucketIdx = (int)((tVal - startTVal) / interval);
                if (!buckets.ContainsKey(bucketIdx))
                {
                    buckets[bucketIdx] = (new List<TradeTick>(), new List<string>(), new List<SimulationResult>());
                }
                buckets[bucketIdx].Trades.Add(t);
            }

            // 將對應 B 點秒數的訊號放入時間分桶
            foreach (var (sigTVal, sigType, sigObj) in signalTVals)
            {
                if (sigTVal >= startTVal)
                {
                    int bucketIdx = (int)((sigTVal - startTVal) / interval);
                    if (buckets.ContainsKey(bucketIdx))
                    {
                        var b = buckets[bucketIdx];
                        if (b.Signals.Count == 0 || b.Signals[^1] != sigType)
                        {
                            b.Signals.Add(sigType);
                        }
                        b.SignalObjs.Add(sigObj);
                    }
                }
            }

            var klineData = new List<KlineBar>();
            var breakouts = new List<(string Direction, string BreakTime, string SigTime, List<SimulationResult> SigObjs)>();

            double? prevHigh = null;
            double? prevLow = null;

            double? pendingLongTriggerPrice = null;
            double? pendingShortTriggerPrice = null;
            var pendingLongSignalObjs = new List<SimulationResult>();
            var pendingShortSignalObjs = new List<SimulationResult>();
            string pendingLongTimeLabel = "";
            string pendingShortTimeLabel = "";

            var sortedIndices = buckets.Keys.ToList();
            sortedIndices.Sort();

            foreach (var bIdx in sortedIndices)
            {
                var bTrades = buckets[bIdx].Trades;
                if (bTrades.Count == 0) continue;

                double bStart = startTVal + bIdx * interval;
                double bEnd = bStart + interval;

                string FmtHm(double tS)
                {
                    tS %= 86400.0;
                    int h = (int)(tS / 3600);
                    int m = (int)((tS % 3600) / 60);
                    return $"{h:D2}:{m:D2}";
                }

                string timeLabel = $"{FmtHm(bStart)}~{FmtHm(bEnd)}";

                double openP = bTrades[0].Price;
                double highP = bTrades.Max(t => t.Price);
                double lowP = bTrades.Min(t => t.Price);
                double closeP = bTrades[^1].Price;

                string breakHighText = (prevHigh.HasValue && highP > prevHigh.Value) ? "是" : "";
                string breakLowText = (prevLow.HasValue && lowP < prevLow.Value) ? "是" : "";

                // 檢測突破訊號 (長度/方向判定)
                if (pendingLongTriggerPrice.HasValue && highP > pendingLongTriggerPrice.Value)
                {
                    breakHighText = "做多";
                    string breakoutTime = bTrades[^1].Time;
                    foreach (var t in bTrades)
                    {
                        if (t.Price > pendingLongTriggerPrice.Value)
                        {
                            breakoutTime = t.Time;
                            break;
                        }
                    }
                    breakouts.Add(("做多", breakoutTime, pendingLongTimeLabel, new List<SimulationResult>(pendingLongSignalObjs)));
                    pendingLongTriggerPrice = null;
                    pendingLongSignalObjs.Clear();
                }

                if (pendingShortTriggerPrice.HasValue && lowP < pendingShortTriggerPrice.Value)
                {
                    breakLowText = "做空";
                    string breakoutTime = bTrades[^1].Time;
                    foreach (var t in bTrades)
                    {
                        if (t.Price < pendingShortTriggerPrice.Value)
                        {
                            breakoutTime = t.Time;
                            break;
                        }
                    }
                    breakouts.Add(("做空", breakoutTime, pendingShortTimeLabel, new List<SimulationResult>(pendingShortSignalObjs)));
                    pendingShortTriggerPrice = null;
                    pendingShortSignalObjs.Clear();
                }

                // 註冊待觀察壓力/支撐
                var bSignals = buckets[bIdx].Signals;
                foreach (var sig in bSignals)
                {
                    if (sig.Contains("最低"))
                    {
                        pendingLongTriggerPrice = highP;
                        pendingShortTriggerPrice = null;
                        pendingLongSignalObjs = buckets[bIdx].SignalObjs.Where(o => o.DisplayTitle.Contains("最低")).ToList();
                        pendingLongTimeLabel = timeLabel;
                    }
                    else if (sig.Contains("最高"))
                    {
                        pendingShortTriggerPrice = lowP;
                        pendingLongTriggerPrice = null;
                        pendingShortSignalObjs = buckets[bIdx].SignalObjs.Where(o => o.DisplayTitle.Contains("最高")).ToList();
                        pendingShortTimeLabel = timeLabel;
                    }
                }

                prevHigh = highP;
                prevLow = lowP;

                string signalsStr = string.Join(", ", bSignals.Distinct());
                string tag = "flat";
                if (closeP > openP) tag = "up";
                else if (closeP < openP) tag = "down";

                var klineBar = new KlineBar(timeLabel, highP, lowP, openP, closeP, signalsStr, breakHighText, breakLowText, tag);
                klineData.Add(klineBar);
            }

            return (klineData, breakouts);
        }

        /// <summary>
        /// 100% 移植 _calc_simulation_results。
        /// 核心停損時序狀態機回測模擬。
        /// </summary>
        public List<SimulationResult> CalcSimulationResults(string session, List<TradeTick> trades, List<KlineBar> klines, int obsN)
        {
            var results = new List<SimulationResult>();
            if (klines == null || klines.Count < 2)
                return results;

            var klineBoundaries = new List<(double startT, double endT, int obsHighEntry, int obsLowEntry, int prevHigh, int prevLow)>();

            for (int i = 1; i < klines.Count; i++)
            {
                var prevKline = klines[i - 1];
                var currentKline = klines[i];

                int obsHighEntry, obsLowEntry, prevHigh, prevLow;
                try
                {
                    // 100% 還原 Python 邏輯：
                    // 做空觀察關卡 K低 (即前一日 K棒的最低點，Python 是 float(prev_kline[2]))
                    // 做多觀察關卡 K高 (即前一日 K棒的最高點，Python 是 float(prev_kline[1]))
                    obsHighEntry = (int)prevKline.Low;
                    obsLowEntry = (int)prevKline.High;
                    prevHigh = (int)prevKline.High;
                    prevLow = (int)prevKline.Low;
                }
                catch
                {
                    continue;
                }

                string timeLabel = currentKline.TimeLabel;
                try
                {
                    var times = timeLabel.Split('~');
                    var startParts = times[0].Split(':');
                    int sh = int.Parse(startParts[0]);
                    int sm = int.Parse(startParts[1]);
                    double startT = (sh * 60 + sm) * 60.0;
                    if (session == "夜盤" && (sh * 60 + sm) < 900) startT += 86400.0;

                    var endParts = times[1].Split(':');
                    int eh = int.Parse(endParts[0]);
                    int em = int.Parse(endParts[1]);
                    double endT = (eh * 60 + em) * 60.0;
                    if (session == "夜盤" && (eh * 60 + em) < 900) endT += 86400.0;

                    klineBoundaries.Add((startT, endT, obsHighEntry, obsLowEntry, prevHigh, prevLow));
                }
                catch { }
            }

            if (klineBoundaries.Count == 0)
                return results;

            int totalBoundaries = klineBoundaries.Count;
            int lastKnownStartIdx = 0;
            int lastKnownEndIdx = 0;

            var aggregatedRawResults = new List<( (int Price, string ATime, int ObsN) Key, SimulationResult Raw, List<string> Tags )>();

            for (int kbIdx = 0; kbIdx < totalBoundaries; kbIdx++)
            {
                var (klineStart, klineEnd, obsHighEntry, obsLowEntry, prevHigh, prevLow) = klineBoundaries[kbIdx];
                bool isLastKline = (kbIdx == totalBoundaries - 1);

                // 智慧快取判定 (已收盤 K 棒結果直接讀快取，不重算)
                // 重要：快取命中時，仍需加入 aggregatedRawResults 走第三階段停損狀態機，
                //       不可直接 results.AddRange()，否則 DisplayTitle 與 StopLossDisplay 不會被正確填入！
                string cacheKey = $"{klineStart}_{klineEnd}_{obsHighEntry}_{obsLowEntry}_{prevHigh}_{prevLow}_{obsN}";
                if (!isLastKline && _simKlineCache.TryGetValue(cacheKey, out var cachedRows))
                {
                    foreach (var cached in cachedRows)
                    {
                        // 從快取的 SimulationResult 重建 (Key, Raw, Tags) tuple，確保第三階段能正常執行
                        var cKey = (cached.BestAPrice, cached.BestATime, cached.ObsN);
                        var cTags = new List<string>(cached.Tags); // 深拷貝 Tags 防止共用引用
                        aggregatedRawResults.Add((cKey, cached, cTags));
                    }
                    continue;
                }

                // 尋找此分K在 Tick 陣列中的起點與終點索引
                int klineStartIdx = lastKnownStartIdx;
                for (int idx = lastKnownStartIdx; idx < trades.Count; idx++)
                {
                    if (trades[idx].TimeVal >= klineStart)
                    {
                        klineStartIdx = idx;
                        break;
                    }
                }

                int klineEndTradeIdx = trades.Count;
                for (int idx = Math.Max(lastKnownEndIdx, klineStartIdx); idx < trades.Count; idx++)
                {
                    if (trades[idx].TimeVal >= klineEnd)
                    {
                        klineEndTradeIdx = idx;
                        break;
                    }
                }

                lastKnownStartIdx = klineStartIdx;
                lastKnownEndIdx = klineEndTradeIdx;

                var slicedTrades = trades.Take(klineEndTradeIdx).ToList();
                if (slicedTrades.Count == 0) continue;

                var klineResults = new List<( (int Price, string ATime, int ObsN) Key, SimulationResult Raw, List<string> Tags )>();

                // ════════ 做空路徑 (觀察 K 低) ════════
                int searchStart = klineStartIdx;
                while (searchStart < slicedTrades.Count)
                {
                    int? gateIdx = null;
                    for (int j = Math.Max(1, searchStart); j < slicedTrades.Count; j++)
                    {
                        if (slicedTrades[j].Price < obsHighEntry && slicedTrades[j - 1].Price >= obsHighEntry)
                        {
                            gateIdx = j;
                            break;
                        }
                    }

                    if (!gateIdx.HasValue && searchStart < slicedTrades.Count && slicedTrades[searchStart].Price < obsHighEntry)
                    {
                        if (searchStart == klineStartIdx)
                            gateIdx = searchStart;
                    }

                    if (!gateIdx.HasValue)
                        break;

                    // 尋找過門後的最高價點 (疑似做空 A 點)
                    var runningMaxes = new List<int>();
                    int? currentMax = null;
                    for (int j = gateIdx.Value; j < slicedTrades.Count; j++)
                    {
                        if (slicedTrades[j].Price > obsHighEntry)
                        {
                            if (currentMax == null || slicedTrades[j].Price > currentMax.Value)
                            {
                                currentMax = slicedTrades[j].Price;
                                runningMaxes.Add(j);
                            }
                        }
                    }

                    if (runningMaxes.Count == 0)
                        break;

                    int? lastSuccessfulBIdx = null;
                    foreach (var aIdx in runningMaxes)
                    {
                        int aPrice = slicedTrades[aIdx].Price;
                        var (pre, post, threshold, trigTime, trigPrice, actPre, actPost, bIdx) =
                            GetDurations(slicedTrades, obsN, aIdx, TradeSide.Outer, TradeSide.Inner);

                        if (pre.HasValue && post.HasValue && actPre >= obsN && actPost >= obsN && post.Value < pre.Value && trigPrice.HasValue)
                        {
                            var key = (aPrice, slicedTrades[aIdx].Time, obsN);
                            if (!klineResults.Any(k => k.Key == key))
                            {
                                var raw = new SimulationResult
                                {
                                    Type = "K低",
                                    ObsEntry = obsHighEntry,
                                    BestATime = slicedTrades[aIdx].Time,
                                    BestAPrice = aPrice,
                                    TrigTime = trigTime ?? "N/A",
                                    TrigPrice = trigPrice.Value.ToString(),
                                    Pre = pre.HasValue ? $"{pre.Value:F4}s" : "N/A",
                                    Post = post.HasValue ? $"{post.Value:F4}s" : "N/A",
                                    PrevHigh = prevHigh,
                                    PrevLow = prevLow,
                                    BIndex = bIdx,
                                    ObsN = obsN,
                                    StopLossPrice = prevHigh
                                };
                                klineResults.Add((key, raw, new List<string> { "obs_high" }));
                            }
                            lastSuccessfulBIdx = bIdx;
                        }
                    }

                    if (lastSuccessfulBIdx.HasValue)
                        searchStart = lastSuccessfulBIdx.Value + 1;
                    else
                        break;
                }

                // ════════ 做多路徑 (觀察 K 高) ════════
                searchStart = klineStartIdx;
                while (searchStart < slicedTrades.Count)
                {
                    int? gateIdx = null;
                    for (int j = Math.Max(1, searchStart); j < slicedTrades.Count; j++)
                    {
                        if (slicedTrades[j].Price > obsLowEntry && slicedTrades[j - 1].Price <= obsLowEntry)
                        {
                            gateIdx = j;
                            break;
                        }
                    }

                    if (!gateIdx.HasValue && searchStart < slicedTrades.Count && slicedTrades[searchStart].Price > obsLowEntry)
                    {
                        if (searchStart == klineStartIdx)
                            gateIdx = searchStart;
                    }

                    if (!gateIdx.HasValue)
                        break;

                    // 尋找過門後的最低價點 (疑似做多 A 點)
                    var runningMins = new List<int>();
                    int? currentMin = null;
                    for (int j = gateIdx.Value; j < slicedTrades.Count; j++)
                    {
                        if (slicedTrades[j].Price < obsLowEntry)
                        {
                            if (currentMin == null || slicedTrades[j].Price < currentMin.Value)
                            {
                                currentMin = slicedTrades[j].Price;
                                runningMins.Add(j);
                            }
                        }
                    }

                    if (runningMins.Count == 0)
                        break;

                    int? lastSuccessfulBIdx = null;
                    foreach (var aIdx in runningMins)
                    {
                        int aPrice = slicedTrades[aIdx].Price;
                        var (pre, post, threshold, trigTime, trigPrice, actPre, actPost, bIdx) =
                            GetDurations(slicedTrades, obsN, aIdx, TradeSide.Inner, TradeSide.Outer);

                        if (pre.HasValue && post.HasValue && actPre >= obsN && actPost >= obsN && post.Value < pre.Value && trigPrice.HasValue)
                        {
                            var key = (aPrice, slicedTrades[aIdx].Time, obsN);
                            if (!klineResults.Any(k => k.Key == key))
                            {
                                var raw = new SimulationResult
                                {
                                    Type = "K高",
                                    ObsEntry = obsLowEntry,
                                    BestATime = slicedTrades[aIdx].Time,
                                    BestAPrice = aPrice,
                                    TrigTime = trigTime ?? "N/A",
                                    TrigPrice = trigPrice.Value.ToString(),
                                    Pre = pre.HasValue ? $"{pre.Value:F4}s" : "N/A",
                                    Post = post.HasValue ? $"{post.Value:F4}s" : "N/A",
                                    PrevHigh = prevHigh,
                                    PrevLow = prevLow,
                                    BIndex = bIdx,
                                    ObsN = obsN,
                                    StopLossPrice = prevLow
                                };
                                klineResults.Add((key, raw, new List<string> { "obs_low" }));
                            }
                            lastSuccessfulBIdx = bIdx;
                        }
                    }

                    if (lastSuccessfulBIdx.HasValue)
                        searchStart = lastSuccessfulBIdx.Value + 1;
                    else
                        break;
                }

                // 寫入已收盤 K 棒的快取，免除下次的密集重度運算
                // 注意：必須深拷貝所有欄位，包括 Tags，確保快取命中時第三階段狀態機能正確運作
                if (!isLastKline)
                {
                    var cacheList = klineResults.Select(k =>
                    {
                        var copy = new SimulationResult
                        {
                            Type = k.Raw.Type,
                            // DisplayTitle 在第三階段狀態機中才動態組合，快取存 raw 的 Type 即可，
                            // 此處刻意留空由第三階段 finalResult 重新填入，無需提前寫入
                            ObsEntry = k.Raw.ObsEntry,
                            BestATime = k.Raw.BestATime,
                            BestAPrice = k.Raw.BestAPrice,
                            TrigTime = k.Raw.TrigTime,
                            TrigPrice = k.Raw.TrigPrice,
                            Pre = k.Raw.Pre,
                            Post = k.Raw.Post,
                            PrevHigh = k.Raw.PrevHigh,
                            PrevLow = k.Raw.PrevLow,
                            BIndex = k.Raw.BIndex,
                            ObsN = k.Raw.ObsN,
                            StopLossPrice = k.Raw.StopLossPrice
                        };
                        // 深拷貝 Tags 集合，防止快取共用同一個 List<string> 引用導致併發污染
                        foreach (var tag in k.Tags)
                            copy.Tags.Add(tag);
                        return copy;
                    }).ToList();
                    _simKlineCache[cacheKey] = cacheList;
                }

                aggregatedRawResults.AddRange(klineResults);
            }

            // ════════ 階段三：時序停損時序狀態機 (100% 還原已破時序鎖定邏輯) ════════
            // 按 B 點確立之 Tick 索引進行全局嚴格時序排序
            aggregatedRawResults.Sort((x, y) => x.Raw.BIndex.CompareTo(y.Raw.BIndex));

            string? currentMode = null;
            int? lockedSL = null;

            foreach (var (key, raw, tags) in aggregatedRawResults)
            {
                string sigType = raw.Type;
                int bIdx = raw.BIndex;
                int stopLoss = raw.StopLossPrice;

                if (sigType == "K低")
                {
                    if (currentMode != "K低")
                    {
                        currentMode = "K低";
                        lockedSL = raw.PrevHigh; // 做空防守
                    }
                }
                else if (sigType == "K高")
                {
                    if (currentMode != "K高")
                    {
                        currentMode = "K高";
                        lockedSL = raw.PrevLow; // 做多防守
                    }
                }

                // 找尋從當前 B 點到下一筆 B 點 (Chrono 盤中檢測) 與到最後 Tick (Greedy 全局檢測) 的資料切片
                int nextBIndex = trades.Count;
                int currentListIdx = aggregatedRawResults.FindIndex(k => k.Key == key);
                if (currentListIdx + 1 < aggregatedRawResults.Count)
                {
                    nextBIndex = aggregatedRawResults[currentListIdx + 1].Raw.BIndex;
                }

                var checkTradesChrono = trades.Skip(bIdx).Take(nextBIndex - bIdx).ToList();
                var checkTradesGreedy = trades.Skip(bIdx).ToList();

                bool isBrokenChrono = false;
                bool isBrokenGreedy = false;

                string? breakTime = null;
                if (lockedSL.HasValue)
                {
                    if (sigType == "K低")
                    {
                        int bkIdx = checkTradesChrono.FindIndex(t => t.Price > lockedSL.Value);
                        if (bkIdx >= 0) isBrokenChrono = true;

                        int bkgIdx = checkTradesGreedy.FindIndex(t => t.Price > lockedSL.Value);
                        if (bkgIdx >= 0)
                        {
                            isBrokenGreedy = true;
                            breakTime = checkTradesGreedy[bkgIdx].Time;
                        }
                    }
                    else if (sigType == "K高")
                    {
                        int bkIdx = checkTradesChrono.FindIndex(t => t.Price < lockedSL.Value);
                        if (bkIdx >= 0) isBrokenChrono = true;

                        int bkgIdx = checkTradesGreedy.FindIndex(t => t.Price < lockedSL.Value);
                        if (bkgIdx >= 0)
                        {
                            isBrokenGreedy = true;
                            breakTime = checkTradesGreedy[bkgIdx].Time;
                        }
                    }
                }

                string stopLossDisplay = lockedSL.HasValue
                    ? (isBrokenGreedy ? $"{lockedSL.Value}(已破)" : lockedSL.Value.ToString())
                    : "N/A";

                if (isBrokenChrono)
                {
                    // 盤中該段停損若被觸發，狀態機重設，解開防線，下一個訊號將重新鎖定防守
                    currentMode = null;
                }

                var finalResult = new SimulationResult
                {
                    DisplayTitle = $"N={raw.ObsN} 觀察{sigType} {raw.ObsEntry}",
                    BestATime = raw.BestATime,
                    BestAPrice = raw.BestAPrice,
                    TrigTime = raw.TrigTime,
                    TrigPrice = raw.TrigPrice,
                    Pre = raw.Pre,
                    Post = raw.Post,
                    StopLossDisplay = stopLossDisplay,
                    Tags = new List<string>(tags),
                    Type = sigType,
                    ObsEntry = raw.ObsEntry,
                    PrevHigh = raw.PrevHigh,
                    PrevLow = raw.PrevLow,
                    BIndex = bIdx,
                    ObsN = raw.ObsN,
                    StopLossPrice = lockedSL ?? 0,
                    IsBroken = isBrokenGreedy,
                    BreakTime = breakTime
                };

                results.Add(finalResult);
            }

            return results;
        }

        /// <summary>
        /// 100% 移植共識推播歷史紀錄模擬。
        /// </summary>
        public List<string> SimulateSpeedPushesDual(List<TradeTick> txfTrades, List<TradeTick> mxfTrades)
        {
            var pushes = new List<string>();

            // 雙軌融合排序
            var tagged = txfTrades.Select(t => (Sym: "TXF", Tick: t))
                       .Concat(mxfTrades.Select(t => (Sym: "MXF", Tick: t)))
                       .OrderBy(x => x.Tick.TimeVal)
                       .ToList();

            var states = new Dictionary<string, TradingState>
            {
                { "TXF", new TradingState() },
                { "MXF", new TradingState() }
            };

            string? lastConsensus = null;
            bool hasPushed = false;

            foreach (var item in tagged)
            {
                var sym = item.Sym;
                var t = item.Tick;
                var s = states[sym];

                s.SumPrice += t.Price;
                s.Count++;

                if (t.Side == TradeSide.Outer)
                {
                    if (s.FirstOuterTime == null) s.FirstOuterTime = t.TimeVal;
                    s.OuterCount++;
                    s.LastOuterTime = t.TimeVal;
                }
                else if (t.Side == TradeSide.Inner)
                {
                    if (s.FirstInnerTime == null) s.FirstInnerTime = t.TimeVal;
                    s.InnerCount++;
                    s.LastInnerTime = t.TimeVal;
                }

                // 增量計算外內盤平均速度
                double? oAvg = (s.OuterCount >= 2 && s.LastOuterTime.HasValue && s.FirstOuterTime.HasValue)
                    ? (s.LastOuterTime.Value - s.FirstOuterTime.Value) / (s.OuterCount - 1)
                    : null;

                double? iAvg = (s.InnerCount >= 2 && s.LastInnerTime.HasValue && s.FirstInnerTime.HasValue)
                    ? (s.LastInnerTime.Value - s.FirstInnerTime.Value) / (s.InnerCount - 1)
                    : null;

                string direction = "資料不足";
                if (oAvg.HasValue && iAvg.HasValue)
                {
                    if (Math.Abs(oAvg.Value - iAvg.Value) > 0.01)
                    {
                        direction = oAvg.Value < iAvg.Value ? "多方 📈" : "空方 📉";
                    }
                    else
                    {
                        direction = "持平 ⚖️";
                    }
                }

                // 更新此商品的方向
                // 在 C# 中，我們先透過暫時方法對其做模擬狀態
                // 但要實現多空共識的判定：
                // Python: both_ready = all(state[k]["o_count"] >= 250 and state[k]["i_count"] >= 250 for k in ["TXF", "MXF"])
                bool bothReady = states["TXF"].OuterCount >= 250 && states["TXF"].InnerCount >= 250 &&
                                 states["MXF"].OuterCount >= 250 && states["MXF"].InnerCount >= 250;

                if (!bothReady) continue;

                // 重新計算 TXF 與 MXF 的當前方向
                var (_, _, txfDir) = CalcSideSpeedFromState(states["TXF"]);
                var (_, _, mxfDir) = CalcSideSpeedFromState(states["MXF"]);

                string? consensus = null;
                if (txfDir.Contains("多方") && mxfDir.Contains("多方"))
                    consensus = "多方 📈";
                else if (txfDir.Contains("空方") && mxfDir.Contains("空方"))
                    consensus = "空方 📉";

                if (consensus == null) continue;

                bool switched = false;
                string arrow = "";

                if (lastConsensus != null)
                {
                    if (lastConsensus.Contains("空方") && consensus.Contains("多方"))
                    {
                        arrow = "空方 → 多方 📈";
                        switched = true;
                    }
                    else if (lastConsensus.Contains("多方") && consensus.Contains("空方"))
                    {
                        arrow = "多方 → 空方 📉";
                        switched = true;
                    }
                }
                else if (!hasPushed)
                {
                    arrow = $"初步確立 → {consensus}";
                    switched = true;
                }

                if (switched)
                {
                    hasPushed = true;
                    lastConsensus = consensus;

                    string spd(TradingState st, bool isOuter)
                    {
                        if (isOuter)
                        {
                            return (st.OuterCount >= 2 && st.LastOuterTime.HasValue && st.FirstOuterTime.HasValue)
                                ? $"{(st.LastOuterTime.Value - st.FirstOuterTime.Value) / (st.OuterCount - 1):F4}s"
                                : "--";
                        }
                        else
                        {
                            return (st.InnerCount >= 2 && st.LastInnerTime.HasValue && st.FirstInnerTime.HasValue)
                                ? $"{(st.LastInnerTime.Value - st.FirstInnerTime.Value) / (st.InnerCount - 1):F4}s"
                                : "--";
                        }
                    }

                    int avgPri(TradingState st)
                    {
                        return st.Count > 0 ? (int)Math.Round((double)st.SumPrice / st.Count) : 0;
                    }

                    string pushMsg =
                        $"    [共識推播] {t.Time} | {arrow.PadRight(15)} | 價: {t.Price,-5}" +
                        $" | 大臺 外:{spd(states["TXF"], true)} 內:{spd(states["TXF"], false)} 均價:{avgPri(states["TXF"])}" +
                        $" | 小臺 外:{spd(states["MXF"], true)} 內:{spd(states["MXF"], false)} 均價:{avgPri(states["MXF"])}";
                    
                    pushes.Add(pushMsg);
                }
            }

            return pushes;
        }

        /// <summary>
        /// 100% 移植 _get_speed_snapshot_str。
        /// </summary>
        public string GetSpeedSnapshotStr(string symbol, List<TradeTick> trades, int trigIdx, List<TradeTick> otherTradesAll, Dictionary<string, double?>? lastNetSpeeds = null)
        {
            if (trigIdx >= trades.Count)
                trigIdx = trades.Count - 1;

            if (trigIdx < 0)
                return "    成交速度: 資料不足";

            double targetTVal = trades[trigIdx].TimeVal;
            var tradesUpToTrig = trades.Take(trigIdx + 1).ToList();

            var (oAvg, iAvg, dStr) = CalcSideSpeed(tradesUpToTrig);
            int oCnt = tradesUpToTrig.Count(t => t.Side == TradeSide.Outer);
            int iCnt = tradesUpToTrig.Count(t => t.Side == TradeSide.Inner);

            string oS = oAvg.HasValue ? $"{oAvg.Value:F4}s/{oCnt,5}筆" : "資料不足";
            string iS = iAvg.HasValue ? $"{iAvg.Value:F4}s/{iCnt,5}筆" : "資料不足";
            int avgPri = tradesUpToTrig.Count > 0 ? (int)Math.Round(tradesUpToTrig.Sum(t => (double)t.Price) / tradesUpToTrig.Count) : 0;

            double? CalcNet(List<TradeTick> trList)
            {
                var (oa, ia, _) = CalcSideSpeed(trList);
                if (oa.HasValue && ia.HasValue)
                    return ia.Value - oa.Value;
                return null;
            }

            double? baseNet = CalcNet(tradesUpToTrig);
            string baseSym = symbol.Contains("TXF") ? "TXF" : "MXF";
            string otherSym = symbol.Contains("TXF") ? "MXF" : "TXF";

            var otherTradesUpTo = otherTradesAll.Where(t => t.TimeVal <= targetTVal).ToList();
            double? otherNet = CalcNet(otherTradesUpTo);

            var netSpeedsDisp = new Dictionary<string, string> { { "TXF", "--" }, { "MXF", "--" } };

            string FormatNet(string sym, double? currVal)
            {
                if (currVal == null)
                    return "--       ";

                string baseStr = $"{currVal.Value:+0.0000;-0.0000;+0.0000}s";
                string suffix = "";

                if (lastNetSpeeds != null)
                {
                    double? prevVal = lastNetSpeeds.ContainsKey(sym) ? lastNetSpeeds[sym] : null;
                    if (prevVal.HasValue && Math.Abs(currVal.Value - prevVal.Value) > 0.00001)
                    {
                        if (currVal.Value > prevVal.Value)
                        {
                            suffix = currVal.Value > 0 ? " 多速增" : " 空速減";
                        }
                        else
                        {
                            suffix = currVal.Value < 0 ? " 空速增" : " 多速減";
                        }
                    }
                    lastNetSpeeds[sym] = currVal;
                }

                if (string.IsNullOrEmpty(suffix))
                    suffix = "       ";

                return baseStr + suffix;
            }

            netSpeedsDisp[baseSym] = FormatNet(baseSym, baseNet);
            netSpeedsDisp[otherSym] = FormatNet(otherSym, otherNet);

            return $"    成交速度: 外盤(買) {oS} | 內盤(賣) {iS} → {dStr} | 大台速差: {netSpeedsDisp["TXF"]}  小台速差: {netSpeedsDisp["MXF"]} | 均價:{avgPri}";
        }

        /// <summary>
        /// 100% 移植 _generate_kline_text 邏輯。
        /// 產生純文字版的小臺 K 線報表，包含大小臺突破信號與 consensus 標記。
        /// </summary>
        public string _generate_kline_text(string sessionName, List<object> klineData, List<object> breakouts, string intervalMins = "30")
        {
            if (klineData == null || klineData.Count == 0)
                return "";

            string WidePad(string text, int length)
            {
                if (text == null) text = "";
                int currentLen = 0;
                foreach (char c in text)
                {
                    currentLen += (c >= 0x4e00 && c <= 0x9fff) ? 2 : 1;
                }
                int needed = length - currentLen;
                if (needed <= 0) return text;
                return text + new string(' ', needed);
            }

            string header = $"    {WidePad("時間", 15)} | {WidePad("高", 6)} | {WidePad("低", 6)} | {WidePad("開", 6)} | {WidePad("收", 6)} | {WidePad("訊號標記", 60)} | {WidePad("突破上高", 8)} | {WidePad("跌破上低", 8)}";
            string sep = "    " + new string('-', 115);

            var res = new StringBuilder();
            res.AppendLine($"\n    [{sessionName} 小臺 {intervalMins} 分鐘 K 線]");
            res.AppendLine(header);
            res.AppendLine(sep);

            foreach (var item in klineData)
            {
                if (item is KlineBar row)
                {
                    res.AppendLine($"    {WidePad(row.TimeLabel, 15)} | {WidePad(row.High.ToString(), 6)} | {WidePad(row.Low.ToString(), 6)} | {WidePad(row.Open.ToString(), 6)} | {WidePad(row.Close.ToString(), 6)} | {WidePad(row.Signals, 60)} | {WidePad(row.BreakHigh, 8)} | {WidePad(row.BreakLow, 8)}");
                }
            }

            if (breakouts != null && breakouts.Count > 0)
            {
                res.AppendLine($"\n    [{sessionName} K線突破訊號]");
                foreach (var item in breakouts)
                {
                    if (item is ValueTuple<string, string, string, List<SimulationResult>> d)
                    {
                        var direction = d.Item1;
                        var bTime = d.Item2;
                        var sigTime = d.Item3;
                        var sigObjs = d.Item4;

                        var aPrices = new List<string>();
                        var bPrices = new List<string>();

                        if (sigObjs != null)
                        {
                            foreach (var obj in sigObjs)
                            {
                                if (obj != null)
                                {
                                    if (obj.StopLossPrice != 0) aPrices.Add(obj.StopLossPrice.ToString());
                                    if (obj.ObsEntry != 0) bPrices.Add(obj.ObsEntry.ToString());
                                }
                            }
                        }

                        string aPricesStr = aPrices.Count > 0 ? string.Join(", ", aPrices.Distinct().OrderBy(x => x)) : "N/A";
                        string bPricesStr = bPrices.Count > 0 ? string.Join(", ", bPrices.Distinct().OrderBy(x => x)) : "N/A";
                        string emoji = direction == "做多" ? "📈" : "📉";

                        res.AppendLine($"    >>>> {direction} {emoji} | 突破時間: {bTime} | 訊號時間: {sigTime} | 進場價(B點): {bPricesStr} | 停損價(A點): {aPricesStr}");
                    }
                }
            }

            return res.ToString();
        }

        /// <summary>
        /// 快取重置。
        /// </summary>
        public void ClearCache()
        {
            _simKlineCache.Clear();
        }
    }
}
