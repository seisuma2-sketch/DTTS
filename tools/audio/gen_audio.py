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


# ----------------------------------------------------------------------
# BGM（テーブルトップ向けの落ち着いたラウンジ・ジャズ。継ぎ目なくループする）
# ----------------------------------------------------------------------
# 効果音とは別の乱数を使う（効果音の生成結果を変えないため）。サンプリング周波数も別（ファイルを小さくする）。

BGM_SR = 32000
BGM_RNG = np.random.default_rng(20261116)


def midi_hz(m):
    return 440.0 * 2 ** ((m - 69) / 12)


def fft_filter(x, low=None, high=None, sr=BGM_SR, slope_hz=200.0):
    """FFT による帯域制限（なだらかな肩を付けて、リンギングを抑える）。長い信号でも速い。"""
    spec = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1.0 / sr)
    gain = np.ones_like(f)
    if low is not None:
        gain *= np.clip((f - (low - slope_hz)) / slope_hz, 0.0, 1.0)
    if high is not None:
        gain *= np.clip(((high + slope_hz) - f) / slope_hz, 0.0, 1.0)
    return np.fft.irfft(spec * gain, len(x))


def ep_note(freq, dur, velocity=1.0, sr=BGM_SR):
    """エレクトリックピアノ（ローズ風）：FM の柔らかいタインの立ち上がり＋丸い減衰。"""
    rel = 0.35
    n = int(sr * (dur + rel))
    t = np.arange(n) / sr
    index = 1.1 * np.exp(-t / 0.18) + 0.25
    tone = np.sin(2 * np.pi * freq * t + index * np.sin(2 * np.pi * freq * t))
    tone += 0.18 * np.sin(2 * np.pi * freq * 0.5 * t)                       # 胴鳴り（1オクターブ下を少し）
    amp = np.clip(t / 0.006, 0.0, 1.0) * np.exp(-t / 1.6)
    amp *= np.clip((dur + rel - t) / rel, 0.0, 1.0)                            # 離鍵
    trem = 1.0 + 0.08 * np.sin(2 * np.pi * 4.3 * t)
    return tone * amp * trem * velocity


def bass_note(freq, dur, sr=BGM_SR):
    """ウッドベース：はじいた立ち上がり・倍音少なめ・指で止める短い余韻。"""
    n = int(sr * (dur + 0.08))
    t = np.arange(n) / sr
    tone = np.sin(2 * np.pi * freq * t) + 0.35 * np.sin(2 * np.pi * freq * 2 * t) * np.exp(-t / 0.12)
    tone += 0.12 * np.sin(2 * np.pi * freq * 3 * t) * np.exp(-t / 0.05)
    amp = np.clip(t / 0.008, 0.0, 1.0) * np.exp(-t / 0.55)
    amp *= np.clip((dur + 0.08 - t) / 0.08, 0.0, 1.0)
    return tone * amp


CHORD_SHAPES = {
    # 根音を省いた（ルートレス）ボイシング。3度・7度を中心に、テンションを少し
    "maj7": [4, 7, 11, 14],
    "m7": [3, 7, 10, 14],
    "7": [4, 9, 10, 14],
    "7b9": [4, 10, 13, 16],
    "m7b5": [3, 6, 10],
    "dim7": [3, 6, 9, 12],
    "7sus": [5, 10, 14],
    "m6": [3, 7, 9, 14],
}


def voicing(root, quality, low=53, high=74):
    notes = []
    for iv in CHORD_SHAPES[quality]:
        m = root + iv
        while m < low:
            m += 12
        while m > high:
            m -= 12
        notes.append(m)
    return sorted(set(notes))


def walking_line(chords, beats_per_bar=4):
    """各小節：根音 → 和音の音 → 和音の音 → 次の根音への半音アプローチ。"""
    line = []
    for i, (root, quality) in enumerate(chords):
        nxt = chords[(i + 1) % len(chords)][0]
        r = 36 + (root % 12)                    # C2 付近
        if r > 43:
            r -= 12
        third = 3 if quality in ("m7", "m7b5", "dim7", "m6") else 4
        fifth = 6 if quality in ("m7b5", "dim7") else 7
        n_next = 36 + (nxt % 12)
        if n_next > 43:
            n_next -= 12
        approach = n_next + (1 if n_next < r + 5 else -1)
        line += [r, r + third, r + fifth, approach][:beats_per_bar]
    return line


