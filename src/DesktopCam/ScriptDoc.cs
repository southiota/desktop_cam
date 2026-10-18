using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopCam;

/// <summary>대본 한 줄: 읽을 문장, 강조 구간, 속한 섹션, 이 줄 앞의 메모(연출 지시).</summary>
public record ScriptLine(string Text, IReadOnlyList<(int Start, int Length)> Emphasis, string Section, string Cue);

/// <summary>
/// 대본 파일 읽기. .md 와 .txt 를 받는다.
///
///   # 제목 / ## 섹션      섹션 이름 (화면 위쪽에 작게, 읽는 줄이 아님)        .txt 에서는 # 줄이 메모
///   문장                  한 줄 = 한 번에 읽을 문장 (음성으로 넘기기 단위)
///   **강조**              노란색으로 강조
///   > 메모                다음 줄 위에 작게 보이는 연출 지시 (읽지 않음, 음성 비교에서 빠짐)
///   - 문장 / 1. 문장       목록 기호는 떼고 한 줄로
///   &lt;!-- 주석 --&gt;, ---  무시
/// </summary>
public static partial class ScriptDoc
{
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)] private static partial Regex CommentRx();
    [GeneratedRegex(@"^(\s*([-*+]|\d+[.)])\s+)")] private static partial Regex ListRx();
    [GeneratedRegex(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$")] private static partial Regex RuleRx();

    public static List<ScriptLine> Load(string path)
    {
        bool md = Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
               || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        return Parse(ReadText(path), md);
    }

    /// <summary>UTF-8 이 아니면 한글 윈도우 인코딩(CP949)으로 읽는다.</summary>
    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    public static List<ScriptLine> Parse(string text, bool markdown)
    {
        text = CommentRx().Replace(text.Replace("\r", ""), "");
        var result = new List<ScriptLine>();
        string section = "";
        var cue = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || RuleRx().IsMatch(line)) continue;

            if (line.StartsWith('#'))
            {
                if (markdown) section = line.TrimStart('#').Trim();
                continue;   // .txt 에서는 메모
            }
            if (line.StartsWith('>'))
            {
                cue.Add(line.TrimStart('>').Trim());
                continue;
            }

            line = ListRx().Replace(line, "");
            var (plain, emphasis) = markdown ? StripEmphasis(line) : (line, new List<(int, int)>());
            if (plain.Trim().Length == 0) continue;
            result.Add(new ScriptLine(plain, emphasis, section, string.Join(" · ", cue)));
            cue.Clear();
        }
        return result;
    }

    /// <summary>**강조** 를 떼고, 강조된 글자 위치를 돌려준다. 남는 마크다운 기호(`, __)도 뗀다.</summary>
    static (string, List<(int, int)>) StripEmphasis(string line)
    {
        var sb = new StringBuilder();
        var ranges = new List<(int, int)>();
        int i = 0, open = -1;
        while (i < line.Length)
        {
            if (i + 1 < line.Length && (line[i] == '*' && line[i + 1] == '*' || line[i] == '_' && line[i + 1] == '_'))
            {
                if (open < 0) open = sb.Length;
                else { if (sb.Length > open) ranges.Add((open, sb.Length - open)); open = -1; }
                i += 2;
                continue;
            }
            if (line[i] == '`') { i++; continue; }
            sb.Append(line[i]);
            i++;
        }
        return (sb.ToString(), ranges);
    }

    public const int LongLine = 40;   // 이보다 긴 줄은 한 화면에 읽기 어렵다

    /// <summary>읽는 시간 어림: 한국어 말하기 약 초당 5음절.</summary>
    public static TimeSpan ReadingTime(IEnumerable<ScriptLine> lines) =>
        TimeSpan.FromSeconds(lines.Sum(l => l.Text.Count(c => !char.IsWhiteSpace(c))) / 5.0);

    /// <summary>
    /// 40자가 넘는 문장 줄을 나눈다. 문장 끝(. ? !)에서 먼저, 그래도 길면 쉼표에서, 그래도 길면 가운데 띄어쓰기에서.
    /// ## · > · 주석 줄과 짧은 줄은 그대로 둔다. 나눈 줄 수를 돌려준다.
    /// </summary>
    public static (string text, int splits) SplitLongLines(string text)
    {
        var outLines = new List<string>();
        int splits = 0;
        bool inComment = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string t = raw.Trim();
            if (t.Contains("<!--")) inComment = !t.Contains("-->");
            else if (inComment) { if (t.Contains("-->")) inComment = false; }
            if (inComment || t.Contains("<!--") || t.StartsWith('#') || t.StartsWith('>') || Plain(t).Length <= LongLine)
            {
                outLines.Add(raw);
                continue;
            }
            var parts = Split(t);
            splits += parts.Count - 1;
            outLines.AddRange(parts);
        }
        return (string.Join("\n", outLines), splits);
    }

    static string Plain(string s) => s.Replace("**", "").Replace("__", "");

    static List<string> Split(string line)
    {
        if (Plain(line).Length <= LongLine) return new() { line };
        // 나눌 자리 후보: 문장 끝 → 쉼표 → 띄어쓰기. 가운데에 가장 가까운 곳에서 자른다.
        foreach (var marks in new[] { ".?!。", ",，", " " })
        {
            int mid = line.Length / 2, best = -1;
            for (int i = 1; i < line.Length - 1; i++)
                if (marks.Contains(line[i]) && (marks == " " || line[i + 1] == ' ') && !InsideEmphasis(line, i)
                    && (best < 0 || Math.Abs(i - mid) < Math.Abs(best - mid)))
                    best = i;
            if (best > 0)
            {
                var a = line[..(best + 1)].Trim();
                var b = line[(best + 1)..].Trim();
                if (a.Length > 0 && b.Length > 0) return Split(a).Concat(Split(b)).ToList();
            }
        }
        return new() { line };
    }

    static bool InsideEmphasis(string line, int i) => (line[..i].Split("**").Length - 1) % 2 == 1;

    /// <summary>대본을 이 프로그램 형식으로 다듬어 달라고 AI 에게 줄 프롬프트.</summary>
    public const string OptimizePrompt =
