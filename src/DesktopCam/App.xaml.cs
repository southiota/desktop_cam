using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DesktopCam;

public partial class App : Application
{
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