def render_lounge(chords, bpm, comp_pattern, swirl_level, out_peak_db=-4.0, darker=False):
    """コード進行をローズ・ウッドベース・ブラシで演奏し、継ぎ目なくループする1本の音にする。"""
    sr = BGM_SR
    beat = 60.0 / bpm
    bar = beat * 4
    length = bar * len(chords)
    tail = 3.0
    total = length + tail
    n_total = int(sr * total)
    ep = np.zeros(n_total)
    bass = np.zeros(n_total)

    def add(buf, x, at):
        i = int(sr * at)
        j = min(n_total, i + len(x))
        if i < n_total:
            buf[i:j] += x[: j - i]

    # --- ローズのコンピング（小節内の位置[拍], 長さ[拍], 強さ） ---
    for b, (root, quality) in enumerate(chords):
        notes = voicing(root, quality, low=50 if darker else 53, high=70 if darker else 74)
        for pos, length_beats, vel in comp_pattern(b):
            for k, m in enumerate(notes):
                # わずかにばらして弾く（人が弾いたような柔らかさ）
                spread = 0.012 * k + BGM_RNG.uniform(0.0, 0.006)
                v = vel * (0.85 + 0.15 * BGM_RNG.random())
                add(ep, ep_note(midi_hz(m), length_beats * beat * 0.92, v), b * bar + pos * beat + spread)

    # --- ウッドベース（4分音符のウォーキング） ---
    line = walking_line(chords)
    for i, m in enumerate(line):
        accent = 1.0 if i % 4 == 0 else 0.82
        add(bass, bass_note(midi_hz(m), beat * 0.9) * accent, i * beat + BGM_RNG.uniform(0.0, 0.004))

    # --- ブラシ（スネアをなでる音と、2・4拍目の軽いタップ） ---
    t = np.arange(n_total) / sr
    swirl_noise = fft_filter(BGM_RNG.uniform(-1, 1, n_total), low=1800, high=7500)
    beat_phase = (t / beat) % 1.0
    swirl_env = swirl_level * (0.55 + 0.45 * np.sin(np.pi * beat_phase) ** 2)
    brushes = swirl_noise * swirl_env
    tap_len = int(sr * 0.09)
    tap_env = np.exp(-np.arange(tap_len) / sr / 0.025)
    for i in range(int(length / beat)):
        if i % 2 == 1:
            tap = fft_filter(BGM_RNG.uniform(-1, 1, tap_len), low=1200, high=6000) * tap_env * swirl_level * 2.2
            add(brushes, tap, i * beat)

    ep = fft_filter(ep, high=3800 if darker else 5200)
    bass = fft_filter(bass, high=900)
    out = 0.6 * ep + 0.55 * bass + brushes

    # 継ぎ目なくループさせる：最後からはみ出した余韻を先頭に重ねて、ちょうど length の長さにする
    n_loop = int(sr * length)
    loop = out[:n_loop].copy()
    over = out[n_loop:]
    loop[: len(over)] += over

    peak = np.max(np.abs(loop))
    loop *= (10 ** (out_peak_db / 20)) / peak
    return loop


def bgm_lounge():
    """通常時：F メジャーの穏やかな進行（16小節・84bpm）。"""
    F, Bb, C, D, G, A, Ab, E = 41, 46, 36, 38, 43, 45, 44, 40
    chords = [
        (F, "maj7"), (D, "7b9"), (G, "m7"), (C, "7"),
        (A, "m7"), (D, "7"), (G, "m7"), (C, "7"),
        (F, "maj7"), (Bb, "maj7"), (A, "m7"), (Ab, "dim7"),
        (G, "m7"), (C, "7sus"), (F, "maj7"), (C, "7"),
    ]

    def comp(bar_index):
        # チャールストン（1拍目と2拍目の裏）を基本に、ときどき4拍目の裏に軽く足す
        hits = [(0.0, 1.4, 0.8), (1.5, 2.0, 0.6)]
        if bar_index % 4 == 3:
            hits.append((3.5, 0.5, 0.45))
        return hits

    return render_lounge(chords, 84, comp, swirl_level=0.07)


def bgm_tension():
    """緊張（革命中・あがりが迫ったとき）：D マイナーの陰りのある進行（8小節×2・96bpm）。"""
    D, Bb, E, A, G, C, F = 38, 46, 40, 45, 43, 36, 41
    phrase = [
        (D, "m6"), (D, "m7"), (Bb, "maj7"), (A, "7b9"),
        (G, "m7"), (C, "7"), (E, "m7b5"), (A, "7b9"),
    ]
    chords = phrase + phrase

    def comp(bar_index):
        # 2拍ごとに押し気味に弾き、4拍目の裏で次の小節へつなぐ
        return [(0.0, 0.9, 0.75), (1.5, 0.9, 0.55), (2.5, 0.9, 0.6), (3.5, 0.5, 0.5)]

    return render_lounge(chords, 96, comp, swirl_level=0.085, darker=True)


def write_wav_sr(path, x, sr):
    pcm = (np.clip(x, -1.0, 1.0) * 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(pcm.tobytes())


BGMS = {
    "bgm_lounge.wav": bgm_lounge,
    "bgm_tension.wav": bgm_tension,
}


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


def main(out_dir, only=None):
    os.makedirs(out_dir, exist_ok=True)
    if only in (None, "se"):
        for name, fn in SOUNDS.items():
            x = fn()
            write_wav(os.path.join(out_dir, name), x)
            print(f"{name}: {len(x) / SR:.2f}s, peak {20 * np.log10(np.max(np.abs(x)) + 1e-9):.1f} dBFS")
    if only in (None, "bgm"):
        for name, fn in BGMS.items():
            x = fn()
            write_wav_sr(os.path.join(out_dir, name), x, BGM_SR)
            rms = 20 * np.log10(np.sqrt(np.mean(x ** 2)) + 1e-9)
            # ループの継ぎ目：最後と最初のサンプルの差（小さいほどプチッと鳴らない）
            seam = abs(x[-1] - x[0])
            print(f"{name}: {len(x) / BGM_SR:.2f}s loop, rms {rms:.1f} dBFS, seam {seam:.4f}")


if __name__ == "__main__":
    # 使い方: python -I tools/audio/gen_audio.py Assets/Audio [se|bgm]
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
