using System;
using MMUCS.Core.Scumm;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// The v7+ camera (ScummVM camera.cpp's ScummEngine_v7 setCameraAt, setCameraFollows, panCameraTo and
/// moveCamera): where it looks in the room (its centre, <see cref="CameraX"/>, <see cref="CameraY"/>), where it's
/// heading and the actor it follows. Each frame it heads for its destination, or the followed actor once that
/// actor is past the thresholds, speeding up by the accel variables to at most 8 pixels a frame. The camera
/// variables mirror it as the engine sets them. <c>camera-follow</c> on an actor in another room starts that
/// room, which is how COMI's boot-parameter jump gets there.
/// </summary>
public sealed partial class ScriptVm
{
    private int _cameraX = ScreenWidth / 2, _cameraY = ScreenHeight / 2, _cameraDestX = ScreenWidth / 2, _cameraDestY = ScreenHeight / 2;
    private int _cameraAccelX, _cameraAccelY;
    private bool _cameraMovingToActor;

    /// <summary>The camera's centre in the room: the page drawn is a screen around it.</summary>
    public int CameraX => _cameraX;
    public int CameraY => _cameraY;

    /// <summary>The actor the camera follows, 0 for none.</summary>
    public int CameraFollows { get; private set; }

    /// <summary><c>camera-pause</c> / <c>camera-resume</c> (v8): a paused camera stays where it is.</summary>
    public bool CameraFrozen { get; private set; }

    private int ClampCameraX(int x) => Math.Clamp(x, State[EngineVars.CameraMinX], Math.Max(State[EngineVars.CameraMinX], State[EngineVars.CameraMaxX]));
    private int ClampCameraY(int y) => Math.Clamp(y, State[EngineVars.CameraMinY], Math.Max(State[EngineVars.CameraMinY], State[EngineVars.CameraMaxY]));

    /// <summary>
    /// ScummVM <c>setCameraAt</c>: the camera looks at x,y (clamped to the room), its destination too; a move
    /// runs the scroll script.
    /// </summary>
    public void SetCameraAt(int x, int y)
    {
        int oldX = _cameraX, oldY = _cameraY;
        _cameraX = _cameraDestX = ClampCameraX(x);
        _cameraY = _cameraDestY = ClampCameraY(y);
        State[EngineVars.CameraDestX] = _cameraDestX;
        State[EngineVars.CameraDestY] = _cameraDestY;
        if ((_cameraX != oldX || _cameraY != oldY) && State[EngineVars.ScrollScript] is int scroll and not 0)
        {
            UpdateCameraVars();
            RunScript(scroll);
        }
    }

    /// <summary>
    /// ScummVM <c>setCameraFollows</c> (v7): the camera follows the actor, whose room starts if it isn't this
    /// one, and jumps to it if it's past the thresholds or more than half a screen away; a new actor to follow
    /// runs the inventory script (for its inventory).
    /// </summary>
    public void SetCameraFollows(Actor a)
    {
        int old = CameraFollows;
        CameraFollows = a.Number;
        State[EngineVars.CameraFollowedActor] = a.Number;
        if (a.Room != CurrentRoom) StartScene(a.Room);
        int ax = Math.Abs(a.X - _cameraX), ay = Math.Abs(a.Y - _cameraY);
        if (ax > State[EngineVars.CameraThresholdX] || ay > State[EngineVars.CameraThresholdY] || ax > ScreenWidth / 2 || ay > ScreenHeight / 2)
            SetCameraAt(a.X, a.Y);
        if (a.Number != old) RunInventoryScript(0);
    }

    /// <summary><c>camera-at</c>: the camera stops following and looks at x,y.</summary>
    public void CameraAt(int x, int y)
    {
        CameraFollows = 0;
        State[EngineVars.CameraFollowedActor] = 0;
        SetCameraAt(x, y);
    }

