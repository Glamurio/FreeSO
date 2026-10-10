using FSO.Files.Utils;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FSO.SimAntics.Primitives
{
    public class VMBurn : VMPrimitiveHandler
    {
        public static uint FIRE_GUID = 0x24C95F99; //probably different for ts1

        /// <summary>
        /// The tile in front of an object. An object that is off the lot (OUT_OF_WORLD: a Sim at work, a hidden pet) has no
        /// front: offsetting its position would produce a nearby coordinate that no longer equals OUT_OF_WORLD, so the fire
        /// would be created off the lot (riperiperi/Simitone#24).
        /// </summary>
        public static LotTilePos FrontOf(LotTilePos pos, FSO.LotView.Model.Direction direction)
        {
            if (pos == LotTilePos.OUT_OF_WORLD) return pos;
            var front = new LotTilePos(pos);
            switch (direction)
            {
                case FSO.LotView.Model.Direction.SOUTH: front.y += 16; break;
                case FSO.LotView.Model.Direction.WEST: front.x -= 16; break;
                case FSO.LotView.Model.Direction.EAST: front.x += 16; break;
                case FSO.LotView.Model.Direction.NORTH: front.y -= 16; break;
            }
            return front;
        }

        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            var operand = (VMBurnOperand)args;
            if (!context.VM.TS1 && (context.VM.Tuning.GetTuning("special", 0, 0) != 1f))
                return VMPrimitiveExitCode.GOTO_FALSE; //fire disabled for now

            bool isSpread = operand.Type != VMBurnType.StackObject;

            if (isSpread && (int)context.VM.Context.NextRandom(10000) >= context.VM.Context.Clock.FirePercent)
            {
                return VMPrimitiveExitCode.GOTO_FALSE;
            }

            var myRoom = context.VM.Context.GetObjectRoom(context.Caller);
            if (context.VM.Context.RoomInfo[myRoom].Room.IsPool) return VMPrimitiveExitCode.GOTO_FALSE;

            //as fires burn, the chance they can spread lowers dramatically.
            if (isSpread) context.VM.Context.Clock.FirePercent -= 500; // lower by 5% every spread. 40 spreads reaches 0.
            if (context.VM.Context.Clock.FirePercent < 0) context.VM.Context.Clock.FirePercent = 0;

            //begin the burn setup. 
            var arch = context.VM.Context.Architecture;
            var query = context.VM.Context.ObjectQueries;

            bool[] fires = new bool[arch.Width * arch.Height];
            var spread = new Queue<LotTilePos>();

            var pos = operand.Type == VMBurnType.FrontOfStackObject
                ? FrontOf(context.StackObject.Position, context.StackObject.Direction)
                : context.StackObject.Position;
            if (pos == LotTilePos.OUT_OF_WORLD) return VMPrimitiveExitCode.GOTO_FALSE;

            bool first = true;
            bool madeAFire = false;
            
            spread.Enqueue(pos);

            while (spread.Count > 0)
            {
                var item = spread.Dequeue();
                if (item == LotTilePos.OUT_OF_WORLD) continue;

                var objat = query.GetObjectsAt(item) ?? new List<VMEntity>();

                if (first && !(objat?.Any(x => x.Object.OBJ.GUID == FIRE_GUID) ?? false))
                {
                    madeAFire = true;
                    var fire = context.VM.Context.CreateObjectInstance(FIRE_GUID, LotTilePos.FromBigTile(pos.TileX, pos.TileY, pos.Level), Direction.NORTH);
                }
                first = false;

                foreach (var obj in objat)
                {
                    var inUse = obj.IsInUse(context.VM.Context, true) || (obj is VMAvatar && ((VMAvatar)obj).GetPersonData(Model.VMPersonDataVariable.IsGhost) > 0);
                    if (inUse) continue;
                    foreach (var sobj in obj.MultitileGroup.Objects)
                    {
                        //attempt to add fire for this element.
                        //for each tile add a spread.

                        //is this object burnable? if so, set it as burning
                        if (sobj.Position == LotTilePos.OUT_OF_WORLD || sobj.Position.Level != pos.Level) continue;

                        if ((((VMEntityFlags2)sobj.GetValue(Model.VMStackObjectVariable.FlagField2) & VMEntityFlags2.Burns) > 0) && !sobj.GetFlag(VMEntityFlags.FireProof))
                        {
                            sobj.SetFlag(VMEntityFlags.Burning, true);
                            var fpos = sobj.Position;
                            var index = fpos.TileX + fpos.TileY * arch.Width;

                            if (fires[index] || (query.GetObjectsAt(fpos)?.Any(x => x.Object.OBJ.GUID == FIRE_GUID) ?? false))
                            {
                                fires[index] = true;
                                continue;
                            }
                            spread.Enqueue(fpos);

                            madeAFire = true;
                            var fire = context.VM.Context.CreateObjectInstance(FIRE_GUID, LotTilePos.FromBigTile(fpos.TileX, fpos.TileY, fpos.Level), Direction.NORTH);
                            fires[index] = true;
                        }
                    }
                }
            }

            return madeAFire?VMPrimitiveExitCode.GOTO_TRUE:VMPrimitiveExitCode.GOTO_FALSE;
        }
    }

    public class VMBurnOperand : VMPrimitiveOperand
    {
        public VMBurnType Type { get; set; }
        public bool BurnBusyObjects { get; set; }

        #region VMPrimitiveOperand Members
        public void Read(byte[] bytes)
        {
            using (var io = IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN))
            {
                Type = (VMBurnType)io.ReadByte();
                BurnBusyObjects = io.ReadByte() > 0;
            }
        }

        public void Write(byte[] bytes)
        {
            using (var io = new BinaryWriter(new MemoryStream(bytes)))
            {
                io.Write((byte)Type);
                io.Write((byte)(BurnBusyObjects?1:0));
            }
        }
        #endregion
    }

    public enum VMBurnType : byte
    {
        StackObject = 0,
        FrontOfStackObject = 1,
        FloorsUnderStackObject = 2
    }
}