"""
아래 [원고]를 영상 녹화용 프롬프터 대본으로 바꿔 주세요. 결과는 Desktop CAM 프로그램이 그대로 읽는 마크다운(.md) 파일 내용 하나로만 답해 주세요. 설명이나 코드 블록 표시(```)는 붙이지 마세요.

[형식]
- 한 줄 = 한 번에 읽을 문장 하나. 화면에 한 줄씩 크게 뜨고, 말을 끝내면 음성인식으로 다음 줄로 넘어갑니다.
- 한 줄은 15~40자. 길면 숨 쉬는 곳에서 나눠 여러 줄로.
- 문장과 문장 사이에 빈 줄을 두지 않아도 됩니다. 빈 줄은 무시됩니다.
- 섹션이 바뀌는 곳에 `## 섹션 이름` (예: `## 인트로`, `## 본론 1`, `## 마무리`). 섹션 제목은 읽지 않습니다.
- 꼭 힘줘 말할 단어만 `**단어**` 로 강조. 한 줄에 하나 이하.
- 읽지 않는 연출 지시(화면 전환, 자료 띄우기, 쉬기, 표정 등)는 그 문장 바로 위에 `> 메모` 로.
- 위의 것 말고 다른 마크다운(표, 링크, 이미지, 목록 기호, 굵은 제목 외 꾸밈)은 쓰지 마세요.

[말하기 다듬기]
- 서술형·문어체 문장은 말하듯 자연스러운 구어체로 바꾸되, 내용과 정보는 빼거나 바꾸지 마세요.
- 숫자·단위·영어 약어는 소리 내어 읽는 대로 한글로 쓰세요 (예: 2026년 → 이천이십육 년, 3배 → 세 배, GPU → 지피유). 음성인식이 맞게 따라가도록 하기 위해서입니다.
- 괄호 속 보충 설명은 문장 안에 풀어 쓰거나 `> 메모` 로 옮기세요.
- 이모지와 특수 기호는 쓰지 마세요.

[예시]
## 인트로
> 카메라 보고 시작
안녕하세요, 고전 게임을 다시 실행하는 채널입니다.
오늘은 **메뉴 화면**을 새로 만든 과정을 보여드릴게요.
## 마무리
> 구독 버튼 띄우기
구독과 좋아요 부탁드립니다.

[원고]

""";

    /// <summary>처음 편집할 때 만드는 예시 대본.</summary>
    public const string Sample =
"""
<!-- Desktop CAM 대본 형식
  한 줄 = 한 번에 읽을 문장 (말을 끝내면 음성인식으로 다음 줄)
  ## 섹션      섹션 이름 (읽지 않음)
  **강조**     노란색으로 강조
  > 메모       다음 문장 위에 작게 뜨는 연출 지시 (읽지 않음)
  저장하면 바로 반영됩니다.
-->

## 인트로
> 카메라 보고 시작
안녕하세요, 고전 게임을 다시 실행하는 채널입니다.
오늘은 **메뉴 화면**을 새로 만든 과정을 보여드릴게요.

## 마무리
> 구독 버튼 띄우기
구독과 좋아요 부탁드립니다.
""";
}
