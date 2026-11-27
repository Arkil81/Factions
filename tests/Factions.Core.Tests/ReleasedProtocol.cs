using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The wire protocol released to players is the one on <c>origin/main</c>: production and the public repo are cut
    /// from it. <see cref="FactionsWire.ProtocolVersion"/> may move at most once past that number, however many wire
    /// changes land before the next release. The test server and <c>C:\Torch\FactionsUI-dev</c> move together on every
    /// deploy, so a second bump inside one release window protects no client.
    /// </summary>
    public static class ReleasedProtocol
    {
        private const string MainRef = "origin/main";
        private const string WireFile = "src/Factions.Wire/FactionsWire.cs";
        private static readonly Regex Constant = new Regex(@"public const int ProtocolVersion\s*=\s*(\d+)");

        private static bool _read;
        private static int? _released;
        private static string _why;

        /// <summary>The <see cref="FactionsWire.ProtocolVersion"/> on <c>origin/main</c>, or null when this checkout
        /// cannot read it (no git, no <c>.git</c>, no fetched <c>origin/main</c>).</summary>
        public static int? Released
        {
            get { Read(); return _released; }
        }

        /// <summary>Why <see cref="Released"/> is null.</summary>
        public static string Unreadable
        {
            get { Read(); return _why; }
        }

        /// <summary>Null when <paramref name="current"/> is allowed against <paramref name="released"/>; otherwise
        /// what is wrong with it.</summary>
        public static string Verdict(int released, int current)
        {
            if (current == released || current == released + 1) return null;
            if (current < released)
                return "ProtocolVersion " + current + " is behind " + MainRef + "'s " + released + ".";
            return "ProtocolVersion " + current + " is " + (current - released) + " ahead of " + MainRef + "'s "
                   + released + ". Nothing between them was released, so one bump covers every wire change in "
                   + "this window: set it to " + (released + 1) + ".";
        }

        private static void Read()
        {
            if (_read) return;
            _read = true;
            try
            {
                string root = TestSupport.RepoFile(".");
                var psi = new ProcessStartInfo("git", "-C \"" + root + "\" show " + MainRef + ":" + WireFile)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using (Process p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) { _why = "git timed out reading " + MainRef + "."; return; }
                    if (p.ExitCode != 0) { _why = "git could not read " + MainRef + ":" + WireFile + ": " + err.Trim(); return; }
                    Match m = Constant.Match(text);
                    if (!m.Success) { _why = MainRef + "'s " + WireFile + " has no ProtocolVersion constant."; return; }
                    _released = int.Parse(m.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                _why = "git is not available: " + ex.Message;
            }
        }
    }

    /// <summary>A fact that needs <see cref="ReleasedProtocol.Released"/>. It reports as skipped, with the reason,
    /// where the checkout cannot read <c>origin/main</c> (a copied tree without <c>.git</c>), rather than passing.</summary>
    public sealed class ReleasedProtocolFactAttribute : FactAttribute
    {
        public ReleasedProtocolFactAttribute()
        {
            if (ReleasedProtocol.Released == null) Skip = ReleasedProtocol.Unreadable;
        }
    }

    public class ReleasedProtocolTests
    {
        [Fact]
        public void Verdict_AllowsTheReleasedNumberAndOneBump_AndRefusesASecond()
        {
            Assert.Null(ReleasedProtocol.Verdict(64, 64));
            Assert.Null(ReleasedProtocol.Verdict(64, 65));
            Assert.Contains("set it to 65", ReleasedProtocol.Verdict(64, 66));
            Assert.Contains("behind", ReleasedProtocol.Verdict(64, 63));
        }
    }
}
