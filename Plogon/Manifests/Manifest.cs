using System.Collections.Generic;
using System.Runtime.Serialization;
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
        }
    
        public string Repository { get; set; }
    
        public string Commit { get; set; }
    
        public string ProjectPath { get; set; }
    
        public string Changelog { get; set; }
    
        public List<string> Owners { get; set; }
    }
    
    public PluginInfo Plugin { get; set; }
}