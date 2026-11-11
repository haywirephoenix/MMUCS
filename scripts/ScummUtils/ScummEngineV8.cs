using System;
using Godot;

public static partial class ScummEngineV8
{
    public static void Execute(byte opcodeCode)
    {
        if (Opcodes.TryGetValue(opcodeCode, out Action action))
            action.Invoke();
        else
            GD.Print($"Warning: Opcode 0x{opcodeCode:X2} not implemented.");
    }
    
    public static void Invoke_O6_PushWord() { }
    public static void Invoke_O6_PushWordVar() { }
    public static void Invoke_O6_WordArrayRead() { }
    public static void Invoke_O6_WordArrayIndexedRead() { }
    public static void Invoke_O6_Dup() { }
    public static void Invoke_O6_Pop() { }
    public static void Invoke_O6_Not() { }
    public static void Invoke_O6_Eq() { }
    public static void Invoke_O6_Neq() { }
    public static void Invoke_O6_Gt() { }
    public static void Invoke_O6_Lt() { }
    public static void Invoke_O6_Le() { }
    public static void Invoke_O6_Ge() { }
    public static void Invoke_O6_Add() { }
    public static void Invoke_O6_Sub() { }
    public static void Invoke_O6_Mul() { }
    public static void Invoke_O6_Div() { }
    public static void Invoke_O6_Land() { }
    public static void Invoke_O6_Lor() { }
    public static void Invoke_O6_Band() { }
    public static void Invoke_O6_Bor() { }
    public static void Invoke_O8_Mod() { }
    public static void Invoke_O6_If() { }
    public static void Invoke_O6_IfNot() { }
    public static void Invoke_O6_Jump() { }
    public static void Invoke_O6_BreakHere() { }
    public static void Invoke_O6_DelayFrames() { }
    public static void Invoke_O8_Wait() { }
    public static void Invoke_O6_Delay() { }
    public static void Invoke_O6_DelaySeconds() { }
    public static void Invoke_O6_DelayMinutes() { }
    public static void Invoke_O6_WriteWordVar() { }
    public static void Invoke_O6_WordVarInc() { }
    public static void Invoke_O6_WordVarDec() { }
    public static void Invoke_O8_DimArray() { }
    public static void Invoke_O6_WordArrayWrite() { }
    public static void Invoke_O6_WordArrayInc() { }
    public static void Invoke_O6_WordArrayDec() { }
    public static void Invoke_O8_Dim2dimArray() { }
    public static void Invoke_O6_WordArrayIndexedWrite() { }
    public static void Invoke_O8_ArrayOps() { }
    public static void Invoke_O6_StartScript() { }
    public static void Invoke_O6_StartScriptQuick() { }
    public static void Invoke_O6_StopObjectCode() { }
    public static void Invoke_O6_StopScript() { }
    public static void Invoke_O6_JumpToScript() { }
    public static void Invoke_O6_Dummy()
    { /* NOP */
    }
    public static void Invoke_O6_StartObject() { }
    public static void Invoke_O6_StopObjectScript() { }
    public static void Invoke_O6_Cutscene() { }
    public static void Invoke_O6_EndCutscene() { }
    public static void Invoke_O6_FreezeUnfreeze() { }
    public static void Invoke_O6_BeginOverride() { }
    public static void Invoke_O6_EndOverride() { }
    public static void Invoke_O6_StopSentence() { }
    public static void Invoke_O6_SetClass() { }
    public static void Invoke_O6_SetState() { }
    public static void Invoke_O6_SetOwner() { }
    public static void Invoke_O6_PanCameraTo() { }
    public static void Invoke_O6_ActorFollowCamera() { }
    public static void Invoke_O6_SetCameraAt() { }
    public static void Invoke_O6_PrintActor() { }
    public static void Invoke_O6_PrintEgo() { }
    public static void Invoke_O6_TalkActor() { }
    public static void Invoke_O6_TalkEgo() { }
    public static void Invoke_O6_PrintLine() { }
    public static void Invoke_O6_PrintText() { }
    public static void Invoke_O6_PrintDebug() { }
    public static void Invoke_O6_PrintSystem() { }
    public static void Invoke_O8_BlastText() { }
    public static void Invoke_O8_DrawObject() { }
    public static void Invoke_O8_CursorCommand() { }
    public static void Invoke_O6_LoadRoom() { }
    public static void Invoke_O6_LoadRoomWithEgo() { }
    public static void Invoke_O6_WalkActorToObj() { }
    public static void Invoke_O6_WalkActorTo() { }
    public static void Invoke_O6_PutActorAtXY() { }
    public static void Invoke_O6_PutActorAtObject() { }
    public static void Invoke_O6_FaceActor() { }
    public static void Invoke_O6_AnimateActor() { }
    public static void Invoke_O6_DoSentence() { }
    public static void Invoke_O6_PickupObject() { }
    public static void Invoke_O6_SetBoxFlags() { }
    public static void Invoke_O6_CreateBoxMatrix() { }
    public static void Invoke_O8_ResourceRoutines() { }
    public static void Invoke_O8_RoomOps() { }
    public static void Invoke_O8_ActorOps()
    { /* Parsing SubOpType logic here */
    }
    public static void Invoke_O8_CameraOps() { }
    public static void Invoke_O8_VerbOps() { }
    public static void Invoke_O6_StartSound() { }
    public static void Invoke_O6_StartMusic() { }
    public static void Invoke_O6_StopSound() { }
    public static void Invoke_O6_SoundKludge() { }
    public static void Invoke_O8_SystemOps() { }
    public static void Invoke_O6_SaveRestoreVerbs() { }
    public static void Invoke_O6_SetObjectName() { }
    public static void Invoke_O6_GetDateTime() { }
    public static void Invoke_O6_DrawBox() { }
    public static void Invoke_O8_StartVideo() { }
    public static void Invoke_O8_KernelSetFunctions() { }
    public static void Invoke_O6_StartScriptQuick2() { }
    public static void Invoke_O6_StartObjectQuick() { }
    public static void Invoke_O6_PickOneOf() { }
    public static void Invoke_O6_PickOneOfDefault() { }
    public static void Invoke_O6_IsAnyOf() { }
    public static void Invoke_O6_GetRandomNumber() { }
    public static void Invoke_O6_GetRandomNumberRange() { }
    public static void Invoke_O6_IfClassOfIs() { }
    public static void Invoke_O6_GetState() { }
    public static void Invoke_O6_GetOwner() { }
    public static void Invoke_O6_IsScriptRunning() { }
    public static void Invoke_O6_IsSoundRunning() { }
    public static void Invoke_O6_Abs() { }
    public static void Invoke_O8_KernelGetFunctions() { }
    public static void Invoke_O6_IsActorInBox() { }
    public static void Invoke_O6_GetVerbEntrypoint() { }
    public static void Invoke_O6_GetActorFromXY() { }
    public static void Invoke_O6_FindObject() { }
    public static void Invoke_O6_GetVerbFromXY() { }
    public static void Invoke_O6_FindInventory() { }
    public static void Invoke_O6_GetInventoryCount() { }
    public static void Invoke_O6_GetAnimateVariable() { }
    public static void Invoke_O6_GetActorRoom() { }
    public static void Invoke_O6_GetActorWalkBox() { }
    public static void Invoke_O6_GetActorMoving() { }
    public static void Invoke_O6_GetActorCostume() { }
    public static void Invoke_O6_GetActorScaleX() { }
    public static void Invoke_O6_GetActorLayer() { }
    public static void Invoke_O6_GetActorElevation() { }
    public static void Invoke_O6_GetActorWidth() { }
    public static void Invoke_O6_GetObjectNewDir() { }
    public static void Invoke_O6_GetObjectX() { }
    public static void Invoke_O6_GetObjectY() { }
    public static void Invoke_O8_GetActorChore() { }
    public static void Invoke_O6_DistObjectObject() { }
    public static void Invoke_O6_DistPtPt() { }
    public static void Invoke_O8_GetObjectImageX() { }
    public static void Invoke_O8_GetObjectImageY() { }
    public static void Invoke_O8_GetObjectImageWidth() { }
    public static void Invoke_O8_GetObjectImageHeight() { }
    public static void Invoke_O8_GetStringWidth() { }
    public static void Invoke_O8_GetActorZPlane() { }
}