"""DTTS の効果音（SE）合成スクリプト。

すべての音をサイン波・ノイズ・エンベロープ・フィルタの組み合わせでコードから合成する（外部素材なし・ライセンス不要）。
出力: 44.1kHz / 16bit / モノラル WAV
必要なもの: Python 3, numpy
使い方（リポジトリのルートで）: python -I tools/audio/gen_audio.py Assets/Audio
"""
import os
import sys
import wave

import numpy as np

SR = 44100
RNG = np.random.default_rng(20261113)  # 乱数を固定して毎回同じ音を生成する


# ----------------------------------------------------------------------
# 基本部品
# ----------------------------------------------------------------------

def t_axis(seconds):
    return np.arange(int(SR * seconds)) / SR


def silence(seconds):
    return np.zeros(int(SR * seconds))


def noise(seconds):
    return RNG.uniform(-1.0, 1.0, int(SR * seconds))


def sine(freq, seconds, phase=0.0):
    t = t_axis(seconds)
    f = np.broadcast_to(np.asarray(freq, dtype=float), t.shape)
    return np.sin(2 * np.pi * np.cumsum(f) / SR + phase)


def saw(freq, seconds):
    t = t_axis(seconds)
    ph = (freq * t) % 1.0
    return 2.0 * ph - 1.0


def triangle(freq, seconds):
    return 2.0 * np.abs(saw(freq, seconds)) - 1.0


def env_ad(n, attack, decay_tau):
    """立ち上がり attack 秒（線形）→ 時定数 decay_tau 秒の指数減衰。"""
    t = np.arange(n) / SR
    a = np.clip(t / max(attack, 1e-5), 0.0, 1.0)
    d = np.exp(-np.maximum(t - attack, 0.0) / decay_tau)
    return a * d


def env_adsr(n, a, d, s, r):
    """ADSR（秒）。サステイン区間は残り時間すべて。"""
    t = np.arange(n) / SR
    total = n / SR
    out = np.ones(n) * s
    out = np.where(t < a, t / max(a, 1e-5), out)
    dm = (t >= a) & (t < a + d)
    out = np.where(dm, 1.0 - (1.0 - s) * (t - a) / max(d, 1e-5), out)
    rm = t >= total - r
    out = np.where(rm, s * np.clip((total - t) / max(r, 1e-5), 0.0, 1.0), out)
    return out


