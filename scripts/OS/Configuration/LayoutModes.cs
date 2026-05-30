public static class LayoutModes
{
    public static string CurrentLayoutMode { get; private set; } = SettingKeys.Layout.Unified;
    public static ELayoutMode CurrentELayoutMode { get; private set; } = ELayoutMode.Unified;

    public enum ELayoutMode
    {
        Unified = 0,
        Classic = 1
    }

    public static void SetLayoutMode(ELayoutMode eLayoutMode)
    {
        CurrentLayoutMode = LayoutModeToString(eLayoutMode);
        CurrentELayoutMode =  eLayoutMode;
    }

    public static ELayoutMode GetELayoutMode() => CurrentELayoutMode;
    public static string GetLayoutMode() => CurrentLayoutMode;
    
    public static bool IsClassicMode() => GetELayoutMode() == LayoutModes.ELayoutMode.Classic;

    public static string LayoutModeToString(ELayoutMode eLayoutMode)
    {
        return eLayoutMode switch
        {
            ELayoutMode.Unified => SettingKeys.Layout.Unified,
            ELayoutMode.Classic => SettingKeys.Layout.Classic,
            _ => SettingKeys.Layout.Unified
        };
    }

    public static ELayoutMode StringToLayoutMode(string layoutModeString)
    {
        return layoutModeString switch
        {
            SettingKeys.Layout.Unified => ELayoutMode.Unified,
            SettingKeys.Layout.Classic => ELayoutMode.Classic,
            _ => ELayoutMode.Unified
        };
    }

}