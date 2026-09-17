using System;

namespace AIDeck.Host
{
    /// <summary>
    /// Command-line options the Mac host understands.
    ///
    /// These exist because the host has no scriptable interface otherwise, and §10 needs a
    /// repeatable way to bring the application up in a known state — with a library imported
    /// and both decks loaded — so that a build can be checked without a person clicking
    /// through it. They are documented in <c>docs/TEST_PLAN.md</c>.
    ///
    /// <code>
    /// AI Deck.app/Contents/MacOS/AI Deck -aideck-import "~/Music/AI Deck" -aideck-autoload
    /// </code>
    /// </summary>
    public sealed class HostStartupOptions
    {
        public const string ImportArgument = "-aideck-import";
        public const string AutoLoadArgument = "-aideck-autoload";
        public const string AutoPlayArgument = "-aideck-autoplay";

        /// <summary>Folder or file to import at launch, or null.</summary>
        public string ImportPath { get; private set; }

        /// <summary>Load the first two library tracks onto decks A and B once the import finishes.</summary>
        public bool AutoLoad { get; private set; }

        /// <summary>Start both decks playing once they are loaded. Implies <see cref="AutoLoad"/>.</summary>
        public bool AutoPlay { get; private set; }

        public bool HasWork => !string.IsNullOrEmpty(ImportPath) || AutoLoad || AutoPlay;

        public static HostStartupOptions Parse(string[] args)
        {
            var options = new HostStartupOptions();
            if (args == null)
            {
                return options;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], ImportArgument, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.ImportPath = args[i + 1];
                    i++;
                }
                else if (string.Equals(args[i], AutoLoadArgument, StringComparison.OrdinalIgnoreCase))
                {
                    options.AutoLoad = true;
                }
                else if (string.Equals(args[i], AutoPlayArgument, StringComparison.OrdinalIgnoreCase))
                {
                    options.AutoPlay = true;
                    options.AutoLoad = true;
                }
            }

            return options;
        }

        public static HostStartupOptions FromEnvironment()
        {
            try
            {
                return Parse(Environment.GetCommandLineArgs());
            }
            catch (Exception)
            {
                // A sandboxed player can refuse the command line; no options is a fine answer.
                return new HostStartupOptions();
            }
        }
    }
}