def lowpass(x, cutoff):
    """1次ローパス（cutoff は定数または各サンプルの配列）。"""
    c = np.broadcast_to(np.asarray(cutoff, dtype=float), x.shape)
    alpha = 1.0 - np.exp(-2 * np.pi * c / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i in range(len(x)):
        acc += alpha[i] * (x[i] - acc)
        y[i] = acc
    return y


def highpass(x, cutoff):
    return x - lowpass(x, cutoff)


def bandpass(x, low, high):
    return lowpass(highpass(x, low), high)


def mix(*parts):
    n = max(len(p) for p in parts)
    out = np.zeros(n)
    for p in parts:
        out[: len(p)] += p
    return out


def place(x, at_seconds, total_seconds):
    out = np.zeros(int(SR * total_seconds))
    i = int(SR * at_seconds)
    out[i: i + len(x)] += x[: max(0, len(out) - i)]
    return out


def finish(x, peak_db=-1.0, fade_ms=8):
    """クリック防止の末尾フェードと、ピーク正規化。"""
    x = x.copy()
    f = int(SR * fade_ms / 1000)
    if f > 0 and len(x) > f:
        x[-f:] *= np.linspace(1.0, 0.0, f)
    x[:16] *= np.linspace(0.0, 1.0, 16)
    peak = np.max(np.abs(x))
    if peak > 0:
        x *= (10 ** (peak_db / 20)) / peak
    return x


def write_wav(path, x):
    pcm = np.clip(x, -1.0, 1.0)
    pcm = (pcm * 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


def note(name):
    """'A4' 形式の音名 → 周波数。"""
    names = {"C": -9, "C#": -8, "D": -7, "D#": -6, "E": -5, "F": -4, "F#": -3,
             "G": -2, "G#": -1, "A": 0, "A#": 1, "B": 2}
    pitch, octave = name[:-1], int(name[-1])
    return 440.0 * 2 ** ((names[pitch] + 12 * (octave - 4)) / 12)


# ----------------------------------------------------------------------
# 効果音
# ----------------------------------------------------------------------

def card_deal():
    """カードが配られる「シュッ」：帯域を絞ったノイズの短いスワイプ。"""
    dur = 0.11
    n = noise(dur)
    sweep = np.linspace(2500, 6500, len(n))
    body = lowpass(highpass(n, 900), sweep)
    e = env_ad(len(n), 0.012, 0.028)
    return finish(body * e, -3.0)


def card_play():
    """卓に出す「パシッ」：アタックのノイズ＋低い胴鳴り＋乾いたクリック。"""
    dur = 0.16
    slap = highpass(noise(dur), 1200) * env_ad(int(SR * dur), 0.0008, 0.007)
    thump = sine(np.linspace(210, 120, int(SR * dur)), dur) * env_ad(int(SR * dur), 0.001, 0.035)
    click = sine(1900, dur) * env_ad(int(SR * dur), 0.0005, 0.010)
    wood = bandpass(noise(dur), 400, 1600) * env_ad(int(SR * dur), 0.001, 0.020)
    return finish(mix(slap * 0.9, thump * 0.8, click * 0.35, wood * 0.6), -1.5)


def card_select():
    """手札選択の微かなクリック（他の音より小さく作る）。"""
    dur = 0.04
    tick = sine(2600, dur) * env_ad(int(SR * dur), 0.0006, 0.006)
    air = highpass(noise(dur), 3000) * env_ad(int(SR * dur), 0.0004, 0.003)
    return finish(mix(tick, air * 0.4), -14.0)


def field_flush():
    """場が流れる「サッ」：カットオフが上下するノイズの風切り。"""
    dur = 0.55
    n = noise(dur)
    t = t_axis(dur)
    cutoff = 500 + 5200 * np.sin(np.pi * np.clip(t / dur, 0, 1)) ** 1.5
    body = lowpass(highpass(n, 250), cutoff)
    e = env_adsr(len(n), 0.16, 0.12, 0.55, 0.26)
    return finish(body * e, -2.0, fade_ms=30)


def cutin_revolution():
    """革命：ノイズのライザー → ブラス風の和音ヒット＋サブの重低音。"""
    total = 1.6
    rise_len = 0.55
    rn = noise(rise_len)
    riser = lowpass(highpass(rn, 300), np.linspace(400, 9000, len(rn))) * np.linspace(0.0, 1.0, len(rn)) ** 2
    riser_tone = sine(np.linspace(110, 880, len(rn)), rise_len) * np.linspace(0, 0.5, len(rn)) ** 2

    hit_len = 1.05
    chord = sum(saw(note(n), hit_len) for n in ("A2", "E3", "A3", "C#4", "E4")) / 5
    brass = lowpass(chord, np.linspace(5200, 900, int(SR * hit_len))) * env_adsr(int(SR * hit_len), 0.015, 0.25, 0.55, 0.55)
    sub = sine(np.linspace(70, 42, int(SR * hit_len)), hit_len) * env_ad(int(SR * hit_len), 0.004, 0.35)
    impact = lowpass(noise(hit_len), 2500) * env_ad(int(SR * hit_len), 0.001, 0.06)

    out = mix(place(riser * 0.6 + riser_tone * 0.3, 0.0, total),
              place(brass * 0.9 + sub * 1.0 + impact * 0.8, rise_len, total))
    return finish(out, -1.0, fade_ms=40)


def cutin_special():
    """特殊効果のアクセント：明るいベル系の2音＋きらめき。"""
    total = 0.62
    def bell(freq, dur):
        n = int(SR * dur)
        car = sine(freq, dur) + 0.5 * sine(freq * 2.76, dur) * np.exp(-np.arange(n) / SR / 0.05)
        return car * env_ad(n, 0.002, 0.18)
    a = bell(note("E6"), 0.4)
    b = bell(note("B6"), 0.45)
    sparkle = highpass(noise(0.3), 6000) * env_ad(int(SR * 0.3), 0.004, 0.06)
    hit = sine(np.linspace(320, 180, int(SR * 0.12)), 0.12) * env_ad(int(SR * 0.12), 0.001, 0.03)
    out = mix(place(hit * 0.7, 0.0, total), place(a * 0.55, 0.0, total),
              place(b * 0.55, 0.09, total), place(sparkle * 0.25, 0.02, total))
    return finish(out, -1.5, fade_ms=25)


def tone(freq, dur, kind="tri"):
    n = int(SR * dur)
    if kind == "tri":
        x = triangle(freq, dur) * 0.8 + sine(freq * 2, dur) * 0.15
    else:
        x = lowpass(saw(freq, dur), 2600)
    vib = 1.0 + 0.004 * sine(5.5, dur)
    return x * vib[:n]


def victory_jingle():
    """あがり：明るい長三和音の上行アルペジオ（最後の音を伸ばす）。"""
    seq = [("C5", 0.00, 0.16), ("E5", 0.11, 0.16), ("G5", 0.22, 0.16), ("C6", 0.33, 0.75)]
    total = 1.2
    parts = []
    for name, at, dur in seq:
        x = tone(note(name), dur) * env_adsr(int(SR * dur), 0.006, 0.08, 0.6, min(0.25, dur * 0.6))
        parts.append(place(x, at, total))
    pad = sum(tone(note(n), 0.85) for n in ("C4", "G4", "E5")) / 3
    pad = lowpass(pad, 1800) * env_adsr(int(SR * 0.85), 0.05, 0.2, 0.5, 0.4)
    parts.append(place(pad * 0.5, 0.33, total))
    return finish(mix(*parts), -1.5, fade_ms=60)


def defeat_jingle():
    """転落：減七の和音を下行（くぐもった音色で短く）。"""
    seq = [("G4", 0.00, 0.22), ("E4", 0.18, 0.22), ("C#4", 0.36, 0.22), ("A#3", 0.54, 0.6)]
    total = 1.25
    parts = []
    for name, at, dur in seq:
        x = tone(note(name), dur, kind="saw") * env_adsr(int(SR * dur), 0.01, 0.1, 0.55, min(0.3, dur * 0.6))
        parts.append(place(x, at, total))
    low = sine(note("A#2"), 0.7) * env_ad(int(SR * 0.7), 0.02, 0.3)
    parts.append(place(low * 0.6, 0.54, total))
    return finish(lowpass(mix(*parts), 2200), -2.0, fade_ms=60)


SOUNDS = {
    "card_deal.wav": card_deal,
    "card_play.wav": card_play,
    "card_select.wav": card_select,
    "field_flush.wav": field_flush,
    "cutin_revolution.wav": cutin_revolution,
    "cutin_special.wav": cutin_special,
    "victory_jingle.wav": victory_jingle,
    "defeat_jingle.wav": defeat_jingle,
}


def main(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    for name, fn in SOUNDS.items():
        x = fn()
        write_wav(os.path.join(out_dir, name), x)
        print(f"{name}: {len(x) / SR:.2f}s, peak {20 * np.log10(np.max(np.abs(x)) + 1e-9):.1f} dBFS")


if __name__ == "__main__":
    main(sys.argv[1])
