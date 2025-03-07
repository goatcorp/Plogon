using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Serilog;

namespace Plogon;

/// <summary>
/// Helper class for executing git commands in a specific directory.
/// </summary>
public class GitHelper
{
    /// <summary>
    /// The working directory where git commands will be executed
    /// </summary>
    public DirectoryInfo WorkingDirectory { get; }

    /// <summary>
    /// Standard output from the last command execution
    /// </summary>
    public string? StandardOutput { get; private set; }

    /// <summary>
    /// Standard error from the last command execution
    /// </summary>
    public string? StandardError { get; private set; }

    /// <summary>
    /// Exit code from the last command execution
    /// </summary>
    public int ExitCode { get; private set; }

    /// <summary>
    /// Private constructor - use ExecuteAsync instead
    /// </summary>
    /// <param name="workingDirectory">Directory where git commands will be executed</param>
    /// <exception cref="ArgumentException">Thrown if the working directory doesn't exist</exception>
    private GitHelper(DirectoryInfo workingDirectory)
    {
        if (!workingDirectory.Exists)
        {
            throw new ArgumentException($"Directory does not exist: {workingDirectory}", nameof(workingDirectory));
        }

        this.WorkingDirectory = workingDirectory;
    }

    /// <summary>
    /// Creates a GitCommandRunner instance and executes a git command asynchronously
    /// </summary>
    /// <param name="workingDirectory">Directory where git commands will be executed</param>
    /// <param name="arguments">The git command arguments (e.g., "status", "pull", etc.)</param>
    /// <returns>A GitCommandRunner instance with the results of the command</returns>
    /// <exception cref="GitCommandException">Thrown if the git command fails</exception>
    public static async Task<GitHelper> ExecuteAsync(DirectoryInfo workingDirectory, string arguments)
    {
        var runner = new GitHelper(workingDirectory);
        await runner.ExecuteCommandAsync(arguments);
        return runner;
    }

    /// <summary>
    /// Private helper method to execute a git command asynchronously
    /// </summary>
    /// <param name="arguments">The git command arguments</param>
    /// <returns>Task representing the asynchronous operation</returns>
    /// <exception cref="GitCommandException">Thrown if the git command fails</exception>
    private async Task ExecuteCommandAsync(string arguments)
    {
        using var process = new Process();
        this.SetupProcess(process, arguments);

        GitHubOutputBuilder.StartGroup($"git {process.StartInfo.Arguments}");
        Log.Verbose($"Executing 'git {process.StartInfo.Arguments}'");
        
        process.Start();

        // Read output and error streams asynchronously
        this.StandardOutput = await process.StandardOutput.ReadToEndAsync();
        this.StandardError = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        this.ExitCode = process.ExitCode;
        
        Log.Verbose("git process exited with code {ExitCode}", this.ExitCode);
        Log.Verbose(this.StandardOutput);
        Log.Verbose(this.StandardError);
        GitHubOutputBuilder.EndGroup();

        if (this.ExitCode != 0)
        {
            throw new GitCommandException(
                $"Git command failed with exit code {this.ExitCode}",
                arguments,
                this.StandardOutput,
                this.StandardError,
                this.ExitCode);
        }
    }

    private void SetupProcess(Process process, string arguments)
    {
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = this.WorkingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
    
    /// <summary>
    /// Exception thrown when a git command fails
    /// </summary>
    public class GitCommandException : Exception
    {
        /// <summary>
        /// The git command arguments that were executed
        /// </summary>
        public string Arguments { get; }

        /// <summary>
        /// Standard output from the failed command
        /// </summary>
        public string StandardOutput { get; }

        /// <summary>
        /// Standard error from the failed command
        /// </summary>
        public string StandardError { get; }

        /// <summary>
        /// Exit code from the failed command
        /// </summary>
        public int ExitCode { get; }

        /// <summary>
        /// Initializes a new instance of the GitCommandException class
        /// </summary>
        public GitCommandException(string message, string arguments, string standardOutput, string standardError, int exitCode)
            : base(message)
        {
            this.Arguments = arguments;
            this.StandardOutput = standardOutput;
            this.StandardError = standardError;
            this.ExitCode = exitCode;
        }

        /// <summary>
        /// Returns a formatted string with the complete error information
        /// </summary>
        /// <returns>Formatted error details</returns>
        public string GetFormattedError()
        {
            return $"Git Command Error:\n" +
                   $"Message: {this.Message}\n" +
                   $"Command: git {this.Arguments}\n" +
                   $"Exit Code: {this.ExitCode}\n" +
                   (string.IsNullOrWhiteSpace(this.StandardOutput) ? "" : $"\nStandard Output:\n{this.StandardOutput}\n") +
                   (string.IsNullOrWhiteSpace(this.StandardError) ? "" : $"\nStandard Error:\n{this.StandardError}");
        }
    }
}

