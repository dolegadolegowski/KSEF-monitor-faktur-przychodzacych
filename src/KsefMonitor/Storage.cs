using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KSeF Monitor");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string StateFile => Path.Combine(Root, "invoices.dat");
    public static string TokenFile => Path.Combine(Root, "credential.dat");
    public static string DownloadRateFile => Path.Combine(Root, "download-rate.dat");
    public static string MyDrCredentialsFile => Path.Combine(Root, "mydr-credentials.dat");
    public static string MyDrStateFile => Path.Combine(Root, "mydr-state.dat");
    public static string LogFile => Path.Combine(Root, "app.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
    }
}

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Granica odczytu danych musi odzyskać kopię lub uruchomić aplikację z pustym stanem zamiast zakończyć proces.")]
internal sealed class AppStore
{
    private const string MyDrCredentialsProtectionPurpose = "mydr-credentials/v1";
    private const string MyDrStateProtectionPurpose = "mydr-state/v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly object _stateQueueGate = new();
    private Task _stateWriteTail = Task.CompletedTask;
    private string? _loadWarning;

    public AppStore(ApplicationLog log) => Log = log;

    public ApplicationLog Log { get; }

    public string? ConsumeLoadWarning()
    {
        lock (_gate)
        {
            var warning = _loadWarning;
            _loadWarning = null;
            return warning;
        }
    }

