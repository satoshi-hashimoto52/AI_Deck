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

        private NetworkBackend _networkBackend;

        private void OnDestroy() => _networkBackend?.Dispose();

        private void OnApplicationQuit() => _networkBackend?.Dispose();

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

            _networkBackend = new NetworkBackend();
            ControllerUi.Initialise(_networkBackend);

            // If the address from the last session is still valid, reconnect without making the
            // user type it again (FR-080). Discovery runs in parallel, so a Mac that has moved
            // still appears in the list.
            var settings = ControllerUi.Settings;
            if (!settings.LastHostAddress.Equals(string.Empty, System.StringComparison.Ordinal))
            {
                _networkBackend.Connect(settings.LastHostAddress, settings.LastHostPort);
            }
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
