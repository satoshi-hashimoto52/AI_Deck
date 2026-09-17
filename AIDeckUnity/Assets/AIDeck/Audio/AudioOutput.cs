using AIDeck.Core.Audio;
using UnityEngine;

namespace AIDeck.Audio
{
    /// <summary>
    /// The one place AI Deck's audio reaches Unity.
    ///
    /// Sits on the <see cref="AudioListener"/> object, so <c>OnAudioFilterRead</c> receives
    /// the final mix buffer every DSP block. AI Deck renders its own decks into that buffer
    /// rather than using <see cref="AudioSource"/> playback — see <see cref="DeckVoice"/> for
    /// why — so the buffer is overwritten rather than added to.
    ///
    /// Everything this calls lives in <c>AIDeck.Core</c>, which cannot reference UnityEngine.
    /// That is what keeps the audio-thread rules of NFR-001 checkable rather than merely
    /// intended: there is no Unity API in reach to accidentally call.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public sealed class AudioOutput : MonoBehaviour
    {
        private DeckChannel _deckA;
        private DeckChannel _deckB;
        private MasterBus _master;
        private volatile bool _active;

        /// <summary>Blocks rendered since the last reset. Used by the tests to confirm the graph is alive.</summary>
        private long _blocksRendered;

        public long BlocksRendered => System.Threading.Interlocked.Read(ref _blocksRendered);

        /// <summary>
        /// Wires the bus in. Called from the main thread before the graph is made active.
        /// </summary>
        public void Attach(DeckChannel deckA, DeckChannel deckB, MasterBus master)
        {
            _active = false;
            _deckA = deckA;
            _deckB = deckB;
            _master = master;
            _active = deckA != null && deckB != null && master != null;
        }

        /// <summary>
        /// Stops the callback contributing anything. Used on shutdown so no audio survives the
        /// engine being torn down (NFR-010).
        /// </summary>
        public void Detach() => _active = false;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_active)
            {
                // Silence rather than whatever the keep-alive source left behind.
                System.Array.Clear(data, 0, data.Length);
                return;
            }

            var master = _master;
            var deckA = _deckA;
            var deckB = _deckB;
            if (master == null || deckA == null || deckB == null)
            {
                System.Array.Clear(data, 0, data.Length);
                return;
            }

            System.Array.Clear(data, 0, data.Length);

            var sampleRate = master.SampleRate;
            deckA.RenderInto(data, channels, sampleRate);
            deckB.RenderInto(data, channels, sampleRate);
            master.Process(data, channels);

            System.Threading.Interlocked.Increment(ref _blocksRendered);
        }

        private void OnDisable() => _active = false;
    }
}
