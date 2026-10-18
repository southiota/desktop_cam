using System.IO;
using System.Text.Json;

namespace DesktopCam;

/// <summary>사용자 설정. %APPDATA%\DesktopCAM\settings.json 에 저장된다.</summary>
public class Settings
{
    public string Preset { get; set; } = "QHD";   // "720" | "HD" | "QHD"
    public int Fps { get; set; } = 60;
    public int Dim { get; set; } = 75;            // 바깥 어둡게 0~90 (%)
    public string OutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DesktopCAM");
    public int OffsetX { get; set; }              // 녹화 영역 왼쪽 위 위치 (모니터 기준 실제 픽셀)
    public int OffsetY { get; set; }

    // 소리: 장치 ID 가 null 이면 Windows 기본 장치를 따른다
    public string? SystemDeviceId { get; set; }
    public string? MicDeviceId { get; set; }
    public string? SystemDeviceName { get; set; }     // 장치가 빠졌을 때 경고에 이름을 보여주려고 저장
    public string? MicDeviceName { get; set; }
    public bool SystemEnabled { get; set; } = true;   // 녹화에 시스템 소리를 넣을지
    public bool MicEnabled { get; set; } = true;      // 녹화에 마이크를 넣을지

    // 마이크 모니터링: 녹화하지 않는 다른 출력 장치(헤드셋)로 내 목소리를 들려준다
    public bool MonitorEnabled { get; set; }
    public string? MonitorDeviceId { get; set; }
    public string? MonitorDeviceName { get; set; }

    // 대본
    public string? ScriptPath { get; set; }
    public int ScriptLine { get; set; }
    public bool ScriptVisible { get; set; }
    public bool ScriptVoice { get; set; } = true;      // 음성인식으로 줄 넘기기
    public bool ScriptAtBottom { get; set; }           // (예전 설정) 위쪽 / 아래쪽 → ScriptPos 로 옮김
    public string? ScriptPos { get; set; }             // Top · Bottom · Left · Right (녹화 영역 안)
    public double ScriptScale { get; set; } = 1.0;     // 글자 크기 배율 0.6 ~ 2.0
    public bool ScriptShowPrev { get; set; } = true;     // 이전 줄 보이기
    public bool ScriptShowSection { get; set; } = true;  // ## 섹션 이름 보이기
    public bool ScriptShowCue { get; set; } = true;      // > 메모 보이기
    public int ScriptNextCount { get; set; } = 2;        // 다음 줄 몇 줄까지 (0~2)
    public string ScriptSensitivity { get; set; } = "Normal";   // 자동스크롤: Fast · Normal · Strict

    public MicFx MicFx { get; set; } = new();          // 마이크 소리 다듬기 (녹화 후 ffmpeg)

    public int Width => Preset switch { "720" => 1280, "HD" => 1920, _ => 2560 };
    public int Height => Preset switch { "720" => 720, "HD" => 1080, _ => 1440 };

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopCAM", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* 설정이 깨졌으면 기본값으로 시작 */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 저장 실패는 다음 실행 때 기본값으로 시작할 뿐이므로 무시 */ }
    }
}
