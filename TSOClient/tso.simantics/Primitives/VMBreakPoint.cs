using FSO.SimAntics.Engine;
using FSO.Files.Utils;
using FSO.SimAntics.Engine.Scopes;
using System.IO;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Primitives
{
    // Generally something bad has happened when this is called.

    public class VMBreakPoint : VMPrimitiveHandler
    {
        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            //todo: check condition
            var instr = context.GetCurrentInstruction();
            if (instr.TruePointer == context.InstructionPointer)
            {
                //Authored trap ("this should never happen"): it jumps to itself, so it would spin until the thread is
                //killed for running >500000 primitives. Fail now, and say what the previous primitive reported
                //(the rabbit's "Setup for Move" ends up here when a Snap fails).
                var caller = context.Caller;
                throw new VMSimanticsException("Breakpoint trap reached (self-looping breakpoint). Last primitive result "
                    + caller.GetValue(VMStackObjectVariable.PrimitiveResult) + ", blocker object "
                    + caller.GetValue(VMStackObjectVariable.PrimitiveResultID) + ", position " + caller.Position
                    + ". Last snap failure: " + (VMSnap.LastFailure ?? "(none)"), context);
            }
            return VMPrimitiveExitCode.GOTO_TRUE;
        }
    }

    public class VMBreakPointOperand : VMPrimitiveOperand
    {
        public short Data { get; set; }
        public VMVariableScope Scope { get; set; }

        #region VMPrimitiveOperand Members
        public void Read(byte[] bytes)
        {
            using (var io = IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN))
            {
                Data = io.ReadInt16();
                Scope = (VMVariableScope)io.ReadUInt16();
            }
        }

        public void Write(byte[] bytes)
        {
            using (var io = new BinaryWriter(new MemoryStream(bytes)))
            {
                io.Write(Data);
                io.Write((ushort)Scope);
            }
        }
        #endregion
    }
}
