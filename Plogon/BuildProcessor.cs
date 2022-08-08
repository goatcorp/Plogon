using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using LibGit2Sharp;
using Newtonsoft.Json;
using Plogon.Manifests;
using Plogon.Repo;
using Serilog;

namespace Plogon;

/// <summary>
/// Class that generates and processes build tasks
/// </summary>
public class BuildProcessor
{
    private readonly DirectoryInfo repoFolder;
    private readonly DirectoryInfo manifestFolder;
    private readonly DirectoryInfo workFolder;
    private readonly DirectoryInfo staticFolder;
    private readonly DirectoryInfo artifactFolder;


    private readonly DockerClient dockerClient;

    private PluginRepository pluginRepository;
    private ManifestStorage manifestStorage;
    private DalamudReleases dalamudReleases;

    private const string DOCKER_IMAGE = "mcr.microsoft.com/dotnet/sdk";
    private const string DOCKER_TAG = "6.0.300";

    /// <summary>
    /// Set up build processor
    /// </summary>
    /// <param name="repoFolder">Repo</param>
    /// <param name="manifestFolder">Manifests</param>
    /// <param name="workFolder">Work</param>
    /// <param name="staticFolder">Static</param>
    /// <param name="artifactFolder">Artifacts</param>
    public BuildProcessor(DirectoryInfo repoFolder, DirectoryInfo manifestFolder, DirectoryInfo workFolder,
        DirectoryInfo staticFolder, DirectoryInfo artifactFolder)
    {
        this.repoFolder = repoFolder;
        this.manifestFolder = manifestFolder;
        this.workFolder = workFolder;
        this.staticFolder = staticFolder;
        this.artifactFolder = artifactFolder;

        this.pluginRepository = new PluginRepository(repoFolder);
        this.manifestStorage = new ManifestStorage(manifestFolder);
        this.dalamudReleases = new DalamudReleases(workFolder.CreateSubdirectory("dalamud_releases_work"));

        this.dockerClient = new DockerClientConfiguration().CreateClient();
    }

    /// <summary>
    /// Set up needed docker images for containers.
    /// </summary>
    /// <returns>List of images</returns>
    public async Task<List<ImageInspectResponse>> SetupDockerImage()
    {
        await this.dockerClient.Images.CreateImageAsync(new ImagesCreateParameters
            {
                //FromImage = "fedora/memcached",
                FromImage = DOCKER_IMAGE,
                //FromSrc = DOCKER_REPO,
                Tag = DOCKER_TAG,
            }, null,
            new Progress<JSONMessage>(progress =>
            {
                Log.Verbose("Docker image pull ({Id}): {Status}", progress.ID, progress.Status);
            }));

        var images = await this.dockerClient.Images.ListImagesAsync(new ImagesListParameters
        {
            All = true,
        });

        List<ImageInspectResponse> inspects = new();
        foreach (var imagesListResponse in images)
        {
            var inspect = await this.dockerClient.Images.InspectImageAsync(imagesListResponse.ID);
            inspects.Add(inspect);
        }

        return inspects;
    }

    /// <summary>
    /// Get all tasks that need to be done
    /// </summary>
    /// <returns>A set of tasks that are pending</returns>
    public ISet<BuildTask> GetTasks()
    {
        var tasks = new HashSet<BuildTask>();

        foreach (var channel in this.manifestStorage.Channels)
        {
            foreach (var manifest in channel.Value)
            {
                var state = this.pluginRepository.GetPluginState(channel.Key, manifest.Key);

                if (state == null || state.BuiltCommit != manifest.Value.Plugin.Commit)
                {
                    tasks.Add(new BuildTask
                    {
                        InternalName = manifest.Key,
                        Manifest = manifest.Value,
                        Channel = channel.Key,
                        HaveCommit = state?.BuiltCommit,
                        HaveTimeBuilt = state?.TimeBuilt,
                        HaveVersion = state?.EffectiveVersion,
                    });
                }
            }
        }

        return tasks;
    }

