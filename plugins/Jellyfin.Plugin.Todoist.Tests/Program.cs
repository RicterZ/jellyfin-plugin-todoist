using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Todoist;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

var ct = CancellationToken.None;
var root = Path.Combine(Path.GetTempPath(), "todoist-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    checks++;
}
async Task Throws<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); } catch (T) { checks++; return; }
    throw new Exception(message);
}

try
{
    Check(EpisodeRules.Title("Series", "Pilot", 0, 1) == "Series S00E01", "Season zero must be preserved");
    Check(EpisodeRules.Title("Series", "Pilot", null, 5) == "Series E05", "Missing season");
    Check(EpisodeRules.Title("Series", "Pilot", 1, null) == "Pilot", "Missing episode");
    Check(EpisodeRules.IsComplete(120 * TimeSpan.TicksPerSecond, 60 * TimeSpan.TicksPerSecond, false), "60-second boundary");
    Check(!EpisodeRules.IsComplete(121 * TimeSpan.TicksPerSecond, 60 * TimeSpan.TicksPerSecond, false), "Early stop");
    Check(!EpisodeRules.IsComplete(null, 0, false), "Unknown runtime");
    Check(EpisodeRules.IsComplete(null, null, true), "Jellyfin completion flag");
    Check(EpisodeRules.RequestId("u:p:item") == EpisodeRules.RequestId("u:p:item"), "Stable request ID");
    Check(EpisodeRules.RequestId("u:p:item") != EpisodeRules.RequestId("v:p:item"), "User-scoped request ID");

    var paths = Stub<IApplicationPaths>.Create((method, _) => method.ReturnType == typeof(string) ? root : null);
    var serializer = Stub<IXmlSerializer>.Create((method, _) =>
        method.Name == "DeserializeFromFile" ? new Configuration() : null);
    var plugin = new Plugin(paths, serializer);
    plugin.Configuration.ClientId = "client";
    plugin.Configuration.ClientSecret = "secret";
    plugin.Configuration.RedirectUri = "https://jellyfin.example/base/Todoist/Callback";

    var first = new User("first", "auth", "reset");
    var second = new User("second", "auth", "reset");
    var episode = new Episode { Id = Guid.NewGuid(), Name = "Pilot", SeriesName = "Series", ParentIndexNumber = 1, IndexNumber = 1 };
    var played = new HashSet<Guid>();
    var queries = new List<Guid>();
    var libraryItems = new Dictionary<Guid, Episode> { [episode.Id] = episode };
    EventHandler<ItemChangeEventArgs>? itemAdded = null;
    EventHandler<UserDataSaveEventArgs>? userDataSaved = null;
    var library = Stub<ILibraryManager>.Create((method, args) =>
    {
        if (method.Name == "add_ItemAdded") itemAdded += (EventHandler<ItemChangeEventArgs>)args![0]!;
        if (method.Name == "remove_ItemAdded") itemAdded -= (EventHandler<ItemChangeEventArgs>)args![0]!;
        if (method.Name == "GetItemList")
        {
            var query = (InternalItemsQuery)args![0]!;
            Check(query.User != null && query.Recursive, "Every query must be user scoped");
            Check(query.ItemIds.Length > 0, "Never query the entire library");
            queries.Add(query.User!.Id);
            return query.ItemIds.Where(libraryItems.ContainsKey).Select(id => (BaseItem)libraryItems[id]).ToArray();
        }
        return null;
    });
    var users = Stub<IUserManager>.Create((method, args) => method.Name == "GetUserById"
        ? ((Guid)args![0]! == first.Id ? first : second) : null);
    var userData = Stub<IUserDataManager>.Create((method, args) =>
    {
        if (method.Name == "add_UserDataSaved") userDataSaved += (EventHandler<UserDataSaveEventArgs>)args![0]!;
        if (method.Name == "remove_UserDataSaved") userDataSaved -= (EventHandler<UserDataSaveEventArgs>)args![0]!;
        return method.Name == "GetUserData"
            ? new UserItemData { Key = "episode", Played = played.Contains(((User)args![0]!).Id) } : null;
    });
    EventHandler<PlaybackStopEventArgs>? playbackStopped = null;
    var sessions = Stub<ISessionManager>.Create((method, args) =>
    {
        if (method.Name == "add_PlaybackStopped") playbackStopped += (EventHandler<PlaybackStopEventArgs>)args![0]!;
        if (method.Name == "remove_PlaybackStopped") playbackStopped -= (EventHandler<PlaybackStopEventArgs>)args![0]!;
        return null;
    });
    var handler = new FakeHandler();
    var api = new TodoistApi(handler);
    var initial = new State();
    initial.Users[first.Id] = new Binding { TodoistUserId = "todoist-user", AccessToken = "first-token", ProjectId = "project-a" };
    initial.Users[second.Id] = new Binding { TodoistUserId = "todoist-second", AccessToken = "second-token", ProjectId = "project-b" };
    StateFile.Save(plugin.StatePath, initial);
    Check(StateFile.Load(plugin.StatePath).Users.Count == 2, "Persist bindings");
    if (!OperatingSystem.IsWindows())
        Check(File.GetUnixFileMode(plugin.StatePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Private credential file");

    TodoistService NewService() => new(library, users, userData, sessions, api, NullLogger<TodoistService>.Instance);
    async Task FlushEvents(TodoistService service, Action action)
    {
        // The second user's attempt changes either LastSync or LastError on every processed cycle.
        string Progress(object status)
        {
            var value = JsonSerializer.SerializeToElement(status);
            return value.GetProperty("LastSync").ToString() + value.GetProperty("LastError").ToString();
        }
        var before = Progress(await service.StatusAsync(second.Id, ct));
        action();
        service.RequestSync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Progress(await service.StatusAsync(second.Id, timeout.Token)) == before)
            await Task.Delay(10, timeout.Token);
    }
    async Task Cycle(Action? action = null)
    {
        using var service = NewService();
        await service.StartAsync(ct);
        try
        {
            await service.StatusAsync(first.Id, ct);
            if (action != null) await FlushEvents(service, action);
        }
        finally { await service.StopAsync(ct); }
    }

    await Cycle();
    Check(handler.Creates == 0 && queries.Count == 0, "Startup must not backfill historical episodes");
    using (var service = NewService())
    {
        await service.StartAsync(ct);
        try
        {
            await service.SetProjectAsync(second.Id, "project-b", ct);
            await FlushEvents(service, () => service.RequestSync());
            Check(handler.Creates == 0 && queries.Count == 0, "Linking and manual retry on a running service must not scan or import history");
        }
        finally { await service.StopAsync(ct); }
    }
    await Cycle(() => userDataSaved!(null, new UserDataSaveEventArgs
    {
        UserId = first.Id, Item = episode, SaveReason = UserDataSaveReason.TogglePlayed,
        UserData = new UserItemData { Key = "episode", Played = true }
    }));
    Check(handler.Creates == 0, "Marking an untracked historical item watched must not create a task");
    await Cycle(() => itemAdded!(null, new ItemChangeEventArgs { Item = episode }));
    Check(handler.Creates == 2, "Create only after ItemAdded, once per Jellyfin user");
    Check(handler.RequestIds.Distinct().Count() == 2, "Task IDs isolated by user/project");
    Check(queries.Contains(first.Id) && queries.Contains(second.Id), "Sync both users");
    var historical = new Episode { Id = Guid.NewGuid(), Name = "Historical", SeriesName = "Series" };
    libraryItems[historical.Id] = historical;
    await Cycle(() => itemAdded!(null, new ItemChangeEventArgs { Item = episode }));
    Check(handler.Creates == 2, "No duplicates after restart or duplicate ItemAdded");
    Check(!StateFile.Load(plugin.StatePath).Users[second.Id].Tasks.ContainsKey(historical.Id), "Unwatched historical items must not be backfilled");
    played.Add(first.Id);
    handler.FailClose = true;
    await Cycle(() => userDataSaved!(null, new UserDataSaveEventArgs
    {
        UserId = first.Id, Item = episode, SaveReason = UserDataSaveReason.TogglePlayed,
        UserData = new UserItemData { Key = "episode", Played = true }
    }));
    Check(StateFile.Load(plugin.StatePath).Users[first.Id].Tasks[episode.Id].CompletionRequested, "Manual watched event persists completion intent");
    Check(!StateFile.Load(plugin.StatePath).Users[first.Id].Tasks[episode.Id].Completed, "Failed close must not mark completed");
    handler.FailClose = false;
    await Cycle();
    var saved = StateFile.Load(plugin.StatePath);
    Check(saved.Users[first.Id].Tasks[episode.Id].Completed, "Retry completion");
    Check(!saved.Users[second.Id].Tasks[episode.Id].Completed, "Watching as first user must not complete second user's task");
    Check(handler.Archives == 1, "Archive the completed user's empty section");

    using (var service = NewService())
    {
        await service.StartAsync(ct);
        try
        {
            await service.StatusAsync(second.Id, ct);
            episode.RunTimeTicks = 10 * TimeSpan.TicksPerMinute;
            playbackStopped!(null, new PlaybackStopEventArgs
            {
                Item = episode, Users = new List<User> { second }, PlaybackPositionTicks = 9 * TimeSpan.TicksPerMinute
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!StateFile.Load(plugin.StatePath).Users[second.Id].Tasks[episode.Id].Completed)
            {
                await service.StatusAsync(second.Id, timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
            Check(StateFile.Load(plugin.StatePath).Users[second.Id].Tasks[episode.Id].CompletionRequested, "Playback within 60 seconds persists completion intent");
        }
        finally { await service.StopAsync(ct); }
    }
    Check(playbackStopped == null, "Unsubscribe playback event on shutdown");

    var previousEpisode = episode;
    episode = new Episode { Id = Guid.NewGuid(), Name = "Second", SeriesName = "Series", ParentIndexNumber = 1, IndexNumber = 2 };
    libraryItems[episode.Id] = episode;
    await Cycle(() => itemAdded!(null, new ItemChangeEventArgs { Item = episode }));
    Check(handler.Unarchives == 1, "Restore archived section when an unwatched episode arrives");
    Check(StateFile.Load(plugin.StatePath).Users[second.Id].Tasks.ContainsKey(episode.Id), "Create task in restored section");
    var failedEpisode = new Episode { Id = Guid.NewGuid(), Name = "Retry", SeriesName = "Series", IndexNumber = 3 };
    libraryItems[failedEpisode.Id] = failedEpisode;
    handler.FailCreate = true;
    await Cycle(() => itemAdded!(null, new ItemChangeEventArgs { Item = failedEpisode }));
    Check(StateFile.Load(plugin.StatePath).Users[second.Id].PendingItems.Contains(failedEpisode.Id), "Persist failed ItemAdded for retry");
    handler.FailCreate = false;
    await Cycle();
    Check(StateFile.Load(plugin.StatePath).Users[second.Id].Tasks.ContainsKey(failedEpisode.Id), "Retry queued additions after restart without scanning history");
    Check(!StateFile.Load(plugin.StatePath).Users[first.Id].Tasks.ContainsKey(episode.Id), "New ItemAdded already watched by a user must not create that user's task");
    episode = previousEpisode;

    var expiring = StateFile.Load(plugin.StatePath);
    expiring.Users[second.Id].ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
    expiring.Users[second.Id].RefreshToken = "second-refresh";
    StateFile.Save(plugin.StatePath, expiring);
    using (var service = NewService())
    {
        await service.ProjectsAsync(second.Id, ct);
        var refreshed = StateFile.Load(plugin.StatePath).Users[second.Id];
        Check(refreshed.RefreshToken == "rotated-refresh" && refreshed.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50), "Persist rotated refresh token and expiration");
        var calls = handler.TokenCalls;
        await service.ProjectsAsync(second.Id, ct);
        Check(handler.TokenCalls == calls, "Do not refresh a valid access token");
    }

    using (var service = NewService())
    {
        var status = JsonSerializer.Serialize(await service.StatusAsync(first.Id, ct));
        Check(!status.Contains("first-token") && !status.Contains("secret"), "Status must not expose secrets");
        var createsBeforeLink = handler.Creates;
        await service.SetProjectAsync(second.Id, "project-a", ct);
        await service.SetProjectAsync(second.Id, "project-b", ct);
        Check(StateFile.Load(plugin.StatePath).Users[second.Id].Tasks.ContainsKey(episode.Id), "Project switching preserves mappings");
        Check(handler.Creates == createsBeforeLink, "Linking or switching projects must not backfill history");
        await Throws<InvalidOperationException>(() => service.SetProjectAsync(first.Id, "foreign-project", ct), "Reject inaccessible projects");

        var url = await service.AuthorizeAsync(first.Id, "browser-proof", ct);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query)["state"].ToString();
        await Throws<InvalidOperationException>(() => service.CallbackAsync(state, "other-browser", "code", null, ct), "Reject OAuth browser mismatch");
        Check(await service.CallbackAsync(state, "browser-proof", "code", null, ct), "Complete OAuth");
        Check(StateFile.Load(plugin.StatePath).Users[first.Id].Tasks.ContainsKey(episode.Id), "Reauthorization preserves same-account mappings");
        await Throws<InvalidOperationException>(() => service.CallbackAsync(state, "browser-proof", "code", null, ct), "Reject OAuth state replay");
        var canceled = await service.AuthorizeAsync(first.Id, "browser-proof", ct);
        var canceledState = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(canceled).Query)["state"].ToString();
        Check(!await service.CallbackAsync(canceledState, "browser-proof", null, "access_denied", ct), "Handle authorization denial");

        var controller = new TodoistController(service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Check(await controller.Status(ct) is UnauthorizedResult, "Reject missing Jellyfin identity");
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("Jellyfin-UserId", first.Id.ToString()), new Claim("Jellyfin-IsApiKey", "true")
        }, "test"));
        Check(await controller.Status(ct) is UnauthorizedResult, "Reject global API keys for account binding");
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", second.Id.ToString()) }, "test"));
        Check(await controller.Status(ct) is OkObjectResult, "Allow authenticated user");
        await service.DisconnectAsync(first.Id, ct);
        Check(!StateFile.Load(plugin.StatePath).Users.ContainsKey(first.Id), "Disconnect removes credentials and mappings");
        Check(StateFile.Load(plugin.StatePath).Users.ContainsKey(second.Id), "Disconnect must preserve other users");
    }

    handler.Paginate = true;
    var projects = await api.ListAsync("token", "projects", ct);
    Check(projects.Count == 2 && handler.CursorSeen, "Follow and escape pagination cursor");
    handler.FailCommand = true;
    await Throws<HttpRequestException>(() => api.SectionCommandAsync("token", "section", true, ct), "Check sync command status even with HTTP 200");
    await File.WriteAllTextAsync(plugin.StatePath, "invalid json");
    await Throws<JsonException>(() => Task.FromResult(StateFile.Load(plugin.StatePath)), "Never overwrite corrupt state");
    Console.WriteLine($"PASS: {checks} checks (sync, persistence, isolation, OAuth, HTTP failures and pagination)");
}
finally { Directory.Delete(root, true); }

