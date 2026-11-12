"""DTTS のブランドアセット生成スクリプト。

すべての図柄をコードで描き、目標サイズの 4 倍で描画してから LANCZOS で縮小する（アンチエイリアス）。
必要なもの: Python 3, Pillow, numpy, Windows の Segoe UI フォント（Black / Semibold / Symbol）
使い方（リポジトリのルートで）: python -I tools/branding/gen_assets.py Assets
確認用の一覧画像は OS の一時フォルダに dtts_assets_preview.png として出力する。
"""
import math
import os
import sys
import tempfile

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

FONTS = r"C:\Windows\Fonts"
F_BLACK = os.path.join(FONTS, "seguibl.ttf")   # Segoe UI Black（ロゴ文字）
F_SEMI = os.path.join(FONTS, "seguisb.ttf")    # Segoe UI Semibold（サブタイトル）
F_SYM = os.path.join(FONTS, "seguisym.ttf")    # Segoe UI Symbol（♠♥♦♣）

# --- 配色（ソフトダーク＋Material 3 風グラデーション） ---
BG_TOP = (0x1A, 0x1E, 0x27)
BG_BOTTOM = (0x12, 0x14, 0x1A)
ACCENT_A = (0x3E, 0x8E, 0xF7)   # ディープアズール
ACCENT_B = (0x9B, 0x6C, 0xFF)   # バイオレットパープル
CARD_FACE = (0xEC, 0xEE, 0xF4)
CARD_EDGE = (0xC9, 0xCD, 0xD8)
INK = (0x1A, 0x1E, 0x27)
RED = (0xE5, 0x48, 0x4D)
MUTED = (0x8A, 0x91, 0xA3)
SS = 4  # スーパーサンプリング倍率


def gradient(w, h, c1, c2, angle_deg=135.0):
    """c1→c2 の線形グラデーション（angle_deg は 0=左→右, 90=上→下）。"""
    a = math.radians(angle_deg)
    dx, dy = math.cos(a), math.sin(a)
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    t = (xs * dx + ys * dy)
    t = (t - t.min()) / max(1e-6, (t.max() - t.min()))
    c1 = np.array(c1, np.float32)
    c2 = np.array(c2, np.float32)
    rgb = c1[None, None, :] * (1 - t[..., None]) + c2[None, None, :] * t[..., None]
    alpha = np.full((h, w, 1), 255, np.float32)
    return Image.fromarray(np.concatenate([rgb, alpha], axis=2).astype(np.uint8), "RGBA")


def rounded_mask(w, h, r):
    m = Image.new("L", (w, h), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, w - 1, h - 1], radius=r, fill=255)
    return m


def glyph_mask(text, font_path, size_px, box_w, box_h):
    """文字を box の中央に置いた L マスク（字形の実寸で中央合わせ）。"""
    font = ImageFont.truetype(font_path, int(size_px))
    m = Image.new("L", (box_w, box_h), 0)
    d = ImageDraw.Draw(m)
    l, t, r, b = d.textbbox((0, 0), text, font=font)
    d.text(((box_w - (r - l)) / 2 - l, (box_h - (b - t)) / 2 - t), text, font=font, fill=255)
    return m


def fill_through(mask, fill):
    """マスクの形に塗る（fill は色タプルまたは RGBA 画像）。"""
    w, h = mask.size
    src = fill if isinstance(fill, Image.Image) else Image.new("RGBA", (w, h), fill + (255,))
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    out.paste(src, (0, 0), mask)
    return out


def shadow(layer, blur, opacity, offset):
    """レイヤーのアルファから落ち影を作る。"""
    a = layer.split()[3].point(lambda v: int(v * opacity))
    sh = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    sh.putalpha(a)
    sh = sh.filter(ImageFilter.GaussianBlur(blur))
    out = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    out.alpha_composite(sh, offset)
    return out


def make_card(cw, ch, face, back=False, outline=None):
    """カード1枚（表：♠ の大きなグラデーション、裏：アクセントのグラデーション）。"""
    r = int(cw * 0.12)
    card = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
    m = rounded_mask(cw, ch, r)
    if back:
        card.paste(gradient(cw, ch, ACCENT_A, ACCENT_B, 120), (0, 0), m)
        inset = int(cw * 0.09)
        d = ImageDraw.Draw(card)
        d.rounded_rectangle([inset, inset, cw - inset, ch - inset], radius=int(r * 0.6),
                            outline=(255, 255, 255, 110), width=max(2, int(cw * 0.025)))
        # 中央の小さな ♦
        dm = glyph_mask("\u2666", F_SYM, cw * 0.42, cw, ch)
        card.alpha_composite(_alpha(fill_through(dm, (255, 255, 255)), 0.55))
    else:
        card.paste(Image.new("RGBA", (cw, ch), face + (255,)), (0, 0), m)
        d = ImageDraw.Draw(card)
        d.rounded_rectangle([0, 0, cw - 1, ch - 1], radius=r, outline=outline or CARD_EDGE + (255,),
                            width=max(2, int(cw * (0.035 if outline else 0.018))))
        # 大きな ♠（アクセントグラデーション）
        sm = glyph_mask("\u2660", F_SYM, cw * 0.95, cw, int(ch * 1.02))
        sm = sm.crop((0, 0, cw, ch)) if sm.size != (cw, ch) else sm
        card.alpha_composite(fill_through(sm, gradient(cw, ch, ACCENT_A, ACCENT_B, 110)))
        # 左上の小さな ♥（赤）
        hm = glyph_mask("\u2665", F_SYM, cw * 0.24, int(cw * 0.34), int(cw * 0.34))
        corner = fill_through(hm, RED)
        card.alpha_composite(corner, (int(cw * 0.05), int(cw * 0.05)))
    return card


