using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIDeck.Core.Model;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// The library list of §5.2: search box, scrolling rows, and a 44 pt load button per deck.
    ///
    /// Rows are pooled rather than rebuilt. A library of a few hundred tracks rebuilt on every
    /// state snapshot would allocate constantly and stutter the scroll; pooling means a
    /// refresh only rewrites the text of the rows that already exist.
    /// </summary>
    public sealed class TrackListView : MonoBehaviour
    {
        private const float RowHeight = 58f;
        private const float SearchHeight = 34f;

        private readonly List<Row> _rows = new List<Row>();
        private readonly List<TrackInfo> _visible = new List<TrackInfo>();

        private RectTransform _content;
        private InputField _search;
        private Text _emptyLabel;
        private TouchRouter _router;

        private IReadOnlyList<TrackInfo> _source = Array.Empty<TrackInfo>();
        private string _query = string.Empty;
        private string _deckATrackId = string.Empty;
        private string _deckBTrackId = string.Empty;

        /// <summary>Raised with the track and the deck to load it onto (FR-020, FR-021).</summary>
        public event Action<TrackInfo, DeckId> LoadRequested;

        /// <summary>Raised when the search text changes (FR-011).</summary>
        public event Action<string> QueryChanged;

        /// <summary>Raised when a row is tapped outside its load buttons.</summary>
        public event Action<TrackInfo> TrackSelected;

        /// <summary>
        /// The row the user last tapped, or null. The Mac UI acts on this for removal (§5.6);
        /// the controller ignores it, because the iPad has no remove control.
        /// </summary>
        public TrackInfo SelectedTrack { get; private set; }

        public string Query => _query;

        public int VisibleCount => _visible.Count;

        private sealed class Row
        {
            public RectTransform Root;
            public Image Background;
            public Text Title;
            public Text Meta;
            public ButtonWidget LoadA;
            public ButtonWidget LoadB;
            public RowHitArea Hit;
            public TrackInfo Track;
        }

        public static TrackListView Create(string name, Transform parent, TouchRouter router)
        {
            var root = UiFactory.Create(name, parent);
            var view = root.gameObject.AddComponent<TrackListView>();
            view._router = router;
            view.Build(root);
            return view;
        }

        private void Build(RectTransform root)
        {
            var search = UiFactory.Create("Search", root);
            search.anchorMin = new Vector2(0f, 1f);
            search.anchorMax = new Vector2(1f, 1f);
            search.pivot = new Vector2(0.5f, 1f);
            search.sizeDelta = new Vector2(0f, SearchHeight);
            search.anchoredPosition = Vector2.zero;

            var searchBackground = search.gameObject.AddComponent<Image>();
            searchBackground.color = Theme.PanelRaised;
            var searchOutline = search.gameObject.AddComponent<Outline>();
            searchOutline.effectColor = Theme.Line;
            searchOutline.effectDistance = new Vector2(1f, 1f);
            searchOutline.useGraphicAlpha = false;

            var searchText = UiFactory.CreateText("Text", search, string.Empty, Theme.FontSizeBody);
            UiFactory.Stretch(searchText.rectTransform, 10f, 0f, 10f, 0f);
            searchText.supportRichText = false;

            var placeholder = UiFactory.CreateText(
                "Placeholder", search, "Search title, artist or file…", Theme.FontSizeBody,
                TextAnchor.MiddleLeft, Theme.TextDim);
            UiFactory.Stretch(placeholder.rectTransform, 10f, 0f, 10f, 0f);

            _search = search.gameObject.AddComponent<InputField>();
            _search.textComponent = searchText;
            _search.placeholder = placeholder;
            _search.lineType = InputField.LineType.SingleLine;
            _search.onValueChanged.AddListener(OnSearchChanged);

            var scrollRoot = UiFactory.Create("Scroll", root);
            UiFactory.Stretch(scrollRoot, 0f, SearchHeight + 6f, 0f, 0f);

            var scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.scrollSensitivity = 24f;

            var viewport = UiFactory.Create("Viewport", scrollRoot);
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll.viewport = viewport;

            _content = UiFactory.Create("Content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.sizeDelta = new Vector2(0f, 0f);
            scroll.content = _content;

            _emptyLabel = UiFactory.CreateText(
                "Empty", scrollRoot, "No tracks yet.", Theme.FontSizeBody,
                TextAnchor.MiddleCenter, Theme.TextDim);
        }

        private void OnSearchChanged(string value)
        {
            _query = value ?? string.Empty;
            QueryChanged?.Invoke(_query);
            Refresh();
        }

        /// <summary>Replaces the backing list. Call whenever the library revision changes.</summary>
        public void SetTracks(IReadOnlyList<TrackInfo> tracks)
        {
            _source = tracks ?? Array.Empty<TrackInfo>();

            // A selection that is no longer in the library would let the remove button act on
            // a track that is already gone.
            if (SelectedTrack != null && !_source.Contains(SelectedTrack))
            {
                SelectedTrack = null;
            }

            Refresh();
        }

        public void ClearSelection()
        {
            SelectedTrack = null;
            for (var i = 0; i < _rows.Count; i++)
            {
                ApplyRowTint(_rows[i]);
            }
        }

        /// <summary>
        /// Marks which tracks are on which deck, so a loaded track is visibly tinted with the
        /// deck colour (§5.1: "ロード済みデッキと再生中デッキを明確に色分け").
        /// </summary>
        public void SetLoadedTracks(string deckATrackId, string deckBTrackId)
        {
            var a = deckATrackId ?? string.Empty;
            var b = deckBTrackId ?? string.Empty;
            if (a == _deckATrackId && b == _deckBTrackId)
            {
                return;
            }

            _deckATrackId = a;
            _deckBTrackId = b;
            for (var i = 0; i < _rows.Count; i++)
            {
                ApplyRowTint(_rows[i]);
            }
        }

        private void Refresh()
        {
            _visible.Clear();
            for (var i = 0; i < _source.Count; i++)
            {
                var track = _source[i];
                if (track != null && track.Matches(_query))
                {
                    _visible.Add(track);
                }
            }

            while (_rows.Count < _visible.Count)
            {
                _rows.Add(CreateRow(_rows.Count));
            }

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (i < _visible.Count)
                {
                    Bind(row, _visible[i], i);
                    row.Root.gameObject.SetActive(true);
                }
                else
                {
                    row.Track = null;
                    row.Root.gameObject.SetActive(false);
                }
            }

            _content.sizeDelta = new Vector2(0f, _visible.Count * RowHeight);
            _emptyLabel.gameObject.SetActive(_visible.Count == 0);
            _emptyLabel.text = _source.Count == 0
                ? "No tracks yet. Add MP3, WAV or AIFF files."
                : "No tracks match the search.";
        }

        private Row CreateRow(int index)
        {
            var rootRect = UiFactory.Create("Row", _content);
            var row = new Row { Root = rootRect };

            row.Background = rootRect.gameObject.AddComponent<Image>();
            row.Background.sprite = UiSprites.Rounded;
            row.Background.type = Image.Type.Sliced;
            row.Background.color = Color.clear;
            row.Background.raycastTarget = false;

            row.Title = UiFactory.CreateText("Title", rootRect, string.Empty,
                Theme.FontSizeBody, TextAnchor.LowerLeft, Theme.Text, FontStyle.Bold);
            row.Meta = UiFactory.CreateText("Meta", rootRect, string.Empty,
                Theme.FontSizeLabel, TextAnchor.UpperLeft, Theme.TextDim);

            // A transparent hit area behind the labels turns the whole row into a tap target.
            // It sits at a lower priority than the load buttons, which overlap it.
            row.Hit = rootRect.gameObject.AddComponent<RowHitArea>();
            row.Hit.TouchPriority = 0;
            row.Hit.Bind(_router);
            row.Hit.Tapped += () =>
            {
                if (row.Track == null)
                {
                    return;
                }

                SelectedTrack = row.Track;
                foreach (var other in _rows)
                {
                    ApplyRowTint(other);
                }

                TrackSelected?.Invoke(row.Track);
            };

            row.LoadA = ButtonWidget.Create("LoadA", rootRect, "A", _router, DeckId.A, Theme.FontSizeBody);
            row.LoadB = ButtonWidget.Create("LoadB", rootRect, "B", _router, DeckId.B, Theme.FontSizeBody);

            // The load buttons sit on top of the row, so they must win the hit test.
            row.LoadA.TouchPriority = 10;
            row.LoadB.TouchPriority = 10;

            row.LoadA.Clicked += () =>
            {
                if (row.Track != null)
                {
                    LoadRequested?.Invoke(row.Track, DeckId.A);
                }
            };

            row.LoadB.Clicked += () =>
            {
                if (row.Track != null)
                {
                    LoadRequested?.Invoke(row.Track, DeckId.B);
                }
            };

            LayoutRow(row, index);
            return row;
        }

        private void LayoutRow(Row row, int index)
        {
            // Top-anchored and width-stretched: the row spans the content's width and steps
            // down by one row height per index. Setting the anchors before the position is
            // what makes anchoredPosition mean "offset from the top edge".
            row.Root.anchorMin = new Vector2(0f, 1f);
            row.Root.anchorMax = new Vector2(1f, 1f);
            row.Root.pivot = new Vector2(0.5f, 1f);
            row.Root.sizeDelta = new Vector2(0f, RowHeight);
            row.Root.anchoredPosition = new Vector2(0f, -index * RowHeight);

            var buttons = Theme.TouchSize * 2f + 14f;

            UiFactory.Stretch(row.Title.rectTransform, 10f, 8f, buttons + 16f, RowHeight * 0.5f);
            UiFactory.Stretch(row.Meta.rectTransform, 10f, RowHeight * 0.5f, buttons + 16f, 8f);

            var a = row.LoadA.Rect;
            a.anchorMin = new Vector2(1f, 0.5f);
            a.anchorMax = new Vector2(1f, 0.5f);
            a.pivot = new Vector2(1f, 0.5f);
            a.sizeDelta = new Vector2(Theme.TouchSize, Theme.TouchSize);
            a.anchoredPosition = new Vector2(-(Theme.TouchSize + 12f), 0f);

            var b = row.LoadB.Rect;
            b.anchorMin = new Vector2(1f, 0.5f);
            b.anchorMax = new Vector2(1f, 0.5f);
            b.pivot = new Vector2(1f, 0.5f);
            b.sizeDelta = new Vector2(Theme.TouchSize, Theme.TouchSize);
            b.anchoredPosition = new Vector2(-6f, 0f);
        }

        private void Bind(Row row, TrackInfo track, int index)
        {
            row.Track = track;
            LayoutRow(row, index);

            row.Title.text = track.Title;

            var bpm = track.HasBpm
                ? track.Bpm.ToString("0.0", CultureInfo.InvariantCulture) + " BPM"
                : "— BPM";
            row.Meta.text = $"{track.Artist} · {bpm} · {track.DurationDisplay} · {FormatName(track.Format)}";

            ApplyRowTint(row);
        }

        private void ApplyRowTint(Row row)
        {
            if (row?.Track == null)
            {
                row?.Background?.SetAllDirty();
                return;
            }

            if (SelectedTrack != null && string.Equals(row.Track.Id, SelectedTrack.Id, StringComparison.Ordinal))
            {
                row.Background.color = Theme.WithAlpha(Theme.PressedEdge, 0.55f);
            }
            else if (string.Equals(row.Track.Id, _deckATrackId, StringComparison.Ordinal))
            {
                row.Background.color = Theme.DeckASoft;
            }
            else if (string.Equals(row.Track.Id, _deckBTrackId, StringComparison.Ordinal))
            {
                row.Background.color = Theme.DeckBSoft;
            }
            else
            {
                row.Background.color = Color.clear;
            }
        }

        private static string FormatName(TrackFormat format)
        {
            switch (format)
            {
                case TrackFormat.Mp3: return "MP3";
                case TrackFormat.Wav: return "WAV";
                case TrackFormat.Aiff: return "AIFF";
                default: return "—";
            }
        }
    }
}
