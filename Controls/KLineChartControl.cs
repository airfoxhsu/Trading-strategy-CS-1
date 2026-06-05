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
        private List<KlineBar> _candles = [];
        private double _minX, _maxX; // 當前 X 軸索引可見範圍
        private double _minY, _maxY; // 當前 Y 軸價格可見範圍

        // 預建 GPU 快取
        private Drawing? _historyDrawingCache;
        private int _cachedHistoryCount = -1;

        // 畫筆與畫刷快取 (優化記憶體分配)
        private readonly Pen _upPen = new(new SolidColorBrush(Color.FromRgb(235, 75, 75)), 1.5);
        private readonly Brush _upBrush = new SolidColorBrush(Color.FromRgb(235, 75, 75));
        private readonly Pen _downPen = new(new SolidColorBrush(Color.FromRgb(40, 167, 69)), 1.5);
        private readonly Brush _downBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));
        private readonly Pen _flatPen = new(new SolidColorBrush(Color.FromRgb(200, 200, 200)), 1.5);
        private readonly Pen _gridPen = new(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 0.8);

        // 文字繪製快取
        private readonly Typeface _typeface = new(new FontFamily("Consolas, Microsoft JhengHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private readonly Brush _textBrush = new SolidColorBrush(Color.FromArgb(200, 220, 220, 220));
        private readonly Brush _highTextBrush = new SolidColorBrush(Color.FromRgb(235, 75, 75));
        private readonly Brush _lowTextBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));

        public KLinePainter()
        {
            ClipToBounds = true; // 開啟邊界剪裁
            _upPen.Freeze();
            _upBrush.Freeze();
            _downPen.Freeze();
            _downBrush.Freeze();
            _flatPen.Freeze();
            _gridPen.Freeze();
            _textBrush.Freeze();
            _highTextBrush.Freeze();
            _lowTextBrush.Freeze();
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
                // 1. 繪製背景暗黑网格線 (10% 透明度) 與左側 Y 軸刻度
                DrawGridLines(drawingContext, w, h);

                // 為 K 線繪製建立第二層剪裁，保護左側 Y 軸刻度不被 K 棒疊加
                drawingContext.PushClip(new RectangleGeometry(new Rect(LeftMargin, 0, Math.Max(1.0, w - LeftMargin), h)));
                try
                {
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

                // 5. 動態繪製可見範圍內的最高與最低價標示
                DrawVisibleHighLow(drawingContext, w, h);
                }
                finally
                {
                    drawingContext.Pop(); // 釋放 K 線專屬剪裁
                }
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

            // 繪製 Y 軸格線與左側價位文字
            int gridRows = 6;
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 1; i < gridRows; i++)
            {
                double gy = (h / gridRows) * i;
                dc.DrawLine(_gridPen, new Point(0, gy), new Point(w, gy));

                // 根據 Y 座標反推價格
                double rangeY = _maxY - _minY;
                double price = _maxY - (gy / h) * rangeY;

                var formattedText = new FormattedText(
                    price.ToString("F0"),
                    CultureInfo.GetCultureInfo("en-us"),
                    System.Windows.FlowDirection.LeftToRight,
                    _typeface,
                    11,
                    _textBrush,
                    pixelsPerDip);

                dc.DrawText(formattedText, new Point(5, gy - formattedText.Height - 2));
            }
        }

        private void DrawVisibleHighLow(DrawingContext dc, double w, double h)
        {
            if (_candles == null || _candles.Count == 0) return;

            int startIdx = (int)Math.Max(0, _minX);
            int endIdx = (int)Math.Min(_candles.Count - 1, _maxX);
            if (startIdx > endIdx) return;

            double highest = -999999;
            double lowest = 999999;
            int highIdx = -1;
            int lowIdx = -1;

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (_candles[i].High > highest)
                {
                    highest = _candles[i].High;
                    highIdx = i;
                }
                if (_candles[i].Low < lowest)
                {
                    lowest = _candles[i].Low;
                    lowIdx = i;
                }
            }

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (highIdx != -1)
            {
                double hx = GetCanvasX(highIdx, w);
                double hy = GetCanvasY(highest, h);
                var text = new FormattedText(
                    "← " + highest.ToString("F0"),
                    CultureInfo.GetCultureInfo("en-us"),
                    System.Windows.FlowDirection.LeftToRight,
                    _typeface,
                    12,
                    _highTextBrush,
                    pixelsPerDip);
                
                // 如果文字會超出右邊界，則改畫在左邊
                if (hx + 5 + text.Width > w)
                    dc.DrawText(text, new Point(hx - text.Width - 5, hy - text.Height / 2));
                else
                    dc.DrawText(text, new Point(hx + 5, hy - text.Height / 2));
            }

            if (lowIdx != -1)
            {
                double lx = GetCanvasX(lowIdx, w);
                double ly = GetCanvasY(lowest, h);
                var text = new FormattedText(
                    "← " + lowest.ToString("F0"),
                    CultureInfo.GetCultureInfo("en-us"),
                    System.Windows.FlowDirection.LeftToRight,
                    _typeface,
                    12,
                    _lowTextBrush,
                    pixelsPerDip);

                if (lx + 5 + text.Width > w)
                    dc.DrawText(text, new Point(lx - text.Width - 5, ly - text.Height / 2));
                else
                    dc.DrawText(text, new Point(lx + 5, ly - text.Height / 2));
            }
        }

        public const double LeftMargin = 55.0;

        public double GetCanvasX(double index, double width)
        {
            double rangeX = _maxX - _minX;
            if (rangeX <= 0) rangeX = 1.0;
            double drawWidth = Math.Max(1.0, width - LeftMargin);
            return LeftMargin + ((index - _minX) / rangeX) * drawWidth;
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

        private List<KlineBar> _candles = [];
        private double _minX, _maxX;
        private double _minY, _maxY;

        // 智慧操作鎖：偵測使用者是否手動 zoom/pan 過，防止行情跳動時強制 autoRange 重對焦
        private bool _isZoomedOrPanned;
        
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartMinX, _dragStartMaxX;

        // 狀態快取，防止 UpdateCandles 時強制蓋掉十字游標所指的 K 棒
        private bool _isMouseInChart;
        private int _lastHoverIndex = -1;
        private bool _isLockedCrosshair;
        private double _lockedCrosshairPrice;

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
                FontSize = 14,
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
                Width = 145,
                Height = 105,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0),
                Child = _infoText,
                Visibility = Visibility.Collapsed,
                Cursor = System.Windows.Input.Cursors.SizeAll // 提示可拖曳
            };

            var infoTransform = new TranslateTransform();
            _infoPanel.RenderTransform = infoTransform;

            bool isDraggingInfo = false;
            Point infoDragStartMouse = new();
            double infoDragStartX = 0;
            double infoDragStartY = 0;

            _infoPanel.MouseLeftButtonDown += (s, e) =>
            {
                isDraggingInfo = true;
                infoDragStartMouse = e.GetPosition(this);
                infoDragStartX = infoTransform.X;
                infoDragStartY = infoTransform.Y;
                _infoPanel.CaptureMouse();
                e.Handled = true; // 防止觸發底層的圖表平移
            };

            _infoPanel.MouseMove += (s, e) =>
            {
                if (isDraggingInfo)
                {
                    Point currentMouse = e.GetPosition(this);
                    double newX = infoDragStartX + (currentMouse.X - infoDragStartMouse.X);
                    double newY = infoDragStartY + (currentMouse.Y - infoDragStartMouse.Y);

                    double chartW = ActualWidth;
                    double chartH = ActualHeight;
                    double panelW = _infoPanel.ActualWidth > 0 ? _infoPanel.ActualWidth : 145;
                    double panelH = _infoPanel.ActualHeight > 0 ? _infoPanel.ActualHeight : 105;

                    // 限制只能在圖表範圍內移動，並且避開左側 Y 軸的刻度區 (LeftMargin)
                    double minX = KLinePainter.LeftMargin + 20 - chartW + panelW;
                    double maxX = 10;
                    double minY = -10;
                    double maxY = chartH - panelH - 10;

                    infoTransform.X = Math.Max(minX, Math.Min(newX, maxX));
                    infoTransform.Y = Math.Max(minY, Math.Min(newY, maxY));
                    e.Handled = true;
                }
            };

            _infoPanel.MouseLeftButtonUp += (s, e) =>
            {
                if (isDraggingInfo)
                {
                    isDraggingInfo = false;
                    _infoPanel.ReleaseMouseCapture();
                    e.Handled = true;
                }
            };

            Children.Add(_infoPanel);

            // 當圖表尺寸改變時 (如視窗最大化/還原)，確保資訊面板不會跑出邊界而消失
            this.SizeChanged += (s, e) =>
            {
                double chartW = ActualWidth;
                double chartH = ActualHeight;
                if (chartW <= 0 || chartH <= 0) return;

                double panelW = _infoPanel.ActualWidth > 0 ? _infoPanel.ActualWidth : 145;
                double panelH = _infoPanel.ActualHeight > 0 ? _infoPanel.ActualHeight : 105;

                double minX = KLinePainter.LeftMargin + 20 - chartW + panelW;
                double maxX = 10;
                double minY = -10;
                double maxY = chartH - panelH - 10;

                infoTransform.X = Math.Max(minX, Math.Min(infoTransform.X, maxX));
                infoTransform.Y = Math.Max(minY, Math.Min(infoTransform.Y, maxY));
            };

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
                _painter.SetData([], 0, 0, 0, 0);
                return;
            }

            // 智慧自動對焦
            if (forceAutoRange || !_isZoomedOrPanned)
            {
                AutoRange();
            }
            else
            {
                // 動態調節功能：當手動縮放時，如果新 K 棒超過右邊界，則跟隨平移 (X 軸保持比例平移)
                int lastIdx = _candles.Count - 1;
                // 只有當原本的右邊界已經包含或是非常接近最新的 K 棒時，才進行自動跟隨平移
                // 這樣如果使用者往左平移去查詢歷史，_maxX 會遠小於 lastIdx，就不會被強制拉回右邊。
                if (_maxX >= lastIdx - 0.5)
                {
                    if (lastIdx >= _maxX - 1.0)
                    {
                        double shift = (lastIdx + 1.5) - _maxX;
                        _minX += shift;
                        _maxX += shift;
                    }
                }

                // 動態調節功能：選項 A - 確保當前可見範圍內的 K 棒最高/最低價不會超出上下邊界 (Y 軸動態彈簧伸縮)
                AutoRangeYForVisibleX();

                _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            }

            if (_isLockedCrosshair && _lastHoverIndex >= 0 && _lastHoverIndex < _candles.Count)
            {
                double newX = _painter.GetCanvasX(_lastHoverIndex, ActualWidth);
                double newY = _painter.GetCanvasY(_lockedCrosshairPrice, ActualHeight);
                _crosshair.SetMousePos(new Point(newX, newY));
            }

            // 若滑鼠正停留在畫面上觀察，則保持顯示游標所指的 K 棒資訊，否則更新顯示最新的一根
            if (_isMouseInChart && _lastHoverIndex >= 0 && _lastHoverIndex < _candles.Count)
            {
                ShowKlineInfo(_lastHoverIndex);
            }
            else
            {
                ShowKlineInfo(_candles.Count - 1);
            }
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
        /// 解除手動鎖定並強制執行 AutoRange
        /// </summary>
        public void EnableAutoRange()
        {
            _isZoomedOrPanned = false;
            if (_candles != null && _candles.Count > 0)
            {
                AutoRange();
            }
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

        // 預建 Frozen 畫刷快取，供 ShowKlineInfo 高頻呼叫使用，消滅每次 new SolidColorBrush 的 GC 壓力
        private static readonly Brush _infoCyanBrush = CreateFrozenBrush(0, 255, 204);
        private static readonly Brush _infoRedBrush = CreateFrozenBrush(235, 75, 75);
        private static readonly Brush _infoGreenBrush = CreateFrozenBrush(40, 167, 69);

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 將指定 K棒 資料渲染成 HTML/簡潔文字並呈現在右上角不透明面板上。
        /// 使用預建 Frozen Brush 快取，避免高頻行情下大量 GC 分配。
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
            _infoText.Inlines.Add(new Run($"📊 {c.TimeLabel}\n") { Foreground = _infoCyanBrush, FontWeight = FontWeights.Bold });
            
            // 開盤
            _infoText.Inlines.Add(new Run("開："));
            _infoText.Inlines.Add(new Run($"{c.Open}\n") { FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            
            // 最高
            _infoText.Inlines.Add(new Run("高："));
            _infoText.Inlines.Add(new Run($"{c.High}\n") { FontWeight = FontWeights.Bold, Foreground = _infoRedBrush });
            
            // 最低
            _infoText.Inlines.Add(new Run("低："));
            _infoText.Inlines.Add(new Run($"{c.Low}\n") { FontWeight = FontWeights.Bold, Foreground = _infoGreenBrush });
            
            // 收盤
            Brush closeBrush = Brushes.White;
            if (c.Close > c.Open)
                closeBrush = _infoRedBrush;
            else if (c.Close < c.Open)
                closeBrush = _infoGreenBrush;

            _infoText.Inlines.Add(new Run("收："));
            _infoText.Inlines.Add(new Run($"{c.Close}") { FontWeight = FontWeights.Bold, Foreground = closeBrush });

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
                double colW = Math.Max(1.0, w - KLinePainter.LeftMargin) / (_dragStartMaxX - _dragStartMinX);
                double indexShift = deltaX / colW;

                _minX = _dragStartMinX - indexShift;
                _maxX = _dragStartMaxX - indexShift;

                // 平移時同步智慧對焦 Y 軸可見價格高度，使拖曳平移絕不失焦
                AutoRangeYForVisibleX();

                _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);
            }

            // 2. 十字游標物理移動
            _crosshair.SetMousePos(mousePoint);
            _isMouseInChart = true;
            _isLockedCrosshair = false;

            // 3. 計算滑鼠所在的 K棒 index
            double rangeX = _maxX - _minX;
            double drawWidth = Math.Max(1.0, w - KLinePainter.LeftMargin);
            double relativeX = (mousePoint.X - KLinePainter.LeftMargin) / drawWidth;
            double floatIndex = relativeX * rangeX + _minX;

            int nearestIndex = (int)Math.Round(floatIndex);
            double distanceFromCenter = Math.Abs(floatIndex - nearestIndex);

            // K 線實體寬度比例為 0.6 (即半徑 0.3)。當游標觸碰到 K 線實體邊緣時，才切換對焦的 K 棒。
            if (distanceFromCenter <= 0.3)
            {
                _lastHoverIndex = nearestIndex;
            }

            if (_lastHoverIndex >= 0 && _lastHoverIndex < _candles.Count)
            {
                ShowKlineInfo(_lastHoverIndex);
            }
            else
            {
                ShowKlineInfo(_candles.Count - 1);
            }
        }

        private void KLineChartControl_MouseLeave(object sender, MouseEventArgs e)
        {
            _isMouseInChart = false;
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
            double drawWidth = Math.Max(1.0, w - KLinePainter.LeftMargin);
            
            // 找出滑鼠對焦在圖表上的 K 棒索引位置 (作為縮放中心點)
            double mouseIndex = ((mouseX - KLinePainter.LeftMargin) / drawWidth) * rangeX + _minX;

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

            if (_isLockedCrosshair && _lastHoverIndex >= 0 && _lastHoverIndex < _candles.Count)
            {
                double newX = _painter.GetCanvasX(_lastHoverIndex, ActualWidth);
                double newY = _painter.GetCanvasY(_lockedCrosshairPrice, ActualHeight);
                _crosshair.SetMousePos(new Point(newX, newY));
            }
        }

        /// <summary>
        /// 安全釋放快取，防止 Zombie 殘留。
        /// </summary>
        public void Reset()
        {
            _painter.ResetCache();
            _candles.Clear();
            _isZoomedOrPanned = false;
            _isLockedCrosshair = false;
            _infoPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 將圖表視界中心平移對焦到指定 K 線，並更新右上角面板與十字游標。
        /// </summary>
        public void FocusCandle(int index, int? price = null)
        {
            if (_candles == null || index < 0 || index >= _candles.Count) return;
            
            _isZoomedOrPanned = true; // 鎖定手動狀態，免除跳動時重對焦
            ShowKlineInfo(index);
            
            double colRange = _maxX - _minX;
            if (colRange <= 0) colRange = 100; // 防呆

            // 計算比例 R，讓目標 K 棒精準出現在畫面的「物理正中心」(ActualWidth / 2)
            // 而非單純「繪圖區」的中心 (會偏移 LeftMargin / 2)
            double R = 0.5;
            if (ActualWidth > KLinePainter.LeftMargin * 2)
            {
                double drawWidth = ActualWidth - KLinePainter.LeftMargin;
                R = (ActualWidth / 2.0 - KLinePainter.LeftMargin) / drawWidth;
            }

            _minX = index - colRange * R;
            _maxX = index + colRange * (1.0 - R);
            
            AutoRangeYForVisibleX();
            _painter.SetData(_candles, _minX, _maxX, _minY, _maxY);

            // 同步十字游標到該 K 棒中心或指定價格
            double x = _painter.GetCanvasX(index, ActualWidth);
            double targetPrice = price ?? (_candles[index].Open + _candles[index].Close) / 2.0;
            _isLockedCrosshair = true;
            _lockedCrosshairPrice = targetPrice;
            double y = _painter.GetCanvasY(targetPrice, ActualHeight);
            _crosshair.SetMousePos(new Point(x, y));
            _lastHoverIndex = index;
            _isMouseInChart = true;
        }

        /// <summary>
        /// 清除十字游標與重置資訊面板
        /// </summary>
        public void ClearCrosshair()
        {
            _crosshair.SetMousePos(null);
            _isMouseInChart = false;
            _isLockedCrosshair = false;
            if (_candles != null && _candles.Count > 0)
            {
                ShowKlineInfo(_candles.Count - 1);
            }
            else
            {
                _infoPanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 為了對應 PyQtGraph 屬性式呼叫的 Dummy ViewBox 屬性。
        /// </summary>
        public static DummyViewBox PlotWidget => new();
    }

    /// <summary>
    /// 對齊 PyQtGraph 程式碼的 Dummy ViewBox 結構。
    /// </summary>
    public class DummyViewBox
    {
        public static DummyViewBox PlotItem => new();
        public static DummyViewBox Vb => new();
        public DummyViewBox GetViewBox() => this;
        public static void EnableAutoRange() { }
    }
}
