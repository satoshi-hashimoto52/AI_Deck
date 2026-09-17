using AIDeck.Host;
using UnityEngine;

namespace AIDeck.App
{
    /// <summary>
    /// The only object in the scene.
    ///
    /// Everything else — canvases, widgets, the audio graph — is built from code at runtime,
    /// so the scene file stays a few lines that nobody has to merge. See
    /// <c>docs/ARCHITECTURE.md</c> §3.
    /// </summary>
    public sealed class AppBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            Application.targetFrameRate = 60;

            // A DJ set is not a place for the screen to dim (FR-076). The controller re-checks
            // this against the user setting once it knows what is playing.
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            var role = AppRoleResolver.Resolve();
            switch (role)
            {
                case AppRole.Controller:
                    StartController();
                    break;
                default:
                    StartHost();
                    break;
            }
        }

        private void StartHost()
        {
            var host = new GameObject("AI Deck Host");
            host.transform.SetParent(transform, false);
            host.AddComponent<HostApp>();
        }

        private void StartController()
        {
            // The controller application lands in Phase 2. Until then, a device that would run
            // it starts the host so the build is never a blank screen, and says so.
            Debug.Log("[AI Deck] Controller role requested; the controller UI arrives in Phase 2.");
            StartHost();
        }

        /// <summary>Creates the bootstrap object. Used by the scene and by the PlayMode tests.</summary>
        public static AppBootstrap Spawn()
        {
            var go = new GameObject("AI Deck");
            return go.AddComponent<AppBootstrap>();
        }
    }
}
