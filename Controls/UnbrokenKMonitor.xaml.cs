using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ExtremeSignalAppCS.Models;
using ExtremeSignalAppCS.Services;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Run = System.Windows.Documents.Run;

namespace ExtremeSignalAppCS.Controls
{
    /// <summary>
    /// UnbrokenKMonitor.xaml 的互動邏輯。
    /// </summary>
    public partial class UnbrokenKMonitor : System.Windows.Controls.UserControl
    {
        private TradingEngine? _engine;
        private MainWindow? _parentApp;
        
        private bool _bgCheckInProgress;
        private readonly object _lock = new();
        private long _currentCheckId = 0;

        // 快取當前未破停損價格 (Key: (Type[high/low], PriceStr) -> Value: 分K分鐘數集合)
        private Dictionary<(string Type, string Price), HashSet<int>> _currentUnbrokenMap = new();
        private Dictionary<(string Type, string Price), string> _currentUnbrokenTimeMap = new();
        
        private class TrendEvent
        {
            public int Direction { get; set; }
            public int LongCount { get; set; }
            public int ShortCount { get; set; }
            public string EstablishedTime { get; set; } = "";
            public int? EstablishedPrice { get; set; }
            public string Reason { get; set; } = "";
        }
        
        // 趨勢方向表單狀態: 1 = 多方, -1 = 空方, 0 = 無資料
        private int _trendDirection = 0;
        private readonly List<TrendEvent> _trendHistory = new();
        
        // 記錄目前在趨勢表單中被選取反白的時間點
        private string? _selectedTrendTime = null;

        public UnbrokenKMonitor()
        {
            InitializeComponent();
        }

        public void Initialize(TradingEngine engine, MainWindow parentApp)
        {
            _engine = engine;
            _parentApp = parentApp;
        }

        /// <summary>
        /// 標記有新行情資料需要重新計算。
        /// 由 MainWindow 在實時分析完成後呼叫，將受限於 2000ms 的降頻保護。
        /// </summary>
        public void MarkDirty()
        {
            TriggerUnbrokenCheck(false); // 必須設為 false 以啟動降頻保護
        }

        private double _lastCheckTimeMs = 0;

