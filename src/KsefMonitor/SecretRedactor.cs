using System;
using System.Text.RegularExpressions;

namespace KsefMonitor;

/// <summary>
/// Usuwa dane uwierzytelniające z tekstu przeznaczonego wyłącznie do diagnostyki.
/// Nie należy używać tej klasy do modyfikowania żądań ani danych zapisywanych przez aplikację.
/// </summary>
internal static class SecretRedactor
{
    private const string Redacted = "[REDACTED]";
    private const string SecretKeyPattern =
        @"(?:client(?:[_\s-]?secret)|refresh(?:[_\s-]?token)|access(?:[_\s-]?token)|authorization|ksef(?:[_\s-]?(?:access[_\s-]?)?token)|token)";

    private static readonly Regex PemPrivateKey = new(
        @"-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----[\s\S]*?-----END(?: [A-Z0-9]+)* PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AuthorizationHeader = new(
        @"(?im)(?<prefix>\bauthorization\b\s*:\s*)(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorizationSchemeAssignment = new(
        @"(?i)(?<prefix>\bauthorization\b\s*=\s*)(?<value>(?:bearer|basic)\s+[^\s&;,]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedKeyValue = new(
        $@"(?<prefix>[""']?\b{SecretKeyPattern}\b[""']?\s*[:=]\s*[""'])(?<value>[^""'\r\n]*)(?<suffix>[""'])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex UnquotedKeyValue = new(
        $@"(?<prefix>[""']?\b{SecretKeyPattern}\b[""']?\s*[:=]\s*)(?<value>(?!\[REDACTED(?:_[A-Z_]+)?\])[^&;,\s}}\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BearerToken = new(
        @"(?i)(?<prefix>\bbearer\s+)(?<value>[A-Z0-9._~+/=-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GitHubToken = new(
        @"(?i)(?<![A-Z0-9_])(?:gh[pousr]_[A-Z0-9_]{20,255}|github_pat_[A-Z0-9_]{20,255})(?![A-Z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsonWebToken = new(
        @"(?<![A-Z0-9_-])eyJ[A-Z0-9_-]{5,}\.[A-Z0-9_-]{5,}\.[A-Z0-9_-]{5,}(?![A-Z0-9_-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;

        var result = PemPrivateKey.Replace(value, "[REDACTED PRIVATE KEY]");
        result = AuthorizationHeader.Replace(result, match => match.Groups["prefix"].Value + Redacted);
        result = AuthorizationSchemeAssignment.Replace(result, match => match.Groups["prefix"].Value + Redacted);
        result = QuotedKeyValue.Replace(result, match =>
            match.Groups["prefix"].Value + Redacted + match.Groups["suffix"].Value);
        result = UnquotedKeyValue.Replace(result, match => match.Groups["prefix"].Value + Redacted);
        result = BearerToken.Replace(result, match => match.Groups["prefix"].Value + Redacted);
        result = GitHubToken.Replace(result, "[REDACTED GITHUB TOKEN]");
        return JsonWebToken.Replace(result, "[REDACTED JWT]");
    }
}
