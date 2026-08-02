using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace KsefMonitor;

internal sealed class MyDrCredentials
{
    public Guid ConnectionId { get; set; } = Guid.NewGuid();
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(RefreshToken);

    public MyDrCredentials Snapshot() => new()
    {
        ConnectionId = ConnectionId == Guid.Empty ? Guid.NewGuid() : ConnectionId,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RefreshToken = RefreshToken
    };

    public void NormalizeAfterLoad()
    {
        if (ConnectionId == Guid.Empty) ConnectionId = Guid.NewGuid();
        ClientId ??= string.Empty;
        ClientSecret ??= string.Empty;
        RefreshToken ??= string.Empty;
    }

    // Chroni przed przypadkowym ujawnieniem poświadczeń przez interpolację
    // obiektu w komunikacie diagnostycznym.
    public override string ToString() => $"MyDrCredentials {{ ConnectionId = {ConnectionId}, Secrets = [REDACTED] }}";
}

internal sealed class MyDrTokenResult
{
    public MyDrTokenResult(string? rotatedRefreshToken) => RotatedRefreshToken = rotatedRefreshToken;

    public string? RotatedRefreshToken { get; }
    public bool HasRotatedRefreshToken => !string.IsNullOrWhiteSpace(RotatedRefreshToken);

    public override string ToString() =>
        $"MyDrTokenResult {{ RotatedRefreshToken = {(HasRotatedRefreshToken ? "[REDACTED]" : "null")} }}";
}

internal sealed class MyDrVisitPage
{
    [JsonPropertyName("current_page")]
    public int? CurrentPage { get; set; }

    [JsonPropertyName("last_page")]
    public int? LastPage { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }

    [JsonPropertyName("previous")]
    public string? Previous { get; set; }

    [JsonPropertyName("results")]
    public List<MyDrVisit>? Results { get; set; }
}

internal sealed class MyDrVisit
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    // API deklaruje format YYYY-MM-DD. Zachowujemy tekst, aby nie zmieniać
    // semantyki stref czasowych i móc odrzucić niepoprawną odpowiedź jawnie.
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("visit_kind")]
    public string? VisitKind { get; set; }

    // Surowa wartość jest stabilnym kluczem cache, również gdy serwer zwróci
    // datę bez offsetu strefy czasowej.
    [JsonPropertyName("latest_modification")]
    public string LatestModification { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsPerformed => MyDrVisitStateClassifier.IsPerformed(State);

    public DateOnly GetDate()
    {
        if (DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return value;

        throw new MyDrApiException("MyDR zwrócił wizytę z niepoprawną datą.");
    }
}

internal sealed class MyDrAttachedPrivateService
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Id { get; set; }

    [JsonPropertyName("value")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? Value { get; set; }

    public decimal GetGrossValue()
    {
        if (Value is not { } result)
            throw new MyDrApiException("MyDR nie zwrócił poprawnej kwoty brutto dla jednej z usług.");

        return result;
    }
}

internal sealed class MyDrRawToken
{
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("requires_2fa")]
    public bool Requires2Fa { get; set; }

    public override string ToString() => "MyDrRawToken { Tokens = [REDACTED] }";
}

public static class MyDrVisitStateClassifier
{
    private static readonly HashSet<string> PerformedStates = new(StringComparer.Ordinal)
    {
        "DO ROZLICZENIA",
        "OCZEKUJE NA PLATNOSC",
        "ZAKONCZONA",
        "ZAMKNIETA",
        "ARCHIWALNA"
    };

    public static bool IsPerformed(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return false;
        return PerformedStates.Contains(Normalize(state));
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var previousWasWhiteSpace = false;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;

            // Litera Ł nie rozkłada się w Unicode do L + znak łączący.
            if (character is 'Ł' or 'ł')
            {
                result.Append('L');
                previousWasWhiteSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhiteSpace && result.Length > 0) result.Append(' ');
                previousWasWhiteSpace = true;
                continue;
            }

            result.Append(char.ToUpperInvariant(character));
            previousWasWhiteSpace = false;
        }

        return result.ToString().TrimEnd();
    }
}
