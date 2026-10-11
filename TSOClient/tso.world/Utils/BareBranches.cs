// BareBranches: procedural leafless ("bare") winter look for deciduous tree sprites.
//
// New behaviour (seasons); not in the original game. Python mirror used for the previews: proc_mirror.py (analysis files,
// not in the repo)
// (same structure, constants, hashing and iteration order - keep the two in sync).
//
// Two stages:
//  A) BuildSkeleton - once per object + view direction. Space colonisation in the shared per-object screen frame:
//     foliage pixels attract growth that starts at the tree's own visible trunk/limbs; pipe-model thickness and
//     hashed twig fans. Output: a list of tapered segments (in "units" = zoom-3 pixels) + the lower-trunk bark
//     colour + a mask of tiny wood fragments (leaf shadow) to treat as foliage.
//  B) Apply - per sprite frame, in place. A foliage pixel becomes shaded bark if a segment covers it, otherwise it
//     becomes transparent. Pure function of (shared position, own pixel, skeleton) -> no seams between tiles.
//     Only recolours/removes opaque pixels and never raises alpha, so the original z-buffer stays valid.
//
// Zooms: geometry is stored in zoom-3 units. Recommended use: BuildSkeleton from the zoom-3 (near) frames with
// zoomScale 1, then ForZoom(skeleton, 2) and ForZoom(skeleton, 4) for zoom 2 / zoom 1, so all zooms show the same
// branches. BuildSkeleton also accepts zoom-2/zoom-1 frames (zoomScale 2/4) when zoom-3 data is not available, but
// the growth then sees coarser foliage and gives a different (sparser) skeleton.
//
// Tile placement (BareTile.X/Y): for a tile with OBJD sub index sx = (sub >> 8) & 0xff, sy = sub & 0xff in an
// nx-by-ny footprint, viewed with DGRP direction dr (1, 4, 16, 64 -> k = 0, 1, 2, 3), rotate k times
// (sx, sy, nx, ny) -> (ny - 1 - sy, sx, ny, nx); then with tw = 128 / 64 / 32 for zoom 3 / 2 / 1:
//     X = (sx - sy) * tw / 2 + spriteOffsetX,   Y = (sx + sy) * tw / 4 + spriteOffsetY - frameHeight
// (sprite offsets from the DGRP sprite entry for that zoom; the engine anchors a sprite at its bottom, see
// DGRPRenderer.AddItem; Flipped = DGRP sprite flip flag). BareTreeGroup does this placement.
//
// Cost (C# estimate from the Python mirror's operation counts): build 5-40 ms per tree view (3x3 sycamore is the
// largest: ~1000 segments, ~3000 attractors); Apply is a few ms per frame (bucket grid, few segments per pixel).

using System;
using System.Collections.Generic;

namespace FSO.LotView.Utils
{
    public static class BareBranches
    {
        public sealed class BareTile
        {
            public byte[] Rgba;   // 4 bytes per pixel, W*H pixels, as decoded (not flipped)
            public byte[] Z;      // 1 byte per pixel (unused by the current algorithm, kept for the API)
            public int W, H;
            public int X, Y;      // top-left of this frame in the shared per-object frame, at its decode zoom
            public bool Flipped;  // DGRP sprite flip flag: column x is drawn at X + (W - 1 - x)
        }

        private sealed class Skeleton
        {
            // segments in units (zoom-3 pixels)
            public double[] SX0 = new double[0], SY0 = new double[0], SX1 = new double[0], SY1 = new double[0];
            public double[] SR0 = new double[0], SR1 = new double[0];
            public int BarkR = DefR, BarkG = DefG, BarkB = DefB;
            public int Scale = 1;            // zoom scale the bucket grid was built for
            public int GridX0, GridY0, GridW, GridH;
            public int[] CellStart = new int[1], CellItems = new int[0];
            public int Count;
            // bit set = wood pixel of a tiny fragment (leaf shadow) -> foliage. In BUILD pixels.
            public byte[] Speck;
            public int SpeckX0, SpeckY0, SpeckW, SpeckH, BuildScale = 1;
        }

        // ---------------------------------------------------------------- constants (units = zoom-3 pixels)
        private const double Step = 3.0;          // growth step
        private const double Infl = 45.0;         // attractor influence radius
        private const double Kill = 7.0;          // attractor kill radius
        private const int Cell = 6;               // one attractor per Cell x Cell units of foliage
        private const int MaxIter = 220;
        private const double Jitter = 0.8;
        private const double Tropism = 0.10;      // upward pull (small crowns)
        private const double TwigRate = 0.35;     // chance that a limb node gets a forked side shoot (small crowns)
        private const double MinCover = 0.40;     // thin segments must run over >= 40% opaque pixels, else dropped
        private const int RootGrid = 4;
        private const int SpeckWood = 40;         // wood components smaller than this (zoom-3 px) are leaf shadow
        private const int RootWood = 80;          // growth starts only from wood components at least this big
        private const double ThickKeep = 1.0;     // limbs this thick are kept across gaps (clipped anyway)
        private const int GridCell = 8;           // Apply bucket size in target pixels
        private const double LX = -0.55, LY = -0.55, LZ = 0.63;
        private const int DefR = 96, DefG = 82, DefB = 72;
        private const int TwigR = 78, TwigG = 64, TwigB = 54;  // thin twigs drift toward this colour