    private async Task RestorePackages(BuildTask task, DirectoryInfo workFolder, DirectoryInfo pkgFolder)
    {
        var lockFile = new FileInfo(Path.Combine(workFolder.FullName, task.Manifest.Plugin.ProjectPath!, "packages.lock.json"));

        if (!lockFile.Exists)
            throw new Exception("Lock file not present - please set \"RestorePackagesWithLockFile\" to true in your project file!");

        var lockFileData = JsonConvert.DeserializeObject<NugetLockfile>(File.ReadAllText(lockFile.FullName));

        if (lockFileData.Version != 1)
            throw new Exception($"Unknown lockfile version: {lockFileData.Version}");

        var runtime = lockFileData.Runtimes.First();
        Log.Information("Getting packages for runtime {Runtime}", runtime.Key);

        using var client = new HttpClient();

        async Task GetDep(string name, NugetLockfile.Dependency dependency)
        {
            Log.Information("   => Getting {DepName}(v{Version})", name, dependency.Resolved);

            var pkgName = name.ToLower();
            var fileName = $"{pkgName}.{dependency.Resolved}.nupkg";
            var url =
                $"https://api.nuget.org/v3-flatcontainer/{pkgName}/{dependency.Resolved}/{fileName}";

            var data = await client.GetByteArrayAsync(url);
            
            // TODO: verify content hash
            
            await File.WriteAllBytesAsync(Path.Combine(pkgFolder.FullName, fileName), data);
        }
        
        foreach (var dependency in runtime.Value.Where(x => x.Value.Type != NugetLockfile.Dependency.DependencyType.Project))
        {
            await GetDep(dependency.Key, dependency.Value);
        }

        await GetDep("Microsoft.NETCore.App.Ref", new NugetLockfile.Dependency()
        {
            Resolved = "5.0.0"
        });
        
        await GetDep("Microsoft.AspNetCore.App.Ref", new NugetLockfile.Dependency()
        {
            Resolved = "5.0.0"
        });
    }

