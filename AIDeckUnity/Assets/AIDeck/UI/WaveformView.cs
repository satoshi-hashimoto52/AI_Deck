using AIDeck.Core.Analysis;
using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// The scrolling waveform of §5.2.
    ///
    /// Drawn as a generated mesh rather than a texture: the envelope is already a peak-per-
    /// bucket array, so one quad per screen column draws it directly with no per-frame
    /// texture upload and no allocation once the vertex helper has grown.
    ///
    /// The playhead stays fixed at <see cref="PlayheadFraction"/> and the audio scrolls past
    /// it, which is how a DJ waveform behaves: the thing you are watching for — the next
    /// transient — is always approaching the same point on screen.
    /// </summary>
    public sealed class WaveformView : MaskableGraphic
    {
        /// <summary>Where the playhead sits across the view, 0 = left edge.</summary>
        public const float PlayheadFraction = 0.35f;

        /// <summary>Seconds of audio visible across the whole view.</summary>
        private float _windowSeconds = 12f;

        private WaveformData _waveform = WaveformData.Empty;
        private double _positionSeconds;
        private Color _accent = Theme.DeckA;
        private bool _loopActive;
        private double _loopIn;
        private double _loopOut;
        private double _cueSeconds;
        private bool _cueSet;

        /// <summary>Columns drawn. Fixed so the cost does not depend on the window length.</summary>
        private const int ColumnCount = 320;

        public float WindowSeconds
        {
            get => _windowSeconds;
            set
            {
                var clamped = AudioSafety.Sanitize(value, 2f, 120f, 12f);
                if (Mathf.Approximately(_windowSeconds, clamped))
                {
                    return;
                }

                _windowSeconds = clamped;
                SetVerticesDirty();
            }
        }

        public Color Accent
        {
            get => _accent;
            set
            {
                _accent = value;
                SetVerticesDirty();
            }
        }

        public bool HasWaveform => _waveform != null && !_waveform.IsEmpty;

        public void SetWaveform(WaveformData waveform)
        {
            _waveform = waveform ?? WaveformData.Empty;
            SetVerticesDirty();
        }

        /// <summary>Moves the playhead. Called every frame from the host state.</summary>
        public void SetPosition(double seconds)
        {
            if (System.Math.Abs(_positionSeconds - seconds) < 1e-4d)
            {
                return;
            }

            _positionSeconds = seconds;
            SetVerticesDirty();
        }

        public void SetLoop(bool active, double inSeconds, double outSeconds)
        {
            if (_loopActive == active &&
                System.Math.Abs(_loopIn - inSeconds) < 1e-4d &&
                System.Math.Abs(_loopOut - outSeconds) < 1e-4d)
            {
                return;
            }

            _loopActive = active;
            _loopIn = inSeconds;
            _loopOut = outSeconds;
            SetVerticesDirty();
        }

        public void SetCue(bool isSet, double seconds)
        {
            if (_cueSet == isSet && System.Math.Abs(_cueSeconds - seconds) < 1e-4d)
            {
                return;
            }

            _cueSet = isSet;
            _cueSeconds = seconds;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper helper)
        {
            helper.Clear();

            var rect = GetPixelAdjustedRect();
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var startSeconds = _positionSeconds - _windowSeconds * PlayheadFraction;
            var secondsPerColumn = _windowSeconds / ColumnCount;
            var columnWidth = rect.width / ColumnCount;
            var centreY = rect.yMin + rect.height * 0.5f;
            var halfHeight = rect.height * 0.5f;

            // Loop region, drawn behind the envelope so the bars stay readable.
            if (_loopActive && _loopOut > _loopIn)
            {
                var loopStart = TimeToX(_loopIn, startSeconds, rect);
                var loopEnd = TimeToX(_loopOut, startSeconds, rect);
                if (loopEnd > rect.xMin && loopStart < rect.xMax)
                {
                    AddQuad(helper,
                        Mathf.Max(rect.xMin, loopStart), rect.yMin,
                        Mathf.Min(rect.xMax, loopEnd), rect.yMax,
                        Theme.WithAlpha(_accent, 0.12f));
                }
            }

            var peakColour = Theme.WithAlpha(_accent, 0.55f);
            var rmsColour = Theme.WithAlpha(_accent, 0.9f);

            for (var column = 0; column < ColumnCount; column++)
            {
                var seconds = startSeconds + column * secondsPerColumn;
                if (seconds < 0d || seconds > _waveform.DurationSeconds)
                {
                    continue;
                }

                var bucket = _waveform.BucketAt(seconds);
                var peak = _waveform.PeakAt(bucket);
                if (peak <= 0f)
                {
                    continue;
                }

                var x0 = rect.xMin + column * columnWidth;
                var x1 = x0 + Mathf.Max(1f, columnWidth - 0.5f);

                var peakHalf = Mathf.Max(1f, peak * halfHeight);
                AddQuad(helper, x0, centreY - peakHalf, x1, centreY + peakHalf, peakColour);

                var rms = _waveform.RmsAt(bucket);
                if (rms > 0f)
                {
                    var rmsHalf = Mathf.Max(0.5f, rms * halfHeight);
                    AddQuad(helper, x0, centreY - rmsHalf, x1, centreY + rmsHalf, rmsColour);
                }
            }

            // Cue marker.
            if (_cueSet)
            {
                var x = TimeToX(_cueSeconds, startSeconds, rect);
                if (x >= rect.xMin && x <= rect.xMax)
                {
                    AddQuad(helper, x - 1f, rect.yMin, x + 1f, rect.yMax, Theme.Warning);
                }
            }

            // Playhead, drawn last so it is never hidden by the envelope.
            var playheadX = rect.xMin + rect.width * PlayheadFraction;
            AddQuad(helper, playheadX - 1f, rect.yMin, playheadX + 1f, rect.yMax, Color.white);
        }

        private float TimeToX(double seconds, double startSeconds, Rect rect) =>
            rect.xMin + (float)((seconds - startSeconds) / _windowSeconds) * rect.width;

        private static void AddQuad(VertexHelper helper, float x0, float y0, float x1, float y1, Color color)
        {
            var index = helper.currentVertCount;
            var vertex = UIVertex.simpleVert;
            vertex.color = color;

            vertex.position = new Vector3(x0, y0);
            helper.AddVert(vertex);
            vertex.position = new Vector3(x0, y1);
            helper.AddVert(vertex);
            vertex.position = new Vector3(x1, y1);
            helper.AddVert(vertex);
            vertex.position = new Vector3(x1, y0);
            helper.AddVert(vertex);

            helper.AddTriangle(index, index + 1, index + 2);
            helper.AddTriangle(index, index + 2, index + 3);
        }

        public static WaveformView Create(string name, Transform parent, Color accent)
        {
            var rect = UiFactory.Create(name, parent);
            var view = rect.gameObject.AddComponent<WaveformView>();
            view.raycastTarget = false;
            view.Accent = accent;
            return view;
        }
    }
}
