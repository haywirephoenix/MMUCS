using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// Runs the engine's work on its own thread, one step at a time, handing control back and forth with the
/// host so only one of them ever runs: <see cref="Run"/> starts a step and waits until it's done or the
/// engine <see cref="Park"/>s in the middle of it (inside <c>start-video</c>, as ScummVM's engine waits for
/// the video); <see cref="Resume"/> carries the parked step on. The interpreter stays as synchronous as
/// ScummVM's, and the host's thread is never blocked for longer than a step.
/// </summary>
public sealed class EngineThread : IDisposable
{
    private readonly SemaphoreSlim _toEngine = new(0), _toHost = new(0);
    private readonly Thread _thread;
    private Action _work;
    private ExceptionDispatchInfo _error;
    private volatile bool _disposed;

    public EngineThread(string name = "MMUCS engine")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Whether a step is parked, waiting for <see cref="Resume"/>.</summary>
    public bool IsParked { get; private set; }

    /// <summary>Whether this is the engine's thread (where <see cref="Park"/> may be called).</summary>
    public bool IsEngineThread => Thread.CurrentThread == _thread;

    /// <summary>Runs <paramref name="work"/> on the engine thread; returns when it's done (true) or parked (false).</summary>
    public bool Run(Action work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsParked) throw new InvalidOperationException("a step is parked: resume it first");
        _work = work;
        _toEngine.Release();
        return WaitForEngine();
    }

    /// <summary>Carries the parked step on; returns when it's done (true) or parked again (false).</summary>
    public bool Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsParked) return true;
        _toEngine.Release();
        return WaitForEngine();
    }

    /// <summary>On the engine thread, inside a step: hands control to the host until it calls <see cref="Resume"/>.</summary>
    public void Park()
    {
        if (!IsEngineThread) throw new InvalidOperationException("only the engine thread parks");
        IsParked = true;
        _toHost.Release();
        _toEngine.Wait();
        IsParked = false;
        if (_disposed) throw new OperationCanceledException("the engine thread was disposed while parked");
    }

    private bool WaitForEngine()
    {
        _toHost.Wait();
        if (_error is { } error)
        {
            _error = null;
            error.Throw();
        }
        return !IsParked;
    }

    private void Loop()
    {
        while (true)
        {
            _toEngine.Wait();
            if (_disposed) return;
            try { _work(); }
            catch (OperationCanceledException) when (_disposed) { return; }
            catch (Exception ex) { _error = ExceptionDispatchInfo.Capture(ex); }
            _work = null;
            IsParked = false;
            _toHost.Release();
        }
    }

    /// <summary>Ends the thread, abandoning a parked step.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _toEngine.Release();
    }
}
