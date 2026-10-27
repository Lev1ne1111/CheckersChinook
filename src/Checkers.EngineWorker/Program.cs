// Engine worker: one long-lived process per engine instance, started by Checkers.Api.
//
//   Checkers.EngineWorker.exe                             JSON-lines protocol on stdin/stdout (see WorkerMessages.cs)
//   Checkers.EngineWorker.exe spike --dll ... --db ...    one-off diagnostics of the engine DLL

using Checkers.EngineWorker;
using Checkers.EngineWorker.Spike;

if (args.Length > 0 && args[0] == "spike")
    return EngineSpike.Run(args[1..]);

return await WorkerHost.RunAsync();
