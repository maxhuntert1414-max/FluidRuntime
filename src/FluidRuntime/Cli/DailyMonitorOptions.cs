using System.Globalization;

namespace FluidRuntime.Cli;

internal sealed record DailyMonitorOptions(int ProcessId, int IntervalMs, int Seconds, string Output, string? Probe)
{
    internal const string Usage = """
        FluidRuntime | daily use (read-only)

          fluidruntime processes [--name text]
          fluidruntime monitor --pid 1234
          fluidruntime monitor --pid 1234 --seconds 60 --out session.json

        monitor options:
          --interval-ms 1000..10000   Default: 1000
          --seconds 1..86400         Omit to run until Ctrl+C or target exit
          --out path.json            Default: %LOCALAPPDATA%/FluidRuntime/monitor-latest.json
          --native-probe path.exe    Default: probe beside fluidruntime.exe
          --cpu-only                 Skip native GPU telemetry

        CPU is normalized to this machine's logical processors. GPU is the busiest
        engine, not total GPU load. Missing counters are NA, not zero.
        No hooks, priority changes, drivers, autostart or Gateway decisions here.
        For opt-in Vulkan observation/temporary priority: fluidruntime app-session --help
        """;

    internal static DailyMonitorOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var cpuOnly = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--cpu-only" && !cpuOnly) { cpuOnly = true; continue; }
            if (args[i] is not ("--pid" or "--interval-ms" or "--seconds" or "--out" or "--native-probe") ||
                i + 1 == args.Length || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Unknown, duplicate or incomplete option: {args[i]}");
            i++;
        }
        if (cpuOnly && values.ContainsKey("--native-probe"))
            throw new ArgumentException("Choose --cpu-only or --native-probe, not both.");
        var pid = Number("--pid", 0, 1, int.MaxValue);
        var interval = Number("--interval-ms", 1000, 1000, 10000);
        var seconds = values.ContainsKey("--seconds") ? Number("--seconds", 0, 1, 86400) : 0;
        var output = Path.GetFullPath(values.GetValueOrDefault("--out") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluidRuntime", "monitor-latest.json"));
        if (!string.Equals(Path.GetExtension(output), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Monitor output must be a .json file.");
        var probe = cpuOnly ? null : Path.GetFullPath(values.GetValueOrDefault("--native-probe") ??
            Path.Combine(AppContext.BaseDirectory, "fluidruntime-native-probe.exe"));
        if (values.ContainsKey("--native-probe") && !File.Exists(probe))
            throw new FileNotFoundException("The requested native probe does not exist.", probe);
        return new(pid, interval, seconds, output, probe);

        int Number(string name, int fallback, int min, int max)
        {
            var text = values.GetValueOrDefault(name);
            var number = fallback;
            if ((text is not null && !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)) ||
                number < min || number > max)
                throw new ArgumentException($"{name} must be an integer between {min} and {max}.");
            return number;
        }
    }
}
