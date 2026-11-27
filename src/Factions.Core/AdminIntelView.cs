using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>
    /// The admins who chose to see every faction exactly on the Factions page (docs/fog-of-war.md →
    /// The Factions page). Off for everyone until they turn it on with <c>!factions adminintel on</c>:
    /// admins play the game too, and an admin who has not asked sees the same estimates as any player.
    ///
    /// <para>Persisted as <c>admin_intel.json</c> on the authority, which answers every faction-intel
    /// request, so the choice survives restarts and holds on every shard. Holds only those who turned
    /// it on.</para>
    /// </summary>
    public sealed class AdminIntelView
    {
        public const string FileName = "admin_intel.json";

        private readonly HashSet<ulong> _on = new HashSet<ulong>();

        public int Count { get { return _on.Count; } }

        public bool IsOn(ulong steam) { return steam != 0UL && _on.Contains(steam); }

        /// <summary>True when this changed what is stored.</summary>
        public bool Set(ulong steam, bool on)
        {
            if (steam == 0UL) return false;
            return on ? _on.Add(steam) : _on.Remove(steam);
        }

        private sealed class Doc
        {
            public List<string> On { get; set; }
        }

        public string ToJson()
        {
            var ids = new List<ulong>(_on);
            ids.Sort();
            var doc = new Doc { On = new List<string>(ids.Count) };
            foreach (ulong id in ids) doc.On.Add(id.ToString(CultureInfo.InvariantCulture));
            return JsonConvert.SerializeObject(doc, Formatting.Indented);
        }

        public static AdminIntelView FromJson(string json)
        {
            var v = new AdminIntelView();
            Doc doc = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<Doc>(json);
            if (doc == null || doc.On == null) return v;
            foreach (string s in doc.On)
            {
                ulong steam;
                if (ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out steam)) v.Set(steam, true);
            }
            return v;
        }
    }
}
