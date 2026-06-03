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

            // 1. 處理帶有冒號 `:` 的標準格式 (例如 "15:04:02.223")
            if (timeStr.Contains(":"))
            {
                try
                {
                    string[] parts = timeStr.Split('.');
                    string[] hms = parts[0].Split(':');
                    int h = int.Parse(hms[0]);
                    int m = int.Parse(hms[1]);
                    int s = int.Parse(hms[2]);
                    double ms = 0.0;
                    if (parts.Length > 1)
                    {
                        int msLen = parts[1].Length;
                        if (msLen == 3) // 毫秒
                            ms = double.Parse(parts[1]) / 1000.0;
                        else if (msLen == 6) // 微秒
                            ms = double.Parse(parts[1]) / 1000000.0;
                        else
                            ms = double.Parse(parts[1]) / Math.Pow(10, msLen);
                    }
                    return h * 3600 + m * 60 + s + ms;
                }
                catch { }
            }

            // 2. 處理純數字格式 (例如 "150405143000" 或 "095957612000")
            if (long.TryParse(timeStr, out _))
            {
                try
                {
                    // 智慧補零：若長度為 11 (如早上 9 點 93005143000)，自動補為 12 位
                    if (timeStr.Length == 11)
                    {
                        timeStr = "0" + timeStr;
                    }

                    if (timeStr.Length >= 6)
                    {
                        int h = int.Parse(timeStr.Substring(0, 2));
                        int m = int.Parse(timeStr.Substring(2, 2));
                        int s = int.Parse(timeStr.Substring(4, 2));
                        double ms = 0.0;

                        if (timeStr.Length == 8) // HHmmssff (百分秒)
                        {
                            ms = double.Parse(timeStr.Substring(6, 2)) / 100.0;
                        }
                        else if (timeStr.Length == 9) // HHmmssfff (毫秒)
                        {
                            ms = double.Parse(timeStr.Substring(6, 3)) / 1000.0;
                        }
                        else if (timeStr.Length == 12) // HHmmssffffff (微秒)
                        {
                            ms = double.Parse(timeStr.Substring(6, 6)) / 1000000.0;
                        }
                        else if (timeStr.Length > 6) // 其它長度純數字
                        {
                            int extraLen = timeStr.Length - 6;
                            ms = double.Parse(timeStr.Substring(6)) / Math.Pow(10, extraLen);
                        }

                        return h * 3600 + m * 60 + s + ms;
                    }
                }
                catch { }
            }

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
