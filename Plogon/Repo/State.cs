using System;
using System.Collections.Generic;
#pragma warning disable CS1591
#pragma warning disable CS8618

namespace Plogon.Repo;

/// <summary>
/// Class defining state for the target plugin repository.
/// </summary>
public class State
{
    public State()
    {
        this.Channels = new Dictionary<string, Channel>();
    }

    public class Channel
    {
        public Channel()
        {
            this.Plugins = new Dictionary<string, PluginState>();
        }

        public class PluginState
        {
            public PluginState()
            {
                this.Changelogs = new Dictionary<string, PluginChangelog>();
            }

            public string BuiltCommit { get; set; }
            public DateTime TimeBuilt { get; set; }
            public string EffectiveVersion { get; set; }
            public string? MinimumVersion { get; set; }

            public Dictionary<string, PluginChangelog> Changelogs { get; set; }

            public class PluginChangelog
            {
                public DateTime TimeReleased { get; set; }
                public string Changelog { get; set; }
            }
        }

        public IDictionary<string, PluginState> Plugins { get; set; }
    }

    public IDictionary<string, Channel> Channels { get; set; }
}
