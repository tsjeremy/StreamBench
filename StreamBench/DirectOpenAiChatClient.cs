#if ENABLE_AI
// DirectOpenAiChatClient.cs
// A lightweight IChatClient implementation that calls OpenAI-compatible chat
// completion endpoints directly via HttpClient, bypassing the OpenAI SDK.
//
// Motivation: The OpenAI .NET SDK (v2.x) fails to parse responses from local
// backends (LM Studio, Ollama, etc.) that include "tool_calls": [] — the SDK's
// ChangeTrackingList throws ArgumentOutOfRangeException on empty choices.
// This direct implementation is immune to such deserialization quirks.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace StreamBench;

/// <summary>
/// Direct IChatClient that calls an OpenAI-compatible /v1/chat/completions endpoint
/// via HttpClient. Works with Foundry Local, LM Studio, Ollama, and any compatible server.
/// </summary>
internal sealed class DirectOpenAiChatClient : IChatClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _defaultModel;
    private readonly string _baseUrl;

    public DirectOpenAiChatClient(string baseUrl, string model)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _defaultModel = model;
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromMinutes(10) };
    }

    public ChatClientMetadata Metadata => new("DirectOpenAI", new Uri(_baseUrl));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<object>();
        foreach (var msg in chatMessages)
            messages.Add(new { role = msg.Role.Value, content = msg.Text });

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = options?.ModelId ?? _defaultModel,
            ["messages"] = messages,
            ["temperature"] = options?.Temperature ?? 0.7f,
        };

        if (options?.MaxOutputTokens is int maxTokens)
            requestBody["max_tokens"] = maxTokens;

        var resp = await _http.PostAsJsonAsync("/v1/chat/completions", requestBody, cancellationToken);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // Parse choices[0].message.content
        var choices = json.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new InvalidOperationException("No choices in completion response");

        var firstChoice = choices[0];
        var messageObj = firstChoice.GetProperty("message");
        string content = messageObj.TryGetProperty("content", out var contentProp)
            ? contentProp.GetString() ?? ""
            : "";

        // Parse usage (optional — some backends omit it)
        UsageDetails? usage = null;
        if (json.TryGetProperty("usage", out var usageObj))
        {
            int inputTokens = usageObj.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            int outputTokens = usageObj.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
            int totalTokens = usageObj.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : 0;
            usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = totalTokens,
            };
        }

        // Parse model ID from response (may differ from request)
        string? responseModel = json.TryGetProperty("model", out var modelProp)
            ? modelProp.GetString()
            : null;

        var responseMessage = new ChatMessage(ChatRole.Assistant, content);
        return new ChatResponse(responseMessage)
        {
            ModelId = responseModel ?? _defaultModel,
            Usage = usage,
        };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming is not used in benchmarks");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
#endif
