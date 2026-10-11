using FSO.Files.Formats.IFF.Chunks;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Finds the leaf litter for autumn trees. The Spellbound pack's "Depressed Deciduous" (EP7, TreesSpellbound.iff) is drawn with
    /// fallen leaves around its foot, baked into each of its four tile sprites. Those sprites sit on the same 2x2 footprint as
    /// the other big trees, so the litter of the matching tile is drawn under a tree tile too.
    /// </summary>
    public static class LeafLitter
    {
        //Depressed Deciduous tile objects: (x, y) in the 2x2 footprint, by sub-index 0 / 256 / 1 / 257
        private const uint Rear = 0x16AE22EE, Right = 0x335E4B40, Left = 0xF89E2350, Front = 0xE049759A;
        private static readonly uint[] Tiles = { Rear, Right, Left, Front };
        private static readonly DGRP[] Cache = new DGRP[4];
        private static readonly bool[] Tried = new bool[4];

        /// <summary>The Depressed Deciduous DGRP for the footprint tile of this object, or null if the pack is not installed.</summary>
        public static DGRP ReferenceFor(OBJD tile)
        {
            if (tile == null) return null;
            int sub = tile.SubIndex;
            int sx = tile.MasterID == 0 || sub < 0 ? 0 : (sub >> 8) & 1;
            int sy = tile.MasterID == 0 || sub < 0 ? 0 : sub & 1;
            int slot = sx + sy * 2; //0 rear, 1 right, 2 left, 3 front
            if (Tried[slot]) return Cache[slot];
            Tried[slot] = true;
            var def = FSO.Content.Content.Get().WorldObjects.Get(Tiles[slot]);
            if (def?.OBJ == null || def.OBJ.BaseGraphicID == 0) return null;
            var dgrp = def.Resource.Get<DGRP>(def.OBJ.BaseGraphicID);
            //the sprites are decoded on demand and the cache would drop the encoded data, so keep it (as for the trees themselves)
            dgrp?.KeepSpritesEncoded();
            Cache[slot] = dgrp;
            return dgrp;
        }
    }
}
