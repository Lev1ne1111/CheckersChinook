using System.Diagnostics;
using Checkers.Api.Engine;
using Checkers.Api.Logging;
using Checkers.Api.Moves;
using Microsoft.AspNetCore.Mvc;

namespace Checkers.Api.Controllers;

/// <summary>
/// POST /v1/move/suggest  - best move for a PDN position
/// POST /v1/move/validate - is a move legal in a position
/// GET  /v1/move/legal    - all legal moves (helper for the test board)
///
/// Status codes: 200 ok; 422 invalid position/request; 504 hardTimeMs passed;
/// 503 no engine worker running; 500 the engine failed or returned an illegal move.
/// </summary>
[ApiController]
[Route("v1/move")]
public sealed class MoveController(MoveService moves, ILogger<MoveController> log) : ControllerBase
{
    [HttpPost("suggest")]
    public async Task<IActionResult> Suggest([FromBody] SuggestRequest? request)
    {
        var sw = Stopwatch.StartNew();
        var entry = RequestLogEntry.Of(HttpContext);
        SuggestPlan plan;
        try
        {
            plan = moves.Prepare(request);
        }
        catch (MoveRequestException ex)
        {
            return Fail(ex.Status, ex.Code, ex.Message, entry);
        }
        if (entry is not null)
        {
            entry.Level = plan.Level.Name;
            entry.PositionKey = plan.PositionKey;
        }

        // hardTimeMs is enforced here: it covers waiting for a free worker and the search.
        using var hard = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        hard.CancelAfter(plan.Limits.HardTimeMs);
        try
        {
            var response = await moves.SuggestAsync(plan, hard.Token);
            response.Info.TimeMs = sw.ElapsedMilliseconds;
            if (entry is not null)
            {
                entry.BestMove = response.BestMove;
                entry.Depth = response.Depth;
                entry.Nodes = response.Nodes;
                entry.TablebaseHit = response.Info.TablebaseHit;
                entry.Cached = response.Info.Cached;
            }
            return Ok(response);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();   // client gone; logged as 499
        }
        catch (OperationCanceledException)
        {
            return Fail(StatusCodes.Status504GatewayTimeout, "timeout",
                        $"no move within hardTimeMs = {plan.Limits.HardTimeMs} ms", entry);
        }
        catch (EngineUnavailableException ex)
        {
            return Fail(StatusCodes.Status503ServiceUnavailable, "engine_unavailable", ex.Message, entry);
        }
        catch (EngineException ex)
        {
            log.LogError(ex, "engine failed for {Key}", plan.PositionKey);
            return Fail(StatusCodes.Status500InternalServerError, "engine_error", ex.Message, entry);
        }
    }

    [HttpPost("validate")]
    public IActionResult Validate([FromBody] ValidateRequest? request)
    {
        try
        {
            return Ok(moves.Validate(request));
        }
        catch (MoveRequestException ex)
        {
            return Fail(ex.Status, ex.Code, ex.Message, RequestLogEntry.Of(HttpContext));
        }
    }

    /// <summary>
    /// GET /v1/move/legal?position=... -> { position, sideToMove, moves: ["22-18", "22x15x8", ...] }
    /// Not in the spec; used by the test board to highlight squares.
    /// </summary>
    [HttpGet("legal")]
    public IActionResult Legal([FromQuery] string? position)
    {
        try
        {
            var p = MoveService.ParsePosition(position);
            return Ok(new
            {
                position = p.ToFen(),
                sideToMove = p.SideToMove == Core.Side.White ? "W" : "B",
                moves = p.LegalMoves().Select(m => m.ToLongNotation()).ToList(),
            });
        }
        catch (MoveRequestException ex)
        {
            return Fail(ex.Status, ex.Code, ex.Message, RequestLogEntry.Of(HttpContext));
        }
    }

    private ObjectResult Fail(int status, string code, string message, RequestLogEntry? entry)
    {
        if (entry is not null) entry.Error = $"{code}: {message}";
        var problem = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = message,
        };
        problem.Extensions["requestId"] = HttpContext.TraceIdentifier;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
