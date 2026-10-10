using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;
using System;
using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetSetRoofCmd : VMNetCommandBodyAbstract
    {
        public float Pitch;
        public uint Style;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1 && (caller == null || caller.AvatarState.Permissions < VMTSOAvatarPermissions.Owner)) return false;
            if (Style >= Content.Content.Get().WorldRoofs.Count) return false;
            Pitch = Math.Max(0f, Math.Min(1.25f, Pitch));
            var arch = vm.Context.Architecture;
            var oldPitch = arch.RoofPitch;
            var oldStyle = arch.RoofStyle;
            arch.SetRoof(Pitch, Style);
            if ((oldPitch != Pitch || oldStyle != Style) && vm.OnArchitectureChange != null)
                vm.OnArchitectureChange(new VMArchitectureChange()
                    { Roof = true, OldPitch = oldPitch, NewPitch = Pitch, OldStyle = oldStyle, NewStyle = Style, ActorUID = ActorUID });
            return true;
        }

        #region VMSerializable Members

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Pitch);
            writer.Write(Style);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Pitch = reader.ReadSingle();
            Style = reader.ReadUInt32();
        }

        #endregion
    }
}
