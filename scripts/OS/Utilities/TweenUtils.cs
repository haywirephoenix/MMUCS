using Godot;

public static class TweenUtils
{
    private static readonly StringName HomePosMetaKey = "TweenUtils_HomePosition";
    
    private const float DefaultDuration = 0.2f;
    
    private static readonly StringName s_tweenMetaKey    = "active_animate_tween";
    private static readonly StringName s_activePosTween  = "active_pos_tween";
    private static readonly StringName s_activeSizeTween = "active_size_tween";
    private static readonly StringName s_activeLayoutTween = "active_layout_tween";

    private const string s_position  = "position";
    private const string s_size      = "size";
    private const string s_scale     = "scale";
    private const string s_modulatea = "modulate:a";
    

    public static void PlayOpenAnimation(this Control ctx, float delay = 0.0f)
    {
        ctx.AnimateVisible(isOpening: true, delay);
    }

    public static void PlayCloseAnimation(this Control ctx, float delay = 0.0f)
    {
        ctx.AnimateVisible(isOpening: false, delay);
    }

    public static void AnimateVisible(this Control ctx, bool isOpening, float delay = 0)
    {
        if (ctx.HasMeta(s_tweenMetaKey))
        {
            var existingTween = ctx.GetMeta(s_tweenMetaKey).As<Tween>();
            if (existingTween != null && existingTween.IsValid())
            {
                existingTween.Kill();
            }
        }

        ctx.PivotOffset = ctx.Size * 0.5f;
        
        if (isOpening && (!ctx.Visible || ctx.Scale == Vector2.Zero || ctx.Modulate.A == 0.0f))
        {
            ctx.Scale = Vector2.Zero;
            ctx.Modulate = new Color(ctx.Modulate.R, ctx.Modulate.G, ctx.Modulate.B, 0.0f);
        }

        Vector2 endScale = isOpening ? Vector2.One  : Vector2.Zero;
        float endAlpha   = isOpening ? 1.0f : 0.0f;

        if (isOpening)
        {
            ctx.Visible = true;
            ctx.MoveToFront();
        }

        Tween tween = ctx.CreateTween().SetParallel(true);
        ctx.SetMeta(s_tweenMetaKey, tween);
    
        tween.SetTrans(Tween.TransitionType.Quart);
        tween.SetEase(Tween.EaseType.Out);

        tween.TweenProperty(ctx, s_scale, endScale, 0.6f).SetDelay(delay);
        tween.TweenProperty(ctx, s_modulatea, endAlpha, isOpening ? 0.4f : 0.6f).SetDelay(delay);

        if (!isOpening)
        {
            tween.Chain().TweenCallback(Callable.From(() =>
            {
                ctx.Visible = false;
                ctx.Scale = Vector2.One;
                ctx.Modulate = new Color(ctx.Modulate.R, ctx.Modulate.G, ctx.Modulate.B, 1.0f);
                ctx.RemoveMeta(s_tweenMetaKey);
            }));
        }
        else
        {
            tween.Chain().TweenCallback(Callable.From(() =>
            {
                ctx.RemoveMeta(s_tweenMetaKey);
            }));
        }
    }
    
    public static PropertyTweener TweenPosition(this Control ctx, Vector2 targetPosition, double duration = DefaultDuration)
    {
        // 1. Kill any existing standalone position tween
        if (ctx.HasMeta(s_activePosTween))
        {
            var oldTween = ctx.GetMeta(s_activePosTween).As<Tween>();
            if (oldTween != null && oldTween.IsValid()) oldTween.Kill();
        }

        // 2. CRITICAL: Kill any active combined layout tween so they don't fight
        if (ctx.HasMeta(s_activeLayoutTween))
        {
            var oldLayout = ctx.GetMeta(s_activeLayoutTween).As<Tween>();
            if (oldLayout != null && oldLayout.IsValid()) oldLayout.Kill();
        }

        Tween tween = ctx.CreateTween();
        ctx.SetMeta(s_activePosTween, tween);

        // Clean up metadata when done
        tween.Chain().TweenCallback(Callable.From(() => ctx.RemoveMeta(s_activePosTween)));

        return tween.TweenProperty(ctx, s_position, targetPosition, duration);
    }

    public static PropertyTweener TweenSize(this Control ctx, Vector2 targetSize, double duration = DefaultDuration)
    {
        if (ctx.HasMeta(s_activeSizeTween))
        {
            var oldTween = ctx.GetMeta(s_activeSizeTween).As<Tween>();
            if (oldTween != null && oldTween.IsValid()) oldTween.Kill();
        }

        if (ctx.HasMeta(s_activeLayoutTween))
        {
            var oldLayout = ctx.GetMeta(s_activeLayoutTween).As<Tween>();
            if (oldLayout != null && oldLayout.IsValid()) oldLayout.Kill();
        }

        Tween tween = ctx.CreateTween();
        ctx.SetMeta(s_activeSizeTween, tween);

        tween.Chain().TweenCallback(Callable.From(() => ctx.RemoveMeta(s_activeSizeTween)));

        return tween.TweenProperty(ctx, s_size, targetSize, duration);
    }

    public static Tween TweenPositionSize(this Control ctx, Vector2 targetPosition, Vector2 targetSize, double duration = DefaultDuration)
    {
        if (ctx.HasMeta(s_activeLayoutTween))
        {
            var oldTween = ctx.GetMeta(s_activeLayoutTween).As<Tween>();
            if (oldTween != null && oldTween.IsValid())
            {
                oldTween.Kill();
            }
        }
        
        if (ctx.HasMeta(s_activePosTween))
        {
            var oldPos = ctx.GetMeta(s_activePosTween).As<Tween>();
            if (oldPos != null && oldPos.IsValid()) oldPos.Kill();
        }
        if (ctx.HasMeta(s_activeSizeTween))
        {
            var oldSize = ctx.GetMeta(s_activeSizeTween).As<Tween>();
            if (oldSize != null && oldSize.IsValid()) oldSize.Kill();
        }

        Tween tween = ctx.CreateTween().SetParallel(true);
        ctx.SetMeta(s_activeLayoutTween, tween);

        tween.TweenProperty(ctx, s_position, targetPosition, duration);
        tween.TweenProperty(ctx, s_size, targetSize, duration);

        tween.Chain().TweenCallback(Callable.From(() =>
        {
            ctx.RemoveMeta(s_activeLayoutTween);
        }));

        return tween;
    }
}