using Godot;
using System;

public partial class MSlider : HSlider
{
    public event Action<int> IndexChanged;
    public int CurrentIndex { get; private set; }
    public int TotalItems { get; private set; }

    public override void _Ready()
    {
        ValueChanged += OnSliderValueChanged;
        Reset();
    }

    public override void _ExitTree()
    {
        ValueChanged -= OnSliderValueChanged;
    }

    public void Setup(int totalItems, int initialIndex)
    {
        TotalItems = totalItems;
        
        int targetIndex = TotalItems > 0 
            ? Mathf.Clamp(initialIndex, 0, TotalItems - 1) 
            : 0;

        if (TotalItems > 1)
        {
            Enable();
            UpdateIndex(targetIndex); 
        }
        else
        {
            Reset();
        }
    }

    private void Enable()
    {
        Editable = true;
        MinValue = 0;
        MaxValue = TotalItems - 1;
        TickCount = TotalItems;
    }

    public void Reset()
    {
        TotalItems = 0;
        Editable = false;
        TickCount = 0;
        MinValue = 0;
        MaxValue = 1;
        UpdateIndex(0);
    }

    private void OnSliderValueChanged(double value)
    {
        int newIndex = (int)value;
        UpdateIndex(newIndex);
    }

    private void UpdateIndex(int newIndex)
    {
        if (CurrentIndex == newIndex) return;

        CurrentIndex = newIndex;
        SetValueNoSignal(CurrentIndex);
        IndexChanged?.Invoke(CurrentIndex);
    }
}