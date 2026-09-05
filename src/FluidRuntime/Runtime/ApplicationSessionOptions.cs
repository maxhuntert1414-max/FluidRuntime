using System.Reflection.PortableExecutable;

namespace FluidRuntime.Runtime;

internal sealed record ApplicationSessionOptions(string Executable, string LayerDirectory, string Output,
    int Seconds, int PrioritySeconds, bool ObserveVulkan, IReadOnlyList<string> Arguments)
{
    internal const string Usage = "fluidruntime app-session --exe <application.exe> --layer-dir <native-directory> " +
        "--out <session.json> --acknowledge-no-anticheat true [--seconds <1-120>] " +
        "[--priority-seconds <0-30>] [--observe-vulkan <true|false>] [-- <application arguments>]";

    internal static ApplicationSessionOptions Parse(string[] args)
    {
        var split = Array.IndexOf(args, "--");
        if (split < 0) split = args.Length;
        if (split % 2 != 1 || args[0] != "app-session") throw new ArgumentException(Usage);
        string[] allowed = ["--exe", "--layer-dir", "--out", "--acknowledge-no-anticheat",
            "--seconds", "--priority-seconds", "--observe-vulkan"];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < split; i += 2)
            if (!allowed.Contains(args[i]) || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Invalid or duplicate option: {args[i]}");
        string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}.");
        int Number(string key, int fallback, int max) => options.TryGetValue(key, out var value) ?
            int.TryParse(value, out var n) && n >= 0 && n <= max ? n : throw new ArgumentException($"Invalid {key}.") : fallback;
        if (Required("--acknowledge-no-anticheat") != "true")
            throw new ArgumentException("Explicit acknowledgement is required; protected/anti-cheat applications are unsupported.");
        var seconds = Number("--seconds", 10, 120);
        var priority = Number("--priority-seconds", 0, 30);
        var observe = options.GetValueOrDefault("--observe-vulkan", "true") switch
        { "true" => true, "false" => false, _ => throw new ArgumentException("Invalid observation mode.") };
        if (seconds == 0 || priority > seconds || (!observe && priority != 0))
            throw new ArgumentException("Priority requires verified observation and must fit inside the session.");
        return new(Path.GetFullPath(Required("--exe")), Path.GetFullPath(Required("--layer-dir")),
            Path.GetFullPath(Required("--out")), seconds, priority, observe,
            split < args.Length ? args[(split + 1)..] : []);
    }

    internal static void RequireApplicationPath(string path)
    {
        var full = Path.GetFullPath(path);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] blocked = ["cmd.exe", "powershell.exe", "pwsh.exe", "rundll32.exe", "regsvr32.exe",
            "msiexec.exe", "easyanticheat.exe", "easyanticheat_eos.exe", "start_protected_game.exe", "beservice.exe"];
        if (full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            blocked.Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("System executables, launch shells and known protected launchers are not eligible.");
        using var stream = File.OpenRead(full);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.IsDll)
            throw new ArgumentException("Application sessions require a native x64 executable image.");
    }
}
