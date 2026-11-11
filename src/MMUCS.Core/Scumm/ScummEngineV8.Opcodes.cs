using System;
using System.Collections.Generic;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;


public static partial class ScummEngineV8
{
    public const byte O6_PushWord              = 0x01;
    public const byte O6_PushWordVar           = 0x02;
    public const byte O6_WordArrayRead         = 0x03;
    public const byte O6_WordArrayIndexedRead  = 0x04;
    public const byte O6_Dup                   = 0x05;
    public const byte O6_Pop                   = 0x06;
    public const byte O6_Not                   = 0x07;
    public const byte O6_Eq                    = 0x08;
    public const byte O6_Neq                   = 0x09;
    public const byte O6_Gt                    = 0x0a;
    public const byte O6_Lt                    = 0x0b;
    public const byte O6_Le                    = 0x0c;
    public const byte O6_Ge                    = 0x0d;
    public const byte O6_Add                   = 0x0e;
    public const byte O6_Sub                   = 0x0f;
    public const byte O6_Mul                   = 0x10;
    public const byte O6_Div                   = 0x11;
    public const byte O6_Land                  = 0x12;
    public const byte O6_Lor                   = 0x13;
    public const byte O6_Band                  = 0x14;
    public const byte O6_Bor                   = 0x15;
    public const byte O8_Mod                   = 0x16;
    public const byte O6_If                    = 0x64;
    public const byte O6_IfNot                 = 0x65;
    public const byte O6_Jump                  = 0x66;
    public const byte O6_BreakHere             = 0x67;
    public const byte O6_DelayFrames           = 0x68;
    public const byte O8_Wait                  = 0x69;
    public const byte O6_Delay                 = 0x6a;
    public const byte O6_DelaySeconds          = 0x6b;
    public const byte O6_DelayMinutes          = 0x6c;
    public const byte O6_WriteWordVar          = 0x6d;
    public const byte O6_WordVarInc            = 0x6e;
    public const byte O6_WordVarDec            = 0x6f;
    public const byte O8_DimArray              = 0x70;
    public const byte O6_WordArrayWrite        = 0x71;
    public const byte O6_WordArrayInc          = 0x72;
    public const byte O6_WordArrayDec          = 0x73;
    public const byte O8_Dim2dimArray          = 0x74;
    public const byte O6_WordArrayIndexedWrite = 0x75;
    public const byte O8_ArrayOps              = 0x76;
    public const byte O6_StartScript           = 0x79;
    public const byte O6_StartScriptQuick      = 0x7a;
    public const byte O6_StopObjectCode        = 0x7b;
    public const byte O6_StopScript            = 0x7c;
    public const byte O6_JumpToScript          = 0x7d;
    public const byte O6_Dummy                 = 0x7e;
    public const byte O6_StartObject           = 0x7f;
    public const byte O6_StopObjectScript      = 0x80;
    public const byte O6_Cutscene              = 0x81;
    public const byte O6_EndCutscene           = 0x82;
    public const byte O6_FreezeUnfreeze        = 0x83;
    public const byte O6_BeginOverride         = 0x84;
    public const byte O6_EndOverride           = 0x85;
    public const byte O6_StopSentence          = 0x86;
    public const byte O6_SetClass              = 0x89;
    public const byte O6_SetState              = 0x8a;
    public const byte O6_SetOwner              = 0x8b;
    public const byte O6_PanCameraTo           = 0x8c;
    public const byte O6_ActorFollowCamera     = 0x8d;
    public const byte O6_SetCameraAt           = 0x8e;
    public const byte O6_PrintActor            = 0x8f;
    public const byte O6_PrintEgo              = 0x90;
    public const byte O6_TalkActor             = 0x91;
    public const byte O6_TalkEgo               = 0x92;
    public const byte O6_PrintLine             = 0x93;
    public const byte O6_PrintText             = 0x94;
    public const byte O6_PrintDebug            = 0x95;
    public const byte O6_PrintSystem           = 0x96;
    public const byte O8_BlastText             = 0x97;
    public const byte O8_DrawObject            = 0x98;
    public const byte O8_CursorCommand         = 0x9c;
    public const byte O6_LoadRoom              = 0x9d;
    public const byte O6_LoadRoomWithEgo       = 0x9e;
    public const byte O6_WalkActorToObj        = 0x9f;
    public const byte O6_WalkActorTo           = 0xa0;
    public const byte O6_PutActorAtXY          = 0xa1;
    public const byte O6_PutActorAtObject      = 0xa2;
    public const byte O6_FaceActor             = 0xa3;
    public const byte O6_AnimateActor          = 0xa4;
    public const byte O6_DoSentence            = 0xa5;
    public const byte O6_PickupObject          = 0xa6;
    public const byte O6_SetBoxFlags           = 0xa7;
    public const byte O6_CreateBoxMatrix       = 0xa8;
    public const byte O8_ResourceRoutines      = 0xaa;
    public const byte O8_RoomOps               = 0xab;
    public const byte O8_ActorOps              = 0xac;
    public const byte O8_CameraOps             = 0xad;
    public const byte O8_VerbOps               = 0xae;
    public const byte O6_StartSound            = 0xaf;
    public const byte O6_StartMusic            = 0xb0;
    public const byte O6_StopSound             = 0xb1;
    public const byte O6_SoundKludge           = 0xb2;
    public const byte O8_SystemOps             = 0xb3;
    public const byte O6_SaveRestoreVerbs      = 0xb4;
    public const byte O6_SetObjectName         = 0xb5;
    public const byte O6_GetDateTime           = 0xb6;
    public const byte O6_DrawBox               = 0xb7;
    public const byte O8_StartVideo            = 0xb9;
    public const byte O8_KernelSetFunctions    = 0xba;
    public const byte O6_StartScriptQuick2     = 0xc8;
    public const byte O6_StartObjectQuick      = 0xc9;
    public const byte O6_PickOneOf             = 0xca;
    public const byte O6_PickOneOfDefault      = 0xcb;
    public const byte O6_IsAnyOf               = 0xcc;
    public const byte O6_GetRandomNumber       = 0xce;
    public const byte O6_GetRandomNumberRange  = 0xcf;
    public const byte O6_IfClassOfIs           = 0xd0;
    public const byte O6_GetState              = 0xd1;
    public const byte O6_GetOwner              = 0xd2;
    public const byte O6_IsScriptRunning       = 0xd3;
    public const byte O6_IsSoundRunning        = 0xd5;
    public const byte O6_Abs                   = 0xd6;
    public const byte O8_KernelGetFunctions    = 0xd8;
    public const byte O6_IsActorInBox          = 0xd9;
    public const byte O6_GetVerbEntrypoint     = 0xda;
    public const byte O6_GetActorFromXY        = 0xdb;
    public const byte O6_FindObject            = 0xdc;
    public const byte O6_GetVerbFromXY         = 0xdd;
    public const byte O6_FindInventory         = 0xdf;
    public const byte O6_GetInventoryCount     = 0xe0;
    public const byte O6_GetAnimateVariable    = 0xe1;
    public const byte O6_GetActorRoom          = 0xe2;
    public const byte O6_GetActorWalkBox       = 0xe3;
    public const byte O6_GetActorMoving        = 0xe4;
    public const byte O6_GetActorCostume       = 0xe5;
    public const byte O6_GetActorScaleX        = 0xe6;
    public const byte O6_GetActorLayer         = 0xe7;
    public const byte O6_GetActorElevation     = 0xe8;
    public const byte O6_GetActorWidth         = 0xe9;
    public const byte O6_GetObjectNewDir       = 0xea;
    public const byte O6_GetObjectX            = 0xeb;
    public const byte O6_GetObjectY            = 0xec;
    public const byte O8_GetActorChore         = 0xed;
    public const byte O6_DistObjectObject      = 0xee;
    public const byte O6_DistPtPt              = 0xef;
    public const byte O8_GetObjectImageX       = 0xf0;
    public const byte O8_GetObjectImageY       = 0xf1;
    public const byte O8_GetObjectImageWidth   = 0xf2;
    public const byte O8_GetObjectImageHeight  = 0xf3;
    public const byte O8_GetStringWidth        = 0xf6;
    public const byte O8_GetActorZPlane        = 0xf7;
    
