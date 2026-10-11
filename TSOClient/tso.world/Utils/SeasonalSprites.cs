using FSO.Common.Rendering;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Runtime.CompilerServices;

namespace FSO.LotView.Utils
{
    /// <summary>
    /// Builds the recoloured textures for seasonal foliage (see SeasonalFoliage). One texture per sprite and look, made the
    /// first time it is drawn and kept for as long as the sprite exists. Falls back to null (draw the original) if the sprite
    /// has no SPR2 frame or cannot be decoded.
    /// </summary>
    public static class SeasonalSprites
    {
        private static readonly ConditionalWeakTable<DGRPSprite, Texture2D[]> Cache = new ConditionalWeakTable<DGRPSprite, Texture2D[]>();

        /// <summary>Counters for the season_info diagnostic: sprites recoloured, and the reasons others fell back to the original.</summary>
        public static int Built, NoFrame, NoPixels, Requests;
        public static string LastFailure = "";

        public static WorldTexture? Get(GraphicsDevice device, DGRPSprite sprite, FoliageLook look)
        {
            Requests++;
            var frame = sprite.GetFrame();
            if (frame == null) { NoFrame++; return null; }

            //the z-buffer is the original's: only the colours change
            var original = frame.GetWorldTexture(device);
            if (original.Pixel == null) return null;

            var textures = Cache.GetValue(sprite, s => new Texture2D[6]);
            var tex = textures[(int)look];
            if (tex == null || tex.IsDisposed)
            {
                var src = frame.GetPixelsForRecolor();
                if (src == null || frame.Width <= 0 || frame.Height <= 0 || src.Length < frame.Width * frame.Height) { NoPixels++; LastFailure = frame.DescribeState() + (src == null ? ", nothing decoded" : ", decoded " + src.Length); return null; }
                var w = frame.Width;
                var h = frame.Height;
                var px = new Color[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var c = src[y * w + x];
                        byte r = c.R, g = c.G, b = c.B, a = c.A;
                        SeasonalFoliage.Recolor(ref r, ref g, ref b, ref a, look, x, y);
                        px[y * w + x] = new Color(r, g, b, a);
                    }
                }
                tex = new Texture2D(device, w, h, false, SurfaceFormat.Color);
                tex.SetData(px);
                tex.Tag = new TextureInfo(tex, w, h);
                textures[(int)look] = tex;
                Built++;
            }
            return new WorldTexture { Pixel = tex, ZBuffer = original.ZBuffer };
        }
    }
}
