using System;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// K 線棒資料模型。
    /// 用於聚合分 K 資料，供 WPF DataGrid 表格資料綁定及 KLineChartControl 原生硬體加速繪製。
    /// </summary>
    public class KlineBar
    {
        /// <summary>
        /// 時間標籤 (如 "08:45~09:15")
        /// </summary>
        public string TimeLabel { get; set; } = string.Empty;

        /// <summary>
        /// 最高價
        /// </summary>
        public double High { get; set; }

        /// <summary>
        /// 最低價
        /// </summary>
        public double Low { get; set; }

        /// <summary>
        /// 開盤價
        /// </summary>
        public double Open { get; set; }

        /// <summary>
        /// 收盤價
        /// </summary>
        public double Close { get; set; }

        /// <summary>
        /// 即時分析聚合所得的訊號標記字串
        /// </summary>
        public string Signals { get; set; } = string.Empty;

        /// <summary>
        /// 突破上高文字 (如 "是"、"做多" 或 "")
        /// </summary>
        public string BreakHigh { get; set; } = string.Empty;

        /// <summary>
        /// 跌破上低文字 (如 "是"、"做空" 或 "")
        /// </summary>
        public string BreakLow { get; set; } = string.Empty;

        /// <summary>
        /// 漲跌標籤狀態 ("up" / "down" / "flat")，用以渲染紅/綠 K 棒與字體顏色
        /// </summary>
        public string Tag { get; set; } = "flat";

        /// <summary>
        /// 建立預設 K棒。
        /// </summary>
        public KlineBar() { }

        /// <summary>
        /// 建立並初始化 K棒。
        /// </summary>
        public KlineBar(string timeLabel, double high, double low, double open, double close, string signals, string breakHigh, string breakLow, string tag)
        {
            TimeLabel = timeLabel;
            High = high;
            Low = low;
            Open = open;
            Close = close;
            Signals = signals;
            BreakHigh = breakHigh;
            BreakLow = breakLow;
            Tag = tag;
        }

        /// <summary>
        /// 深拷貝一份 K棒。
        /// </summary>
        public KlineBar Clone()
        {
            return new KlineBar(TimeLabel, High, Low, Open, Close, Signals, BreakHigh, BreakLow, Tag);
        }
    }
}
