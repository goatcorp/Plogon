using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Plogon.Manifests;

public class Manifest
{
    public class PluginInfo
    {
        public PluginInfo()
        {
            this.Owners = new List<uint>();
        }
    
        public string Repository { get; set; }
    
        public string Commit { get; set; }
    
        public string ProjectPath { get; set; }
    
        public string Changelog { get; set; }
    
        public List<uint> Owners { get; set; }
    }
    
    public PluginInfo Plugin { get; set; }
}