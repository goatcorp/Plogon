using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;
using Tomlyn;
#pragma warning disable CS1591

namespace Plogon.Manifests;

public class ManifestStorage
{
    private readonly DirectoryInfo baseDirectory;
    private readonly ISet<string>? affectedManifests;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Manifest>> Channels;

    public ManifestStorage(DirectoryInfo baseDirectory, string? prDiff)
    {
        this.baseDirectory = baseDirectory;

        if (prDiff is not null)
        {
            this.affectedManifests = GetAffectedManifestsFromDiff(prDiff);
        }

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

    private Dictionary<string, Manifest> GetManifestsInDirectory(DirectoryInfo directory)
    {
        var manifests = new Dictionary<string, Manifest>();

        foreach (var manifestDir in directory.EnumerateDirectories())
        {
            try
            {
                var tomlFile = manifestDir.GetFiles("*.toml").First();
                if (affectedManifests is not null && !affectedManifests.Contains(tomlFile.FullName))
                {
                    continue;
                }
                
                var tomlText = tomlFile.OpenText().ReadToEnd();
                manifests.Add(manifestDir.Name, Toml.ToModel<Manifest>(tomlText));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not load manifest for {Name} in {Channel}", manifestDir.Name, directory.Name);
            }
        }

        return manifests;
    }
    
    private ISet<string> GetAffectedManifestsFromDiff(string prDiff)
    {
        var manifestFiles = new HashSet<string>();
        
        var rx = new Regex(@"(\+\+\+\s+b\/)(.*\.toml)", RegexOptions.IgnoreCase);
        foreach (Match match in rx.Matches(prDiff))
        {
            manifestFiles.Add(new FileInfo(Path.Combine(this.baseDirectory.FullName, match.Groups[2].Value)).FullName);
        }

        return manifestFiles;
    }
}