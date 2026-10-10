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

        /// <summary>
        /// Fireplace ignition. BHAV "burn something" (Fireplaces.iff 4112) runs once every 4 ticks while the fire is lit and,
        /// when random_number(Tuning[8]) comes up 0 (Tuning[8] = 750 in BCON 4096), burns every object with the Burns flag on
        /// the four tiles next to the fireplace in front of it. A fire lasts Tuning[9] * 450 = 1350 passes, so about 83% of
        /// fires ignite something, on average ~100 game minutes in, and usually the first thing next to it (a pet bed).
        /// There is no per-material flammability: only the single Burns flag (flag 6 of object data 40 = VMEntityFlags2.Burns).
        /// The roll is divided here by raising the range, so the ignition stays possible but is much rarer.
        /// </summary>
        public static int FireIgnitionDivisor = 10;
        private static readonly byte[] BurnRollHead = { 0x00, 0x00, 0x19, 0x00 };

        public static ushort ScaleIgnitionRange(ushort range, int divisor)
        {
            if (divisor <= 1) return range;
            var scaled = (long)range * divisor;
            return (ushort)Math.Min(scaled, short.MaxValue);
        }

        /// <summary>Raises the roll range of the fireplace "burn something" BHAV in its Tuning BCON. Returns true if changed.</summary>
        public static bool ReduceFireplaceIgnition(BHAV bhav, BCON tuning, int divisor)
        {
            if (bhav?.Instructions == null || tuning == null || bhav.ChunkLabel == null) return false;
            if (!bhav.ChunkLabel.Equals("burn something", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var ins in bhav.Instructions)
            {
                var o = ins.Operand;
                //random_number(Local[0], Tuning[n]); the roll is the one in the BHAV that reads a BCON constant (scope 26)
                if (ins.Opcode != 8 || o == null || o.Length < 8) continue;
                if (!o.Take(4).SequenceEqual(BurnRollHead) || o[6] != 26 || o[7] != 0) continue;
                var index = o[4] | (o[5] << 8);
                if ((index >> 7) != tuning.ChunkID - 4096 || (index & 0x7F) >= tuning.Constants.Length) return false;
                var i = index & 0x7F;
                var scaled = ScaleIgnitionRange(tuning.Constants[i], divisor);
                if (scaled == tuning.Constants[i]) return false;
                tuning.Constants[i] = scaled;
                return true;
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
            var bcons = iff.List<BCON>();
            if (bcons != null)
            {
                foreach (var b in bhavs)
                {
                    if (b.ChunkLabel == null || !b.ChunkLabel.Equals("burn something", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var c in bcons) if (ReduceFireplaceIgnition(b, c, FireIgnitionDivisor)) { count++; break; }
                }
            }
            return count;
        }
    }
}