    public static readonly Dictionary<byte, Action> Opcodes = new()
    {
        {O6_PushWord             , Invoke_O6_PushWord},
        {O6_PushWordVar          , Invoke_O6_PushWordVar},
        {O6_WordArrayRead        , Invoke_O6_WordArrayRead},
        {O6_WordArrayIndexedRead , Invoke_O6_WordArrayIndexedRead},
        {O6_Dup                  , Invoke_O6_Dup},
        {O6_Pop                  , Invoke_O6_Pop},
        {O6_Not                  , Invoke_O6_Not},
        {O6_Eq                   , Invoke_O6_Eq},
        {O6_Neq                  , Invoke_O6_Neq},
        {O6_Gt                   , Invoke_O6_Gt},
        {O6_Lt                   , Invoke_O6_Lt},
        {O6_Le                   , Invoke_O6_Le},
        {O6_Ge                   , Invoke_O6_Ge},
        {O6_Add                  , Invoke_O6_Add},
        {O6_Sub                  , Invoke_O6_Sub},
        {O6_Mul                  , Invoke_O6_Mul},
        {O6_Div                  , Invoke_O6_Div},
        {O6_Land                 , Invoke_O6_Land},
        {O6_Lor                  , Invoke_O6_Lor},
        {O6_Band                 , Invoke_O6_Band},
        {O6_Bor                  , Invoke_O6_Bor},
        {O8_Mod                  , Invoke_O8_Mod},
        {O6_If                   , Invoke_O6_If},
        {O6_IfNot                , Invoke_O6_IfNot},
        {O6_Jump                 , Invoke_O6_Jump},
        {O6_BreakHere            , Invoke_O6_BreakHere},
        {O6_DelayFrames          , Invoke_O6_DelayFrames},
        {O8_Wait                 , Invoke_O8_Wait},
        {O6_Delay                , Invoke_O6_Delay},
        {O6_DelaySeconds         , Invoke_O6_DelaySeconds},
        {O6_DelayMinutes         , Invoke_O6_DelayMinutes},
        {O6_WriteWordVar         , Invoke_O6_WriteWordVar},
        {O6_WordVarInc           , Invoke_O6_WordVarInc},
        {O6_WordVarDec           , Invoke_O6_WordVarDec},
        {O8_DimArray             , Invoke_O8_DimArray},
        {O6_WordArrayWrite       , Invoke_O6_WordArrayWrite},
        {O6_WordArrayInc         , Invoke_O6_WordArrayInc},
        {O6_WordArrayDec         , Invoke_O6_WordArrayDec},
        {O8_Dim2dimArray         , Invoke_O8_Dim2dimArray},
        {O6_WordArrayIndexedWrite, Invoke_O6_WordArrayIndexedWrite},
        {O8_ArrayOps             , Invoke_O8_ArrayOps},
        {O6_StartScript          , Invoke_O6_StartScript},
        {O6_StartScriptQuick     , Invoke_O6_StartScriptQuick},
        {O6_StopObjectCode       , Invoke_O6_StopObjectCode},
        {O6_StopScript           , Invoke_O6_StopScript},
        {O6_JumpToScript         , Invoke_O6_JumpToScript},
        {O6_Dummy                , Invoke_O6_Dummy},
        {O6_StartObject          , Invoke_O6_StartObject},
        {O6_StopObjectScript     , Invoke_O6_StopObjectScript},
        {O6_Cutscene             , Invoke_O6_Cutscene},
        {O6_EndCutscene          , Invoke_O6_EndCutscene},
        {O6_FreezeUnfreeze       , Invoke_O6_FreezeUnfreeze},
        {O6_BeginOverride        , Invoke_O6_BeginOverride},
        {O6_EndOverride          , Invoke_O6_EndOverride},
        {O6_StopSentence         , Invoke_O6_StopSentence},
        {O6_SetClass             , Invoke_O6_SetClass},
        {O6_SetState             , Invoke_O6_SetState},
        {O6_SetOwner             , Invoke_O6_SetOwner},
        {O6_PanCameraTo          , Invoke_O6_PanCameraTo},
        {O6_ActorFollowCamera    , Invoke_O6_ActorFollowCamera},
        {O6_SetCameraAt          , Invoke_O6_SetCameraAt},
        {O6_PrintActor           , Invoke_O6_PrintActor},
        {O6_PrintEgo             , Invoke_O6_PrintEgo},
        {O6_TalkActor            , Invoke_O6_TalkActor},
        {O6_TalkEgo              , Invoke_O6_TalkEgo},
        {O6_PrintLine            , Invoke_O6_PrintLine},
        {O6_PrintText            , Invoke_O6_PrintText},
        {O6_PrintDebug           , Invoke_O6_PrintDebug},
        {O6_PrintSystem          , Invoke_O6_PrintSystem},
        {O8_BlastText            , Invoke_O8_BlastText},
        {O8_DrawObject           , Invoke_O8_DrawObject},
        {O8_CursorCommand        , Invoke_O8_CursorCommand},
        {O6_LoadRoom             , Invoke_O6_LoadRoom},
        {O6_LoadRoomWithEgo      , Invoke_O6_LoadRoomWithEgo},
        {O6_WalkActorToObj       , Invoke_O6_WalkActorToObj},
        {O6_WalkActorTo          , Invoke_O6_WalkActorTo},
        {O6_PutActorAtXY         , Invoke_O6_PutActorAtXY},
        {O6_PutActorAtObject     , Invoke_O6_PutActorAtObject},
        {O6_FaceActor            , Invoke_O6_FaceActor},
        {O6_AnimateActor         , Invoke_O6_AnimateActor},
        {O6_DoSentence           , Invoke_O6_DoSentence},
        {O6_PickupObject         , Invoke_O6_PickupObject},
        {O6_SetBoxFlags          , Invoke_O6_SetBoxFlags},
        {O6_CreateBoxMatrix      , Invoke_O6_CreateBoxMatrix},
        {O8_ResourceRoutines     , Invoke_O8_ResourceRoutines},
        {O8_RoomOps              , Invoke_O8_RoomOps},
        {O8_ActorOps             , Invoke_O8_ActorOps},
        {O8_CameraOps            , Invoke_O8_CameraOps},
        {O8_VerbOps              , Invoke_O8_VerbOps},
        {O6_StartSound           , Invoke_O6_StartSound},
        {O6_StartMusic           , Invoke_O6_StartMusic},
        {O6_StopSound            , Invoke_O6_StopSound},
        {O6_SoundKludge          , Invoke_O6_SoundKludge},
        {O8_SystemOps            , Invoke_O8_SystemOps},
        {O6_SaveRestoreVerbs     , Invoke_O6_SaveRestoreVerbs},
        {O6_SetObjectName        , Invoke_O6_SetObjectName},
        {O6_GetDateTime          , Invoke_O6_GetDateTime},
        {O6_DrawBox              , Invoke_O6_DrawBox},
        {O8_StartVideo           , Invoke_O8_StartVideo},
        {O8_KernelSetFunctions   , Invoke_O8_KernelSetFunctions},
        {O6_StartScriptQuick2    , Invoke_O6_StartScriptQuick2},
        {O6_StartObjectQuick     , Invoke_O6_StartObjectQuick},
        {O6_PickOneOf            , Invoke_O6_PickOneOf},
        {O6_PickOneOfDefault     , Invoke_O6_PickOneOfDefault},
        {O6_IsAnyOf              , Invoke_O6_IsAnyOf},
        {O6_GetRandomNumber      , Invoke_O6_GetRandomNumber},
        {O6_GetRandomNumberRange , Invoke_O6_GetRandomNumberRange},
        {O6_IfClassOfIs          , Invoke_O6_IfClassOfIs},
        {O6_GetState             , Invoke_O6_GetState},
        {O6_GetOwner             , Invoke_O6_GetOwner},
        {O6_IsScriptRunning      , Invoke_O6_IsScriptRunning},
        {O6_IsSoundRunning       , Invoke_O6_IsSoundRunning},
        {O6_Abs                  , Invoke_O6_Abs},
        {O8_KernelGetFunctions   , Invoke_O8_KernelGetFunctions},
        {O6_IsActorInBox         , Invoke_O6_IsActorInBox},
        {O6_GetVerbEntrypoint    , Invoke_O6_GetVerbEntrypoint},
        {O6_GetActorFromXY       , Invoke_O6_GetActorFromXY},
        {O6_FindObject           , Invoke_O6_FindObject},
        {O6_GetVerbFromXY        , Invoke_O6_GetVerbFromXY},
        {O6_FindInventory        , Invoke_O6_FindInventory},
        {O6_GetInventoryCount    , Invoke_O6_GetInventoryCount},
        {O6_GetAnimateVariable   , Invoke_O6_GetAnimateVariable},
        {O6_GetActorRoom         , Invoke_O6_GetActorRoom},
        {O6_GetActorWalkBox      , Invoke_O6_GetActorWalkBox},
        {O6_GetActorMoving       , Invoke_O6_GetActorMoving},
        {O6_GetActorCostume      , Invoke_O6_GetActorCostume},
        {O6_GetActorScaleX       , Invoke_O6_GetActorScaleX},
        {O6_GetActorLayer        , Invoke_O6_GetActorLayer},
        {O6_GetActorElevation    , Invoke_O6_GetActorElevation},
        {O6_GetActorWidth        , Invoke_O6_GetActorWidth},
        {O6_GetObjectNewDir      , Invoke_O6_GetObjectNewDir},
        {O6_GetObjectX           , Invoke_O6_GetObjectX},
        {O6_GetObjectY           , Invoke_O6_GetObjectY},
        {O8_GetActorChore        , Invoke_O8_GetActorChore},
        {O6_DistObjectObject     , Invoke_O6_DistObjectObject},
        {O6_DistPtPt             , Invoke_O6_DistPtPt},
        {O8_GetObjectImageX      , Invoke_O8_GetObjectImageX},
        {O8_GetObjectImageY      , Invoke_O8_GetObjectImageY},
        {O8_GetObjectImageWidth  , Invoke_O8_GetObjectImageWidth},
        {O8_GetObjectImageHeight , Invoke_O8_GetObjectImageHeight},
        {O8_GetStringWidth       , Invoke_O8_GetStringWidth},
        {O8_GetActorZPlane       , Invoke_O8_GetActorZPlane},
    };

