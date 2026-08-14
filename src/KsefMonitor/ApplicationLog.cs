using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;

namespace KsefMonitor;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Awaria pomocniczego dziennika nigdy nie może zatrzymać monitora faktur.")]
internal sealed class ApplicationLog
{
    private const long DefaultMaximumFileBytes = 1_000_000;
    private const int MaximumEntryCharacters = 50_000;
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _previousFilePath;
    private readonly long _maximumFileBytes;

    public ApplicationLog(string filePath, long maximumFileBytes = DefaultMaximumFileBytes)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Ścieżka dziennika nie może być pusta.", nameof(filePath));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 256);
        _filePath = filePath;
        _previousFilePath = Path.Combine(
            Path.GetDirectoryName(filePath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(filePath)}.previous{Path.GetExtension(filePath)}");
        _maximumFileBytes = maximumFileBytes;
    }

    public string FilePath => _filePath;

    public void Info(string source, string message) => Write("INFO", source, message, null);
    public void Warning(string source, string message, Exception? exception = null) => Write("WARN", source, message, exception);
    public void Error(string source, string message, Exception exception) => Write("ERROR", source, message, exception);

    public string ReadRecent(int maximumCharacters = 250_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        lock (_gate)
        {
            try
            {
                var text = new StringBuilder();
                if (File.Exists(_previousFilePath)) text.Append(File.ReadAllText(_previousFilePath, Encoding.UTF8));
                if (File.Exists(_filePath)) text.Append(File.ReadAllText(_filePath, Encoding.UTF8));
                if (text.Length == 0) return "Dziennik jest pusty.";
                var safeText = SecretRedactor.Redact(text.ToString());
                if (safeText.Length <= maximumCharacters) return safeText;
                return "… pokazano ostatnią część dziennika …" + Environment.NewLine +
                       safeText[^maximumCharacters..];
            }
            catch
            {
                return "Nie udało się odczytać dziennika aplikacji.";
            }
        }
    }

    private void Write(string level, string source, string message, Exception? exception)
    {
        try
        {
            var safeSource = NormalizeSingleLine(source);
            var safeMessage = NormalizeSingleLine(message);
            var details = exception is null
                ? string.Empty
                : Environment.NewLine + Limit(SecretRedactor.Redact(exception.ToString()));
            var entry = $"[{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}] [{level}] [{safeSource}] {safeMessage}{details}{Environment.NewLine}";
            var entryBytes = Encoding.UTF8.GetByteCount(entry);

            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                RotateIfNeeded(entryBytes);
                using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(entry);
            }
        }
        catch
        {
            // Dziennik jest funkcją diagnostyczną i nie może wywołać kolejnego błędu.
        }
    }

    private void RotateIfNeeded(int nextEntryBytes)
    {
        if (!File.Exists(_filePath) || new FileInfo(_filePath).Length + nextEntryBytes <= _maximumFileBytes) return;
        if (File.Exists(_previousFilePath)) File.Delete(_previousFilePath);
        File.Move(_filePath, _previousFilePath);
    }

    private static string NormalizeSingleLine(string value) =>
        Limit(SecretRedactor.Redact(value).Replace('\r', ' ').Replace('\n', ' ').Trim());

    private static string Limit(string value) =>
        value.Length <= MaximumEntryCharacters ? value : value[..MaximumEntryCharacters] + "…";
}
