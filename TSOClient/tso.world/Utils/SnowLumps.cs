using System;
using System.Collections.Generic;
using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Winter snow for evergreens that have no snowy graphic of their own. NEW behaviour (seasons), not in the original game.
    ///
    /// The snow is not drawn by us: it is cut out of the game's own snowy sprites (the Vacation pack's snowy Fir, Pine and
    /// Spruce in Trees4.iff, and the Snowy Tumbleweed and Snowy Cypress in Shrubs3.iff) as separate lumps, and those lumps are
    /// set onto the upward-facing parts of other plants. So every snow pixel is one the game's artists rendered.
    ///
    /// Where a lump goes: on top edges of the silhouette (open sky above) and on surfaces that face up. In the sprites' z-buffer
    /// a horizontal surface recedes as it goes up the screen: z grows by about 2.5 per pixel on a hedge's top face and falls by
    /// about 0.5 per pixel on its sides (CONFIRMED on Shrubs.iff hedges), so "z grows upwards" picks out the tops.
    ///
    /// Lumps stick up above their branch, so the sprite gets extra transparent rows on top (the engine anchors a sprite at its
    /// bottom: top = baseline - height + offset, so added rows on top leave the rest where it was) and its own z-buffer: new
    /// pixels get the depth of the branch they rest on.
    ///
    /// Python prototype with the same steps and constants: /tmp/claude-0/snow/transplant.py (analysis, not in the repo).
    /// </summary>
    public static class SnowLumps
    {
        public sealed class Lump
        {
            public int W, H, Area;
            /// <summary>Order in which the lump was cut, the last sort key (keeps equal-sized lumps in a fixed order).</summary>
            public int Order;
            public Color[] Px; //W*H, A = 0 outside the lump
        }

        //the tiles of the Vacation pack's snowy trees and shrubs; their snowy graphic is the next DGRP after the normal one
        private static readonly uint[] TreeSources =
        {
            0x3026C759, 0x3026EA3C, 0x3026E8FE, 0x3026B863, //Western Slope Fir
            0x3007ED15, 0x3007C49E, 0x30078DCF, 0x3007A790, //Scenic Pine
            0x23F3DD6C, 0x03CDE481, 0xF140D974, 0xC15387AD  //Goose Spruce
        };
        private static readonly uint[] ShrubSources = { 0x72BDBB7D, 0xAED3C88A }; //New Tumbleweed, Polymer Cypress Bush

        /// <summary>Share of a plant's pixels the lumps should cover, in percent (trees are snowier: they hold more on their tiers).</summary>
        public const int TreeCover = 62, ShrubCover = 42;
        /// <summary>Lumps are picked from the larger part of the library that fits (percent of the fitting lumps skipped).</summary>
        public const int SizeBias = 50;
        /// <summary>Minimum distance between lump centres, in percent of their mean width (80% of that on top surfaces).</summary>
        public const int Spacing = 110;
        private const int CoreLuminance = 205;

        private static readonly Dictionary<int, List<Lump>> Libraries = new Dictionary<int, List<Lump>>();
        public static int LibraryFrames, LibraryFailures;

        private static uint Hash(int x, int y, int salt)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(salt * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }

        private static int Lum(Color c) { return (299 * c.R + 587 * c.G + 114 * c.B) / 1000; }

        private static bool IsSnow(Color c)
        {
            if (c.A < 128) return false;
            int mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B));
            return Lum(c) >= 120 && mx - mn <= 40 && c.B >= c.G - 12;
        }

        /// <summary>
        /// The lumps of one snowy sprite: bright cores (the sunlit tops) are found first, then each grows through the rest of the
        /// snow, so neighbouring lumps split along their shaded undersides. Lumps cut by the frame edge, small, hollow or tall
        /// ones are dropped.
        /// </summary>
        public static List<Lump> CutLumps(Color[] px, int w, int h, int minArea)
        {
            var result = new List<Lump>();
            if (px == null || w <= 0 || h <= 0 || px.Length < w * h) return result;
            int n = w * h;
            var snow = new bool[n];
            for (int i = 0; i < n; i++) snow[i] = IsSnow(px[i]);
            var lab = new int[n];
            for (int i = 0; i < n; i++) lab[i] = -1;
            int cores = 0;
            var q = new Queue<int>();
            for (int s = 0; s < n; s++)
            {
                if (!snow[s] || lab[s] >= 0 || Lum(px[s]) < CoreLuminance) continue;
                q.Enqueue(s); lab[s] = cores;
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); int cx = c % w, cy = c / w;
                    for (int d = 0; d < 4; d++)
                    {
                        int X = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), Y = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (X < 0 || X >= w || Y < 0 || Y >= h) continue;
                        int j = Y * w + X;
                        if (snow[j] && lab[j] < 0 && Lum(px[j]) >= CoreLuminance) { lab[j] = cores; q.Enqueue(j); }
                    }
                }
                cores++;
            }
            for (int i = 0; i < n; i++) if (lab[i] >= 0) q.Enqueue(i);
            while (q.Count > 0)
            {
                int c = q.Dequeue(); int cx = c % w, cy = c / w;
                for (int d = 0; d < 4; d++)
                {
                    //down first, then right, left, up (same order as the prototype)
                    int X = cx + (d == 1 ? 1 : d == 2 ? -1 : 0), Y = cy + (d == 0 ? 1 : d == 3 ? -1 : 0);
                    if (X < 0 || X >= w || Y < 0 || Y >= h) continue;
                    int j = Y * w + X;
                    if (snow[j] && lab[j] < 0) { lab[j] = lab[c]; q.Enqueue(j); }
                }
            }
            if (cores == 0) return result;
            var x0 = new int[cores]; var y0 = new int[cores]; var x1 = new int[cores]; var y1 = new int[cores]; var area = new int[cores];
            for (int k = 0; k < cores; k++) { x0[k] = int.MaxValue; y0[k] = int.MaxValue; x1[k] = -1; y1[k] = -1; }
            for (int i = 0; i < n; i++)
            {
                int k = lab[i]; if (k < 0) continue;
                int x = i % w, y = i / w;
                if (x < x0[k]) x0[k] = x; if (y < y0[k]) y0[k] = y;
                if (x > x1[k]) x1[k] = x; if (y > y1[k]) y1[k] = y;
                area[k]++;
            }
            for (int k = 0; k < cores; k++)
            {
                if (area[k] == 0) continue;
                int bw = x1[k] - x0[k] + 1, bh = y1[k] - y0[k] + 1;
                if (area[k] < minArea || x0[k] == 0 || y0[k] == 0 || x1[k] == w - 1 || y1[k] == h - 1) continue;
                if (area[k] * 100 < bw * bh * 50 || bw * 10 < bh * 8) continue;
                var lp = new Color[bw * bh];
                for (int y = y0[k]; y <= y1[k]; y++)
                    for (int x = x0[k]; x <= x1[k]; x++)
                        if (lab[y * w + x] == k) lp[(y - y0[k]) * bw + (x - x0[k])] = px[y * w + x];
                result.Add(new Lump { W = bw, H = bh, Area = area[k], Px = lp });
            }
            return result;
        }

        /// <summary>The lumps for trees or shrubs at a zoom (3 near .. 1 far), cut from the game's snowy sprites once. Empty if the pack is missing.</summary>
        public static List<Lump> Library(bool tree, int zoom)
        {
            int key = (tree ? 10 : 20) + zoom;
            lock (Libraries)
            {
                if (Libraries.TryGetValue(key, out var found)) return found;
                var lib = new List<Lump>();
                int minArea = zoom >= 3 ? 70 : zoom == 2 ? 18 : 5;
                var dirs = new uint[] { 1, 4, 16, 64 };
                foreach (var guid in tree ? TreeSources : ShrubSources)
                {
                    try
                    {
                        var def = FSO.Content.Content.Get().WorldObjects.Get(guid);
                        if (def?.OBJ == null || def.OBJ.BaseGraphicID == 0) continue;
                        var dgrp = def.Resource.Get<DGRP>((ushort)(def.OBJ.BaseGraphicID + 1));
                        if (dgrp == null) continue;
                        dgrp.KeepSpritesEncoded();
                        foreach (var dir in dirs)
                        {
                            foreach (var image in dgrp.Images)
                            {
                                if (image.Direction != dir || image.Zoom != zoom) continue;
                                foreach (var sprite in image.Sprites)
                                {
                                    var frame = sprite?.GetFrame();
                                    if (frame == null) continue;
                                    var px = frame.GetPixelsForRecolor();
                                    if (px == null) { LibraryFailures++; continue; }
                                    foreach (var lump in CutLumps(px, frame.Width, frame.Height, minArea))
                                    {
                                        lump.Order = lib.Count;
                                        lib.Add(lump);
                                    }
                                    LibraryFrames++;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        LibraryFailures++;
                    }
                }
                //smallest first, equal sizes in the order they were cut (as the prototype's stable sort)
                lib.Sort((a, b) => a.W != b.W ? a.W.CompareTo(b.W) : a.H != b.H ? a.H.CompareTo(b.H)
                    : a.Area != b.Area ? a.Area.CompareTo(b.Area) : a.Order.CompareTo(b.Order));
                //an empty library (content not ready, pack missing) is not kept, so a later call can try again
                if (lib.Count > 0) Libraries[key] = lib;
                return lib;
            }
        }

        /// <summary>True once the lumps for trees or shrubs at this zoom have been cut (the snowy plants are installed).</summary>
        public static bool HasLibrary(bool tree, int zoom)
        {
            lock (Libraries) return Libraries.ContainsKey((tree ? 10 : 20) + zoom);
        }

        /// <summary>
        /// Sets snow lumps onto one sprite frame. Returns false (and leaves the outputs null) when there is nothing to do.
        /// The result is outH = h + pad rows tall (the pad is on top), with its own z-buffer.
        /// </summary>
        public static bool Apply(Color[] src, byte[] z, int w, int h, int zoom, bool tree, out Color[] outPx, out byte[] outZ, out int outH)
        {
            outPx = null; outZ = null; outH = h;
            if (src == null || w <= 0 || h <= 0 || src.Length < w * h) return false;
            return Apply(src, z, w, h, zoom, tree ? TreeCover : ShrubCover, Library(tree, zoom), out outPx, out outZ, out outH);
        }

        /// <summary>Apply with a given lump library (smallest first) and cover share in percent.</summary>
        public static bool Apply(Color[] src, byte[] z, int w, int h, int zoom, int cover, List<Lump> lib, out Color[] outPx, out byte[] outZ, out int outH)
        {
            outPx = null; outZ = null; outH = h;
            if (src == null || w <= 0 || h <= 0 || src.Length < w * h) return false;
            if (lib == null || lib.Count == 0) return false;

            int pad = 0;
            foreach (var l in lib) pad = Math.Max(pad, l.H);
            int h2 = h + pad, n2 = w * h2;
            var px = new Color[n2];
            var zz = new byte[n2];
            for (int i = 0; i < pad * w; i++) zz[i] = 255;
            Array.Copy(src, 0, px, pad * w, w * h);
            for (int i = 0; i < w * h; i++) zz[pad * w + i] = (z != null && i < z.Length) ? z[i] : (byte)255;
            var opq = new bool[n2];
            int tot = 0, ytop = int.MaxValue, ybot = -1;
            for (int i = 0; i < n2; i++)
            {
                if (px[i].A < 128) continue;
                opq[i] = true; tot++;
                int y = i / w; if (y < ytop) ytop = y; if (y > ybot) ybot = y;
            }
            if (tot == 0) return false;

            const int K = 3;
            int th = zoom >= 3 ? 3 : zoom == 2 ? 6 : 12;
            var cands = new List<long[]>(); //rank, hash, index
            for (int i = 0; i < n2; i++)
            {
                if (!opq[i]) continue;
                int x = i % w, y = i / w;
                int up = i - w;
                int rank;
                if (up < 0 || !opq[up])
                {
                    //a top edge of the silhouette, not a pinhole in the leaves: open sky for a few rows above
                    if (y - 3 >= 0 && (opq[up - w] || opq[up - 2 * w])) continue;
                    rank = 0;
                }
                else
                {
                    int k = i - K * w;
                    if (k < 0 || !opq[k] || zz[k] - zz[i] < th) continue;
                    //still rising over twice the distance: a real top surface, not a bump in the leaves of a side
                    int k2 = i - 2 * K * w;
                    if (k2 < 0 || !opq[k2] || zz[k2] - zz[i] < 2 * th) continue;
                    rank = 1;
                }
                cands.Add(new long[] { rank, Hash(x, y, 7), i });
            }
            cands.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1] != b[1] ? a[1].CompareTo(b[1]) : a[2].CompareTo(b[2]));

            int medw = lib[lib.Count / 2].W, maxw = lib[lib.Count - 1].W;
            int G = Math.Max(4, maxw);
            long target = (long)tot * cover / 100, covered = 0;
            var grid = new Dictionary<long, List<int[]>>();
            var placed = new List<int[]>(); //z, index, lump, sx0, sy0, order
            var fit = new List<Lump>();
            var chosen = new List<Lump>();
            foreach (var c in cands)
            {
                if (covered >= target) break;
                int rank = (int)c[0]; uint hv = (uint)c[1]; int i = (int)c[2];
                int x = i % w, y = i / w;
                //width of what the lump rests on (the row below the anchor)
                int r = y + 1 < h2 ? y + 1 : y;
                int xl = x; while (xl > 0 && opq[r * w + xl - 1]) xl--;
                int xr = x; while (xr < w - 1 && opq[r * w + xr + 1]) xr++;
                int width = xr - xl + 1;
                int maxLump = Math.Max(width * 14 / 10, medw * 6 / 10);
                fit.Clear();
                foreach (var l in lib) if (l.W <= maxLump) fit.Add(l);
                if (fit.Count == 0) continue;
                int lo = fit.Count * SizeBias / 100;
                var s = fit[lo + (int)((hv >> 8) % (uint)(fit.Count - lo))];
                int sx0 = x - s.W / 2;
                if (sx0 < 0 || sx0 + s.W > w) continue;
                int sy0 = y - s.H * 55 / 100;
                if (sy0 < 0) continue;
                //lumps keep apart: centre distance at least Spacing percent of their mean width (vertical distance counts double)
                bool ok = true; int gx = x / G, gy = y / G;
                for (int gxx = gx - 1; gxx <= gx + 1 && ok; gxx++)
                {
                    for (int gyy = gy - 1; gyy <= gy + 1 && ok; gyy++)
                    {
                        if (!grid.TryGetValue(((long)gxx << 32) ^ (uint)gyy, out var cell)) continue;
                        foreach (var p in cell)
                        {
                            int d = (p[2] + s.W) * (rank == 0 ? Spacing : Spacing * 80 / 100) / 200;
                            int dx = p[0] - x, dy = (p[1] - y) * 2;
                            if (dx * dx + dy * dy < d * d) { ok = false; break; }
                        }
                    }
                }
                if (!ok) continue;
                long gk = ((long)gx << 32) ^ (uint)gy;
                if (!grid.TryGetValue(gk, out var list)) grid[gk] = list = new List<int[]>();
                list.Add(new int[] { x, y, s.W });
                placed.Add(new int[] { zz[i], i, chosen.Count, sx0, sy0, placed.Count });
                chosen.Add(s);
                covered += s.Area;
            }
            if (placed.Count == 0) return false;

            //far lumps first, so nearer ones overlap them
            placed.Sort((a, b) => a[0] != b[0] ? b[0].CompareTo(a[0]) : a[5].CompareTo(b[5]));
            var snowed = new bool[n2];
            foreach (var p in placed)
            {
                int az = p[0], ay = p[1] / w; var s = chosen[p[2]]; int sx0 = p[3], sy0 = p[4];
                for (int yy = 0; yy < s.H; yy++)
                {
                    for (int xx = 0; xx < s.W; xx++)
                    {
                        var c = s.Px[yy * s.W + xx];
                        if (c.A == 0) continue;
                        int X = sx0 + xx, Y = sy0 + yy;
                        if (Y >= h2) continue;
                        int j = Y * w + X;
                        //below its branch only where there is something to rest on
                        if (Y > ay + 1 && !opq[j]) continue;
                        if (!opq[j] && !snowed[j]) zz[j] = (byte)Math.Max(0, az - 1);
                        snowed[j] = true;
                        px[j] = c;
                    }
                }
            }
            outPx = px; outZ = zz; outH = h2;
            return true;
        }
    }
}
