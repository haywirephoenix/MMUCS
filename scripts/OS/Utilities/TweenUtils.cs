using Godot;

public static class TweenUtils
{
    private static readonly StringName HomePosMetaKey = "TweenUtils_HomePosition";
    
    private const float DefaultDuration = 0.2f;
    
    private const string s_active_pos_tween = "active_pos_tween";
    private const string s_active_size_tween = "active_size_tween";
    private const string s_position = "position";
    private const string s_size = "size";
    private const string s_scale = "scale";
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
        ctx.CreateTween().Kill();

        ctx.PivotOffset = ctx.Size * 0.5f;

        Vector2 startScale = isOpening ? Vector2.Zero : Vector2.One;
        Vector2 endScale   = isOpening ? Vector2.One  : Vector2.Zero;

        float startAlpha = isOpening ? 0.0f : 1.0f;
        float endAlpha   = isOpening ? 1.0f : 0.0f;

        ctx.Scale = startScale;
        ctx.Modulate = new Color(ctx.Modulate.R, ctx.Modulate.G, ctx.Modulate.B, startAlpha);
        ctx.Visible = true;

        if (isOpening) ctx.MoveToFront();

        Tween tween = ctx.CreateTween().SetParallel(true);
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
            }));
        }
    }
    
    public static PropertyTweener TweenPosition(this Control ctx, Vector2 targetPosition, double duration = DefaultDuration)
    {
        if (ctx.HasMeta(s_active_pos_tween) && ctx.GetMeta(s_active_pos_tween).Obj is Tween oldTween)
            oldTween.Kill();

        Tween tween = ctx.CreateTween();

        ctx.SetMeta(s_active_pos_tween, tween);

        return tween.TweenProperty(ctx, s_position, targetPosition, duration);
    }

    public static PropertyTweener TweenSize(this Control ctx, Vector2 targetSize, double duration = DefaultDuration)
    {
        if (ctx.HasMeta(s_active_size_tween) && ctx.GetMeta(s_active_size_tween).Obj is Tween oldTween)
            oldTween.Kill();

        Tween tween = ctx.CreateTween();
        ctx.SetMeta(s_active_size_tween, tween);

        return tween.TweenProperty(ctx, s_size, targetSize, duration);
    }

    public static Tween TweenPositionSize(this Control ctx, Vector2 targetPosition, Vector2 targetSize, double duration = DefaultDuration)
    {
        if (ctx.HasMeta(s_active_pos_tween) && ctx.GetMeta(s_active_pos_tween).Obj is Tween oldPos) oldPos.Kill();
        if (ctx.HasMeta(s_active_size_tween) && ctx.GetMeta(s_active_size_tween).Obj is Tween oldSize) oldSize.Kill();

        Tween tween = ctx.CreateTween().SetParallel(true);
        
        ctx.SetMeta(s_active_pos_tween, tween);
        ctx.SetMeta(s_active_size_tween, tween);

        tween.TweenProperty(ctx, s_position, targetPosition, duration);
        tween.TweenProperty(ctx, s_size, targetSize, duration);

        return tween;
    }
}