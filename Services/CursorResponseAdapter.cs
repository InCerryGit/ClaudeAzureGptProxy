using System.Text;
using System.Text.Json;

namespace ClaudeAzureGptProxy.Services;

public sealed class CursorResponseAdapter
{
    private readonly string _inboundModel;
    private bool _sentRole;
    private bool _thinkOpen;
    private int _created;
    private readonly Dictionary<string, string> _toolItemIdToCallId = new(StringComparer.Ordinal);

    public CursorResponseAdapter(string inboundModel)
    {
        _inboundModel = inboundModel;
        _created = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public IEnumerable<string> ConvertAzureSseDataToOpenAiSse(string azureData)
    {
        if (string.IsNullOrWhiteSpace(azureData))
        {
            yield break;
        }

        // Azure may send [DONE] too; if so just pass through as OpenAI done.
        if (string.Equals(azureData.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            yield return OpenAiSseEncoder.Done();
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(azureData);
        }
        catch
        {
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, "response.output_item.added", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in HandleOutputItemAdded(root))
                {
                    yield return s;
                }
                yield break;
            }

            if (string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "response.reasoning.summary_text.delta", StringComparison.OrdinalIgnoreCase))
            {
                var delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                foreach (var s in EmitContentDelta(delta))
                {
                    yield return s;
                }
                yield break;
            }

            if (string.Equals(type, "response.function_call.arguments.delta", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in HandleFunctionCallArgumentsDelta(root))
                {
                    yield return s;
                }
                yield break;
            }

            if (string.Equals(type, "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in HandleCompleted(root))
                {
                    yield return s;
                }
                yield break;
            }
        }
    }