public class Stub<T> : DispatchProxy where T : class
{
    public Func<MethodInfo, object?[]?, object?> InvokeMethod { get; set; } = null!;
    public static T Create(Func<MethodInfo, object?[]?, object?> invoke)
    {
        var proxy = Create<T, Stub<T>>();
        ((Stub<T>)(object)proxy).InvokeMethod = invoke;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!, args);
}

public sealed class FakeHandler : HttpMessageHandler, IHttpClientFactory
{
    public int Creates, Archives, Unarchives, TokenCalls;
    public bool FailClose, FailCreate, FailCommand, Paginate, CursorSeen;
    public List<string> RequestIds { get; } = new();
    private readonly HashSet<string> _closed = new();
    private readonly HashSet<string> _archived = new();
    public HttpClient CreateClient(string name) => new(this, false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var token = request.Headers.Authorization?.Parameter ?? "";
        var sectionId = "section-" + token;
        object payload = new { };
        if (path.EndsWith("/access_token"))
        {
            TokenCalls++;
            var body = await request.Content!.ReadAsStringAsync(ct);
            payload = new { access_token = body.Contains("second-refresh") ? "second-token" : "first-token", refresh_token = "rotated-refresh", expires_in = 3600 };
        }
        else if (path.EndsWith("/user")) payload = new { id = "todoist-user" };
        else if (path.EndsWith("/projects"))
        {
            CursorSeen |= request.RequestUri.Query.Contains("cursor=next%2Bpage%2F");
            payload = Paginate
                ? new { results = new[] { new { id = "project", name = "Project" } }, next_cursor = CursorSeen ? null : "next+page/" }
                : (object)new { results = new[] { new { id = "project-a", name = "A" }, new { id = "project-b", name = "B" } }, next_cursor = (string?)null };
        }
        else if (path.EndsWith("/sync"))
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            if (body.TryGetProperty("commands", out var commands))
            {
                var command = commands[0];
                var uuid = command.GetProperty("uuid").GetString()!;
                if (command.GetProperty("type").GetString() == "section_archive") { Archives++; _archived.Add(sectionId); }
                else { Unarchives++; _archived.Remove(sectionId); }
                payload = new { sync_status = new Dictionary<string, object> { [uuid] = FailCommand ? new { error = "failure" } : "ok" } };
            }
            else payload = new { sections = new[] { new { id = sectionId, name = "Series", project_id = token == "first-token" ? "project-a" : "project-b", is_archived = _archived.Contains(sectionId) } } };
        }
        else if (path.EndsWith("/tasks") && request.Method == HttpMethod.Post)
        {
            if (FailCreate) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            Creates++; RequestIds.Add(request.Headers.GetValues("X-Request-Id").Single());
            _closed.Remove(token);
            payload = new { id = "task-" + token + Creates };
        }
        else if (path.EndsWith("/close"))
        {
            if (FailClose) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            _closed.Add(token);
        }
        else if (path.EndsWith("/tasks")) payload = new
        {
            results = _closed.Contains(token) ? Array.Empty<object>() : new object[] { new { id = "task-" + token, section_id = sectionId } }, next_cursor = (string?)null
        };
        else throw new Exception("Unexpected Todoist endpoint: " + path);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };
    }
}
