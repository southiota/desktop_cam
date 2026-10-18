using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopCam;

/// <summary>
/// 확인용: 환경 변수 DESKTOPCAM_CHECK_CLIP=1 이면 창의 모든 글자 칸을 훑어,
/// 글자가 칸보다 넓어서 잘리는 곳을 %TEMP%\desktopcam_clip.txt 에 적는다. 평소에는 아무 일도 하지 않는다.
/// 일부러 말줄임(…)하는 칸(창 제목·폴더 경로·장치 이름 등)은 "의도"로 따로 표시한다.
/// </summary>
public static class ClipCheck
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DESKTOPCAM_CHECK_CLIP") == "1";
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "desktopcam_clip.txt");

    public static void Run(Window w, string label)
    {
        if (!Enabled) return;
        w.Dispatcher.BeginInvoke(() =>
        {
            var sb = new StringBuilder($"== {label} {DateTime.Now:HH:mm:ss}\n");
            int bad = 0;
            foreach (var tb in Walk(w).OfType<TextBlock>())
            {
                if (!tb.IsVisible || tb.ActualWidth <= 0 || string.IsNullOrEmpty(tb.Text)) continue;
                // 화면과 같은 방식(Display: 픽셀에 맞춘 글자 폭)으로 잰다
                var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black,
                    null, TextOptions.GetTextFormattingMode(tb), VisualTreeHelper.GetDpi(tb).PixelsPerDip);
                double need = tb.TextWrapping == TextWrapping.NoWrap ? ft.WidthIncludingTrailingWhitespace : 0;
                bool cut = need > tb.ActualWidth + 0.5;
                if (tb.TextWrapping != TextWrapping.NoWrap)
                {
                    ft.MaxTextWidth = Math.Max(1, tb.ActualWidth);
                    cut = ft.Height > tb.ActualHeight + 1;
                }
                if (!cut) continue;
                bool intended = tb.TextTrimming != TextTrimming.None;
                if (!intended) bad++;
                sb.AppendLine($"{(intended ? "  의도(…)" : "  잘림!! ")} \"{Short(tb.Text)}\" 필요 {need:0} > 칸 {tb.ActualWidth:0}  [{tb.Name}]");
            }
            sb.AppendLine($"  → 잘림 {bad}곳");
            File.AppendAllText(LogPath, sb.ToString());
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Walk(c)) yield return d;
        }
    }
}
