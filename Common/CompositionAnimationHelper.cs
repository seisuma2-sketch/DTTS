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
    public static void AnimateCutIn(UIElement bannerElement, Action? onCompleted = null)
    {
        var visual = ElementCompositionPreview.GetElementVisual(bannerElement);
        var compositor = visual.Compositor;

        if (bannerElement is FrameworkElement fe)
        {
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0f);
        }

        bannerElement.Visibility = Visibility.Visible;
        visual.Scale = new Vector3(0.3f, 0.3f, 1f);
        visual.Opacity = 0f;

        // フェーズ1: Spring バウンドイン
        var scaleSpring = compositor.CreateSpringVector3Animation();
        scaleSpring.Target = "Scale";
        scaleSpring.FinalValue = new Vector3(1.05f, 1.05f, 1f);
        scaleSpring.DampingRatio = 0.55f;
        scaleSpring.Period = TimeSpan.FromMilliseconds(320);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.Target = "Opacity";
        opacityAnim.InsertKeyFrame(1.0f, 1.0f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(150);

        var inBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        inBatch.Completed += (s, e) =>
        {
            // 700ms 表示後、上方向にスライド＆フェードアウト
            var outBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            outBatch.Completed += (s2, e2) =>
            {
                bannerElement.Visibility = Visibility.Collapsed;
                visual.Opacity = 1f;
                visual.Scale = new Vector3(1f, 1f, 1f);
                ElementCompositionPreview.SetIsTranslationEnabled(bannerElement, false);
                onCompleted?.Invoke();
            };

            ElementCompositionPreview.SetIsTranslationEnabled(bannerElement, true);

            var slideOut = compositor.CreateVector3KeyFrameAnimation();
            slideOut.Target = "Translation";
            slideOut.InsertKeyFrame(1.0f, new Vector3(0f, -40f, 0f));
            slideOut.Duration = TimeSpan.FromMilliseconds(250);
            slideOut.DelayTime = TimeSpan.FromMilliseconds(700);

            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.Target = "Opacity";
            fadeOut.InsertKeyFrame(1.0f, 0f);
            fadeOut.Duration = TimeSpan.FromMilliseconds(250);
            fadeOut.DelayTime = TimeSpan.FromMilliseconds(700);

            visual.StartAnimation("Translation", slideOut);
            visual.StartAnimation("Opacity", fadeOut);
            outBatch.End();
        };

        visual.StartAnimation("Scale", scaleSpring);
        visual.StartAnimation("Opacity", opacityAnim);
        inBatch.End();
    }
}

