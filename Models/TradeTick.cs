using System;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 唯讀高性能成交 Tick 資料結構。
    /// 宣告為 struct 以在 high-frequency 行情（每秒 10,000+ Ticks）下
    /// 完全在 Stack 上運行，杜絕 GC Heap 分配與垃圾回收暫停。
    /// </summary>
    public readonly struct TradeTick
    {
        /// <summary>
        /// 商品名稱 (如 "TXF" 或 "MXF")
        /// </summary>
        public string Symbol { get; }

        /// <summary>
        /// 行情時間字串 (例如 "08:45:00" 或 "084500000000")
        /// </summary>
        public string Time { get; }

        /// <summary>
        /// 當日累計秒數 (高精精確至微秒)
        /// </summary>
        public double TimeVal { get; }

        /// <summary>
        /// 成交價位
        /// </summary>
        public int Price { get; }

        /// <summary>
        /// 買賣雙邊主動方向 (Outer/Inner/Unknown)
        /// </summary>
        public TradeSide Side { get; }

        /// <summary>
        /// 當時最佳買價 (Best Bid Price)
        /// </summary>
        public int BestBp { get; }

        /// <summary>
        /// 當時最佳賣價 (Best Ask Price)
        /// </summary>
        public int BestSp { get; }

        /// <summary>
        /// 交易盤別 ("日盤" 或 "夜盤")
        /// </summary>
        public string Session { get; }

        /// <summary>
        /// 初始化唯讀成交 Tick 實例。
        /// </summary>
        public TradeTick(string symbol, string time, double timeVal, int price, TradeSide side, int bestBp = 0, int bestSp = 0, string session = "")
        {
            Symbol = symbol;
            Time = time;
            TimeVal = timeVal;
            Price = price;
            Side = side;
            BestBp = bestBp;
            BestSp = bestSp;
            Session = session;
        }
    }
}
