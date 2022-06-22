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

    private State state;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginRepository"/> class.
    /// </summary>
    /// <param name="repoDirectory">The directory containing the repo.</param>
    public PluginRepository(DirectoryInfo repoDirectory)
    {
        this.repoDirectory = repoDirectory;

        var stateFile = new FileInfo(Path.Combine(repoDirectory.FullName, "State.toml"));
        if (stateFile.Exists)
        {
            this.state = Toml.ToModel<State>(stateFile.OpenText().ReadToEnd());
        }
        else
        {
            this.state = new State();
            Log.Information("State for repo at {repo} does not exist, creating new one", repoDirectory.FullName);
        }
        
        Log.Information("Plugin repository at {repo} initialized", repoDirectory.FullName);
    }
    
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
}