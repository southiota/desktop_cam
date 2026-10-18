using System.Text;

namespace DesktopCam;

/// <summary>
/// 들린 말이 대본 한 줄을 얼마나 읽었는지 0~1 로 잰다.
/// 한글은 자모(ㄱ ㅏ ㄴ …)로 풀어서 비교하므로, 음성인식이 '고전'을 '고잔'으로 들어도 대부분 맞은 것으로 친다.
/// </summary>
public static class ScriptMatcher
{
    const int SBase = 0xAC00, LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    /// <summary>비교용 문자열: 띄어쓰기·문장부호를 빼고, 한글은 자모로, 영문은 소문자로.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length * 3);
        foreach (char c in text)
        {
            int s = c - SBase;
            if (s >= 0 && s < SCount)
            {
                sb.Append((char)(0x1100 + s / NCount));                 // 초성
                sb.Append((char)(0x1161 + s % NCount / TCount));        // 중성
                int t = s % TCount;
                if (t != 0) sb.Append((char)(0x11A7 + t));              // 종성
            }
            else if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 대본 줄이 들린 말 안에 순서대로 들어 있는 비율 (최장 공통 부분열 / 줄 길이).
    /// 들린 말이 길게 쌓이면 끝부분(줄 길이의 1.6배)만 본다.
    /// </summary>
    public static double Score(string normalizedLine, string heard)
    {
        if (normalizedLine.Length == 0) return 1;
        string h = Normalize(heard);
        int window = (int)(normalizedLine.Length * 1.6) + 4;
        if (h.Length > window) h = h[^window..];
        if (h.Length == 0) return 0;
        return (double)Lcs(normalizedLine, h) / normalizedLine.Length;
    }

    static int Lcs(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        foreach (char ca in a)
        {
            for (int j = 1; j <= b.Length; j++)
                cur[j] = ca == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
