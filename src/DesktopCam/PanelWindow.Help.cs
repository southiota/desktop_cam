using System.Windows;

namespace DesktopCam;

/// <summary>패널 중 사용법: 녹화 영역 오른쪽 아래 밖의 버튼과, 떠 있는 창 위에 한꺼번에 번호 · 색 짝 화살표로 설명하는 안내.</summary>
public partial class PanelWindow
{
    HelpButtonWindow? _help;
    TourWindow? _tour;
    Rect _panelPx;
    Native.RECT _workArea;

    void CreateHelpButton()
    {
        _help = new HelpButtonWindow { Owner = _overlay };
        _help.Clicked += StartTour;
        _help.Show();
        PlaceHelpButton();
    }

    /// <summary>ApplyRegion 끝에서 불린다 (영역·패널이 움직이면 같이 따라감).</summary>
    void PlaceHelpButton()
    {
        if (_help is { IsVisible: true }) _help.Place(_region, _panelPx, _workArea);
    }

    /// <summary>
    /// 화면 위 요소의 자리 (실제 픽셀). 안 보이면 null.
    /// 두 모서리를 각각 화면 좌표로 바꾼다: 패널 확대·축소(LayoutTransform)와 Windows 배율이 모두 반영된다.
    /// </summary>
    Rect? Px(FrameworkElement? e)
    {
        if (e == null || !e.IsVisible || e.ActualWidth <= 0 || PresentationSource.FromVisual(e) == null) return null;
        var a = e.PointToScreen(new Point(0, 0));
        var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new Rect(a, b);
    }

    /// <summary>
    /// 패널 안 칸의 자리. 패널이 화면보다 길어 스크롤 중이면 지금 보이는 부분만 돌려주고,
    /// 스크롤로 가려져 거의 안 보이면 null (화면 밖을 가리키지 않게).
    /// </summary>
    Rect? PanelPx(FrameworkElement? e)
    {
        if (Px(e) is not Rect r || Px(PanelScroll) is not Rect view) return null;
        r.Intersect(view);
        return r.IsEmpty || r.Height < 12 ? null : r;
    }

    void StartTour()
    {
        if (_tour != null) { _tour.Activate(); return; }
        _tour = new TourWindow(TourItems(), () => Px(this), () => Math.Clamp(_settings.PanelZoom, ZoomMin, ZoomMax) / 100.0) { Owner = this };
        _tour.Closed += (_, _) => _tour = null;
        _tour.Show();
    }

    /// <summary>한 화면에 같이 뜨는 설명들 (이 순서로 ① ② … 번호와 짝 색이 붙음). 짧게: 한 상자에 서너 줄.</summary>
    List<TourItem> TourItems() => new()
    {
        new(() => new Rect(_region.Left, _region.Top, _region.Width, _region.Height),
            "녹화 영역",
            "점선 안만 녹화됩니다. 바깥은 '밖 가림'만큼 어둡게 덮이고, 패널 · 대본 · 이 안내는 녹화에 찍히지 않습니다.",
            Inside: true),

        new(() => PanelPx(Header),
            "머리 칸",
            "네모 칸 속 십자 화살표(이동 아이콘)를 잡고 끌면 녹화 영역과 패널이 같이 움직입니다. 시그니처 / 심플테마 = 화면 테마. - + = 패널 크기(70~150%, 화면보다 길면 스크롤). 상태 READY · REC · PAUSE, X 는 끄기."),

        new(() => PanelPx(RecCard),
            "녹화 · 저장 폴더",
            "REC 시작 · STOP 저장 · II 멈춤. 아래는 저장 폴더(변경 · 열기). 녹화 중 꺼져도 다음에 켜면 자동 복구.",
            "Ctrl+Alt+R · P"),

        new(() => PanelPx(AreaCard),
            "영역 크기",
            "720p · HD · QHD, 30 / 60fps. 밖 가림 = 바깥 어둡기, 숫자 = 영역 위치, 0,0 = 왼쪽 위로."),

        new(() => PanelPx(ScriptCard),
            "대본",
            "보이기 ON → 영역 안에 대본 글자만. < > 줄 이동, 자동스크롤 [AI] = 말하는 대로 넘김. ⚙ 편집 · 위치 · 크기.",
            "Ctrl+Alt+S · N · B"),

        new(() => PanelPx(FitCard),
            "창 맞추기",
            "지금 쓰는 창이 맨 위(굵게). 위로 = 맨 앞으로, 맞추기 = 영역에 꼭 맞춤 ↔ 되돌리기, 최소화 ↔ 열기. 제목 표시줄 빼기 = 맞춘 창의 제목 표시줄을 영역 위로 밀어 올려 안 찍히게 함. 비활성 숨기기 = 최소화된 창을 목록에서 뺌.",
            "Ctrl+Alt+F · M"),

        new(() => PanelPx(AudioCard),
            "소리",
            "시스템 · 마이크를 녹화에 넣을지 ON/OFF, 막대 = 음량 (CLIP 큼 · QUIET 무음). 모니터 = 헤드셋으로 내 목소리. ⚙ 필터 = 녹화 후 자동 적용."),

        new(() => PanelPx(ShortcutText),
            "단축키",
            "Ctrl+Alt 와 같이 누릅니다. 다른 프로그램을 쓰는 중에도 됩니다."),

        new(() => _help != null ? Px(_help.HelpBtn) : null,
            "사용법",
            "이 안내를 다시 봅니다."),
    };
}
