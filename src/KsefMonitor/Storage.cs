using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsefMonitor;

internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KSeF Monitor");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string StateFile => Path.Combine(Root, "invoices.dat");
    public static string TokenFile => Path.Combine(Root, "credential.dat");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
    }
}

internal sealed class AppStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    public AppSettings LoadSettings()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions)
                       ?? new AppSettings();
            }
            catch
            {
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
                if (!File.Exists(AppPaths.StateFile)) return new AppState();
                var clear = WindowsDataProtection.Unprotect(File.ReadAllBytes(AppPaths.StateFile));
                return JsonSerializer.Deserialize<AppState>(clear, JsonOptions) ?? new AppState();
            }
            catch
            {
                return new AppState();
            }
        }
    }

    public void SaveState(AppState state)
    {
        lock (_gate)
        {
            var clear = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            AtomicWrite(AppPaths.StateFile, WindowsDataProtection.Protect(clear));
        }
    }

    public string? LoadToken()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(AppPaths.TokenFile)) return null;
                return Encoding.UTF8.GetString(WindowsDataProtection.Unprotect(File.ReadAllBytes(AppPaths.TokenFile)));
            }
            catch
            {
                return null;
            }
        }
    }

    public void SaveToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Token KSeF nie może być pusty.", nameof(token));
        lock (_gate)
        {
            AtomicWrite(AppPaths.TokenFile, WindowsDataProtection.Protect(Encoding.UTF8.GetBytes(token.Trim())));
        }
    }

    public void DeleteToken()
    {
        lock (_gate)
        {
            if (File.Exists(AppPaths.TokenFile)) File.Delete(AppPaths.TokenFile);
        }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }
}

internal static class WindowsDataProtection
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KSeFMonitor/v1/current-user");

    public static byte[] Protect(byte[] clear) => Transform(clear, protect: true);
    public static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Magazyn danych KSeF wymaga mechanizmu DPAPI systemu Windows.");

        using var inputBlob = DataBlob.FromBytes(input);
        using var entropyBlob = DataBlob.FromBytes(Entropy);
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
    private static extern IntPtr LocalFree(IntPtr hMem);
}
