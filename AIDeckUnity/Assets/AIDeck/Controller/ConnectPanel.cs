using System;
using System.Collections.Generic;
using AIDeck.Core.Net;
using AIDeck.UI;
using UnityEngine;
using UnityEngine.UI;

namespace AIDeck.Controller
{
    /// <summary>
    /// The connection sheet (FR-060, FR-061, FR-062).
    ///
    /// Covers the control surface whenever there is no link, rather than sitting in a corner.
    /// Operating a DJ control that is not connected to anything is worse than being told you
    /// cannot: the controls would move, nothing would happen, and the reason would not be
    /// obvious.
    ///
    /// Discovery and manual entry are both offered from the start. Discovery fails on any
    /// network with client isolation — guest Wi-Fi, many mesh systems — and on those the typed
    /// address is the only way in, so it is not hidden behind a "having trouble?" link.
    /// </summary>
    public sealed class ConnectPanel : MonoBehaviour
    {
        private const float RowHeight = 52f;
        private const int MaxRows = 4;

        private RectTransform _card;
        private Text _title;
        private Text _status;
        private Text _hostsLabel;
        private readonly List<ButtonWidget> _hostButtons = new List<ButtonWidget>();
        private InputField _addressField;
        private ButtonWidget _connectButton;
        private ButtonWidget _retryButton;
        private Text _manualLabel;
        private TouchRouter _router;

        /// <summary>Raised with the address and port the user chose.</summary>
        public event Action<string, int> ConnectRequested;

        /// <summary>Raised when the user asks to search again.</summary>
        public event Action RetryRequested;

        public static ConnectPanel Create(string name, Transform parent, TouchRouter router, string lastAddress)
        {
            var root = UiFactory.Create(name, parent);
            var panel = root.gameObject.AddComponent<ConnectPanel>();
            panel._router = router;
            panel.Build(root, lastAddress);
            return panel;
        }

        private void Build(RectTransform root, string lastAddress)
        {
            // A scrim, so the controls behind are visibly out of reach rather than merely dim.
            var scrim = UiFactory.CreateImage("Scrim", root, new Color(0.03f, 0.035f, 0.05f, 0.96f));
            scrim.raycastTarget = false;

            _card = UiFactory.CreatePanel("Card", root, Theme.Panel, Theme.Line);

            _title = UiFactory.CreateText("Title", _card, "Connect to AI Deck on your Mac",
                Theme.FontSizeLarge, TextAnchor.MiddleCenter, Theme.Text, FontStyle.Bold);

            _status = UiFactory.CreateText("Status", _card, "Searching…",
                Theme.FontSizeBody, TextAnchor.UpperCenter, Theme.TextDim);
            // The status can be a full sentence; let it wrap inside the card rather than run
            // off both edges.
            _status.horizontalOverflow = HorizontalWrapMode.Wrap;
            _status.verticalOverflow = VerticalWrapMode.Truncate;

            _hostsLabel = UiFactory.CreateText("HostsLabel", _card, "Found on this network",
                Theme.FontSizeSmall, TextAnchor.MiddleLeft, Theme.TextDim);

            for (var i = 0; i < MaxRows; i++)
            {
                var button = ButtonWidget.Create($"Host{i}", _card, string.Empty, _router, null, Theme.FontSizeBody);
                var index = i;
                button.Clicked += () => OnHostChosen(index);
                button.gameObject.SetActive(false);
                _hostButtons.Add(button);
            }

            var field = UiFactory.Create("Address", _card);
            var fieldEdge = field.gameObject.AddComponent<Image>();
            fieldEdge.sprite = UiSprites.Rounded;
            fieldEdge.type = Image.Type.Sliced;
            fieldEdge.color = Theme.Line;
            fieldEdge.raycastTarget = false;

            var fill = UiFactory.Create("Fill", field);
            UiFactory.Stretch(fill, 1f, 1f, 1f, 1f);
            var fillImage = fill.gameObject.AddComponent<Image>();
            fillImage.sprite = UiSprites.Rounded;
            fillImage.type = Image.Type.Sliced;
            fillImage.color = Theme.PanelRaised;
            fillImage.raycastTarget = false;

            var fieldText = UiFactory.CreateText("Text", field, string.Empty, Theme.FontSizeBody);
            UiFactory.Stretch(fieldText.rectTransform, 12f, 0f, 12f, 0f);
            var placeholder = UiFactory.CreateText("Placeholder", field, "Mac IP address, e.g. 192.168.1.42",
                Theme.FontSizeBody, TextAnchor.MiddleLeft, Theme.TextDim);
            UiFactory.Stretch(placeholder.rectTransform, 12f, 0f, 12f, 0f);

            _addressField = field.gameObject.AddComponent<InputField>();
            _addressField.textComponent = fieldText;
            _addressField.placeholder = placeholder;
            _addressField.lineType = InputField.LineType.SingleLine;
            _addressField.contentType = InputField.ContentType.Standard;
            _addressField.keyboardType = TouchScreenKeyboardType.NumbersAndPunctuation;
            _addressField.text = lastAddress ?? string.Empty;
            _addressField.onSubmit.AddListener(_ => SubmitAddress());

            _connectButton = ButtonWidget.Create("Connect", _card, "CONNECT", _router, null, Theme.FontSizeBody);
            _connectButton.Clicked += SubmitAddress;

            _retryButton = ButtonWidget.Create("Retry", _card, "SEARCH AGAIN", _router, null, Theme.FontSizeSmall);
            _retryButton.Clicked += () => RetryRequested?.Invoke();

            _manualLabel = UiFactory.CreateText("ManualLabel", _card, "or enter the address shown on the Mac",
                Theme.FontSizeSmall, TextAnchor.MiddleLeft, Theme.TextDim);

            // The sheet sits above everything else on the canvas.
            foreach (var button in _hostButtons)
            {
                button.TouchPriority = 100;
            }

            _connectButton.TouchPriority = 100;
            _retryButton.TouchPriority = 100;
        }

