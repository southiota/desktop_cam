# 주간·월간 보고용 지표를 센다 (캠_개발팀). 결과는 TSV 로 표준 출력에 낸다.
#   python tools/weekly_metrics.py                 지난 화~월 (화요일에 돌리는 것을 기준으로)
#   python tools/weekly_metrics.py --end 2026-10-26  그 월요일로 끝나는 주
#   python tools/weekly_metrics.py --month 2026-10   한 달
#   --no-build  로컬 빌드(M3)를 건너뜀
# 필요한 것: git, gh(로그인), dotnet 8 SDK. 기간은 한국 시간(KST) 기준.
import argparse, json, re, subprocess, sys, tempfile
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REPO = "southiota/desktop_cam"
KST = timezone(timedelta(hours=9))
SNAPSHOT = ROOT / ".metrics" / "downloads.tsv"   # 다운로드 누적 수 기록 (주간 증가분 계산용)
# 커밋 메시지(제목·본문) 한 줄 = 고친 것 하나. '새로고침' 같은 낱말은 빼려고 앞을 막는다.
FIX_RE = re.compile(r"문제 수정|오류 수정|(?<!새로)고침|고쳤|버그|\bfix|\bbug", re.I)


def run(args, **kw):
    return subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", **kw)


def period(args):
    if args.month:
        y, m = map(int, args.month.split("-"))
        start = date(y, m, 1)
        end = (date(y + m // 12, m % 12 + 1, 1)) - timedelta(days=1)
        return start, end
    if args.end:
        end = date.fromisoformat(args.end)
    else:
        end = datetime.now(KST).date() - timedelta(days=1)
        while end.weekday() != 0:   # 월요일
            end -= timedelta(days=1)
    return end - timedelta(days=6), end


def in_period(iso, start, end):
    d = datetime.fromisoformat(iso.replace("Z", "+00:00")).astimezone(KST).date()
    return start <= d <= end


def gh_json(args):
    r = run(["gh"] + args)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip() or "gh 실패")
    return json.loads(r.stdout or "[]")


def m1_releases(start, end):
    rels = gh_json(["api", f"repos/{REPO}/releases", "--paginate"])
    tags = [r["tag_name"] for r in rels if r.get("published_at") and in_period(r["published_at"], start, end)]
    return len(tags), " ".join(sorted(tags))


def m2_release_runs(start, end):
    runs = gh_json(["run", "list", "--workflow", "release.yml", "-L", "200", "--json", "conclusion,createdAt,status"])
    done = [r for r in runs if r["status"] == "completed" and in_period(r["createdAt"], start, end)]
    if not done:
        return "-", "돌린 빌드 없음"
    ok = sum(r["conclusion"] == "success" for r in done)
    return round(ok * 100 / len(done)), f"{ok}/{len(done)}"


def m3_build():
    with tempfile.TemporaryDirectory() as out:   # 켜 둔 exe 가 bin 을 잡고 있어도 빌드되게 따로 낸다
        r = run(["dotnet", "build", "src/DesktopCam", "-c", "Release", "--no-incremental", "-nologo", "-o", out])
    text = r.stdout + r.stderr
    warns = {m.group(0) for m in re.finditer(r"^.*: warning [A-Z]+\d+.*$", text, re.M)}
    if r.returncode != 0:
        return "FAIL", f"빌드 실패, 경고 {len(warns)}"
    return len(warns), "빌드 통과"


def m4_fix_commits(start, end):
    since = f"{start.isoformat()}T00:00:00+09:00"
    until = f"{end.isoformat()}T23:59:59+09:00"
    r = run(["git", "log", "--no-merges", f"--since={since}", f"--until={until}", "--format=%x00%B"])
    commits = [c for c in r.stdout.split("\x00") if c.strip()]
    fixes = sum(1 for c in commits for line in c.splitlines() if FIX_RE.search(line))
    return fixes, f"전체 커밋 {len(commits)}"


def m5_downloads(start, end):
    rels = gh_json(["api", f"repos/{REPO}/releases", "--paginate"])
    now = sum(a["download_count"] for r in rels for a in r.get("assets", []))
    today = datetime.now(KST).date().isoformat()
    rows = []
    if SNAPSHOT.exists():
        rows = [l.split("\t") for l in SNAPSHOT.read_text(encoding="utf-8").splitlines() if l.strip()]
    if not rows or rows[-1][0] != today:
        SNAPSHOT.parent.mkdir(exist_ok=True)
        with SNAPSHOT.open("a", encoding="utf-8") as f:
            f.write(f"{today}\t{now}\n")
    # 기간 첫날(화요일 보고 때 찍은 것)까지의 가장 늦은 기록을 기준으로 증가분
    base = [int(n) for d, n in rows if d <= start.isoformat()]
    if not base:
        return "-", f"누적 {now} (기간 앞 기록 없음)"
    return now - base[-1], f"누적 {now}"


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--end")
    p.add_argument("--month")
    p.add_argument("--no-build", action="store_true")
    args = p.parse_args()
    start, end = period(args)

    metrics = [
        ("M1 배포 횟수", "회", lambda: m1_releases(start, end)),
        ("M2 릴리스 빌드 성공률", "%", lambda: m2_release_runs(start, end)),
        ("M3 빌드 경고 수", "개", (lambda: ("-", "건너뜀")) if args.no_build else m3_build),
        ("M4 버그 수정 건수", "건", lambda: m4_fix_commits(start, end)),
        ("M5 다운로드 증가", "회", lambda: m5_downloads(start, end)),
    ]
    sys.stdout.reconfigure(encoding="utf-8")
    print("팀\t지표\t기간 시작\t기간 끝\t값\t단위\t비고")
    for name, unit, fn in metrics:
        try:
            value, note = fn()
        except Exception as e:
            value, note = "ERR", str(e)[:80]
        print(f"캠_개발팀\t{name}\t{start}\t{end}\t{value}\t{unit}\t{note}")


if __name__ == "__main__":
    main()
