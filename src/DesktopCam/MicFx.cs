using System.Globalization;
using System.IO;

namespace DesktopCam;

/// <summary>
/// 마이크 소리 다듬기 설정 (녹화가 끝나면 ffmpeg 로 적용). 프리셋을 고르면 값이 채워지고, 값을 바꾸면 '사용자'가 된다.
/// </summary>
public class MicFx
{
    public string Preset { get; set; } = "Voice"; // Off · Voice · Noisy · Clear · Warm · Custom
    public int HighPass { get; set; } = 80;       // 이 주파수(Hz) 아래 웅웅거림을 자름. 0 = 끔
    public int Denoise { get; set; } = 1;         // 0 끔 · 1 약하게(FFT) · 2 강하게(AI, RNNoise)
    public bool Gate { get; set; } = true;        // 말 안 할 때 남은 잡음을 20dB 더 줄임
    public double Low { get; set; }               // 저음 150Hz (dB)
    public double Mid { get; set; }               // 중음 800Hz (dB)
    public double Presence { get; set; } = 2;     // 또렷함 3.5kHz (dB)
    public double Air { get; set; } = 1;          // 공기감 10kHz (dB)
    public int Compress { get; set; } = 1;        // 0 끔 · 1 약하게 · 2 강하게 (작은 소리·큰 소리 차이를 줄임)
    public bool Normalize { get; set; } = true;   // 유튜브 기준 음량(-16 LUFS)으로 맞춤
    public bool KeepRaw { get; set; }             // 원본(처리 전 영상·마이크 소리)도 남기기

    public bool Enabled => Preset != "Off";

    public static readonly (string Key, string Name, string Tip)[] Presets =
    {
        ("Off", "원음", "다듬지 않고 그대로 녹음합니다"),
        ("Voice", "목소리 기본", "웅웅거림 자르기 · 가벼운 잡음 제거 · 살짝 또렷하게 · 음량 맞추기"),
        ("Noisy", "잡음 많은 방", "AI 잡음 제거(RNNoise) · 말 안 할 때 잡음 줄이기 · 또렷하게"),
        ("Clear", "선명하게", "저음을 덜고 고음을 올려 또박또박 들리게"),
        ("Warm", "따뜻하게", "저음을 살리고 날카로운 고음을 덜어 부드럽게"),
        ("Custom", "사용자", "아래 값을 직접 맞춘 설정"),
    };

    /// <summary>프리셋 값을 채운다 (Custom 은 그대로).</summary>
    public void ApplyPreset(string key)
    {
        Preset = key;
        (int hp, int dn, bool gate, double low, double mid, double pres, double air, int comp) v = key switch
        {
            "Voice" => (80, 1, true, 0, 0, 2, 1, 1),
            "Noisy" => (100, 2, true, -1, 0, 3, 1, 1),
            "Clear" => (100, 1, true, -2, -1, 4, 3, 2),
            "Warm" => (60, 1, true, 3, 0, 1, -1, 1),
            _ => (HighPass, Denoise, Gate, Low, Mid, Presence, Air, Compress),
        };
        (HighPass, Denoise, Gate, Low, Mid, Presence, Air, Compress) = v;
        if (key is "Voice" or "Noisy" or "Clear" or "Warm") Normalize = true;
    }

    public string PresetName => Presets.FirstOrDefault(p => p.Key == Preset).Name ?? "원음";

    /// <summary>
    /// ffmpeg 오디오 필터 사슬. 마이크 입력이 두 채널이면(인터페이스 입력 1/2 등 한쪽만 쓰는 경우가 많음) 먼저 모노로 합치고,
    /// 끝에서 48kHz 스테레오로 맞춘다.
    /// </summary>
    public string BuildChain(string? rnnoiseModel, int inputChannels)
    {
        var f = new List<string>();
        if (inputChannels >= 2) f.Add("pan=mono|c0=0.5*c0+0.5*c1");
        f.Add("aresample=48000");
        if (HighPass > 0) f.Add($"highpass=f={HighPass}");
        if (Denoise == 2 && rnnoiseModel != null) f.Add($"arnndn=m='{EscapePath(rnnoiseModel)}'");
        else if (Denoise >= 1) f.Add("afftdn=nr=24:nf=-45:tn=1");
        // 게이트: 말 안 할 때 남은 잡음을 20dB 낮춘다 (완전히 끊지 않아 말끝이 잘리지 않음). 압축·음량 맞추기 전에 둬야 잡음이 다시 커지지 않는다.
        if (Gate) f.Add("agate=threshold=0.02:ratio=2:range=0.1:attack=10:release=250");
        if (Low != 0) f.Add($"bass=g={N(Low)}:f=150");
        if (Mid != 0) f.Add($"equalizer=f=800:t=q:w=1:g={N(Mid)}");
        if (Presence != 0) f.Add($"equalizer=f=3500:t=q:w=1:g={N(Presence)}");
        if (Air != 0) f.Add($"treble=g={N(Air)}:f=10000");
        if (Compress == 1) f.Add("acompressor=threshold=-20dB:ratio=3:attack=5:release=120:makeup=2");
        if (Compress == 2) f.Add("acompressor=threshold=-26dB:ratio=5:attack=3:release=80:makeup=4");
        f.Add(Normalize ? "loudnorm=I=-16:TP=-1.5:LRA=11" : "alimiter=limit=0.95");
        f.Add("aresample=48000");
        f.Add("aformat=sample_fmts=fltp:channel_layouts=stereo");
        return string.Join(",", f);
    }

    static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>필터 안의 경로: 역슬래시 → 슬래시, 콜론은 \: 로.</summary>
    static string EscapePath(string p) => p.Replace('\\', '/').Replace(":", "\\:");

    public MicFx Clone() => (MicFx)MemberwiseClone();
}

/// <summary>ffmpeg.exe 와 AI 잡음 제거 모델(cb.rnnn) 찾기: 실행 폴더의 tools\ 또는 %LOCALAPPDATA%\DesktopCAM\tools\.</summary>
public static class Tools
{
    static IEnumerable<string> Dirs => new[]
    {
        Path.Combine(App.ExeDir, "tools"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCAM", "tools"),
    };

    public static string? Ffmpeg => Dirs.Select(d => Path.Combine(d, "ffmpeg.exe")).FirstOrDefault(File.Exists);
    public static string? RnnoiseModel => Dirs.Select(d => Path.Combine(d, "cb.rnnn")).FirstOrDefault(File.Exists);
}
