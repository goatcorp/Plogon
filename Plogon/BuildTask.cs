using System;
using System.Collections.Generic;

using Plogon.Manifests;
#pragma warning disable CS8618
#pragma warning disable CS1591

namespace Plogon;

public class BuildTask
{
    public Manifest Manifest { get; init; }

    public string? HaveCommit { get; init; }

    public DateTime? HaveTimeBuilt { get; init; }

    public string? HaveVersion { get; init; }

    public string Channel { get; init; }

    public string InternalName { get; init; }

    // New in ANY channel
    public bool IsNewPlugin { get; init; }

    // New in THIS channel
    public bool IsNewInThisChannel { get; init; }

    public TaskType Type { get; init; }
    
    /// <summary>
    /// Set of old owners for the plugin, if they have changed.
    /// </summary>
    public List<string>? OldOwners { get; init; }

    public enum TaskType
    {
        Build,
        Remove,
    }

    public bool IsGitHub => new Uri(Manifest.Plugin.Repository).Host == "github.com";

    public bool IsGitLab => new Uri(Manifest.Plugin.Repository).Host == "gitlab.com";

    public override string ToString() => $"{Type} - {InternalName}[{Channel}] - {HaveCommit ?? "?"} - {Manifest.Plugin.Commit}";
}