    public enum SubOpType : byte
    {
        SO_INT_ARRAY    = 10, // SO_ARRAY_SCUMMVAR
        SO_STRING_ARRAY = 11, // SO_ARRAY_STRING
        SO_UNDIM_ARRAY  = 12, // SO_ARRAY_UNDIM

        SO_ASSIGN_STRING    = 20,
        SO_ASSIGN_INT_LIST  = 21, // SO_ASSIGN_SCUMMVAR_LIST
        SO_ASSIGN_2DIM_LIST = 22,

        SO_WAIT_FOR_ACTOR     = 30,
        SO_WAIT_FOR_MESSAGE   = 31,
        SO_WAIT_FOR_CAMERA    = 32,
        SO_WAIT_FOR_SENTENCE  = 33,
        SO_WAIT_FOR_ANIMATION = 34,
        SO_WAIT_FOR_TURN      = 35,

        SO_RESTART = 40, // SO_SYSTEM_RESTART
        SO_QUIT    = 41, // SO_SYSTEM_QUIT

        SO_CAMERA_PAUSE  = 50,
        SO_CAMERA_RESUME = 51,

        SO_HEAP_LOAD_CHARSET   = 60,
        SO_HEAP_LOAD_COSTUME   = 61,
        SO_HEAP_LOAD_OBJECT    = 62,
        SO_HEAP_LOAD_ROOM      = 63,
        SO_HEAP_LOAD_SCRIPT    = 64,
        SO_HEAP_LOAD_SOUND     = 65,
        SO_HEAP_LOCK_COSTUME   = 66,
        SO_HEAP_LOCK_ROOM      = 67,
        SO_HEAP_LOCK_SCRIPT    = 68,
        SO_HEAP_LOCK_SOUND     = 69,
        SO_HEAP_UNLOCK_COSTUME = 70,
        SO_HEAP_UNLOCK_ROOM    = 71,
        SO_HEAP_UNLOCK_SCRIPT  = 72,
        SO_HEAP_UNLOCK_SOUND   = 73,
        SO_HEAP_NUKE_COSTUME   = 74,
        SO_HEAP_NUKE_ROOM      = 75,
        SO_HEAP_NUKE_SCRIPT    = 76,
        SO_HEAP_NUKE_SOUND     = 77,

