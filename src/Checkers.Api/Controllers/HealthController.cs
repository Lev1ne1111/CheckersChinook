using Checkers.Api.Engine;
using Microsoft.AspNetCore.Mvc;

namespace Checkers.Api.Controllers;

[ApiController]
public sealed class HealthController(IEnginePool pool) : ControllerBase
{
    /// <summary>
    /// GET /healthz -> { "ok": true, "workers": 2, ... }
    /// ok = every configured engine worker is running and warmed up; otherwise HTTP 503 with ok=false.
    /// </summary>
    [HttpGet("/healthz")]
    public IActionResult Get()
    {
        int ready = pool.ReadyCount;
        bool ok = ready == pool.Configured;
        var body = new
        {
            ok,
            workers = ready,
            configured = pool.Configured,
            engine = pool.EngineName,
            tablebasePieces = pool.DbPieces,
        };
        return ok ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
