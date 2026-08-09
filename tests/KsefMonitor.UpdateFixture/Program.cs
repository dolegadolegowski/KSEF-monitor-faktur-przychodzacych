using System;
using System.IO;
using System.Text.Json;
using System.Threading;

if (!OperatingSystem.IsWindows() || args.Length != 3 ||
    !string.Equals(args[0], "--post-update", StringComparison.Ordinal))
    return 10;

try
{
    var descriptorPath = Path.GetFullPath(args[1]);
    if (args[2].Length != 32 || !File.Exists(descriptorPath)) return 11;
    using var descriptor = JsonDocument.Parse(File.ReadAllBytes(descriptorPath));
    var healthEventName = descriptor.RootElement.GetProperty("HealthEventName").GetString();
    if (string.IsNullOrWhiteSpace(healthEventName)) return 12;
    if (File.Exists(Path.Combine(Path.GetDirectoryName(descriptorPath)!, "suppress-health"))) return 7;

    using var healthEvent = EventWaitHandle.OpenExisting(healthEventName);
    healthEvent.Set();
    Thread.Sleep(TimeSpan.FromSeconds(5));
    return 0;
}
catch
{
    return 13;
}
