using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Plogon;

/// <summary>
/// Helper class to parse the diff and extract the changed files.
/// </summary>
public partial class DiffHelper
{
    private readonly HashSet<string> changedFiles = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DiffHelper"/> class.
    /// </summary>
    
    /// <param name="diff">The diff being applied.</param>
    public DiffHelper(string diff)
    {
        foreach (Match match in FileChangesRegex().Matches(diff))
        {
            changedFiles.Add(match.Groups[2].Value);
        }
    }
    
    /// <summary>
    /// Gets the changed files.
    /// </summary>
    public IReadOnlySet<string> ChangedFiles => changedFiles;
    
    /// <summary>
    /// Check if a file is included in the diff.
    /// The path to the file must be relative to where the diff is being applied.
    /// </summary>
    /// <param name="file">The file path to check for.</param>
    /// <returns>Whether the file is included in the diff.</returns>
    public bool IsFileChanged(string file)
    {
        var normalizedPath = file.Replace("\\", "/");
        return changedFiles.Contains(normalizedPath);
    }

    /// <summary>
    /// Check whether an actual file on the filesystem is included in this diff.
    /// </summary>
    /// <param name="baseDirectory">The directory the diff is being applied to.</param>
    /// <param name="file">The file that we are looking for.</param>
    /// <returns>Whether the file is included.</returns>
    public bool IsFileChanged(DirectoryInfo baseDirectory, FileInfo file)
    {
        foreach (var changedFile in changedFiles)
        {
            if (Path.GetFullPath(Path.Combine(baseDirectory.FullName, changedFile)) == file.FullName)
            {
                return true;
            }
        }
        
        return false;
    }

    [GeneratedRegex(@"((?:\+\+\+\s+b\/)|(?:---\s+a\/)|(?:rename to\s+))(.*\.toml)", RegexOptions.IgnoreCase)]
    private static partial Regex FileChangesRegex();
}
