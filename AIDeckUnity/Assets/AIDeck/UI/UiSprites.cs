using UnityEngine;

namespace AIDeck.UI
{
    /// <summary>
    /// Procedurally generated sprites.
    ///
    /// uGUI's <c>Image</c> draws a rectangle unless it is given a sprite, so a jog wheel or a
    /// knob without one is a square. Rather than commit a PNG — a binary asset in a repository
    /// that deliberately has none — the shapes are drawn into a texture at startup. They are
    /// tiny, generated once, and shared by every widget.
    /// </summary>
    public static class UiSprites
    {
        private static Sprite _circle;
        private static Sprite _ring;
        private static Sprite _rounded;

        /// <summary>A filled, anti-aliased circle. Used for jog wheels and knob faces.</summary>
        public static Sprite Circle => _circle != null ? _circle : _circle = BuildCircle(256, 0f);

        /// <summary>An anti-aliased ring, two pixels thick at the generated resolution.</summary>
        public static Sprite Ring => _ring != null ? _ring : _ring = BuildCircle(256, 0.965f);

        /// <summary>
        /// A nine-sliced rounded rectangle matching the mock-up's 10 px corner radius.
        /// Sliced so one texture serves every panel and button size.
        /// </summary>
        public static Sprite Rounded => _rounded != null ? _rounded : _rounded = BuildRounded(32, 10);

        private static Sprite BuildCircle(int size, float innerFraction)
        {
            var texture = NewTexture(size);
            var pixels = new Color32[size * size];
            var centre = (size - 1) * 0.5f;
            var outer = centre;
            var inner = outer * innerFraction;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - centre;
                    var dy = y - centre;
                    var distance = Mathf.Sqrt(dx * dx + dy * dy);

                    // One pixel of smoothing on each edge: without it the circle's rim aliases
                    // badly at the sizes a jog wheel is drawn at.
                    var alpha = Mathf.Clamp01(outer - distance);
                    if (inner > 0f)
                    {
                        alpha = Mathf.Min(alpha, Mathf.Clamp01(distance - inner));
                    }

                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite BuildRounded(int size, int radius)
        {
            var texture = NewTexture(size);
            var pixels = new Color32[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(RoundedAlpha(x, y, size, radius) * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            // The border keeps the corners their true radius at any widget size.
            var border = new Vector4(radius, radius, radius, radius);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, border);
        }

        private static float RoundedAlpha(int x, int y, int size, int radius)
        {
            var cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
            var cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
            var dx = x + 0.5f - cx;
            var dy = y + 0.5f - cy;
            var distance = Mathf.Sqrt(dx * dx + dy * dy);
            return Mathf.Clamp01(radius - distance + 0.5f);
        }

        private static Texture2D NewTexture(int size) =>
            new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "AIDeck Generated",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
    }
}
