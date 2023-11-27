using System.Collections.Generic;

using Newtonsoft.Json;
#pragma warning disable CS8618
#pragma warning disable CS1591

namespace Plogon;

public class NugetLockfile
{
    [JsonProperty("version")]
    public int Version { get; set; }

    [JsonProperty("dependencies")]
    public Dictionary<string, Dictionary<string, Dependency>> Runtimes { get; set; }

    public class Dependency
    {
        [JsonProperty("type")]
        public DependencyType Type { get; set; }

        [JsonProperty("resolved")]
        public string Resolved { get; set; }

        [JsonProperty("contentHash")]
        public string ContentHash { get; set; }

        public enum DependencyType
        {
            Direct,
            Transitive,
            Project,
        }
    }
}
