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

    public ManifestStorage(DirectoryInfo baseDirectory, string? prDiff, bool ignoreNonAffected)
    {
        this.baseDirectory = baseDirectory;

        if (prDiff is not null)
        {
            this.affectedManifests = GetAffectedManifestsFromDiff(prDiff);
        }

        var channels = new Dictionary<string, IReadOnlyDictionary<string, Manifest>>();

        var stableDir = new DirectoryInfo(Path.Combine(this.baseDirectory.FullName, "stable"));
        var testingDir = new DirectoryInfo(Path.Combine(this.baseDirectory.FullName, "testing"));

        channels.Add(stableDir.Name, GetManifestsInDirectory(stableDir, ignoreNonAffected));

        foreach (var testingChannelDir in testingDir.EnumerateDirectories())
        {
            var manifests = GetManifestsInDirectory(testingChannelDir, ignoreNonAffected);
            channels.Add($"testing-{testingChannelDir.Name}", manifests);
        }

        this.Channels = channels;
    }

    private Dictionary<string, Manifest> GetManifestsInDirectory(DirectoryInfo directory, bool ignoreNonAffected)
    {
        var manifests = new Dictionary<string, Manifest>();

        foreach (var manifestDir in directory.EnumerateDirectories())
        {
            try
            {
                var tomlFile = manifestDir.GetFiles("*.toml").First();
                if (affectedManifests is not null && !affectedManifests.Contains(tomlFile.FullName) && ignoreNonAffected)
                    continue;
                
                var tomlText = tomlFile.OpenText().ReadToEnd();
                var manifest = Toml.ToModel<Manifest>(tomlText);

                manifest.Directory = manifestDir;
                manifests.Add(manifestDir.Name, manifest);
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
        
        var rx = new Regex(@"((?:\+\+\+\s+b\/)|(?:rename to\s+))(.*\.toml)", RegexOptions.IgnoreCase);
        foreach (Match match in rx.Matches(prDiff))
        {
            manifestFiles.Add(new FileInfo(Path.Combine(this.baseDirectory.FullName, match.Groups[2].Value)).FullName);
        }

        return manifestFiles;
    }
}