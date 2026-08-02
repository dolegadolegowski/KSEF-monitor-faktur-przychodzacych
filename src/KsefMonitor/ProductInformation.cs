using System;

namespace KsefMonitor;

internal static class ProductInformation
{
    public const string SourceRepositoryUrl =
        "https://github.com/dolegadolegowski/KSEF-monitor-faktur-przychodzacych";

    public static Uri LatestReleaseApiUri { get; } = new(
        "https://api.github.com/repos/dolegadolegowski/KSEF-monitor-faktur-przychodzacych/releases/latest");

    public const string WindowsReleaseAssetName = "KSeFMonitor.exe";
    public const string WindowsReleaseChecksumAssetName = "KSeFMonitor.exe.sha256";
}
