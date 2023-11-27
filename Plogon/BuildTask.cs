using System;

using Plogon.Manifests;
#pragma warning disable CS8618
#pragma warning disable CS1591

namespace Plogon;

public class BuildTask
{
    public Manifest? Manifest { get; set; }

    public string? HaveCommit { get; set; }

    public DateTime? HaveTimeBuilt { get; set; }

    public string? HaveVersion { get; set; }

    public string Channel { get; set; }

    public string InternalName { get; set; }

    // New in ANY channel
    public bool IsNewPlugin { get; set; }

    // New in THIS channel
    public bool IsNewInThisChannel { get; set; }

    public TaskType Type { get; set; }

    public enum TaskType
    {
        Build,
        Remove,
    }

    public bool IsGitHub => Manifest != null && new Uri(Manifest.Plugin.Repository).Host == "github.com";

    public bool IsGitLab => Manifest != null && new Uri(Manifest.Plugin.Repository).Host == "gitlab.com";

    public override string ToString() => $"{Type} - {InternalName}[{Channel}] - {HaveCommit ?? "?"} - {Manifest?.Plugin?.Commit ?? "?"} - {Manifest?.Directory?.FullName ?? "?"}";
}
