using Plogon.Manifests;

namespace Plogon;

public class BuildTask
{
    public Manifest Manifest { get; set; }
    
    public string? HaveCommit { get; set; }
    
    public string Channel { get; set; }
    
    public string InternalName { get; set; }
}