using System;
using System.Collections.Generic;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// All tiles of one deciduous tree, for the leafless winter look (BareBranches). The branch skeleton has to see the whole tree,
    /// but the engine draws every tile as its own object, so this collects the tiles of a multi-tile tree (the OBJDs of its iff
    /// with the same MasterID) and builds one skeleton per view direction, shared by all tiles of that tree type.
    ///
    /// Where the tiles' sprites sit relative to each other (CONFIRMED against the sprite data: the pieces of every multi-tile
    /// tree tested join up with no overlap only with this placement): a tile at sub index (sx, sy) = ((sub >> 8) &amp; 0xFF, sub &amp; 0xFF)
    /// in an nx-by-ny footprint, drawn with DGRP image direction 1/4/16/64, is rotated k = 0/1/2/3 times as
    /// (sx, sy, nx, ny) -> (ny - 1 - sy, sx, ny, nx), and lies at ((sx - sy) * tw / 2, (sx + sy) * tw / 4) with tile width tw
    /// 128/64/32 at zoom 3/2/1. A sprite frame's top-left is that plus the DGRP sprite offset, minus the frame height in y
    /// (the engine anchors sprites at the bottom, see DGRPRenderer.AddItem).
    /// </summary>
    public sealed class BareTreeGroup
    {
        private readonly OBJD[] Tiles;
        private readonly DGRP[] Groups;
        private readonly int NX, NY;
        private readonly FoliageLeaf Leaf;
        private readonly bool Fruit;
        private readonly Dictionary<uint, object> Built = new Dictionary<uint, object>(); //zoom 3 skeletons by direction
        private readonly Dictionary<long, object> Zoomed = new Dictionary<long, object>();

        public static int Skeletons, Failures;
        private static readonly Dictionary<IffFile, Dictionary<int, BareTreeGroup>> Cache = new Dictionary<IffFile, Dictionary<int, BareTreeGroup>>();

        private BareTreeGroup(OBJD[] tiles, DGRP[] groups, FoliageLeaf leaf, bool fruit)
        {
            Tiles = tiles; Groups = groups; Leaf = leaf; Fruit = fruit;
            foreach (var t in tiles)
            {
                int sub = Math.Max((short)0, t.SubIndex);
                NX = Math.Max(NX, ((sub >> 8) & 0xFF) + 1);
                NY = Math.Max(NY, (sub & 0xFF) + 1);
            }
            foreach (var g in groups) g?.KeepSpritesEncoded();
        }

        /// <summary>The group of the tree this tile belongs to. getDgrp resolves a DGRP id in the object's resource.</summary>
        public static BareTreeGroup For(OBJD tile, Func<ushort, DGRP> getDgrp, FoliageLeaf leaf, bool fruit)
        {
            if (tile == null || getDgrp == null) return null;
            var iff = tile.ChunkParent;
            bool multi = tile.MasterID != 0 && tile.SubIndex >= 0 && iff != null;
            int key = multi ? tile.MasterID : 0x10000 + tile.ChunkID;
            lock (Cache)
            {
                Dictionary<int, BareTreeGroup> byIff = null;
                if (iff != null)
                {
                    if (!Cache.TryGetValue(iff, out byIff)) Cache[iff] = byIff = new Dictionary<int, BareTreeGroup>();
                    if (byIff.TryGetValue(key, out var found)) return found;
                }
                var tiles = new List<OBJD>();
                if (multi)
                {
                    foreach (var o in iff.List<OBJD>())
                        if (o.MasterID == tile.MasterID && o.SubIndex >= 0 && o.BaseGraphicID != 0) tiles.Add(o);
                }
                if (tiles.Count == 0) tiles.Add(tile);
                var groups = new DGRP[tiles.Count];
                for (int i = 0; i < tiles.Count; i++) groups[i] = getDgrp(tiles[i].BaseGraphicID);
                var group = new BareTreeGroup(tiles.ToArray(), groups, leaf, fruit);
                if (byIff != null) byIff[key] = group;
                return group;
            }
        }

        private static int TileWidth(int zoom) { return zoom >= 3 ? 128 : zoom == 2 ? 64 : 32; }

        private void TileOrigin(OBJD tile, uint direction, int zoom, out int x, out int y)
        {
            int sub = Math.Max((short)0, tile.SubIndex);
            int sx = (sub >> 8) & 0xFF, sy = sub & 0xFF, nx = NX, ny = NY;
            int k = direction == 4 ? 1 : direction == 16 ? 2 : direction == 64 ? 3 : 0;
            for (int i = 0; i < k; i++)
            {
                int nsx = ny - 1 - sy, nsy = sx;
                sx = nsx; sy = nsy;
                int t = nx; nx = ny; ny = t;
            }
            int tw = TileWidth(zoom);
            x = (sx - sy) * tw / 2;
            y = (sx + sy) * tw / 4;
        }

        /// <summary>Top-left of a sprite frame of one of this tree's tiles in the tree's shared pixel frame.</summary>
        public void FramePosition(OBJD tile, DGRPSprite sprite, int frameHeight, uint direction, int zoom, out int x, out int y)
        {
            TileOrigin(tile, direction, zoom, out x, out y);
            x += (int)sprite.SpriteOffset.X;
            y += (int)sprite.SpriteOffset.Y - frameHeight;
        }

        /// <summary>The skeleton for frames drawn with this image direction at this zoom (3 near .. 1 far); built from the zoom 3 art.</summary>
        public object SkeletonFor(uint direction, int zoom)
        {
            lock (this)
            {
                long key = ((long)direction << 8) | (uint)zoom;
                if (Zoomed.TryGetValue(key, out var found)) return found;
                if (!Built.TryGetValue(direction, out var sk))
                {
                    sk = Build(direction);
                    Built[direction] = sk;
                }
                int scale = zoom >= 3 ? 1 : zoom == 2 ? 2 : 4;
                var result = scale == 1 ? sk : BareBranches.ForZoom(sk, scale);
                Zoomed[key] = result;
                return result;
            }
        }

        private object Build(uint direction)
        {
            var tiles = new List<BareBranches.BareTile>();
            for (int i = 0; i < Tiles.Length; i++)
            {
                var group = Groups[i];
                if (group?.Images == null) continue;
                foreach (var image in group.Images)
                {
                    if (image.Direction != direction || image.Zoom != 3) continue;
                    foreach (var sprite in image.Sprites)
                    {
                        var frame = sprite?.GetFrame();
                        if (frame == null || frame.Width <= 0 || frame.Height <= 0) continue;
                        var px = frame.GetPixelsForRecolor();
                        if (px == null || px.Length < frame.Width * frame.Height) { Failures++; continue; }
                        var rgba = new byte[frame.Width * frame.Height * 4];
                        for (int p = 0; p < frame.Width * frame.Height; p++)
                        {
                            rgba[p * 4] = px[p].R; rgba[p * 4 + 1] = px[p].G; rgba[p * 4 + 2] = px[p].B; rgba[p * 4 + 3] = px[p].A;
                        }
                        FramePosition(Tiles[i], sprite, frame.Height, direction, 3, out int x, out int y);
                        tiles.Add(new BareBranches.BareTile { Rgba = rgba, W = frame.Width, H = frame.Height, X = x, Y = y, Flipped = sprite.Flip });
                    }
                }
            }
            Skeletons++;
            return BareBranches.BuildSkeleton(tiles, 1, Leaf, Fruit);
        }
    }
}
