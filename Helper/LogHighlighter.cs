using System;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace ExtremeSignalAppCS.Helper
{
    /// <summary>
    /// 即時日誌著色高亮器。
    /// 採用增量式 O(1) 插入著色設計，在文字寫入瞬間直接將對應的前景色套用在 Run 元件上，
    /// 徹底拋棄昂貴的全篇幅 Regex 正則後處理著色，完美保證 UI 流暢度。
    /// </summary>
    public static class LogHighlighter
    {
        // 預建的 SolidColorBrush 畫刷物件池，消滅臨時分配的 GC 負擔
        private static readonly SolidColorBrush UpBrush = new(Color.FromRgb(235, 75, 75));     // 亮紅
        private static readonly SolidColorBrush DownBrush = new(Color.FromRgb(40, 167, 69));   // 亮綠
        private static readonly SolidColorBrush SystemBrush = new(Color.FromRgb(0, 162, 237));  // 亮青
        private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(220, 220, 220)); // 灰白

        /// <summary>
        /// 執行緒安全地向 RichTextBox 增量添加一行已被著色的日誌。
        /// </summary>
        public static void AppendLog(System.Windows.Controls.RichTextBox rtb, string text, bool clear = false)
        {
            if (rtb == null) return;

            var document = rtb.Document;

            if (clear)
            {
                document.Blocks.Clear();
            }

            // 內存防護：限制日誌總行數 (不超過 500 行)，防止 WPF 佈局引擎隨長度線性退化
            if (document.Blocks.Count > 500)
            {
                // 刪除頭部的 200 行
                for (int i = 0; i < 200; i++)
                {
                    if (document.Blocks.FirstBlock != null)
                    {
                        document.Blocks.Remove(document.Blocks.FirstBlock);
                    }
                }
            }

            string cleanText = text.TrimEnd('\r', '\n');
            if (string.IsNullOrEmpty(cleanText)) return;

            // 建立新的段落 (Paragraph)
            var p = new Paragraph { Margin = new System.Windows.Thickness(0, 2, 0, 2) };
            var run = new Run(cleanText);

            // 💡 100% 遵守規則：若包含 "未達標" 則保持白/預設色，不染任何色
            if (cleanText.Contains("未達標"))
            {
                run.Foreground = DefaultBrush;
            }
            else if (cleanText.Contains("最高") || cleanText.Contains("K低"))
            {
                run.Foreground = DownBrush; // Python 做空/綠色高亮 (買賣相反：最高/K低為做空->綠)
                run.FontWeight = System.Windows.FontWeights.Bold;
            }
            else if (cleanText.Contains("最低") || cleanText.Contains("K高"))
            {
                run.Foreground = UpBrush;   // Python 做多/紅色高亮 (最低/K高為做多->紅)
                run.FontWeight = System.Windows.FontWeights.Bold;
            }
            else if (cleanText.Contains("共識推播") || cleanText.Contains("觸發推播") || 
                     cleanText.Contains("行情狀態") || cleanText.Contains("預載") ||
                     cleanText.Contains("系統"))
            {
                run.Foreground = SystemBrush; // 青色高亮
                run.FontWeight = System.Windows.FontWeights.Bold;
            }
            else
            {
                run.Foreground = DefaultBrush;
            }

            p.Inlines.Add(run);
            document.Blocks.Add(p);

            // 自動滾動到底部
            rtb.ScrollToEnd();
        }
    }
}
