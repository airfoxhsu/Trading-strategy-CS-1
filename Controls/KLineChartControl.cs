using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExtremeSignalAppCS.Models;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Run = System.Windows.Documents.Run;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace ExtremeSignalAppCS.Controls
{
    /// <summary>
    /// 內部 K 線 DX 預繪繪圖元件。
    /// 繼承自 FrameworkElement，以 OnRender 進行 DirectX GPU 級預繪與局部增量手繪。
    /// </summary>
    public class KLinePainter : FrameworkElement
    {
        private List<KlineBar> _candles = new();
        private double _minX, _maxX; // 當前 X 軸索引可見範圍
        private double _minY, _maxY; // 當前 Y 軸價格可見範圍

        // 預建 GPU 快取
        private Drawing? _historyDrawingCache;
        private int _cachedHistoryCount = -1;

        // 畫筆與畫刷快取 (優化記憶體分配)
        private readonly Pen _upPen = new(new SolidColorBrush(Color.FromRgb(235, 75, 75)), 1.5);
        private readonly Brush _upBrush = new SolidColorBrush(Color.FromArgb(120, 235, 75, 75));
        private readonly Pen _downPen = new(new SolidColorBrush(Color.FromRgb(40, 167, 69)), 1.5);
        private readonly Brush _downBrush = new SolidColorBrush(Color.FromArgb(120, 40, 167, 69));
        private readonly Pen _flatPen = new(new SolidColorBrush(Color.FromRgb(200, 200, 200)), 1.5);
        private readonly Pen _gridPen = new(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 0.8);

        public KLinePainter()
        {
            ClipToBounds = true; // 開啟邊界剪裁
            _upPen.Freeze();
            _upBrush.Freeze();
            _downPen.Freeze();
            _downBrush.Freeze();
            _flatPen.Freeze();
            _gridPen.Freeze();
        }

        public void SetData(List<KlineBar> candles, double minX, double maxX, double minY, double maxY)
        {
            _candles = candles;
            _minX = minX;
            _maxX = maxX;
            _minY = minY;
            _maxY = maxY;

            _historyDrawingCache = null; // 重置快取，強迫縮放平移時重構坐標

            InvalidateVisual(); // 重新繪製
        }

        /// <summary>
        /// 一次性將已收盤的歷史 K 棒編譯進 Drawing 快取中。
        /// 實現 GPU 貼圖硬件剪裁與極致流暢平移。
        /// </summary>
        private void GenerateHistoryDrawingCache(double w, double h)
        {
            if (_candles == null || _candles.Count <= 1)
            {
                _historyDrawingCache = null;
                _cachedHistoryCount = 0;
                return;
            }

            int historyCount = _candles.Count - 1; // 歷史收盤 K 棒
            var group = new DrawingGroup();
            
            using (var dc = group.Open())
            {
                for (int i = 0; i < historyCount; i++)
                {
                    var c = _candles[i];
                    double x = GetCanvasX(i, w);
                    double openY = GetCanvasY(c.Open, h);
                    double closeY = GetCanvasY(c.Close, h);
                    double highY = GetCanvasY(c.High, h);
                    double lowY = GetCanvasY(c.Low, h);
                    
                    double colW = w / Math.Max(1.0, _maxX - _minX);
                    double barW = colW * 0.6;

                    Pen pen = _flatPen;
                    Brush brush = Brushes.Transparent;

                    if (c.Tag == "up")
                    {
                        pen = _upPen;
                        brush = _upBrush;
                    }
                    else if (c.Tag == "down")
                    {
                        pen = _downPen;
                        brush = _downBrush;
                    }

                    // 畫最高/最低影線
                    dc.DrawLine(pen, new Point(x, highY), new Point(x, lowY));
                    
                    // 畫開收實體
                    double rectH = Math.Abs(closeY - openY);
                    if (rectH < 1.0) rectH = 1.0;
                    dc.DrawRectangle(brush, pen, new Rect(x - barW / 2, Math.Min(openY, closeY), barW, rectH));
                }
            }

            group.Freeze();
            _historyDrawingCache = group;
            _cachedHistoryCount = historyCount;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            double w = ActualWidth;
            double h = ActualHeight;

            if (w <= 0 || h <= 0 || _candles == null || _candles.Count == 0)
                return;

            // 建立物理硬體邊界剪裁矩形，杜絕任何 K 線外溢
            drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            try
            {
                // 1. 繪製背景暗黑网格線 (10% 透明度)
                DrawGridLines(drawingContext, w, h);

                // 2. 檢測歷史 K 棒數量是否改變，若新分 K 收盤則重建 GPU 預繪快取
                int historyCount = Math.Max(0, _candles.Count - 1);
                if (historyCount != _cachedHistoryCount || _historyDrawingCache == null)
                {
                    GenerateHistoryDrawingCache(w, h);
                }

                // 3. DirectX GPU 硬件貼圖重播歷史 K 線快取 (極速零負擔)
                if (_historyDrawingCache != null)
                {
                    drawingContext.DrawDrawing(_historyDrawingCache);
                }

                // 4. 局部增量手繪最後一根未收盤 K 棒
                int lastIdx = _candles.Count - 1;
                var last = _candles[lastIdx];
                double lx = GetCanvasX(lastIdx, w);
                double lopenY = GetCanvasY(last.Open, h);
                double lcloseY = GetCanvasY(last.Close, h);
                double lhighY = GetCanvasY(last.High, h);
                double llowY = GetCanvasY(last.Low, h);

                double colW = w / Math.Max(1.0, _maxX - _minX);
                double barW = colW * 0.6;

                Pen lpen = _flatPen;
                Brush lbrush = Brushes.Transparent;

                if (last.Tag == "up")
                {
                    lpen = _upPen;
                    lbrush = _upBrush;
                }
                else if (last.Tag == "down")
                {
                    lpen = _downPen;
                    lbrush = _downBrush;
                }

                drawingContext.DrawLine(lpen, new Point(lx, lhighY), new Point(lx, llowY));
                double lrectH = Math.Abs(lcloseY - lopenY);
                if (lrectH < 1.0) lrectH = 1.0;
                drawingContext.DrawRectangle(lbrush, lpen, new Rect(lx - barW / 2, Math.Min(lopenY, lcloseY), barW, lrectH));
            }
            finally
            {
                drawingContext.Pop(); // 釋放剪裁
            }
        }

        private void DrawGridLines(DrawingContext dc, double w, double h)
        {
            // 繪製 X 軸格線
            int gridCols = 8;
            for (int i = 1; i < gridCols; i++)
            {
                double gx = (w / gridCols) * i;
                dc.DrawLine(_gridPen, new Point(gx, 0), new Point(gx, h));
            }

            // 繪製 Y 軸格線
            int gridRows = 6;
            for (int i = 1; i < gridRows; i++)
            {
                double gy = (h / gridRows) * i;
                dc.DrawLine(_gridPen, new Point(0, gy), new Point(w, gy));
            }
        }

        // 坐標對應映射方法 (與 PyQtGraph 映射概念等價)
        public double GetCanvasX(double index, double width)
        {
            double rangeX = _maxX - _minX;
            if (rangeX <= 0) rangeX = 1.0;
            return ((index - _minX) / rangeX) * width;
        }

        public double GetCanvasY(double price, double height)
        {
            double rangeY = _maxY - _minY;
            if (rangeY <= 0) rangeY = 1.0;
            // 頂端是 Y=0，底端是 Y=Height，所以要翻轉
            return (1.0 - (price - _minY) / rangeY) * height;
        }

        /// <summary>
        /// 重置快取。
        /// </summary>
        public void ResetCache()
        {
            _historyDrawingCache = null;
            _cachedHistoryCount = -1;
        }
    }

    /// <summary>
    /// 專業看盤 K 線圖表控制元件。
    /// 封裝 DirectX GPU 預繪繪圖層、獨立透明十字游標 Overlay 層與置頂不透明開高低收面板。
    /// </summary>
    public class KLineChartControl : Grid
    {
        private readonly KLinePainter _painter;
        private readonly CrosshairOverlay _crosshair;
        private readonly Border _infoPanel;
        private readonly TextBlock _infoText;

        private List<KlineBar> _candles = new();
        private double _minX, _maxX;
        private double _minY, _maxY;

        // 智慧操作鎖：偵測使用者是否手動 zoom/pan 過，防止行情跳動時強制 autoRange 重對焦
        private bool _isZoomedOrPanned;
        
        // 拖曳狀態
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartMinX, _dragStartMaxX;

        public KLineChartControl()
        {
            ClipToBounds = true; // 強制圖表大容器邊界剪裁
            // 1. 背景設為交易暗黑底色
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));

            // 2. 初始化 K 線 DX 繪圖層
            _painter = new KLinePainter();
            Children.Add(_painter);

            // 3. 初始化透明十字游標層
            _crosshair = new CrosshairOverlay();
            Children.Add(_crosshair);

            // 4. 初始化置頂開高低收面板 (完全不透明交易暗灰，科幻綠邊框)
            _infoText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                FontFamily = new FontFamily("Consolas, Microsoft JhengHei"),
                FontSize = 11,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap
            };

            _infoPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 255, 204)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6),
                Width = 160,
                Height = 120,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0),
                Child = _infoText,
                Visibility = Visibility.Collapsed
            };
            Children.Add(_infoPanel);

            // 5. 註冊滑鼠事件 (Zoom 與 Pan)
            MouseMove += KLineChartControl_MouseMove;
            MouseLeave += KLineChartControl_MouseLeave;
            MouseWheel += KLineChartControl_MouseWheel;
            MouseLeftButtonDown += KLineChartControl_MouseLeftButtonDown;
            MouseLeftButtonUp += KLineChartControl_MouseLeftButtonUp;
        }

        /// <summary>
        /// 更新並繪製 K 線，增量更新，免除銷毀重建物件開銷。
        /// </summary>
        public void UpdateCandles(List<KlineBar> klineData, bool forceAutoRange = false)
        {
            _candles = klineData;

            if (forceAutoRange)
            {
                _isZoomedOrPanned = false; // 解鎖，重開全自動對焦模式
            }

            if (_candles == null || _candles.Count == 0)
            {
                _infoPanel.Visibility = Visibility.Collapsed;
                _painter.SetData(new List<KlineBar>(), 0, 0, 0, 0);
                return;
            }

            // 智慧自動對焦
            if (forceAutoRange || !_isZoomedOrPanned)
            {
                AutoRange();
            }
            else
            {
                // 手動狀態下，僅重繪
                _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            }

            // 右上角預設更新顯示最新的一根 K棒
            ShowKlineInfo(_candles.Count - 1);
        }

        /// <summary>
        /// 全自動對焦 X/Y 軸 ViewRange (加上 5% 上下緩衝間距以確保視覺美觀)
        /// </summary>
        public void AutoRange()
        {
            if (_candles == null || _candles.Count == 0) return;

            // X軸對焦：寬度顯示所有 K棒 (索引區間為 [-1, count+1])
            _minX = -1.0;
            _maxX = _candles.Count + 1.0;

            // Y軸對焦：自動尋找此區間內的價格最高與最低
            double highest = _candles.Max(c => c.High);
            double lowest = _candles.Min(c => c.Low);
            double height = highest - lowest;
            if (height <= 0) height = 1.0;

            // 加上 5% 上下邊界緩衝
            _minY = lowest - height * 0.05;
            _maxY = highest + height * 0.05;

            _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
        }

        /// <summary>
        /// 手動調整 X 軸範圍時，智慧對焦 Y 軸可見價格高度。
        /// </summary>
        private void AutoRangeYForVisibleX()
        {
            if (_candles == null || _candles.Count == 0) return;

            int startIdx = (int)Math.Max(0, _minX);
            int endIdx = (int)Math.Min(_candles.Count - 1, _maxX);

            if (startIdx > endIdx)
            {
                startIdx = 0;
                endIdx = _candles.Count - 1;
            }

            double highest = -999999;
            double lowest = 999999;

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (_candles[i].High > highest) highest = _candles[i].High;
                if (_candles[i].Low < lowest) lowest = _candles[i].Low;
            }

            if (highest == -999999 || lowest == 999999)
                return;

            double height = highest - lowest;
            if (height <= 0) height = 1.0;

            _minY = lowest - height * 0.05;
            _maxY = highest + height * 0.05;
        }

        /// <summary>
        /// 將指定 K棒 資料渲染成 HTML/簡潔文字並呈現在右上角不透明面板上。
        /// </summary>
        private void ShowKlineInfo(int index)
        {
            if (_candles == null || index < 0 || index >= _candles.Count)
            {
                _infoPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var c = _candles[index];
            _infoText.Inlines.Clear();
            
            // 標題 (時間)
            _infoText.Inlines.Add(new Run($"📊 {c.TimeLabel}\n") { Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 204)), FontWeight = FontWeights.Bold });
            
            // 開盤
            _infoText.Inlines.Add(new Run("開："));
            _infoText.Inlines.Add(new Run($"{c.Open}\n") { FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            
            // 最高
            _infoText.Inlines.Add(new Run("高："));
            _infoText.Inlines.Add(new Run($"{c.High}\n") { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(235, 75, 75)) });
            
            // 最低
            _infoText.Inlines.Add(new Run("低："));
            _infoText.Inlines.Add(new Run($"{c.Low}\n") { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(40, 167, 69)) });
            
            // 收盤
            _infoText.Inlines.Add(new Run("收："));
            _infoText.Inlines.Add(new Run($"{c.Close}") { FontWeight = FontWeights.Bold, Foreground = Brushes.White });

            _infoPanel.Visibility = Visibility.Visible;
        }

        // ==================== 互動事件 (滑鼠 Drag & Wheel) ====================

        private void KLineChartControl_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_candles == null || _candles.Count == 0) return;

            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartMinX = _minX;
            _dragStartMaxX = _maxX;
            CaptureMouse();
            
            _isZoomedOrPanned = true; // 鎖定手動狀態
        }

        private void KLineChartControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();
            }
        }

        private void KLineChartControl_MouseMove(object sender, MouseEventArgs e)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0 || _candles == null || _candles.Count == 0) return;

            Point mousePoint = e.GetPosition(this);

            // 1. 拖曳平移 (Pan)
            if (_isDragging)
            {
                double deltaX = mousePoint.X - _dragStartPoint.X;
                double colW = w / (_dragStartMaxX - _dragStartMinX);
                double indexShift = deltaX / colW;

                _minX = _dragStartMinX - indexShift;
                _maxX = _dragStartMaxX - indexShift;

                // 平移時同步智慧對焦 Y 軸可見價格高度，使拖曳平移絕不失焦
                AutoRangeYForVisibleX();

                _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            }

            // 2. 十字游標物理移動
            _crosshair.SetMousePos(mousePoint);

            // 3. 計算滑鼠所在的 K棒 index
            double rangeX = _maxX - _minX;
            double relativeX = mousePoint.X / w;
            int hoverIndex = (int)(relativeX * rangeX + _minX);

            if (hoverIndex >= 0 && hoverIndex < _candles.Count)
            {
                ShowKlineInfo(hoverIndex);
            }
            else
            {
                ShowKlineInfo(_candles.Count - 1);
            }
        }

        private void KLineChartControl_MouseLeave(object sender, MouseEventArgs e)
        {
            // 清空十字游標，資訊面板回歸最新一根 K棒
            _crosshair.SetMousePos(null);
            if (_candles != null && _candles.Count > 0)
            {
                ShowKlineInfo(_candles.Count - 1);
            }
        }

        private void KLineChartControl_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_candles == null || _candles.Count == 0) return;

            _isZoomedOrPanned = true; // 鎖定手動狀態

            double mouseX = e.GetPosition(this).X;
            double w = ActualWidth;
            double rangeX = _maxX - _minX;
            
            // 找出滑鼠對焦在圖表上的 K 棒索引位置 (作為縮放中心點)
            double mouseIndex = (mouseX / w) * rangeX + _minX;

            // 縮放乘數
            double zoomFactor = e.Delta > 0 ? 0.85 : 1.15; // 滾輪往前放大，往後縮小

            double newRange = rangeX * zoomFactor;
            // 限制最大/最小縮放寬度
            if (newRange < 5.0) newRange = 5.0;
            if (newRange > _candles.Count * 2.0) newRange = _candles.Count * 2.0;

            // 以滑鼠中心點進行寬度縮放比例配置
            double leftRatio = (mouseIndex - _minX) / rangeX;
            _minX = mouseIndex - newRange * leftRatio;
            _maxX = _minX + newRange;

            // 縮放時同步對焦價格 Y 軸
            AutoRangeYForVisibleX();

            _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
        }

        /// <summary>
        /// 安全釋放快取，防止 Zombie 殘留。
        /// </summary>
        public void Reset()
        {
            _painter.ResetCache();
            _candles.Clear();
            _isZoomedOrPanned = false;
            _infoPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 將圖表視界中心平移對焦到指定 K 線，並更新右上角面板。
        /// </summary>
        public void FocusCandle(int index)
        {
            if (_candles == null || index < 0 || index >= _candles.Count) return;
            
            _isZoomedOrPanned = true; // 鎖定手動狀態，免除跳動時重對焦
            ShowKlineInfo(index);
            
            double colRange = _maxX - _minX;
            _minX = index - colRange / 2.0;
            _maxX = index + colRange / 2.0;
            
            AutoRangeYForVisibleX();
            _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
        }

        /// <summary>
        /// 為了對應 PyQtGraph 屬性式呼叫的 Dummy ViewBox 屬性。
        /// </summary>
        public DummyViewBox plot_widget => new();
    }

    /// <summary>
    /// 對齊 PyQtGraph 程式碼的 Dummy ViewBox 結構。
    /// </summary>
    public class DummyViewBox
    {
        public DummyViewBox plotItem => new();
        public DummyViewBox vb => new();
        public DummyViewBox getViewBox() => this;
        public void enableAutoRange() { }
    }
}
