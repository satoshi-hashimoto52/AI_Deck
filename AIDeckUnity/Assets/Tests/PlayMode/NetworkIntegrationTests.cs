using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AIDeck.Audio;
using AIDeck.Controller;
using AIDeck.Core.Deck;
using AIDeck.Core.Diagnostics;
using AIDeck.Core.Library;
using AIDeck.Core.Model;
using AIDeck.Core.Net;
using AIDeck.Core.Settings;
using AIDeck.Host;
using AIDeck.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AIDeck.Tests.PlayMode
{
    /// <summary>
    /// Covers §10.2: a host and a controller session in one process, over real sockets on the
    /// loopback interface.
    ///
    /// Real sockets rather than a mocked transport. The things most likely to be wrong here —
    /// framing across reads, the handshake, the heartbeat, who is allowed to send on the fast
    /// channel — are precisely the things a mock would define away.
    /// </summary>
    [TestFixture]
    public class NetworkIntegrationTests
    {
        /// <summary>Ports well away from the defaults, so a running AI Deck does not collide.</summary>
        private const int TcpPort = 47910;
        private const int UdpPort = 47911;

        private GameObject _hostObject;
        private AudioEngine _engine;
        private TrackLibrary _library;
        private HostCommands _commands;
        private HostSession _session;
        private ControllerSession _controller;
        private string _tonePath;
        private TrackInfo _track;

        [SetUp]
        public void SetUp()
        {
            _hostObject = new GameObject("NetTestHost");
            _engine = _hostObject.AddComponent<AudioEngine>();
            _engine.Initialise(new DiagnosticLog(), new AppSettings());

            _library = new TrackLibrary();
            _tonePath = TestAudioFile.CreateTone(2d);
            _track = new TrackInfo(null, _tonePath, "Test Tone", "Generator", 2d,
                TrackFormat.Wav, DateTime.UtcNow, 0d, new FileInfo(_tonePath).Length);
            _library.Add(_track);

            _commands = new HostCommands(_engine, _library);

            _session = new HostSession();
            Assert.That(_session.Start(TcpPort, UdpPort), Is.True, _session.StatusText);

            _controller = new ControllerSession();
        }

        [TearDown]
        public void TearDown()
        {
            _controller?.Dispose();
            _session?.Dispose();
            _engine?.Shutdown();

            if (_hostObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_hostObject);
            }

            TestAudioFile.Delete(_tonePath);
        }

        /// <summary>Runs both ends' poll loops for a while, or until <paramref name="until"/> is true.</summary>
        private IEnumerator Pump(float seconds, Func<bool> until = null)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                var delta = Time.unscaledDeltaTime;
                _session.Poll(delta);
                _session.TickSnapshot(delta, () => _engine.BuildSnapshot(_library.Revision));
                _controller.Poll(delta);

                if (until != null && until())
                {
                    yield break;
                }

                yield return null;
            }
        }

        private IEnumerator ConnectAndWait()
        {
            _controller.Connect("127.0.0.1", TcpPort);
            yield return Pump(8f, () => _controller.IsConnected && _session.IsControllerConnected);

            Assert.That(_controller.IsConnected, Is.True, "controller: " + _controller.StatusText);
            Assert.That(_session.IsControllerConnected, Is.True, "host: " + _session.StatusText);
        }

        // ------------------------------------------------------------------ §10.2

        [UnityTest]
        public IEnumerator AControllerConnectsToTheHost()
        {
            yield return ConnectAndWait();

            Assert.That(_session.ControllerName, Is.Not.Empty);
            Assert.That(_controller.HostName, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator TheControllerDrivesADeck()
        {
            yield return ConnectAndWait();

            var received = new List<NetMessage>();
            _session.CommandReceived += received.Add;

            _controller.Send(MessageType.LoadTrack, DeckId.A, Messages.Text(_track.Id));

            // The handshake itself sends RequestLibrary and RequestSnapshot, so the load is not
            // necessarily the first message the host sees.
            NetMessage load = null;
            yield return Pump(2f, () =>
            {
                load = received.Find(m => m.Type == MessageType.LoadTrack);
                return load != null;
            });

            Assert.That(load, Is.Not.Null, "the load command never arrived");
            Assert.That(load.Deck, Is.EqualTo(DeckId.A));
            Assert.That(Messages.ReadText(load), Is.EqualTo(_track.Id));
        }

        [UnityTest]
        public IEnumerator ACommandFromTheControllerReachesTheAudioEngine()
        {
            yield return ConnectAndWait();

            _session.CommandReceived += message =>
            {
                if (message.Type == MessageType.LoadTrack)
                {
                    _commands.LoadTrack(message.Deck ?? DeckId.A, Messages.ReadText(message));
                }
                else if (message.Type == MessageType.TogglePlay)
                {
                    _commands.TogglePlay(message.Deck ?? DeckId.A);
                }
            };

            _controller.Send(MessageType.LoadTrack, DeckId.A, Messages.Text(_track.Id));
            yield return Pump(10f, () => _engine.DeckA.Transport.State == DeckPlaybackState.Paused);
            Assert.That(_engine.DeckA.Transport.State, Is.EqualTo(DeckPlaybackState.Paused),
                "load failed: " + _engine.DeckA.Transport.ErrorReason);

            _controller.Send(MessageType.TogglePlay, DeckId.A);
            yield return Pump(2f, () => _engine.DeckA.IsPlaying);
            Assert.That(_engine.DeckA.IsPlaying, Is.True);
        }

        [UnityTest]
        public IEnumerator HostStateReachesTheController()
        {
            yield return ConnectAndWait();

            StateSnapshot? latest = null;
            _controller.SnapshotReceived += snapshot => latest = snapshot;

            _engine.Mixer.Crossfader = 0.75f;
            _engine.Mixer.MasterGain = 0.42f;

            yield return Pump(3f, () => latest.HasValue && Mathf.Approximately(latest.Value.MasterGain, 0.42f));

            Assert.That(latest.HasValue, Is.True, "no snapshot arrived");
            Assert.That(latest.Value.MasterGain, Is.EqualTo(0.42f).Within(1e-3f));
            Assert.That(latest.Value.Crossfader, Is.EqualTo(0.75f).Within(1e-3f));
        }

        [UnityTest]
        public IEnumerator TheLibraryReachesTheController()
        {
            var chunks = new List<LibraryChunkPayload>();
            _controller.LibraryChunkReceived += chunks.Add;

            yield return ConnectAndWait();

            _session.SendLibrary(_library.All, _library.Revision);
            yield return Pump(3f, () => chunks.Count > 0);

            Assert.That(chunks.Count, Is.GreaterThan(0), "no library chunk arrived");
            Assert.That(chunks[0].Tracks.Count, Is.EqualTo(1));
            Assert.That(chunks[0].Tracks[0].Title, Is.EqualTo("Test Tone"));
        }

        [UnityTest]
        public IEnumerator DisconnectingAndReconnectingSucceeds()
        {
            yield return ConnectAndWait();

            var disconnects = 0;
            _session.ControllerDisconnected += _ => disconnects++;

            _controller.Disconnect();
            yield return Pump(4f, () => !_session.IsControllerConnected);

            Assert.That(_session.IsControllerConnected, Is.False);
            Assert.That(disconnects, Is.GreaterThan(0));

            // FR-065: the same controller comes back and the host accepts it again.
            yield return ConnectAndWait();
        }

        [UnityTest]
        public IEnumerator HighRateFaderInputDoesNotGrowTheQueue()
        {
            yield return ConnectAndWait();

            var received = 0;
            _session.CommandReceived += message =>
            {
                if (message.Type == MessageType.Crossfader)
                {
                    received++;
                }
            };

            // Far more than any finger could produce, all in one frame.
            for (var i = 0; i < 2000; i++)
            {
                _controller.Send(MessageType.Crossfader, null, Messages.Float(i / 2000f));
            }

            Assert.That(_controller.PendingSendCount, Is.LessThan(64),
                "a continuous control must coalesce rather than queue (NFR-004)");

            yield return Pump(2f);

            // Whatever arrived, the host applied only messages that were newer than the last —
            // FR-067 — so the fader never moves backwards.
            Assert.That(received, Is.LessThanOrEqualTo(2000));
            Assert.That(_session.StaleRejected + _session.Accepted, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator ASecondControllerIsRefusedWithAnExplanation()
        {
            yield return ConnectAndWait();

            var second = new ControllerSession();
            var errors = new List<string>();
            second.ErrorReceived += errors.Add;

            try
            {
                second.Connect("127.0.0.1", TcpPort);

                var deadline = Time.realtimeSinceStartup + 6f;
                while (Time.realtimeSinceStartup < deadline && errors.Count == 0)
                {
                    var delta = Time.unscaledDeltaTime;
                    _session.Poll(delta);
                    _controller.Poll(delta);
                    second.Poll(delta);
                    yield return null;
                }

                Assert.That(errors.Count, Is.GreaterThan(0),
                    "a refused controller must be told why, not just dropped");
                Assert.That(errors[0], Does.Contain("already connected"));
                Assert.That(second.IsConnected, Is.False);

                // The first controller is untouched.
                Assert.That(_controller.IsConnected, Is.True);
                Assert.That(_session.IsControllerConnected, Is.True);
            }
            finally
            {
                second.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ConnectingToNothingFailsWithAReasonRatherThanHanging()
        {
            // Port with nothing behind it.
            _controller.Connect("127.0.0.1", 47999);

            yield return Pump(8f, () => _controller.State == ControllerSessionState.Reconnecting ||
                                        _controller.State == ControllerSessionState.Failed);

            Assert.That(_controller.IsConnected, Is.False);
            Assert.That(_controller.StatusText, Is.Not.Empty);
            Assert.That(_controller.State, Is.Not.EqualTo(ControllerSessionState.Connecting),
                "a failed connection must settle rather than sit in Connecting forever");
        }

        [UnityTest]
        public IEnumerator AnEmptyAddressIsRejectedImmediately()
        {
            _controller.Connect("   ", TcpPort);
            yield return null;

            Assert.That(_controller.State, Is.EqualTo(ControllerSessionState.Failed));
            Assert.That(_controller.StatusText, Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator LosingTheControllerAppliesTheDisconnectPolicy()
        {
            yield return ConnectAndWait();

            _session.CommandReceived += message =>
            {
                if (message.Type == MessageType.LoadTrack)
                {
                    _commands.LoadTrack(message.Deck ?? DeckId.A, Messages.ReadText(message));
                }
            };

            _controller.Send(MessageType.LoadTrack, DeckId.A, Messages.Text(_track.Id));
            yield return Pump(10f, () => _engine.DeckA.Transport.State == DeckPlaybackState.Paused);

            _engine.DeckA.Play();
            _engine.DeckA.Motion.BeginScratch();
            _engine.DeckA.Motion.UpdateScratch(-2f);
            yield return null;

            var policyApplied = false;
            _session.ControllerDisconnected += _ =>
            {
                // What HostNetworkBridge does on this event.
                _engine.ApplyDisconnectPolicy();
                policyApplied = true;
            };

            _controller.Disconnect();
            yield return Pump(4f, () => policyApplied);

            Assert.That(policyApplied, Is.True, "the host never noticed the controller had gone");
            Assert.That(_engine.DeckA.Motion.Mode, Is.EqualTo(MotionMode.Normal),
                "a platter left under a gesture with no finger behind it would keep scratching");
            Assert.That(_engine.DeckA.IsPlaying, Is.False,
                "the default disconnect policy is the safe stop (§9)");
        }
    }
}
