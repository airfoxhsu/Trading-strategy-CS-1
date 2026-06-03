using System;
using System.Collections.Generic;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 停損回測狀態機之單筆觀測結果。
    /// 用於「極值觀測表 DataGrid」的直接資料繫結，並快取歷史狀態以實現增量更新與點選連動。
    /// </summary>
    public class SimulationResult
    {
        // 唯讀快取 Key，用於去重 (Price, ATime, ObsN)
        public (int Price, string ATime, int ObsN) ConfirmedKey => (BestAPrice, BestATime, ObsN);

        /// <summary>
        /// 顯示標籤 (如 "N=25 觀察K低 18452")
        /// </summary>
        public string DisplayTitle { get; set; } = string.Empty;

        /// <summary>
        /// A 點極值發生的時間
        /// </summary>
        public string BestATime { get; set; } = string.Empty;

        /// <summary>
        /// A 點極端價位
        /// </summary>
        public int BestAPrice { get; set; }

        /// <summary>
        /// B 點訊號確立觸發時間
        /// </summary>
        public string TrigTime { get; set; } = "N/A";

        public string BestATimeDisplay => FormatTimeStr(BestATime);
        public string TrigTimeDisplay => FormatTimeStr(TrigTime);

        private string FormatTimeStr(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "N/A" || raw.Contains(":")) return raw;
            string padded = raw.PadLeft(12, '0');
            if (padded.Length >= 6)
                return $"{padded.Substring(0, 2)}:{padded.Substring(2, 2)}:{padded.Substring(4, 2)}";
            return raw;
        }

        /// <summary>
        /// B 點訊號確立觸發價位 (即進場價)
        /// </summary>
        public string TrigPrice { get; set; } = "N/A";

        /// <summary>
        /// A 點前向同盤 N 筆的平均間隔秒數 (顯示文字如 "0.2345s")
        /// </summary>
        public string Pre { get; set; } = "N/A";

        /// <summary>
        /// A 點後向同盤 N 筆的平均間隔秒數 (顯示文字如 "0.1234s")
        /// </summary>
        public string Post { get; set; } = "N/A";

        /// <summary>
        /// 停損價位的顯示字串 (如 "18400" 或 "18400(已破)")
        /// </summary>
        public string StopLossDisplay { get; set; } = "N/A";

        /// <summary>
        /// 渲染樣式標籤 (如 "obs_high"、"obs_low"、"history"、"annotation")
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        // --- WPF UI 繫結樣式屬性 ---
        public string ForegroundColor => Tags.Contains("up") || Tags.Contains("obs_low") ? "#EB4B4B" : 
                                         (Tags.Contains("down") || Tags.Contains("obs_high") ? "#28A745" : 
                                         (Tags.Contains("annotation") ? "#808080" : "#DCDCDC"));

        public string BackgroundColor => Tags.Contains("obs_k_low_highlight") ? "#1928A745" : // 10% 亮綠透明
                                         (Tags.Contains("obs_k_high_highlight") ? "#19EB4B4B" : // 10% 亮紅透明
                                         "Transparent");

        public string FontWeightVal => Tags.Contains("obs_high") || Tags.Contains("obs_low") || Tags.Contains("up") || Tags.Contains("down") ? "Bold" : "Normal";

        // --- 核心狀態追蹤屬性 (不直接綁定 DataGrid，但用於引擎計算與破位檢測) ---

        /// <summary>
        /// 訊號類型 ("K高" 或 "K低")
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// 觀察關卡價 (K低對應時段最高；K高對應時段最低)
        /// </summary>
        public int ObsEntry { get; set; }

        /// <summary>
        /// 前一根已收盤 K 棒的最高點 (做空停損防守)
        /// </summary>
        public int PrevHigh { get; set; }

        /// <summary>
        /// 前一根已收盤 K 棒的最低點 (做多停損防守)
        /// </summary>
        public int PrevLow { get; set; }

        /// <summary>
        /// B 點確立時的 Tick 索引 (用於時間軸已破檢測)
        /// </summary>
        public int BIndex { get; set; }

        /// <summary>
        /// 觀察 N 筆值
        /// </summary>
        public int ObsN { get; set; }

        /// <summary>
        /// 此停損點目前是否已在歷史上被突破跌破而失效
        /// </summary>
        public bool IsBroken { get; set; }

        /// <summary>
        /// 原始防守停損價
        /// </summary>
        public int StopLossPrice { get; set; }

        /// <summary>
        /// 極值點振幅
        /// </summary>
        public int AmpVal { get; set; }

        /// <summary>
        /// 建立空的 SimulationResult。
        /// </summary>
        public SimulationResult() { }

        /// <summary>
        /// 將目前狀態打包為 DataGrid 顯示所需的字串陣列 (對照 Python row tuple)。
        /// </summary>
        public string[] ToRowArray()
        {
            return new string[]
            {
                DisplayTitle,
                BestATimeDisplay,
                BestAPrice.ToString(),
                TrigTimeDisplay,
                TrigPrice,
                Pre,
                Post,
                StopLossDisplay
            };
        }
    }
}
