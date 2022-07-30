using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
                    });
                }
            }
        }

        return tasks;
    }

    private async Task RestorePackages(BuildTask task, DirectoryInfo workFolder, DirectoryInfo pkgFolder)
    {
        var lockFile = new FileInfo(Path.Combine(workFolder.FullName, task.Manifest.Plugin.ProjectPath, "packages.lock.json"));

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
        
        foreach (var dependency in runtime.Value)
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
        public string Key { get; set; }
    };
    
    private async Task<string> GetDiffUrl(DirectoryInfo workDir, string haveCommit, string wantCommit)
    {
        if (string.IsNullOrEmpty(haveCommit))
        {
            var revListPsi = new ProcessStartInfo("git", "rev-list --max-parents=0 HEAD")
            {
                RedirectStandardOutput = true,
                WorkingDirectory = workDir.FullName,
            };

            var revListProcess = Process.Start(revListPsi);
            if (revListProcess == null)
                throw new Exception("Could not start rev-list");

            haveCommit = Regex.Replace(revListProcess.StandardOutput.ReadToEnd(), @"\t|\n|\r", "");

            if (revListProcess.ExitCode != 0)
                throw new Exception("Rev-list did not succeed");
            
            Log.Information("Using {NewRev} as have", haveCommit);
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

        if (process.ExitCode != 0)
            throw new Exception($"Git could not diff: {process.ExitCode} -- {diffPsi.Arguments}");

        using var client = new HttpClient();
        var res = await client.PostAsync("https://haste.soulja-boy-told.me/documents", new StringContent(output));
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<HasteResponse>();

        return $"https://haste.soulja-boy-told.me/{json!.Key}.diff";
    }

    public class BuildResult
    {
        public bool Success { get; set; }
        
        public string DiffUrl { get; set; }
    }
    
    public async Task<BuildResult> ProcessTask(BuildTask task, bool commit)
    {
        var folderName = $"{task.InternalName}-{task.Manifest.Plugin.Commit}";
        var work = this.workFolder.CreateSubdirectory($"{folderName}-work");
        var output = this.workFolder.CreateSubdirectory($"{folderName}-output");
        var packages = this.workFolder.CreateSubdirectory($"{folderName}-packages");

        if (task.Manifest.Plugin.ProjectPath.Contains(".."))
            throw new Exception("Not allowed");
        
        Debug.Assert(staticFolder.Exists);
        
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

        if (!dpOutput.Exists)
            throw new Exception("DalamudPackager output not found?");

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

        if (exitCode == 0 && commit)
        {
            try
            {
                this.pluginRepository.UpdatePluginHave(task.Channel, task.InternalName, task.Manifest.Plugin.Commit);
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

        return new BuildResult
        {
            DiffUrl = diffUrl,
            Success = exitCode == 0,
        };
    }

    public class PluginCommitException : Exception
    {
        public PluginCommitException(Exception inner)
            : base("Could not commit plugin.", inner)
        {
        }
    }
}