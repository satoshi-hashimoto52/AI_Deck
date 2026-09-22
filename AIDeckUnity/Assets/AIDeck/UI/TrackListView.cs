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
        private RectTransform _viewport;
        private Image _scrollTrack;
        private Image _scrollHandle;

        /// <summary>Width of the scroll indicator, in reference points.</summary>
        private const float ScrollBarWidth = 4f;

        /// <summary>
        /// How far a finger may travel on a load button before it counts as a scroll.
        ///
        /// The same figure <see cref="RowHitArea"/> uses to tell a tap from a drag, so the two
        /// cannot disagree about which gesture happened.
        /// </summary>
        private const float RowDragTolerance = 10f;

        /// <summary>Pixels of list movement per notch of the wheel.</summary>
        private const float WheelStep = 48f;

        /// <summary>How far down the list we are, in pixels. Zero is the first track.</summary>
        private float _scrollOffset;
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

            // No ScrollRect. It needs the EventSystem to deliver scroll and drag, and this
            // control surface deliberately does not use the EventSystem — TouchRouter reads
            // input directly so that several fingers work at once and a cancelled touch can
            // release what it held (FR-071, FR-073). The ScrollRect that used to be here was
            // wired to a viewport with no raycast target and to rows that swallowed their own
            // drag, so nothing ever reached it and the list could not be scrolled at all.
            //
            // Driving the content offset from the router keeps one input path for the whole
            // surface, and makes the behaviour reachable from a PlayMode test.
            _viewport = UiFactory.Create("Viewport", scrollRoot);
            _viewport.gameObject.AddComponent<RectMask2D>();

            _content = UiFactory.Create("Content", _viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.sizeDelta = new Vector2(0f, 0f);

            BuildScrollBar(scrollRoot);

            _emptyLabel = UiFactory.CreateText(
                "Empty", scrollRoot, "No tracks yet.", Theme.FontSizeBody,
                TextAnchor.MiddleCenter, Theme.TextDim);
        }

        /// <summary>A thin indicator, so it is obvious there is more list below the fold.</summary>
        private void BuildScrollBar(RectTransform scrollRoot)
        {
            _scrollTrack = UiFactory.CreateImage("ScrollTrack", scrollRoot, Theme.Line);
            var track = _scrollTrack.rectTransform;
            track.anchorMin = new Vector2(1f, 0f);
            track.anchorMax = new Vector2(1f, 1f);
            track.pivot = new Vector2(1f, 1f);
            track.offsetMin = new Vector2(-ScrollBarWidth, 2f);
            track.offsetMax = new Vector2(0f, -2f);
            _scrollTrack.raycastTarget = false;

            _scrollHandle = UiFactory.CreateImage("ScrollHandle", track, Theme.KnobEdge);
            var handle = _scrollHandle.rectTransform;
            handle.anchorMin = new Vector2(0f, 1f);
            handle.anchorMax = new Vector2(1f, 1f);
            handle.pivot = new Vector2(0.5f, 1f);
            _scrollHandle.raycastTarget = false;
        }

        /// <summary>
        /// Sets the search text as if it had been typed.
        ///
        /// Goes through the field so the box on screen and the filter can never disagree, and
        /// so the reset-to-top that a new search performs happens here too.
        /// </summary>
        public void SetQuery(string query)
        {
            if (_search != null)
            {
                _search.text = query ?? string.Empty;   // raises onValueChanged
                return;
            }

            OnSearchChanged(query);
        }

        private void OnSearchChanged(string value)
        {
            _query = value ?? string.Empty;
            QueryChanged?.Invoke(_query);

            // A new search shows a different list; staying at the old offset would open it
            // somewhere in the middle, or past the end of a shorter result.
            _scrollOffset = 0f;
            Refresh();
        }

        // ---------------------------------------------------------------- scrolling

        /// <summary>Height of the list that is off-screen. Zero when everything fits.</summary>
        public float MaxScroll =>
            Mathf.Max(0f, _visible.Count * RowHeight - ViewportHeight);

        private float ViewportHeight =>
            _viewport == null ? 0f : _viewport.rect.height;

        /// <summary>How far down the list we are, in pixels. Zero is the first track.</summary>
        public float ScrollOffset => _scrollOffset;

        /// <summary>Moves the list by a number of pixels. Positive reveals later tracks.</summary>
        public void ScrollBy(float pixels) => ApplyScroll(_scrollOffset + pixels);

        /// <summary>Back to the first track.</summary>
        public void ScrollToTop() => ApplyScroll(0f);

        /// <summary>
        /// One wheel or trackpad notch.
        ///
        /// Public so a test can drive it: <c>Input.mouseScrollDelta</c> cannot be set, and the
        /// thing worth testing is that a notch moves the list, not that Unity reports notches.
        ///
        /// The sign is uGUI's, deliberately: <c>ScrollRect.OnScroll</c> does
        /// <c>anchoredPosition += -scrollDelta.y * sensitivity</c>, so turning a wheel or
        /// pushing a trackpad here moves the library the same way it moves every other scroll
        /// view in the app — including the generation sheet, a few pixels to the right. Copying
        /// the convention is the only way to get it right on a platform where the natural
        /// scrolling setting can invert what the hardware reports.
        /// </summary>
        public void ScrollByWheel(float notches) => ScrollBy(-notches * WheelStep);

        private void ApplyScroll(float offset)
        {
            // Clamped rather than elastic: overscrolling a library list past its ends only
            // ever shows empty space where tracks should be.
            _scrollOffset = Mathf.Clamp(offset, 0f, MaxScroll);

            if (_content != null)
            {
                _content.anchoredPosition = new Vector2(_content.anchoredPosition.x, _scrollOffset);
            }

            UpdateScrollBar();
        }

        private void UpdateScrollBar()
        {
            if (_scrollTrack == null || _scrollHandle == null)
            {
                return;
            }

            var contentHeight = _visible.Count * RowHeight;
            var viewport = ViewportHeight;
            var scrollable = MaxScroll > 0.5f && viewport > 1f;

            _scrollTrack.gameObject.SetActive(scrollable);
            if (!scrollable)
            {
                return;
            }

            var trackHeight = _scrollTrack.rectTransform.rect.height;
            var handleHeight = Mathf.Max(24f, trackHeight * (viewport / contentHeight));
            var travel = trackHeight - handleHeight;

            var handle = _scrollHandle.rectTransform;
            handle.sizeDelta = new Vector2(0f, handleHeight);
            handle.anchoredPosition = new Vector2(0f, -travel * (_scrollOffset / MaxScroll));
        }

        /// <summary>
        /// Wheel and trackpad, read straight from <c>Input</c> like the rest of this surface.
        ///
        /// Only when the pointer is actually over the list, so turning the wheel above a deck
        /// does not move the library, and only while the window has focus — the same rule the
        /// router applies, for the same reason.
        /// </summary>
        private void Update()
        {
            if (_viewport == null || _router == null || !_router.HasFocus)
            {
                return;
            }

            var notches = Input.mouseScrollDelta.y;
            if (Mathf.Approximately(notches, 0f))
            {
                return;
            }

            if (RectTransformUtility.RectangleContainsScreenPoint(
                    _viewport, Input.mousePosition, _router.UiCamera))
            {
                ScrollByWheel(notches);
            }
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

            // Clamped after every rebuild: removing tracks, or narrowing a search, can leave
            // the offset pointing past the end, and an empty list scrolled to nowhere is how a
            // library looks like it has lost everything.
            ApplyScroll(_scrollOffset);

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

            // Clip rather than overflow: an over-long line would otherwise run underneath the
            // load buttons and look like a rendering fault.
            row.Meta.horizontalOverflow = HorizontalWrapMode.Wrap;
            row.Meta.verticalOverflow = VerticalWrapMode.Truncate;
            row.Title.horizontalOverflow = HorizontalWrapMode.Wrap;
            row.Title.verticalOverflow = VerticalWrapMode.Truncate;

            // A transparent hit area behind the labels turns the whole row into a tap target.
            // It sits at a lower priority than the load buttons, which overlap it.
            row.Hit = rootRect.gameObject.AddComponent<RowHitArea>();
            row.Hit.TouchPriority = 0;
            row.Hit.Bind(_router);

            // The row is what the finger is on, so the row is what reports the scroll. This is
            // the whole Mac-drag and iPad-one-finger path: the router gives the press to the
            // row, and the row hands the movement on instead of dropping it.
            row.Hit.DraggedBy += ScrollBy;
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

            // A drag that happens to begin on A or B is still a scroll. Without this the
            // buttons cover most of the row's right-hand side and starting there would either
            // do nothing or, worse, load a track on release.
            foreach (var button in new[] { row.LoadA, row.LoadB })
            {
                button.DragCancelDistance = RowDragTolerance;
                button.DraggedBy += ScrollBy;
            }

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

            // Built from whatever is actually known. Padding the line with "Unknown" costs
            // space that the duration and format need, and tells the user nothing.
            var bpm = track.HasBpm
                ? track.Bpm.ToString("0.0", CultureInfo.InvariantCulture) + " BPM"
                : "— BPM";
            var artist = string.Equals(track.Artist, TrackInfo.UnknownArtist, StringComparison.Ordinal)
                ? null
                : track.Artist;
            row.Meta.text = artist == null
                ? $"{bpm} · {track.DurationDisplay} · {FormatName(track.Format)}"
                : $"{artist} · {bpm} · {track.DurationDisplay} · {FormatName(track.Format)}";

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
