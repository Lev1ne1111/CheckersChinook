using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Checkers.EngineProtocol;

namespace Checkers.EngineWorker;

/// <summary>
/// The worker's main loop: reads JSON-line requests from stdin and writes responses to stdout.
///
/// "stop" and "ping" are handled immediately by the reading thread, so they work while a
/// search is running. Everything else goes into a queue and runs one at a time, in order.
/// When stdin closes (the API died, or a test file was piped in), queued work is finished
/// and the process exits.
/// </summary>
internal static class WorkerHost
{
    public static async Task<int> RunAsync()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: true);
        var writeLock = new object();

        void Send(WorkerResponse response)
        {
            string json = ProtocolJson.Serialize(response);
            lock (writeLock) stdout.WriteLine(json);
        }

        using var session = new EngineSession();
        var queue = Channel.CreateUnbounded<WorkerRequest>(new UnboundedChannelOptions { SingleReader = true });
        int busy = 0;

        var processor = Task.Run(async () =>
        {
            await foreach (var request in queue.Reader.ReadAllAsync())
            {
                Volatile.Write(ref busy, 1);
                try
                {
                    Send(Handle(session, request));
                }
                catch (Exception ex)
                {
                    if (ex is not WorkerException) Console.Error.WriteLine($"[worker {Environment.ProcessId}] {ex}");
                    Send(WorkerResponse.Fail(request.Id, ex.Message));
                }
                finally
                {
                    Volatile.Write(ref busy, 0);
                }
            }
        });

        Console.Error.WriteLine($"[worker {Environment.ProcessId}] ready");
        bool quit = false;
        string? line;
        while (!quit && (line = await stdin.ReadLineAsync()) is not null)
        {
            line = line.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            WorkerRequest? request;
            try
            {
                // plain words ("ping", "stop", "quit") are accepted too, handy when typing by hand
                request = line.StartsWith('{')
                    ? ProtocolJson.Deserialize<WorkerRequest>(line)
                    : new WorkerRequest { Cmd = line };
            }
            catch (JsonException ex)
            {
                Send(WorkerResponse.Fail(null, $"bad json: {ex.Message}"));
                continue;
            }
            if (request is null) continue;

            switch (request.Cmd.Trim().ToLowerInvariant())
            {
                case Commands.Stop:
                    session.Stop();
                    break;
                case Commands.Ping:
                    Send(new WorkerResponse { Id = request.Id, Ok = true, Busy = Volatile.Read(ref busy) == 1 });
                    break;
                case Commands.Quit:
                    session.Stop();
                    quit = true;
                    break;
                default:
                    queue.Writer.TryWrite(request);
                    break;
            }
        }

        queue.Writer.Complete();
        if (quit)
            await Task.WhenAny(processor, Task.Delay(TimeSpan.FromSeconds(2)));
        else
            await processor;   // stdin closed: finish what was queued

        Console.Error.WriteLine($"[worker {Environment.ProcessId}] exit");
        return 0;
    }

    private static WorkerResponse Handle(EngineSession session, WorkerRequest request) =>
        request.Cmd.Trim().ToLowerInvariant() switch
        {
            Commands.Init => new WorkerResponse { Id = request.Id, Ok = true, Init = session.Init(request) },
            Commands.Search => new WorkerResponse { Id = request.Id, Ok = true, Search = session.Search(request) },
            var other => WorkerResponse.Fail(request.Id, $"unknown command '{other}'"),
        };
}
