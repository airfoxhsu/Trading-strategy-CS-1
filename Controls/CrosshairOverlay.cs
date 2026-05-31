using System;
using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace ExtremeSignalAppCS.Controls
{
    /// <summary>
    /// 高性能十字游標透明疊加畫布。
    /// 滑鼠事件穿透，將十字游標的繪製物理隔離於 K 線主圖表之外，
    /// 滑鼠高頻滑動時底層圖表無需任何重繪，實現 0 延遲與極致流暢。
    /// </summary>
    public class CrosshairOverlay : FrameworkElement
    {
        private System.Windows.Point? _mousePos;
        private readonly System.Windows.Media.Pen _sciFiPen;
        private readonly System.Windows.Media.Brush _sciFiBrush;
        private readonly System.Windows.Threading.DispatcherTimer _throttleTimer;

        public CrosshairOverlay()
        {
            // 滑鼠事件穿透，不干涉底層圖表的拖曳與縮放
            IsHitTestVisible = false;

            // 預先建立科幻綠 `#00ffcc` 畫筆與畫刷
            var brush = new SolidColorBrush(Color.FromRgb(0, 255, 204));
            brush.Freeze();
            _sciFiBrush = brush;

            var pen = new System.Windows.Media.Pen(brush, 1.2)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
            };
            pen.Freeze();
            _sciFiPen = pen;

            // 16ms 節流定時器 (≈60FPS 上限)，合併高頻滑鼠事件，消滅 Repaint Storm
            _throttleTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _throttleTimer.Tick += (s, e) =>
            {
                _throttleTimer.Stop();
                InvalidateVisual(); // 觸發 WPF 的 OnRender
            };
        }

        /// <summary>
        /// 設定目前滑鼠座標，透過 16ms 節流合併重繪。
        /// </summary>
        public void SetMousePos(Point? pos)
        {
            _mousePos = pos;
            
            if (!_throttleTimer.IsEnabled)
            {
                _throttleTimer.Start();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (!_mousePos.HasValue)
                return;

            double w = ActualWidth;
            double h = ActualHeight;
            double x = _mousePos.Value.X;
            double y = _mousePos.Value.Y;

            // 畫十字科幻綠虛線
            drawingContext.DrawLine(_sciFiPen, new Point(x, 0), new Point(x, h));
            drawingContext.DrawLine(_sciFiPen, new Point(0, y), new Point(w, y));

            // 於交叉點繪製一個實心科技小圓點
            drawingContext.DrawEllipse(_sciFiBrush, null, new Point(x, y), 3, 3);
        }
    }
}
