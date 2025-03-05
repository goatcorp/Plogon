using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Serilog;

namespace Plogon.Repo;

/// <summary>
/// Class representing a plugin repository.
/// </summary>
public class PluginRepository
{
    private readonly DirectoryInfo repoDirectory;
    private FileInfo StateFile => new FileInfo(Path.Combine(repoDirectory.FullName, "state.json"));

    /// <summary>
    /// Current state of the repository
    /// </summary>
    public State State { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginRepository"/> class.
    /// </summary>
    /// <param name="repoDirectory">The directory containing the repo.</param>
    public PluginRepository(DirectoryInfo repoDirectory)
    {
        this.repoDirectory = repoDirectory;

        if (StateFile.Exists)
        {
            this.State = JsonSerializer.Deserialize<State>(StateFile.OpenText().ReadToEnd())
                ?? throw new Exception("Failed to load state");
        }
        else
        {
            this.State = new State();
            Log.Warning("State for repo at {Repo} does not exist, creating new one", repoDirectory.FullName);
        }
    }

    private void SaveState()
    {
        File.WriteAllText(this.StateFile.FullName, JsonSerializer.Serialize(this.State, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    /// <summary>
    /// Get the output directory of a plugin.
    /// </summary>
    /// <param name="channelName">The name of the channel</param>
    /// <param name="plugin">The internalname of the plugin</param>
    /// <returns>The target output directory</returns>
    public DirectoryInfo GetPluginOutputDirectory(string channelName, string plugin)
    {
        return this.repoDirectory.CreateSubdirectory(channelName).CreateSubdirectory(plugin);
    }

    /// <summary>
    /// Get the state of a plugin on the repo
    /// </summary>
    /// <param name="channelName">The name of the channel</param>
    /// <param name="plugin">The internalname of the plugin</param>
    /// <returns>The state of the plugin, or null if not present</returns>
    public State.Channel.PluginState? GetPluginState(string channelName, string plugin)
    {
        if (!this.State.Channels.ContainsKey(channelName))
        {
            return null;
        }

        var channel = this.State.Channels[channelName];

        if (channel.Plugins.TryGetValue(plugin, out var pluginState))
        {
            return pluginState;
        }

        return null;
    }

    /// <summary>
    /// Check if a plugin is in any channel
    /// </summary>
    /// <param name="plugin">InternalName of the plugin</param>
    /// <returns></returns>
    public bool IsPluginInAnyChannel(string plugin)
    {
        return this.State.Channels.Any(x => x.Value.Plugins.Any(y => y.Key == plugin));
    }

    /// <summary>
    /// Remove a plugin from the repository.
    /// </summary>
    /// <param name="channelName">The name of the channel</param>
    /// <param name="plugin">The name of the plugin</param>
    public void RemovePlugin(string channelName, string plugin)
    {
        this.State.Channels[channelName].Plugins.Remove(plugin);
        SaveState();
    }

    /// <summary>
    /// Update the have commit on the repo
    /// </summary>
    /// <param name="channelName">The name of the channel</param>
    /// <param name="internalName">The internalname of the plugin</param>
    /// <param name="haveCommit">Commit that is now have</param>
    /// <param name="effectiveVersion">New version of the plugin</param>
    /// <param name="minimumVersion">Minimum version Dalamud should still try to load.</param>
    /// <param name="changelog">Plugin changelog</param>
    /// <param name="reviewer">Who reviewed this version</param>
    /// <param name="submitter">Who submitted this version</param>
    /// <param name="needs">Needs we had for this version</param>
    public void UpdatePluginHave(
        string channelName,
        string internalName,
        string haveCommit,
        string effectiveVersion,
        string? minimumVersion,
        string? changelog,
        string reviewer,
        string submitter,
        IEnumerable<(string Key, string Version)> needs)
    {
        if (!this.State.Channels.ContainsKey(channelName))
        {
            this.State.Channels[channelName] = new State.Channel();
        }

        var channel = this.State.Channels[channelName];

        if (channel.Plugins.TryGetValue(internalName, out var pluginState))
        {
            pluginState.BuiltCommit = haveCommit;
            pluginState.TimeBuilt = DateTime.Now;
            pluginState.EffectiveVersion = effectiveVersion;
            pluginState.MinimumVersion = minimumVersion;
        }
        else
        {
            pluginState = new State.Channel.PluginState
            {
                BuiltCommit = haveCommit,
                TimeBuilt = DateTime.Now,
                EffectiveVersion = effectiveVersion,
                MinimumVersion = minimumVersion,
            };
            channel.Plugins[internalName] = pluginState;
        }

        pluginState.Changelogs[effectiveVersion] = new State.Channel.PluginState.PluginChangelog
        {
            Changelog = changelog,
            TimeReleased = DateTime.Now,
            UsedNeeds = needs.Select(x => new State.Channel.PluginState.PluginChangelog.UsedNeed
            {
                Key = x.Key,
                Version = x.Version,
            }).ToList(),
            Reviewer = reviewer,
            Submitter = submitter,
        };

        SaveState();
    }

    /// <summary>
    /// Add a list of needs to the repository.
    /// </summary>
    /// <param name="needs">The needs to add.</param>
    public void AddReviewedNeeds(IEnumerable<State.Need> needs)
    {
        foreach (var need in needs)
        {
            AddReviewedNeed(need);
        }
        
        this.SaveState();
    }
    
    /// <summary>
    /// Add a need to the repository.
    /// </summary>
    /// <param name="need">The need to add.</param>
    /// <exception cref="Exception">Thrown if the need was already reviewed.</exception>
    private void AddReviewedNeed(State.Need need)
    {
        if (this.State.ReviewedNeeds.Any(x => x.Key == need.Key && x.Version == need.Version))
        {
            throw new Exception($"Need {need.Key}(v{need.Version}) already in state");
        }
        
        this.State.ReviewedNeeds.Add(need);
    }
}
