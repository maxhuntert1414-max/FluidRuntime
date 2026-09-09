using FluidRuntime.Runtime;

namespace FluidRuntime.Cli;

public sealed record GatewayBackendOptions(string Mode, string? LibraryPath = null, string? LibrarySha256 = null)
{
    public static GatewayBackendOptions Server { get; } = new("server");
    public const string Usage = "[--gateway-backend server|inprocess] " +
        "[--gateway-library <absolute-dll-path> --gateway-library-sha256 <sha256>] ";

    internal static GatewayBackendOptions Extract(ref string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args.Length % 2 != 1)
            throw new ArgumentException("Options require name/value pairs. " + Usage);
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var remaining = new List<string> { args[0] };
        for (var i = 1; i < args.Length; i += 2)
        {
            if (args[i] is "--gateway-backend" or "--gateway-library" or "--gateway-library-sha256")
            {
                if (!selected.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException($"Duplicate option: {args[i]}");
            }
            else remaining.AddRange([args[i], args[i + 1]]);
        }
        var mode = selected.GetValueOrDefault("--gateway-backend", "server");
        var path = selected.GetValueOrDefault("--gateway-library");
        var sha = selected.GetValueOrDefault("--gateway-library-sha256");
        if (mode is not ("server" or "inprocess"))
            throw new ArgumentException("--gateway-backend must be server or inprocess.");
        if (mode == "server" && (path is not null || sha is not null))
            throw new ArgumentException("DLL options require --gateway-backend inprocess.");
        if (mode == "inprocess")
        {
            if (path is null || !Path.IsPathFullyQualified(path) || sha is null ||
                sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                throw new ArgumentException("In-process mode requires an absolute DLL path and SHA-256.");
            for (var i = 1; i < remaining.Count; i += 2)
                if (remaining[i] is "--host" or "--port" or "--gateway-pid" or "--gateway-executable-sha256")
                    throw new ArgumentException("TCP identity options cannot be mixed with in-process mode.");
        }
        args = remaining.ToArray();
        return new(mode, path, sha?.ToLowerInvariant());
    }

    public FluidLinkGatewayUpdateUploadAuthorizer Create(
        string host, int port, int timeoutMs, int processId, string executableSha256) =>
        Mode switch
        {
            "server" => new(host, port, TimeSpan.FromMilliseconds(timeoutMs), processId, executableSha256),
            "inprocess" => FluidLinkGatewayUpdateUploadAuthorizer.InProcess(
                LibraryPath!, LibrarySha256!, TimeSpan.FromMilliseconds(timeoutMs)),
            _ => throw new ArgumentException("Unknown Gateway backend.")
        };
}
