using Godot;

public static class TweenUtils
{
    private static readonly StringName HomePosMetaKey = "TweenUtils_HomePosition";

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
        if (!ConfigManager.GUISettings.WindowAnimations)
        {
            ctx.Visible = isOpening;
            if(isOpening)
                ctx.MoveToFront();
            return;
        }
        Vector2 originalPosition;
        
        if (ctx.HasMeta(HomePosMetaKey))
        {
            originalPosition = ctx.GetMeta(HomePosMetaKey).AsVector2();
        }
        else
        {
            originalPosition = ctx.Position;
            ctx.SetMeta(HomePosMetaKey, originalPosition);
        }

        Vector2 viewportCenter = ctx.GetViewportRect().Size * 0.5f;
        Vector2 centeredPosition = viewportCenter - (ctx.Size * 0.5f);
        ctx.PivotOffset = ctx.Size * 0.5f;

        Vector2 startPos = isOpening ? centeredPosition : originalPosition;
        Vector2 endPos   = isOpening ? originalPosition  : centeredPosition;

        Vector2 startScale = isOpening ? Vector2.Zero : Vector2.One;
        Vector2 endScale   = isOpening ? Vector2.One  : Vector2.Zero;

        float startAlpha = isOpening ? 0.0f : 1.0f;
        float endAlpha   = isOpening ? 1.0f : 0.0f;

        Tween.EaseType easeType = isOpening ? Tween.EaseType.Out : Tween.EaseType.In;

        if (isOpening)
        {
            ctx.Visible = true;
            ctx.MoveToFront();
        }
        
        ctx.Position = startPos;
        ctx.Scale = startScale;
        ctx.Modulate = new Color(ctx.Modulate.R, ctx.Modulate.G, ctx.Modulate.B, startAlpha);

        Tween tween = ctx.CreateTween().SetParallel(true);
        tween.SetTrans(Tween.TransitionType.Quart);
        tween.SetEase(easeType);

        tween.TweenProperty(ctx, "position", endPos, 0.6f).SetDelay(delay);
        tween.TweenProperty(ctx, "scale", endScale, 0.6f).SetDelay(delay);
        tween.TweenProperty(ctx, "modulate:a", endAlpha, isOpening ? 0.4f : 0.6f).SetDelay(delay);

        if (!isOpening)
        {
            tween.Chain().TweenCallback(Callable.From(() => ctx.Visible = false));
        }
    }
}