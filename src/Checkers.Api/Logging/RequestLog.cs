using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Checkers.Api.Options;
using Microsoft.Extensions.Options;

namespace Checkers.Api.Logging;

/// <summary>
/// Fields of one request's log line. The middleware creates it; controllers fill in what they know.
/// </summary>
public sealed class RequestLogEntry
{
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string RequestId { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public long TimeMs { get; set; }
    public string? Level { get; set; }
    public string? PositionKey { get; set; }
    public string? BestMove { get; set; }
    public int? Depth { get; set; }
    public long? Nodes { get; set; }
    public bool? TablebaseHit { get; set; }
    public bool? Cached { get; set; }
    public string? Error { get; set; }

    private const string ItemKey = "Checkers.RequestLog";

    public static RequestLogEntry? Of(HttpContext context) => context.Items[ItemKey] as RequestLogEntry;
    internal void AttachTo(HttpContext context) => context.Items[ItemKey] = this;
}

/// <summary>
/// Writes one JSON line per request under /v1 and /healthz: to the ILogger ("Checkers.Api.Requests")
/// and to logs/requests-YYYYMMDD.jsonl. File writes happen on a background task, so a slow
/// disk never delays a response.
/// </summary>
public sealed class RequestLogMiddleware(RequestDelegate next, RequestLogWriter writer)
{
    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) && !path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var entry = new RequestLogEntry
        {
            RequestId = context.TraceIdentifier,
            Method = context.Request.Method,
            Path = path,
        };
        entry.AttachTo(context);
        context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context);
            entry.Status = context.Response.StatusCode;
        }
        catch
        {
            entry.Status = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            entry.TimeMs = sw.ElapsedMilliseconds;
            if (context.RequestAborted.IsCancellationRequested && entry.Status < 400) entry.Status = 499;   // client went away
            writer.Write(entry);
        }
    }
}

public sealed class RequestLogWriter : IHostedService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _log;
    private readonly string? _directory;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private Task? _pump;

    public RequestLogWriter(IOptions<RequestLogOptions> options, IHostEnvironment env, ILoggerFactory loggers)
    {
        _log = loggers.CreateLogger("Checkers.Api.Requests");
        string dir = options.Value.Directory;
        if (!string.IsNullOrWhiteSpace(dir))
            _directory = Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir);
    }

    public void Write(RequestLogEntry entry)
    {
        string line = JsonSerializer.Serialize(entry, Json);
        _log.LogInformation("{RequestJson}", line);
        if (_directory is not null) _lines.Writer.TryWrite(line);
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_directory is not null) _pump = Task.Run(PumpAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _lines.Writer.TryComplete();
        if (_pump is not null) await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2), ct));
    }

    private async Task PumpAsync()
    {
        bool warned = false;
        await foreach (var line in _lines.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(_directory!);
                string file = Path.Combine(_directory!, $"requests-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                await File.AppendAllTextAsync(file, line + Environment.NewLine);
                warned = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // e.g. the IIS app pool may not write here; say so once, keep serving requests
                if (!warned) _log.LogWarning(ex, "cannot write the request log to {Dir}", _directory);
                warned = true;
            }
        }
    }
}
