using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Serilog;

using Tomlyn;
#pragma warning disable CS1591

namespace Plogon.Manifests;

public class ManifestStorage
{
    private readonly DateTime? cutoffDate;

    public ManifestStorage(DirectoryInfo baseDirectory, DateTime? cutoffDate = null)
    {
        this.BaseDirectory = baseDirectory;
        this.cutoffDate = cutoffDate;

        var channels = new Dictionary<string, IReadOnlyDictionary<string, Manifest>>();

        var stableDir = new DirectoryInfo(Path.Combine(this.BaseDirectory.FullName, "stable"));
        var testingDir = new DirectoryInfo(Path.Combine(this.BaseDirectory.FullName, "testing"));

        channels.Add(stableDir.Name, GetManifestsInDirectory(stableDir));

        foreach (var testingChannelDir in testingDir.EnumerateDirectories())
        {
            var manifests = GetManifestsInDirectory(testingChannelDir);
            channels.Add($"testing-{testingChannelDir.Name}", manifests);
        }

        this.Channels = channels;
    }
    
    public DirectoryInfo BaseDirectory { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Manifest>> Channels { get; private set; }
    
    /// <summary>
    /// Get the manifest for a specific channel and internal name.
    /// If the manifest was deleted, get the last known manifest.
    /// </summary>
    /// <param name="channel">Name of the channel to get the manifest from.</param>
    /// <param name="internalName">Internal name of the plugin to look for.</param>
    /// <returns>The parsed historical manifest.</returns>
    /// <exception cref="Exception">If the manifest could not be found or parsed</exception>
    public async Task<Manifest?> GetHistoricManifestAsync(string channel, string internalName)
    {
        var formattedPath = $"\"{PlogonSystemDefine.ChannelIdToPath(channel)}/{internalName}/manifest.toml\"";

        var revHelper = await GitHelper.ExecuteAsync(this.BaseDirectory, $"rev-list -n 1 HEAD -- {formattedPath}");
        if (string.IsNullOrWhiteSpace(revHelper.StandardOutput))
            throw new Exception("Historic manifest rev not found");
        
        var manifestHelper = await GitHelper.ExecuteAsync(this.BaseDirectory, $"show {revHelper.StandardOutput.Trim()}^:{formattedPath}");
        return Toml.ToModel<Manifest>(manifestHelper.StandardOutput ?? throw new Exception("Could not get historic manifest content"));
    }

    private Dictionary<string, Manifest> GetManifestsInDirectory(DirectoryInfo directory)
    {
        var manifests = new Dictionary<string, Manifest>();

        foreach (var manifestDir in directory.EnumerateDirectories())
        {
            try
            {
                var tomlFile = manifestDir.GetFiles("*.toml").First();

                if (cutoffDate != null)
                {
                    var psi = new ProcessStartInfo("git",
                        $"log -n 1 --pretty=format:%cd --date=iso-strict \"{tomlFile.FullName}\"")
                    {
                        RedirectStandardOutput = true,
                        WorkingDirectory = this.BaseDirectory.FullName,
                    };

                    var process = Process.Start(psi);
                    if (process == null)
                        throw new Exception("Date process was null.");

                    var dateOutput = process.StandardOutput.ReadToEnd();

                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new Exception($"Git could not get date for manifest: {process.ExitCode} -- {psi.Arguments}");

                    var updateDate = DateTime.Parse(dateOutput);
                    if (updateDate < cutoffDate)
                    {
                        Log.Information("Skipping manifest {Name} in {Channel} because it was updated at {Date} which is before the cutoff date of {CutoffDate}",
                            manifestDir.Name, directory.Name, updateDate, cutoffDate);
                        continue;
                    }
                }

                var tomlText = tomlFile.OpenText().ReadToEnd();
                var manifest = Toml.ToModel<Manifest>(tomlText);

                manifest.File = tomlFile;
                manifests.Add(manifestDir.Name, manifest);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not load manifest for {Name} in {Channel}", manifestDir.Name, directory.Name);
            }
        }

        return manifests;
    }
}
