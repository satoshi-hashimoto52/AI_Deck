using System;
using System.Collections.Generic;
using AIDeck.Core.Analysis;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// The browse and waveform area of §5.2: the library on the left, the status line and both
    /// scrolling waveforms on the right.
    ///
    /// Shared by the Mac and the iPad. The status line is where the connection state, the
    /// recording state and any notice from the host appear (FR-051, FR-062).
    /// </summary>
    public sealed class BrowserView : MonoBehaviour
    {
        private const float LibraryWidth = 300f;
        private const float StatusHeight = 24f;

        private TrackListView _library;
        private Text _status;
        private Text _notice;
        private RectTransform _wavePanel;
        private readonly WaveformStrip[] _strips = new WaveformStrip[2];

        /// <summary>Raised with the track and target deck (FR-020, FR-021).</summary>
        public event Action<TrackInfo, DeckId> LoadRequested;

        public TrackListView Library => _library;

        private sealed class WaveformStrip
        {
            public Text Header;
            public WaveformView Wave;
            public Text Elapsed;
            public Text Remaining;
        }

        public static BrowserView Create(string name, Transform parent, TouchRouter router)
        {
            var root = UiFactory.Create(name, parent);
            var view = root.gameObject.AddComponent<BrowserView>();
            view.Build(root, router);
            return view;
        }

        private void Build(RectTransform root, TouchRouter router)
        {
            var libraryPanel = UiFactory.CreatePanel("LibraryPanel", root);
            _library = TrackListView.Create("Library", libraryPanel, router);
            UiFactory.Stretch(_library.GetComponent<RectTransform>(), 8f, 8f, 8f, 8f);
            _library.LoadRequested += (track, deck) => LoadRequested?.Invoke(track, deck);

            _wavePanel = UiFactory.CreatePanel("WavePanel", root);

            _status = UiFactory.CreateText("Status", _wavePanel, "Starting…",
                Theme.FontSizeLabel, TextAnchor.MiddleLeft, Theme.TextDim);
            _notice = UiFactory.CreateText("Notice", _wavePanel, string.Empty,
                Theme.FontSizeLabel, TextAnchor.MiddleRight, Theme.Warning);

            for (var i = 0; i < 2; i++)
            {
                var deck = (DeckId)i;
                var strip = new WaveformStrip
                {
                    Header = UiFactory.CreateText($"Header{deck}", _wavePanel, deck.ToDisplayName(),
                        Theme.FontSizeLabel, TextAnchor.MiddleLeft, Theme.Accent(deck), FontStyle.Bold),
                    Wave = WaveformView.Create($"Wave{deck}", _wavePanel, Theme.Accent(deck)),
                    Elapsed = UiFactory.CreateText($"Elapsed{deck}", _wavePanel, "0:00",
                        Theme.FontSizeLabel, TextAnchor.MiddleRight, Theme.TextDim),
                    Remaining = UiFactory.CreateText($"Remaining{deck}", _wavePanel, "-0:00",
                        Theme.FontSizeLabel, TextAnchor.MiddleRight, Theme.TextDim)
                };

                _strips[i] = strip;
            }
        }

        private void Start() => Layout();

        private void OnRectTransformDimensionsChange() => Layout();

        private void Layout()
        {
            var rect = (RectTransform)transform;
            var width = rect.rect.width;
            var height = rect.rect.height;
            if (width <= 1f || height <= 1f)
            {
                return;
            }

            var libraryWidth = Mathf.Min(LibraryWidth, width * 0.4f);
            var libraryPanel = (RectTransform)transform.Find("LibraryPanel");
            UiFactory.Place(libraryPanel, 0f, 0f, libraryWidth, height);

            var waveX = libraryWidth + Theme.Gap;
            var waveWidth = width - waveX;
            UiFactory.Place(_wavePanel, waveX, 0f, waveWidth, height);

            const float pad = 10f;
            var innerWidth = waveWidth - pad * 2f;

            UiFactory.Place(_status.rectTransform, pad, 4f, innerWidth * 0.6f, StatusHeight);
            UiFactory.Place(_notice.rectTransform, pad + innerWidth * 0.6f, 4f, innerWidth * 0.4f, StatusHeight);

            var stripTop = StatusHeight + 8f;
            var stripHeight = (height - stripTop - pad) * 0.5f;

            for (var i = 0; i < 2; i++)
            {
                var strip = _strips[i];
                var y = stripTop + i * stripHeight;

                UiFactory.Place(strip.Header.rectTransform, pad, y, 120f, 16f);
                UiFactory.Place(strip.Elapsed.rectTransform, pad + innerWidth - 180f, y, 88f, 16f);
                UiFactory.Place(strip.Remaining.rectTransform, pad + innerWidth - 88f, y, 88f, 16f);
                UiFactory.Place(strip.Wave.rectTransform, pad, y + 18f, innerWidth, Mathf.Max(20f, stripHeight - 24f));
            }
        }

        /// <summary>Renders both waveform strips from the host state.</summary>
        public void Apply(StateSnapshot snapshot)
        {
            ApplyStrip(0, snapshot.DeckA);
            ApplyStrip(1, snapshot.DeckB);
            _library.SetLoadedTracks(snapshot.DeckA.TrackId, snapshot.DeckB.TrackId);
            _notice.text = snapshot.Notice ?? string.Empty;
        }

        private void ApplyStrip(int index, DeckSnapshot deck)
        {
            var strip = _strips[index];
            strip.Wave.SetPosition(deck.PositionSeconds);
            strip.Wave.SetLoop(deck.LoopActive, deck.LoopInSeconds, deck.LoopOutSeconds);
            strip.Wave.SetCue(deck.CueIsSet, deck.CueSeconds);
            strip.Elapsed.text = TrackInfo.FormatDuration(deck.PositionSeconds);
            strip.Remaining.text = "-" + TrackInfo.FormatDuration(deck.RemainingSeconds);
        }

        /// <summary>Sets the title shown beside a deck's waveform.</summary>
        public void SetDeckTitle(DeckId deck, string title)
        {
            var strip = _strips[(int)deck];
            strip.Header.text = string.IsNullOrEmpty(title)
                ? deck.ToDisplayName()
                : deck.ToDisplayName() + "  " + title;
        }

        public void SetWaveform(DeckId deck, WaveformData waveform) =>
            _strips[(int)deck].Wave.SetWaveform(waveform);

        public bool HasWaveform(DeckId deck) => _strips[(int)deck].Wave.HasWaveform;

        /// <summary>The single status line: connection, recording and device information.</summary>
        public void SetStatus(string text, Color? color = null)
        {
            _status.text = text ?? string.Empty;
            _status.color = color ?? Theme.TextDim;
        }

        public void SetTracks(IReadOnlyList<TrackInfo> tracks) => _library.SetTracks(tracks);
    }
}
