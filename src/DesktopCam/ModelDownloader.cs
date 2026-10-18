using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace DesktopCam;

/// <summary>
/// 자동스크롤 [AI] 의 한국어 음성 모델(약 83MB zip)을 alphacephei.com 에서 받아 푼다.
/// 프로그램 폴더의 models\ 에 쓸 수 있으면 거기, 아니면(Program Files 등) %LOCALAPPDATA%\DesktopCAM\models\.
/// 다 받고 다 푼 뒤에만 제 이름으로 옮기므로, 중간에 끊겨도 반쪽 모델이 남지 않는다.
/// </summary>
public static class ModelDownloader
{
    public static readonly string Url = $"https://alphacephei.com/vosk/models/{SpeechFollower.ModelName}.zip";
    public const string SizeText = "약 83MB";

    /// <param name="progress">0~100</param>
    public static async Task DownloadAsync(Action<double> progress, CancellationToken ct)
    {
        string root = WritableRoot();
        string zip = Path.Combine(root, SpeechFollower.ModelName + ".zip.part");
        string unpack = Path.Combine(root, SpeechFollower.ModelName + ".unpack");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var res = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? 0;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);
                var buf = new byte[1 << 16];
                long done = 0;
                int n, lastPct = -1;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    int pct = total > 0 ? (int)(done * 95 / total) : 0;   // 마지막 5% 는 풀기
                    if (pct != lastPct) { lastPct = pct; progress(pct); }
                }
            }

            await Task.Run(() =>
            {
                if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
                ZipFile.ExtractToDirectory(zip, unpack);
                // zip 안에 모델 이름 폴더가 한 겹 있다. 없으면 푼 곳 자체가 모델
                string inner = Path.Combine(unpack, SpeechFollower.ModelName);
                string from = Directory.Exists(inner) ? inner : unpack;
                if (!File.Exists(Path.Combine(from, "am", "final.mdl")))
                    throw new InvalidDataException("받은 파일이 음성 모델이 아닙니다.");
                string to = Path.Combine(root, SpeechFollower.ModelName);
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.Move(from, to);
            }, ct);
            progress(100);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            try { if (Directory.Exists(unpack)) Directory.Delete(unpack, true); } catch { }
        }
    }

    static string WritableRoot()
    {
        foreach (var root in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCAM", "models"),
        })
        {
            try
            {
                Directory.CreateDirectory(root);
                string probe = Path.Combine(root, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return root;
            }
            catch { }
        }
        throw new IOException("모델을 저장할 폴더에 쓸 수 없습니다.");
    }
}
