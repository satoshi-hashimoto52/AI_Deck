using System.Globalization;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.UI
{
    /// <summary>
    /// The mixer column of §5.4: filters, channel faders and meters, cue and mute, the
    /// crossfader, master level and the record button.
    ///
    /// Like <see cref="DeckPanelView"/> it is shared by the Mac and the iPad and holds no
    /// state: it renders a <see cref="StateSnapshot"/> and reports intents.
    /// </summary>
    public sealed class MixerPanelView : MonoBehaviour
    {
        private IDeckCommands _commands;

        private KnobWidget _filterA;
        private KnobWidget _filterB;
        private FaderWidget _gainA;
        private FaderWidget _gainB;
        private MeterView _meterA;
        private MeterView _meterB;
        private ButtonWidget _cueA;
        private ButtonWidget _cueB;
        private ButtonWidget _muteA;
        private ButtonWidget _muteB;
        private FaderWidget _crossfader;
        private FaderWidget _master;
        private Text _masterLabel;
        private MeterView _masterMeter;
        private ButtonWidget _record;
        private Text _recordLabel;

        public static MixerPanelView Create(string name, Transform parent, TouchRouter router, IDeckCommands commands)
        {
            var root = UiFactory.CreatePanel(name, parent);
            var view = root.gameObject.AddComponent<MixerPanelView>();
            view._commands = commands;
            view.Build(root, router);
            return view;
        }

        private void Build(RectTransform root, TouchRouter router)
        {
            _filterA = KnobWidget.Create("FilterA", root, router, DeckId.A);
            _filterA.ValueChanged += v => _commands.SetFilter(DeckId.A, v);
            _filterB = KnobWidget.Create("FilterB", root, router, DeckId.B);
            _filterB.ValueChanged += v => _commands.SetFilter(DeckId.B, v);

            CreateCaption(root, "FILTER A", out _filterACaption);
            CreateCaption(root, "FILTER B", out _filterBCaption);

            _gainA = FaderWidget.Create("GainA", root, router, true, 0.8f, false, DeckId.A);
            _gainA.ValueChanged += v => _commands.SetChannelGain(DeckId.A, v);
            _gainB = FaderWidget.Create("GainB", root, router, true, 0.8f, false, DeckId.B);
            _gainB.ValueChanged += v => _commands.SetChannelGain(DeckId.B, v);

            _meterA = MeterView.Create("MeterA", root);
            _meterB = MeterView.Create("MeterB", root);

            _cueA = ButtonWidget.Create("CueA", root, "CUE A", router, DeckId.A, Theme.FontSizeSmall);
            _cueA.Clicked += () => _commands.SetCueMonitor(DeckId.A, !_cueAOn);
            _muteA = ButtonWidget.Create("MuteA", root, "MUTE A", router, DeckId.A, Theme.FontSizeSmall);
            _muteA.Clicked += () => _commands.SetMute(DeckId.A, !_muteAOn);
            _muteB = ButtonWidget.Create("MuteB", root, "MUTE B", router, DeckId.B, Theme.FontSizeSmall);
            _muteB.Clicked += () => _commands.SetMute(DeckId.B, !_muteBOn);
            _cueB = ButtonWidget.Create("CueB", root, "CUE B", router, DeckId.B, Theme.FontSizeSmall);
            _cueB.Clicked += () => _commands.SetCueMonitor(DeckId.B, !_cueBOn);

            _crossfader = FaderWidget.Create("Crossfader", root, router, false, 0.5f, true);
            _crossfader.ValueChanged += v => _commands.SetCrossfader(v * 2f - 1f);

            // End labels: with the knob near the centre it is otherwise not obvious which way
            // is deck A, and getting that wrong mid-mix is unrecoverable.
            _crossfaderA = UiFactory.CreateText("XfA", root, "A", Theme.FontSizeSmall,
                TextAnchor.MiddleLeft, Theme.DeckA, FontStyle.Bold);
            _crossfaderB = UiFactory.CreateText("XfB", root, "B", Theme.FontSizeSmall,
                TextAnchor.MiddleRight, Theme.DeckB, FontStyle.Bold);

            _master = FaderWidget.Create("Master", root, router, false, 0.8f);
            _master.ValueChanged += v => _commands.SetMasterGain(v);

            _masterLabel = UiFactory.CreateText("MasterLabel", root, "MASTER",
                Theme.FontSizeSmall, TextAnchor.MiddleLeft, Theme.TextDim);
            _masterMeter = MeterView.Create("MasterMeter", root, false);

            _record = ButtonWidget.Create("Record", root, "● REC", router, null, Theme.FontSizeSmall);
            _record.Clicked += () =>
            {
                if (_recording)
                {
                    _commands.StopRecording();
                }
                else
                {
                    _commands.StartRecording();
                }
            };

            _recordLabel = UiFactory.CreateText("RecordLabel", root, string.Empty,
                Theme.FontSizeSmall, TextAnchor.MiddleLeft, Theme.TextDim);
        }

        private Text _filterACaption;
        private Text _filterBCaption;
        private Text _crossfaderA;
        private Text _crossfaderB;
        private bool _muteAOn;
        private bool _muteBOn;
        private bool _cueAOn;
        private bool _cueBOn;
        private bool _recording;

        private void CreateCaption(Transform parent, string text, out Text label) =>
            label = UiFactory.CreateText(text, parent, text, Theme.FontSizeSmall,
                TextAnchor.MiddleCenter, Theme.TextDim);

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

            const float pad = 9f;
            const float gap = 7f;
            var innerWidth = width - pad * 2f;
            var half = (innerWidth - gap) * 0.5f;

            var y = pad;

            // Filter knobs.
            var knob = Mathf.Max(Theme.TouchSize, Mathf.Min(half, 52f));
            UiFactory.Place(_filterA.Rect, pad + (half - knob) * 0.5f, y, knob, knob);
            UiFactory.Place(_filterB.Rect, pad + half + gap + (half - knob) * 0.5f, y, knob, knob);
            UiFactory.Place(_filterACaption.rectTransform, pad, y + knob, half, 12f);
            UiFactory.Place(_filterBCaption.rectTransform, pad + half + gap, y + knob, half, 12f);
            y += knob + 16f;

            // Channel faders with their meters. This block takes whatever height is left over
            // after the fixed rows below, so the faders get the longest throw the panel allows.
            //   cue/mute row + crossfader + master label + master fader row + meter
            //   cue/mute + crossfader + master meter + master label + master row
            var bottomBlock = Theme.TouchSize + 6f + 40f + 8f + 6f + 6f + 14f + 30f + pad;
            var faderHeight = Mathf.Max(70f, height - y - bottomBlock);
            var faderWidth = 34f;
            var meterWidth = 8f;

            var aX = pad + (half - faderWidth - meterWidth - 5f) * 0.5f;
            UiFactory.Place(_gainA.Rect, aX, y, faderWidth, faderHeight);
            UiFactory.Place(_meterA.GetComponent<RectTransform>(), aX + faderWidth + 5f, y, meterWidth, faderHeight);

            var bX = pad + half + gap + (half - faderWidth - meterWidth - 5f) * 0.5f;
            UiFactory.Place(_gainB.Rect, bX, y, faderWidth, faderHeight);
            UiFactory.Place(_meterB.GetComponent<RectTransform>(), bX + faderWidth + 5f, y, meterWidth, faderHeight);
            y += faderHeight + 8f;

            // Cue / mute row.
            var quarter = (innerWidth - gap * 3f) * 0.25f;
            UiFactory.Place(_cueA.Rect, pad, y, quarter, Theme.TouchSize);
            UiFactory.Place(_muteA.Rect, pad + (quarter + gap), y, quarter, Theme.TouchSize);
            UiFactory.Place(_muteB.Rect, pad + (quarter + gap) * 2f, y, quarter, Theme.TouchSize);
            UiFactory.Place(_cueB.Rect, pad + (quarter + gap) * 3f, y, quarter, Theme.TouchSize);
            y += Theme.TouchSize + 6f;

            // Crossfader — the widest, most important control in the panel.
            UiFactory.Place(_crossfader.Rect, pad, y, innerWidth, 40f);
            UiFactory.Place(_crossfaderA.rectTransform, pad + 6f, y, 14f, 40f);
            UiFactory.Place(_crossfaderB.rectTransform, pad + innerWidth - 20f, y, 14f, 40f);
            y += 40f + 8f;

            // Master, top to bottom: the output meter, then the label line, then the fader
            // beside the record button. The meter goes above the controls rather than below so
            // it is never the element pushed against the panel edge, and so a clipping
            // indicator sits in the middle of the panel where it is hard to miss.
            UiFactory.Place(_masterMeter.GetComponent<RectTransform>(), pad, y, innerWidth, 6f);
            y += 6f + 6f;

            UiFactory.Place(_masterLabel.rectTransform, pad, y, innerWidth * 0.55f, 14f);
            UiFactory.Place(_recordLabel.rectTransform, pad + innerWidth * 0.55f, y, innerWidth * 0.45f, 14f);
            _recordLabel.alignment = TextAnchor.MiddleRight;
            y += 14f;

            var recordWidth = 80f;
            var masterWidth = innerWidth - recordWidth - gap;
            UiFactory.Place(_master.Rect, pad, y + 6f, masterWidth, 18f);
            UiFactory.Place(_record.Rect, pad + masterWidth + gap, y, recordWidth, 30f);
        }

        /// <summary>Renders the host's mixer state.</summary>
        public void Apply(StateSnapshot snapshot)
        {
            if (!_filterA.IsBeingDragged)
            {
                _filterA.Value = snapshot.FilterA;
            }

            if (!_filterB.IsBeingDragged)
            {
                _filterB.Value = snapshot.FilterB;
            }

            if (!_gainA.IsBeingDragged)
            {
                _gainA.Value = snapshot.ChannelGainA;
            }

            if (!_gainB.IsBeingDragged)
            {
                _gainB.Value = snapshot.ChannelGainB;
            }

            if (!_crossfader.IsBeingDragged)
            {
                _crossfader.Value = (snapshot.Crossfader + 1f) * 0.5f;
            }

            if (!_master.IsBeingDragged)
            {
                _master.Value = snapshot.MasterGain;
            }

            _meterA.SetLevel(snapshot.DeckA.PeakLevel, false);
            _meterB.SetLevel(snapshot.DeckB.PeakLevel, false);
            _masterMeter.SetLevel(snapshot.MasterPeak, snapshot.MasterClipping);

            _muteAOn = snapshot.MuteA;
            _muteBOn = snapshot.MuteB;
            _cueAOn = snapshot.CueA;
            _cueBOn = snapshot.CueB;

            _muteA.State = _muteAOn ? ButtonVisualState.Active : ButtonVisualState.Normal;
            _muteB.State = _muteBOn ? ButtonVisualState.Active : ButtonVisualState.Normal;
            _cueA.State = _cueAOn ? ButtonVisualState.Active : ButtonVisualState.Normal;
            _cueB.State = _cueBOn ? ButtonVisualState.Active : ButtonVisualState.Normal;

            _recording = snapshot.IsRecording;
            _record.State = _recording ? ButtonVisualState.Error : ButtonVisualState.Normal;

            var masterDb = AudioSafety.LinearToDb(snapshot.MasterGain);
            _masterLabel.text = "MASTER " + masterDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB";

            _recordLabel.text = _recording
                ? "REC " + TrackInfo.FormatDuration(snapshot.RecordingSeconds) +
                  (string.IsNullOrEmpty(snapshot.RecordingFileName) ? string.Empty : " · " + snapshot.RecordingFileName)
                : string.Empty;
            _recordLabel.color = _recording ? Theme.Danger : Theme.TextDim;
        }

        /// <summary>Releases any held control (FR-066, FR-074).</summary>
        public void ReleaseAll()
        {
            _filterA.ForceRelease();
            _filterB.ForceRelease();
            _gainA.ForceRelease();
            _gainB.ForceRelease();
            _crossfader.ForceRelease();
            _master.ForceRelease();
            _cueA.ForceRelease();
            _cueB.ForceRelease();
            _muteA.ForceRelease();
            _muteB.ForceRelease();
            _record.ForceRelease();
        }
    }
}