        SO_ROOM_PALETTE = 82,

        SO_ROOM_FADE          = 87,
        SO_ROOM_RGB_INTENSITY = 88,
        SO_ROOM_TRANSFORM     = 89,

        SO_ROOM_NEW_PALETTE = 92,
        SO_ROOM_SAVE_GAME   = 93,
        SO_ROOM_LOAD_GAME   = 94,
        SO_ROOM_SATURATION  = 95,

        SO_COSTUME   = 100,
        SO_STEP_DIST = 101,

        SO_ANIMATION_DEFAULT      = 103, // SO_ACTOR_ANIMATION_DEFAULT
        SO_INIT_ANIMATION         = 104, // SO_ACTOR_ANIMATION_INIT
        SO_TALK_ANIMATION         = 105, // SO_ACTOR_ANIMATION_TALK
        SO_WALK_ANIMATION         = 106, // SO_ACTOR_ANIMATION_WALK
        SO_STAND_ANIMATION        = 107, // SO_ACTOR_ANIMATION_STAND
        SO_ANIMATION_SPEED        = 108, // SO_ACTOR_ANIMATION_SPEED
        SO_DEFAULT                = 109, // SO_ACTOR_DEFAULT
        SO_ELEVATION              = 110, // SO_ACTOR_ELEVATION
        SO_PALETTE                = 111, // SO_ACTOR_PALETTE
        SO_TALK_COLOR             = 112, // SO_ACTOR_TALK_COLOR
        SO_ACTOR_NAME             = 113,
        SO_ACTOR_WIDTH            = 114,
        SO_SCALE                  = 115, // SO_ACTOR_SCALE
        SO_NEVER_ZCLIP            = 116, // SO_ACTOR_NEVER_ZCLIP
        SO_ALWAYS_ZCLIP           = 117, // SO_ACTOR_ALWAYS_ZCLIP
        SO_IGNORE_BOXES           = 118, // SO_ACTOR_IGNORE_BOXES
        SO_FOLLOW_BOXES           = 119, // SO_ACTOR_FOLLOW_BOXES
        SO_SHADOW                 = 120, // SO_ACTOR_SPECIAL_DRAW
        SO_TEXT_OFFSET            = 121, // SO_ACTOR_TEXT_OFFSET
        SO_ACTOR_INIT             = 122,
        SO_ACTOR_VARIABLE         = 123,
        SO_ACTOR_IGNORE_TURNS_ON  = 124,
        SO_ACTOR_IGNORE_TURNS_OFF = 125,
        SO_NEW                    = 126, // SO_ACTOR_NEW
        SO_ACTOR_DEPTH            = 127,
        SO_ACTOR_STOP             = 128,
        SO_ACTOR_FACE             = 129,
        SO_ACTOR_TURN             = 130,
        SO_ACTOR_WALK_SCRIPT      = 131,
        SO_ACTOR_TALK_SCRIPT      = 132,
        SO_ACTOR_WALK_PAUSE       = 133,
        SO_ACTOR_WALK_RESUME      = 134,
        SO_ACTOR_VOLUME           = 135,
        SO_ACTOR_FREQUENCY        = 136,
        SO_ACTOR_PAN              = 137,

