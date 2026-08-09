using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KsefMonitor;

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>
{
    public static SemanticVersion FromAssemblyVersion(Version? version) => new(
        Math.Max(0, version?.Major ?? 0),
        Math.Max(0, version?.Minor ?? 0),
        Math.Max(0, version?.Build ?? 0));

    public static bool TryParseReleaseTag(string? tag, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(tag) || tag.Length is < 6 or > 32 || tag[0] != 'v') return false;
        var parts = tag.AsSpan(1).ToString().Split('.', StringSplitOptions.None);
        if (parts.Length != 3 ||
            !TryParseComponent(parts[0], out var major) ||
            !TryParseComponent(parts[1], out var minor) ||
            !TryParseComponent(parts[2], out var patch)) return false;
        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public string ToTag() => $"v{this}";

    private static bool TryParseComponent(string value, out int component)
    {
        component = 0;
        if (value.Length == 0 || value.Length > 10 || value.Length > 1 && value[0] == '0') return false;
        foreach (var character in value)
            if (character is < '0' or > '9') return false;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out component);
    }
}

internal sealed record GitHubReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string Sha256Digest);

internal sealed record GitHubReleaseInfo(
    SemanticVersion Version,
    string Tag,
    Uri ReleasePageUri,
    GitHubReleaseAsset Executable,
    GitHubReleaseAsset Checksum);

internal enum AppUpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Preparing,
    ReadyToRestart,
    Failed
}

internal sealed record AppUpdateSnapshot(
    SemanticVersion CurrentVersion,
    AppUpdatePhase Phase,
    GitHubReleaseInfo? AvailableRelease = null,
    string? Message = null,
    int? ProgressPercent = null,
    DateTimeOffset? LastCheckedUtc = null,
    bool HasError = false)
{
    public bool HasAvailableUpdate => AvailableRelease is not null && AvailableRelease.Version.CompareTo(CurrentVersion) > 0;
}

internal sealed class AppUpdateException : Exception
{
    public AppUpdateException(string technicalMessage, string userMessage)
        : base(technicalMessage) => UserMessage = userMessage;

    public AppUpdateException(string technicalMessage, string userMessage, Exception innerException)
        : base(technicalMessage, innerException) => UserMessage = userMessage;

    public string UserMessage { get; }
}

internal static class GitHubReleasePolicy
{
    public const int MaximumMetadataBytes = 512 * 1024;
    public const int MaximumChecksumBytes = 4096;
    public const long MaximumExecutableBytes = 512L * 1024 * 1024;
    public const int MaximumAssets = 64;
    public const int MaximumRedirects = 4;

