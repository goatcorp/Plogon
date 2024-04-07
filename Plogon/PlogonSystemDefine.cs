namespace Plogon;

/// <summary>
/// Static values for Plogon.
/// </summary>
public static class PlogonSystemDefine
{
    /// <summary>
    /// Current API level.
    /// </summary>
    public const int API_LEVEL = 9;
    
    /// <summary>
    /// PAC members, github logins.
    /// </summary>
    public static readonly string[] PacMembers = new[] { "goaaats", "reiichi001", "lmcintyre", "ackwell", "karashiiro", "philpax" };
    
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
}
