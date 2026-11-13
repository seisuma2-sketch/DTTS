using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace DTTS.Common;

/// <summary>
/// Composition API (Windows.UI.Composition / Microsoft.UI.Composition) を用いた
/// 60fps 物理アニメーション（Spring 自然振動・減衰）および演出用ヘルパー。
/// </summary>
public static class CompositionAnimationHelper
{
    /// <summary>
    /// UIElement の Translation（Y軸）を Spring 物理アニメーションで目標値へ移動します。
    /// </summary>
    public static void AnimateSpringTranslationY(UIElement element, float targetY, float dampingRatio = 0.65f, float periodMs = 220f)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var spring = compositor.CreateSpringVector3Animation();
        spring.Target = "Translation";
        spring.FinalValue = new Vector3(0f, targetY, 0f);
        spring.DampingRatio = dampingRatio;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);

        visual.StartAnimation("Translation", spring);
    }

    /// <summary>
    /// UIElement の Scale を Spring 物理アニメーションで目標倍率へ滑らかに変倍します。
    /// </summary>
    public static void AnimateSpringScale(UIElement element, float targetScale, float dampingRatio = 0.7f, float periodMs = 250f)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        // 中心を軸にスケールさせるため CenterPoint を設定
        if (element is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0f);
        }

        var spring = compositor.CreateSpringVector3Animation();
        spring.Target = "Scale";
        spring.FinalValue = new Vector3(targetScale, targetScale, 1f);
        spring.DampingRatio = dampingRatio;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);

        visual.StartAnimation("Scale", spring);
    }

    /// <summary>
    /// UIElement の不透明度（Opacity）を KeyFrame で滑らかに遷移させます。
    /// </summary>
    public static void AnimateOpacity(UIElement element, float targetOpacity, double durationMs = 200)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.Target = "Opacity";
        anim.InsertKeyFrame(1.0f, targetOpacity);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);

        visual.StartAnimation("Opacity", anim);
    }

    /// <summary>
    /// カード出現（ポップイン）アニメーション。
    /// 縮小状態から Spring 物理でポンッと弾んで登場します。
    /// </summary>
    public static void AnimateCardPopIn(UIElement element, int delayMs = 0)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        if (element is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0f);
        }

        visual.Scale = new Vector3(0.6f, 0.6f, 1f);
        visual.Opacity = 0f;

        var scaleSpring = compositor.CreateSpringVector3Animation();
        scaleSpring.Target = "Scale";
        scaleSpring.FinalValue = new Vector3(1f, 1f, 1f);
        scaleSpring.DampingRatio = 0.6f;
        scaleSpring.Period = TimeSpan.FromMilliseconds(280);
        if (delayMs > 0) scaleSpring.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.Target = "Opacity";
        opacityAnim.InsertKeyFrame(1.0f, 1.0f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(180);
        if (delayMs > 0) opacityAnim.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        visual.StartAnimation("Scale", scaleSpring);
        visual.StartAnimation("Opacity", opacityAnim);
    }

    /// <summary>
    /// 場流れ演出（カードが右方向へスッと流れて消滅）。
    /// </summary>
    public static void AnimateFlushOut(UIElement element, Action? onCompleted = null)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var scopedBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var slideAnim = compositor.CreateVector3KeyFrameAnimation();
        slideAnim.Target = "Translation";
        slideAnim.InsertKeyFrame(1.0f, new Vector3(160f, 10f, 0f));
        slideAnim.Duration = TimeSpan.FromMilliseconds(320);

        var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.Target = "Opacity";
        fadeAnim.InsertKeyFrame(1.0f, 0f);
        fadeAnim.Duration = TimeSpan.FromMilliseconds(300);

        scopedBatch.Completed += (s, e) =>
        {
            // 位置をリセット
            ElementCompositionPreview.SetIsTranslationEnabled(element, false);
            visual.Opacity = 1f;
            onCompleted?.Invoke();
        };

        visual.StartAnimation("Translation", slideAnim);
        visual.StartAnimation("Opacity", fadeAnim);

        scopedBatch.End();
    }

    /// <summary>
    /// 大迫力のカットイン演出（中央でSpring拡大してバウンド着地後、一定時間後にフェードアウト）。
    /// </summary>
    /// <remarks>
    /// 登場・静止・退場を1本のキーフレームアニメーション（合計 <see cref="CutInDurationMs"/>）で表し、
    /// 最終フレームで必ず透明になる。完了通知（ScopedBatch）には依存しないため、演出が重なったり
    /// 途中で止められたりしても画面に残らない。非表示への切り替えは呼び出し側が <see cref="ResetCutIn"/> で行う。
    /// </remarks>
    public static void AnimateCutIn(UIElement bannerElement)
    {
        var visual = ElementCompositionPreview.GetElementVisual(bannerElement);
        var compositor = visual.Compositor;

        StopCutInAnimations(bannerElement, visual);
        bannerElement.Visibility = Visibility.Visible;
        bannerElement.UpdateLayout(); // 中心点の計算に実サイズを使う

        if (bannerElement is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0f);
        }
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.Scale = new Vector3(0.3f, 0.3f, 1f);
        visual.Opacity = 0f;

        var duration = TimeSpan.FromMilliseconds(CutInDurationMs);

        // 0〜0.25: 弾むように拡大 → 0.25〜0.79: 静止 → 0.79〜1.0: 上へ流れて消える
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.Duration = duration;
        scale.InsertKeyFrame(0.00f, new Vector3(0.3f, 0.3f, 1f));
        scale.InsertKeyFrame(0.17f, new Vector3(1.12f, 1.12f, 1f));
        scale.InsertKeyFrame(0.25f, new Vector3(1.0f, 1.0f, 1f));
        scale.InsertKeyFrame(1.00f, new Vector3(1.0f, 1.0f, 1f));

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.Duration = duration;
        opacity.InsertKeyFrame(0.00f, 0f);
        opacity.InsertKeyFrame(0.12f, 1f);
        opacity.InsertKeyFrame(0.79f, 1f);
        opacity.InsertKeyFrame(1.00f, 0f);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.Duration = duration;
        slide.InsertKeyFrame(0.00f, Vector3.Zero);
        slide.InsertKeyFrame(0.79f, Vector3.Zero);
        slide.InsertKeyFrame(1.00f, new Vector3(0f, -40f, 0f));

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", slide);
    }

    /// <summary>
    /// Prism Flash：画面の四辺（上→右→下→左の順）に光の線が伸びて消える。場が流れたときの演出。
    /// 画面の端だけを使うので、手札や場の見やすさを邪魔しない。
    /// </summary>
    public static void AnimatePrismFlash(FrameworkElement top, FrameworkElement right, FrameworkElement bottom, FrameworkElement left)
    {
        // 伸び始める位置（時計回りに走るよう、各辺の始点を中心にして拡大する）
        AnimateEdge(top, horizontal: true, fromEnd: false, delayMs: 0);
        AnimateEdge(right, horizontal: false, fromEnd: false, delayMs: 90);
        AnimateEdge(bottom, horizontal: true, fromEnd: true, delayMs: 180);
        AnimateEdge(left, horizontal: false, fromEnd: true, delayMs: 270);
    }

    private static void AnimateEdge(FrameworkElement edge, bool horizontal, bool fromEnd, int delayMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(edge);
        var compositor = visual.Compositor;
        edge.Opacity = 1; // XAML 側は初期状態で 0（未再生時に見えないように）。以降は Visual の不透明度で制御する

        float w = (float)edge.ActualWidth, h = (float)edge.ActualHeight;
        visual.CenterPoint = horizontal
            ? new Vector3(fromEnd ? w : 0f, h / 2f, 0f)
            : new Vector3(w / 2f, fromEnd ? h : 0f, 0f);
        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");

        var duration = TimeSpan.FromMilliseconds(700);
        var delay = TimeSpan.FromMilliseconds(delayMs);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        scale.DelayTime = delay;
        scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var start = horizontal ? new Vector3(0f, 1f, 1f) : new Vector3(1f, 0f, 1f);
        scale.InsertKeyFrame(0f, start);
        scale.InsertKeyFrame(0.45f, Vector3.One);
        scale.InsertKeyFrame(1f, Vector3.One);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = duration;
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(0.15f, 1f);
        opacity.InsertKeyFrame(0.55f, 1f);
        opacity.InsertKeyFrame(1f, 0f);

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    /// <summary>
    /// Aurora Sweep：テーマカラー・シアン・バイオレットの柔らかな光の帯が、画面を斜めに横切る。
    /// 12ボンバー・革命など、場の状況が大きく変わったときの演出。
    /// </summary>
    public static void AnimateAuroraSweep(FrameworkElement band, double hostWidth)
    {
        var visual = ElementCompositionPreview.GetElementVisual(band);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(band, true);
        band.Opacity = 1;
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");

        float bandWidth = (float)band.ActualWidth;
        float from = -bandWidth - 120f;
        float to = (float)Math.Max(hostWidth, 600) + 120f;
        var duration = TimeSpan.FromMilliseconds(1100);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = duration;
        slide.InsertKeyFrame(0f, new Vector3(from, 0f, 0f));
        slide.InsertKeyFrame(1f, new Vector3(to, 0f, 0f), compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f)));

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = duration;
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(0.2f, 1f);
        opacity.InsertKeyFrame(0.75f, 1f);
        opacity.InsertKeyFrame(1f, 0f);

        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", opacity);
    }

    /// <summary>カットイン演出1回分の長さ（ミリ秒）。</summary>
    public const double CutInDurationMs = 1200;

    /// <summary>再生中のカットインを即座に止めて非表示にし、次回のために状態を初期値へ戻す。</summary>
    public static void ResetCutIn(UIElement bannerElement)
    {
        var visual = ElementCompositionPreview.GetElementVisual(bannerElement);
        StopCutInAnimations(bannerElement, visual);
        bannerElement.Visibility = Visibility.Collapsed;
        visual.Opacity = 1f;
        visual.Scale = Vector3.One;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private static void StopCutInAnimations(UIElement element, Visual visual)
    {
        // Translation は有効化するまでプロパティとして存在せず、止めようとすると E_INVALIDARG で落ちる
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
    }
}

