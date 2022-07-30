using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;
using Tomlyn;
#pragma warning disable CS1591

namespace Plogon.Manifests;

public class ManifestStorage
{
    private readonly DirectoryInfo baseDirectory;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Manifest>> Channels;

    public ManifestStorage(DirectoryInfo baseDirectory)
    {
        this.baseDirectory = baseDirectory;
        
        var channels = new Dictionary<string, IReadOnlyDictionary<string, Manifest>>();

        var stableDir = new DirectoryInfo(Path.Combine(this.baseDirectory.FullName, "stable"));
        var testingDir = new DirectoryInfo(Path.Combine(this.baseDirectory.FullName, "testing"));

        channels.Add(stableDir.Name, GetManifestsInDirectory(stableDir));

        foreach (var testingChannelDir in testingDir.EnumerateDirectories())
        {
            var manifests = GetManifestsInDirectory(testingChannelDir);
            channels.Add($"testing-{testingChannelDir.Name}", manifests);
        }

        this.Channels = channels;
    }

    private static Dictionary<string, Manifest> GetManifestsInDirectory(DirectoryInfo directory)
    {
        var manifests = new Dictionary<string, Manifest>();

        foreach (var manifestDir in directory.EnumerateDirectories())
        {
            try
            {
                var tomlText = manifestDir.GetFiles("*.toml").First().OpenText().ReadToEnd();
                manifests.Add(manifestDir.Name, Toml.ToModel<Manifest>(tomlText));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not load manifest for {Name} in {Channel}", manifestDir.Name, directory.Name);
            }
        }

        return manifests;
    }
}