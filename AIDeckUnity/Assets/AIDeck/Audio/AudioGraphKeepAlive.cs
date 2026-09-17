using UnityEngine;

namespace AIDeck.Audio
{
    /// <summary>
    /// Keeps Unity's DSP graph running when nothing is playing.
    ///
    /// <c>OnAudioFilterRead</c> on the listener is only guaranteed to be called while the
    /// audio graph is active. AI Deck generates all of its audio inside that callback, so if
    /// the graph idles when no <see cref="AudioSource"/> is playing, the decks go silent and
    /// — worse — the gain ramps and echo tails stop advancing, which would leave a deck
    /// stuck part-way through a fade.
    ///
    /// A silent looping source on a **child** object keeps the graph alive for a negligible
    /// cost. It has to be a child: a source on the listener's own object would take over that
    /// object's filter chain, and <see cref="AudioOutput"/> would receive the source's output
    /// instead of the final mix.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class AudioGraphKeepAlive : MonoBehaviour
    {
        private AudioSource _source;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();

            // One second of silence, looped. Created in code so the repository carries no
            // binary audio asset.
            var clip = AudioClip.Create("AIDeckKeepAlive", 4096, 1, AudioSettings.outputSampleRate, false);
            clip.SetData(new float[4096], 0);

            _source.clip = clip;
            _source.loop = true;
            _source.volume = 0f;
            _source.spatialBlend = 0f;
            _source.playOnAwake = false;
            _source.bypassEffects = true;
            _source.bypassListenerEffects = false;
            _source.Play();
        }

        private void OnDestroy()
        {
            if (_source != null && _source.clip != null)
            {
                Destroy(_source.clip);
            }
        }
    }
}
