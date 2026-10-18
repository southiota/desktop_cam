using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DesktopCam;

public partial class App : Application
{
    /// <summary>
    /// DesktopCam.exe 가 있는 폴더. 한 파일 배포에서는 AppContext.BaseDirectory 가 임시 풀림 폴더라서,
    /// 사용자가 보는 models · tools 는 이 폴더 기준으로 찾는다.
    /// </summary>
    public static readonly string ExeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 두 번 실행되면 오버레이가 겹치므로 하나만
        _single = new Mutex(true, "DesktopCAM.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        StartupTrace.Mark("OnStartup");
        base.OnStartup(e);
        LoadPixelFont();

        var settings = Settings.Load();
        var overlay = new OverlayWindow();
        overlay.Show();
        StartupTrace.Mark("overlay shown");

        var panel = new PanelWindow(overlay, settings);
        MainWindow = panel;
        panel.Owner = overlay;   // 패널은 항상 오버레이 위에
        panel.Show();
        StartupTrace.Mark("panel.Show returned");
    }

    /// <summary>Fonts\PressStart2P-Regular.ttf 가 있으면 픽셀 서체로 쓴다. 없으면 Consolas.</summary>
    void LoadPixelFont()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fonts");
        if (File.Exists(Path.Combine(dir, "PressStart2P-Regular.ttf")))
            Resources["PixelFont"] = new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#Press Start 2P, Consolas");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
