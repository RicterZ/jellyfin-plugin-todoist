using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jellyfin.Plugin.Todoist;

public sealed class TodoistApi(IHttpClientFactory clients)
{
    public const string BaseUrl = "https://api.todoist.com/api/v1/";

    public async Task<JsonElement> SendAsync(string token, HttpMethod method, string path, object? body,
        CancellationToken ct, string? requestId = null)
    {
        using var client = clients.CreateClient("Todoist");
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (requestId != null) request.Headers.Add("X-Request-Id", requestId);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        // Do not include response bodies or credentials in errors shown to users or logs.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Todoist returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    public async Task<List<JsonElement>> ListAsync(string token, string path, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await SendAsync(token, HttpMethod.Get, path + (cursor == null ? "" :
                (path.Contains('?') ? "&" : "?") + "cursor=" + Uri.EscapeDataString(cursor)), null, ct);
            result.AddRange(page.GetProperty("results").EnumerateArray());
            cursor = page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor));
        return result;
    }

    public async Task<JsonElement> TokenAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        using var client = clients.CreateClient("Todoist");
        using var response = await client.PostAsync("https://todoist.com/oauth/access_token", new FormUrlEncodedContent(fields), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Todoist authorization failed. Please authorize again.", null, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<List<JsonElement>> SectionsAsync(string token, string project, CancellationToken ct)
    {
        var response = await SendAsync(token, HttpMethod.Post, "sync", new
        {
            sync_token = "*",
            resource_types = new[] { "sections" }
        }, ct);
        return response.GetProperty("sections").EnumerateArray()
            .Where(s => Id(s, "project_id") == project && !Flag(s, "is_deleted")).ToList();
    }

    public async Task SectionCommandAsync(string token, string id, bool archive, CancellationToken ct)
    {
        var uuid = Guid.NewGuid().ToString();
        var response = await SendAsync(token, HttpMethod.Post, "sync", new
        {
            sync_token = "*",
            resource_types = new[] { "sections" },
            commands = new[] { new { type = archive ? "section_archive" : "section_unarchive", uuid, args = new { id } } }
        }, ct);
        var status = response.GetProperty("sync_status").GetProperty(uuid);
        if (status.ValueKind != JsonValueKind.String || status.GetString() != "ok")
            throw new HttpRequestException("Todoist section update failed.");
    }

    public static string Id(JsonElement element, string property = "id") => element.GetProperty(property).ToString();
    public static bool Flag(JsonElement element, string property) => element.TryGetProperty(property, out var value)
        && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Number && value.GetInt32() == 1));
}
