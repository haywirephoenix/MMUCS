using System;

namespace MMUCS.Runtime.Scripts;

/// <summary>
/// The v7+ camera (ScummVM camera.cpp's ScummEngine_v7 setCameraAt and setCameraFollows): where it looks in the
/// room, kept in the camera variables, and the actor it follows. <c>camera-follow</c> on an actor in another
/// room starts that room, which is how COMI's boot-parameter jump gets there. Moving with the followed actor
/// each frame (moveCamera) isn't done yet.
/// </summary>
public sealed partial class ScriptVm
{
    /// <summary>The actor the camera follows, 0 for none.</summary>
    public int CameraFollows { get; private set; }

    /// <summary>
    /// ScummVM <c>setCameraAt</c>: the camera looks at x,y (clamped to the room), its destination too; a move
    /// runs the scroll script.
    /// </summary>
    public void SetCameraAt(int x, int y)
    {
        int oldX = State[EngineVars.CameraPosX], oldY = State[EngineVars.CameraPosY];
        x = Math.Clamp(x, State[EngineVars.CameraMinX], Math.Max(State[EngineVars.CameraMinX], State[EngineVars.CameraMaxX]));
        y = Math.Clamp(y, State[EngineVars.CameraMinY], Math.Max(State[EngineVars.CameraMinY], State[EngineVars.CameraMaxY]));
        State[EngineVars.CameraPosX] = State[EngineVars.CameraDestX] = x;
        State[EngineVars.CameraPosY] = State[EngineVars.CameraDestY] = y;
        if ((x != oldX || y != oldY) && State[EngineVars.ScrollScript] is int scroll and not 0) RunScript(scroll);
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
        int ax = Math.Abs(a.X - State[EngineVars.CameraPosX]), ay = Math.Abs(a.Y - State[EngineVars.CameraPosY]);
        if (ax > State[EngineVars.CameraThresholdX] || ay > State[EngineVars.CameraThresholdY] || ax > ScreenWidth / 2 || ay > ScreenHeight / 2)
            SetCameraAt(a.X, a.Y);
        if (a.Number != old) RunInventoryScript(0);
    }

    /// <summary><c>camera-at</c>: the camera stops following and looks at x,y.</summary>
    private void CameraAt(int x, int y)
    {
        CameraFollows = 0;
        State[EngineVars.CameraFollowedActor] = 0;
        SetCameraAt(x, y);
    }
}
