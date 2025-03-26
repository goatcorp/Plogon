namespace Plogon;

/// <summary>
/// Static values for Plogon.
/// </summary>
public static class PlogonSystemDefine
{
    /// <summary>
    /// Current API level.
    /// </summary>
    public const int API_LEVEL = 12;
    
    /// <summary>
    /// PAC members, github logins.
    /// </summary>
    public static readonly string[] PacMembers =
    [
        "goaaats",
        "reiichi001",
        "perchbirdd",
        "Critical-Impact",
        "karashiiro",
        "philpax",
    ];

    /// <summary>
    /// Nuget namespaces/packages we know are safe and don't want to see every time.
    /// </summary>
    public static readonly string[] SafeNugetNamespaces =
    [
        "Microsoft.SourceLink.",
        "DotNet.ReproducibleBuilds",
        "DalamudPackager",
        "System.Memory",
        "System.Threading.",
        "System.Buffers",
        "System.Reactive",
        "System.Text.Json",
        "System.Text.Encodings",
        "System.Drawing.Common",
        "Newtonsoft.Json",
        "JetBrains.Annotations",
        "System.Runtime.CompilerServices.Unsafe",
        "Microsoft.Extensions."
    ];
    
    /// <summary>
    /// Label for a new plugin.
    /// </summary>
    public const string PR_LABEL_NEW_PLUGIN = "new plugin";
    
    /// <summary>
    /// Label for a plugin that needs an icon.
    /// </summary>
    public const string PR_LABEL_NEED_ICON = "need icon";
    
    /// <summary>
    /// Label for failed builds.
    /// </summary>
    public const string PR_LABEL_BUILD_FAILED = "build failed";
    
    /// <summary>
    /// Label for version conflicts.
    /// </summary>
    public const string PR_LABEL_VERSION_CONFLICT = "version conflict";
    
    /// <summary>
    /// Label for channel moves.
    /// </summary>
    public const string PR_LABEL_MOVE_CHANNEL = "move channel";
    
    /// <summary>
    /// Label for small PRs.
    /// </summary>
    public const string PR_LABEL_SIZE_SMALL = "size-small";
    
    /// <summary>
    /// Label for mid-sized PRs.
    /// </summary>
    public const string PR_LABEL_SIZE_MID = "size-mid";
    
    /// <summary>
    /// Label for large PRs.
    /// </summary>
    public const string PR_LABEL_SIZE_LARGE = "size-large";
    
    /// <summary>
    /// PR label for a plugin pending code review.
    /// </summary>
    public const string PR_LABEL_PENDING_CODE_REVIEW = "pending-code-review";
    
    /// <summary>
    /// PR label for a plugin pending rules compliance.
    /// </summary>
    public const string PR_LABEL_PENDING_RULES_COMPLIANCE = "pending-rules-compliance";
    
    /// <summary>
    /// PR label for a plugin pending testing.
    /// </summary>
    public const string PR_LABEL_PENDING_TESTING = "pending-testing";
    
    /// <summary>
    /// Transform a channel ID to a path into the repository.
    /// Past goat was dumb as a rock and decided that we want to have channel-subfolders that magically transform to this.
    /// </summary>
    /// <param name="channelId">The ID of the channel.</param>
    /// <returns>The path that can be used to get manifests.</returns>
    public static string ChannelIdToPath(string channelId) => channelId.Replace("-", "/");
}
