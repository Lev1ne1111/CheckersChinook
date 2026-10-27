namespace Checkers.Api.Options;

/// <summary>The "Engine" section of appsettings.json.</summary>
public sealed class EngineOptions
{
    public const string Section = "Engine";

    /// "chinook": KingsRow engine with Chinook endgame databases, run in worker processes.
    /// "fake":    no engine; plays the first legal move. For tests and for trying the API without the engine.
    public string Type { get; set; } = "chinook";

    /// Engine DLL (Kingsrow64.dll).
    public string Path { get; set; } = "";

    /// Number of long-lived worker processes.
    public int Workers { get; set; } = 2;

    /// Folder with the endgame database files. Empty = run without databases.
    public string Databases { get; set; } = "";

    /// Worker executable; a relative path is resolved against the API's own folder.
    public string WorkerPath { get; set; } = "worker/Checkers.EngineWorker.exe";

    /// Current directory for the engine. Empty = parent of the DLL's folder (the CheckerBoard layout).
    public string? WorkDir { get; set; }

    /// Writable folder for the engine's own log when the account has no usable Documents folder
    /// (the case for IIS app pool accounts). A relative path is resolved against the API's folder.
    public string HomeDir { get; set; } = "logs/engine";

    /// Per worker: engine hashtable and endgame DB cache, in MB.
    public int HashMb { get; set; } = 128;
    public int DbMb { get; set; } = 512;

    /// How long a worker may take to start, load the engine and warm up.
    public int StartupTimeoutMs { get; set; } = 30_000;

    /// After a cancelled search: how long to wait for the engine to stop before the worker is killed.
    public int StopGraceMs { get; set; } = 300;
}
