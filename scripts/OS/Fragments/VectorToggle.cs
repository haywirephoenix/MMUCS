using System.Collections.Generic;

public static class VectorToggle
{
    static VectorToggle() => _totalQuotes = _vectorToggleQuotes.Count;
    private enum ToggleState { TextCycle, Counting, Achievement, None }
    private static ToggleState _currentState = ToggleState.TextCycle;
    
    private static int _clickCount = 0;
    private static readonly int _totalQuotes; 

    private static readonly Queue<string> _vectorToggleQuotes = new Queue<string>(new[] { 
        "This toggle doesn't do anything yet.",
        "Seriously, it has no effect.",
        "You can toggle it all you like, it won't work.",
        "Toggling the same thing again, expecting a different result...",
        "Look, I'm just a string array, I can't actually compile vector math.",
        "Are you doing this just to watch me change text?",
        "Okay, fine. Vector mode: ACTIVATED. (Just kidding).",
        "Please stop. The UI layout engine is getting tired.",
        "Your dedication to toggling this is honestly inspiring.",
        "Maybe if you toggle it 100 times it will unlock.",
    });

    private const string _achieve = "[ Achievement Unlocked! ] - Unshakable Determination.";
    public static void OnVectorToggled(bool toggledOn)
    {
        if (_currentState == ToggleState.None) return;

        _clickCount++;

        string statusText = _currentState switch
        {
            ToggleState.TextCycle => RotateQueue(),
            ToggleState.Counting  => $"You have toggled {_clickCount} times.",
            ToggleState.Achievement => _achieve,
            _ => string.Empty
        };

        StatusBar.SetStatus(statusText);

        _currentState = _currentState switch
        {
            ToggleState.TextCycle when _clickCount >= _totalQuotes => ToggleState.Counting,
            ToggleState.Counting  when _clickCount >= 100          => ToggleState.Achievement,
            ToggleState.Achievement                                => ToggleState.None,
            var state => state
        };
    }
    
    public static void ResetClickCount()
    {
        _clickCount = 0;
        _currentState = ToggleState.TextCycle; 
    }
    private static string RotateQueue()
    {
        string quote = _vectorToggleQuotes.Dequeue();
        _vectorToggleQuotes.Enqueue(quote);
        return quote;
    }
}