        /// <summary>
        /// 週期性或事件驅動觸發未破停損分析。
        /// </summary>
        public void TriggerUnbrokenCheck(bool force = false)
        {
            if (_engine == null || _parentApp == null) return;
            if (_bgCheckInProgress) return;

            // 加入 2000ms 的操作感知型降頻，因為這牽涉到 7 個時間級別的全局大迴圈狀態機運算
            double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            if (!force && nowMs - _lastCheckTimeMs < 2000) return;
            _lastCheckTimeMs = nowMs;

            _bgCheckInProgress = true;
            long myRunId = Interlocked.Increment(ref _currentCheckId);

            // 在主執行緒提取不可在背景直接讀取的 GUI 變數快照 (避免 Thread Access Exception)
            int obsN = _parentApp.GetObsN();
            List<string> intervalsStr = _parentApp.GetKlineIntervals();
            var sessionDataSnapshot = _parentApp.GatherSessionDataSnapshot();

            if (sessionDataSnapshot == null || sessionDataSnapshot.Count == 0 || !sessionDataSnapshot.Any(x => x.Trades.Count > 0))
            {
                _bgCheckInProgress = false;
                return;
            }

            // 背景非同步執行緒運行重度 K 線 OHLC 聚合與狀態機運算
            Task.Run(() =>
            {
                try
                {
                    var tempUnbrokenMap = new Dictionary<(string Type, string Price), HashSet<int>>();
                    var tempTimeMap = new Dictionary<(string Type, string Price), string>();
                    var stopLossLifespans = new Dictionary<(string Type, string StopLossVal), List<(double TrigTVal, string TrigTimeStr, double? BreakTVal, string BreakTimeStr, int? TrigPrice, string IntervalStr)>>();

                    foreach (var intervalStr in intervalsStr)
                    {
                        if (!int.TryParse(intervalStr, out int intMins))
                            continue;

                        foreach (var (sessionName, trades, txfSigs, mxfSigs) in sessionDataSnapshot)
                        {
                            // 背景非同步聚合分 K
                            var (klineData, _) = _engine.CalcKlineData(sessionName, trades, txfSigs, mxfSigs, intMins);

                            // 背景非同步跑停損狀態機模擬
                            var results = _engine.CalcSimulationResults(sessionName, trades, klineData, obsN, true);

                            foreach (var item in results)
                            {
                                if (item.Tags.Contains("history") || item.Tags.Contains("annotation"))
                                    continue;

                                string sigLabel = item.DisplayTitle;
                                string stopLossVal = item.StopLossDisplay;
                                
                                string? typeObj = null;
                                if (sigLabel.Contains("K高")) typeObj = "K高";
                                else if (sigLabel.Contains("K低")) typeObj = "K低";
                                
                                if (typeObj != null)
                                {
                                    double trigTVal = ParseTimeStr(item.TrigTime);
                                    double? breakTVal = null;
                                    string breakTimeStr = "";
                                    if (item.IsBroken && !string.IsNullOrEmpty(item.BreakTime))
                                    {
                                        breakTVal = ParseTimeStr(item.BreakTime);
                                        breakTimeStr = item.BreakTime;
                                    }
                                    
                                    string slKeyStr = item.StopLossPrice.ToString();
                                    var lifeKey = (typeObj, slKeyStr);
                                    
                                    if (!stopLossLifespans.ContainsKey(lifeKey))
                                    {
                                        stopLossLifespans[lifeKey] = new List<(double TrigTVal, string TrigTimeStr, double? BreakTVal, string BreakTimeStr, int? TrigPrice, string IntervalStr)>();
                                    }
                                    int? tp = int.TryParse(item.TrigPrice, out int tpVal) ? tpVal : (int?)null;
                                    stopLossLifespans[lifeKey].Add((trigTVal, item.TrigTime, breakTVal, breakTimeStr, tp, intMins.ToString()));
                                }

                                if (!string.IsNullOrEmpty(stopLossVal) && stopLossVal != "N/A" && !stopLossVal.Contains("已破"))
                                {
                                    string? type = null;
                                    if (sigLabel.Contains("K高")) type = "high";
                                    else if (sigLabel.Contains("K低")) type = "low";

                                    if (type != null)
                                    {
                                        var key = (type, stopLossVal);
                                        string timeStr = item.BestATimeDisplay;
                                        lock (_lock)
                                        {
                                            if (!tempUnbrokenMap.ContainsKey(key))
                                            {
                                                tempUnbrokenMap[key] = new HashSet<int>();
                                            }
                                            tempUnbrokenMap[key].Add(intMins);

                                            if (!tempTimeMap.ContainsKey(key) || string.Compare(timeStr, tempTimeMap[key]) > 0)
                                            {
                                                tempTimeMap[key] = timeStr;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 根據 stopLossLifespans 建立歷史時間軸
                    var timeline = new List<(double TVal, string TimeStr, string Type, int Delta, int? Price, string Reason)>();
                    foreach (var kvp in stopLossLifespans)
                    {
                        var spans = kvp.Value;
                        spans.Sort((a, b) => a.TrigTVal.CompareTo(b.TrigTVal));
                        
                        var merged = new List<(double Trig, string TrigStr, double Break, string BreakStr, int? Price, string IntervalStr)>();
                        foreach (var span in spans)
                        {
                            double spanBreak = span.BreakTVal ?? 999999;
                            if (merged.Count == 0)
                            {
                                merged.Add((span.TrigTVal, span.TrigTimeStr, spanBreak, span.BreakTimeStr, span.TrigPrice, span.IntervalStr));
                            }
                            else
                            {
                                var last = merged[^1];
                                if (span.TrigTVal <= last.Break)
                                {
                                    if (spanBreak > last.Break)
                                    {
                                        merged[^1] = (last.Trig, last.TrigStr, spanBreak, spanBreak == 999999 ? "" : span.BreakTimeStr, last.Price, last.IntervalStr);
                                    }
                                }
                                else
                                {
                                    merged.Add((span.TrigTVal, span.TrigTimeStr, spanBreak, span.BreakTimeStr, span.TrigPrice, span.IntervalStr));
                                }
                            }
                        }
                        
                        foreach (var m in merged)
                        {
                            if (m.Trig < 999999)
                                timeline.Add((m.Trig, m.TrigStr, kvp.Key.Type, 1, m.Price, $"{m.IntervalStr} 分 K"));
                            
                            if (m.Break < 999999)
                            {
                                int.TryParse(kvp.Key.StopLossVal, out int sl);
                                timeline.Add((m.Break, m.BreakStr, kvp.Key.Type, -1, sl, "即時破位"));
                            }
                        }
                    }
                    
                    timeline.Sort((a, b) => a.TVal.CompareTo(b.TVal));
                    
                    int computedLongCount = 0;
                    int computedShortCount = 0;
                    int computedDir = 0;
                    var computedHistory = new List<TrendEvent>();
                    
                    foreach (var ev in timeline)
                    {
                        if (ev.Type == "K高") computedLongCount += ev.Delta;
                        if (ev.Type == "K低") computedShortCount += ev.Delta;
                        
                        int newDir = computedDir;
                        if (computedLongCount > computedShortCount) newDir = 1;
                        else if (computedShortCount > computedLongCount) newDir = -1;
                        else if (computedLongCount == 0 && computedShortCount == 0) newDir = 0;
                        
                        if (newDir != computedDir)
                        {
                            computedDir = newDir;
                            if (computedDir != 0)
                            {
                                computedHistory.Add(new TrendEvent
                                {
                                    Direction = computedDir,
                                    LongCount = computedLongCount,
                                    ShortCount = computedShortCount,
                                    EstablishedTime = FormatTimeStr(ev.TimeStr),
                                    EstablishedPrice = ev.Price,
                                    Reason = ev.Reason
                                });
                                // Keep memory bounded if there are insane amounts of flips
                                if (computedHistory.Count > 1000) computedHistory.RemoveAt(0);
                            }
                        }
                    }

                    // 取得目前最新小台價格
                    string currentPrice = "N/A";
                    string tradeTimeStr = "N/A";
                    if (sessionDataSnapshot.Count > 0)
                    {
                        for (int i = sessionDataSnapshot.Count - 1; i >= 0; i--)
                        {
                            var trades = sessionDataSnapshot[i].Trades;
                            if (trades.Count > 0)
                            {
                                currentPrice = trades[^1].Price.ToString();
                                tradeTimeStr = trades[^1].Time;
                                break;
                            }
                        }
                    }

                    // 安全 Dispatch 回 UI 執行緒刷新渲染
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Interlocked.Read(ref _currentCheckId) != myRunId) return;

                        _currentUnbrokenMap = tempUnbrokenMap;
                        _currentUnbrokenTimeMap = tempTimeMap;
                        UpdateUI(currentPrice, tradeTimeStr, computedHistory, computedDir);
                    }));
                }
                catch (Exception)
                {
                    // Ignore or log internally
                }
                finally
                {
                    if (Interlocked.Read(ref _currentCheckId) == myRunId)
                    {
                        _bgCheckInProgress = false;
                    }
                }
            });
        }

        /// <summary>
        /// Tick 行情跳動時 O(1) 穿價破位即時剔除。
        /// </summary>
        public void CheckInstantUnbrokenBreakout(double price, string tradeTimeStr = "")
        {
            if (_currentUnbrokenMap.Count == 0) return;

            var brokenKeys = new List<(string Type, string Price)>();

            lock (_lock)
            {
                foreach (var kvp in _currentUnbrokenMap)
                {
                    var (sigType, stopLossVal) = kvp.Key;
                    if (double.TryParse(stopLossVal, out double slPrice))
                    {
                        if (sigType == "low" && price >= slPrice)
                        {
                            brokenKeys.Add(kvp.Key);
                        }
                        else if (sigType == "high" && price <= slPrice)
                        {
                            brokenKeys.Add(kvp.Key);
                        }
                    }
                }

                if (brokenKeys.Count > 0)
                {
                    foreach (var k in brokenKeys)
                    {
                        _currentUnbrokenMap.Remove(k);
                    }
                    UpdateUI(price.ToString(), tradeTimeStr);
                }
            }
        }

        private string FormatTimeStr(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 6) return t;
            if (t.Contains(':')) return t;
            return $"{t[..2]}:{t[2..4]}:{t[4..6]}";
        }

        private double ParseTimeStr(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "N/A") return 999999;
            string digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length < 6) return 999999;
            string hStr = digits.Substring(0, 2);
            string mStr = digits.Substring(2, 2);
            string sStr = digits.Substring(4, 2);
            if (int.TryParse(hStr, out int h) && int.TryParse(mStr, out int m) && int.TryParse(sStr, out int s))
            {
                double t = h * 3600 + m * 60 + s;
                if (t <= 18000) t += 86400;
                return t;
            }
            return 999999;
        }