    public AppSettings LoadSettings()
    {
        lock (_gate)
        {
            try
            {
                if (!FileOrBackupExists(AppPaths.SettingsFile)) return new AppSettings();
                return ReadTextWithBackup(AppPaths.SettingsFile, "ustawień", DeserializeSettings);
            }
            catch (Exception exception)
            {
                RecordLoadWarning("Nie udało się odczytać ustawień.", exception);
                return new AppSettings();
            }
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_gate)
        {
            AtomicWrite(AppPaths.SettingsFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, JsonOptions)));
        }
    }

    public AppState LoadState()
    {
        lock (_gate)
        {
            try
            {
                if (!FileOrBackupExists(AppPaths.StateFile)) return new AppState();
                var state = ReadProtectedWithBackup(
                    AppPaths.StateFile,
                    "lokalnego cache faktur",
                    clear => JsonSerializer.Deserialize<AppState>(clear, JsonOptions)) ?? new AppState();
                state.NormalizeAfterLoad();
                return state;
            }
            catch (Exception exception)
            {
                RecordLoadWarning("Nie udało się odczytać lokalnego cache faktur.", exception);
                return new AppState();
            }
        }
    }

    public void SaveState(AppState state)
    {
        SaveStateAsync(state).GetAwaiter().GetResult();
    }

    public Task SaveStateAsync(AppState state)
    {
        var snapshot = state.Snapshot();
        lock (_stateQueueGate)
        {
            _stateWriteTail = _stateWriteTail.ContinueWith(
                previous =>
                {
                    // Błąd wcześniejszego zapisu nie może zatrzymać nowszego stanu.
                    _ = previous.Exception;
                    SaveStateCore(snapshot);
                },
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
            return _stateWriteTail;
        }
    }

    public void FlushStateWrites()
    {
        Task pending;
        lock (_stateQueueGate) pending = _stateWriteTail;
        pending.GetAwaiter().GetResult();
    }

    private void SaveStateCore(AppState state)
    {
        lock (_gate)
        {
            var clear = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            try
            {
                AtomicWrite(AppPaths.StateFile, WindowsDataProtection.Protect(clear));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    public string? LoadToken()
    {
        lock (_gate)
        {
            try
            {
                if (!FileOrBackupExists(AppPaths.TokenFile)) return null;
                return ReadProtectedWithBackup(
                    AppPaths.TokenFile,
                    "tokena KSeF",
                    clear => Encoding.UTF8.GetString(clear));
            }
            catch (Exception exception)
            {
                RecordLoadWarning("Nie udało się odczytać tokena KSeF.", exception);
                return null;
            }
        }
    }

    public void SaveToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Token KSeF nie może być pusty.", nameof(token));
        lock (_gate)
        {
            var clear = Encoding.UTF8.GetBytes(token.Trim());
            try
            {
                AtomicWrite(AppPaths.TokenFile, WindowsDataProtection.Protect(clear));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    public List<DateTimeOffset>? LoadDownloadAttempts()
    {
        lock (_gate)
        {
            try
            {
                if (!FileOrBackupExists(AppPaths.DownloadRateFile)) return null;
                return ReadProtectedWithBackup(
                           AppPaths.DownloadRateFile,
                           "licznika pobierania",
                           clear => JsonSerializer.Deserialize<List<DateTimeOffset>>(clear, JsonOptions))
                       ?? new List<DateTimeOffset>();
            }
            catch (Exception exception)
            {
                RecordLoadWarning("Nie udało się odczytać licznika pobierania.", exception);
                return new List<DateTimeOffset>();
            }
        }
    }

    public void SaveDownloadAttempts(IReadOnlyList<DateTimeOffset> attempts)
    {
        lock (_gate)
        {
            var clear = JsonSerializer.SerializeToUtf8Bytes(attempts, JsonOptions);
            try
            {
                AtomicWrite(AppPaths.DownloadRateFile, WindowsDataProtection.Protect(clear));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    public void DeleteToken()
    {
        lock (_gate)
        {
            if (File.Exists(AppPaths.TokenFile)) File.Delete(AppPaths.TokenFile);
            if (File.Exists(AppPaths.TokenFile + ".bak")) File.Delete(AppPaths.TokenFile + ".bak");
            if (File.Exists(AppPaths.TokenFile + ".tmp")) File.Delete(AppPaths.TokenFile + ".tmp");
        }
    }

    public MyDrCredentials? LoadMyDrCredentials()
    {
        lock (_gate)
        {
            try
            {
                if (!FileOrBackupExists(AppPaths.MyDrCredentialsFile)) return null;
                var credentials = ReadProtectedWithBackup(
                    AppPaths.MyDrCredentialsFile,
                    "danych dostępowych MyDR",
                    clear => JsonSerializer.Deserialize<MyDrCredentials>(clear, JsonOptions),
                    MyDrCredentialsProtectionPurpose);
                credentials?.NormalizeAfterLoad();
                return credentials;
            }
            catch (Exception exception)
            {
                RecordLoadWarning("Nie udało się odczytać danych dostępowych MyDR.", exception);
                return null;
            }
        }
    }

    public void SaveMyDrCredentials(MyDrCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        credentials.NormalizeAfterLoad();
        if (!credentials.IsConfigured)
            throw new ArgumentException("Dane dostępowe MyDR są niekompletne.", nameof(credentials));

        lock (_gate)
        {
            var clear = JsonSerializer.SerializeToUtf8Bytes(credentials.Snapshot(), JsonOptions);
            try
            {
                AtomicWrite(
                    AppPaths.MyDrCredentialsFile,
                    WindowsDataProtection.Protect(clear, MyDrCredentialsProtectionPurpose));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    public bool TryReplaceMyDrCredentials(Guid expectedConnectionId, MyDrCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        lock (_gate)
        {
            var current = LoadMyDrCredentials();
            if (current is null || current.ConnectionId != expectedConnectionId) return false;
            SaveMyDrCredentials(credentials);
            return true;
        }
    }

    public void DeleteMyDrCredentials()
    {
        lock (_gate)
        {
            DeleteFileAndRecoveryCopies(AppPaths.MyDrCredentialsFile);
        }
    }

    public MyDrState LoadMyDrState()
    {
        lock (_gate)
        {
            try
            {
                if (!FileOrBackupExists(AppPaths.MyDrStateFile)) return new MyDrState();
                var state = ReadProtectedWithBackup(
                                AppPaths.MyDrStateFile,
                                "lokalnego podsumowania MyDR",
                                clear => JsonSerializer.Deserialize<MyDrState>(clear, JsonOptions),
                                MyDrStateProtectionPurpose)
                            ?? new MyDrState();
                state.NormalizeAfterLoad();
                return state;
            }
            catch (Exception exception)
            {
                RecordLoadWarning("Nie udało się odczytać lokalnego podsumowania MyDR.", exception);
                return new MyDrState();
            }
        }
    }

    public void SaveMyDrState(MyDrState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var snapshot = state.Snapshot();
        lock (_gate)
        {
            var clear = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            try
            {
                AtomicWrite(
                    AppPaths.MyDrStateFile,
                    WindowsDataProtection.Protect(clear, MyDrStateProtectionPurpose));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    private static void DeleteFileAndRecoveryCopies(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static AppSettings DeserializeSettings(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.Nip ??= string.Empty;

        // Starsze wersje pozwalały wybrać TEST/DEMO. Po przejściu na wyłącznie produkcyjny
        // endpoint nie wolno automatycznie użyć tokena zapisanego dla innego środowiska.
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Environment", out var environment) &&
            !string.Equals(environment.ToString(), "Production", StringComparison.OrdinalIgnoreCase))
            settings.RequiresProductionToken = true;
        return settings;
    }

    private T ReadTextWithBackup<T>(string path, string description, Func<string, T> deserialize)
    {
        try
        {
            return deserialize(File.ReadAllText(path));
        }
        catch (Exception primaryException)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup)) throw;
            try
            {
                var value = deserialize(File.ReadAllText(backup));
                File.Copy(backup, path, overwrite: true);
                RecordLoadWarning($"Odzyskano {description} z kopii zapasowej.", primaryException);
                return value;
            }
            catch
            {
                throw new InvalidDataException($"Uszkodzony plik {description} i jego kopia zapasowa.", primaryException);
            }
        }
    }

    private T? ReadProtectedWithBackup<T>(
        string path,
        string description,
        Func<byte[], T?> deserialize,
        string? protectionPurpose = null)
    {
        try
        {
            return ReadProtected(path, deserialize, protectionPurpose);
        }
        catch (Exception primaryException)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup)) throw;
            try
            {
                var value = ReadProtected(backup, deserialize, protectionPurpose);
                File.Copy(backup, path, overwrite: true);
                RecordLoadWarning($"Odzyskano {description} z kopii zapasowej.", primaryException);
                return value;
            }
            catch
            {
                throw new InvalidDataException($"Uszkodzony plik {description} i jego kopia zapasowa.", primaryException);
            }
        }
    }

    private static T? ReadProtected<T>(string path, Func<byte[], T?> deserialize, string? protectionPurpose)
    {
        var encrypted = File.ReadAllBytes(path);
        var clear = protectionPurpose is null
            ? WindowsDataProtection.Unprotect(encrypted)
            : WindowsDataProtection.Unprotect(encrypted, protectionPurpose);
        try
        {
            var value = deserialize(clear);
            return value is null
                ? throw new InvalidDataException("Chroniony plik nie zawiera oczekiwanych danych.")
                : value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private void RecordLoadWarning(string warning, Exception? exception = null)
    {
        _loadWarning = string.IsNullOrWhiteSpace(_loadWarning) ? warning : $"{_loadWarning} {warning}";
        Log.Warning("Pamięć lokalna", warning, exception);
    }

    private static bool FileOrBackupExists(string path) => File.Exists(path) || File.Exists(path + ".bak");
}

internal static class WindowsDataProtection
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KSeFMonitor/v1/current-user");

    public static byte[] Protect(byte[] clear) => Transform(clear, protect: true, Entropy);
    public static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, protect: false, Entropy);

    public static byte[] Protect(byte[] clear, string purpose) =>
        Transform(clear, protect: true, CreatePurposeEntropy(purpose));

    public static byte[] Unprotect(byte[] encrypted, string purpose) =>
        Transform(encrypted, protect: false, CreatePurposeEntropy(purpose));

    private static byte[] Transform(byte[] input, bool protect, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Magazyn danych KSeF wymaga mechanizmu DPAPI systemu Windows.");

        using var inputBlob = DataBlob.FromBytes(input);
        using var entropyBlob = DataBlob.FromBytes(entropy);
        NativeBlob outputBlob = default;

        var success = protect
            ? CryptProtectData(ref inputBlob.Value, "KSeF Monitor", ref entropyBlob.Value, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, ref outputBlob)
            : CryptUnprotectData(ref inputBlob.Value, IntPtr.Zero, ref entropyBlob.Value, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, ref outputBlob);

        if (!success) throw new InvalidOperationException($"DPAPI zwróciło błąd {Marshal.GetLastWin32Error()}.");

        try
        {
            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (outputBlob.pbData != IntPtr.Zero) LocalFree(outputBlob.pbData);
        }
    }

    private static byte[] CreatePurposeEntropy(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("Cel ochrony danych nie może być pusty.", nameof(purpose));
        return Encoding.UTF8.GetBytes($"KSeFMonitor/v1/current-user/{purpose.Trim()}");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private sealed class DataBlob : IDisposable
    {
        public NativeBlob Value;

        public static DataBlob FromBytes(byte[] bytes)
        {
            var blob = new DataBlob();
            blob.Value.cbData = bytes.Length;
            blob.Value.pbData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob.Value.pbData, bytes.Length);
            return blob;
        }

        public void Dispose()
        {
            if (Value.pbData == IntPtr.Zero) return;
            for (var i = 0; i < Value.cbData; i++) Marshal.WriteByte(Value.pbData, i, 0);
            Marshal.FreeHGlobal(Value.pbData);
            Value.pbData = IntPtr.Zero;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref NativeBlob pDataIn,
        string szDataDescr,
        ref NativeBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref NativeBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref NativeBlob pDataIn,
        IntPtr ppszDataDescr,
        ref NativeBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref NativeBlob pDataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
