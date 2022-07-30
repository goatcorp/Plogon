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

    private State state;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginRepository"/> class.
    /// </summary>
    /// <param name="repoDirectory">The directory containing the repo.</param>
    public PluginRepository(DirectoryInfo repoDirectory)
    {
        this.repoDirectory = repoDirectory;
        
        if (StateFile.Exists)
        {
            this.state = Toml.ToModel<State>(StateFile.OpenText().ReadToEnd());
        }
        else
        {
            this.state = new State();
            Log.Information("State for repo at {repo} does not exist, creating new one", repoDirectory.FullName);
        }
        
        Log.Information("Plugin repository at {repo} initialized", repoDirectory.FullName);
    }

    private void SaveState()
    {
        File.WriteAllText(this.StateFile.FullName, Toml.FromModel(this.state));
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
        if (!this.state.Channels.ContainsKey(channelName))
        {
            return null;
        }
        
        var channel = this.state.Channels[channelName];
        
        if (channel.Plugins.TryGetValue(plugin, out var pluginState))
        {
            return pluginState;
        }

        return null;
    }

    /// <summary>
    /// Update the have commit on the repo
    /// </summary>
    /// <param name="channelName">The name of the channel</param>
    /// <param name="plugin">The internalname of the plugin</param>
    /// <param name="haveCommit">Commit that is now have</param>
    public void UpdatePluginHave(string channelName, string plugin, string haveCommit)
    {
        if (!this.state.Channels.ContainsKey(channelName))
        {
            this.state.Channels[channelName] = new State.Channel();
        }
        
        var channel = this.state.Channels[channelName];
        if (channel.Plugins.TryGetValue(plugin, out var pluginState))
        {
            pluginState.BuiltCommit = haveCommit;
        }
        else
        {
            var newState = new State.Channel.PluginState()
            {
                BuiltCommit = haveCommit,
            };
            channel.Plugins[plugin] = newState;
        }
        
        SaveState();
    }
}