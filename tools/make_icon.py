"""Desktop CAM 로고·앱 아이콘 생성기 (Re:Legacy 디자인 시스템).

16×16 픽셀 격자에 그린 뒤 정수 배율로만 키워서 어떤 크기에서도 픽셀이 또렷하게 보이게 한다.
  - 바탕: bg #14122B, 모서리만 2칸 깎은 사각형 (앱 아이콘에만 허용되는 둥근 모서리의 픽셀 버전)
  - 프롬프트 '>': teal, 오른쪽 아래 1칸 teal-deep 그림자
  - 커서 '_': yellow, orange 그림자
  - 녹화 점: danger, 오른쪽 위

사용: py tools/make_icon.py  →  src/DesktopCam/Assets/DesktopCam.ico, logo.png
"""
from pathlib import Path
from PIL import Image

BG, TEAL, TEAL_DEEP = (0x14, 0x12, 0x2B), (0x3B, 0xCE, 0xAC), (0x1F, 0x7A, 0x68)
YELLOW, ORANGE, DANGER = (0xFF, 0xD2, 0x3F), (0xE4, 0x57, 0x2E), (0xFF, 0x84, 0x60)
N = 16

def base() -> Image.Image:
    im = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    px = im.load()
    corners = {(0, 0), (N - 1, 0), (0, N - 1), (N - 1, N - 1)}   # 모서리 1칸만 깎기
    for y in range(N):
        for x in range(N):
            if (x, y) not in corners:
                px[x, y] = BG + (255,)

    def put(cells, color, shadow=None):
        # 그림자: 흐림 없이 오른쪽 아래로 1칸 밀어낸 단색 (shadow-deep / shadow-hot)
        if shadow:
            for x, y in cells:
                if (x + 1, y + 1) not in cells:
                    px[x + 1, y + 1] = shadow + (255,)
        for x, y in cells:
            px[x, y] = color + (255,)

    # '>' : 2칸 굵기, 한 줄에 1칸씩 계단 (y 5~11, 꼭짓점 y 8)
    chevron = set()
    for y in range(5, 12):
        x = 2 + (3 - abs(y - 8))
        chevron |= {(x, y), (x + 1, y)}
    put(chevron, TEAL, TEAL_DEEP)
    # '_' : 5×2, 바닥선에 맞춤
    put({(x, y) for x in range(8, 13) for y in (10, 11)}, YELLOW, ORANGE)
    # 녹화 점: 2×2, 오른쪽 위
    put({(11, 4), (12, 4), (11, 5), (12, 5)}, DANGER)
    return im

def to_svg(im: Image.Image, with_bg: bool = True) -> str:
    """픽셀 격자를 SVG 사각형으로. 같은 색이 가로로 이어지면 한 칸으로 합친다.
    viewBox 는 16×16 이고 crispEdges 로 그려서 어떤 크기로 키워도 픽셀이 뭉개지지 않는다."""
    px = im.load()
    rects = []
    for y in range(N):
        x = 0
        while x < N:
            c = px[x, y]
            if c[3] == 0 or (not with_bg and c[:3] == BG):
                x += 1
                continue
            start = x
            while x < N and px[x, y] == c:
                x += 1
            rects.append(f'<rect x="{start}" y="{y}" width="{x - start}" height="1" fill="#{c[0]:02X}{c[1]:02X}{c[2]:02X}"/>')
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" width="512" height="512" '
            'shape-rendering="crispEdges">\n'
            '  <title>Desktop CAM · &gt; re:legacy_</title>\n  '
            + "\n  ".join(rects) + "\n</svg>\n")

def main():
    root = Path(__file__).resolve().parent.parent
    out = root / "src" / "DesktopCam" / "Assets"
    brand = root / "brand"          # 나중에 다른 곳에서도 쓸 원본 모음
    out.mkdir(parents=True, exist_ok=True)
    brand.mkdir(exist_ok=True)
    b = base()
    sizes = [16, 32, 48, 64, 128, 256]
    imgs = [b.resize((s, s), Image.NEAREST) for s in sizes]
    imgs[-1].save(out / "DesktopCam.ico", sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
    b.resize((64, 64), Image.NEAREST).save(out / "logo.png")
    b.resize((512, 512), Image.NEAREST).save(out / "logo_512.png")

    # 벡터 원본: 바탕 포함(앱 아이콘) / 바탕 없음(어두운 면 위에 얹는 마크)
    (brand / "desktopcam-icon.svg").write_text(to_svg(b, True), encoding="utf-8")
    (brand / "desktopcam-mark.svg").write_text(to_svg(b, False), encoding="utf-8")
    for name in ("DesktopCam.ico", "logo_512.png"):
        (brand / name).write_bytes((out / name).read_bytes())
    print("saved", out, brand)

if __name__ == "__main__":
    main()
