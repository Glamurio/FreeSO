using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Linq;

namespace FSO.Content
{
    /// <summary>
    /// Corrections to original object behaviour that cannot be made in the VM, applied to the loaded IFF in memory only
    /// (the game files are never modified). Switched on by Simitone's "QoL Fixes" setting.
    /// </summary>
    public static class ContentHotfixes
    {
        public static bool Enabled = false;

        /// <summary>
        /// Trash Pile, Poop, Dead Mouse, Dog Hole and others share the BHAV "inc trash amount": when the pile's amount
        /// (SOAttr[0]) goes over 10 it creates a new pile for the excess and is meant to cap itself at 10. The cap is
        /// written to SOAttr[1] (the graphic size) instead, so the amount stays over 10 and EVERY later increase creates
        /// another pile, each larger than the last. Pets "playing" in a pile add 2 each time, so a single pile multiplies.
        /// The same BHAV in trash1.iff / TrashCanUnleashed.iff writes SOAttr[0], which is the intended behaviour.
        /// CONFIRMED from the data; the original executable runs the same BHAV, so the original game most likely had this bug too.
        /// </summary>
        private static readonly byte[] CapWrongAttr = { 0x01, 0x00, 0x0a, 0x00, 0x00, 0x05, 0x01, 0x07 }; //SOAttr[1] := 10
        private static readonly byte[] SubtractTen = { 0x01, 0x00, 0x0a, 0x00, 0x00, 0x04, 0x08, 0x07 };   //Temp[1] -= 10
        private static readonly byte[] CapRightAttr = { 0x00, 0x00, 0x0a, 0x00, 0x00, 0x05, 0x01, 0x07 };  //SOAttr[0] := 10

        /// <summary>Patches one BHAV if it is the faulty "inc trash amount". Returns true if it changed.</summary>
        public static bool FixPileCap(BHAV bhav)
        {
            if (bhav?.Instructions == null || bhav.ChunkLabel == null) return false;
            if (!bhav.ChunkLabel.StartsWith("inc trash amount", StringComparison.OrdinalIgnoreCase)) return false;
            var ins = bhav.Instructions;
            for (int i = 1; i < ins.Length; i++)
            {
                //the faulty write directly follows "Temp[1] -= 10" (the excess that goes to the new pile).
                if (ins[i].Opcode == 2 && ins[i].Operand != null && ins[i].Operand.SequenceEqual(CapWrongAttr)
                    && ins.Any(x => x.Opcode == 2 && x.Operand != null && x.Operand.SequenceEqual(SubtractTen)))
                {
                    ins[i].Operand = (byte[])CapRightAttr.Clone();
                    return true;
                }
            }
            return false;
        }

        public static int Apply(IffFile iff)
        {
            if (!Enabled || iff == null) return 0;
            var count = 0;
            var bhavs = iff.List<BHAV>();
            if (bhavs == null) return 0;
            foreach (var b in bhavs) if (FixPileCap(b)) count++;
            return count;
        }
    }
}
