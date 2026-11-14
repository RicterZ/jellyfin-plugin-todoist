using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Todoist;

public sealed class State
{
    public Dictionary<Guid, Binding> Users { get; set; } = new();
}

public sealed class Binding
{
    public string TodoistUserId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public string ProjectId { get; set; } = "";
    public Dictionary<string, Dictionary<Guid, TaskMapping>> ProjectTasks { get; set; } = new();
    public Dictionary<string, HashSet<Guid>> PendingAdditions { get; set; } = new();
    [JsonIgnore]
    public HashSet<Guid> PendingItems
    {
        get
        {
            if (!PendingAdditions.TryGetValue(ProjectId, out var items)) PendingAdditions[ProjectId] = items = new();
            return items;
        }
    }
    [JsonIgnore]
    public Dictionary<Guid, TaskMapping> Tasks
    {
        get
        {
            if (!ProjectTasks.TryGetValue(ProjectId, out var tasks)) ProjectTasks[ProjectId] = tasks = new();
            return tasks;
        }
    }
    public string? LastError { get; set; }
    public DateTimeOffset? LastSync { get; set; }
}

public sealed class TaskMapping
{
    public string TaskId { get; set; } = "";
    public string SectionId { get; set; } = "";
    public bool CompletionRequested { get; set; }
    public bool Completed { get; set; }
}

public static class StateFile
{
    public static State Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty Todoist state")
        : new State();

    public static void Save(string path, State state)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}

public static class EpisodeRules
{
    public static bool IsComplete(long? runtime, long? position, bool playedToCompletion) =>
        playedToCompletion || (runtime > 0 && position >= 0 && Math.Abs(runtime.Value - position.Value) <= TimeSpan.TicksPerMinute);

    public static string Title(string series, string name, int? season, int? episode) => episode.HasValue
        ? $"{series} {(season.HasValue ? $"S{season:00}" : "")}E{episode:00}".Trim()
        : name;

    public static string RequestId(string key) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16)).ToString();
}
