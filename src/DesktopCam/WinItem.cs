using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopCam;

/// <summary>패널의 창 목록 한 줄.</summary>
public class WinItem : INotifyPropertyChanged
{
    public IntPtr Handle { get; init; }

    string _title = "", _sub = "";
    bool _fitted, _minimized, _active;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Sub { get => _sub; set => Set(ref _sub, value); }

    public bool IsFitted
    {
        get => _fitted;
        set { if (Set(ref _fitted, value)) Raise(nameof(FitLabel)); }
    }

    public bool IsMinimized
    {
        get => _minimized;
        set { if (Set(ref _minimized, value)) Raise(nameof(MinLabel)); }
    }

    /// <summary>지금 맨 앞(사용 중)인 창. 목록 맨 위에 굵게.</summary>
    public bool IsActive { get => _active; set => Set(ref _active, value); }

    public string FitLabel => IsFitted ? "되돌리기" : "맞추기";
    public string MinLabel => IsMinimized ? "열기" : "최소화";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