        // ---------------------------------------------------------------- helpers
        private static uint Hash(int x, int y, int salt)
        {
            unchecked
            {
                uint h = ((uint)x * 73856093u) ^ ((uint)y * 19349663u) ^ ((uint)salt * 83492791u);
                h ^= h >> 13;
                h *= 0x5bd1e995u;
                h ^= h >> 15;
                return h;
            }
        }

        // C# '/' truncates toward zero; this floors (b > 0).
        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b) != 0 && (a < 0)) q--;
            return q;
        }

        // C# '%' can be negative; this is always 0..b-1 (b > 0).
        private static int PosMod(int a, int b)
        {
            int m = a % b;
            return m < 0 ? m + b : m;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static int Max3(int a, int b, int c) { return Math.Max(a, Math.Max(b, c)); }
        private static int Min3(int a, int b, int c) { return Math.Min(a, Math.Min(b, c)); }
        private static int ValidScale(int s) { return (s == 1 || s == 2 || s == 4) ? s : 1; }

        private static bool TileOk(BareTile t)
        {
            return t != null && t.Rgba != null && t.W > 0 && t.H > 0 && t.Rgba.Length >= t.W * t.H * 4;
        }

        // ---------------------------------------------------------------- colour classification
        private static bool IsFoliage(int r, int g, int b, FoliageLeaf leaf, bool fruit)
        {
            int mx = Max3(r, g, b), mn = Min3(r, g, b), c = mx - mn;
            if (mx < 70 && g > r && g > b) return true;                                   // deep leaf shadow
            if (r >= 150 && b >= 90 && r > g + 30) return true;                           // pink / white blossom
            if (r >= 80 && r >= 2 * g && r * 10 >= b * 18) return true;                   // red fruit / red leaf
            if (r >= 165 && g >= 85 && g <= 175 && b <= 70 && r * 10 >= g * 13) return true; // orange fruit
            if (c == 0) return false;
            double s = (double)c / mx;
            double h;
            if (mx == r)
            {
                h = (double)(g - b) / c;
                if (h < 0) h += 6.0;
            }
            else if (mx == g) h = (double)(b - r) / c + 2.0;
            else h = (double)(r - g) / c + 4.0;
            h *= 60.0;
            if (h >= 75.0 && h <= 200.0 && s >= 0.16 && c >= 10) return true;          // green
            if (h >= 44.0 && h < 75.0 && s >= 0.35 && c >= 14) return true;             // olive / yellow-green
            if (h >= 12.0 && h < 44.0 && s >= 0.65 && mx >= 95) return true;            // yellow / orange leaf
            if ((h < 12.0 || h >= 320.0) && c >= 12)
            {
                // red hue: prune leaves, pink flowers, fruit. On plain green trees only clearly saturated red,
                // so red-brown bark (mulberry) stays bark.
                double need = (leaf != FoliageLeaf.Green || fruit) ? 0.45 : 0.70;
                if (s >= need) return true;
            }
            return false;
        }

        // cls: 0 transparent (alpha < minAlpha), 1 foliage, 2 wood. Near-black non-foliage pixels are leaf
        // self-shadow unless within 2px of lit wood (the shaded side of a real limb).
        private static byte[] ClassifyFrame(byte[] rgba, int w, int h, FoliageLeaf leaf, bool fruit, int minAlpha)
        {
            int n = w * h;
            byte[] cls = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int a = rgba[i * 4 + 3];
                if (a == 0 || a < minAlpha) continue;
                cls[i] = IsFoliage(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], leaf, fruit) ? (byte)1 : (byte)2;
            }
            bool[] lit = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (cls[i] == 2) lit[i] = Max3(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]) >= 60;
            }
            for (int i = 0; i < n; i++)
            {
                if (cls[i] != 2) continue;
                int mx = Max3(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
                if (mx >= 40) continue;
                int x = i % w, y = i / w;
                bool near = false;
                int yEnd = Math.Min(h - 1, y + 2), xEnd = Math.Min(w - 1, x + 2);
                for (int yy = Math.Max(0, y - 2); yy <= yEnd && !near; yy++)
                {
                    for (int xx = Math.Max(0, x - 2); xx <= xEnd; xx++)
                    {
                        if (lit[yy * w + xx]) { near = true; break; }
                    }
                }
                if (!near) cls[i] = 1;
            }
            return cls;
        }

        // ---------------------------------------------------------------- stage A: per object + view
        public static object BuildSkeleton(IList<BareTile> tiles, int zoomScale)
        {
            return BuildSkeleton(tiles, zoomScale, FoliageLeaf.Green, false);
        }

        /// <summary>
        /// Builds the branch skeleton of one object in one view direction from all of its sprite frames
        /// (tiles) decoded at one zoom. zoomScale: 1 = zoom 3 (recommended), 2 = zoom 2, 4 = zoom 1.
        /// Returns a skeleton for that zoom; use ForZoom for the other zooms. Never throws.
        /// </summary>
        public static object BuildSkeleton(IList<BareTile> tiles, int zoomScale, FoliageLeaf leaf, bool fruit)
        {
            int s = ValidScale(zoomScale);
            try
            {
                return Build(tiles, s, leaf, fruit);
            }
            catch (Exception)
            {
                // malformed input: an empty skeleton still gives a clean "all leaves removed" look
                return ForZoom(new Skeleton(), s);
            }
        }

        private static object Build(IList<BareTile> tiles, int s, FoliageLeaf leaf, bool fruit)
        {
            Skeleton sk = new Skeleton();
            if (tiles == null || tiles.Count == 0) return ForZoom(sk, s);
            // ---- shared mask over the union of tiles (build-zoom pixels)
            int bx0 = 1 << 30, by0 = 1 << 30, bx1 = -(1 << 30), by1 = -(1 << 30);
            for (int ti = 0; ti < tiles.Count; ti++)
            {
                BareTile t = tiles[ti];
                if (!TileOk(t)) continue;
                bx0 = Math.Min(bx0, t.X); by0 = Math.Min(by0, t.Y);
                bx1 = Math.Max(bx1, t.X + t.W); by1 = Math.Max(by1, t.Y + t.H);
            }
            if (bx1 <= bx0 || by1 <= by0) return ForZoom(sk, s);
            int mw = bx1 - bx0, mh = by1 - by0;
            int mn = mw * mh;
            byte[] mcls = new byte[mn];      // 0 empty, 1 foliage, 2 wood (alpha >= 128 only)
            bool[] mopq = new bool[mn];      // any opaque pixel
            int[] mrgb = new int[mn];        // packed rgb of classified pixels
            for (int ti = 0; ti < tiles.Count; ti++)
            {
                BareTile t = tiles[ti];
                if (!TileOk(t)) continue;
                byte[] cls = ClassifyFrame(t.Rgba, t.W, t.H, leaf, fruit, 128);
                for (int y = 0; y < t.H; y++)
                {
                    for (int x = 0; x < t.W; x++)
                    {
                        int i = y * t.W + x;
                        int fx = t.Flipped ? (t.W - 1 - x) : x;
                        int j = (t.Y - by0 + y) * mw + (t.X - bx0 + fx);
                        if (t.Rgba[i * 4 + 3] > 0) mopq[j] = true;
                        if (cls[i] != 0)
                        {
                            mcls[j] = cls[i];
                            mrgb[j] = (t.Rgba[i * 4] << 16) | (t.Rgba[i * 4 + 1] << 8) | t.Rgba[i * 4 + 2];
                        }
                    }
                }
            }
            // ---- wood components: tiny fragments become foliage, roots come from big ones only
            int[] comp = new int[mn];
            for (int i = 0; i < mn; i++) comp[i] = -1;
            List<int> compSize = new List<int>();
            Stack<int> stack = new Stack<int>();
            for (int st = 0; st < mn; st++)
            {
                if (mcls[st] != 2 || comp[st] >= 0) continue;
                int cid = compSize.Count;
                comp[st] = cid; stack.Push(st);
                int size = 0;
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); size++;
                    int px = i % mw, py = i / mw;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = px + dx, yy = py + dy;
                            if (xx >= 0 && xx < mw && yy >= 0 && yy < mh)
                            {
                                int j = yy * mw + xx;
                                if (mcls[j] == 2 && comp[j] < 0) { comp[j] = cid; stack.Push(j); }
                            }
                        }
                    }
                }
                compSize.Add(size);
            }
            int speckMin = Math.Max(2, SpeckWood / (s * s));
            int rootMin = Math.Max(3, RootWood / (s * s));
            byte[] speck = new byte[(mn + 7) / 8];
            for (int j = 0; j < mn; j++)
            {
                if (mcls[j] == 2 && compSize[comp[j]] < speckMin)
                {
                    mcls[j] = 1;
                    speck[j >> 3] |= (byte)(1 << (j & 7));
                }
            }
            sk.Speck = speck; sk.SpeckX0 = bx0; sk.SpeckY0 = by0; sk.SpeckW = mw; sk.SpeckH = mh; sk.BuildScale = s;
            // ---- extents
            int top = -1, bottom = -1, fx0 = mw, fx1 = -1, fy0 = mh, fy1 = -1;
            for (int y = 0; y < mh; y++)
            {
                for (int x = 0; x < mw; x++)
                {
                    int c = mcls[y * mw + x];
                    if (c == 0) continue;
                    if (top < 0) top = y;
                    bottom = y;
                    if (c == 1)
                    {
                        fx0 = Math.Min(fx0, x); fx1 = Math.Max(fx1, x); fy0 = Math.Min(fy0, y); fy1 = Math.Max(fy1, y);
                    }
                }
            }
            if (top < 0) return ForZoom(sk, s);
            // ---- bark colour from the lower trunk: wood pixels 3%..30% of the height above the bottom
            int hgt = bottom - top + 1;
            int bandLo = bottom - (hgt * 30) / 100, bandHi = bottom - (hgt * 3) / 100;
            List<long> keys = new List<long>();
            for (int pas = 0; pas < 2; pas++)
            {
                for (int y = 0; y < mh; y++)
                {
                    if (pas == 0 && (y < bandLo || y > bandHi)) continue;
                    for (int x = 0; x < mw; x++)
                    {
                        int j = y * mw + x;
                        if (mcls[j] != 2) continue;
                        int c = mrgb[j];
                        int lum = (((c >> 16) & 255) * 30 + ((c >> 8) & 255) * 59 + (c & 255) * 11) / 100;
                        if (lum < 30) continue;
                        keys.Add((long)lum * mn + j);   // unique keys -> unstable sort is still deterministic
                    }
                }
                if (keys.Count >= 8 || pas == 1) break;
                keys.Clear();
            }
            if (keys.Count > 0)
            {
                keys.Sort();
                int lo = (keys.Count * 45) / 100;
                int hi = Math.Max(lo + 1, (keys.Count * 75) / 100);
                int sr = 0, sg = 0, sb = 0;
                for (int k = lo; k < hi; k++)
                {
                    int c = mrgb[(int)(keys[k] % mn)];
                    sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255;
                }
                sk.BarkR = sr / (hi - lo); sk.BarkG = sg / (hi - lo); sk.BarkB = sb / (hi - lo);
            }
            if (fx1 < 0) return ForZoom(sk, s);
            // ---- crown size -> broom control: big crowns (3x3 sycamore) otherwise grow many parallel upright
            //      limbs, so they get less upward pull, fewer twig fans and a larger kill radius
            int crownW = (fx1 - fx0 + 1) * s, crownH = (fy1 - fy0 + 1) * s;
            double wide = Clamp(((double)crownW * crownH - 75000.0) / 35000.0, 0.0, 1.0);
            double tropism = Tropism * (1.0 - wide);
            double twigRate = TwigRate * (1.0 - 0.5 * wide);
            double kill = Kill + 2.0 * wide;
            // ---- attractors: one hashed foliage pixel per Cell x Cell units, above the trunk zone
            int cellPx = Math.Max(1, Cell / s);
            int gw = (mw + cellPx - 1) / cellPx, gh = (mh + cellPx - 1) / cellPx;
            int[] pick = new int[gw * gh];
            uint[] pickH = new uint[gw * gh];
            for (int k = 0; k < pick.Length; k++) pick[k] = -1;
            for (int y = 0; y < mh; y++)
            {
                for (int x = 0; x < mw; x++)
                {
                    int j = y * mw + x;
                    if (mcls[j] != 1) continue;
                    int k = (y / cellPx) * gw + (x / cellPx);
                    uint hv = Hash(bx0 + x, by0 + y, 1);
                    if (pick[k] < 0 || hv < pickH[k]) { pick[k] = j; pickH[k] = hv; }
                }
            }
            int ycut = bottom - (hgt * 12) / 100;
            List<double> axl = new List<double>(), ayl = new List<double>();
            for (int k = 0; k < gw * gh; k++)
            {
                int j = pick[k];
                if (j < 0) continue;
                int py = j / mw, px = j % mw;
                if (py >= ycut) continue;
                axl.Add((bx0 + px + 0.5) * s); ayl.Add((by0 + py + 0.5) * s);
            }
            int na = axl.Count;
            if (na == 0) return ForZoom(sk, s);
            double[] ax = axl.ToArray(), ay = ayl.ToArray();
            // ---- roots: solid wood pixels of big components on a RootGrid-unit lattice
            List<double> nx = new List<double>(), ny = new List<double>();
            List<int> npar = new List<int>();
            int gridPx = Math.Max(1, RootGrid / s);
            int needNb = s == 1 ? 8 : (s == 2 ? 5 : 3);
            for (int y = 0; y < mh; y++)
            {
                if (PosMod(by0 + y, gridPx) != 0) continue;
                for (int x = 0; x < mw; x++)
                {
                    if (PosMod(bx0 + x, gridPx) != 0) continue;
                    int j0 = y * mw + x;
                    if (mcls[j0] != 2 || compSize[comp[j0]] < rootMin) continue;
                    int nb = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int xx = x + dx, yy = y + dy;
                            if (xx >= 0 && xx < mw && yy >= 0 && yy < mh && mcls[yy * mw + xx] == 2) nb++;
                        }
                    }
                    if (nb >= needNb)
                    {
                        nx.Add((bx0 + x + 0.5) * s); ny.Add((by0 + y + 0.5) * s); npar.Add(-1);
                    }
                }
            }
            if (nx.Count == 0)
            {
                double sumX = 0.0, maxY = -1e18;
                for (int a = 0; a < na; a++)
                {
                    sumX += ax[a];
                    if (ay[a] > maxY) maxY = ay[a];
                }
                nx.Add(sumX / na); ny.Add(maxY + 10.0); npar.Add(-1);
            }
            int nroot = nx.Count;
            // ---- space colonisation with a cached nearest node per attractor
            double ux0 = bx0 * s, uy0 = by0 * s;
            int agw = (int)((mw * s) / Infl) + 1, agh = (int)((mh * s) / Infl) + 1;
            int[] acount = new int[agw * agh];
            int[] acell = new int[na];
            for (int a = 0; a < na; a++)
            {
                int cx = Clamp((int)((ax[a] - ux0) / Infl), 0, agw - 1);
                int cy = Clamp((int)((ay[a] - uy0) / Infl), 0, agh - 1);
                acell[a] = cy * agw + cx; acount[acell[a]]++;
            }
            int[] astart = new int[agw * agh + 1];
            for (int k = 0; k < agw * agh; k++) astart[k + 1] = astart[k] + acount[k];
            int[] aitems = new int[na];
            int[] afill = new int[agw * agh];
            Array.Copy(astart, afill, agw * agh);
            for (int a = 0; a < na; a++) { aitems[afill[acell[a]]] = a; afill[acell[a]]++; }
            bool[] alive = new bool[na];
            int[] near = new int[na];
            double[] nearD = new double[na];
            for (int a = 0; a < na; a++) { alive[a] = true; near[a] = -1; }
            double infl2 = Infl * Infl, kill2 = kill * kill;
            Grow g = new Grow
            {
                Ax = ax, Ay = ay, Nx = nx, Ny = ny, Alive = alive, Near = near, NearD = nearD,
                AStart = astart, AItems = aitems, Agw = agw, Agh = agh, Ux0 = ux0, Uy0 = uy0, Infl2 = infl2
            };
            for (int i = 0; i < nroot; i++) Offer(g, i);
            List<double> pullX = new List<double>(), pullY = new List<double>();
            List<int> pullN = new List<int>();
            List<int> touched = new List<int>();
            for (int it = 0; it < MaxIter; it++)
            {
                while (pullX.Count < nx.Count) { pullX.Add(0.0); pullY.Add(0.0); pullN.Add(0); }
                touched.Clear();
                for (int a = 0; a < na; a++)
                {
                    if (!alive[a] || near[a] < 0) continue;
                    if (nearD[a] < kill2) { alive[a] = false; continue; }
                    int b = near[a];
                    double d = Math.Sqrt(nearD[a]);
                    if (d <= 0.0) d = 1.0;
                    if (pullN[b] == 0) touched.Add(b);
                    pullX[b] += (ax[a] - nx[b]) / d; pullY[b] += (ay[a] - ny[b]) / d; pullN[b]++;
                }
                if (touched.Count == 0) break;
                touched.Sort();
                int first = nx.Count;
                for (int q = 0; q < touched.Count; q++)
                {
                    int b = touched[q];
                    double jit = (Hash(b, it, 7) % 1000) / 1000.0 - 0.5;
                    double vx = pullX[b] / pullN[b] + Jitter * jit;
                    double vy = pullY[b] / pullN[b] - tropism;
                    pullX[b] = 0.0; pullY[b] = 0.0; pullN[b] = 0;
                    double L = Math.Sqrt(vx * vx + vy * vy);
                    if (L < 1e-6) continue;
                    nx.Add(nx[b] + vx / L * Step); ny.Add(ny[b] + vy / L * Step); npar.Add(b);
                }
                if (nx.Count == first) break;
                for (int i = first; i < nx.Count; i++) Offer(g, i);
            }
            int N = nx.Count;
            // ---- pipe-model thickness
            int[] cnt = new int[N];
            bool[] hasChild = new bool[N];
            for (int i = 0; i < N; i++) if (npar[i] >= 0) hasChild[npar[i]] = true;
            for (int i = nroot; i < N; i++) if (!hasChild[i]) cnt[i] = 1;
            for (int i = N - 1; i >= nroot; i--) if (npar[i] >= 0) cnt[npar[i]] += cnt[i];
            double[] rad = new double[N];
            for (int i = 0; i < N; i++) rad[i] = Math.Min(3.0, 0.5 * Math.Pow(cnt[i], 0.4));
            // ---- limb segments; drop thin segments (and their subtrees) that mostly cross transparent pixels
            SegList segs = new SegList();
            Mask m = new Mask { Opq = mopq, W = mw, H = mh, X0 = bx0, Y0 = by0, S = s };
            bool[] dropped = new bool[N];
            for (int i = nroot; i < N; i++)
            {
                int p = npar[i];
                if (p >= nroot && dropped[p]) { dropped[i] = true; continue; }
                if (rad[i] < ThickKeep && Coverage(m, nx[p], ny[p], nx[i], ny[i]) < MinCover)
                {
                    dropped[i] = true; continue;
                }
                double r0 = p >= nroot ? Math.Max(rad[p], rad[i]) : rad[i] + 0.4;
                segs.Add(nx[p], ny[p], nx[i], ny[i], r0, rad[i]);
            }
            // ---- twig fans: short forked shoots off limb nodes (clipped to the original foliage in Apply)
            for (int i = nroot; i < N; i++)
            {
                if (dropped[i]) continue;
                int p = npar[i];
                uint hk = Hash(i, 0, 21);
                if ((hk % 1000) / 1000.0 >= twigRate) continue;
                double bas = Math.Atan2(ny[i] - ny[p], nx[i] - nx[p]);
                int side = ((hk >> 10) & 1) != 0 ? 1 : -1;
                double ang = bas + side * (0.55 + 0.5 * ((hk >> 12) % 100) / 100.0);
                if (Math.Abs(ang + Math.PI / 2) < Math.PI) ang = ang * 0.8 + (-Math.PI / 2) * 0.2;
                double len = 6.0 + ((hk >> 20) % 100) / 100.0 * 10.0;
                Shoot(segs, m, nx[i], ny[i], ang, len, Math.Max(0.45, rad[i] * 0.6), 1, (int)(hk & 0xffff));
            }
            segs.CopyTo(sk);
            return ForZoom(sk, s);
        }

        private sealed class Grow
        {
            public double[] Ax, Ay, NearD;
            public List<double> Nx, Ny;
            public bool[] Alive;
            public int[] Near, AStart, AItems;
            public int Agw, Agh;
            public double Ux0, Uy0, Infl2;
        }

        // Tell the attractors within Infl about a new node. Strictly closer wins, so ties keep the lower index.
        private static void Offer(Grow g, int node)
        {
            double px = g.Nx[node], py = g.Ny[node];
            int cx = Clamp((int)((px - g.Ux0) / Infl), 0, g.Agw - 1);
            int cy = Clamp((int)((py - g.Uy0) / Infl), 0, g.Agh - 1);
            for (int gy = cy - 1; gy <= cy + 1; gy++)
            {
                if (gy < 0 || gy >= g.Agh) continue;
                for (int gx = cx - 1; gx <= cx + 1; gx++)
                {
                    if (gx < 0 || gx >= g.Agw) continue;
                    int k = gy * g.Agw + gx;
                    for (int q = g.AStart[k]; q < g.AStart[k + 1]; q++)
                    {
                        int a = g.AItems[q];
                        if (!g.Alive[a]) continue;
                        double dx = g.Ax[a] - px, dy = g.Ay[a] - py;
                        double d = dx * dx + dy * dy;
                        if (d >= g.Infl2) continue;
                        if (g.Near[a] < 0 || d < g.NearD[a]) { g.Near[a] = node; g.NearD[a] = d; }
                    }
                }
            }
        }

        private sealed class Mask
        {
            public bool[] Opq;
            public int W, H, X0, Y0, S;
        }

        private sealed class SegList
        {
            private readonly List<double> x0 = new List<double>(), y0 = new List<double>();
            private readonly List<double> x1 = new List<double>(), y1 = new List<double>();
            private readonly List<double> r0 = new List<double>(), r1 = new List<double>();

            public void Add(double ax, double ay, double bx, double by, double ra, double rb)
            {
                x0.Add(ax); y0.Add(ay); x1.Add(bx); y1.Add(by); r0.Add(ra); r1.Add(rb);
            }

            public void CopyTo(Skeleton sk)
            {
                sk.SX0 = x0.ToArray(); sk.SY0 = y0.ToArray(); sk.SX1 = x1.ToArray(); sk.SY1 = y1.ToArray();
                sk.SR0 = r0.ToArray(); sk.SR1 = r1.ToArray();
            }
        }

        // Fraction of 1-unit samples along the segment that land on opaque original pixels.
        private static double Coverage(Mask m, double x0, double y0, double x1, double y1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            double L = Math.Sqrt(dx * dx + dy * dy);
            int n = (int)L + 1;
            int hit = 0;
            for (int k = 0; k <= n; k++)
            {
                double t = (double)k / n;
                int px = (int)Math.Floor((x0 + dx * t) / m.S) - m.X0;
                int py = (int)Math.Floor((y0 + dy * t) / m.S) - m.Y0;
                if (px >= 0 && px < m.W && py >= 0 && py < m.H && m.Opq[py * m.W + px]) hit++;
            }
            return (double)hit / (n + 1);
        }

        private static void Shoot(SegList segs, Mask m, double x, double y, double ang, double length, double r,
                                  int depth, int key)
        {
            double ex = x + Math.Cos(ang) * length, ey = y + Math.Sin(ang) * length;
            if (Coverage(m, x, y, ex, ey) < MinCover) return;
            segs.Add(x, y, ex, ey, r, 0.35);
            if (depth <= 0) return;
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                uint hk = Hash(key, sgn + 5, 31);
                if (hk % 3 == 0) continue;
                double da = sgn * (0.35 + 0.4 * ((hk >> 8) % 100) / 100.0);
                Shoot(segs, m, ex, ey, ang + da, length * 0.6, 0.4, depth - 1, (int)(hk & 0xffff));
            }
        }

        // X,Y: pixel in the skeleton's target zoom; samples the build-resolution speck mask at the pixel centre.
        private static bool IsSpeck(Skeleton sk, int X, int Y)
        {
            if (sk == null || sk.Speck == null) return false;
            int bx = FloorDiv((2 * X + 1) * sk.Scale, 2 * sk.BuildScale) - sk.SpeckX0;
            int by = FloorDiv((2 * Y + 1) * sk.Scale, 2 * sk.BuildScale) - sk.SpeckY0;
            if (bx < 0 || by < 0 || bx >= sk.SpeckW || by >= sk.SpeckH) return false;
            int j = by * sk.SpeckW + bx;
            return ((sk.Speck[j >> 3] >> (j & 7)) & 1) != 0;
        }

        /// <summary>
        /// Re-targets a skeleton (built at any zoom) to frames decoded at zoomScale (1 = zoom 3, 2 = zoom 2,
        /// 4 = zoom 1). Shares the segment data and builds a bucket grid for that zoom. Cheap. Never throws.
        /// </summary>
        public static object ForZoom(object skeleton, int zoomScale)
        {
            Skeleton src = skeleton as Skeleton;
            if (src == null) return null;
            int s = ValidScale(zoomScale);
            Skeleton sk = new Skeleton();
            sk.SX0 = src.SX0; sk.SY0 = src.SY0; sk.SX1 = src.SX1; sk.SY1 = src.SY1; sk.SR0 = src.SR0; sk.SR1 = src.SR1;
            sk.BarkR = src.BarkR; sk.BarkG = src.BarkG; sk.BarkB = src.BarkB;
            sk.Speck = src.Speck; sk.SpeckX0 = src.SpeckX0; sk.SpeckY0 = src.SpeckY0; sk.SpeckW = src.SpeckW;
            sk.SpeckH = src.SpeckH; sk.BuildScale = src.BuildScale;
            sk.Scale = s;
            int n = sk.SX0.Length;
            sk.Count = n;
            if (n == 0) return sk;
            double inv = 1.0 / s;
            double x0 = 1e18, y0 = 1e18, x1 = -1e18, y1 = -1e18;
            for (int i = 0; i < n; i++)
            {
                double r = Math.Max(sk.SR0[i], sk.SR1[i]) * inv + 1.5;
                x0 = Math.Min(x0, Math.Min(sk.SX0[i], sk.SX1[i]) * inv - r);
                x1 = Math.Max(x1, Math.Max(sk.SX0[i], sk.SX1[i]) * inv + r);
                y0 = Math.Min(y0, Math.Min(sk.SY0[i], sk.SY1[i]) * inv - r);
                y1 = Math.Max(y1, Math.Max(sk.SY0[i], sk.SY1[i]) * inv + r);
            }
            sk.GridX0 = (int)Math.Floor(x0); sk.GridY0 = (int)Math.Floor(y0);
            sk.GridW = FloorDiv((int)Math.Floor(x1) - sk.GridX0, GridCell) + 1;
            sk.GridH = FloorDiv((int)Math.Floor(y1) - sk.GridY0, GridCell) + 1;
            int cells = sk.GridW * sk.GridH;
            int[] count = new int[cells + 1];
            int[] fill = null;
            for (int pas = 0; pas < 2; pas++)
            {
                if (pas == 1)
                {
                    int[] start = new int[cells + 1];
                    for (int k = 0; k < cells; k++) start[k + 1] = start[k] + count[k];
                    sk.CellStart = start;
                    sk.CellItems = new int[start[cells]];
                    fill = new int[cells];
                    Array.Copy(start, fill, cells);
                }
                for (int i = 0; i < n; i++)
                {
                    double r = Math.Max(sk.SR0[i], sk.SR1[i]) * inv + 1.5;
                    int cx0 = FloorDiv((int)Math.Floor(Math.Min(sk.SX0[i], sk.SX1[i]) * inv - r) - sk.GridX0, GridCell);
                    int cx1 = FloorDiv((int)Math.Floor(Math.Max(sk.SX0[i], sk.SX1[i]) * inv + r) - sk.GridX0, GridCell);
                    int cy0 = FloorDiv((int)Math.Floor(Math.Min(sk.SY0[i], sk.SY1[i]) * inv - r) - sk.GridY0, GridCell);
                    int cy1 = FloorDiv((int)Math.Floor(Math.Max(sk.SY0[i], sk.SY1[i]) * inv + r) - sk.GridY0, GridCell);
                    for (int cy = cy0; cy <= cy1; cy++)
                    {
                        for (int cx = cx0; cx <= cx1; cx++)
                        {
                            int k = cy * sk.GridW + cx;
                            if (pas == 0) count[k]++;
                            else { sk.CellItems[fill[k]] = i; fill[k]++; }
                        }
                    }
                }
            }
            return sk;
        }

        // ---------------------------------------------------------------- stage B: per sprite frame
        public static void Apply(object skeleton, byte[] rgba, byte[] z, int w, int h, int x, int y,
                                 FoliageLeaf leaf, bool fruit)
        {
            Apply(skeleton, rgba, z, w, h, x, y, false, leaf, fruit);
        }

        /// <summary>
        /// In place, per sprite frame. x,y: frame top-left in the shared per-object frame at the skeleton's zoom;
        /// flipped: DGRP flip flag of this sprite. Foliage pixels become shaded bark where a skeleton segment
        /// covers them, otherwise transparent. Never touches a transparent pixel and never raises alpha.
        /// A null skeleton removes all foliage (plain bare trunk). Never throws on empty/short input.
        /// </summary>
        public static void Apply(object skeleton, byte[] rgba, byte[] z, int w, int h, int x, int y, bool flipped,
                                 FoliageLeaf leaf, bool fruit)
        {
            if (rgba == null || w <= 0 || h <= 0 || rgba.Length < w * h * 4) return;
            Skeleton sk = skeleton as Skeleton;
            int n = w * h;
            byte[] cls = ClassifyFrame(rgba, w, h, leaf, fruit, 1);
            // Isolated non-foliage specks (leaf glints) are foliage too; a real limb crossing the frame edge
            // still has >= 2 wood neighbours along itself, so this cannot seam. Tiny wood fragments found by
            // the build (shared mask) are foliage as well.
            bool[] fol = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (cls[i] == 1) { fol[i] = true; continue; }
                if (cls[i] != 2) continue;
                int px = i % w, py = i / w, nb = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int xx = px + dx, yy = py + dy;
                        if (xx >= 0 && xx < w && yy >= 0 && yy < h && cls[yy * w + xx] == 2) nb++;
                    }
                }
                fol[i] = nb <= 1 || IsSpeck(sk, x + (flipped ? (w - 1 - px) : px), y + py);
            }
            int br = DefR, bg = DefG, bb = DefB, s = 1;
            if (sk != null) { br = sk.BarkR; bg = sk.BarkG; bb = sk.BarkB; s = sk.Scale; }
            double inv = 1.0 / s;
            for (int i = 0; i < n; i++)
            {
                if (!fol[i]) continue;
                int o = i * 4;
                int a = rgba[o + 3];
                int px = i % w, py = i / w;
                int fx = flipped ? (w - 1 - px) : px;
                double X = x + fx + 0.5, Y = y + py + 0.5;
                double best = -1.0, bt = 0.0, bnx = 0.0, bny = 0.0, bru = 0.0;
                if (sk != null && sk.Count > 0)
                {
                    int cx = FloorDiv((int)Math.Floor(X) - sk.GridX0, GridCell);
                    int cy = FloorDiv((int)Math.Floor(Y) - sk.GridY0, GridCell);
                    if (cx >= 0 && cx < sk.GridW && cy >= 0 && cy < sk.GridH)
                    {
                        int k = cy * sk.GridW + cx;
                        for (int q = sk.CellStart[k]; q < sk.CellStart[k + 1]; q++)
                        {
                            int j = sk.CellItems[q];
                            double x0 = sk.SX0[j] * inv, y0 = sk.SY0[j] * inv;
                            double dx = sk.SX1[j] * inv - x0, dy = sk.SY1[j] * inv - y0;
                            double L2 = dx * dx + dy * dy;
                            if (L2 < 1e-9) L2 = 1e-9;
                            double t = ((X - x0) * dx + (Y - y0) * dy) / L2;
                            t = Clamp(t, 0.0, 1.0);
                            double qx = x0 + t * dx, qy = y0 + t * dy;
                            double ru = sk.SR0[j] + (sk.SR1[j] - sk.SR0[j]) * t;
                            double rr = ru * inv;
                            if (rr < 0.5) rr = 0.5;   // thinnest drawn twig is about 1 pixel at any zoom
                            double ex = X - qx, ey = Y - qy;
                            double d = Math.Sqrt(ex * ex + ey * ey);
                            double cov = rr + 0.5 - d;
                            if (cov <= 0.0 || cov <= best) continue;
                            double L = Math.Sqrt(L2);
                            best = cov;
                            bt = (ex * (-dy) + ey * dx) / L / rr;   // signed position across the limb
                            bnx = -dy / L; bny = dx / L; bru = ru;
                        }
                    }
                }
                if (best <= 0.0)
                {
                    rgba[o] = 0; rgba[o + 1] = 0; rgba[o + 2] = 0; rgba[o + 3] = 0;
                    continue;
                }
                double tt = Clamp(bt, -1.0, 1.0);
                double nz = Math.Sqrt(Math.Max(0.0, 1.0 - tt * tt));
                double diff = Math.Max(0.0, bnx * tt * LX + bny * tt * LY + nz * LZ);
                // bark grain: hash of unit coordinates, identical for every tile -> seamless
                double gr = ((Hash((int)Math.Floor(X * s * 2.0), (int)Math.Floor(Y * s), 11) % 100) / 100.0 - 0.5) * 0.18;
                double kf = 0.55 + 0.6 * diff + gr;
                int mx = Max3(rgba[o], rgba[o + 1], rgba[o + 2]);
                kf *= 0.75 + 0.25 * Math.Min(1.0, mx / 170.0);   // keep some of the crown's own light/shadow
                int al = best >= 1.0 ? 255 : (int)(255.0 * best);
                double m = Clamp((1.6 - bru) / 1.2, 0.0, 1.0) * 0.55;
                double cr = br + (TwigR - br) * m, cg = bg + (TwigG - bg) * m, cb = bb + (TwigB - bb) * m;
                rgba[o] = (byte)Math.Min(255, (int)(cr * kf));
                rgba[o + 1] = (byte)Math.Min(255, (int)(cg * kf));
                rgba[o + 2] = (byte)Math.Min(255, (int)(cb * kf));
                rgba[o + 3] = (byte)Math.Min(a, al);
            }
        }
    }
}
