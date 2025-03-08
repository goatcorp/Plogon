using System.Collections.Generic;
using System.IO;

#pragma warning disable CS1591
#pragma warning disable CS8618

namespace Plogon.Manifests;

public class Manifest
{
    public class PluginInfo
    {
        public PluginInfo()
        {
            this.Owners = new List<string>();
            this.Secrets = new Dictionary<string, string>();
        }

        public string Repository { get; set; }

        public string Commit { get; set; }

        public string? ProjectPath { get; set; }

        public string Changelog { get; set; }

        public string Version { get; set; }

        public string MinimumVersion { get; set; }
        
        public List<string> Owners { get; set; }

        public Dictionary<string, string> Secrets { get; set; }
    }

    public class BuildInfo
    {
        public class Need
        {
            public string Type { get; set; }

            public string Url { get; set; }

            public string? Dest { get; set; }

            public string? Sha512 { get; set; }
        }

        public BuildInfo()
        {
            this.Needs = new List<Need>();
        }

        public List<Need> Needs { get; set; }

        public string? Image { get; set; }
    }

    public BuildInfo? Build { get; set; }

    public PluginInfo Plugin { get; set; }

    /// <summary>
    /// File on disk. May be null if this manifest isn't actually on disk (=historical)
    /// </summary>
    public FileInfo? File { get; set; }
    
    /// <summary>
    /// Path to the manifest in the repo it was loaded from (e.g. "stable/MyPlugin/manifest.toml")
    /// </summary>
    public string PathInRepo { get; set; }
}
