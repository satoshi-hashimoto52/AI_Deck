using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// A level meter (§5.4).
    ///
    /// The bar is drawn on a decibel scale rather than linearly. A linear meter spends most of
    /// its length on the top 6 dB and shows almost nothing in the range a DJ actually mixes
    /// in; −60…0 dB puts the useful detail where the eye is looking.
    ///
    /// The clip indicator latches until it is explicitly cleared, so a transient between two
    /// UI frames is still reported (FR-045).
    /// </summary>
    public sealed class MeterView : MonoBehaviour
    {
        private const float FloorDb = -60f;

        private RectTransform _fill;
        private Image _fillImage;
        private Image _clipLamp;
        private bool _vertical = true;

        public static MeterView Create(string name, Transform parent, bool vertical = true)
        {
            var rect = UiFactory.Create(name, parent);
            var view = rect.gameObject.AddComponent<MeterView>();
            view._vertical = vertical;

            // Two layers: a border-coloured rounded rect with the track inset inside it.
            // An 8 px meter on a dark panel is otherwise invisible until it lights up, which
            // makes the mixer look like it is missing a control.
            var edge = rect.gameObject.AddComponent<Image>();
            edge.sprite = UiSprites.Rounded;
            edge.type = Image.Type.Sliced;
            edge.color = Theme.Line;
            edge.raycastTarget = false;

            var track = UiFactory.Create("Track", rect);
            UiFactory.Stretch(track, 1f, 1f, 1f, 1f);
            var background = track.gameObject.AddComponent<Image>();
            background.sprite = UiSprites.Rounded;
            background.type = Image.Type.Sliced;
            background.color = Theme.PanelRaised;
            background.raycastTarget = false;

            view._fill = UiFactory.Create("Fill", rect);
            view._fillImage = view._fill.gameObject.AddComponent<Image>();
            view._fillImage.color = Theme.Ok;
            view._fillImage.raycastTarget = false;

            view._clipLamp = UiFactory.CreateImage("Clip", rect, Theme.WithAlpha(Theme.Danger, 0f));
            view._clipLamp.rectTransform.anchorMin = new Vector2(0f, vertical ? 1f : 1f);
            view._clipLamp.rectTransform.anchorMax = Vector2.one;
            view._clipLamp.rectTransform.offsetMin = Vector2.zero;
            view._clipLamp.rectTransform.offsetMax = Vector2.zero;
            view._clipLamp.rectTransform.sizeDelta = new Vector2(0f, 4f);

            view.SetLevel(0f, false);
            return view;
        }

        /// <summary>Updates the bar. <paramref name="level"/> is linear amplitude, 0…1.</summary>
        public void SetLevel(float level, bool clipping)
        {
            if (_fill == null)
            {
                return;
            }

            var db = AudioSafety.LinearToDb(AudioSafety.Sanitize01(level), FloorDb);
            var fraction = Mathf.Clamp01((db - FloorDb) / -FloorDb);

            if (_vertical)
            {
                _fill.anchorMin = Vector2.zero;
                _fill.anchorMax = new Vector2(1f, fraction);
            }
            else
            {
                _fill.anchorMin = Vector2.zero;
                _fill.anchorMax = new Vector2(fraction, 1f);
            }

            _fill.offsetMin = Vector2.zero;
            _fill.offsetMax = Vector2.zero;

            // Green through the working range, amber as it approaches the limiter, red at it.
            _fillImage.color = db > -3f ? Theme.Danger : db > -12f ? Theme.Warning : Theme.Ok;
            _clipLamp.color = Theme.WithAlpha(Theme.Danger, clipping ? 1f : 0f);
        }
    }
}
