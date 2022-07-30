using Plogon.Manifests;
#pragma warning disable CS8618
#pragma warning disable CS1591

namespace Plogon;

public class BuildTask
{
    public Manifest Manifest { get; set; }
    
    public string? HaveCommit { get; set; }
    
    public string Channel { get; set; }
    
    public string InternalName { get; set; }
}