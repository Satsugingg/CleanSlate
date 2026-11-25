using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace CleanSlate.Settings.Services;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentTag,
    string LatestTag,
    string? DownloadUrl,
    string? ReleaseUrl,
    long AssetSizeBytes);

public sealed class UpdateCheckerService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/Satsugingg/CleanSlate/releases/latest";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSlate";
    private const string SetupAssetName = "CleanSlate-Setup.exe";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static Version CurrentVersion
    {
        get
        {
            Version? raw = Assembly.GetExecutingAssembly().GetName().Version;
            if (raw is null)
            {
                return new Version(1, 1, 0);
            }

            return new Version(raw.Major, raw.Minor, raw.Build >= 0 ? raw.Build : 0);
        }
    }

    public static string CurrentVersionTag => $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = doc.RootElement;

        string latestTag = root.TryGetProperty("tag_name", out JsonElement tagEl)
            ? tagEl.GetString() ?? string.Empty
            : string.Empty;

        string? releaseUrl = root.TryGetProperty("html_url", out JsonElement htmlEl)
            ? htmlEl.GetString()
            : null;

        string? downloadUrl = null;
        long assetSize = 0;

        if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetsEl.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                if (!string.Equals(name, SetupAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlEl)
                    ? urlEl.GetString()
                    : null;
                assetSize = asset.TryGetProperty("size", out JsonElement sizeEl) && sizeEl.TryGetInt64(out long bytes)
                    ? bytes
                    : 0;
                break;
            }
        }

        Version? latestVersion = ParseTagVersion(latestTag);
        bool isUpdateAvailable = latestVersion is not null && latestVersion > CurrentVersion;

        return new UpdateCheckResult(
            IsUpdateAvailable: isUpdateAvailable,
            CurrentTag: CurrentVersionTag,
            LatestTag: string.IsNullOrWhiteSpace(latestTag) ? CurrentVersionTag : latestTag,
            DownloadUrl: downloadUrl,
            ReleaseUrl: releaseUrl,
            AssetSizeBytes: assetSize);
    }

    public async Task DownloadAndInstallAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Invalid update download URL.");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "CleanSlate-Update");
        Directory.CreateDirectory(tempDir);
        string installerPath = Path.Combine(tempDir, SetupAssetName);

        using (HttpResponseMessage response = await HttpClient.GetAsync(
                   uri,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken))
        {
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using Stream networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream fileStream = new(
                installerPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await networkStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                if (totalBytes is > 0)
                {
                    progress?.Report(Math.Clamp(totalRead * 100.0 / totalBytes.Value, 0, 100));
                }
            }
        }

        FileInfo info = new(installerPath);
        if (!info.Exists || info.Length < 1024)
        {
            throw new InvalidOperationException("Downloaded installer appears incomplete.");
        }

        using (FileStream fs = File.OpenRead(installerPath))
        {
            byte[] magic = new byte[2];
            if (fs.Read(magic, 0, 2) < 2 || magic[0] != (byte)'M' || magic[1] != (byte)'Z')
            {
                throw new InvalidOperationException("Downloaded file is not a valid Windows executable.");
            }
        }

        string installDir = ResolveInstallDirectory();
        ProcessStartInfo psi = new()
        {
            FileName = installerPath,
            Arguments = $"--silent --dir \"{installDir}\"",
            UseShellExecute = true,
            WorkingDirectory = tempDir
        };

        Process.Start(psi);
    }

    private static string ResolveInstallDirectory()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath, writable: false);
            string? regDir = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(regDir) && Directory.Exists(regDir))
            {
                return Path.GetFullPath(regDir);
            }
        }
        catch
        {
            // Fall back to current application directory.
        }

        return Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static Version? ParseTagVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string cleaned = tag.Trim().TrimStart('v', 'V');
        int dashIndex = cleaned.IndexOf('-');
        if (dashIndex >= 0)
        {
            cleaned = cleaned[..dashIndex];
        }

        if (!Version.TryParse(cleaned, out Version? parsed))
        {
            return null;
        }

        return new Version(parsed.Major, parsed.Minor, parsed.Build >= 0 ? parsed.Build : 0);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CleanSlate-Settings/{CurrentVersion}");
        return client;
    }
}
