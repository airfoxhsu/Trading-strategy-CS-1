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
        
        private readonly DispatcherTimer _timer;
        private bool _bgCheckInProgress;
        private double _lastTriggerTime;
        private readonly object _lock = new();

        // 快取當前未破停損價格 (Key: (Type[high/low], PriceStr) -> Value: 分K分鐘數集合)
        private Dictionary<(string Type, string Price), HashSet<int>> _currentUnbrokenMap = new();
        private Dictionary<(string Type, string Price), string> _currentUnbrokenTimeMap = new();

        public UnbrokenKMonitor()
        {
            InitializeComponent();

            // 1.5 秒更新一次 (防抖節流背景計算)
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _timer.Tick += (s, e) => TriggerUnbrokenCheck(false);
        }

        public void Initialize(TradingEngine engine, MainWindow parentApp)
        {
            _engine = engine;
            _parentApp = parentApp;
            
            _timer.Start();
        }

        /// <summary>
        /// 週期性或事件驅動觸發未破停損分析。
        /// </summary>
        public void TriggerUnbrokenCheck(bool force = false)
        {
            if (_engine == null || _parentApp == null) return;
            if (_bgCheckInProgress) return;

            double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            
            // 普通 Tick 行情跳動期間限制 1.5 秒 Debounce。如果是極值訊號達標，則 force = true 瞬間啟動背景 Thread 計算
            if (!force && (now - _lastTriggerTime < 1.5))
            {
                return;
            }

            _lastTriggerTime = now;
            _bgCheckInProgress = true;

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

                    foreach (var intervalStr in intervalsStr)
                    {
                        if (!int.TryParse(intervalStr, out int intMins))
                            continue;

                        // 微幅休眠 5ms 給 UI Thread 絕對流暢渲染優先權
                        Thread.Sleep(5);

                        foreach (var (sessionName, trades, txfSigs, mxfSigs) in sessionDataSnapshot)
                        {
                            // 背景非同步聚合分 K
                            var (klineData, _) = _engine.CalcKlineData(sessionName, trades, txfSigs, mxfSigs, intMins);

                            // 背景非同步跑停損狀態機模擬
                            var results = _engine.CalcSimulationResults(sessionName, trades, klineData, obsN);

                            foreach (var item in results)
                            {
                                if (item.Tags.Contains("history") || item.Tags.Contains("annotation"))
                                    continue;

                                string sigLabel = item.DisplayTitle;
                                string stopLossVal = item.StopLossDisplay;

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

                    // 取得目前最新小台價格
                    string currentPrice = "N/A";
                    if (sessionDataSnapshot.Count > 0)
                    {
                        for (int i = sessionDataSnapshot.Count - 1; i >= 0; i--)
                        {
                            var trades = sessionDataSnapshot[i].Trades;
                            if (trades.Count > 0)
                            {
                                currentPrice = trades[^1].Price.ToString();
                                break;
                            }
                        }
                    }

                    // 安全 Dispatch 回 UI 執行緒刷新渲染
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _currentUnbrokenMap = tempUnbrokenMap;
                        _currentUnbrokenTimeMap = tempTimeMap;
                        UpdateUI(currentPrice);
                    }));
                }
                catch (Exception ex)
                {
                    // Ignore or log internally
                }
                finally
                {
                    _bgCheckInProgress = false;
                }
            });
        }

        /// <summary>
        /// Tick 行情跳動時 O(1) 穿價破位即時剔除。
        /// </summary>
        public void CheckInstantUnbrokenBreakout(double price)
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
                    UpdateUI(price.ToString());
                }
            }
        }

        /// <summary>
        /// 主執行緒 UI 著色與排版更新。
        /// 智慧快取滾動條位置，防止更新文字時畫面抖動跳躍。
        /// </summary>
        private void UpdateUI(string currentPrice)
        {
            // 1. 快取滾動條位置
            double vOffset = txtDisplay.VerticalOffset;
            double hOffset = txtDisplay.HorizontalOffset;

            int displayPrice = 0;
            if (double.TryParse(currentPrice, out double p))
            {
                displayPrice = (int)Math.Round(p);
            }

            string nowStr = DateTime.Now.ToString("HH:mm:ss");
            lblTitle.Text = $"🛡️ 未破分 K 停損監控 | 目前時間：{nowStr} | 價位: {(displayPrice > 0 ? displayPrice.ToString() : currentPrice)}";

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

            // 增量填入 Paragraph，並且對多/空標題進行發光著色
            if (shortEntries.Count > 0)
            {
                var pHeader = new Paragraph();
                var run = new Run($"═══ 做空（觀察 K 低） 共 {shortEntries.Count} 項 ═══\n")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69)), // 亮綠
                    FontWeight = FontWeights.Bold
                };
                pHeader.Inlines.Add(run);

                foreach (var item in shortEntries)
                {
                    pHeader.Inlines.Add(new Run($"  停損價: {item.price}  未破: {item.intervalsStr} 分K  ({item.timeStr})\n"));
                }
                txtDisplay.Document.Blocks.Add(pHeader);
            }

            if (longEntries.Count > 0)
            {
                var pHeader = new Paragraph();
                if (shortEntries.Count > 0)
                {
                    pHeader.Inlines.Add(new Run("\n")); // 空行分隔
                }
                var run = new Run($"═══ 做多（觀察 K 高） 共 {longEntries.Count} 項 ═══\n")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(235, 75, 75)), // 亮紅
                    FontWeight = FontWeights.Bold
                };
                pHeader.Inlines.Add(run);

                foreach (var item in longEntries)
                {
                    pHeader.Inlines.Add(new Run($"  停損價: {item.price}  未破: {item.intervalsStr} 分K  ({item.timeStr})\n"));
                }
                txtDisplay.Document.Blocks.Add(pHeader);
            }

            if (shortEntries.Count == 0 && longEntries.Count == 0)
            {
                txtDisplay.Document.Blocks.Add(new Paragraph(new Run("所有分 K 的停損價均已顯示「已破」或目前無觀察訊號。") { Foreground = Brushes.Gray }));
            }

            // 2. 還原滾動條位置
            txtDisplay.ScrollToVerticalOffset(vOffset);
            txtDisplay.ScrollToHorizontalOffset(hOffset);
        }

        /// <summary>
        /// 安全釋放資源（停止 Timer 並清空所有狀態）。
        /// 視窗關閉時呼叫。
        /// </summary>
        public void Reset()
        {
            _timer.Stop();
            lock (_lock)
            {
                _currentUnbrokenMap.Clear();
            }
            txtDisplay.Document.Blocks.Clear();
            lblTitle.Text = "🛡️ 未破分 K 停損監控";
        }

        /// <summary>
        /// 清空所有內部狀態與 UI 顯示，但保持 Timer 運行。
        /// 供「停止」按鈕呼叫，恢復到初始化剛完成的狀態。
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _currentUnbrokenMap.Clear();
            }
            _bgCheckInProgress = false;
            _lastTriggerTime = 0;
            txtDisplay.Document.Blocks.Clear();
            lblTitle.Text = "🛡️ 未破分 K 停損監控";
        }
    }
}
