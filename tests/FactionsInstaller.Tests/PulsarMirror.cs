// The serialized shape of Pulsar's config classes, copied from SpaceGT/Pulsar (MIT) at v2.4.3:
// Shared/Config/SourcesConfig.cs, Shared/Config/Sources/*.cs, Shared/Data/Profile.cs,
// Shared/Config/PluginDataConfig.cs, GitHubPluginConfig.cs, LocalFolderConfig.cs.
// Only members that XmlSerializer reads or writes are kept. The tests deserialize the installer's
// output with these, the way Pulsar does on start-up, and apply Profile.Validate.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace FactionsInstaller.Tests.Pulsar
{
    public class SourcesConfig
    {
        public bool ShowWarning { get; set; } = true;
        public int MaxSourceAge { get; set; } = 2;

        [XmlArray]
        [XmlArrayItem("LocalHub")]
        public LocalHubConfig[] LocalHubSources
        {
            get { return localHubSources.ToArray(); }
            set { localHubSources.Clear(); foreach (var v in value) localHubSources.Add(v); }
        }
        private readonly HashSet<LocalHubConfig> localHubSources = new HashSet<LocalHubConfig>();

        [XmlArray]
        [XmlArrayItem("RemoteHub")]
        public RemoteHubConfig[] RemoteHubSources
        {
            get { return remoteHubSources.ToArray(); }
            set { remoteHubSources.Clear(); foreach (var v in value) remoteHubSources.Add(v); }
        }
        private readonly HashSet<RemoteHubConfig> remoteHubSources = new HashSet<RemoteHubConfig>();

        [XmlArray]
        [XmlArrayItem("RemotePlugin")]
        public RemotePluginConfig[] RemotePluginSources
        {
            get { return remotePluginSources.ToArray(); }
            set { remotePluginSources.Clear(); foreach (var v in value) remotePluginSources.Add(v); }
        }
        private readonly HashSet<RemotePluginConfig> remotePluginSources = new HashSet<RemotePluginConfig>();

        [XmlArray]
        [XmlArrayItem("LocalPlugin")]
        public LocalPluginConfig[] LocalPluginSources
        {
            get { return localPluginSources.ToArray(); }
            set { localPluginSources.Clear(); foreach (var v in value) localPluginSources.Add(v); }
        }
        private readonly HashSet<LocalPluginConfig> localPluginSources = new HashSet<LocalPluginConfig>();

        [XmlArray]
        [XmlArrayItem("Mod")]
        public ModConfig[] ModSources
        {
            get { return modSources.ToArray(); }
            set { modSources.Clear(); foreach (var v in value) modSources.Add(v); }
        }
        private readonly HashSet<ModConfig> modSources = new HashSet<ModConfig>();
    }

    public class LocalHubConfig
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public bool Enabled { get; set; }
        public string Hash { get; set; }
    }

    public class RemoteHubConfig
    {
        public string Name { get; set; }
        public string Repo { get; set; }
        public string Branch { get; set; }
        public DateTime? LastCheck { get; set; }
        public string Hash { get; set; }
        public bool Enabled { get; set; }
        public bool Trusted { get; set; }
    }

    public class RemotePluginConfig
    {
        public string Name { get; set; }
        public string Repo { get; set; }
        public string Branch { get; set; }
        public string File { get; set; }
        public DateTime? LastCheck { get; set; }
        public bool Enabled { get; set; }
        public bool Trusted { get; set; }
    }

    public class LocalPluginConfig
    {
        public string Name { get; set; }
        public string Folder { get; set; }
        public string File { get; set; }
        public bool Enabled { get; set; }
    }

    public class ModConfig
    {
        public string Name { get; set; }
        public long ID { get; set; }
        public bool Enabled { get; set; }
    }

    [XmlInclude(typeof(LocalFolderConfig))]
    [XmlInclude(typeof(GitHubPluginConfig))]
    public abstract class PluginDataConfig
    {
        public string Id { get; set; }
    }

    public class GitHubPluginConfig : PluginDataConfig
    {
        public string SelectedVersion { get; set; }
    }

    public class LocalFolderConfig : PluginDataConfig
    {
        public bool DebugBuild { get; set; } = true;
    }

    public class Profile
    {
        public string Name { get; set; }
        public HashSet<GitHubPluginConfig> GitHub { get; set; }
        public HashSet<LocalFolderConfig> DevFolder { get; set; }
        public HashSet<string> Local { get; set; }
        public HashSet<ulong> Mods { get; set; }

        public Profile() { }

        public Profile(string name)
        {
            Name = name;
            GitHub = new HashSet<GitHubPluginConfig>();
            DevFolder = new HashSet<LocalFolderConfig>();
            Local = new HashSet<string>();
            Mods = new HashSet<ulong>();
        }

        public bool Validate() => !new object[] { Name, GitHub, DevFolder, Local, Mods }.Contains(null);
    }
}
