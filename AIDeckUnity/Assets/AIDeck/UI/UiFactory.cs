using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// Builders for the small set of uGUI objects AI Deck uses.
    ///
    /// Every screen is constructed from code — see <c>docs/ARCHITECTURE.md</c> §3 — so these
    /// helpers stand in for the prefabs a scene-authored project would have. Keeping them in
    /// one place means a spacing or colour decision is made once.
    /// </summary>
    public static class UiFactory
    {
        private static Font _font;

        /// <summary>
        /// The UI font.
        ///
        /// Unity 6 no longer ships <c>Arial.ttf</c> as a builtin, and TextMeshPro would need a
        /// generated font asset committed as a binary. A dynamic OS font keeps the repository
        /// free of binary assets and renders the short, large labels this interface uses
        /// perfectly well.
        /// </summary>
        public static Font Font
        {
            get
            {
                if (_font != null)
                {
                    return _font;
                }

                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_font == null)
                {
                    _font = Font.CreateDynamicFontFromOSFont(
                        new[] { "Helvetica Neue", "Helvetica", "Arial", "SF Pro Text" }, 16);
                }

                return _font;
            }
        }

        /// <summary>Creates a child object with a stretched RectTransform.</summary>
        public static RectTransform Create(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        /// <summary>A flat coloured rectangle.</summary>
        public static Image CreateImage(string name, Transform parent, Color color)
        {
            var rect = Create(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// A rounded panel: a border-coloured rounded rectangle with the fill inset by one
        /// pixel on top of it.
        ///
        /// Two layers rather than uGUI's <c>Outline</c>, because <c>Outline</c> duplicates the
        /// mesh at an offset — it produces a drop shadow, not a border, and on a rounded
        /// sprite it looks like a printing misregistration. Inset fill gives a true one-pixel
        /// edge that follows the corner radius.
        /// </summary>
        public static RectTransform CreatePanel(string name, Transform parent, Color? fill = null, Color? border = null)
        {
            var root = Create(name, parent);

            var edge = root.gameObject.AddComponent<Image>();
            edge.sprite = UiSprites.Rounded;
            edge.type = Image.Type.Sliced;
            edge.color = border ?? Theme.Line;
            edge.raycastTarget = false;

            var inner = Create("Fill", root);
            Stretch(inner, 1f, 1f, 1f, 1f);
            var innerImage = inner.gameObject.AddComponent<Image>();
            innerImage.sprite = UiSprites.Rounded;
            innerImage.type = Image.Type.Sliced;
            innerImage.color = fill ?? Theme.Panel;
            innerImage.raycastTarget = false;

            return root;
        }

        /// <summary>The border and fill images of a panel built by <see cref="CreatePanel"/>.</summary>
        public static void GetPanelLayers(RectTransform panel, out Image border, out Image fill)
        {
            border = panel.GetComponent<Image>();
            var child = panel.Find("Fill");
            fill = child != null ? child.GetComponent<Image>() : null;
        }

        public static Text CreateText(
            string name,
            Transform parent,
            string content,
            int fontSize = Theme.FontSizeBody,
            TextAnchor anchor = TextAnchor.MiddleLeft,
            Color? color = null,
            FontStyle style = FontStyle.Normal)
        {
            var rect = Create(name, parent);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = Font;
            text.text = content ?? string.Empty;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = anchor;
            text.color = color ?? Theme.Text;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>Anchors a rect to a fixed offset from its parent's edges.</summary>
        public static RectTransform Stretch(RectTransform rect, float left, float top, float right, float bottom)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
            return rect;
        }

        /// <summary>Places a rect at an absolute position and size within its parent.</summary>
        public static RectTransform Place(RectTransform rect, float x, float y, float width, float height)
        {
            // Anchored to the parent's top-left, which makes a hand-written layout read the
            // same way as the mock-up's CSS.
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        /// <summary>Builds the canvas both applications hang their UI from.</summary>
        public static Canvas CreateCanvas(string name, Transform parent, Vector2 referenceResolution)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            // Match height: the control surface is laid out in rows, so a wider screen should
            // give more room per row rather than shrink everything.
            scaler.matchWidthOrHeight = 1f;

            return canvas;
        }

        /// <summary>
        /// Insets a rect by the device's safe area (FR-075).
        ///
        /// Applied to the root content rect, so every control inside it is automatically clear
        /// of the rounded corners and the home indicator without each widget knowing about them.
        /// </summary>
        public static void ApplySafeArea(RectTransform rect)
        {
            if (rect == null)
            {
                return;
            }

            var safe = Screen.safeArea;
            var width = Screen.width;
            var height = Screen.height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var min = new Vector2(safe.xMin / width, safe.yMin / height);
            var max = new Vector2(safe.xMax / width, safe.yMax / height);

            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
