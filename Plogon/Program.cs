using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;

namespace Plogon;

class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    /// <param name="outputFolder">The folder used for storing output and state.</param>
    /// <param name="manifestFolder">The folder used for storing plugin manifests.</param>
    /// <param name="workFolder">The folder to store temporary files and build output in.</param>
    /// <param name="staticFolder">The 'static' folder that holds script files.</param>
    /// <param name="ci">Running in CI.</param>
    static async Task Main(DirectoryInfo outputFolder, DirectoryInfo manifestFolder, DirectoryInfo workFolder,
        DirectoryInfo staticFolder, bool ci = false)
    {
        SetupLogging();

        var githubSummary = "## Build Summary";
        GitHubOutputBuilder.SetActive(ci);
        
        var buildProcessor = new BuildProcessor(outputFolder, manifestFolder, workFolder, staticFolder);
        var tasks = buildProcessor.GetTasks();

        if (!tasks.Any())
        {
            Log.Information("Nothing to do, goodbye...");
            githubSummary += "No tasks were detected, this is probably an issue on our side, please report.";
        }
        else
        {
            GitHubOutputBuilder.StartGroup("Get images");
            var images = await buildProcessor.SetupDockerImage();
            Debug.Assert(images.Any(), "No images returned");

            var imagesMd = MarkdownTableBuilder.Create("Tags", "Created");
            foreach (var imageInspectResponse in images)
            {
                imagesMd.AddRow(string.Join(",", imageInspectResponse.RepoTags), imageInspectResponse.Created.ToLongDateString());
            }
            GitHubOutputBuilder.EndGroup();

            githubSummary += imagesMd.ToString();

            foreach (var task in tasks)
            {
                GitHubOutputBuilder.StartGroup($"Build {task.InternalName} ({task.Manifest.Plugin.Commit})");
                
                try
                {
                    Log.Information("Need: {Name} - {Sha} (have {HaveCommit})", task.InternalName,
                        task.Manifest.Plugin.Commit,
                        task.HaveCommit ?? "nothing");
                    var status = await buildProcessor.ProcessTask(task);

                    if (!status)
                    {
                        Log.Error("Could not build: {Name} - {Sha}", task.InternalName, task.Manifest.Plugin.Commit);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not build");
                }

                GitHubOutputBuilder.EndGroup();
            }
        }

        var githubSummaryFilePath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(githubSummaryFilePath))
        {
            await File.WriteAllTextAsync(githubSummaryFilePath, githubSummary);
        }
    }

    private static void SetupLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Verbose()
            .CreateLogger();
    }
}