        /// <summary>
        /// 主執行緒 UI 著色與排版更新。
        /// 智慧快取滾動條位置，防止更新文字時畫面抖動跳躍。
        /// </summary>
        private void UpdateUI(string currentPrice, string tradeTimeStr = "", List<TrendEvent>? newHistory = null, int? newDir = null)
        {
            // 1. 快取滾動條位置
            double vOffset = txtDisplay.VerticalOffset;
            double hOffset = txtDisplay.HorizontalOffset;

            // 趨勢歷史表單智慧捲動偵測
            double trendVOffset = txtTrendHistory.VerticalOffset;
            double trendHOffset = txtTrendHistory.HorizontalOffset;
            bool isTrendAtBottom = (txtTrendHistory.VerticalOffset + txtTrendHistory.ViewportHeight >= txtTrendHistory.ExtentHeight - 5.0) || txtTrendHistory.ExtentHeight < 1.0;

            int displayPrice = 0;
            if (double.TryParse(currentPrice, out double p))
            {
                displayPrice = (int)Math.Round(p);
            }

            string displayTimeStr = string.IsNullOrEmpty(tradeTimeStr) || tradeTimeStr == "N/A" 
                ? DateTime.Now.ToString("HH:mm:ss") 
                : FormatTimeStr(tradeTimeStr);

            lblTitle.Text = $"🛡️ 未破分 K 停損監控 | 目前時間：{displayTimeStr} | 價位: {(displayPrice > 0 ? displayPrice.ToString() : currentPrice)}";

            txtDisplay.Document.Blocks.Clear();

            var shortEntries = new List<(int count, string price, string intervalsStr, string timeStr)>();
            var longEntries = new List<(int count, string price, string intervalsStr, string timeStr)>();

            lock (_lock)
            {
                foreach (var kvp in _currentUnbrokenMap)
                {
                    var (sigType, priceVal) = kvp.Key;
                    var sortedIntervals = kvp.Value.ToList();
                    sortedIntervals.Sort();
                    string intervalsStr = string.Join("、", sortedIntervals);
                    string timeStr = _currentUnbrokenTimeMap.TryGetValue(kvp.Key, out var t) ? t : "";

                    if (sigType == "low")
                    {
                        shortEntries.Add((kvp.Value.Count, priceVal, intervalsStr, timeStr));
                    }
                    else if (sigType == "high")
                    {
                        longEntries.Add((kvp.Value.Count, priceVal, intervalsStr, timeStr));
                    }
                }
            }

            // 100% 遵守原版排序：停損價格從高到低 (由大到小) 排序
            shortEntries.Sort((x, y) =>
            {
                double.TryParse(x.price, out double px);
                double.TryParse(y.price, out double py);
                return py.CompareTo(px);
            });

            longEntries.Sort((x, y) =>
            {
                double.TryParse(x.price, out double px);
                double.TryParse(y.price, out double py);
                return py.CompareTo(px);
            });

            // 更新統計顯示條
            lblSummaryShort.Text = $"做空共有 {shortEntries.Count} 項";
            lblSummaryLong.Text = $"做多共有 {longEntries.Count} 項";

            // 增量填入 Paragraph，並且對多/空標題進行發光著色
            // 使用單一 Paragraph 且設定 Margin=0 以精準控制換行距離
            var pContent = new Paragraph { Margin = new Thickness(0) };

            if (shortEntries.Count > 0)
            {
                var run = new Run($"═══ 做空（觀察 K 低） 共 {shortEntries.Count} 項 ═══\n")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69)), // 亮綠
                    FontWeight = FontWeights.Bold
                };
                pContent.Inlines.Add(run);