def _alpha(img, k):
    r, g, b, a = img.split()
    return Image.merge("RGBA", (r, g, b, a.point(lambda v: int(v * k))))


def paste_rotated(canvas, layer, cx, cy, angle):
    rot = layer.rotate(angle, resample=Image.BICUBIC, expand=True)
    canvas.alpha_composite(rot, (int(cx - rot.width / 2), int(cy - rot.height / 2)))


def emblem(size, plate=True, simple=False, light=False):
    """正方形のアプリアイコン。plate=角丸の暗いプレート付き、simple=小サイズ用の1枚カード。"""
    S = size * SS
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    if plate:
        r = int(S * 0.22)
        canvas.paste(gradient(S, S, BG_TOP, BG_BOTTOM, 90), (0, 0), rounded_mask(S, S, r))
        # 上辺のハイライトリング（M3 風の柔らかい縁）
        ring = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ImageDraw.Draw(ring).rounded_rectangle([1, 1, S - 2, S - 2], radius=r,
                                               outline=ACCENT_A + (70,), width=max(2, int(S * 0.012)))
        canvas.alpha_composite(ring)
        # アクセントのにじみ（右下）
        glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ImageDraw.Draw(glow).ellipse([S * 0.45, S * 0.45, S * 1.25, S * 1.25], fill=ACCENT_B + (60,))
        glow = glow.filter(ImageFilter.GaussianBlur(S * 0.12))
        glow_masked = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        glow_masked.paste(glow, (0, 0), rounded_mask(S, S, r))
        canvas.alpha_composite(glow_masked)

    outline = (0x5A, 0x60, 0x70, 255) if light else None
    span = 0.80 if plate else 0.98  # カード群の占める割合
    if simple:
        cw = int(S * 0.56 * span / 0.8)
        ch = int(cw * 1.36)
        card = make_card(cw, ch, CARD_FACE, outline=outline)
        layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        layer.alpha_composite(card, ((S - cw) // 2, (S - ch) // 2))
        canvas.alpha_composite(shadow(layer, S * 0.02, 0.45, (0, int(S * 0.015))))
        canvas.alpha_composite(layer)
    else:
        cw = int(S * 0.46 * span / 0.8)
        ch = int(cw * 1.38)
        layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        paste_rotated(layer, make_card(cw, ch, CARD_FACE, back=True), S * 0.40, S * 0.50, 14)
        paste_rotated(layer, make_card(cw, ch, CARD_FACE, outline=outline), S * 0.59, S * 0.52, -9)
        canvas.alpha_composite(shadow(layer, S * 0.025, 0.55, (0, int(S * 0.02))))
        canvas.alpha_composite(layer)
    return canvas.resize((size, size), Image.LANCZOS)


def wordmark_layer(w, h, text_h, subtitle=True):
    """『DTTS』＋サブタイトル＋スート列のブロック（透明背景）。"""
    layer = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    font = ImageFont.truetype(F_BLACK, int(text_h))
    d = ImageDraw.Draw(layer)
    l, t, r, b = d.textbbox((0, 0), "DTTS", font=font)
    tw, th = r - l, b - t
    m = Image.new("L", (w, h), 0)
    ImageDraw.Draw(m).text((-l, -t), "DTTS", font=font, fill=255)
    grad = gradient(w, h, (0xF2, 0xF4, 0xFA), (0xB9, 0xC8, 0xFF), 90)
    layer.alpha_composite(fill_through(m, grad))
    y = th + text_h * 0.18
    if subtitle:
        sf = ImageFont.truetype(F_SEMI, int(text_h * 0.20))
        sub = "DAIFUGO  TABLETOP  SUITE"
        d.text((text_h * 0.02, y), sub, font=sf, fill=MUTED + (255,))
        y += text_h * 0.30
    # スート列（♠♦ はアクセント、♥♣…♥♦ は赤）
    suits = [("\u2660", ACCENT_A), ("\u2665", RED), ("\u2666", RED), ("\u2663", ACCENT_B)]
    sz = int(text_h * 0.30)
    x = text_h * 0.02
    for ch, col in suits:
        gm = glyph_mask(ch, F_SYM, sz, int(sz * 1.2), int(sz * 1.2))
        layer.alpha_composite(fill_through(gm, col), (int(x), int(y)))
        x += sz * 1.35
    bbox_w = max(tw, int(x))
    bbox_h = int(y + sz * 1.2)
    return layer, bbox_w, bbox_h


def banner(w, h, opaque):
    """ワイドタイル・スプラッシュ用の横長画像。"""
    W, H = w * SS, h * SS
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    if opaque:
        canvas.alpha_composite(gradient(W, H, BG_TOP, BG_BOTTOM, 90))
        glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        ImageDraw.Draw(glow).ellipse([-W * 0.1, H * 0.1, W * 0.45, H * 1.2], fill=ACCENT_A + (45,))
        canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(H * 0.25)))
    icon_px = int(H * 0.78)
    # アイコンは最終解像度相当（icon_px / SS）で描き、作業キャンバスの倍率に合わせて拡大する
    icon = emblem(max(16, icon_px // SS), plate=False).resize((icon_px, icon_px), Image.LANCZOS)
    text_h = H * 0.30
    wl, bw, bh = wordmark_layer(W, H, text_h)
    gap = H * 0.06
    total = icon_px + gap + bw
    x0 = (W - total) / 2
    canvas.alpha_composite(icon, (int(x0), int((H - icon_px) / 2)))
    canvas.alpha_composite(wl, (int(x0 + icon_px + gap), int((H - bh) / 2)))
    return canvas.resize((w, h), Image.LANCZOS)


def main(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    written = []

    def save(img, name):
        p = os.path.join(out_dir, name)
        img.save(p, optimize=True)
        written.append(name)

    scales = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}
    for sc, k in scales.items():
        s44 = round(44 * k)
        save(emblem(s44, plate=True, simple=s44 <= 24), f"Square44x44Logo.scale-{sc}.png")
        save(emblem(round(150 * k), plate=True), f"Square150x150Logo.scale-{sc}.png")
        save(banner(round(310 * k), round(150 * k), opaque=True), f"Wide310x150Logo.scale-{sc}.png")
        save(banner(round(620 * k), round(300 * k), opaque=False), f"SplashScreen.scale-{sc}.png")
        save(emblem(round(50 * k), plate=True), f"StoreLogo.scale-{sc}.png")
        save(emblem(round(24 * k), plate=False, simple=round(24 * k) <= 32), f"LockScreenLogo.scale-{sc}.png")

    # タスクバー・スタートメニュー用（targetsize：プレートなし／ライトテーマ用／プレートあり）
    for t in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        simple = t <= 24
        save(emblem(t, plate=True, simple=simple), f"Square44x44Logo.targetsize-{t}.png")
        save(emblem(t, plate=False, simple=simple), f"Square44x44Logo.targetsize-{t}_altform-unplated.png")
        save(emblem(t, plate=False, simple=simple, light=True), f"Square44x44Logo.targetsize-{t}_altform-lightunplated.png")

    # ウィンドウ・exe 用 .ico（各サイズを個別に描いたものを格納）
    ico_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = [emblem(s, plate=True, simple=s <= 24) for s in ico_sizes]
    imgs[-1].save(os.path.join(out_dir, "WindowIcon.ico"), format="ICO",
                  sizes=[(s, s) for s in ico_sizes], append_images=imgs[:-1])
    written.append("WindowIcon.ico")

    # 確認用の一覧（リポジトリには入れない）
    preview = Image.new("RGBA", (1400, 760), BG_BOTTOM + (255,))
    preview.alpha_composite(emblem(256, plate=True), (20, 20))
    preview.alpha_composite(emblem(256, plate=False), (300, 20))
    preview.alpha_composite(emblem(256, plate=False, light=True), (580, 20))
    lightbg = Image.new("RGBA", (300, 300), (0xF3, 0xF3, 0xF3, 255))
    lightbg.alpha_composite(emblem(256, plate=False, light=True), (22, 22))
    preview.alpha_composite(lightbg, (850, 0))
    x = 1170
    for s in (16, 24, 32, 48):
        preview.alpha_composite(emblem(s, plate=True, simple=s <= 24), (x, 40))
        preview.alpha_composite(emblem(s, plate=False, simple=s <= 24), (x, 120))
        x += s + 14
    preview.alpha_composite(banner(620, 300, opaque=True), (20, 300))
    preview.alpha_composite(banner(620, 300, opaque=False), (680, 300))
    preview.alpha_composite(banner(310, 150, opaque=True), (20, 610))
    preview_path = os.path.join(tempfile.gettempdir(), "dtts_assets_preview.png")
    preview.save(preview_path)
    print(f"wrote {len(written)} files (preview: {preview_path})")


if __name__ == "__main__":
    main(sys.argv[1])