    /// <summary>ScummVM <c>panCameraTo</c> (v7): the camera stops following and heads for x,y over the next frames.</summary>
    private void PanCameraTo(int x, int y)
    {
        CameraFollows = 0;
        State[EngineVars.CameraFollowedActor] = 0;
        State[EngineVars.CameraDestX] = _cameraDestX = x;
        State[EngineVars.CameraDestY] = _cameraDestY = y;
    }

    /// <summary>ScummVM <c>scummLoop_updateScummVars</c> (v7+): the position variables are the camera's.</summary>
    internal void UpdateCameraVars()
    {
        State[EngineVars.CameraPosX] = _cameraX;
        State[EngineVars.CameraPosY] = _cameraY;
    }

    /// <summary>
    /// ScummVM <c>moveCamera</c> (v7), once a frame in a room: a followed actor past the thresholds becomes the
    /// destination (the camera snaps to it on an axis whose threshold is 0); the camera steps towards the
    /// destination by the speed variables, which the accel variables raise (to 8) until it's there, when they're
    /// reset. A move runs the scroll script.
    /// </summary>
    public void MoveCamera()
    {
        if (CameraFrozen) return;
        int oldX = _cameraX, oldY = _cameraY;
        Actor a = null;
        if (CameraFollows != 0)
        {
            a = Actors[CameraFollows];
            if (Math.Abs(_cameraX - a.X) > State[EngineVars.CameraThresholdX] || Math.Abs(_cameraY - a.Y) > State[EngineVars.CameraThresholdY])
            {
                _cameraMovingToActor = true;
                if (State[EngineVars.CameraThresholdX] == 0) _cameraX = a.X;
                if (State[EngineVars.CameraThresholdY] == 0) _cameraY = a.Y;
                _cameraX = ClampCameraX(_cameraX);
                _cameraY = ClampCameraY(_cameraY);
            }
        }
        else _cameraMovingToActor = false;

        if (_cameraMovingToActor)
        {
            State[EngineVars.CameraDestX] = _cameraDestX = a.X;
            State[EngineVars.CameraDestY] = _cameraDestY = a.Y;
        }
        _cameraDestX = ClampCameraX(_cameraDestX);
        _cameraDestY = ClampCameraY(_cameraDestY);

        int speedX = (short)State[EngineVars.CameraSpeedX], speedY = (short)State[EngineVars.CameraSpeedY];
        if (_cameraX < _cameraDestX) _cameraX = Math.Min(_cameraX + speedX, _cameraDestX);
        if (_cameraX > _cameraDestX) _cameraX = Math.Max(_cameraX - speedX, _cameraDestX);
        if (_cameraY < _cameraDestY) _cameraY = Math.Min(_cameraY + speedY, _cameraDestY);
        if (_cameraY > _cameraDestY) _cameraY = Math.Max(_cameraY - speedY, _cameraDestY);

        if (_cameraX == _cameraDestX && _cameraY == _cameraDestY)
        {
            _cameraMovingToActor = false;
            _cameraAccelX = _cameraAccelY = 0;
            State[EngineVars.CameraSpeedX] = State[EngineVars.CameraSpeedY] = 0;
        }
        else
        {
            _cameraAccelX += (short)State[EngineVars.CameraAccelX];
            _cameraAccelY += (short)State[EngineVars.CameraAccelY];
            State[EngineVars.CameraSpeedX] = Math.Min(8, State[EngineVars.CameraSpeedX] + _cameraAccelX / 100);
            State[EngineVars.CameraSpeedY] = Math.Min(8, State[EngineVars.CameraSpeedY] + _cameraAccelY / 100);
        }

        if (_cameraX != oldX || _cameraY != oldY)
        {
            UpdateCameraVars();
            if (State[EngineVars.ScrollScript] is int scroll and not 0) RunScript(scroll);
        }
    }

    /// <summary><c>cameraOps</c> (v8): pause and resume.</summary>
    private bool CameraOps(ScriptInstruction ins)
    {
        switch (ins.SubOp)
        {
            case 0x32: CameraFrozen = true; return true;
            case 0x33: CameraFrozen = false; return true;
            default: return false;
        }
    }
}