        private readonly List<DiscoveredHost> _hosts = new List<DiscoveredHost>();

        private void OnHostChosen(int index)
        {
            if (index < 0 || index >= _hosts.Count)
            {
                return;
            }

            var host = _hosts[index];
            ConnectRequested?.Invoke(host.Address, host.Port);
        }

        private void SubmitAddress()
        {
            var address = (_addressField.text ?? string.Empty).Trim();
            if (address.Length == 0)
            {
                return;
            }

            // An address may carry an explicit port: 192.168.1.42:47810.
            var port = ProtocolInfo.DefaultTcpPort;
            var colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out var parsed) &&
                parsed > 0 && parsed <= 65535)
            {
                port = parsed;
                address = address.Substring(0, colon);
            }

            ConnectRequested?.Invoke(address, port);
        }

        public string Address => _addressField != null ? _addressField.text : string.Empty;

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

            const float pad = 22f;
            const float statusHeight = 46f;
            const float gap = 10f;

            // The card is sized to its contents. A fixed height leaves a hole in the middle
            // when discovery has found nothing, which reads as a broken layout rather than as
            // an empty list.
            var rowsHeight = _hosts.Count * (RowHeight + 6f);
            var cardHeight = pad + 30f + statusHeight + 20f + rowsHeight + 38f + gap
                             + 18f + Theme.TouchSize + pad;
            cardHeight = Mathf.Min(cardHeight, height - 40f);

            var cardWidth = Mathf.Min(560f, width - 80f);
            _card.anchorMin = new Vector2(0.5f, 0.5f);
            _card.anchorMax = new Vector2(0.5f, 0.5f);
            _card.pivot = new Vector2(0.5f, 0.5f);
            _card.sizeDelta = new Vector2(cardWidth, cardHeight);
            _card.anchoredPosition = Vector2.zero;

            var inner = cardWidth - pad * 2f;
            var y = pad;

            UiFactory.Place(_title.rectTransform, pad, y, inner, 28f);
            y += 30f;

            UiFactory.Place(_status.rectTransform, pad, y, inner, statusHeight);
            y += statusHeight;

            UiFactory.Place(_hostsLabel.rectTransform, pad, y, inner, 16f);
            y += 20f;

            for (var i = 0; i < _hostButtons.Count; i++)
            {
                UiFactory.Place(_hostButtons[i].Rect, pad, y + i * (RowHeight + 6f), inner, RowHeight);
            }

            y += rowsHeight;

            UiFactory.Place(_retryButton.Rect, pad, y, inner, 34f);
            y += 34f + gap;

            UiFactory.Place(_manualLabel.rectTransform, pad, y, inner, 16f);
            y += 18f;

            UiFactory.Place((RectTransform)_addressField.transform, pad, y, inner - 150f, Theme.TouchSize);
            UiFactory.Place(_connectButton.Rect, pad + inner - 142f, y, 142f, Theme.TouchSize);
        }

        /// <summary>Renders the current connection state.</summary>
        public void Apply(ConnectionState state, string statusText, IReadOnlyList<DiscoveredHost> hosts)
        {
            _status.text = statusText ?? string.Empty;
            _status.color = state == ConnectionState.Failed ? Theme.Danger
                : state == ConnectionState.Connecting || state == ConnectionState.Reconnecting ? Theme.Warning
                : Theme.TextDim;

            _hosts.Clear();
            if (hosts != null)
            {
                for (var i = 0; i < hosts.Count && i < MaxRows; i++)
                {
                    _hosts.Add(hosts[i]);
                }
            }

            _hostsLabel.text = _hosts.Count > 0
                ? "Found on this network"
                : "No Mac found yet — you can type its address below";

            for (var i = 0; i < _hostButtons.Count; i++)
            {
                var active = i < _hosts.Count;
                _hostButtons[i].gameObject.SetActive(active);
                if (active)
                {
                    var host = _hosts[i];
                    _hostButtons[i].Label = string.IsNullOrEmpty(host.Name)
                        ? host.Address
                        : $"{host.Name}   {host.Address}";
                }
            }

            var busy = state == ConnectionState.Connecting || state == ConnectionState.Reconnecting;
            _connectButton.State = busy ? ButtonVisualState.Waiting : ButtonVisualState.Normal;
            _retryButton.State = state == ConnectionState.Searching ? ButtonVisualState.Waiting : ButtonVisualState.Normal;

            Layout();
        }

        public void ReleaseAll()
        {
            foreach (var button in _hostButtons)
            {
                button.ForceRelease();
            }

            _connectButton.ForceRelease();
            _retryButton.ForceRelease();
        }
    }
}
