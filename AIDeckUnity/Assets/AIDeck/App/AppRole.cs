using System;
using UnityEngine;

namespace AIDeck.App
{
    /// <summary>Which half of AI Deck this process is.</summary>
    public enum AppRole
    {
        /// <summary>Mac: library, audio, mixing, recording. The authority.</summary>
        Host,

        /// <summary>iPad: the control surface.</summary>
        Controller,

        /// <summary>
        /// Both halves in one process: a host with its window hidden, driven by the controller
        /// UI. The development and verification path for the iPad layout, and the shape the
        /// integration tests use. See <c>docs/TEST_PLAN.md</c>.
        /// </summary>
        ControllerLocal
    }

    /// <summary>
    /// Decides which role to start in.
    ///
    /// Platform is the normal answer, but the command line overrides it. That override is not
    /// a debug convenience: running a controller against a host on one Mac is how the
    /// integration tests of §10.2 drive a session without a second device.
    /// </summary>
    public static class AppRoleResolver
    {
        public const string Argument = "-aideck-role";

        public static AppRole Resolve()
        {
            var fromCommandLine = FromCommandLine();
            if (fromCommandLine.HasValue)
            {
                return fromCommandLine.Value;
            }

            return Application.platform == RuntimePlatform.IPhonePlayer
                ? AppRole.Controller
                : AppRole.Host;
        }

        /// <summary>Reads <c>-aideck-role host|controller</c>, or null when absent.</summary>
        public static AppRole? FromCommandLine()
        {
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception)
            {
                // Sandboxed players can refuse this; the platform default is then used.
                return null;
            }

            for (var i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], Argument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = args[i + 1];
                if (string.Equals(value, "host", StringComparison.OrdinalIgnoreCase))
                {
                    return AppRole.Host;
                }

                if (string.Equals(value, "controller", StringComparison.OrdinalIgnoreCase))
                {
                    return AppRole.Controller;
                }

                if (string.Equals(value, "controller-local", StringComparison.OrdinalIgnoreCase))
                {
                    return AppRole.ControllerLocal;
                }
            }

            return null;
        }
    }
}
