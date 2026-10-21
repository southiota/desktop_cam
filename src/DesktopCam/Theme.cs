using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DesktopCam;

/// <summary>
/// 색 테마: 시그니처(픽셀 서체 · 주사선 · teal) / 심플(일반 서체 · 단색 · 얇은 테두리).
/// App.xaml 의 첫 번째 합친 사전을 Themes\*.xaml 로 바꿔 끼운다. 모든 창이 테마 키를 DynamicResource 로 쓰므로
/// XAML 쪽은 바로 바뀌고, 코드에서 브러시를 기억해 두는 곳은 <see cref="Changed"/> 를 받아 다시 칠한다.
/// </summary>
public static class Theme
{
    public const string Signature = "Signature";
    public const string Simple = "Simple";

    /// <summary>테마가 바뀐 뒤 (UI 스레드). 코드에서 칠한 색을 다시 칠할 때 쓴다.</summary>
    public static event Action? Changed;

    public static string Current { get; private set; } = Signature;

    static FontFamily? _pixelFont;   // Fonts\PressStart2P-Regular.ttf (있으면)

    public static string Normalize(string? name) => name == Simple ? Simple : Signature;

    /// <summary>시작할 때 한 번: 픽셀 서체 파일이 있으면 시그니처 테마에서 쓴다.</summary>
    public static void LoadPixelFont()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fonts");
        if (File.Exists(Path.Combine(dir, "PressStart2P-Regular.ttf")))
            _pixelFont = new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#Press Start 2P, Consolas");
    }

    public static void Apply(string? name)
    {
        name = Normalize(name);
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute) };
        if (name == Signature && _pixelFont != null) dict["PixelFont"] = _pixelFont;

        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);

        bool changed = Current != name;
        Current = name;
        if (changed) Changed?.Invoke();
    }
}
