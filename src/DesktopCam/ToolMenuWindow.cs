using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 녹화 영역 안에서 Ctrl+우클릭으로 뜨는 메뉴: 대본 이전 줄 · 다음 줄, 커서 · 펜 · 형광펜 · 색 · 지우기.
/// 커서 오른쪽 아래에 뜨고, 고르거나 바깥을 누르거나 Esc 면 닫힌다. 녹화에 찍히지 않는다.
/// </summary>
public sealed class ToolMenuWindow : Window
{
    public record Item(string Text, string Keys, Action Run, bool Checked = false, bool GapBefore = false);

    /// <summary>고른 항목 (없으면 null). Closed 뒤에 읽는다.</summary>
    public Item? Chosen { get; private set; }
    bool _closing;

    public ToolMenuWindow(IEnumerable<Item> items)
    {
        Title = "Desktop CAM Menu";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(BackgroundProperty, "BgPanel");

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        };

        var list = new StackPanel { MinWidth = 190 };
        foreach (var it in items) list.Children.Add(Row(it));
        var border = new Border { Padding = new Thickness(4), Child = list };
        border.SetResourceReference(Border.BorderBrushProperty, "TealDeep");
        border.SetResourceReference(Border.BorderThicknessProperty, "EdgeW");
        Content = border;

        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
    }

    FrameworkElement Row(Item it)
    {
        var g = new Grid { Margin = new Thickness(0, it.GapBefore ? 5 : 0, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new TextBlock { Text = it.Checked ? "✓" : "", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        check.SetResourceReference(TextBlock.ForegroundProperty, "Teal");
        var text = new TextBlock { Text = it.Text, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "KoFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Cream");
        var keys = new TextBlock { Text = it.Keys, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(16, 0, 2, 0) };
        keys.SetResourceReference(TextBlock.FontFamilyProperty, "CodeFont");
        keys.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Grid.SetColumn(text, 1);
        Grid.SetColumn(keys, 2);
        g.Children.Add(check);
        g.Children.Add(text);
        g.Children.Add(keys);

        var row = new Border { Padding = new Thickness(6, 5, 6, 5), Background = Brushes.Transparent, Child = g, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "BgRaised");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { Chosen = it; SafeClose(); };
        return row;
    }

    void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>커서 오른쪽 아래에 띄운다 (화면 밖이면 안쪽으로).</summary>
    public void ShowAt(POINT p)
    {
        Show();
        UpdateLayout();
        double s = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;
        var (_, work) = GetPrimaryMonitor();
        double w = ActualWidth * s, h = ActualHeight * s;
        Left = Math.Min(p.X + 4, work.Right - w - 4) / s;
        Top = Math.Min(p.Y + 4, work.Bottom - h - 4) / s;
        Activate();
        ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);   // 훅 · 단축키에서 불려도 포커스를 받게
    }
}
