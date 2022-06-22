using System.IO;
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
    static async Task Main(DirectoryInfo outputFolder, DirectoryInfo manifestFolder, DirectoryInfo workFolder)
    {
        SetupLogging();

        var buildProcessor = new BuildProcessor(outputFolder, manifestFolder, workFolder);
        var tasks = buildProcessor.GetTasks();

        await buildProcessor.SetupDockerImage();
        
        foreach (var task in tasks)
        {
            Log.Information("Need: {Name} - {Sha} (have {HaveCommit})", task.InternalName, task.Manifest.Plugin.Commit, task.HaveCommit ?? "nothing");
            var status = await buildProcessor.ProcessTask(task);

            if (!status)
            {
                Log.Error("Could not build: {Name} - {Sha}", task.InternalName, task.Manifest.Plugin.Commit);
            }
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