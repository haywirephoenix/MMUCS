using System.Collections.Generic;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// The sentence queue (ScummVM script.cpp's doSentence and checkAndRunSentenceScript, o6_stopSentence): the
/// verb script turns a click into a sentence (verb, object, object; COMI's walk to a point is verb 10 with the
/// point as the objects), and once a frame, when the sentence script isn't busy, the newest sentence runs it.
/// Freezing scripts holds the sentences queued then.
/// </summary>
public sealed partial class ScriptVm
{
    /// <summary>ScummVM <c>NUM_SENTENCE</c>.</summary>
    public const int MaxSentences = 6;

    private sealed class Sentence
    {
        public int Verb, ObjectA, ObjectB, FreezeCount;
    }

    private readonly List<Sentence> _sentences = [];

    /// <summary>The sentences waiting, oldest first (verb, object, object).</summary>
    public IEnumerable<(int Verb, int ObjectA, int ObjectB)> Sentences
    {
        get { foreach (var s in _sentences) yield return (s.Verb, s.ObjectA, s.ObjectB); }
    }

    /// <summary>Counts <c>stop-sentence</c>s, which drop the frame's click (ScummVM <c>clearClickedStatus</c>).</summary>
    internal int ClickClears { get; private set; }

    /// <summary>ScummVM <c>doSentence</c> (v7+): queued, unless both objects are the same or it's the newest one again.</summary>
    private void DoSentence(int verb, int objectA, int objectB)
    {
        if (objectA == objectB) return;
        if (_sentences.Count > 0 && _sentences[^1] is var last && last.Verb == verb && last.ObjectA == objectA && last.ObjectB == objectB) return;
        if (_sentences.Count >= MaxSentences) throw new ScriptException($"more than {MaxSentences} sentences queued");
        _sentences.Add(new Sentence { Verb = verb, ObjectA = objectA, ObjectB = objectB });
    }

    /// <summary>ScummVM <c>o6_stopSentence</c>: the queue empties, the sentence script stops and the frame's click is dropped.</summary>
    private void StopSentence()
    {
        _sentences.Clear();
        StopScript(State[EngineVars.SentenceScript]);
        ClickClears++;
    }

    /// <summary>
    /// ScummVM <c>checkAndRunSentenceScript</c>: unless a copy of the sentence script is running (not frozen) or
    /// the newest sentence is held by a freeze, that sentence leaves the queue and runs the sentence script with
    /// verb, object, object.
    /// </summary>
    public void CheckAndRunSentenceScript()
    {
        int script = State[EngineVars.SentenceScript];
        foreach (var s in State.Slots)
            if (s.Number == script && s.IsScript && s.IsAlive && s.FreezeCount == 0) return;
        if (_sentences.Count == 0 || _sentences[^1].FreezeCount != 0) return;
        var st = _sentences[^1];
        _sentences.RemoveAt(_sentences.Count - 1);
        if (script != 0) RunScript(script, args: [st.Verb, st.ObjectA, st.ObjectB]);
    }

    /// <summary><c>wait-for-sentence</c> (v8): while the sentence script runs or a sentence waits that isn't held by a freeze.</summary>
    private bool SentenceBusy()
    {
        bool running = State[EngineVars.SentenceScript] is int script and not 0 && IsScriptRunning(script);
        if (_sentences.Count > 0) return !(_sentences[^1].FreezeCount != 0 && !running);
        return running;
    }

    private void FreezeSentences()
    {
        foreach (var s in _sentences) s.FreezeCount++;
    }

    private void UnfreezeSentences()
    {
        foreach (var s in _sentences)
            if (s.FreezeCount > 0) s.FreezeCount--;
    }
}