    private static readonly HashSet<string> ReleaseDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com"
    };

    public static GitHubReleaseInfo ParseRelease(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > MaximumMetadataBytes)
            throw InvalidRelease("Metadane wydania mają niedozwolony rozmiar.");

        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidRelease("Odpowiedź nie jest obiektem JSON.");
            if (ReadRequiredBoolean(root, "draft") || ReadRequiredBoolean(root, "prerelease"))
                throw InvalidRelease("Najnowsze wydanie jest szkicem lub wersją testową.");
            if (!ReadRequiredBoolean(root, "immutable"))
                throw InvalidRelease("Najnowsze wydanie nie jest chronione przez GitHub Immutable Releases.");

            var tag = ReadRequiredString(root, "tag_name");
            if (!SemanticVersion.TryParseReleaseTag(tag, out var version))
                throw InvalidRelease("Tag wydania nie ma formatu vMAJOR.MINOR.PATCH.");

            var releasePage = ReadRequiredHttpsUri(root, "html_url");
            ValidateCanonicalReleasePage(releasePage, tag);

            if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
                throw InvalidRelease("Brakuje listy plików wydania.");
            var assets = assetsElement.EnumerateArray().ToArray();
            if (assets.Length > MaximumAssets) throw InvalidRelease("Wydanie zawiera zbyt wiele plików.");

            var executable = ReadSingleAsset(assets, ProductInformation.WindowsReleaseAssetName, MaximumExecutableBytes, tag);
            var checksum = ReadSingleAsset(assets, ProductInformation.WindowsReleaseChecksumAssetName, MaximumChecksumBytes, tag);
            return new GitHubReleaseInfo(version, tag, releasePage, executable, checksum);
        }
        catch (AppUpdateException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new AppUpdateException(
                "GitHub zwrócił niepoprawny JSON wydania.",
                "Nie udało się odczytać informacji o aktualizacji z GitHuba.",
                exception);
        }
    }

    public static Uri CreateCanonicalAssetUri(string tag, string assetName) => new(
        $"{ProductInformation.SourceRepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}");

    public static void ValidateInitialAssetUri(Uri uri, string tag, string assetName)
    {
        var expected = CreateCanonicalAssetUri(tag, assetName);
        if (!HaveSameAbsoluteUri(uri, expected))
            throw InvalidRelease($"Adres pliku {assetName} nie wskazuje na oczekiwane wydanie GitHub.");
    }

    public static void ValidateRedirectUri(Uri uri)
    {
        if (!IsSafeHttpsUri(uri) || !ReleaseDownloadHosts.Contains(uri.IdnHost))
            throw new AppUpdateException(
                $"Niedozwolone przekierowanie pobierania aktualizacji: {uri.GetLeftPart(UriPartial.Authority)}.",
                "GitHub przekierował pobieranie poza zaufany kanał. Aktualizacja została zatrzymana.");
    }

    public static bool TryNormalizeSha256Digest(string? value, out string normalized)
    {
        normalized = string.Empty;
        const string prefix = "sha256:";
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return TryNormalizeSha256(value[prefix.Length..], out normalized);
    }

    public static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 64) return false;
        Span<char> lower = stackalloc char[64];
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= '0' and <= '9') lower[index] = character;
            else if (character is >= 'a' and <= 'f') lower[index] = character;
            else if (character is >= 'A' and <= 'F') lower[index] = (char)(character + ('a' - 'A'));
            else return false;
        }
        normalized = new string(lower);
        return true;
    }

    public static bool HashesEqual(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return leftBytes.Length == 32 && rightBytes.Length == 32 &&
                   CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static GitHubReleaseAsset ReadSingleAsset(
        IReadOnlyList<JsonElement> assets,
        string expectedName,
        long maximumSize,
        string tag)
    {
        var matches = assets
            .Where(asset => asset.ValueKind == JsonValueKind.Object &&
                            asset.TryGetProperty("name", out var name) &&
                            name.ValueKind == JsonValueKind.String &&
                            string.Equals(name.GetString(), expectedName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw InvalidRelease($"Wydanie musi zawierać dokładnie jeden plik {expectedName}.");

        var match = matches[0];
        if (!string.Equals(ReadRequiredString(match, "state"), "uploaded", StringComparison.Ordinal))
            throw InvalidRelease($"Plik {expectedName} nie został jeszcze poprawnie przesłany.");
        if (!match.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size <= 0 || size > maximumSize)
            throw InvalidRelease($"Plik {expectedName} ma niedozwolony rozmiar.");

        var downloadUri = ReadRequiredHttpsUri(match, "browser_download_url");
        ValidateInitialAssetUri(downloadUri, tag, expectedName);
        var digest = ReadRequiredString(match, "digest");
        if (!TryNormalizeSha256Digest(digest, out var normalizedDigest))
            throw InvalidRelease($"Plik {expectedName} nie ma poprawnego skrótu SHA-256 w metadanych GitHub.");
        return new GitHubReleaseAsset(expectedName, downloadUri, size, normalizedDigest);
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString())) throw InvalidRelease($"Brakuje pola {name}.");
        return property.GetString()!;
    }

    private static bool ReadRequiredBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidRelease($"Brakuje pola {name}.");
        return property.GetBoolean();
    }

    private static Uri ReadRequiredHttpsUri(JsonElement element, string name)
    {
        var value = ReadRequiredString(element, name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsSafeHttpsUri(uri))
            throw InvalidRelease($"Pole {name} nie zawiera bezpiecznego adresu HTTPS.");
        return uri;
    }

    private static void ValidateCanonicalReleasePage(Uri uri, string tag)
    {
        var expected = new Uri($"{ProductInformation.SourceRepositoryUrl}/releases/tag/{Uri.EscapeDataString(tag)}");
        if (!HaveSameAbsoluteUri(uri, expected)) throw InvalidRelease("Strona wydania wskazuje na inne repozytorium.");
    }

    private static bool HaveSameAbsoluteUri(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port &&
        string.Equals(left.AbsolutePath, right.AbsolutePath, StringComparison.Ordinal) &&
        string.Equals(left.Query, right.Query, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(left.Fragment) &&
        string.IsNullOrEmpty(right.Fragment) &&
        string.IsNullOrEmpty(left.UserInfo) &&
        string.IsNullOrEmpty(right.UserInfo);

    private static bool IsSafeHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == 443 &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static AppUpdateException InvalidRelease(string technicalMessage) => new(
        technicalMessage,
        "Wydanie na GitHubie jest niekompletne lub nie spełnia wymagań bezpieczeństwa. Aktualizacja została zatrzymana.");
}

internal static class ReleaseChecksumParser
{
    public static string Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > GitHubReleasePolicy.MaximumChecksumBytes)
            throw InvalidChecksum("Plik sumy kontrolnej ma niedozwolony rozmiar.");
        foreach (var value in content)
            if (value > 0x7f) throw InvalidChecksum("Plik sumy kontrolnej nie jest zapisany jako ASCII.");

        var text = Encoding.ASCII.GetString(content);
        if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
        else if (text.EndsWith('\n')) text = text[..^1];
        if (text.Contains('\r') || text.Contains('\n'))
            throw InvalidChecksum("Plik sumy kontrolnej zawiera więcej niż jeden wiersz.");

        var suffix = $"  {ProductInformation.WindowsReleaseAssetName}";
        if (text.Length != 64 + suffix.Length || !text.EndsWith(suffix, StringComparison.Ordinal) ||
            !GitHubReleasePolicy.TryNormalizeSha256(text[..64], out var hash))
            throw InvalidChecksum("Plik sumy kontrolnej ma niepoprawny format.");
        return hash;
    }

    private static AppUpdateException InvalidChecksum(string technicalMessage) => new(
        technicalMessage,
        "Nie udało się potwierdzić integralności pobieranej aktualizacji. Instalacja została zatrzymana.");
}
