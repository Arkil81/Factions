using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FactionsInstaller.Logic
{
    public enum PulsarLauncher { None, Legacy, Interim, Modern }

    /// <summary>What a Space Engineers launch-options string says about Pulsar.</summary>
    public sealed class LaunchOptionsInfo
    {
        public string Raw;
        /// <summary>The Pulsar executable named before %command%, unquoted; null if none.</summary>
        public string PulsarExe;
        public PulsarLauncher Launcher;
        public bool HasCommand;
        /// <summary>Something other than a Pulsar executable stands before %command%.</summary>
        public bool OtherWrapper;
        /// <summary>-useHome: Pulsar keeps its data under %AppData%\Pulsar instead of beside the exe.</summary>
        public bool UseHome;
        /// <summary>-profile &lt;name&gt;: Pulsar loads that profile instead of Current.</summary>
        public string Profile;

        internal int ExeStart, ExeLength;
    }

    /// <summary>
    /// Steam starts the game as the launch options with <c>%command%</c> replaced by the game's
    /// executable, so "<c>"…\Legacy.exe" %command% -x</c>" runs Pulsar with the game path and -x.
    /// Options without %command% are plain arguments appended to the game.
    /// </summary>
    public static class LaunchOptions
    {
        public const string Command = "%command%";

        private static readonly Regex PulsarExe = new Regex(
            "\"(?<p>[^\"]*?(?<l>Legacy|Interim|Modern)\\.exe)\"|(?<p>[^\\s\"]*?(?<l>Legacy|Interim|Modern)\\.exe)(?=\\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static LaunchOptionsInfo Parse(string raw)
        {
            raw = raw ?? "";
            var info = new LaunchOptionsInfo { Raw = raw };
            int cmd = raw.IndexOf(Command, StringComparison.OrdinalIgnoreCase);
            info.HasCommand = cmd >= 0;
            string args = raw;
            if (info.HasCommand)
            {
                string prefix = raw.Substring(0, cmd);
                args = raw.Substring(cmd + Command.Length);
                Match m = PulsarExe.Match(prefix);
                if (m.Success)
                {
                    info.PulsarExe = m.Groups["p"].Value;
                    info.Launcher = (PulsarLauncher)Enum.Parse(typeof(PulsarLauncher), m.Groups["l"].Value, true);
                    info.ExeStart = m.Index;
                    info.ExeLength = m.Length;
                    prefix = prefix.Remove(m.Index, m.Length);
                }
                info.OtherWrapper = prefix.Trim().Length > 0;
            }

            List<string> tokens = Tokenize(args);
            for (int i = 0; i < tokens.Count; i++)
            {
                if (string.Equals(tokens[i], "-useHome", StringComparison.OrdinalIgnoreCase))
                    info.UseHome = true;
                else if (string.Equals(tokens[i], "-profile", StringComparison.OrdinalIgnoreCase))
                    info.Profile = i + 1 < tokens.Count ? tokens[i + 1] : "";
            }
            return info;
        }

        /// <summary>
        /// The options with Pulsar's <paramref name="legacyExe"/> as the launcher, every other
        /// argument kept. Throws when something other than Pulsar already wraps the game.
        /// </summary>
        public static string WithLauncher(string raw, string legacyExe)
        {
            LaunchOptionsInfo info = Parse(raw);
            string quoted = "\"" + legacyExe + "\"";
            if (info.Launcher != PulsarLauncher.None)
            {
                if (info.Launcher == PulsarLauncher.Legacy && string.Equals(info.PulsarExe, legacyExe, StringComparison.OrdinalIgnoreCase))
                    return info.Raw;
                return info.Raw.Substring(0, info.ExeStart) + quoted + info.Raw.Substring(info.ExeStart + info.ExeLength);
            }
            if (info.OtherWrapper)
                throw new InvalidOperationException("the launch options already start the game through another program");
            string trimmed = info.Raw.Trim();
            if (info.HasCommand)
                return quoted + " " + trimmed;
            return trimmed.Length == 0 ? quoted + " " + Command : quoted + " " + Command + " " + trimmed;
        }

        private static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                if (s[i] == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) end = s.Length;
                    list.Add(s.Substring(i + 1, end - i - 1));
                    i = end + 1;
                }
                else
                {
                    int start = i;
                    while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                    list.Add(s.Substring(start, i - start));
                }
            }
            return list;
        }
    }
}