                foreach (var item in shortEntries)
                {
                    pContent.Inlines.Add(new Run($"  停損價: {item.price}  未破: {item.intervalsStr} 分K  ({item.timeStr})\n"));
                }
            }

            if (longEntries.Count > 0)
            {
                if (shortEntries.Count > 0)
                {
                    pContent.Inlines.Add(new Run("\n")); // 做空與做多之間空一行
                }
                var run = new Run($"═══ 做多（觀察 K 高） 共 {longEntries.Count} 項 ═══\n")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(235, 75, 75)), // 亮紅
                    FontWeight = FontWeights.Bold
                };
                pContent.Inlines.Add(run);

                foreach (var item in longEntries)
                {
                    pContent.Inlines.Add(new Run($"  停損價: {item.price}  未破: {item.intervalsStr} 分K  ({item.timeStr})\n"));
                }
            }

            if (shortEntries.Count == 0 && longEntries.Count == 0)
            {
                pContent.Inlines.Add(new Run("所有分 K 的停損價均已顯示「已破」或目前無觀察訊號。") { Foreground = Brushes.Gray });
            }

            txtDisplay.Document.Blocks.Add(pContent);

            // --- 趨勢方向表單邏輯 ---
            if (newHistory != null && newDir != null)
            {
                // 防止舊的背景計算快照覆蓋了 UI 執行緒的樂觀更新
                bool isStale = false;
                if (_trendHistory.Count > 0 && newHistory.Count > 0)
                {
                    double currentLatest = ParseTimeStr(_trendHistory[^1].EstablishedTime);
                    double incomingLatest = ParseTimeStr(newHistory[^1].EstablishedTime);
                    if (incomingLatest < currentLatest)
                    {
                        isStale = true;
                    }
                }

                if (!isStale)
                {
                    int oldDir = _trendDirection;
                    _trendDirection = newDir.Value;
                    _trendHistory.Clear();
                    _trendHistory.AddRange(newHistory);

                    if (oldDir != _trendDirection && _trendDirection != 0 && newHistory.Count > 0)
                    {
                        var evt = newHistory[^1];
                        string dirStr = _trendDirection == 1 ? "多方 📈" : "空方 📉";
                        string op = _trendDirection == 1 ? (evt.LongCount > evt.ShortCount ? ">" : "=") : (evt.ShortCount > evt.LongCount ? ">" : "=");
                        string statusStr = _trendDirection == 1
                            ? $"做多 {evt.LongCount} 項 {op} 做空 {evt.ShortCount} 項"
                            : $"做空 {evt.ShortCount} 項 {op} 做多 {evt.LongCount} 項";
                        string cpStr = evt.EstablishedPrice.HasValue ? evt.EstablishedPrice.Value.ToString() : "--";
                        string reasonStr = !string.IsNullOrEmpty(evt.Reason) ? $" ({evt.Reason})" : "";
                        string msgTitle = oldDir == 0 ? "【趨勢確立】" : "【趨勢轉向】";
                        string msg = $"{msgTitle}{dirStr}\n" +
                                     $"時間：{evt.EstablishedTime}{reasonStr}\n" +
                                     $"觸發價位：{cpStr}\n" +
                                     $"未破狀態：{statusStr}";
                        
                        _parentApp?.PushTelegramMessage(msg);
                    }
                }
            }
            else
            {
                int shortCount = shortEntries.Count; // 做空
                int longCount = longEntries.Count;   // 做多
    
                int newDirection = _trendDirection;
                if (longCount > shortCount)
                {
                    newDirection = 1;
                }
                else if (shortCount > longCount)
                {
                    newDirection = -1;
                }
                else if (longCount == 0 && shortCount == 0)
                {
                    newDirection = 0;
                }
    
                if (newDirection != _trendDirection)
                {
                    int oldDir = _trendDirection;
                    _trendDirection = newDirection;
                    if (_trendDirection != 0)
                    {
                        int? currentP = int.TryParse(currentPrice, out int cp) ? cp : (int?)null;
                        var evt = new TrendEvent 
                        { 
                            Direction = _trendDirection, 
                            EstablishedTime = FormatTimeStr(tradeTimeStr),
                            EstablishedPrice = currentP,
                            LongCount = longCount,
                            ShortCount = shortCount,
                            Reason = "即時破位"
                        };
                        _trendHistory.Add(evt);
                        if (_trendHistory.Count > 1000) _trendHistory.RemoveAt(0);

                        // Trigger Telegram push
                        string dirStr = _trendDirection == 1 ? "多方 📈" : "空方 📉";
                        string op = _trendDirection == 1 ? (longCount > shortCount ? ">" : "=") : (shortCount > longCount ? ">" : "=");
                        string statusStr = _trendDirection == 1
                            ? $"做多 {longCount} 項 {op} 做空 {shortCount} 項"
                            : $"做空 {shortCount} 項 {op} 做多 {longCount} 項";
                        string cpStr = currentP.HasValue ? currentP.Value.ToString() : "--";
                        string reasonStr = !string.IsNullOrEmpty(evt.Reason) ? $" ({evt.Reason})" : "";
                        string msgTitle = oldDir == 0 ? "【趨勢確立】" : "【趨勢轉向】";
                        string msg = $"{msgTitle}{dirStr}\n" +
                                     $"時間：{evt.EstablishedTime}{reasonStr}\n" +
                                     $"觸發價位：{cpStr}\n" +
                                     $"未破狀態：{statusStr}";
                        
                        _parentApp?.PushTelegramMessage(msg);
                    }
                }
            }

            // 渲染趨勢歷史 (改用增量渲染，避免破壞選取反白與滾動狀態)
            int existingCount = txtTrendHistory.Document.Blocks.Count;
            bool hasNoDataPara = existingCount == 1 && ((txtTrendHistory.Document.Blocks.FirstBlock as Paragraph)?.Inlines.FirstInline as Run)?.Text == "-- 無資料 --";

            if (_trendHistory.Count == 0)
            {
                if (!hasNoDataPara)
                {
                    txtTrendHistory.Document.Blocks.Clear();
                    txtTrendHistory.Document.Blocks.Add(new Paragraph(new Run("-- 無資料 --") { Foreground = Brushes.Gray }));
                }
            }
            else
            {
                if (hasNoDataPara)
                {
                    txtTrendHistory.Document.Blocks.Clear();
                    existingCount = 0;
                }

                bool hasNewTrend = false;

                Paragraph CreateTrendParagraph(TrendEvent evt, string signature, bool isSelected)
                {
                    var para = new Paragraph { Margin = new Thickness(0, 0, 0, 4), Tag = signature };
                    para.Background = isSelected ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : Brushes.Transparent;
                    para.Cursor = System.Windows.Input.Cursors.Hand;

                    para.PreviewMouseLeftButtonDown += (s, e) =>
                    {
                        if (_selectedTrendTime == evt.EstablishedTime)
                        {
                            _selectedTrendTime = null;
                            para.Background = Brushes.Transparent;
                            _parentApp?.ClearChartCrosshair();
                        }
                        else
                        {
                            _selectedTrendTime = evt.EstablishedTime;
                            foreach (var block in txtTrendHistory.Document.Blocks)
                            {
                                if (block is Paragraph p) p.Background = Brushes.Transparent;
                            }
                            para.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
                            _parentApp?.FocusChartOnTime(evt.EstablishedTime, evt.EstablishedPrice, evt.Direction);
                        }
                        e.Handled = true;
                    };

                    if (evt.Direction == 1)
                    {
                        string op = evt.LongCount > evt.ShortCount ? ">" : "=";
                        string pStr = evt.EstablishedPrice.HasValue ? $" {evt.EstablishedPrice.Value}" : "";
                        string reasonStr = !string.IsNullOrEmpty(evt.Reason) ? $" ({evt.Reason})" : "";
                        para.Inlines.Add(new Run($"多方 做多 {evt.LongCount} 項 {op} 做空 {evt.ShortCount} 項 {evt.EstablishedTime}{pStr}{reasonStr}")
                        {
                            Foreground = new SolidColorBrush(Color.FromRgb(235, 75, 75))
                        });
                    }
                    else if (evt.Direction == -1)
                    {
                        string op = evt.ShortCount > evt.LongCount ? ">" : "=";
                        string pStr = evt.EstablishedPrice.HasValue ? $" {evt.EstablishedPrice.Value}" : "";
                        string reasonStr = !string.IsNullOrEmpty(evt.Reason) ? $" ({evt.Reason})" : "";
                        para.Inlines.Add(new Run($"空方 做空 {evt.ShortCount} 項 {op} 做多 {evt.LongCount} 項 {evt.EstablishedTime}{pStr}{reasonStr}")
                        {
                            Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69))
                        });
                    }
                    return para;
                }

                for (int i = 0; i < _trendHistory.Count; i++)
                {
                    var evt = _trendHistory[i];
                    string signature = $"{evt.EstablishedTime}_{evt.Direction}_{evt.LongCount}_{evt.ShortCount}_{evt.Reason}_{evt.EstablishedPrice}";
                    bool isSelected = _selectedTrendTime == evt.EstablishedTime;

                    if (i < existingCount)
                    {
                        var para = (Paragraph)txtTrendHistory.Document.Blocks.ElementAt(i);
                        string currentTag = para.Tag as string ?? "";
                        
                        if (currentTag != signature)
                        {
                            // 內容實質改變，取代舊行
                            var newPara = CreateTrendParagraph(evt, signature, isSelected);
                            txtTrendHistory.Document.Blocks.InsertBefore(para, newPara);
                            txtTrendHistory.Document.Blocks.Remove(para);
                        }
                        else
                        {
                            // 內容未變，僅更新選取背景（若有改變）
                            var expectedBg = isSelected ? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)) : Brushes.Transparent;
                            if (para.Background is SolidColorBrush solidBg && expectedBg is SolidColorBrush expectedSolid)
                            {
                                if (solidBg.Color != expectedSolid.Color) para.Background = expectedBg;
                            }
                            else
                            {
                                para.Background = expectedBg;
                            }
                        }
                    }
                    else
                    {
                        // 新增的行
                        var newPara = CreateTrendParagraph(evt, signature, isSelected);
                        txtTrendHistory.Document.Blocks.Add(newPara);
                        hasNewTrend = true;
                    }
                }

                // 移除多餘的行（如有）
                while (txtTrendHistory.Document.Blocks.Count > _trendHistory.Count)
                {
                    txtTrendHistory.Document.Blocks.Remove(txtTrendHistory.Document.Blocks.LastBlock);
                }

                // 如果有最新方向新增，強制標記為在底部，確保後續會捲動到最新的一列
                if (hasNewTrend)
                {
                    isTrendAtBottom = true;
                }
            }

            // 2. 還原滾動條位置
            txtDisplay.ScrollToVerticalOffset(vOffset);
            txtDisplay.ScrollToHorizontalOffset(hOffset);

            if (isTrendAtBottom)
            {
                txtTrendHistory.ScrollToEnd();
            }
            else
            {
                txtTrendHistory.ScrollToVerticalOffset(trendVOffset);
                txtTrendHistory.ScrollToHorizontalOffset(trendHOffset);
            }
        }

        /// <summary>
        /// 安全釋放資源（停止 Timer 並清空所有狀態）。
        /// 視窗關閉時呼叫。
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _currentUnbrokenMap.Clear();
            }
            txtDisplay.Document.Blocks.Clear();
            lblTitle.Text = "🛡️ 未破分 K 停損監控";
            lblSummaryShort.Text = "做空共有 0 項";
            lblSummaryLong.Text = "做多共有 0 項";
            
            _trendDirection = 0;
            _selectedTrendTime = null;
            _trendHistory.Clear();
            if (txtTrendHistory != null)
                txtTrendHistory.Document.Blocks.Clear();
        }

        /// <summary>
        /// 清空所有內部狀態與 UI 顯示，但保持 Timer 運行。
        /// 供「停止」按鈕呼叫，恢復到初始化剛完成的狀態。
        /// </summary>
        public void Clear()
        {
            Interlocked.Increment(ref _currentCheckId); // 取消任何執行中的背景更新
            lock (_lock)
            {
                _currentUnbrokenMap.Clear();
            }
            _bgCheckInProgress = false;
            txtDisplay.Document.Blocks.Clear();
            lblTitle.Text = "🛡️ 未破分 K 停損監控";
            lblSummaryShort.Text = "做空共有 0 項";
            lblSummaryLong.Text = "做多共有 0 項";
            
            _trendDirection = 0;
            _selectedTrendTime = null;
            _trendHistory.Clear();
            if (txtTrendHistory != null)
                txtTrendHistory.Document.Blocks.Clear();
        }

        private void MenuCopyDisplay_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var text = new System.Windows.Documents.TextRange(txtDisplay.Document.ContentStart, txtDisplay.Document.ContentEnd).Text;
            System.Windows.Clipboard.SetText(text);
        }

        private void MenuCopyTrend_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var text = new System.Windows.Documents.TextRange(txtTrendHistory.Document.ContentStart, txtTrendHistory.Document.ContentEnd).Text;
            System.Windows.Clipboard.SetText(text);
        }
    }
}
