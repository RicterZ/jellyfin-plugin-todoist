using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Todoist;

public sealed class TodoistService : BackgroundService
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ISessionManager _sessions;
    private readonly TodoistApi _api;
    private readonly ILogger<TodoistService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly ConcurrentQueue<(Guid UserId, Guid ItemId)> _completions = new();
    private readonly Dictionary<string, PendingAuthorization> _pending = new();
    private readonly State _state;
    private record PendingAuthorization(Guid UserId, DateTimeOffset Expires, string BrowserProof);

    public TodoistService(ILibraryManager library, IUserManager users, IUserDataManager userData,
        ISessionManager sessions, TodoistApi api, ILogger<TodoistService> logger)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _sessions = sessions;
        _api = api;
        _logger = logger;
        // A corrupt state must stop startup rather than silently creating duplicate tasks.
        _state = StateFile.Load(Plugin.Instance.StatePath);
    }

    private void Save() => StateFile.Save(Plugin.Instance.StatePath, _state);
    public void RequestSync() { if (_signal.CurrentCount == 0) { try { _signal.Release(); } catch (SemaphoreFullException) { } } }

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    public Task<object> StatusAsync(Guid user, CancellationToken ct) => Locked<object>(() =>
    {
        _state.Users.TryGetValue(user, out var binding);
        return Task.FromResult<object>(new
        {
            Connected = binding != null,
            ProjectId = binding?.ProjectId ?? "",
            binding?.LastSync,
            binding?.LastError,
            TrackedTasks = binding?.Tasks.Count ?? 0
        });
    }, ct);

    public Task<object[]> ConnectionsAsync(CancellationToken ct) => Locked(() => Task.FromResult(
        _state.Users.Select(pair => (object)new
        {
            UserId = pair.Key,
            UserName = _users.GetUserById(pair.Key)?.Username ?? "Deleted user",
            pair.Value.ProjectId,
            TrackedTasks = pair.Value.Tasks.Count,
            pair.Value.LastSync,
            pair.Value.LastError
        }).ToArray()), ct);

    public Task<string> AuthorizeAsync(Guid user, string browserProof, CancellationToken ct) => Locked(() =>
    {
        var config = Plugin.Instance.Configuration;
        if (string.IsNullOrWhiteSpace(config.ClientId) || string.IsNullOrWhiteSpace(config.ClientSecret)
            || !Uri.TryCreate(config.RedirectUri, UriKind.Absolute, out var redirect)
            || (redirect.Scheme != "https" && !(redirect.Scheme == "http" && redirect.IsLoopback)))
            throw new InvalidOperationException("Ask the administrator to configure Todoist OAuth and an HTTPS callback URL.");
        foreach (var key in _pending.Where(p => p.Value.Expires < DateTimeOffset.UtcNow || p.Value.UserId == user).Select(p => p.Key).ToArray())
            _pending.Remove(key);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _pending[state] = new(user, DateTimeOffset.UtcNow.AddMinutes(10), browserProof);
        return Task.FromResult("https://todoist.com/oauth/authorize?client_id=" + Uri.EscapeDataString(config.ClientId)
            + "&scope=data:read_write&state=" + state + "&redirect_uri=" + Uri.EscapeDataString(config.RedirectUri));
    }, ct);

    public Task<bool> CallbackAsync(string state, string browserProof, string? code, string? error, CancellationToken ct) => Locked(async () =>
    {
        if (!_pending.TryGetValue(state, out var pending) || pending.Expires < DateTimeOffset.UtcNow
            || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(pending.BrowserProof),
                System.Text.Encoding.UTF8.GetBytes(browserProof)))
            throw new InvalidOperationException("Authorization expired or belongs to another browser. Please try again.");
        _pending.Remove(state);
        if (!string.IsNullOrEmpty(error) || string.IsNullOrWhiteSpace(code)) return false;
        var token = await _api.TokenAsync(Credentials(new() { ["code"] = code }), ct);
        var account = await _api.SendAsync(token.GetProperty("access_token").GetString()!, HttpMethod.Get, "user", null, ct);
        var accountId = TodoistApi.Id(account);
        // Keep mappings on reauthorization, but never carry them into another Todoist account.
        var binding = _state.Users.TryGetValue(pending.UserId, out var existing) && existing.TodoistUserId == accountId
            ? existing : new Binding { TodoistUserId = accountId };
        UpdateToken(binding, token);
        _state.Users[pending.UserId] = binding;
        Save();
        return true;
    }, ct);

    private static Dictionary<string, string> Credentials(Dictionary<string, string> fields)
    {
        fields["client_id"] = Plugin.Instance.Configuration.ClientId;
        fields["client_secret"] = Plugin.Instance.Configuration.ClientSecret;
        return fields;
    }

    private static void UpdateToken(Binding binding, JsonElement token)
    {
        var access = token.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidOperationException("Todoist returned an empty access token.");
        binding.AccessToken = access;
        if (token.TryGetProperty("refresh_token", out var refresh)) binding.RefreshToken = refresh.GetString() ?? "";
        binding.ExpiresAt = token.TryGetProperty("expires_in", out var expires)
            ? DateTimeOffset.UtcNow.AddSeconds(expires.GetInt32()) : null;
    }

    private async Task RefreshAsync(Binding binding, CancellationToken ct)
    {
        if (binding.ExpiresAt == null || binding.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return;
        if (string.IsNullOrEmpty(binding.RefreshToken)) throw new InvalidOperationException("Todoist authorization expired. Please authorize again.");
        UpdateToken(binding, await _api.TokenAsync(Credentials(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = binding.RefreshToken
        }), ct));
        Save();
    }

    private Binding GetBinding(Guid user) => _state.Users.TryGetValue(user, out var binding)
        ? binding : throw new InvalidOperationException("Authorize Todoist first.");

    public Task<object[]> ProjectsAsync(Guid user, CancellationToken ct) => Locked(async () =>
    {
        var binding = GetBinding(user);
        await RefreshAsync(binding, ct);
        return (await _api.ListAsync(binding.AccessToken, "projects", ct))
            .Where(p => !TodoistApi.Flag(p, "is_archived"))
            .Select(p => (object)new { Id = TodoistApi.Id(p), Name = p.GetProperty("name").GetString() }).ToArray();
    }, ct);

    public Task<bool> SetProjectAsync(Guid user, string projectId, CancellationToken ct) => Locked(async () =>
    {
        var binding = GetBinding(user);
        await RefreshAsync(binding, ct);
        var projects = await _api.ListAsync(binding.AccessToken, "projects", ct);
        if (!projects.Any(p => TodoistApi.Id(p) == projectId && !TodoistApi.Flag(p, "is_archived")))
            throw new InvalidOperationException("Select an accessible, active Todoist project.");
        if (binding.ProjectId != projectId)
        {
            binding.ProjectId = projectId;
        }
        binding.LastError = null;
        Save();
        RequestSync();
        return true;
    }, ct);

    public Task<bool> DisconnectAsync(Guid user, CancellationToken ct) => Locked(() =>
    {
        _state.Users.Remove(user);
        foreach (var key in _pending.Where(p => p.Value.UserId == user).Select(p => p.Key).ToArray()) _pending.Remove(key);
        Save();
        return Task.FromResult(true);
    }, ct);

    public override Task StartAsync(CancellationToken ct)
    {
        _library.ItemAdded += OnItemAdded;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _userData.UserDataSaved += OnUserDataSaved;
        return base.StartAsync(ct);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _library.ItemAdded -= OnItemAdded;
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        _userData.UserDataSaved -= OnUserDataSaved;
        await base.StopAsync(ct);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) { if (e.Item is Episode) RequestSync(); }
    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.Item is Episode && e.UserData.Played && e.SaveReason != UserDataSaveReason.PlaybackProgress)
        {
            _completions.Enqueue((e.UserId, e.Item.Id));
            RequestSync();
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is not Episode || !EpisodeRules.IsComplete(e.Item.RunTimeTicks, e.PlaybackPositionTicks, e.PlayedToCompletion)) return;
        foreach (var user in e.Users) _completions.Enqueue((user.Id, e.Item.Id));
        RequestSync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Locked(async () =>
                {
                    while (_completions.TryDequeue(out var completion))
                    {
                        if (_state.Users.TryGetValue(completion.UserId, out var binding)
                            && binding.Tasks.TryGetValue(completion.ItemId, out var mapping))
                            mapping.CompletionRequested = true;
                    }
                    Save();
                    foreach (var (user, binding) in _state.Users)
                    {
                        if (string.IsNullOrEmpty(binding.ProjectId)) continue;
                        try
                        {
                            await SyncAsync(user, binding, stoppingToken);
                            binding.LastError = null;
                            binding.LastSync = DateTimeOffset.UtcNow;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                        {
                            binding.LastError = ex is HttpRequestException ? ex.Message : "Synchronization failed. Check the Jellyfin server log.";
                            _logger.LogWarning("Todoist sync failed for user {UserId}: {ErrorType}", user, ex.GetType().Name);
                        }
                        Save();
                    }
                    return true;
                }, stoppingToken);
                await _signal.WaitAsync(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SyncAsync(Guid userId, Binding binding, CancellationToken ct)
    {
        var user = _users.GetUserById(userId);
        if (user == null) return;
        await RefreshAsync(binding, ct);
        var token = binding.AccessToken;
        var sections = await _api.SectionsAsync(token, binding.ProjectId, ct);
        var touchedSections = new HashSet<string>();
        // User-scoped queries preserve library access and parental restrictions.
        var episodes = _library.GetItemList(new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode }
        });
        foreach (var episode in episodes.OfType<Episode>())
        {
            ct.ThrowIfCancellationRequested();
            var played = _userData.GetUserData(user, episode)?.Played == true;
            if (!binding.Tasks.TryGetValue(episode.Id, out var mapping))
            {
                if (played) continue;
                var series = string.IsNullOrWhiteSpace(episode.SeriesName) ? episode.Name : episode.SeriesName;
                var section = sections.FirstOrDefault(s => s.GetProperty("name").GetString() == series);
                if (section.ValueKind == JsonValueKind.Undefined)
                {
                    section = await _api.SendAsync(token, HttpMethod.Post, "sections", new { project_id = binding.ProjectId, name = series }, ct,
                        EpisodeRules.RequestId($"section:{userId}:{binding.ProjectId}:{series}"));
                    sections.Add(section);
                }
                if (TodoistApi.Flag(section, "is_archived"))
                {
                    await _api.SectionCommandAsync(token, TodoistApi.Id(section), false, ct);
                    sections.Remove(section);
                    // Reflect the unarchive locally to avoid repeating the command for every episode.
                    section = JsonSerializer.SerializeToElement(new { id = TodoistApi.Id(section), name = series, is_archived = false });
                    sections.Add(section);
                }
                var task = await _api.SendAsync(token, HttpMethod.Post, "tasks", new
                {
                    project_id = binding.ProjectId,
                    section_id = TodoistApi.Id(section),
                    content = EpisodeRules.Title(series, episode.Name, episode.ParentIndexNumber, episode.IndexNumber),
                    due_string = "today"
                }, ct, EpisodeRules.RequestId($"task:{userId}:{binding.ProjectId}:{episode.Id}"));
                mapping = new TaskMapping { TaskId = TodoistApi.Id(task), SectionId = TodoistApi.Id(section) };
                binding.Tasks[episode.Id] = mapping;
                Save();
            }
            touchedSections.Add(mapping.SectionId);
            if (!mapping.Completed && (played || mapping.CompletionRequested))
            {
                await _api.SendAsync(token, HttpMethod.Post, "tasks/" + Uri.EscapeDataString(mapping.TaskId) + "/close", null, ct,
                    EpisodeRules.RequestId($"close:{mapping.TaskId}"));
                mapping.Completed = true;
                Save();
            }
        }
        // Archive only sections this plugin tracks, never unrelated empty sections.
        var tasks = await _api.ListAsync(token, "tasks?project_id=" + Uri.EscapeDataString(binding.ProjectId), ct);
        foreach (var section in sections.Where(s => touchedSections.Contains(TodoistApi.Id(s)) && !TodoistApi.Flag(s, "is_archived")))
        {
            var id = TodoistApi.Id(section);
            if (!tasks.Any(t => TodoistApi.Id(t, "section_id") == id))
                await _api.SectionCommandAsync(token, id, true, ct);
        }
    }
}
