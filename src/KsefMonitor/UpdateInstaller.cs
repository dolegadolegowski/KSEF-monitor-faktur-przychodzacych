using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KsefMonitor;

internal sealed class UpdateInstallDescriptor
{
    public int SchemaVersion { get; init; } = 1;
    public string Nonce { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;
    public string TargetExePath { get; set; } = string.Empty;
    public string HelperExePath { get; set; } = string.Empty;
    public string CandidateExePath { get; set; } = string.Empty;
    public string PendingExePath { get; set; } = string.Empty;
    public string BackupExePath { get; set; } = string.Empty;
    public string FailedExePath { get; set; } = string.Empty;
    public string DescriptorPath { get; set; } = string.Empty;
    public int ParentProcessId { get; init; }
    public long ParentProcessStartUtcTicks { get; init; }
    public string CurrentExeSha256 { get; init; } = string.Empty;
    public string NewExeSha256 { get; init; } = string.Empty;
    public string ReadyEventName { get; init; } = string.Empty;
    public string HealthEventName { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public string State { get; set; } = "Verified";
}

internal sealed record PostUpdateInvocation(string DescriptorPath, string Nonce);

internal static class UpdateInstaller
{
    private const string HelperArgument = "--update-helper";
    private const string PostUpdateArgument = "--post-update";
    private const string ApplyMutexName = "Local\\KSeFMonitor.Update.Apply";
    private const int DescriptorMaximumBytes = 32 * 1024;
    private static readonly TimeSpan DescriptorMaximumAge = TimeSpan.FromHours(2);
    private static readonly TimeSpan HelperReadyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NewVersionHealthTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NormalStartWaitTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HealthySessionRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan RolledBackSessionRetention = TimeSpan.FromDays(2);
    private static readonly TimeSpan AbandonedSessionRetention = TimeSpan.FromDays(7);
    private static readonly char[] FileVersionSeparators = [' ', '+', '-'];
    private static readonly JsonSerializerOptions DescriptorJsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool IsHelperInvocation(string[] args) =>
        args.Length == 3 && string.Equals(args[0], HelperArgument, StringComparison.Ordinal);

    public static bool TryParsePostUpdateInvocation(string[] args, out PostUpdateInvocation? invocation)
    {
        invocation = null;
        if (args.Length == 3 && string.Equals(args[0], PostUpdateArgument, StringComparison.Ordinal) &&
            IsValidNonce(args[2]))
        {
            try
            {
                var candidate = new PostUpdateInvocation(Path.GetFullPath(args[1]), args[2]);
                if (!OperatingSystem.IsWindows() || IsExpectedPostUpdateInvocation(candidate))
                {
                    invocation = candidate;
                    return true;
                }
            }
            catch
            {
                // Niepoprawne argumenty nie mogą ominąć oczekiwania zwykłego startu na instalator.
            }
        }

        WaitForActiveInstallerBeforeNormalStart();
        return false;
    }

    private static bool IsExpectedPostUpdateInvocation(PostUpdateInvocation invocation)
    {
        try
        {
            var descriptor = ReadAndValidateDescriptor(
                invocation.DescriptorPath,
                invocation.Nonce,
                requireHelperPath: false);
            var processPath = Environment.ProcessPath;
            return !string.IsNullOrWhiteSpace(processPath) &&
                   PathsEqual(processPath, descriptor.TargetExePath) &&
                   string.Equals(descriptor.State, "Launching", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForActiveInstallerBeforeNormalStart()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mutex = new Mutex(initiallyOwned: false, ApplyMutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                // Zwykły start nie może wejść pomiędzy podmianę pliku a rollback.
                ownsMutex = mutex.WaitOne(NormalStartWaitTimeout);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex)
                throw new AppUpdateException(
                    "Aktywny instalator nie zakończył pracy w ciągu trzech minut.",
                    "Aktualizacja nadal trwa albo instalator przestał odpowiadać. Poczekaj chwilę i uruchom aplikację ponownie.");
        }
        finally
        {
            if (ownsMutex)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Brak własności oznacza, że system zwolnił już mutex.
                }
            }
        }
    }

    public static int RunHelper(string[] args)
    {
        AppPaths.EnsureCreated();
        var log = new ApplicationLog(AppPaths.LogFile);
        if (!OperatingSystem.IsWindows())
        {
            log.Warning("Aktualizacja", "Tryb instalatora został uruchomiony poza systemem Windows.");
            return 20;
        }

        UpdateInstallDescriptor? descriptor = null;
        Process? newProcess = null;
        var readySignaled = false;
        var parentExited = false;
        var replacementCompleted = false;
        Mutex? applyMutex = null;
        var ownsApplyMutex = false;
        try
        {
            if (!IsHelperInvocation(args) || !IsValidNonce(args[2]))
                throw new AppUpdateException("Niepoprawne argumenty trybu instalatora.", "Nie udało się uruchomić instalatora aktualizacji.");
            descriptor = ReadAndValidateDescriptor(args[1], args[2], requireHelperPath: true);
            if (!string.Equals(descriptor.State, "Verified", StringComparison.Ordinal))
                throw new AppUpdateException("Deskryptor nie jest w stanie Verified.", "Ta sesja aktualizacji została już użyta lub jest niepoprawna.");
            applyMutex = new Mutex(initiallyOwned: false, ApplyMutexName);
            try
            {
                ownsApplyMutex = applyMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsApplyMutex = true;
            }
            if (!ownsApplyMutex)
                throw new AppUpdateException("Inny instalator aktualizacji już działa.", "Aktualizacja jest już instalowana.");

            ValidateExecutable(descriptor.HelperExePath, descriptor.CurrentExeSha256,
                ParseDescriptorVersion(descriptor.CurrentVersion, "bieżącej"));
            ValidateExecutable(descriptor.CandidateExePath, descriptor.NewExeSha256,
                ParseDescriptorVersion(descriptor.NewVersion, "nowej"));
            ValidateFileHash(descriptor.TargetExePath, descriptor.CurrentExeSha256, "Bieżący plik aplikacji został zmieniony.");

            using var parent = Process.GetProcessById(descriptor.ParentProcessId);
            var parentStartUtc = parent.StartTime.ToUniversalTime().Ticks;
            if (parentStartUtc != descriptor.ParentProcessStartUtcTicks)
                throw new AppUpdateException("PID procesu nadrzędnego został ponownie użyty.", "Nie udało się bezpiecznie rozpocząć aktualizacji.");

            using var readyEvent = EventWaitHandle.OpenExisting(descriptor.ReadyEventName);
            using var healthEvent = EventWaitHandle.OpenExisting(descriptor.HealthEventName);
            readyEvent.Set();
            readySignaled = true;
            log.Info("Aktualizacja", $"Instalator v{descriptor.NewVersion} jest gotowy; oczekiwanie na zamknięcie aplikacji.");

            if (!parent.WaitForExit((int)ParentExitTimeout.TotalMilliseconds))
                throw new AppUpdateException("Bieżąca aplikacja nie zamknęła się w wymaganym czasie.", "Aplikacja nie zamknęła się na czas. Aktualizacja nie została zainstalowana.");
            parentExited = true;

            ValidateFileHash(descriptor.TargetExePath, descriptor.CurrentExeSha256, "Plik aplikacji zmienił się przed instalacją.");
            CopyFileDurably(descriptor.CandidateExePath, descriptor.PendingExePath);
            ValidateExecutable(descriptor.PendingExePath, descriptor.NewExeSha256,
                ParseDescriptorVersion(descriptor.NewVersion, "nowej"));
            if (File.Exists(descriptor.BackupExePath) || File.Exists(descriptor.FailedExePath))
                throw new AppUpdateException("Plik kopii zapasowej sesji już istnieje.", "Nie udało się bezpiecznie przygotować kopii poprzedniej wersji.");

            descriptor.State = "Replacing";
            WriteDescriptor(descriptor);
            ValidateDescriptorPaths(descriptor, descriptor.DescriptorPath, descriptor.Nonce, requireHelperPath: true);
            ValidateFileHash(descriptor.TargetExePath, descriptor.CurrentExeSha256, "Plik aplikacji zmienił się bezpośrednio przed instalacją.");
            ValidateExecutable(descriptor.PendingExePath, descriptor.NewExeSha256,
                ParseDescriptorVersion(descriptor.NewVersion, "nowej"));
            ReplaceWithRetries(descriptor.PendingExePath, descriptor.TargetExePath, descriptor.BackupExePath);
            replacementCompleted = true;
            ValidateFileHash(descriptor.TargetExePath, descriptor.NewExeSha256, "Podmieniony plik aplikacji ma niepoprawny skrót.");

            descriptor.State = "Launching";
            WriteDescriptor(descriptor);
            newProcess = StartUpdatedApplication(descriptor);
            var healthy = WaitForHealth(healthEvent, newProcess, NewVersionHealthTimeout);
            if (!healthy)
                throw new AppUpdateException("Nowa wersja nie potwierdziła poprawnego uruchomienia.", "Nowa wersja nie uruchomiła się poprawnie. Przywracanie poprzedniej wersji.");

            descriptor.State = "Healthy";
            WriteDescriptor(descriptor);
            log.Info("Aktualizacja", $"Aktualizacja do v{descriptor.NewVersion} została zainstalowana i uruchomiona poprawnie.");
            return 0;
        }
        catch (Exception exception)
        {
            log.Error("Aktualizacja", "Instalacja aktualizacji nie powiodła się.", exception);
            if (descriptor is not null && (replacementCompleted || File.Exists(descriptor.BackupExePath)))
            {
                var rolledBack = TryRollback(descriptor, newProcess, log);
                if (rolledBack)
                {
                    WriteFailureMarker("Aktualizacja nie uruchomiła się poprawnie. Poprzednia wersja została automatycznie przywrócona.");
                    StartApplicationWithoutArguments(descriptor.TargetExePath, log);
                    return 31;
                }
                WriteFailureMarker("Aktualizacja nie powiodła się i nie udało się automatycznie przywrócić poprzedniej wersji. Pobierz KSeFMonitor.exe ze strony GitHub Releases.");
                return 32;
            }

            if (descriptor is not null && readySignaled && parentExited)
            {
                descriptor.State = "Failed";
                TryWriteDescriptor(descriptor, log);
                WriteFailureMarker("Aktualizacja nie została zainstalowana. Aplikacja uruchomiła ponownie dotychczasową wersję.");
                StartApplicationWithoutArguments(descriptor.TargetExePath, log);
            }
            else if (descriptor is not null && readySignaled)
            {
                descriptor.State = "Failed";
                TryWriteDescriptor(descriptor, log);
                WriteFailureMarker("Aplikacja nie zamknęła się na czas, dlatego aktualizacja nie została zainstalowana. Spróbuj ponownie po ponownym uruchomieniu aplikacji.");
            }
            return 30;
        }
        finally
        {
            if (ownsApplyMutex)
            {
                try
                {
                    applyMutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Proces kończy się; brak własności oznacza, że mutex został już zwolniony przez system.
                }
            }
            applyMutex?.Dispose();
            newProcess?.Dispose();
        }
    }

    public static string CreateSessionDirectory(string targetExePath, long executableSize)
    {
        if (!OperatingSystem.IsWindows())
            throw new AppUpdateException("Automatyczna instalacja jest dostępna wyłącznie w Windows.", "Automatyczne aktualizacje są obsługiwane wyłącznie w Windows 11.");
        var target = Path.GetFullPath(targetExePath);
        if (!File.Exists(target) || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new AppUpdateException("Nie odnaleziono działającego pliku EXE aplikacji.", "Nie udało się odnaleźć pliku aplikacji do aktualizacji.");
        if (new Uri(target).IsUnc)
            throw new AppUpdateException("Aplikacja działa ze ścieżki sieciowej UNC.", "Przenieś aplikację na lokalny dysk, aby korzystać z automatycznych aktualizacji.");

        var targetDirectory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Brakuje katalogu pliku aplikacji.");
        EnsureTargetDirectoryWritable(targetDirectory);
        EnsureFreeSpace(targetDirectory, executableSize);
        var root = Path.Combine(targetDirectory, ProductInformation.UpdateDirectoryName);
        Directory.CreateDirectory(root);
        RejectReparsePoint(root);
        var session = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(session);
        RejectReparsePoint(session);
        return session;
    }

    public static async Task PrepareAndStartHelperAsync(
        string targetExePath,
        string candidateExePath,
        SemanticVersion newVersion,
        string newExeSha256,
        ApplicationLog log,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(targetExePath);
        var candidate = Path.GetFullPath(candidateExePath);
        var session = Path.GetDirectoryName(candidate) ?? throw new InvalidOperationException("Brakuje katalogu aktualizacji.");
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var helper = Path.Combine(session, $"KSeFMonitor.Updater.{nonce}.exe");
        var descriptorPath = Path.Combine(session, "update.json");
        var targetDirectory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Brakuje katalogu pliku aplikacji.");
        var targetName = Path.GetFileName(target);
        var currentHash = await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false);
        var currentVersion = ProductInformation.CurrentVersion;

        ValidateExecutable(candidate, newExeSha256, newVersion);
        await CopyFileDurablyAsync(target, helper, cancellationToken).ConfigureAwait(false);
        ValidateExecutable(helper, currentHash, currentVersion);

        using var currentProcess = Process.GetCurrentProcess();
        var descriptor = new UpdateInstallDescriptor
        {
            Nonce = nonce,
            CurrentVersion = currentVersion.ToString(),
            NewVersion = newVersion.ToString(),
            TargetExePath = target,
            HelperExePath = helper,
            CandidateExePath = candidate,
            PendingExePath = Path.Combine(targetDirectory, $".{targetName}.{nonce}.pending"),
            BackupExePath = Path.Combine(targetDirectory, $".{targetName}.{nonce}.bak"),
            FailedExePath = Path.Combine(targetDirectory, $".{targetName}.{nonce}.failed"),
            DescriptorPath = descriptorPath,
            ParentProcessId = Environment.ProcessId,
            ParentProcessStartUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks,
            CurrentExeSha256 = currentHash,
            NewExeSha256 = newExeSha256,
            ReadyEventName = $"Local\\KSeFMonitor.Update.Ready.{nonce}",
            HealthEventName = $"Local\\KSeFMonitor.Update.Health.{nonce}",
            CreatedUtc = DateTimeOffset.UtcNow
        };
        ValidateDescriptorPaths(descriptor, descriptorPath, nonce, requireHelperPath: false);
        WriteDescriptor(descriptor);

        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, descriptor.ReadyEventName);
        using var healthEvent = new EventWaitHandle(false, EventResetMode.ManualReset, descriptor.HealthEventName);
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            WorkingDirectory = targetDirectory,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(descriptorPath);
        start.ArgumentList.Add(nonce);
        using var helperProcess = Process.Start(start) ?? throw new AppUpdateException(
            "Process.Start nie uruchomił instalatora.",
            "Nie udało się uruchomić instalatora aktualizacji.");

        var readyTask = Task.Run(() => readyEvent.WaitOne(HelperReadyTimeout));
        var exitTask = helperProcess.WaitForExitAsync(CancellationToken.None);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(readyTask, exitTask, cancellationTask).ConfigureAwait(false);
        if (completed == cancellationTask)
        {
            TryStopProcess(helperProcess);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (completed == readyTask && await readyTask.ConfigureAwait(false))
        {
            log.Info("Aktualizacja", $"Zweryfikowano aktualizację v{newVersion}; instalator oczekuje na zamknięcie aplikacji.");
            return;
        }

        TryStopProcess(helperProcess);
        throw new AppUpdateException(
            $"Instalator nie zgłosił gotowości. Kod wyjścia: {(helperProcess.HasExited ? helperProcess.ExitCode : -1)}.",
            "Nie udało się przygotować instalacji aktualizacji. Aplikacja pozostaje uruchomiona.");
    }

    public static void SignalPostUpdateHealth(PostUpdateInvocation invocation, ApplicationLog log)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var descriptor = ReadAndValidateDescriptor(invocation.DescriptorPath, invocation.Nonce, requireHelperPath: false);
            if (string.Equals(descriptor.State, "Healthy", StringComparison.Ordinal))
            {
                log.Info("Aktualizacja", $"Sesja v{descriptor.NewVersion} została odzyskana po przerwaniu instalatora.");
                return;
            }
            if (!string.Equals(descriptor.State, "Launching", StringComparison.Ordinal))
                throw new AppUpdateException("Deskryptor nie jest w stanie Launching.", "Nie udało się potwierdzić uruchomienia aktualizacji.");
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !PathsEqual(processPath, descriptor.TargetExePath))
                throw new AppUpdateException("Nowy proces działa z innej ścieżki niż target aktualizacji.", "Nie udało się potwierdzić uruchomienia aktualizacji.");
            ValidateFileHash(descriptor.TargetExePath, descriptor.NewExeSha256, "Uruchomiony EXE ma nieoczekiwany skrót.");
            using var health = EventWaitHandle.OpenExisting(descriptor.HealthEventName);
            health.Set();
            log.Info("Aktualizacja", $"Nowa wersja v{descriptor.NewVersion} potwierdziła poprawny start.");
        }
        catch (Exception exception)
        {
            log.Error("Aktualizacja", "Nie udało się wysłać potwierdzenia startu nowej wersji.", exception);
        }
    }

    public static string? ConsumeFailureMarker(ApplicationLog log)
    {
        try
        {
            if (!File.Exists(AppPaths.UpdateFailureFile)) return null;
            var info = new FileInfo(AppPaths.UpdateFailureFile);
            var message = info.Length is > 0 and <= 4096
                ? File.ReadAllText(AppPaths.UpdateFailureFile, Encoding.UTF8).Trim()
                : "Poprzednia próba aktualizacji nie powiodła się.";
            File.Delete(AppPaths.UpdateFailureFile);
            return string.IsNullOrWhiteSpace(message) ? "Poprzednia próba aktualizacji nie powiodła się." : message;
        }
        catch (Exception exception)
        {
            log.Warning("Aktualizacja", "Nie udało się odczytać komunikatu poprzedniej aktualizacji.", exception);
            return null;
        }
    }

    public static void CleanupStaleArtifacts(ApplicationLog log)
    {
        if (!OperatingSystem.IsWindows()) return;
        Mutex? applyMutex = null;
        var ownsApplyMutex = false;
        try
        {
            applyMutex = new Mutex(initiallyOwned: false, ApplyMutexName);
            try
            {
                ownsApplyMutex = applyMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsApplyMutex = true;
            }
            if (!ownsApplyMutex)
            {
                log.Info("Aktualizacja", "Pominięto porządkowanie plików, ponieważ instalator nadal pracuje.");
                return;
            }

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath)) return;
            var target = Path.GetFullPath(processPath);
            var targetDirectory = Path.GetDirectoryName(target);
            if (targetDirectory is null) return;
            var root = Path.Combine(targetDirectory, ProductInformation.UpdateDirectoryName);
            if (!Directory.Exists(root) || IsReparsePoint(root)) return;

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (!IsDirectChild(directory, root) || IsReparsePoint(directory)) continue;
                    var descriptorPath = Path.Combine(directory, "update.json");
                    var descriptor = TryReadDescriptorForMaintenance(descriptorPath, log);
                    if (descriptor is null || !PathsEqual(descriptor.TargetExePath, target)) continue;

                    if (IsInterruptedState(descriptor.State)) continue;

                    var age = DateTimeOffset.UtcNow - descriptor.CreatedUtc;
                    if (string.Equals(descriptor.State, "Healthy", StringComparison.Ordinal) &&
                        age >= HealthySessionRetention)
                    {
                        TryCleanupHealthySession(descriptor, log);
                    }
                    else if (string.Equals(descriptor.State, "RolledBack", StringComparison.Ordinal) &&
                             age >= RolledBackSessionRetention)
                    {
                        TryCleanupRolledBackSession(descriptor, log);
                    }
                    else if ((string.Equals(descriptor.State, "Failed", StringComparison.Ordinal) ||
                              string.Equals(descriptor.State, "Verified", StringComparison.Ordinal)) &&
                             age >= AbandonedSessionRetention)
                    {
                        TryCleanupAbandonedSession(descriptor, log);
                    }
                }
                catch (Exception exception)
                {
                    log.Warning("Aktualizacja", $"Nie udało się sprawdzić sesji {Path.GetFileName(directory)}. Pliki pozostawiono bez zmian.", exception);
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning("Aktualizacja", "Nie udało się posprzątać starych plików aktualizacji.", exception);
        }
        finally
        {
            if (ownsApplyMutex)
            {
                try
                {
                    applyMutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Mutex został już zwolniony przez system.
                }
            }
            applyMutex?.Dispose();
        }
    }

    public static void RecoverInterruptedSessionsAfterStartup(ApplicationLog log)
    {
        if (!OperatingSystem.IsWindows()) return;
        Mutex? applyMutex = null;
        var ownsApplyMutex = false;
        try
        {
            applyMutex = new Mutex(initiallyOwned: false, ApplyMutexName);
            try
            {
                ownsApplyMutex = applyMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsApplyMutex = true;
            }
            if (!ownsApplyMutex) return;

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath)) return;
            var target = Path.GetFullPath(processPath);
            var targetDirectory = Path.GetDirectoryName(target);
            if (targetDirectory is null) return;
            var root = Path.Combine(targetDirectory, ProductInformation.UpdateDirectoryName);
            if (!Directory.Exists(root) || IsReparsePoint(root)) return;

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (!IsDirectChild(directory, root) || IsReparsePoint(directory)) continue;
                    var descriptor = TryReadDescriptorForMaintenance(Path.Combine(directory, "update.json"), log);
                    if (descriptor is null || !PathsEqual(descriptor.TargetExePath, target) ||
                        !IsInterruptedState(descriptor.State)) continue;
                    _ = TryRecoverInterruptedSession(descriptor, log);
                }
                catch (Exception exception)
                {
                    log.Warning("Aktualizacja", $"Nie udało się odzyskać sesji {Path.GetFileName(directory)}. Kopię zapasową pozostawiono bez zmian.", exception);
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning("Aktualizacja", "Nie udało się sprawdzić przerwanych aktualizacji po starcie aplikacji.", exception);
        }
        finally
        {
            if (ownsApplyMutex)
            {
                try
                {
                    applyMutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Mutex został już zwolniony przez system.
                }
            }
            applyMutex?.Dispose();
        }
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static void ReplaceFileTransaction(string replacementPath, string targetPath, string backupPath) =>
        File.Replace(replacementPath, targetPath, backupPath, ignoreMetadataErrors: false);

    internal static void RollbackFileTransaction(string backupPath, string targetPath, string failedPath) =>
        File.Replace(backupPath, targetPath, failedPath, ignoreMetadataErrors: false);

    private static UpdateInstallDescriptor ReadAndValidateDescriptor(
        string descriptorPath,
        string nonce,
        bool requireHelperPath,
        bool enforceMaximumAge = true)
    {
        var fullPath = Path.GetFullPath(descriptorPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > DescriptorMaximumBytes)
            throw new AppUpdateException("Deskryptor aktualizacji nie istnieje lub jest za duży.", "Nie udało się odczytać danych instalatora aktualizacji.");
        var descriptor = JsonSerializer.Deserialize<UpdateInstallDescriptor>(ReadDescriptorBytes(fullPath, (int)info.Length), DescriptorJsonOptions)
            ?? throw new AppUpdateException("Deskryptor aktualizacji jest pusty.", "Nie udało się odczytać danych instalatora aktualizacji.");
        ValidateDescriptorPaths(descriptor, fullPath, nonce, requireHelperPath, enforceMaximumAge);
        return descriptor;
    }

    private static void ValidateDescriptorPaths(
        UpdateInstallDescriptor descriptor,
        string actualDescriptorPath,
        string nonce,
        bool requireHelperPath,
        bool enforceMaximumAge = true)
    {
        if (descriptor.SchemaVersion != 1 || !IsValidNonce(nonce) ||
            !string.Equals(descriptor.Nonce, nonce, StringComparison.Ordinal))
            throw new AppUpdateException("Niepoprawna wersja lub nonce deskryptora.", "Dane instalatora aktualizacji są niepoprawne.");
        var age = DateTimeOffset.UtcNow - descriptor.CreatedUtc;
        if (age < TimeSpan.FromMinutes(-5) || (enforceMaximumAge && age > DescriptorMaximumAge))
            throw new AppUpdateException("Deskryptor aktualizacji jest przeterminowany.", "Dane instalatora aktualizacji straciły ważność.");
        if (!SemanticVersion.TryParseReleaseTag($"v{descriptor.CurrentVersion}", out _) ||
            !SemanticVersion.TryParseReleaseTag($"v{descriptor.NewVersion}", out var nextVersion))
            throw new AppUpdateException("Deskryptor zawiera niepoprawne wersje.", "Dane instalatora aktualizacji są niepoprawne.");
        if (!GitHubReleasePolicy.TryNormalizeSha256(descriptor.CurrentExeSha256, out _) ||
            !GitHubReleasePolicy.TryNormalizeSha256(descriptor.NewExeSha256, out _))
            throw new AppUpdateException("Deskryptor zawiera niepoprawne skróty SHA-256.", "Dane instalatora aktualizacji są niepoprawne.");
        if (nextVersion.CompareTo(ParseDescriptorVersion(descriptor.CurrentVersion, "bieżącej")) <= 0)
            throw new AppUpdateException("Deskryptor próbował zainstalować tę samą lub starszą wersję.", "Aktualizacja do starszej wersji została zablokowana.");
        if (descriptor.ParentProcessId <= 0 || descriptor.ParentProcessStartUtcTicks <= 0)
            throw new AppUpdateException("Deskryptor zawiera niepoprawną tożsamość procesu nadrzędnego.", "Dane instalatora aktualizacji są niepoprawne.");

        descriptor.TargetExePath = Path.GetFullPath(descriptor.TargetExePath);
        descriptor.HelperExePath = Path.GetFullPath(descriptor.HelperExePath);
        descriptor.CandidateExePath = Path.GetFullPath(descriptor.CandidateExePath);
        descriptor.PendingExePath = Path.GetFullPath(descriptor.PendingExePath);
        descriptor.BackupExePath = Path.GetFullPath(descriptor.BackupExePath);
        descriptor.FailedExePath = Path.GetFullPath(descriptor.FailedExePath);
        descriptor.DescriptorPath = Path.GetFullPath(descriptor.DescriptorPath);
        var targetDirectory = Path.GetDirectoryName(descriptor.TargetExePath) ?? throw new AppUpdateException(
            "Target nie ma katalogu.", "Dane instalatora aktualizacji są niepoprawne.");
        var sessionDirectory = Path.GetDirectoryName(descriptor.DescriptorPath) ?? throw new AppUpdateException(
            "Deskryptor nie ma katalogu.", "Dane instalatora aktualizacji są niepoprawne.");
        var expectedRoot = Path.Combine(targetDirectory, ProductInformation.UpdateDirectoryName);
        var targetName = Path.GetFileName(descriptor.TargetExePath);
        if (!IsDirectChild(sessionDirectory, expectedRoot) ||
            !PathsEqual(descriptor.DescriptorPath, actualDescriptorPath) ||
            !PathsEqual(Path.GetDirectoryName(descriptor.HelperExePath)!, sessionDirectory) ||
            !PathsEqual(Path.GetDirectoryName(descriptor.CandidateExePath)!, sessionDirectory) ||
            !PathsEqual(Path.GetDirectoryName(descriptor.PendingExePath)!, targetDirectory) ||
            !PathsEqual(Path.GetDirectoryName(descriptor.BackupExePath)!, targetDirectory) ||
            !PathsEqual(Path.GetDirectoryName(descriptor.FailedExePath)!, targetDirectory))
            throw new AppUpdateException("Ścieżki deskryptora wychodzą poza katalog aktualizacji.", "Dane instalatora aktualizacji są niepoprawne.");
        if (!string.Equals(Path.GetFileName(descriptor.CandidateExePath), ProductInformation.WindowsReleaseAssetName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(descriptor.HelperExePath), $"KSeFMonitor.Updater.{nonce}.exe", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(descriptor.DescriptorPath), "update.json", StringComparison.Ordinal) ||
            !PathsEqual(descriptor.PendingExePath, Path.Combine(targetDirectory, $".{targetName}.{nonce}.pending")) ||
            !PathsEqual(descriptor.BackupExePath, Path.Combine(targetDirectory, $".{targetName}.{nonce}.bak")) ||
            !PathsEqual(descriptor.FailedExePath, Path.Combine(targetDirectory, $".{targetName}.{nonce}.failed")))
            throw new AppUpdateException("Nazwy plików deskryptora są niepoprawne.", "Dane instalatora aktualizacji są niepoprawne.");
        if (!string.Equals(descriptor.ReadyEventName, $"Local\\KSeFMonitor.Update.Ready.{nonce}", StringComparison.Ordinal) ||
            !string.Equals(descriptor.HealthEventName, $"Local\\KSeFMonitor.Update.Health.{nonce}", StringComparison.Ordinal))
            throw new AppUpdateException("Nazwy zdarzeń instalatora są niepoprawne.", "Dane instalatora aktualizacji są niepoprawne.");
        RejectReparsePoint(expectedRoot);
        RejectReparsePoint(sessionDirectory);
        if (File.Exists(descriptor.TargetExePath)) RejectReparsePoint(descriptor.TargetExePath);
        if (File.Exists(descriptor.CandidateExePath)) RejectReparsePoint(descriptor.CandidateExePath);
        if (File.Exists(descriptor.HelperExePath)) RejectReparsePoint(descriptor.HelperExePath);
        if (File.Exists(descriptor.PendingExePath)) RejectReparsePoint(descriptor.PendingExePath);
        if (File.Exists(descriptor.BackupExePath)) RejectReparsePoint(descriptor.BackupExePath);
        if (File.Exists(descriptor.FailedExePath)) RejectReparsePoint(descriptor.FailedExePath);
        if (requireHelperPath)
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !PathsEqual(processPath, descriptor.HelperExePath))
                throw new AppUpdateException("Tryb instalatora działa z nieoczekiwanej ścieżki.", "Nie udało się bezpiecznie uruchomić instalatora aktualizacji.");
        }
    }

    private static UpdateInstallDescriptor? TryReadDescriptorForMaintenance(string descriptorPath, ApplicationLog log)
    {
        try
        {
            var fullPath = Path.GetFullPath(descriptorPath);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length is <= 0 or > DescriptorMaximumBytes) return null;
            var descriptor = JsonSerializer.Deserialize<UpdateInstallDescriptor>(
                ReadDescriptorBytes(fullPath, (int)info.Length),
                DescriptorJsonOptions);
            if (descriptor is null || !IsValidNonce(descriptor.Nonce)) return null;
            ValidateDescriptorPaths(
                descriptor,
                fullPath,
                descriptor.Nonce,
                requireHelperPath: false,
                enforceMaximumAge: false);
            return descriptor;
        }
        catch (Exception exception)
        {
            log.Warning("Aktualizacja", $"Pominięto niepoprawny deskryptor w {Path.GetFileName(Path.GetDirectoryName(descriptorPath))}.", exception);
            return null;
        }
    }

    private static bool IsInterruptedState(string state) =>
        string.Equals(state, "Replacing", StringComparison.Ordinal) ||
        string.Equals(state, "Launching", StringComparison.Ordinal);

    private static bool TryRecoverInterruptedSession(UpdateInstallDescriptor descriptor, ApplicationLog log)
    {
        if (FileMatchesHash(descriptor.TargetExePath, descriptor.NewExeSha256))
        {
            descriptor.State = "Healthy";
            if (!TryWriteDescriptor(descriptor, log)) return false;
            log.Info("Aktualizacja", $"Odzyskano przerwaną sesję aktualizacji v{descriptor.NewVersion}; działający plik jest poprawną nową wersją.");
            return true;
        }

        if (FileMatchesHash(descriptor.TargetExePath, descriptor.CurrentExeSha256))
        {
            descriptor.State = "RolledBack";
            if (!TryWriteDescriptor(descriptor, log)) return false;
            log.Warning("Aktualizacja", $"Odzyskano przerwaną sesję aktualizacji; zachowano v{descriptor.CurrentVersion}.");
            return true;
        }

        log.Warning(
            "Aktualizacja",
            $"Sesja v{descriptor.NewVersion} została przerwana, ale działający plik nie odpowiada żadnej wersji z tej sesji. Pliki diagnostyczne i kopię zapasową pozostawiono bez zmian.");
        return false;
    }

    private static void TryCleanupHealthySession(UpdateInstallDescriptor descriptor, ApplicationLog log)
    {
        if (File.Exists(descriptor.FailedExePath) ||
            !CanDeleteHashedArtifact(descriptor.BackupExePath, descriptor.CurrentExeSha256) ||
            !CanDeleteHashedArtifact(descriptor.PendingExePath, descriptor.NewExeSha256) ||
            !CanDeleteHashedArtifact(descriptor.CandidateExePath, descriptor.NewExeSha256) ||
            !CanDeleteHashedArtifact(descriptor.HelperExePath, descriptor.CurrentExeSha256) ||
            !SessionContainsOnlyKnownArtifacts(descriptor))
        {
            log.Warning("Aktualizacja", $"Sesja v{descriptor.NewVersion} zawiera nieoczekiwane pliki. Nie usunięto jej ani kopii zapasowej.");
            return;
        }

        if (!TryDeleteHashedFile(descriptor.BackupExePath, descriptor.CurrentExeSha256, log) ||
            !TryDeleteHashedFile(descriptor.PendingExePath, descriptor.NewExeSha256, log) ||
            !TryDeleteHashedFile(descriptor.CandidateExePath, descriptor.NewExeSha256, log) ||
            !TryDeleteHashedFile(descriptor.HelperExePath, descriptor.CurrentExeSha256, log) ||
            !TryDeleteValidatedFile(descriptor.DescriptorPath + ".tmp", log) ||
            !TryDeleteValidatedFile(descriptor.DescriptorPath, log)) return;

        TryDeleteEmptyDirectory(Path.GetDirectoryName(descriptor.DescriptorPath)!, log);
    }

    private static void TryCleanupRolledBackSession(UpdateInstallDescriptor descriptor, ApplicationLog log)
    {
        if (File.Exists(descriptor.BackupExePath) ||
            !CanDeleteHashedArtifact(descriptor.FailedExePath, descriptor.NewExeSha256) ||
            !CanDeleteHashedArtifact(descriptor.PendingExePath, descriptor.NewExeSha256) ||
            !CanDeleteHashedArtifact(descriptor.CandidateExePath, descriptor.NewExeSha256) ||
            !CanDeleteHashedArtifact(descriptor.HelperExePath, descriptor.CurrentExeSha256) ||
            !SessionContainsOnlyKnownArtifacts(descriptor))
        {
            log.Warning("Aktualizacja", $"Wycofana sesja v{descriptor.NewVersion} zawiera nieoczekiwane pliki. Pozostawiono ją bez zmian.");
            return;
        }

        if (!TryDeleteHashedFile(descriptor.FailedExePath, descriptor.NewExeSha256, log) ||
            !TryDeleteHashedFile(descriptor.PendingExePath, descriptor.NewExeSha256, log) ||
            !TryDeleteHashedFile(descriptor.CandidateExePath, descriptor.NewExeSha256, log) ||
            !TryDeleteHashedFile(descriptor.HelperExePath, descriptor.CurrentExeSha256, log) ||
            !TryDeleteValidatedFile(descriptor.DescriptorPath + ".tmp", log) ||
            !TryDeleteValidatedFile(descriptor.DescriptorPath, log)) return;

        TryDeleteEmptyDirectory(Path.GetDirectoryName(descriptor.DescriptorPath)!, log);
    }

    private static void TryCleanupAbandonedSession(UpdateInstallDescriptor descriptor, ApplicationLog log)
    {
        if (!FileMatchesHash(descriptor.TargetExePath, descriptor.CurrentExeSha256) ||
            File.Exists(descriptor.BackupExePath) ||
            File.Exists(descriptor.FailedExePath) ||
            !CanDeleteHashedArtifact(descriptor.PendingExePath, descriptor.NewExeSha256) ||
            !CanDeleteHashedArtifact(descriptor.CandidateExePath, descriptor.NewExeSha256) ||
            !CanDeleteHashedArtifact(descriptor.HelperExePath, descriptor.CurrentExeSha256) ||
            !SessionContainsOnlyKnownArtifacts(descriptor))
        {
            log.Warning("Aktualizacja", $"Nieukończona sesja v{descriptor.NewVersion} nie spełnia warunków bezpiecznego usunięcia. Pozostawiono ją bez zmian.");
            return;
        }

        if (!TryDeleteHashedFile(descriptor.PendingExePath, descriptor.NewExeSha256, log) ||
            !TryDeleteHashedFile(descriptor.CandidateExePath, descriptor.NewExeSha256, log) ||
            !TryDeleteHashedFile(descriptor.HelperExePath, descriptor.CurrentExeSha256, log) ||
            !TryDeleteValidatedFile(descriptor.DescriptorPath + ".tmp", log) ||
            !TryDeleteValidatedFile(descriptor.DescriptorPath, log)) return;

        TryDeleteEmptyDirectory(Path.GetDirectoryName(descriptor.DescriptorPath)!, log);
    }

    private static bool CanDeleteHashedArtifact(string path, string expectedHash) =>
        !File.Exists(path) || (!IsReparsePoint(path) && FileMatchesHash(path, expectedHash));

    private static bool FileMatchesHash(string path, string expectedHash)
    {
        try
        {
            if (!File.Exists(path) || IsReparsePoint(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return GitHubReleasePolicy.HashesEqual(actual, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    private static bool SessionContainsOnlyKnownArtifacts(UpdateInstallDescriptor descriptor)
    {
        var session = Path.GetDirectoryName(descriptor.DescriptorPath)!;
        foreach (var entry in Directory.EnumerateFileSystemEntries(session))
        {
            if (PathsEqual(entry, descriptor.DescriptorPath) ||
                PathsEqual(entry, descriptor.DescriptorPath + ".tmp") ||
                PathsEqual(entry, descriptor.CandidateExePath) ||
                PathsEqual(entry, descriptor.HelperExePath)) continue;
            return false;
        }
        return true;
    }

    private static bool TryDeleteValidatedFile(string path, ApplicationLog log)
    {
        try
        {
            if (!File.Exists(path)) return true;
            RejectReparsePoint(path);
            File.Delete(path);
            return true;
        }
        catch (Exception exception)
        {
            log.Warning("Aktualizacja", $"Nie udało się usunąć starego pliku {Path.GetFileName(path)}.", exception);
            return false;
        }
    }

    private static bool TryDeleteHashedFile(string path, string expectedHash, ApplicationLog log)
    {
        if (!File.Exists(path)) return true;
        if (!FileMatchesHash(path, expectedHash))
        {
            log.Warning("Aktualizacja", $"Nie usunięto pliku {Path.GetFileName(path)}, ponieważ jego suma kontrolna jest nieoczekiwana.");
            return false;
        }
        return TryDeleteValidatedFile(path, log);
    }

    private static void TryDeleteEmptyDirectory(string path, ApplicationLog log)
    {
        try
        {
            RejectReparsePoint(path);
            Directory.Delete(path, recursive: false);
        }
        catch (Exception exception)
        {
            log.Warning("Aktualizacja", $"Nie udało się usunąć pustego katalogu {Path.GetFileName(path)}.", exception);
        }
    }

    private static bool TryRollback(UpdateInstallDescriptor descriptor, Process? newProcess, ApplicationLog log)
    {
        try
        {
            if (newProcess is { HasExited: false })
            {
                TryStopProcess(newProcess);
                newProcess.WaitForExit(10_000);
            }
            ValidateFileHash(descriptor.BackupExePath, descriptor.CurrentExeSha256, "Kopia zapasowa ma niepoprawny skrót.");
            if (File.Exists(descriptor.FailedExePath)) return false;
            RollbackWithRetries(descriptor.BackupExePath, descriptor.TargetExePath, descriptor.FailedExePath);
            ValidateFileHash(descriptor.TargetExePath, descriptor.CurrentExeSha256, "Przywrócony plik ma niepoprawny skrót.");
            descriptor.State = "RolledBack";
            TryWriteDescriptor(descriptor, log);
            log.Warning("Aktualizacja", $"Automatycznie przywrócono v{descriptor.CurrentVersion}.");
            return true;
        }
        catch (Exception exception)
        {
            if (FileMatchesHash(descriptor.TargetExePath, descriptor.CurrentExeSha256))
            {
                descriptor.State = "RolledBack";
                TryWriteDescriptor(descriptor, log);
                log.Warning("Aktualizacja", $"Przywrócono v{descriptor.CurrentVersion}, mimo że Windows zgłosił błąd operacji rollback.", exception);
                return true;
            }
            log.Error("Aktualizacja", "Automatyczne przywrócenie poprzedniej wersji nie powiodło się.", exception);
            return false;
        }
    }

    private static void RollbackWithRetries(string backup, string target, string failed)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (File.Exists(backup)) RejectReparsePoint(backup);
                if (File.Exists(target)) RejectReparsePoint(target);
                if (File.Exists(failed))
                    throw new AppUpdateException(
                        "Plik zabezpieczający nieudaną wersję już istnieje.",
                        "Nie udało się bezpiecznie przywrócić poprzedniej wersji.");

                if (File.Exists(target))
                    RollbackFileTransaction(backup, target, failed);
                else
                    File.Move(backup, target);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                Thread.Sleep(TimeSpan.FromMilliseconds(200 + attempt * 200));
            }
        }

        throw new AppUpdateException(
            "Nie udało się przywrócić kopii zapasowej po kilku próbach.",
            "Windows lub program antywirusowy blokuje przywrócenie poprzedniej wersji.",
            last!);
    }

    private static Process StartUpdatedApplication(UpdateInstallDescriptor descriptor)
    {
        var start = new ProcessStartInfo(descriptor.TargetExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(descriptor.TargetExePath)!
        };
        start.ArgumentList.Add(PostUpdateArgument);
        start.ArgumentList.Add(descriptor.DescriptorPath);
        start.ArgumentList.Add(descriptor.Nonce);
        return Process.Start(start) ?? throw new AppUpdateException(
            "Nie udało się uruchomić nowego EXE po podmianie.",
            "Nie udało się uruchomić nowej wersji aplikacji.");
    }

    private static bool WaitForHealth(EventWaitHandle health, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (health.WaitOne(TimeSpan.FromMilliseconds(500)))
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
                return !process.HasExited;
            }
            if (process.HasExited) return false;
        }
        return false;
    }

    private static void ReplaceWithRetries(string replacement, string target, string backup)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                ReplaceFileTransaction(replacement, target, backup);
                return;
            }
            catch (IOException exception)
            {
                last = exception;
                Thread.Sleep(TimeSpan.FromMilliseconds(200 + attempt * 200));
            }
            catch (UnauthorizedAccessException exception)
            {
                last = exception;
                Thread.Sleep(TimeSpan.FromMilliseconds(200 + attempt * 200));
            }
        }
        throw new AppUpdateException(
            "Nie udało się atomowo podmienić EXE po kilku próbach.",
            "Windows lub program antywirusowy blokuje plik aplikacji. Spróbuj ponownie za chwilę.",
            last!);
    }

    private static void ValidateExecutable(string path, string expectedHash, SemanticVersion expectedVersion)
    {
        ValidateFileHash(path, expectedHash, "Plik EXE ma niepoprawny skrót SHA-256.");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                throw new AppUpdateException("Plik aktualizacji nie ma nagłówka PE/MZ.", "Pobrany plik nie jest poprawną aplikacją Windows.");
        }
        var versionInfo = FileVersionInfo.GetVersionInfo(path);
        if (!TryParseFileVersion(versionInfo.FileVersion, out var actual) || actual != expectedVersion)
            throw new AppUpdateException(
                $"Wersja pliku EXE {versionInfo.FileVersion ?? "(brak)"} nie zgadza się z oczekiwaną {expectedVersion}.",
                "Numer wersji pobranego pliku nie zgadza się z wydaniem GitHub. Instalacja została zatrzymana.");
    }

    private static bool TryParseFileVersion(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var numeric = value.Split(FileVersionSeparators, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (!Version.TryParse(numeric, out var parsed)) return false;
        version = SemanticVersion.FromAssemblyVersion(parsed);
        return true;
    }

    private static SemanticVersion ParseDescriptorVersion(string value, string kind)
    {
        if (SemanticVersion.TryParseReleaseTag($"v{value}", out var version)) return version;
        throw new AppUpdateException($"Niepoprawna wersja {kind}: {value}.", "Dane instalatora aktualizacji są niepoprawne.");
    }

    private static void ValidateFileHash(string path, string expectedHash, string technicalMessage)
    {
        if (!File.Exists(path)) throw new AppUpdateException($"Brakuje pliku: {path}.", "Brakuje pliku potrzebnego do instalacji aktualizacji.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!GitHubReleasePolicy.HashesEqual(actual, expectedHash))
            throw new AppUpdateException(technicalMessage, "Plik aktualizacji zmienił się po weryfikacji. Instalacja została zatrzymana.");
    }

    private static async Task CopyFileDurablyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static void CopyFileDurably(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        input.CopyTo(output, 128 * 1024);
        output.Flush(flushToDisk: true);
    }

    private static void WriteDescriptor(UpdateInstallDescriptor descriptor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, DescriptorJsonOptions);
        if (bytes.Length > DescriptorMaximumBytes) throw new InvalidOperationException("Deskryptor aktualizacji jest zbyt duży.");
        var temporary = descriptor.DescriptorPath + ".tmp";
        if (File.Exists(temporary))
        {
            RejectReparsePoint(temporary);
            File.Delete(temporary);
        }
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, descriptor.DescriptorPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary) && !IsReparsePoint(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Następny zapis rozpozna osierocony plik tymczasowy i spróbuje go bezpiecznie usunąć.
            }
            throw;
        }
    }

    private static byte[] ReadDescriptorBytes(string path, int expectedLength)
    {
        var bytes = new byte[expectedLength];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0) throw new AppUpdateException("Deskryptor został skrócony podczas odczytu.", "Nie udało się odczytać danych instalatora aktualizacji.");
            total += read;
        }
        if (stream.ReadByte() != -1)
            throw new AppUpdateException("Deskryptor urósł podczas odczytu.", "Nie udało się odczytać danych instalatora aktualizacji.");
        return bytes;
    }

    private static bool TryWriteDescriptor(UpdateInstallDescriptor descriptor, ApplicationLog log)
    {
        try
        {
            WriteDescriptor(descriptor);
            return true;
        }
        catch (Exception exception)
        {
            log.Warning("Aktualizacja", "Nie udało się zapisać stanu instalatora.", exception);
            return false;
        }
    }

    private static void EnsureTargetDirectoryWritable(string targetDirectory)
    {
        var probe = Path.Combine(targetDirectory, $".ksef-update-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AppUpdateException(
                $"Brak prawa zapisu w katalogu aplikacji: {targetDirectory}.",
                "Windows nie pozwala aplikacji zaktualizować pliku w tym katalogu. Pobierz nową wersję ręcznie z GitHuba lub przenieś aplikację do własnego folderu użytkownika.",
                exception);
        }
    }

    private static void EnsureFreeSpace(string targetDirectory, long executableSize)
    {
        try
        {
            var root = Path.GetPathRoot(targetDirectory);
            if (string.IsNullOrWhiteSpace(root)) return;
            var required = checked(executableSize * 3 + 100L * 1024 * 1024);
            if (new DriveInfo(root).AvailableFreeSpace < required)
                throw new AppUpdateException(
                    $"Za mało miejsca na aktualizację; wymagane co najmniej {required} bajtów.",
                    "Na dysku jest za mało miejsca na aktualizację i bezpieczną kopię poprzedniej wersji.");
        }
        catch (AppUpdateException)
        {
            throw;
        }
        catch (Exception)
        {
            // Brak danych DriveInfo nie jest wystarczającym powodem do blokowania instalacji; zapis i tak jest limitowany przez system plików.
        }
    }

    private static void WriteFailureMarker(string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.UpdateFailureFile, message, new UTF8Encoding(false));
        }
        catch
        {
            // Marker jest pomocniczy; błąd zapisu nie może przesłonić wyniku rollbacku.
        }
    }

    private static void StartApplicationWithoutArguments(string targetPath, ApplicationLog log)
    {
        try
        {
            var start = new ProcessStartInfo(targetPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(targetPath)!
            };
            _ = Process.Start(start);
        }
        catch (Exception exception)
        {
            log.Error("Aktualizacja", "Nie udało się ponownie uruchomić aplikacji.", exception);
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Kolejne kroki zweryfikują, czy plik można bezpiecznie przywrócić.
        }
    }

    private static bool IsValidNonce(string? value) =>
        value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectChild(string candidate, string parent)
    {
        var candidateDirectory = Directory.GetParent(Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        return candidateDirectory is not null && PathsEqual(candidateDirectory, parent);
    }

    private static void RejectReparsePoint(string path)
    {
        if (IsReparsePoint(path))
            throw new AppUpdateException($"Ścieżka aktualizacji jest dowiązaniem: {path}.", "Nie można bezpiecznie zainstalować aktualizacji w tym katalogu.");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

}
