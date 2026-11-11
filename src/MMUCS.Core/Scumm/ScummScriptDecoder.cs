using System;
using System.Collections.Generic;
using System.Text;
using MMUCS.Core.Diagnostics;
using MMUCS.Core.Imaging;

namespace MMUCS.Core.Scumm;


public class ScummScriptDecoder
{
    public struct DisassembledInstruction
    {
        public int Address; // Relative block address offset
        public byte Opcode; // Raw instruction identifier
        public string Name; // e.g., "o8_actorOps" or "o6_pushWord"
        public List<int> Arguments;
        public string DecodedString;
    }
    
    private static readonly Dictionary<byte, string> OpcodeNames = new()
    {
        { 0x01, "pushWord" },       { 0x02, "pushWordVar" },
        { 0x06, "pop" },            { 0x64, "if" },
        { 0x66, "jump" },           { 0x79, "startScript" },
        { 0x91, "talkActor" },      { 0xA1, "putActorAtXY" },
        { 0xAC, "actorOps" },       { 0xAD, "cameraOps" },
        { 0xAE, "verbOps" },        { 0xAF, "startSound" }
    };

    public List<DisassembledInstruction> Disassemble(ScummBlock scriptBlock)
    {
        var instructions = new List<DisassembledInstruction>();
        var span = scriptBlock.DataSpan;
        int pos = 0;

        var valueStack = new Stack<int>();

        while (pos < span.Length)
        {
            int currentAddr = pos;
            byte opcode = span[pos++];

            var instr = new DisassembledInstruction
            {
                Address = currentAddr,
                Opcode = opcode,
                Name = OpcodeNames.TryGetValue(opcode, out var name) ? name : $"OP_0x{opcode:X2}",
                Arguments = new List<int>()
            };

            switch (opcode)
            {
                case 0x01: // o6_pushWord (Expects an immediate 16-bit word parameter value)
                    if (pos + 2 <= span.Length)
                    {
                        short wordVal = span.S16LE(pos);
                        pos += 2;
                        instr.Arguments.Add(wordVal);
                        valueStack.Push(wordVal); // Log onto value stack track
                        instr.DecodedString = $"PUSH {wordVal}";
                    }
                    break;

                case 0x02: // o6_pushWordVar
                    if (pos + 2 <= span.Length)
                    {
                        ushort varIdx = span.U16LE(pos);
                        pos += 2;
                        instr.Arguments.Add(varIdx);
                        instr.DecodedString = $"PUSH_VAR [Var_{varIdx}]";
                    }
                    break;

                case 0xAC: // o8_actorOps
                    // This opcode pops target ID elements from stack and fetches a sub-opcode byte!
                    if (pos < span.Length)
                    {
                        byte subOpByte = span[pos++];
                        instr.Arguments.Add(subOpByte);
                        
                        string subOpName = Enum.IsDefined(typeof(ScummEngineV8.SubOpType), subOpByte) 
                            ? ((ScummEngineV8.SubOpType)subOpByte).ToString() 
                            : $"SUBOP_{subOpByte}";

                        // Pull tracking parameters off historical value stack if populated
                        int actorId = valueStack.Count > 0 ? valueStack.Pop() : -1;
                        int parameterVal = valueStack.Count > 0 ? valueStack.Pop() : -1;

                        instr.DecodedString = $"actorOps::{subOpName} (Actor: {actorId}, Param: {parameterVal})";
                    }
                    break;

                case 0x66: // o6_jump
                    if (pos + 2 <= span.Length)
                    {
                        short offset = span.S16LE(pos);
                        pos += 2;
                        int targetAddr = currentAddr + offset;
                        instr.Arguments.Add(targetAddr);
                        instr.DecodedString = $"JUMP to 0x{targetAddr:X4}";
                    }
                    break;

                default:
                    instr.DecodedString = $"{instr.Name}";
                    break;
            }

            instructions.Add(instr);
        }

        return instructions;
    }
}