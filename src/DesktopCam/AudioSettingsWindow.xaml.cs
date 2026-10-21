using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using static DesktopCam.Native;

namespace DesktopCam;

/// <summary>
/// 마이크 소리 다듬기 설정: 프리셋, 잡음 제거·EQ·압축·음량, 5초 녹음해서 원음/적용 비교, NVIDIA Broadcast 마이크 찾기.
/// 다듬기는 녹화가 끝난 뒤 적용된다 (모니터·음성인식은 원음).
/// </summary>
public partial class AudioSettingsWindow : Window
{
    readonly PanelWindow _panel;
    readonly Settings _s;
    MicFx Fx => _s.MicFx;
    // 색은 그때그때 찾는다 (테마를 바꾸면 새 색이 나오게). 자주 불리지 않으므로 비용은 무시할 만하다
    Brush _teal => (Brush)FindResource("Teal");
    Brush _muted => (Brush)FindResource("Muted");
    Brush _danger => (Brush)FindResource("Danger");
    bool _loading;
    string? _rawWav, _fxWav, _fxWavFor;
    WasapiOut? _player;

    public AudioSettingsWindow(PanelWindow panel, Settings settings)
    {
        _panel = panel;
        _s = settings;
        InitializeComponent();

        foreach (var (key, name, tip) in MicFx.Presets)
        {
            var rb = new RadioButton { Content = name, Tag = key, GroupName = "preset", ToolTip = tip, Margin = new Thickness(0, 0, 4, 4),
                                       Style = (Style)FindResource("SegSm"), BorderThickness = new Thickness(0) };
            rb.Checked += OnPreset;
            var box = new Border { Margin = new Thickness(0, 0, 4, 4), Child = rb };
            box.SetResourceReference(Border.BorderBrushProperty, "Edge");
            box.SetResourceReference(Border.BorderThicknessProperty, "EdgeW");
            PresetPanel.Children.Add(box);
        }
        LoadValues();
        UpdateBroadcast();
        ToolText.Text = Tools.Ffmpeg != null
            ? $"다듬기는 녹화가 끝난 뒤 적용됩니다 (영상은 그대로, 소리만 새로). 1시간 녹화면 3~4분쯤 걸립니다. 모니터(헤드셋)와 자동스크롤에는 원음이 쓰입니다."
            : "ffmpeg.exe 가 없어 다듬기를 쓸 수 없습니다. 프로그램 폴더의 tools 에 넣어 주세요.";
        ToolText.Foreground = Tools.Ffmpeg != null ? _muted : _danger;
        Theme.Changed += OnThemeChanged;

        SourceInitialized += (_, _) =>
        {
            if (!ShowInCaptureForTesting) SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WDA_EXCLUDEFROMCAPTURE);
        };
        Closed += (_, _) => { Theme.Changed -= OnThemeChanged; StopPlayer(); TryDelete(_rawWav); TryDelete(_fxWav); };
        Loaded += (_, _) => ClipCheck.Run(this, "소리 설정");
    }

    // ── 값 ↔ 화면 ──

    void LoadValues()
    {
        _loading = true;
        foreach (Border b in PresetPanel.Children)
            if (b.Child is RadioButton rb) rb.IsChecked = (string)rb.Tag == Fx.Preset;
        UpdatePresetTip();
        (Fx.Denoise switch { 0 => Dn0, 2 => Dn2, _ => Dn1 }).IsChecked = true;
        (Fx.Compress switch { 0 => Cp0, 2 => Cp2, _ => Cp1 }).IsChecked = true;
        HpSlider.Value = Fx.HighPass;
        LowSlider.Value = Fx.Low;
        MidSlider.Value = Fx.Mid;
        PresSlider.Value = Fx.Presence;
        AirSlider.Value = Fx.Air;
        _loading = false;
        UpdateTexts();
        FxGrid.IsEnabled = Fx.Enabled;
        FxGrid.Opacity = Fx.Enabled ? 1 : 0.45;
    }

    void UpdateTexts()
    {
        HpText.Text = Fx.HighPass == 0 ? "끔" : $"{Fx.HighPass}Hz";
        LowText.Text = Db(Fx.Low);
        MidText.Text = Db(Fx.Mid);
        PresText.Text = Db(Fx.Presence);
        AirText.Text = Db(Fx.Air);
        Toggle(GateBtn, Fx.Gate);
        Toggle(NormBtn, Fx.Normalize);
        Toggle(KeepRawBtn, Fx.KeepRaw);
    }

    /// <summary>프리셋 설명 + 녹화 때 자동으로 적용되는지.</summary>
    void UpdatePresetTip()
    {
        var (_, name, tip) = MicFx.Presets.FirstOrDefault(p => p.Key == Fx.Preset);
        PresetTip.Text = Fx.Enabled ? $"{name} (녹화 시 자동 적용) — {tip}" : $"{name} (적용 안 함) — {tip}";
        PresetTip.Foreground = Fx.Enabled ? _teal : _muted;
    }

    static string Db(double v) => v == 0 ? "0dB" : $"{v:+0.#;-0.#}dB";

    void Toggle(Button b, bool on)
    {
        b.Content = on ? "ON" : "OFF";
        b.Foreground = on ? _teal : _muted;
        b.SetResourceReference(FontFamilyProperty, "PixelFont");
        b.SetResourceReference(FontSizeProperty, "PxS");
        b.MinWidth = 46;
    }

    /// <summary>값을 손으로 바꾸면 '사용자' 프리셋이 된다.</summary>
    void Changed()
    {
        if (_loading) return;
        if (Fx.Preset != "Custom" && Fx.Preset != "Off")
        {
            Fx.Preset = "Custom";
            _loading = true;
            foreach (Border b in PresetPanel.Children)
                if (b.Child is RadioButton rb) rb.IsChecked = (string)rb.Tag == "Custom";
            UpdatePresetTip();
            _loading = false;
        }
        UpdateTexts();
        _panel.OnMicFxChanged();
    }

    void OnPreset(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string key }) return;
        Fx.ApplyPreset(key);
        LoadValues();
        _panel.OnMicFxChanged();
    }

    void OnDenoise(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string t }) return;
        Fx.Denoise = int.Parse(t);
        Changed();
    }

    void OnCompress(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string t }) return;
        Fx.Compress = int.Parse(t);
        Changed();
    }

    void OnSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Fx.HighPass = (int)HpSlider.Value;
        Fx.Low = LowSlider.Value;
        Fx.Mid = MidSlider.Value;
        Fx.Presence = PresSlider.Value;
        Fx.Air = AirSlider.Value;
        Changed();
    }

    void OnGate(object sender, RoutedEventArgs e) { Fx.Gate = !Fx.Gate; Changed(); }
    void OnNormalize(object sender, RoutedEventArgs e) { Fx.Normalize = !Fx.Normalize; Changed(); }
    void OnKeepRaw(object sender, RoutedEventArgs e) { Fx.KeepRaw = !Fx.KeepRaw; UpdateTexts(); _panel.OnMicFxChanged(); }

    // ── 미리 듣기 ──

    async void OnRecordTest(object sender, RoutedEventArgs e)
    {
        StopPlayer();
        RecTestBtn.IsEnabled = PlayRawBtn.IsEnabled = PlayFxBtn.IsEnabled = false;
        for (int i = 5; i >= 1; i--)
        {
            PreviewText.Text = $"녹음 중… {i}";
            if (i == 5) _ = RecordAsync();
            await Task.Delay(1000);
        }
        async Task RecordAsync()
        {
            TryDelete(_rawWav);
            _rawWav = await _panel.RecordMicSampleAsync(5);
            _fxWavFor = null;
            RecTestBtn.IsEnabled = true;
            PlayRawBtn.IsEnabled = PlayFxBtn.IsEnabled = _rawWav != null;
            PreviewText.Text = _rawWav != null ? "녹음했습니다. 원음과 적용한 소리를 번갈아 들어 보세요." : "마이크를 열 수 없어 녹음하지 못했습니다.";
        }
    }

    void OnPlayRaw(object sender, RoutedEventArgs e) { if (_rawWav != null) Play(_rawWav); }

    async void OnPlayFx(object sender, RoutedEventArgs e)
    {
        if (_rawWav == null) return;
        string key = System.Text.Json.JsonSerializer.Serialize(Fx);
        if (_fxWav == null || _fxWavFor != key)
        {
            PreviewText.Text = "다듬는 중…";
            PlayFxBtn.IsEnabled = false;
            try
            {
                TryDelete(_fxWav);
                _fxWav = Path.Combine(Path.GetTempPath(), $"desktopcam_fx_{Guid.NewGuid():N}.wav");
                var fx = Fx.Enabled ? Fx : new MicFx { Preset = "Off", Denoise = 0, Gate = false, HighPass = 0, Low = 0, Mid = 0, Presence = 0, Air = 0, Compress = 0, Normalize = false };
                await AudioPost.ProcessWavAsync(_rawWav, fx, _fxWav);
                _fxWavFor = key;
            }
            catch (Exception ex) { PreviewText.Text = $"다듬지 못했습니다. {ex.Message}"; PlayFxBtn.IsEnabled = true; return; }
            PlayFxBtn.IsEnabled = true;
        }
        PreviewText.Text = $"적용해서 듣는 중 ({Fx.PresetName})";
        Play(_fxWav);
    }

    void Play(string path)
    {
        StopPlayer();
        try
        {
            var reader = new WaveFileReader(path);
            var player = new WasapiOut(AudioClientShareMode.Shared, 80);
            player.Init(reader);
            player.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(() => { reader.Dispose(); if (_player == player) StopBtn.IsEnabled = false; });
            player.Play();
            _player = player;
            StopBtn.IsEnabled = true;
            if (path == _rawWav) PreviewText.Text = "원음 듣는 중";
        }
        catch (Exception ex) { PreviewText.Text = $"재생하지 못했습니다. {ex.Message}"; }
    }

    void OnStopPlay(object sender, RoutedEventArgs e) => StopPlayer();

    void StopPlayer()
    {
        var p = _player;
        _player = null;
        if (p == null) return;
        try { p.Stop(); } catch { }
        p.Dispose();
        StopBtn.IsEnabled = false;
    }

    static void TryDelete(string? path) { try { if (path != null) File.Delete(path); } catch { } }

    // ── NVIDIA Broadcast ──

    void UpdateBroadcast()
    {
        var bc = _panel.CaptureDevices.FirstOrDefault(d => d.Id != null && d.Name.Contains("NVIDIA Broadcast", StringComparison.OrdinalIgnoreCase));
        if (bc == null)
        {
            BroadcastText.Text = "NVIDIA Broadcast 를 설치하면 RTX 그래픽 카드의 AI 잡음 제거를 실시간으로 쓸 수 있습니다 (nvidia.com/broadcast). 설치하면 여기서 찾아 줍니다.";
            BroadcastBtn.Visibility = Visibility.Collapsed;
            return;
        }
        bool inUse = _s.MicDeviceId == bc.Id;
        BroadcastText.Text = inUse
            ? "NVIDIA Broadcast 마이크를 쓰는 중입니다. 잡음은 이미 지워지므로 '잡음 제거'는 끄거나 '약하게'를 권합니다."
            : $"NVIDIA Broadcast 마이크를 찾았습니다: {bc.Name}";
        BroadcastText.Foreground = _teal;
        BroadcastBtn.Visibility = inUse ? Visibility.Collapsed : Visibility.Visible;
        BroadcastBtn.Tag = bc.Id;
    }

    void OnUseBroadcast(object sender, RoutedEventArgs e)
    {
        if (BroadcastBtn.Tag is not string id) return;
        _panel.UseMic(id);
        UpdateBroadcast();
    }

    /// <summary>테마가 바뀌면 코드에서 칠한 색을 다시 칠한다.</summary>
    void OnThemeChanged()
    {
        UpdateTexts();
        UpdatePresetTip();
        UpdateBroadcast();
        ToolText.Foreground = Tools.Ffmpeg != null ? _muted : _danger;
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    void OnHeaderDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
