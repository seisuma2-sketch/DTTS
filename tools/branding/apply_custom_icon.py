"""カスタムアイコン（icon.png）から DTTS のブランドアセット一式を生成するスクリプト。

使い方（リポジトリのルートで）:
  python -I tools/branding/apply_custom_icon.py icon.png Assets
"""
import os
import sys

from PIL import Image

# 既存のロゴ・グラデーション生成ヘルパーを再利用する
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_assets import BG_BOTTOM, BG_TOP, SS, gradient, wordmark_layer
from PIL import ImageFilter


def fit_square(src: Image.Image, size: int, padding_ratio: float = 0.04) -> Image.Image:
    """透過画像を正方形キャンバスの中央に余白付きで配置してリサイズする。"""
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    inner = max(1, round(size * (1.0 - padding_ratio * 2)))
    w, h = src.size
    scale = min(inner / w, inner / h)
    nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
    resized = src.resize((nw, nh), Image.LANCZOS)
    canvas.alpha_composite(resized, ((size - nw) // 2, (size - nh) // 2))
    return canvas


def custom_banner(src: Image.Image, w: int, h: int, opaque: bool) -> Image.Image:
    """カスタムアイコン＋『DTTS』ワードマークの横長バナーを生成する。"""
    W, H = w * SS, h * SS
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    if opaque:
        canvas.alpha_composite(gradient(W, H, BG_TOP, BG_BOTTOM, 90))
        glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        from PIL import ImageDraw
        ImageDraw.Draw(glow).ellipse([-W * 0.1, H * 0.1, W * 0.45, H * 1.2], fill=(0xE5, 0x48, 0x4D, 50))
        canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(H * 0.25)))
    icon_px = int(H * 0.78)
    icon = fit_square(src, icon_px, padding_ratio=0.02)
    text_h = H * 0.30
    wl, bw, bh = wordmark_layer(W, H, text_h)
    gap = H * 0.06
    total = icon_px + gap + bw
    x0 = (W - total) / 2
    canvas.alpha_composite(icon, (int(x0), int((H - icon_px) / 2)))
    canvas.alpha_composite(wl, (int(x0 + icon_px + gap), int((H - bh) / 2)))
    return canvas.resize((w, h), Image.LANCZOS)


def main(icon_path: str, out_dir: str) -> None:
    src = Image.open(icon_path).convert("RGBA")
    # 透明でない領域でトリミングしてから均等に配置する
    bbox = src.getbbox()
    if bbox:
        src = src.crop(bbox)

    os.makedirs(out_dir, exist_ok=True)
    written = []

    def save(img: Image.Image, name: str) -> None:
        p = os.path.join(out_dir, name)
        img.save(p, optimize=True)
        written.append(name)

    scales = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}
    for sc, k in scales.items():
        save(fit_square(src, round(44 * k)), f"Square44x44Logo.scale-{sc}.png")
        save(fit_square(src, round(150 * k)), f"Square150x150Logo.scale-{sc}.png")
        save(custom_banner(src, round(310 * k), round(150 * k), opaque=True), f"Wide310x150Logo.scale-{sc}.png")
        save(custom_banner(src, round(620 * k), round(300 * k), opaque=False), f"SplashScreen.scale-{sc}.png")
        save(fit_square(src, round(50 * k)), f"StoreLogo.scale-{sc}.png")
        save(fit_square(src, round(24 * k)), f"LockScreenLogo.scale-{sc}.png")

    # タスクバー・スタートメニュー用の各 targetsize
    for t in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        img = fit_square(src, t)
        save(img, f"Square44x44Logo.targetsize-{t}.png")
        save(img, f"Square44x44Logo.targetsize-{t}_altform-unplated.png")
        save(img, f"Square44x44Logo.targetsize-{t}_altform-lightunplated.png")

    # ウィンドウ・exe・デスクトップショートカット用マルチサイズ .ico
    ico_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = [fit_square(src, s, padding_ratio=0.03) for s in ico_sizes]
    imgs[-1].save(
        os.path.join(out_dir, "WindowIcon.ico"),
        format="ICO",
        sizes=[(s, s) for s in ico_sizes],
        append_images=imgs[:-1],
    )
    written.append("WindowIcon.ico")
    print(f"wrote {len(written)} files to {out_dir}")


if __name__ == "__main__":
    icon = sys.argv[1] if len(sys.argv) > 1 else "icon.png"
    out = sys.argv[2] if len(sys.argv) > 2 else "Assets"
    main(icon, out)
