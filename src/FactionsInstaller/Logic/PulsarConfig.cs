using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FactionsInstaller.Logic
{
    /// <summary>The Factions UI identity, as FactionsUI.xml in the public repository states it.</summary>
    public static class FactionsUi
    {
        public const string PluginId = "7B1F4C2E-9A3D-4E58-8C71-0D6B2A5F3E94";
        public const string Repo = "Arkil81/Factions";
        public const string Branch = "main";
        public const string File = "FactionsUI.xml";
        public const string SourceName = "Factions";
    }

    /// <summary>The result of editing one config file: the new text, and what changed in words.</summary>
    public sealed class ConfigEdit
    {
        public string Text;
        public bool Changed;
        public readonly List<string> Changes = new List<string>();
    }

    /// <summary>
    /// Edits Pulsar's XmlSerializer files: <c>Sources\sources.xml</c> (SourcesConfig) and
    /// <c>Profiles\Current.xml</c> (Profile). Elements are written in the classes' declaration
    /// order, and anything this does not own is carried over unchanged.
    /// A Current.xml Pulsar cannot read is moved aside by Pulsar with every enabled plugin reset,
    /// so an unreadable input is refused here rather than rewritten.
    /// </summary>
    public static class PulsarConfig
    {
        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

        private static readonly string[] SourcesOrder =
            { "ShowWarning", "MaxSourceAge", "LocalHubSources", "RemoteHubSources", "RemotePluginSources", "LocalPluginSources", "ModSources" };
        private static readonly string[] ProfileOrder = { "Name", "GitHub", "DevFolder", "Local", "Mods" };

        // ---- sources.xml ----------------------------------------------------------------

        /// <summary>
        /// Adds the Factions UI remote-plugin source, or repairs an existing one (wrong file, wrong
        /// branch, disabled). A hub entry for the same repository is removed: it would offer the
        /// same plugin a second time. <paramref name="existing"/> null means Pulsar has not written
        /// the file yet, and then the official hub Pulsar would have defaulted to is written too.
        /// </summary>
        public static ConfigEdit AddSource(string existing)
        {
            var edit = new ConfigEdit();
            XDocument doc;
            if (existing == null)
            {
                doc = NewDocument("SourcesConfig");
                XElement r = doc.Root;
                r.Add(new XElement("ShowWarning", "true"));
                r.Add(new XElement("MaxSourceAge", "2"));
                r.Add(new XElement("LocalHubSources"));
                r.Add(new XElement("RemoteHubSources",
                    new XElement("RemoteHub",
                        new XElement("Name", "PluginHub"),
                        new XElement("Repo", "StarCpt/PluginHub"),
                        new XElement("Branch", "main"),
                        new XElement("Enabled", "true"),
                        new XElement("Trusted", "true"))));
                r.Add(new XElement("RemotePluginSources"));
                r.Add(new XElement("LocalPluginSources"));
                r.Add(new XElement("ModSources"));
                edit.Changes.Add("create Pulsar's sources.xml, with its default plugin hub");
            }
            else
            {
                doc = Load(existing, "SourcesConfig", "sources.xml");
            }
            XElement root = doc.Root;

            XElement hubs = root.Element("RemoteHubSources");
            if (hubs != null)
            {
                foreach (XElement hub in hubs.Elements("RemoteHub").Where(IsOurs).ToList())
                {
                    hub.Remove();
                    edit.Changes.Add("remove the Factions hub entry, which offers the plugin a second time");
                }
            }

            XElement plugins = EnsureChild(root, "RemotePluginSources", SourcesOrder);
            List<XElement> ours = plugins.Elements("RemotePlugin").Where(IsOurs).ToList();
            foreach (XElement extra in ours.Skip(1))
            {
                extra.Remove();
                edit.Changes.Add("remove a duplicate Factions plugin source");
            }

            XElement src = ours.FirstOrDefault();
            if (src == null)
            {
                plugins.Add(new XElement("RemotePlugin",
                    new XElement("Name", FactionsUi.SourceName),
                    new XElement("Repo", FactionsUi.Repo),
                    new XElement("Branch", FactionsUi.Branch),
                    new XElement("File", FactionsUi.File),
                    new XElement("Enabled", "true"),
                    new XElement("Trusted", "false")));
                edit.Changes.Add("add the Factions UI plugin source (github.com/" + FactionsUi.Repo + ", " + FactionsUi.File + ")");
            }
            else
            {
                bool repaired = false;
                repaired |= SetValue(src, "Branch", FactionsUi.Branch, edit, "branch");
                repaired |= SetValue(src, "File", FactionsUi.File, edit, "metadata file");
                repaired |= SetValue(src, "Enabled", "true", edit, "enabled flag");
                if (repaired)
                {
                    // A source checked within the last two hours is served from Pulsar's cached copy
                    // of the old metadata file; clearing the stamp makes it fetch the right one.
                    XElement last = src.Element("LastCheck");
                    if (last != null) last.Remove();
                }
            }

            return Finish(doc, existing, edit);
        }

        /// <summary>Removes the Factions UI source and the Factions hub entry, if present.</summary>
        public static ConfigEdit RemoveSource(string existing)
        {
            var edit = new ConfigEdit();
            if (existing == null) { edit.Text = null; return edit; }
            XDocument doc = Load(existing, "SourcesConfig", "sources.xml");
            foreach (string list in new[] { "RemotePluginSources", "RemoteHubSources" })
            {
                XElement e = doc.Root.Element(list);
                if (e == null) continue;
                foreach (XElement s in e.Elements().Where(IsOurs).ToList())
                {
                    s.Remove();
                    edit.Changes.Add("remove the Factions plugin source");
                }
            }
            return Finish(doc, existing, edit);
        }

        private static bool IsOurs(XElement source)
        {
            return string.Equals(((string)source.Element("Repo") ?? "").Trim(), FactionsUi.Repo, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SetValue(XElement parent, string name, string value, ConfigEdit edit, string what)
        {
            XElement e = parent.Element(name);
            string current = e == null ? null : e.Value;
            if (string.Equals(current, value, StringComparison.Ordinal)) return false;
            if (e == null) parent.Add(new XElement(name, value));
            else e.Value = value;
            edit.Changes.Add("set the Factions source's " + what + " to " + value + (current == null ? "" : " (was " + current + ")"));
            return true;
        }

        // ---- Profiles\Current.xml -------------------------------------------------------

        /// <summary>Enables Factions UI in the profile. <paramref name="existing"/> null: no profile yet.</summary>
        public static ConfigEdit EnablePlugin(string existing)
        {
            var edit = new ConfigEdit();
            XDocument doc;
            if (existing == null)
            {
                doc = NewDocument("Profile");
                doc.Root.Add(new XElement("Name", "Current"), new XElement("GitHub"),
                    new XElement("DevFolder"), new XElement("Local"), new XElement("Mods"));
                edit.Changes.Add("create Pulsar's Current profile");
            }
            else
            {
                doc = Load(existing, "Profile", "Current.xml");
                if (doc.Root.Element("Name") == null)
                    throw new ConfigException("Current.xml has no <Name>, so Pulsar cannot load it either; start the game once and run this again");
                // Pulsar always writes all five; one that is missing is written empty, so the profile
                // validates whatever the serializer does with an absent collection.
                foreach (string collection in ProfileOrder.Skip(1))
                {
                    if (doc.Root.Element(collection) == null)
                    {
                        EnsureChild(doc.Root, collection, ProfileOrder);
                        edit.Changes.Add("add the missing empty <" + collection + "> list to the Current profile");
                    }
                }
            }

            XElement gitHub = doc.Root.Element("GitHub");
            if (gitHub.Elements("GitHubPluginConfig").Any(IsOurPlugin))
                return Finish(doc, existing, edit);

            gitHub.Add(new XElement("GitHubPluginConfig", new XElement("Id", FactionsUi.PluginId)));
            edit.Changes.Add("enable Factions UI in the Current profile");
            return Finish(doc, existing, edit);
        }

        /// <summary>Disables Factions UI in the profile, if it is enabled.</summary>
        public static ConfigEdit DisablePlugin(string existing)
        {
            var edit = new ConfigEdit();
            if (existing == null) { edit.Text = null; return edit; }
            XDocument doc = Load(existing, "Profile", "Current.xml");
            XElement gitHub = doc.Root.Element("GitHub");
            if (gitHub != null)
            {
                foreach (XElement p in gitHub.Elements("GitHubPluginConfig").Where(IsOurPlugin).ToList())
                {
                    p.Remove();
                    edit.Changes.Add("disable Factions UI in the Current profile");
                }
            }
            return Finish(doc, existing, edit);
        }

        public static bool IsEnabled(string profile)
        {
            if (profile == null) return false;
            XDocument doc = Load(profile, "Profile", "Current.xml");
            XElement gitHub = doc.Root.Element("GitHub");
            return gitHub != null && gitHub.Elements("GitHubPluginConfig").Any(IsOurPlugin);
        }

        private static bool IsOurPlugin(XElement p)
        {
            // Pulsar matches plugin ids with ==, so the case must match FactionsUI.xml exactly.
            return string.Equals(((string)p.Element("Id") ?? "").Trim(), FactionsUi.PluginId, StringComparison.Ordinal);
        }

        // ---- shared ---------------------------------------------------------------------

        private static XDocument NewDocument(string rootName)
        {
            return new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement(rootName,
                    new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "xsd", Xsd.NamespaceName)));
        }

        private static XDocument Load(string text, string rootName, string fileName)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(text);
            }
            catch (XmlException e)
            {
                throw new ConfigException(fileName + " is not valid XML (" + e.Message + ")");
            }
            if (doc.Root == null || doc.Root.Name != rootName)
                throw new ConfigException(fileName + " does not have the layout this installer knows (root <" + (doc.Root == null ? "" : doc.Root.Name.LocalName) + ">, expected <" + rootName + ">)");
            return doc;
        }

        private static XElement EnsureChild(XElement parent, string name, string[] order)
        {
            XElement e = parent.Element(name);
            if (e != null) return e;
            e = new XElement(name);
            int idx = Array.IndexOf(order, name);
            XElement after = null;
            for (int i = idx - 1; i >= 0 && after == null; i--)
                after = parent.Element(order[i]);
            if (after != null) after.AddAfterSelf(e);
            else parent.AddFirst(e);
            return e;
        }

        private static ConfigEdit Finish(XDocument doc, string existing, ConfigEdit edit)
        {
            edit.Changed = edit.Changes.Count > 0;
            edit.Text = edit.Changed ? Serialize(doc) : existing;
            return edit;
        }

        public static string Serialize(XDocument doc)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
                NewLineChars = "\r\n",
            };
            using (var ms = new MemoryStream())
            {
                using (XmlWriter w = XmlWriter.Create(ms, settings))
                    doc.Save(w);
                return new UTF8Encoding(false).GetString(ms.ToArray());
            }
        }
    }

    public sealed class ConfigException : Exception
    {
        public ConfigException(string message) : base(message) { }
    }
}