    private IEnumerable<string> HandleOutputItemAdded(JsonElement root)
    {
        EnsureRoleSent();

        // Path (common): root.item.type == "reasoning" | "output_text" | "function_call"
        if (!root.TryGetProperty("item", out var item))
        {
            yield break;
        }

        var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;

        if (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            if (!_thinkOpen)
            {
                _thinkOpen = true;
                yield return BuildChunk(content: "<think>\n\n", toolCalls: null, finishReason: null);
            }
            yield break;
        }

        if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            // OpenAI streaming tool_calls begins with an item carrying id/name and empty arguments.
            var callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() : null;
            var itemId = item.TryGetProperty("id", out var iid) ? iid.GetString() : null;
            var name = item.TryGetProperty("name", out var nm) ? nm.GetString() : null;

            if (string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(itemId))
            {
                callId = itemId;
            }

            if (!string.IsNullOrWhiteSpace(itemId) && !string.IsNullOrWhiteSpace(callId))
            {
                _toolItemIdToCallId[itemId] = callId;
            }

            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name))
            {
                yield break;
            }

            foreach (var s in CloseThinkIfNeeded())
            {
                yield return s;
            }

            var toolCalls = new[]
            {
                new
                {
                    index = 0,
                    id = callId,
                    type = "function",
                    function = new { name, arguments = string.Empty }
                }
            };

            yield return BuildChunk(content: null, toolCalls: toolCalls, finishReason: null);
            yield break;
        }

        if (string.Equals(itemType, "output_text", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var s in CloseThinkIfNeeded())
            {
                yield return s;
            }
            yield break;
        }
    }

    private IEnumerable<string> HandleFunctionCallArgumentsDelta(JsonElement root)
    {
        EnsureRoleSent();

        var callId = root.TryGetProperty("call_id", out var cid) ? cid.GetString() : null;
        if (string.IsNullOrWhiteSpace(callId) && root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            callId = item.TryGetProperty("call_id", out var itemCallId) ? itemCallId.GetString() : null;
        }

        var itemId = root.TryGetProperty("item_id", out var itemIdProp) ? itemIdProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(itemId))
        {
            itemId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(itemId) && root.TryGetProperty("item", out var rootItem) && rootItem.ValueKind == JsonValueKind.Object)
        {
            itemId = rootItem.TryGetProperty("id", out var itemIdProp2) ? itemIdProp2.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(itemId) && _toolItemIdToCallId.TryGetValue(itemId, out var mappedCallId))
        {
            callId = mappedCallId;
        }

        if (string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(itemId))
        {
            callId = itemId;
        }

        var delta = string.Empty;
        if (root.TryGetProperty("delta", out var d))
        {
            delta = d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : d.GetRawText();
        }
        else if (root.TryGetProperty("arguments", out var argumentsProp))
        {
            delta = argumentsProp.ValueKind == JsonValueKind.String ? argumentsProp.GetString() ?? string.Empty : argumentsProp.GetRawText();
        }
        else if (root.TryGetProperty("arguments_delta", out var argumentsDeltaProp))
        {
            delta = argumentsDeltaProp.ValueKind == JsonValueKind.String ? argumentsDeltaProp.GetString() ?? string.Empty : argumentsDeltaProp.GetRawText();
        }
        else if (root.TryGetProperty("item", out var itemProp) && itemProp.ValueKind == JsonValueKind.Object)
        {
            if (itemProp.TryGetProperty("delta", out var itemDeltaProp))
            {
                delta = itemDeltaProp.ValueKind == JsonValueKind.String ? itemDeltaProp.GetString() ?? string.Empty : itemDeltaProp.GetRawText();
            }
            else if (itemProp.TryGetProperty("arguments", out var itemArgumentsProp))
            {
                delta = itemArgumentsProp.ValueKind == JsonValueKind.String ? itemArgumentsProp.GetString() ?? string.Empty : itemArgumentsProp.GetRawText();
            }
        }

        if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrEmpty(delta))
        {
            yield break;
        }

        var toolCalls = new[]
        {
            new
            {
                index = 0,
                id = callId,
                type = "function",
                function = new { name = (string?)null, arguments = delta }
            }
        };

        yield return BuildChunk(content: null, toolCalls: toolCalls, finishReason: null);
    }

    private IEnumerable<string> EmitContentDelta(string text)
    {
        EnsureRoleSent();

        foreach (var s in CloseThinkIfNeeded())
        {
            yield return s;
        }

        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        yield return BuildChunk(content: text, toolCalls: null, finishReason: null);
    }

    private IEnumerable<string> HandleCompleted(JsonElement root)
    {
        foreach (var s in CloseThinkIfNeeded())
        {
            yield return s;
        }

        // Try to match python finish_reason decision:
        // if any function_call outputs exist -> tool_calls else stop.
        var finish = "stop";

        if (root.TryGetProperty("response", out var response) &&
            response.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    finish = "tool_calls";
                    break;
                }
            }
        }

        // Send a final chunk with finish_reason to match typical chat.completions stream.
        yield return BuildChunk(content: null, toolCalls: null, finishReason: finish);
        yield return OpenAiSseEncoder.Done();
    }

    private void EnsureRoleSent()
    {
        if (_sentRole)
        {
            return;
        }

        _sentRole = true;
        // Role chunk is emitted via BuildChunk with delta.role
        // Caller will emit it when first event arrives.
    }

    private IEnumerable<string> CloseThinkIfNeeded()
    {
        if (_thinkOpen)
        {
            _thinkOpen = false;
            yield return BuildChunk(content: "</think>\n\n", toolCalls: null, finishReason: null);
        }
    }

    private string BuildChunk(string? content, object? toolCalls, string? finishReason)
    {
        // Build per python: {id, object:"chat.completion.chunk", created, model, choices:[{index, delta, finish_reason}]}
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", "chatcmpl-" + Guid.NewGuid().ToString("N"));
            writer.WriteString("object", "chat.completion.chunk");
            writer.WriteNumber("created", _created);
            writer.WriteString("model", _inboundModel);

            writer.WritePropertyName("choices");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);

            writer.WritePropertyName("delta");
            writer.WriteStartObject();

            if (!_sentRole)
            {
                writer.WriteString("role", "assistant");
                _sentRole = true;
            }

            if (content is not null)
            {
                writer.WriteString("content", content);
            }

            if (toolCalls is not null)
            {
                writer.WritePropertyName("tool_calls");
                JsonSerializer.Serialize(writer, toolCalls);
            }

            writer.WriteEndObject();

            if (finishReason is not null)
            {
                writer.WriteString("finish_reason", finishReason);
            }
            else
            {
                writer.WriteNull("finish_reason");
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        return OpenAiSseEncoder.EncodeJson(doc.RootElement);
    }
}
