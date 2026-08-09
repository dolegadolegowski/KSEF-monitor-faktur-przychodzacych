using System;
using System.Reflection;

namespace KsefMonitor;

internal static class ProductInformation
{
    public const string SourceRepositoryUrl =
        "https://github.com/dolegadolegowski/KSEF-monitor-faktur-przychodzacych";

    public static Uri LatestReleaseApiUri { get; } = new(
        "https://api.github.com/repos/dolegadolegowski/KSEF-monitor-faktur-przychodzacych/releases/latest");

    public const string WindowsReleaseAssetName = "KSeFMonitor.exe";
    public const string WindowsReleaseChecksumAssetName = "KSeFMonitor.exe.sha256";

    public const string GitHubApiVersion = "2026-03-10";
    public const string UpdateDirectoryName = ".ksef-update";

    public static SemanticVersion CurrentVersion { get; } = SemanticVersion.FromAssemblyVersion(
        Assembly.GetExecutingAssembly().GetName().Version);

    public static string DisplayVersion => $"v{CurrentVersion}";

    public static string UserAgent => $"KSeFMonitor/{CurrentVersion} (Windows 11)";
}