        SO_VERB_INIT    = 150,
        SO_VERB_NEW     = 151,
        SO_VERB_DELETE  = 152,
        SO_VERB_NAME    = 153,
        SO_VERB_AT      = 154,
        SO_VERB_ON      = 155,
        SO_VERB_OFF     = 156,
        SO_VERB_COLOR   = 157,
        SO_VERB_HICOLOR = 158,

        SO_VERB_DIMCOLOR     = 160,
        SO_VERB_DIM          = 161,
        SO_VERB_KEY          = 162,
        SO_VERB_IMAGE        = 163,
        SO_VERB_NAME_STR     = 164,
        SO_VERB_CENTER       = 165,
        SO_VERB_CHARSET      = 166,
        SO_VERB_LINE_SPACING = 167,

        SO_SAVE_VERBS    = 180, // SO_VERBS_SAVE
        SO_RESTORE_VERBS = 181, // SO_VERBS_RESTORE
        SO_DELETE_VERBS  = 182, // SO_VERBS_DELETE

        SO_BASEOP        = 200, // SO_PRINT_BASEOP
        SO_END           = 201, // SO_PRINT_END
        SO_AT            = 202, // SO_PRINT_AT
        SO_COLOR         = 203, // SO_PRINT_COLOR
        SO_CENTER        = 204, // SO_PRINT_CENTER
        SO_PRINT_CHARSET = 205,
        SO_LEFT          = 206, // SO_PRINT_LEFT
        SO_OVERHEAD      = 207, // SO_PRINT_OVERHEAD
        SO_MUMBLE        = 208, // SO_PRINT_MUMBLE
        SO_PRINT_STRING  = 209,
        SO_PRINT_WRAP    = 210,

