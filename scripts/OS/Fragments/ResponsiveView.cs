using Godot;
using System;
using System.Collections.Generic;

[Tool]
public partial class ResponsiveView : Control
{
    [Export] public TabBar TabBar;
    [Export] public HBoxContainer SplitContainer;

    private float _calculatedThreshold = 0f;

    public override void _Ready()
    {
        if (!Engine.IsEditorHint())
        {
            this.Resized += OnResized;
        }

        if (SplitContainer != null)
        {
            SplitContainer.ChildOrderChanged += OnTreeStructureChanged;
        }
        
        if (TabBar != null && !Engine.IsEditorHint())
        {
            TabBar.TabChanged += OnTabChanged;
        }

        CallDeferred(MethodName.SetupDynamicUI);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && Engine.IsEditorHint())
        {
            SetupDynamicUI();
        }
    }

    private void OnTreeStructureChanged()
    {
        SetupDynamicUI();
    }

    private void SetupDynamicUI()
    {
        SplitContainer ??= GetNodeOrNull<HBoxContainer>("SplitContainer");
        TabBar ??= GetNodeOrNull<TabBar>("TabBar");
        
        if (SplitContainer == null || TabBar == null) return;

        var columns = GetColumns();
        if (columns.Count == 0) return;

        if (TabBar.TabCount != columns.Count)
        {
            TabBar.ClearTabs();
            foreach (var col in columns)
            {
                TabBar.AddTab(col.Name);
            }
        }

        foreach (var col in columns) col.Visible = true;
        
        _calculatedThreshold = SplitContainer.Size.X * columns.Count;

        UpdateLayout(columns);
    }

    private void OnResized()
    {
        if (!Engine.IsEditorHint()) UpdateLayout(GetColumns());
    }

    private void OnTabChanged(long tabIndex)
    {
        if (TabBar != null && TabBar.Visible)
        {
            UpdateTabVisibility(GetColumns(), (int)tabIndex);
        }
    }

    private void UpdateLayout(List<Control> columns)
    {
        bool isSmallScreen = Size.X < _calculatedThreshold;

        if (isSmallScreen)
        {
            TabBar.Visible = true;
            UpdateTabVisibility(columns, TabBar.CurrentTab);
        }
        else
        {
            TabBar.Visible = false;
            foreach (var col in columns) col.Visible = true;
        }
    }

    private void UpdateTabVisibility(List<Control> columns, int activeTab)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            columns[i].Visible = (i == activeTab);
        }
    }

    private List<Control> GetColumns()
    {
        var columns = new List<Control>();
        if (SplitContainer == null) return columns;

        foreach (var child in SplitContainer.GetChildren())
        {
            if (child is Control controlNode)
            {
                columns.Add(controlNode);
            }
        }
        return columns;
    }
}