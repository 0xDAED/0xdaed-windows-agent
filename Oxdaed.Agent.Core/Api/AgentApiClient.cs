using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oxdaed.Agent.Api;

namespace Oxdaed.Agent.Api;

public sealed class AgentApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public AgentApiClient(string? apiKey)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<string> GetText(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        return body;
    }

    public async Task<TOut?> PostJson<TIn, TOut>(string url, TIn payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);

        Console.WriteLine(body);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"POST {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");

        if (typeof(TOut) == typeof(object) || string.IsNullOrWhiteSpace(body))
            return default;

        try
        {
            return JsonSerializer.Deserialize<TOut>(body, _json);
        }
        catch (Exception ex)
        {
            throw new Exception($"JSON parse failed for {typeof(TOut).Name}. Body: {body}", ex);
        }
    }

    public Task PostJson<TIn>(string url, TIn payload, CancellationToken ct)
        => PostJson<TIn, object>(url, payload, ct);

    public void Dispose() => _http.Dispose();

    public async Task<T?> GetJson<T>(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");

        if (string.IsNullOrWhiteSpace(body))
            return default;

        try { return JsonSerializer.Deserialize<T>(body, _json); }
        catch { return default; }
    }

    public async Task DownloadToFile(string url, string filePath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).Catch("");
            throw new HttpRequestException($"GET {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }

        await using var fs = File.Create(filePath);
        await resp.Content.CopyToAsync(fs, ct);
    }
}

static class TaskExt
{
    public static async Task<string> Catch(this Task<string> t, string fallback)
    {
        try { return await t; } catch { return fallback; }
    }
}