        SO_CURSOR_ON          = 220,
        SO_CURSOR_OFF         = 221,
        SO_CURSOR_SOFT_ON     = 222,
        SO_CURSOR_SOFT_OFF    = 223,
        SO_USERPUT_ON         = 224,
        SO_USERPUT_OFF        = 225,
        SO_USERPUT_SOFT_ON    = 226,
        SO_USERPUT_SOFT_OFF   = 227,
        SO_CURSOR_IMAGE       = 228,
        SO_CURSOR_HOTSPOT     = 229,
        SO_CURSOR_TRANSPARENT = 230,
        SO_CHARSET_SET        = 231,
        SO_CHARSET_COLOR      = 232,
        SO_CURSOR_PUT         = 233
    }
    
    public static readonly Dictionary<byte, string> OpcodeNames = new()
    {
        {O6_PushWord             , "O6_PushWord" },
        {O6_PushWordVar          , "O6_PushWordVar" },
        {O6_WordArrayRead        , "O6_WordArrayRead" },
        {O6_WordArrayIndexedRead , "O6_WordArrayIndexedRead" },
        {O6_Dup                  , "O6_Dup" },
        {O6_Pop                  , "O6_Pop" },
        {O6_Not                  , "O6_Not" },
        {O6_Eq                   , "O6_Eq" },
        {O6_Neq                  , "O6_Neq" },
        {O6_Gt                   , "O6_Gt" },
        {O6_Lt                   , "O6_Lt" },
        {O6_Le                   , "O6_Le" },
        {O6_Ge                   , "O6_Ge" },
        {O6_Add                  , "O6_Add" },
        {O6_Sub                  , "O6_Sub" },
        {O6_Mul                  , "O6_Mul" },
        {O6_Div                  , "O6_Div" },
        {O6_Land                 , "O6_Land" },
        {O6_Lor                  , "O6_Lor" },
        {O6_Band                 , "O6_Band" },
        {O6_Bor                  , "O6_Bor" },
        {O8_Mod                  , "O8_Mod" },
        {O6_If                   , "O6_If" },
        {O6_IfNot                , "O6_IfNot" },
        {O6_Jump                 , "O6_Jump" },
        {O6_BreakHere            , "O6_BreakHere" },
        {O6_DelayFrames          , "O6_DelayFrames" },
        {O8_Wait                 , "O8_Wait" },
        {O6_Delay                , "O6_Delay" },
        {O6_DelaySeconds         , "O6_DelaySeconds" },
        {O6_DelayMinutes         , "O6_DelayMinutes" },
        {O6_WriteWordVar         , "O6_WriteWordVar" },
        {O6_WordVarInc           , "O6_WordVarInc" },
        {O6_WordVarDec           , "O6_WordVarDec" },
        {O8_DimArray             , "O8_DimArray" },
        {O6_WordArrayWrite       , "O6_WordArrayWrite" },
        {O6_WordArrayInc         , "O6_WordArrayInc" },
        {O6_WordArrayDec         , "O6_WordArrayDec" },
        {O8_Dim2dimArray         , "O8_Dim2dimArray" },
        {O6_WordArrayIndexedWrite, "O6_WordArrayIndexedWrite" },
        {O8_ArrayOps             , "O8_ArrayOps" },
        {O6_StartScript          , "O6_StartScript" },
        {O6_StartScriptQuick     , "O6_StartScriptQuick" },
        {O6_StopObjectCode       , "O6_StopObjectCode" },
        {O6_StopScript           , "O6_StopScript" },
        {O6_JumpToScript         , "O6_JumpToScript" },
        {O6_Dummy                , "O6_Dummy" },
        {O6_StartObject          , "O6_StartObject" },
        {O6_StopObjectScript     , "O6_StopObjectScript" },
        {O6_Cutscene             , "O6_Cutscene" },
        {O6_EndCutscene          , "O6_EndCutscene" },
        {O6_FreezeUnfreeze       , "O6_FreezeUnfreeze" },
        {O6_BeginOverride        , "O6_BeginOverride" },
        {O6_EndOverride          , "O6_EndOverride" },
        {O6_StopSentence         , "O6_StopSentence" },
        {O6_SetClass             , "O6_SetClass" },
        {O6_SetState             , "O6_SetState" },
        {O6_SetOwner             , "O6_SetOwner" },
        {O6_PanCameraTo          , "O6_PanCameraTo" },
        {O6_ActorFollowCamera    , "O6_ActorFollowCamera" },
        {O6_SetCameraAt          , "O6_SetCameraAt" },
        {O6_PrintActor           , "O6_PrintActor" },
        {O6_PrintEgo             , "O6_PrintEgo" },
        {O6_TalkActor            , "O6_TalkActor" },
        {O6_TalkEgo              , "O6_TalkEgo" },
        {O6_PrintLine            , "O6_PrintLine" },
        {O6_PrintText            , "O6_PrintText" },
        {O6_PrintDebug           , "O6_PrintDebug" },
        {O6_PrintSystem          , "O6_PrintSystem" },
        {O8_BlastText            , "O8_BlastText" },
        {O8_DrawObject           , "O8_DrawObject" },
        {O8_CursorCommand        , "O8_CursorCommand" },
        {O6_LoadRoom             , "O6_LoadRoom" },
        {O6_LoadRoomWithEgo      , "O6_LoadRoomWithEgo" },
        {O6_WalkActorToObj       , "O6_WalkActorToObj" },
        {O6_WalkActorTo          , "O6_WalkActorTo" },
        {O6_PutActorAtXY         , "O6_PutActorAtXY" },
        {O6_PutActorAtObject     , "O6_PutActorAtObject" },
        {O6_FaceActor            , "O6_FaceActor" },
        {O6_AnimateActor         , "O6_AnimateActor" },
        {O6_DoSentence           , "O6_DoSentence" },
        {O6_PickupObject         , "O6_PickupObject" },
        {O6_SetBoxFlags          , "O6_SetBoxFlags" },
        {O6_CreateBoxMatrix      , "O6_CreateBoxMatrix" },
        {O8_ResourceRoutines     , "O8_ResourceRoutines" },
        {O8_RoomOps              , "O8_RoomOps" },
        {O8_ActorOps             , "O8_ActorOps" },
        {O8_CameraOps            , "O8_CameraOps" },
        {O8_VerbOps              , "O8_VerbOps" },
        {O6_StartSound           , "O6_StartSound" },
        {O6_StartMusic           , "O6_StartMusic" },
        {O6_StopSound            , "O6_StopSound" },
        {O6_SoundKludge          , "O6_SoundKludge" },
        {O8_SystemOps            , "O8_SystemOps" },
        {O6_SaveRestoreVerbs     , "O6_SaveRestoreVerbs" },
        {O6_SetObjectName        , "O6_SetObjectName" },
        {O6_GetDateTime          , "O6_GetDateTime" },
        {O6_DrawBox              , "O6_DrawBox" },
        {O8_StartVideo           , "O8_StartVideo" },
        {O8_KernelSetFunctions   , "O8_KernelSetFunctions" },
        {O6_StartScriptQuick2    , "O6_StartScriptQuick2" },
        {O6_StartObjectQuick     , "O6_StartObjectQuick" },
        {O6_PickOneOf            , "O6_PickOneOf" },
        {O6_PickOneOfDefault     , "O6_PickOneOfDefault" },
        {O6_IsAnyOf              , "O6_IsAnyOf" },
        {O6_GetRandomNumber      , "O6_GetRandomNumber" },
        {O6_GetRandomNumberRange , "O6_GetRandomNumberRange" },
        {O6_IfClassOfIs          , "O6_IfClassOfIs" },
        {O6_GetState             , "O6_GetState" },
        {O6_GetOwner             , "O6_GetOwner" },
        {O6_IsScriptRunning      , "O6_IsScriptRunning" },
        {O6_IsSoundRunning       , "O6_IsSoundRunning" },
        {O6_Abs                  , "O6_Abs" },
        {O8_KernelGetFunctions   , "O8_KernelGetFunctions" },
        {O6_IsActorInBox         , "O6_IsActorInBox" },
        {O6_GetVerbEntrypoint    , "O6_GetVerbEntrypoint" },
        {O6_GetActorFromXY       , "O6_GetActorFromXY" },
        {O6_FindObject           , "O6_FindObject" },
        {O6_GetVerbFromXY        , "O6_GetVerbFromXY" },
        {O6_FindInventory        , "O6_FindInventory" },
        {O6_GetInventoryCount    , "O6_GetInventoryCount" },
        {O6_GetAnimateVariable   , "O6_GetAnimateVariable" },
        {O6_GetActorRoom         , "O6_GetActorRoom" },
        {O6_GetActorWalkBox      , "O6_GetActorWalkBox" },
        {O6_GetActorMoving       , "O6_GetActorMoving" },
        {O6_GetActorCostume      , "O6_GetActorCostume" },
        {O6_GetActorScaleX       , "O6_GetActorScaleX" },
        {O6_GetActorLayer        , "O6_GetActorLayer" },
        {O6_GetActorElevation    , "O6_GetActorElevation" },
        {O6_GetActorWidth        , "O6_GetActorWidth" },
        {O6_GetObjectNewDir      , "O6_GetObjectNewDir" },
        {O6_GetObjectX           , "O6_GetObjectX" },
        {O6_GetObjectY           , "O6_GetObjectY" },
        {O8_GetActorChore        , "O8_GetActorChore" },
        {O6_DistObjectObject     , "O6_DistObjectObject" },
        {O6_DistPtPt             , "O6_DistPtPt" },
        {O8_GetObjectImageX      , "O8_GetObjectImageX" },
        {O8_GetObjectImageY      , "O8_GetObjectImageY" },
        {O8_GetObjectImageWidth  , "O8_GetObjectImageWidth" },
        {O8_GetObjectImageHeight , "O8_GetObjectImageHeight" },
        {O8_GetStringWidth       , "O8_GetStringWidth" },
        {O8_GetActorZPlane       , "O8_GetActorZPlane" },
    };

    public static string ToString(this byte b)
    {
        if (OpcodeNames.TryGetValue(b, out string s))
            return s;
        else
        {
            // Log.Info($"Warning: Opcode 0x{b:X2} not implemented.");
            return $"[Unknown Opcode 0x{b:X2}]";
        }
    }
}