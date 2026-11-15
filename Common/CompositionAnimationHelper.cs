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
    /// ひとことの吹き出し：少し下から浮かび上がって現れ、しばらく留まってから消える（1本のキーフレームで完結し、
    /// 途中で次の吹き出しが来ても最後は必ず透明になる）。
    /// </summary>
    public static void AnimateReactionBubble(FrameworkElement bubble, double lifetimeMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(bubble);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(bubble, true);
        bubble.Opacity = 1; // 表示の不透明度は Composition 側で動かす
        visual.CenterPoint = new Vector3((float)bubble.ActualWidth, (float)bubble.ActualHeight, 0f);
        var duration = TimeSpan.FromMilliseconds(Math.Max(400, lifetimeMs));
        float inEnd = (float)Math.Min(0.25, 180 / duration.TotalMilliseconds);
        float outStart = (float)Math.Max(inEnd, 1 - 260 / duration.TotalMilliseconds);
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(inEnd, 1f, ease);
        opacity.InsertKeyFrame(outStart, 1f);
        opacity.InsertKeyFrame(1f, 0f);
        opacity.Duration = duration;

        var lift = compositor.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(0f, new Vector3(0f, 8f, 0f));
        lift.InsertKeyFrame(inEnd, Vector3.Zero, ease);
        lift.InsertKeyFrame(outStart, Vector3.Zero);
        lift.InsertKeyFrame(1f, new Vector3(0f, -4f, 0f));
        lift.Duration = duration;

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.85f, 0.85f, 1f));
        scale.InsertKeyFrame(inEnd, Vector3.One, ease);
        scale.InsertKeyFrame(1f, Vector3.One);
        scale.Duration = duration;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", lift);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>
    /// カードが出したプレイヤーの位置から場へ滑ってきて着地する（Translation と Scale のばね）。
    /// </summary>
    /// <param name="element">場のカード（の入れ物）。</param>
    /// <param name="from">出どころから見た、カードの今の位置までのずれ（出どころ − カード中心）。</param>
    /// <param name="startScale">出発時の大きさ（相手の席からは小さく、手札からはほぼ同じ大きさ）。</param>
    /// <param name="delayMs">複数枚を少しずつずらして飛ばすための遅れ。</param>
    /// <param name="onLanded">着地（ばねが落ち着いた）後に呼ぶ処理（写しを消して本物を見せる等）。</param>
    public static void AnimateCardFlight(FrameworkElement element, Vector3 from, float startScale, int delayMs, Action? onLanded = null)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (onLanded is not null)
        {
            batch.Completed += (_, _) => onLanded();
        }
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2f, (float)element.ActualHeight / 2f, 0f);

        // 出発位置に置いてから、ばねで場の位置（ずれ 0）へ戻す。遅れている間は見えないようにしておく
        visual.Properties.InsertVector3("Translation", from);
        visual.Scale = new Vector3(startScale, startScale, 1f);
        visual.Opacity = 0f;

        var move = compositor.CreateSpringVector3Animation();
        move.InitialValue = from;
        move.FinalValue = Vector3.Zero;
        move.DampingRatio = 0.78f;
        move.Period = TimeSpan.FromMilliseconds(80);
        move.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        if (delayMs > 0) move.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        var bounce = compositor.CreateSpringVector3Animation();
        bounce.InitialValue = new Vector3(startScale, startScale, 1f);
        bounce.FinalValue = Vector3.One;
        bounce.DampingRatio = 0.5f;
        bounce.Period = TimeSpan.FromMilliseconds(70);
        bounce.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        if (delayMs > 0) bounce.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(90);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        if (delayMs > 0) fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);

        visual.StartAnimation("Translation", move);
        visual.StartAnimation("Scale", bounce);
        visual.StartAnimation("Opacity", fade);
        batch.End();
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

    /// <summary>
    /// 12ボンバーで消える数字のカード1枚分：ポンッと弾んで現れ、少し止まってから、上へシュッと抜けて消える。
    /// </summary>
    /// <param name="chipHost">カードと枚数表示をまとめた要素。</param>
    /// <param name="strike">カードに入る斜めの打ち消し線（誰も持っていなかった数字には入れない）。</param>
    /// <param name="discarded">その数字のカードが実際に捨てられたか。</param>
    /// <param name="durationMs">1枚分の長さ（ミリ秒）。</param>
    public static void AnimateBomberChip(FrameworkElement chipHost, FrameworkElement strike, bool discarded, double durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(chipHost);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(chipHost, true);
        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        chipHost.UpdateLayout();
        visual.CenterPoint = new Vector3((float)chipHost.ActualWidth / 2f, (float)chipHost.ActualHeight / 2f, 0f);
        visual.Opacity = 0f;

        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var easeIn = compositor.CreateCubicBezierEasingFunction(new Vector2(0.6f, 0f), new Vector2(0.9f, 0.4f));

        // 0〜0.26: 弾んで登場 → 〜0.66: 静止 → 〜1.0: 上へ抜けて消える
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        scale.InsertKeyFrame(0.00f, new Vector3(0.45f, 0.45f, 1f));
        scale.InsertKeyFrame(0.17f, new Vector3(1.14f, 1.14f, 1f), easeOut);
        scale.InsertKeyFrame(0.26f, Vector3.One);
        scale.InsertKeyFrame(0.66f, Vector3.One);
        scale.InsertKeyFrame(1.00f, new Vector3(0.78f, 1.22f, 1f), easeIn);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = duration;
        opacity.InsertKeyFrame(0.00f, 0f);
        opacity.InsertKeyFrame(0.12f, 1f);
        opacity.InsertKeyFrame(0.66f, discarded ? 1f : 0.7f);
        opacity.InsertKeyFrame(1.00f, 0f, easeIn);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = duration;
        slide.InsertKeyFrame(0.00f, Vector3.Zero);
        slide.InsertKeyFrame(0.66f, Vector3.Zero);
        slide.InsertKeyFrame(1.00f, new Vector3(0f, -90f, 0f), easeIn);

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", slide);

        // 打ち消し線：カードが止まったところで左から右へ走る
        var strikeVisual = ElementCompositionPreview.GetElementVisual(strike);
        strikeVisual.StopAnimation("Scale");
        strikeVisual.StopAnimation("Opacity");
        strikeVisual.CenterPoint = new Vector3(0f, (float)strike.ActualHeight / 2f, 0f);
        strikeVisual.Opacity = 0f;
        if (discarded)
        {
            var strikeScale = compositor.CreateVector3KeyFrameAnimation();
            strikeScale.Duration = duration;
            strikeScale.InsertKeyFrame(0.00f, new Vector3(0f, 1f, 1f));
            strikeScale.InsertKeyFrame(0.34f, new Vector3(0f, 1f, 1f));
            strikeScale.InsertKeyFrame(0.52f, Vector3.One, easeOut);
            strikeScale.InsertKeyFrame(1.00f, Vector3.One);

            var strikeOpacity = compositor.CreateScalarKeyFrameAnimation();
            strikeOpacity.Duration = duration;
            strikeOpacity.InsertKeyFrame(0.00f, 0f);
            strikeOpacity.InsertKeyFrame(0.33f, 0f);
            strikeOpacity.InsertKeyFrame(0.36f, 1f);
            strikeOpacity.InsertKeyFrame(1.00f, 1f);

            strikeVisual.StartAnimation("Scale", strikeScale);
            strikeVisual.StartAnimation("Opacity", strikeOpacity);
        }
    }

    /// <summary>
    /// あがりの演出：柔らかい光がふわりと広がり、上下の細い線が中央から伸び、順位のリボンが静かに浮かび上がって消える。
    /// 弾み（Spring）や拡大の衝撃は使わず、ゆっくりした緩急だけで見せる。
    /// </summary>
    /// <param name="glow">背後の光（放射状のグラデーション）。</param>
    /// <param name="lineTop">リボンの上の線。</param>
    /// <param name="lineBottom">リボンの下の線。</param>
    /// <param name="ribbon">順位と名前のリボン。</param>
    /// <param name="durationMs">全体の長さ（ミリ秒）。</param>
    public static void AnimateFinishCelebration(
        FrameworkElement glow, FrameworkElement lineTop, FrameworkElement lineBottom, FrameworkElement ribbon, double durationMs)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(ribbon).Compositor;
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var easeInOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));

        // --- 光：ゆっくり明るくなり、わずかに広がりながら消える ---
        var glowVisual = ElementCompositionPreview.GetElementVisual(glow);
        glowVisual.StopAnimation("Scale");
        glowVisual.StopAnimation("Opacity");
        glowVisual.CenterPoint = new Vector3((float)glow.ActualWidth / 2f, (float)glow.ActualHeight / 2f, 0f);
        glowVisual.Opacity = 0f;

        var glowScale = compositor.CreateVector3KeyFrameAnimation();
        glowScale.Duration = duration;
        glowScale.InsertKeyFrame(0f, new Vector3(0.82f, 0.82f, 1f));
        glowScale.InsertKeyFrame(1f, new Vector3(1.08f, 1.08f, 1f), easeOut);

        var glowOpacity = compositor.CreateScalarKeyFrameAnimation();
        glowOpacity.Duration = duration;
        glowOpacity.InsertKeyFrame(0.00f, 0f);
        glowOpacity.InsertKeyFrame(0.30f, 1f, easeInOut);
        glowOpacity.InsertKeyFrame(0.68f, 0.85f);
        glowOpacity.InsertKeyFrame(1.00f, 0f, easeInOut);

        glowVisual.StartAnimation("Scale", glowScale);
        glowVisual.StartAnimation("Opacity", glowOpacity);

        // --- 線：中央から左右へ伸びる ---
        foreach (var line in new[] { lineTop, lineBottom })
        {
            var lineVisual = ElementCompositionPreview.GetElementVisual(line);
            lineVisual.StopAnimation("Scale");
            lineVisual.StopAnimation("Opacity");
            lineVisual.CenterPoint = new Vector3((float)line.ActualWidth / 2f, (float)line.ActualHeight / 2f, 0f);
            lineVisual.Opacity = 0f;

            var lineScale = compositor.CreateVector3KeyFrameAnimation();
            lineScale.Duration = duration;
            lineScale.InsertKeyFrame(0.00f, new Vector3(0f, 1f, 1f));
            lineScale.InsertKeyFrame(0.40f, Vector3.One, easeOut);
            lineScale.InsertKeyFrame(1.00f, Vector3.One);

            var lineOpacity = compositor.CreateScalarKeyFrameAnimation();
            lineOpacity.Duration = duration;
            lineOpacity.InsertKeyFrame(0.00f, 0f);
            lineOpacity.InsertKeyFrame(0.14f, 1f);
            lineOpacity.InsertKeyFrame(0.76f, 1f);
            lineOpacity.InsertKeyFrame(1.00f, 0f, easeInOut);

            lineVisual.StartAnimation("Scale", lineScale);
            lineVisual.StartAnimation("Opacity", lineOpacity);
        }

        // --- リボン：下からそっと浮かび上がり、上へ抜けて消える ---
        var ribbonVisual = ElementCompositionPreview.GetElementVisual(ribbon);
        ElementCompositionPreview.SetIsTranslationEnabled(ribbon, true);
        ribbonVisual.StopAnimation("Opacity");
        ribbonVisual.StopAnimation("Translation");
        ribbonVisual.Opacity = 0f;

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.Duration = duration;
        rise.InsertKeyFrame(0.00f, new Vector3(0f, 18f, 0f));
        rise.InsertKeyFrame(0.08f, new Vector3(0f, 18f, 0f));
        rise.InsertKeyFrame(0.44f, Vector3.Zero, easeOut);
        rise.InsertKeyFrame(0.76f, Vector3.Zero);
        rise.InsertKeyFrame(1.00f, new Vector3(0f, -14f, 0f), easeInOut);

        var ribbonOpacity = compositor.CreateScalarKeyFrameAnimation();
        ribbonOpacity.Duration = duration;
        ribbonOpacity.InsertKeyFrame(0.00f, 0f);
        ribbonOpacity.InsertKeyFrame(0.08f, 0f);
        ribbonOpacity.InsertKeyFrame(0.36f, 1f, easeInOut);
        ribbonOpacity.InsertKeyFrame(0.76f, 1f);
        ribbonOpacity.InsertKeyFrame(1.00f, 0f, easeInOut);

        ribbonVisual.StartAnimation("Translation", rise);
        ribbonVisual.StartAnimation("Opacity", ribbonOpacity);
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