    private class HasteResponse
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }
    };
    
    private async Task<string> GetDiffUrl(DirectoryInfo workDir, string haveCommit, string wantCommit)
    {
        if (string.IsNullOrEmpty(haveCommit))
        {
            haveCommit = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"; // "empty tree"
        }
        
        var diffPsi = new ProcessStartInfo("git",
            $"diff --submodule=diff {haveCommit}..{wantCommit}")
        {
            RedirectStandardOutput = true,
            WorkingDirectory = workDir.FullName,
        };

        var process = Process.Start(diffPsi);
        if (process == null)
            throw new Exception("Diff process was null.");

        var output = await process.StandardOutput.ReadToEndAsync();

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception($"Git could not diff: {process.ExitCode} -- {diffPsi.Arguments}");
        
        Log.Verbose("{Args}: {Length}", diffPsi.Arguments, output.Length);

        using var client = new HttpClient();
        var res = await client.PostAsync("https://haste.soulja-boy-told.me/documents", new StringContent(output));
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<HasteResponse>();

        return $"https://haste.soulja-boy-told.me/{json!.Key}.diff";
    }

    /// <summary>
    /// Info about build status
    /// </summary>
    public class BuildResult
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="success">If it worked</param>
        /// <param name="diffUrl">diff url</param>
        /// <param name="version">plugin version</param>
        public BuildResult(bool success, string diffUrl, string? version)
        {
            this.Success = success;
            this.DiffUrl = diffUrl;
            this.Version = version;
        }
        
        /// <summary>
        /// If it worked
        /// </summary>
        public bool Success { get; private set; }
        
        /// <summary>
        /// Where the diff is
        /// </summary>
        public string DiffUrl { get; private set; }

        /// <summary>
        /// The version of the plugin artifact
        /// </summary>
        public string? Version { get; private set; }
    }

    private class LegacyPluginManifest
    {
        [JsonProperty]
        public string? AssemblyVersion { get; set; }
    }
    
    /// <summary>
    /// Check out and build a plugin from a task
    /// </summary>
    /// <param name="task">The task to build</param>
    /// <param name="commit">Whether or not the plugin should be committed to the repo</param>
    /// <returns>The result of the build</returns>
    /// <exception cref="Exception">Generic build system errors</exception>
    /// <exception cref="PluginCommitException">Error during repo commit, all no further work should be done</exception>
    public async Task<BuildResult> ProcessTask(BuildTask task, bool commit)
    {
        var folderName = $"{task.InternalName}-{task.Manifest.Plugin.Commit}";
        var work = this.workFolder.CreateSubdirectory($"{folderName}-work");
        var output = this.workFolder.CreateSubdirectory($"{folderName}-output");
        var packages = this.workFolder.CreateSubdirectory($"{folderName}-packages");

        Debug.Assert(staticFolder.Exists);

        if (string.IsNullOrWhiteSpace(task.Manifest.Plugin.Repository))
            throw new Exception("No repository specified");
        
        if (!task.Manifest.Plugin.Repository.StartsWith("https://") ||
            !task.Manifest.Plugin.Repository.EndsWith(".git"))
            throw new Exception("You can only use HTTPS git endpoints for your plugin.");

        if (string.IsNullOrWhiteSpace(task.Manifest.Plugin.Commit))
            throw new Exception("No commit specified");

        task.Manifest.Plugin.ProjectPath ??= string.Empty;
        
        if (task.Manifest.Plugin.ProjectPath.Contains(".."))
            throw new Exception("Not allowed");

        if (!work.Exists || work.GetFiles().Length == 0)
        {
            Repository.Clone(task.Manifest.Plugin.Repository, work.FullName, new CloneOptions
            {
                Checkout = false,
                RecurseSubmodules = false,
                OnProgress = output =>
                {
                    Log.Verbose("Cloning: {GitOutput}", output);
                    return true;
                }
            });
        }

        var repo = new Repository(work.FullName);
        Commands.Fetch(repo, "origin", new string[] { task.Manifest.Plugin.Commit }, new FetchOptions
        {
            OnProgress = output =>
            {
                Log.Verbose("Fetching: {GitOutput}", output);
                return true;
            }
        }, null);
        repo.Reset(ResetMode.Hard, task.Manifest.Plugin.Commit);

        foreach (var submodule in repo.Submodules)
        {
            repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions
            {
                Init = true,
                OnProgress = output =>
                {
                    Log.Verbose("Updating submodule {ModuleName}: {GitOutput}", submodule.Name, output);
                    return true;
                }
            });
        }
        
        var diffUrl = await GetDiffUrl(work, task.HaveCommit!, task.Manifest.Plugin.Commit);

        var dalamudAssemblyDir = await this.dalamudReleases.GetDalamudAssemblyDirAsync(task.Channel);

        await RestorePackages(task, work, packages);
        
        var containerCreateResponse = await this.dockerClient.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = $"{DOCKER_IMAGE}:{DOCKER_TAG}",
                
                NetworkDisabled = true,

                AttachStderr = true,
                AttachStdout = true,
                HostConfig = new HostConfig
                {
                    Privileged = false,
                    IpcMode = "none",
                    AutoRemove = false,
                    Binds = new List<string>
                    {
                        $"{work.FullName}:/work/repo",
                        $"{dalamudAssemblyDir.FullName}:/work/dalamud:ro",
                        $"{staticFolder.FullName}:/static:ro",
                        $"{output.FullName}:/output",
                        $"{packages.FullName}:/packages:ro",
                    }
                },
                Env = new List<string>
                {
                    $"PLOGON_PROJECT_DIR={task.Manifest.Plugin.ProjectPath}",
                    $"PLOGON_PLUGIN_NAME={task.InternalName}",
                    $"PLOGON_PLUGIN_COMMIT={task.Manifest.Plugin.Commit}",
                    $"PLOGON_PLUGIN_VERSION={task.Manifest.Plugin.Version}",
                    "DALAMUD_LIB_PATH=/work/dalamud/"
                },
                Entrypoint = new List<string>
                {
                    "/static/entrypoint.sh"
                }
            });

        var startResponse =
            await this.dockerClient.Containers.StartContainerAsync(containerCreateResponse.ID,
                new ContainerStartParameters());

        if (!startResponse)
        {
            throw new Exception("Couldn't start container");
        }

        var logResponse = await this.dockerClient.Containers.GetContainerLogsAsync(containerCreateResponse.ID, false,
            new ContainerLogsParameters
            {
                Follow = true,
                ShowStderr = true,
                ShowStdout = true,
            });

        var hasExited = false;
        while (!hasExited)
        {
            var inspectResponse = await this.dockerClient.Containers.InspectContainerAsync(containerCreateResponse.ID);
            hasExited = inspectResponse.State.Running == false;

            // Get logs from multiplexed stream
            var buffer = new byte[4096];
            var eof = false;
            while (!eof)
            {
                var result = await logResponse.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
                eof = result.EOF;

                var log = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                Log.Information(log.Replace("\n", string.Empty));
            }
        }

        var containerInspectResponse =
            await this.dockerClient.Containers.InspectContainerAsync(containerCreateResponse.ID);
        var exitCode = containerInspectResponse.State.ExitCode;

        Log.Information("Container for build exited, exit code: {Code}", exitCode);

        await this.dockerClient.Containers.RemoveContainerAsync(containerCreateResponse.ID,
            new ContainerRemoveParameters
            {
                Force = true,
            });

        var dpOutput = new DirectoryInfo(Path.Combine(output.FullName, task.InternalName));
        string? version = null;

        if (dpOutput.Exists)
        {
            var artifact = this.artifactFolder.CreateSubdirectory($"{task.InternalName}-{task.Manifest.Plugin.Commit}");
            try
            {
                foreach (var file in dpOutput.GetFiles())
                {
                    file.CopyTo(Path.Combine(artifact.FullName, file.Name), true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not copy to artifact output");
                throw new Exception("Could not copy to artifact", ex);
            }

            try
            {
                var manifestFile = new FileInfo(Path.Combine(dpOutput.FullName, $"{task.InternalName}.json"));
                if (!manifestFile.Exists)
                    throw new Exception("Generated manifest didn't exist");

                var manifestText = await manifestFile.OpenText().ReadToEndAsync();
                var manifest = JsonConvert.DeserializeObject<LegacyPluginManifest>(manifestText);

                if (manifest == null)
                    throw new Exception("Generated manifest was null");

                version = manifest.AssemblyVersion ?? throw new Exception("AssemblyVersion in generated manifest was null");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't read generated manifest");
                if (exitCode == 0)
                    throw;
            }

            if (exitCode == 0 && commit)
            {
                try
                {
                    this.pluginRepository.UpdatePluginHave(task.Channel, task.InternalName, task.Manifest.Plugin.Commit, version!);
                    var repoOutputDir = this.pluginRepository.GetPluginOutputDirectory(task.Channel, task.InternalName);

                    foreach (var file in dpOutput.GetFiles())
                    {
                        file.CopyTo(Path.Combine(repoOutputDir.FullName, file.Name), true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error during plugin commit");
                    throw new PluginCommitException(ex);
                }
            }
        }
        else if (exitCode == 0)
        {
            throw new Exception("DalamudPackager output not found, make sure it is installed");
        }
        
        return new BuildResult(exitCode == 0, diffUrl, version);
    }

    /// <summary>
    /// Exception when repo commit fails
    /// </summary>
    public class PluginCommitException : Exception
    {
        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="inner">Actual error</param>
        public PluginCommitException(Exception inner)
            : base("Could not commit plugin.", inner)
        {
        }
    }
}