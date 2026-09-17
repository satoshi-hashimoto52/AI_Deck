using AIDeck.Controller;
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

            Role = AppRoleResolver.Resolve();
            switch (Role)
            {
                case AppRole.Controller:
                    StartController();
                    break;
                case AppRole.ControllerLocal:
                    StartControllerAgainstLocalHost();
                    break;
                default:
                    StartHost();
                    break;
            }
        }

        /// <summary>The role this process started in.</summary>
        public AppRole Role { get; private set; }

        public HostApp Host { get; private set; }

        public ControllerApp ControllerUi { get; private set; }

        private HostApp StartHost()
        {
            var host = new GameObject("AI Deck Host");
            host.transform.SetParent(transform, false);
            Host = host.AddComponent<HostApp>();
            return Host;
        }

        private void StartController()
        {
            var controller = new GameObject("AI Deck Controller");
            controller.transform.SetParent(transform, false);
            ControllerUi = controller.AddComponent<ControllerApp>();

            // The network session lands in Phase 3. Until then the controller comes up on its
            // connection sheet and says plainly that it cannot connect yet, rather than
            // pretending to be a host or showing a blank screen.
            ControllerUi.Initialise(new DisconnectedBackend(
                "The network link arrives in Phase 3, so this build cannot reach a Mac yet."));
        }

        private void StartControllerAgainstLocalHost()
        {
            var host = StartHost();
            var controller = new GameObject("AI Deck Controller");
            controller.transform.SetParent(transform, false);
            ControllerUi = controller.AddComponent<ControllerApp>();
            ControllerUi.Initialise(new LocalHostBackend(host), host.Log, host.Settings);

            // Both would otherwise draw their canvases over each other.
            host.Screen.SetVisible(false);
        }

        /// <summary>Creates the bootstrap object. Used by the scene and by the PlayMode tests.</summary>
        public static AppBootstrap Spawn()
        {
            var go = new GameObject("AI Deck");
            return go.AddComponent<AppBootstrap>();
        }
    }
}
