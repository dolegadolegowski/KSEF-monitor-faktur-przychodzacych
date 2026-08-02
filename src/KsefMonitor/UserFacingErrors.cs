using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace KsefMonitor;

internal enum StatusSeverity
{
    Information,
    Error
}

internal sealed record AppStatusMessage(string Text, StatusSeverity Severity = StatusSeverity.Information)
{
    public bool IsError => Severity == StatusSeverity.Error;
}

internal sealed class StatusBannerState
{
    private readonly TimeSpan _errorLifetime;

    public StatusBannerState(TimeSpan errorLifetime)
    {
        if (errorLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(errorLifetime));
        _errorLifetime = errorLifetime;
    }

    public AppStatusMessage? Current { get; private set; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public void Apply(AppStatusMessage status, DateTimeOffset nowUtc)
    {
        Current = status;
        ExpiresAtUtc = status.IsError ? nowUtc + _errorLifetime : null;
    }

    public bool Expire(DateTimeOffset nowUtc)
    {
        if (Current is not { IsError: true } || ExpiresAtUtc is not { } expiry || nowUtc < expiry) return false;
        Current = null;
        ExpiresAtUtc = null;
        return true;
    }
}

internal static class UserFacingErrors
{
    public static string ForSynchronization(Exception exception)
    {
        exception = Unwrap(exception);
        if (IsMissingConfiguration(exception))
            return "Brakuje poprawnego NIP-u lub tokena KSeF. Otwórz ustawienia i uzupełnij dane.";
        if (exception is KsefApiException api)
        {
            if (api.StatusCode == HttpStatusCode.Unauthorized)
                return "Nie udało się zalogować do KSeF. Sprawdź NIP i token w ustawieniach.";
            if (api.StatusCode == HttpStatusCode.Forbidden)
                return "Token nie pozwala odczytywać faktur. Otwórz ustawienia i sprawdź token.";
            if (api.Message.Contains("Uwierzytelnienie", StringComparison.OrdinalIgnoreCase))
                return "Nie udało się zalogować do KSeF. Sprawdź NIP i token w ustawieniach.";
            if (api.StatusCode == HttpStatusCode.TooManyRequests)
                return "KSeF chwilowo ograniczył liczbę zapytań. Aplikacja spróbuje ponownie automatycznie.";
            if (api.StatusCode is { } statusCode && (int)statusCode >= 500)
                return "KSeF jest chwilowo niedostępny. Aplikacja spróbuje ponownie automatycznie.";
            if (api.HasErrorCode(21183))
                return "KSeF nie jest jeszcze gotowy na kolejne sprawdzenie. Aplikacja spróbuje ponownie automatycznie.";
            if (api.StatusCode == HttpStatusCode.BadRequest)
                return "KSeF nie przyjął zapytania o faktury. Aplikacja spróbuje ponownie automatycznie.";
        }
        if (IsTimeout(exception))
            return "KSeF nie odpowiedział na czas. Aplikacja spróbuje ponownie automatycznie.";
        if (exception is HttpRequestException)
            return "Nie można połączyć się z KSeF. Sprawdź połączenie z internetem.";
        if (IsLocalStorageError(exception))
            return "Nie udało się zapisać danych aplikacji na tym komputerze. Sprawdź wolne miejsce na dysku.";
        return "Nie udało się odświeżyć faktur. Aplikacja spróbuje ponownie automatycznie. Szczegóły zapisano w dzienniku.";
    }

    public static string ForConnectionTest(Exception exception)
    {
        exception = Unwrap(exception);
        if (IsTimeout(exception)) return "KSeF nie odpowiedział w ciągu minuty. Spróbuj ponownie za chwilę.";
        if (exception is HttpRequestException) return "Nie można połączyć się z KSeF. Sprawdź połączenie z internetem.";
        if (exception is KsefApiException api)
        {
            if (api.StatusCode == HttpStatusCode.Forbidden)
                return "Token działa, ale nie ma uprawnienia InvoiceRead potrzebnego do odczytu faktur.";
            if (api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest ||
                api.Message.Contains("Uwierzytelnienie", StringComparison.OrdinalIgnoreCase))
                return "Nie udało się zalogować do KSeF. Sprawdź NIP i token.";
            if (api.StatusCode == HttpStatusCode.TooManyRequests)
                return "KSeF chwilowo ograniczył liczbę prób. Spróbuj ponownie za kilka minut.";
            if (api.StatusCode is { } statusCode && (int)statusCode >= 500)
                return "KSeF jest chwilowo niedostępny. Spróbuj ponownie za kilka minut.";
        }
        return "Nie udało się sprawdzić połączenia. Szczegóły zapisano w zakładce Dziennik.";
    }

    public static string ForSettingsSave(Exception exception) => IsLocalStorageError(Unwrap(exception))
        ? "Nie udało się zapisać ustawień na tym komputerze. Sprawdź wolne miejsce na dysku."
        : "Nie udało się zapisać ustawień. Spróbuj ponownie.";

    public static string ForUnexpectedError() =>
        "Wystąpił nieoczekiwany problem. Szczegóły zapisano w dzienniku aplikacji.";

    private static Exception Unwrap(Exception exception) =>
        exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
            ? aggregate.InnerExceptions[0]
            : exception;

    private static bool IsTimeout(Exception exception) =>
        exception is TimeoutException or TaskCanceledException;

    private static bool IsMissingConfiguration(Exception exception) =>
        exception is InvalidOperationException &&
        exception.Message.Contains("NIP-u lub", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalStorageError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or CryptographicException ||
        exception.Message.Contains("DPAPI", StringComparison.OrdinalIgnoreCase);
}
