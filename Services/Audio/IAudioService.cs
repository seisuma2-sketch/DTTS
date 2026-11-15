using System;
using System.Threading.Tasks;
using DTTS.Services.Settings;

namespace DTTS.Services.Audio;

/// <summary>効果音の種類。</summary>
public enum SoundEffect : byte
{
    /// <summary>カードが配られる。</summary>
    CardDeal = 0,
    /// <summary>カードを場に出す。</summary>
    CardPlay,
    /// <summary>手札を選択する。</summary>
    CardSelect,
    /// <summary>場が流れる。</summary>
    FieldFlush,
    /// <summary>革命系のカットイン。</summary>
    CutInRevolution,
    /// <summary>その他の特殊効果のカットイン。</summary>
    CutInSpecial,
    /// <summary>あがり・勝利。</summary>
    VictoryJingle,
    /// <summary>都落ち・大貧民確定などの転落。</summary>
    DefeatJingle,
}

/// <summary>BGM の曲。</summary>
public enum BgmTrack : byte
{
    /// <summary>BGM なし。</summary>
    None = 0,
    /// <summary>通常時：落ち着いたラウンジ・ジャズ。</summary>
    Lounge,
    /// <summary>緊張：革命中・あがりが迫ったとき。</summary>
    Tension,
}

/// <summary>
/// 効果音の再生。ゲーム進行（Core）とは独立し、UI層がゲームイベントを受けて「鳴らすだけ」の後追い演出に使う。
/// 再生の完了を待つAPIは持たない（音でゲーム進行を止めない）。
/// </summary>
public interface IAudioService : IDisposable
{
    /// <summary>音声出力を使える状態か（出力デバイスが無い環境では false のまま、再生要求は無視される）。</summary>
    bool IsAvailable { get; }

    /// <summary>サウンドが有効か。</summary>
    bool IsEnabled { get; }

    /// <summary>効果音の音量（0.0〜1.0）。</summary>
    double SeVolume { get; }

    /// <summary>BGM の音量（0.0〜1.0）。</summary>
    double BgmVolume { get; }

    /// <summary>流している（流そうとしている）BGM。</summary>
    BgmTrack CurrentBgm { get; }

    /// <summary>音声出力を準備し、全効果音を読み込む。何度呼んでも初期化は1回だけ。</summary>
    Task InitializeAsync();

    /// <summary>効果音を鳴らす（無効・音量0・準備前は何もしない）。</summary>
    void Play(SoundEffect effect);

    /// <summary>
    /// BGM を切り替える（前の曲はフェードアウト、新しい曲はフェードインして継ぎ目なくループする）。
    /// 同じ曲なら何もしない。<see cref="BgmTrack.None"/> でフェードアウトして止める。
    /// サウンドが無効・BGM 音量 0 のときは鳴らないが、どの曲を流すかは覚えておき、有効になったらその曲を流す。
    /// </summary>
    void PlayBgm(BgmTrack track);

    /// <summary>サウンドの有効・無効を切り替える。</summary>
    void SetSoundEnabled(bool enabled);

    /// <summary>効果音の音量を設定する（0.0〜1.0）。</summary>
    void SetSeVolume(double volume);

    /// <summary>BGM の音量を設定する（0.0〜1.0）。</summary>
    void SetBgmVolume(double volume);

    /// <summary>外見設定のサウンド項目をまとめて反映する。</summary>
    void ApplySettings(AppearanceSettings settings);
}

/// <summary>効果音ファイルの対応表。</summary>
public static class SoundEffectCatalog
{
    /// <summary>効果音を格納するフォルダ（アプリのインストール先からの相対パス）。</summary>
    public const string RelativeDirectory = "Assets\\Audio";

    /// <summary>ファイル名。</summary>
    public static string FileName(SoundEffect effect) => effect switch
    {
        SoundEffect.CardDeal => "card_deal.wav",
        SoundEffect.CardPlay => "card_play.wav",
        SoundEffect.CardSelect => "card_select.wav",
        SoundEffect.FieldFlush => "field_flush.wav",
        SoundEffect.CutInRevolution => "cutin_revolution.wav",
        SoundEffect.CutInSpecial => "cutin_special.wav",
        SoundEffect.VictoryJingle => "victory_jingle.wav",
        SoundEffect.DefeatJingle => "defeat_jingle.wav",
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };

    /// <summary>BGM のファイル名（<see cref="BgmTrack.None"/> は null）。</summary>
    public static string? BgmFileName(BgmTrack track) => track switch
    {
        BgmTrack.Lounge => "bgm_lounge.wav",
        BgmTrack.Tension => "bgm_tension.wav",
        _ => null,
    };

    /// <summary>
    /// BGM の基準の大きさ（効果音より控えめにする）。スライダー 100% でもこの倍率まで。
    /// </summary>
    public const double BgmHeadroom = 0.55;

    /// <summary>
    /// 同じ音を同時に鳴らせる数。連続で起きる音（配る・出す・選ぶ）は複数のボイスを交互に使い、
    /// 前の音を途中で切らずに重ねて鳴らす。
    /// </summary>
    public static int Polyphony(SoundEffect effect) => effect switch
    {
        SoundEffect.CardDeal => 4,
        SoundEffect.CardPlay => 3,
        SoundEffect.CardSelect => 3,
        SoundEffect.FieldFlush => 2,
        _ => 1,
    };

    /// <summary>
    /// 音量スライダー値（0.0〜1.0）を出力ゲインに変換する。
    /// 人の音量感覚に近づけるため2乗カーブにする（50% で約 -12dB）。
    /// </summary>
    public static double ToGain(double volume)
    {
        var v = Math.Clamp(double.IsFinite(volume) ? volume : 0.0, 0.0, 1.0);
        return v * v;
    }
}
