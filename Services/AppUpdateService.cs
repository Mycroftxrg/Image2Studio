using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
#if ANDROID
using Android.Content;
using Microsoft.Maui.ApplicationModel;
#endif

namespace Image2Studio.Services;

public sealed class MissingPlatformUpdateAssetException : InvalidOperationException
{
    public MissingPlatformUpdateAssetException(string message) : base(message)
    {
    }
}

public sealed class AppUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public AppUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Image2Studio", CurrentVersionText));
    }

    public static string CurrentVersionText
    {
        get
        {
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (assemblyVersion is not null &&
                (assemblyVersion.Major != 1 || assemblyVersion.Minor != 0 || assemblyVersion.Build != 0))
            {
                return assemblyVersion.ToString(3);
            }

            return AppInfo.Current.VersionString
                ?? assemblyVersion?.ToString(3)
                ?? "1.0";
        }
    }

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

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidOperationException("更新信息缺少版本号。");
        }

        var asset = ResolvePlatformAsset(manifest);

        var currentVersion = ParseVersion(CurrentVersionText);
        var latestVersion = ParseVersion(manifest.Version);

        return new UpdateCheckResult(
            IsUpdateAvailable: latestVersion > currentVersion,
            CurrentVersion: CurrentVersionText,
            LatestVersion: manifest.Version.Trim().TrimStart('v', 'V'),
            DownloadUrl: asset.Url,
            Sha256: asset.Sha256,
            Notes: manifest.Notes?.Trim() ?? string.Empty,
            ChangelogUrl: manifest.ChangelogUrl?.Trim() ?? string.Empty);
    }

    public async Task<string> GetUpdateNotesAsync(UpdateCheckResult update, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.ChangelogUrl) ||
            !Uri.TryCreate(update.ChangelogUrl, UriKind.Absolute, out var uri))
        {
            return update.Notes;
        }

        try
        {
            var changelog = await _httpClient.GetStringAsync(uri, cancellationToken);
            if (string.IsNullOrWhiteSpace(changelog))
            {
                return update.Notes;
            }

            var relevantNotes = ExtractRelevantChangelog(changelog, update);
            return string.IsNullOrWhiteSpace(relevantNotes) ? update.Notes : relevantNotes;
        }
        catch
        {
            return update.Notes;
        }
    }

    public async Task<string> DownloadInstallerAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("更新下载地址无效。");
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        var expectedExtension = GetInstallerExtension();
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            fileName = GetDefaultInstallerFileName(update.LatestVersion);
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
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(targetPath))
        {
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

            await output.FlushAsync(cancellationToken);
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

    public Process LaunchInstaller(string installerPath)
    {
#if ANDROID
        LaunchAndroidPackageInstaller(installerPath);
        return Process.GetCurrentProcess();
#else
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("更新安装包不存在。", installerPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? FileSystem.Current.CacheDirectory
        };

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("未能启动更新安装程序。");
        }

        return process;
#endif
    }

    public void LaunchInstallerForUpdate(string installerPath)
    {
        var installerProcess = LaunchInstaller(installerPath);
#if WINDOWS
        ScheduleInstallerCleanup(installerPath, installerProcess.Id);
#endif
    }

    private static void ScheduleInstallerCleanup(string installerPath, int installerProcessId)
    {
        var cleanupPath = Path.Combine(
            Path.GetDirectoryName(installerPath) ?? FileSystem.Current.CacheDirectory,
            $"cleanup-update-{DateTime.Now:yyyyMMddHHmmss}.cmd");
        var escapedInstallerPath = installerPath.Replace("\"", "\"\"");
        var script = string.Join(Environment.NewLine, new[]
        {
            "@echo off",
            "setlocal",
            $"set \"installer={escapedInstallerPath}\"",
            $"set \"pid={installerProcessId}\"",
            "if \"%pid%\"==\"\" goto delete_installer",
            ":wait_process",
            "tasklist /FI \"PID eq %pid%\" | find \"%pid%\" >nul 2>nul",
            "if errorlevel 1 goto delete_installer",
            "timeout /t 2 /nobreak >nul",
            "goto wait_process",
            ":delete_installer",
            "for /L %%i in (1,1,90) do (",
            "  del /F /Q \"%installer%\" >nul 2>nul",
            "  if not exist \"%installer%\" goto done",
            "  timeout /t 2 /nobreak >nul",
            ")",
            ":done",
            "del /F /Q \"%~f0\" >nul 2>nul"
        });

        File.WriteAllText(cleanupPath, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = cleanupPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(cleanupPath) ?? FileSystem.Current.CacheDirectory
        });
    }

    public static bool ShouldRunAutoCheck(Image2Settings settings)
    {
        return settings.AutoCheckUpdates && (OperatingSystem.IsWindows() || OperatingSystem.IsAndroid());
    }

    private static (string Url, string Sha256) ResolvePlatformAsset(UpdateManifest manifest)
    {
#if ANDROID
        var url = FirstNonEmpty(manifest.AndroidUrl, IsApkUrl(manifest.Url) ? manifest.Url : null);
        var sha256 = FirstNonEmpty(manifest.AndroidSha256, IsApkUrl(manifest.Url) ? manifest.Sha256 : null);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new MissingPlatformUpdateAssetException("更新信息缺少 Android APK 下载地址。");
        }
