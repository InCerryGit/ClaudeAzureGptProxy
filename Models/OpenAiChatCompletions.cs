using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeAzureGptProxy.Models;

public sealed class OpenAiChatCompletionsRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OpenAiChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("tools")]
    public List<OpenAiTool>? Tools { get; set; }

    // Can be string ("auto"/"none"/...) or object ({"type":"function","function":{"name":"..."}})
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }
}

public sealed class OpenAiChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    // Cursor/OpenAI inbound may be string or structured array; keep as raw JSON and adapt later.
    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenAiToolCall>? ToolCalls { get; set; }

    // role=tool
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

public sealed class OpenAiTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionTool Function { get; set; } = new();
}

public sealed class OpenAiFunctionTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

public sealed class OpenAiToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiToolCallFunction Function { get; set; } = new();
}

public sealed class OpenAiToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // OpenAI chat.completions uses stringified JSON for arguments (streaming deltas are string fragments)
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}
