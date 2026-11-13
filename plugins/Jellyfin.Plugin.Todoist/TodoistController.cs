using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Todoist;

[ApiController]
[Route("Todoist")]
[Authorize]
public sealed class TodoistController(TodoistService service) : ControllerBase
{
    private const string ProofCookie = "TodoistOAuthProof";
    private const string ReturnCookie = "TodoistOAuthReturn";

    private Guid CurrentUser()
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) || id == Guid.Empty
            || string.Equals(User.FindFirst("Jellyfin-IsApiKey")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException();
        return id;
    }

    private async Task<IActionResult> Handle(Func<Task<object>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await action()); }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (InvalidOperationException ex) { return BadRequest(new { Error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { Error = ex.Message }); }
    }

    [HttpGet("Account")]
    [AllowAnonymous]
    public IActionResult Account()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Frame-Options"] = "DENY";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Todoist.Web.account.html")!;
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
    }

    [HttpGet("Status")]
    public Task<IActionResult> Status(CancellationToken ct) => Handle(() => service.StatusAsync(CurrentUser(), ct));

    [HttpPost("Authorize")]
    public Task<IActionResult> Authorize(CancellationToken ct, [FromQuery] bool dashboard = false) => Handle(async () =>
    {
        var proof = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var url = await service.AuthorizeAsync(CurrentUser(), proof, ct);
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = Request.PathBase + "/Todoist",
            MaxAge = TimeSpan.FromMinutes(10),
            IsEssential = true
        };
        Response.Cookies.Append(ProofCookie, proof, options);
        Response.Cookies.Append(ReturnCookie, dashboard ? "dashboard" : "account", options);
        return new { Url = url };
    });

    [HttpGet("Callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string? state, [FromQuery] string? code,
        [FromQuery] string? error, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            if (string.IsNullOrEmpty(state) || !Request.Cookies.TryGetValue(ProofCookie, out var proof))
                return BadRequest("Authorization expired. Return to Todoist/Account and try again.");
            var connected = await service.CallbackAsync(state, proof, code, error, ct);
            Response.Cookies.Delete(ProofCookie, new CookieOptions { Path = Request.PathBase + "/Todoist" });
            var dashboard = Request.Cookies[ReturnCookie] == "dashboard";
            Response.Cookies.Delete(ReturnCookie, new CookieOptions { Path = Request.PathBase + "/Todoist" });
            if (dashboard) return Redirect(Request.PathBase + "/web/index.html#!/configurationpage?name=todoist&authorization=" + (connected ? "success" : "denied"));
            return Redirect(Request.PathBase + "/Todoist/Account?authorization=" + (connected ? "success" : "denied"));
        }
        catch (InvalidOperationException) { return BadRequest("Authorization expired or invalid. Return to Todoist/Account and try again."); }
        catch (HttpRequestException) { return StatusCode(502, "Todoist authorization failed. Return to Todoist/Account and try again."); }
    }

    [HttpGet("Projects")]
    public Task<IActionResult> Projects(CancellationToken ct) => Handle(async () => await service.ProjectsAsync(CurrentUser(), ct));

    [HttpGet("Connections")]
    [Authorize(Policy = "RequiresElevation")]
    public Task<IActionResult> Connections(CancellationToken ct) => Handle(async () => await service.ConnectionsAsync(ct));

    public sealed record ProjectSelection(string ProjectId);

    [HttpPost("Project")]
    public Task<IActionResult> Project([FromBody] ProjectSelection selection, CancellationToken ct) => Handle(async () =>
    {
        if (string.IsNullOrWhiteSpace(selection.ProjectId)) throw new InvalidOperationException("Select a Todoist project.");
        await service.SetProjectAsync(CurrentUser(), selection.ProjectId, ct);
        return new { Saved = true };
    });

    [HttpPost("Sync")]
    public Task<IActionResult> Sync() => Handle(() =>
    {
        CurrentUser();
        service.RequestSync();
        return Task.FromResult<object>(new { Queued = true });
    });

    [HttpDelete("Authorization")]
    public Task<IActionResult> Disconnect(CancellationToken ct) => Handle(async () =>
    {
        await service.DisconnectAsync(CurrentUser(), ct);
        return new { Disconnected = true };
    });
}