#elif WINDOWS
        var url = FirstNonEmpty(manifest.WindowsUrl, IsExeUrl(manifest.Url) ? manifest.Url : null, manifest.Url);
        var sha256 = FirstNonEmpty(manifest.WindowsSha256, manifest.Sha256);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new MissingPlatformUpdateAssetException("更新信息缺少 Windows 安装包下载地址。");
        }
#else
        var url = FirstNonEmpty(manifest.Url);
        var sha256 = FirstNonEmpty(manifest.Sha256);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new MissingPlatformUpdateAssetException("当前平台没有可用的更新安装包。");
        }
#endif

        return (url.Trim(), sha256?.Trim() ?? string.Empty);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsApkUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.AbsolutePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExeUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetInstallerExtension()
    {
#if ANDROID
        return ".apk";
#else
        return ".exe";
#endif
    }

    private static string GetDefaultInstallerFileName(string version)
    {
#if ANDROID
        return $"Image2Studio-{version}-android.apk";
#else
        return $"Image2StudioSetup-{version}-win-x64.exe";
#endif
    }

#if ANDROID
    private static void LaunchAndroidPackageInstaller(string apkPath)
    {
        if (!File.Exists(apkPath))
        {
            throw new FileNotFoundException("更新安装包不存在。", apkPath);
        }

        var activity = Platform.CurrentActivity ??
                       throw new InvalidOperationException("找不到当前 Android Activity。");
        var authority = $"{activity.PackageName}.fileprovider";
        var apkUri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, authority, new Java.IO.File(apkPath));
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(intent);
    }
#endif

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

    private static string ExtractRelevantChangelog(string changelog, UpdateCheckResult update)
    {
        var currentVersion = NormalizeVersion(ParseVersion(update.CurrentVersion));
        var latestVersion = NormalizeVersion(ParseVersion(update.LatestVersion));
        var sections = new List<string>();
        var sectionLines = new List<string>();
        var includeSection = false;

        foreach (var line in changelog.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (TryParseChangelogHeadingVersion(line, out var headingVersion))
            {
                AddCurrentSection();
                sectionLines.Clear();
                sectionLines.Add(line.TrimEnd());

                var normalizedHeadingVersion = NormalizeVersion(headingVersion);
                includeSection = normalizedHeadingVersion == latestVersion && latestVersion > currentVersion;
                continue;
            }

            if (sectionLines.Count > 0)
            {
                sectionLines.Add(line.TrimEnd());
            }
        }

        AddCurrentSection();
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", sections);

        void AddCurrentSection()
        {
            if (!includeSection || sectionLines.Count == 0)
            {
                return;
            }

            while (sectionLines.Count > 0 && string.IsNullOrWhiteSpace(sectionLines[^1]))
            {
                sectionLines.RemoveAt(sectionLines.Count - 1);
            }

            if (sectionLines.Count > 0)
            {
                sections.Add(string.Join(Environment.NewLine, sectionLines).Trim());
            }
        }
    }

    private static bool TryParseChangelogHeadingVersion(string line, out Version version)
    {
        version = new Version(0, 0);
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("## ", StringComparison.Ordinal) || trimmed.StartsWith("### ", StringComparison.Ordinal))
        {
            return false;
        }

        var heading = trimmed[3..].Trim();
        var separatorIndex = heading.IndexOfAny(new[] { ' ', '-' });
        var token = separatorIndex >= 0 ? heading[..separatorIndex] : heading;
        token = token.Trim('[', ']', 'v', 'V');
        if (!Version.TryParse(token, out var parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            version.Major < 0 ? 0 : version.Major,
            version.Minor < 0 ? 0 : version.Minor,
            version.Build < 0 ? 0 : version.Build,
            version.Revision < 0 ? 0 : version.Revision);
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
        string? Notes,
        string? ChangelogUrl,
        string? WindowsUrl,
        string? WindowsSha256,
        string? AndroidUrl,
        string? AndroidSha256);
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string DownloadUrl,
    string Sha256,
    string Notes,
    string ChangelogUrl);
