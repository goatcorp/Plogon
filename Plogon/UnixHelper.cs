using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Plogon;

/// <summary>
/// Support for Unix-specific operations.
/// </summary>
public static partial class UnixHelper
{
    /// <summary>
    /// Get the current user and group IDs.
    /// </summary>
    /// <returns>Tuple of user and group ID.</returns>
    [SupportedOSPlatform("linux")]
    public static (int Uid, int Gid) GetUidGid()
    {
        return (Native.getuid(), Native.getgid());
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private static partial class Native
    {
        [LibraryImport("libc", SetLastError = true)]
        public static partial int getuid();

        [LibraryImport("libc", SetLastError = true)]
        public static partial int getgid();
    }
}
