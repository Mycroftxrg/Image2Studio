using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Image2Studio.Services;

public sealed class AppUpdateService
{
    private const string LastAutoCheckKey = "update_last_auto_check_utc";
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public AppUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Image2Studio", CurrentVersionText));
    }

    public static string CurrentVersionText =>
        AppInfo.Current.VersionString
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "1.0";

    public async Task<UpdateCheckResult> CheckAsync(Image2Settings settings, CancellationToken cancellationToken = default)
    {
        var manifestUrl = string.IsNullOrWhiteSpace(settings.UpdateManifestUrl)
            ? Image2Settings.DefaultUpdateManifestUrl
            : settings.UpdateManifestUrl.Trim();

        using var response = await _httpClient.GetAsync(manifestUrl, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"检查更新失败：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(raw, JsonOptions)
            ?? throw new InvalidOperationException("更新信息格式无效。");

        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Url))
        {
            throw new InvalidOperationException("更新信息缺少版本号或下载地址。");
        }

        var currentVersion = ParseVersion(CurrentVersionText);
        var latestVersion = ParseVersion(manifest.Version);

        return new UpdateCheckResult(
            IsUpdateAvailable: latestVersion > currentVersion,
            CurrentVersion: CurrentVersionText,
            LatestVersion: manifest.Version.Trim().TrimStart('v', 'V'),
            DownloadUrl: manifest.Url.Trim(),
            Sha256: manifest.Sha256?.Trim() ?? string.Empty,
            Notes: manifest.Notes?.Trim() ?? string.Empty);
    }

    public async Task<string> DownloadInstallerAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("更新下载地址无效。");
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = $"Image2StudioSetup-{update.LatestVersion}-win-x64.exe";
        }

        var downloadDir = Path.Combine(FileSystem.Current.CacheDirectory, "updates");
        Directory.CreateDirectory(downloadDir);
        var targetPath = Path.Combine(downloadDir, fileName);

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"下载更新失败：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var totalLength = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(targetPath);

        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (totalLength is > 0)
            {
                progress?.Report((double)copied / totalLength.Value);
            }
        }

        progress?.Report(1);

        if (!string.IsNullOrWhiteSpace(update.Sha256))
        {
            var actual = await ComputeSha256Async(targetPath, cancellationToken);
            if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(targetPath);
                throw new InvalidOperationException("更新安装包校验失败，请稍后重试。");
            }
        }

        return targetPath;
    }

    public void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("更新安装包不存在。", installerPath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });
    }

    public static bool ShouldRunAutoCheck(Image2Settings settings)
    {
        if (!settings.AutoCheckUpdates)
        {
            return false;
        }

        var raw = Preferences.Default.Get(LastAutoCheckKey, string.Empty);
        return !DateTimeOffset.TryParse(raw, out var lastCheck)
            || DateTimeOffset.UtcNow - lastCheck.ToUniversalTime() >= AutoCheckInterval;
    }

    public static void MarkAutoCheckCompleted()
    {
        Preferences.Default.Set(LastAutoCheckKey, DateTimeOffset.UtcNow.ToString("O"));
    }

    private static Version ParseVersion(string value)
    {
        var clean = value.Trim().TrimStart('v', 'V');
        var dashIndex = clean.IndexOfAny(new[] { '-', '+' });
        if (dashIndex >= 0)
        {
            clean = clean[..dashIndex];
        }

        return Version.TryParse(clean, out var version)
            ? version
            : new Version(0, 0);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private sealed record UpdateManifest(
        string Version,
        string Url,
        string? Sha256,
        string? Notes);
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string DownloadUrl,
    string Sha256,
    string Notes);
