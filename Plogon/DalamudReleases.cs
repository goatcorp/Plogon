using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Serilog;

namespace Plogon;

public class DalamudReleases
{
    private const string URL_TEMPLATE = "https://kamori.goats.dev/Dalamud/Release/VersionInfo?track={0}";

    public DalamudReleases(DirectoryInfo releasesDir)
    {
        this.ReleasesDir = releasesDir;
    }
    
    public DirectoryInfo ReleasesDir { get; }
    
    private async Task<DalamudVersionInfo?> GetVersionInfoForTrackAsync(string track)
    {
        if (track == "stable")
            track = "release";
        
        if (track.StartsWith("testing-"))
            track = track.Split("-")[1];

        using var client = new HttpClient();
        return await client.GetFromJsonAsync<DalamudVersionInfo>(string.Format(URL_TEMPLATE, track));
    }

    public async Task<DirectoryInfo> GetDalamudAssemblyDirAsync(string track)
    {
        var versionInfo = await this.GetVersionInfoForTrackAsync(track);
        var extractDir = this.ReleasesDir.CreateSubdirectory($"{track}-{versionInfo.AssemblyVersion}");

        if (extractDir.GetFiles().Length != 0)
            return extractDir;
        
        Log.Information("Downloading Dalamud assembly for track {Track}({Version})", track, versionInfo.AssemblyVersion);

        using var client = new HttpClient();
        var zipBytes = await client.GetByteArrayAsync(versionInfo.DownloadUrl);

        // Extract the zip file to the extractDir
        using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream);
        archive.ExtractToDirectory(extractDir.FullName);

        return extractDir;
    }
    
    private class DalamudVersionInfo
    {
        public string AssemblyVersion { get; set; }
        public string SupportedGameVer { get; set; }
        public string RuntimeVersion { get; set; }
        public bool RuntimeRequired { get; set; }
        public string Key { get; set; }
        public string DownloadUrl { get; set; }
    }
}