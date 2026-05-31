using System;

namespace ExtremeSignalAppCS.Helper
{
    /// <summary>
    /// 高精時間解析工具。
    /// 負責將交易所成交時間戳記轉換為累積秒數。
    /// </summary>
    public static class TimeParser
    {
        /// <summary>
        /// 解析時間字串為當日累積秒數 (支援交易所 12 位元高精微秒格式與 HH:mm:ss 格式)。
        /// </summary>
        public static double ParseTime(string timeStr)
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
        /// 將當日累積秒數格式化為交易員易讀的 HH:mm:ss 字串。
        /// </summary>
        public static string FormatTime(double timeVal)
        {
            timeVal %= 86400.0;
            int h = (int)(timeVal / 3600);
            int m = (int)((timeVal % 3600) / 60);
            int s = (int)(timeVal % 60);
            return $"{h:D2}:{m:D2}:{s:D2}";
        }
    }
}
