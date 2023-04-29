using System;
using System.IO;
using System.Linq;
using Serilog;
using Tomlyn;

namespace Plogon.Repo;

/// <summary>
/// Class representing a plugin repository.
/// </summary>
public class PluginRepository
{
    private readonly DirectoryInfo repoDirectory;
    private FileInfo StateFile => new FileInfo(Path.Combine(repoDirectory.FullName, "State.toml"));

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
            this.State = Toml.ToModel<State>(StateFile.OpenText().ReadToEnd());
        }
        else
        {
            this.State = new State();
            Log.Information("State for repo at {repo} does not exist, creating new one", repoDirectory.FullName);
        }
        
        Log.Information("Plugin repository at {repo} initialized", repoDirectory.FullName);
    }

    private void SaveState()
    {
        File.WriteAllText(this.StateFile.FullName, Toml.FromModel(this.State));
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
    /// <param name="plugin">The internalname of the plugin</param>
    /// <param name="haveCommit">Commit that is now have</param>
    /// <param name="effectiveVersion">New version of the plugin</param>
    /// <param name="changelog">Plugin changelog</param>
    public void UpdatePluginHave(string channelName, string plugin, string haveCommit, string effectiveVersion, string? changelog)
    {
        if (!this.State.Channels.ContainsKey(channelName))
        {
            this.State.Channels[channelName] = new State.Channel();
        }
        
        var channel = this.State.Channels[channelName];

        if (channel.Plugins.TryGetValue(plugin, out var pluginState))
        {
            pluginState.BuiltCommit = haveCommit;
            pluginState.TimeBuilt = DateTime.Now;
            pluginState.EffectiveVersion = effectiveVersion;
        }
        else
        {
            pluginState = new State.Channel.PluginState()
            {
                BuiltCommit = haveCommit,
                TimeBuilt = DateTime.Now,
                EffectiveVersion = effectiveVersion,
            };
            channel.Plugins[plugin] = pluginState;
        }

        if (!string.IsNullOrWhiteSpace(changelog))
        {
            pluginState.Changelogs[effectiveVersion] = new State.Channel.PluginState.PluginChangelog()
            {
                Changelog = changelog,
                TimeReleased = DateTime.Now,
            };
        }

        SaveState();
    }
}