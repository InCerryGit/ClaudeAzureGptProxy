using System.Text.Json;
using ClaudeAzureGptProxy.Models;
using Microsoft.Extensions.Logging;
using SharpToken;

namespace ClaudeAzureGptProxy.Services;

public sealed class TokenCounter
{
    private const string ImagePlaceholder = "[Image content - not displayed in text format]";
    private const string DocumentPlaceholder = "[Document content - not displayed in text format]";
    private const string ThinkingPlaceholder = "[thinking enabled]";
    private const string FallbackEncodingName = "cl100k_base";
    private const string LargeEncodingName = "o200k_base";

    // Prevent huge payloads (e.g. base64 documents/images) from exploding memory/time.
    private const int MaxSerializedCharsForCounting = 32_000;

    private readonly ILogger<TokenCounter> _logger;

    public TokenCounter(ILogger<TokenCounter> logger)
    {
        _logger = logger;
    }

    public int CountInputTokens(TokenCountRequest request)
    {
        var encodingName = SelectEncodingName(request.ResolvedAzureModel ?? request.Model);
        var encoding = GetEncoding(encodingName, out var usedFallback);

        if (usedFallback)
        {
            _logger.LogWarning("Tokenizer encoding {RequestedEncoding} unavailable; using {FallbackEncoding}",
                encodingName, FallbackEncodingName);
        }

        var total = 0;
        total += CountSystemTokens(request.System, encoding);
        total += CountMessagesTokens(request.Messages, encoding);
        total += CountToolsTokens(request.Tools, encoding);
        total += CountToolChoiceTokens(request.ToolChoice, encoding);
        total += CountThinkingTokens(request.Thinking, encoding);

        _logger.LogInformation("Counted {TokenCount} input tokens for model {Model} using {Encoding}",
            total, request.Model, usedFallback ? FallbackEncodingName : encodingName);

        return total;
    }

    private static string SelectEncodingName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return FallbackEncodingName;
        }

        var normalized = model.ToLowerInvariant();
        if (normalized.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("gpt-4.1", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("o3", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("gpt-5", StringComparison.OrdinalIgnoreCase))
        {
            return LargeEncodingName;
        }

        return FallbackEncodingName;
    }

    private static GptEncoding GetEncoding(string encodingName, out bool usedFallback)
    {
        try
        {
            usedFallback = false;
            return GptEncoding.GetEncoding(encodingName);
        }
        catch (Exception)
        {
            usedFallback = true;
            return GptEncoding.GetEncoding(FallbackEncodingName);
        }
    }

    private static int CountSystemTokens(object? systemBlock, GptEncoding encoding)
    {
        var systemText = ExtractTextFromSystem(systemBlock);
        if (string.IsNullOrWhiteSpace(systemText))
        {
            return 0;
        }

        return encoding.Encode(systemText).Count;
    }

    private static int CountMessagesTokens(IEnumerable<Message> messages, GptEncoding encoding)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += CountMessageTokens(message, encoding);
        }

        return total;
    }

    private static int CountMessageTokens(Message message, GptEncoding encoding)
    {
        if (message.Content is string textContent)
        {
            return encoding.Encode(textContent).Count;
        }

        // When binding JSON to `object`, ASP.NET may materialize content as JsonElement.
        if (message.Content is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => encoding.Encode(element.GetString() ?? string.Empty).Count,
                JsonValueKind.Array => CountContentBlocks(EnumerateJsonArray(element), encoding),
                JsonValueKind.Object => CountContentBlocks(new object[] { element }, encoding),
                _ => 0
            };
        }

        if (message.Content is IEnumerable<object> list)
        {
            return CountContentBlocks(list, encoding);
        }

        return 0;
    }

    private static int CountContentBlocks(IEnumerable<object> contentBlocks, GptEncoding encoding)
    {
        var total = 0;
        foreach (var block in contentBlocks)
        {
            string? blockType = null;
            object? blockObj = block;

            if (block is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("type", out var typeProp))
                {
                    blockType = typeProp.GetString();
                }
                blockObj = element;
            }
            else if (block is IDictionary<string, object?> dict)
            {
                blockType = dict.TryGetValue("type", out var typeObj) ? typeObj?.ToString() : null;
            }

            switch (blockType)
            {
                case "text":
                {
                    var textValue = ExtractTextValue(blockObj);
                    if (!string.IsNullOrWhiteSpace(textValue))
                    {
                        total += encoding.Encode(textValue).Count;
                    }
                    break;
                }
                case "image":
                {
                    // Anthropic counts images; we can only approximate locally.
                    total += encoding.Encode(ImagePlaceholder).Count;
                    total += EstimateBinaryBlockTokens(blockObj, encoding);
                    break;
                }
                case "document":
                {
                    // Documents can be text, content blocks, or PDFs; approximate conservatively.
                    total += encoding.Encode(DocumentPlaceholder).Count;
                    total += EstimateDocumentTokens(blockObj, encoding);
                    break;
                }
                case "search_result":
                {
                    total += CountSearchResultTokens(blockObj, encoding);
                    break;
                }
                case "thinking":
                case "redacted_thinking":
                {
                    total += encoding.Encode(ThinkingPlaceholder).Count;
                    total += CountJsonFallback(blockObj, encoding);
                    break;
                }
                case "tool_use":
                {
                    var toolName = ExtractString(blockObj, "name") ?? string.Empty;
                    var toolInput = GetField(blockObj, "input");
                    var toolPayload = new Dictionary<string, object?>
                    {
                        ["name"] = toolName,
                        ["input"] = toolInput
                    };
                    total += CountSerializedTokens(toolPayload, encoding);
                    break;
                }
                case "tool_result":
                {
                    var toolUseId = ExtractString(blockObj, "tool_use_id") ?? string.Empty;
                    var resultContent = GetField(blockObj, "content");
                    var isError = ExtractBool(blockObj, "is_error");
                    var payload = new Dictionary<string, object?>
                    {
                        ["tool_use_id"] = toolUseId,
                        ["content"] = resultContent,
                        ["is_error"] = isError
                    };
                    total += CountSerializedTokens(payload, encoding);
                    break;
                }
                default:
                {
                    // Don't silently drop unknown block types (citations/cache_control/etc.).
                    total += CountJsonFallback(blockObj, encoding);
                    break;
                }
            }
        }

        return total;
    }

    private static int CountToolsTokens(IEnumerable<Tool>? tools, GptEncoding encoding)
    {
        if (tools is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var tool in tools)
        {
            var payload = new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description ?? string.Empty,
                ["input_schema"] = tool.InputSchema
            };
            var json = JsonSerializer.Serialize(payload);
            total += encoding.Encode(json).Count;
        }

        return total;
    }

    private static int CountToolChoiceTokens(ToolChoice? toolChoice, GptEncoding encoding)
    {
        if (toolChoice is null)
        {
            return 0;
        }

        // Count the structured object rather than a single keyword to reduce undercount.
        var payload = new Dictionary<string, object?>
        {
            ["type"] = toolChoice.Type,
            ["name"] = toolChoice.Name,
            ["disable_parallel_tool_use"] = toolChoice.DisableParallelToolUse
        };

        return CountSerializedTokens(payload, encoding);
    }

    private static int CountThinkingTokens(ThinkingConfig? thinking, GptEncoding encoding)
    {
        if (thinking is null)
        {
            return 0;
        }

        if (!thinking.IsEnabled())
        {
            return CountSerializedTokens(new { type = thinking.Type ?? "disabled" }, encoding);
        }

        var payload = new Dictionary<string, object?>
        {
            ["type"] = thinking.Type ?? "enabled",
            ["budget_tokens"] = thinking.BudgetTokens,
            ["enabled"] = thinking.Enabled
        };

        return encoding.Encode(ThinkingPlaceholder).Count + CountSerializedTokens(payload, encoding);
    }

    private static int CountSerializedTokens(object payload, GptEncoding encoding)
    {
        var json = JsonSerializer.Serialize(payload);
        if (json.Length > MaxSerializedCharsForCounting)
        {
            json = json[..MaxSerializedCharsForCounting] + "…";
        }

        return encoding.Encode(json).Count;
    }

    private static int CountJsonFallback(object? blockObj, GptEncoding encoding)
    {
        if (blockObj is null)
        {
            return 0;
        }

        try
        {
            if (blockObj is JsonElement element)
            {
                var raw = element.GetRawText();
                if (raw.Length > MaxSerializedCharsForCounting)
                {
                    raw = raw[..MaxSerializedCharsForCounting] + "…";
                }

                return encoding.Encode(raw).Count;
            }

            return CountSerializedTokens(blockObj, encoding);
        }
        catch
        {
            var text = blockObj.ToString() ?? string.Empty;
            if (text.Length > MaxSerializedCharsForCounting)
            {
                text = text[..MaxSerializedCharsForCounting] + "…";
            }

            return encoding.Encode(text).Count;
        }
    }

    private static int EstimateBinaryBlockTokens(object? blockObj, GptEncoding encoding)
    {
        var sourceObj = GetField(blockObj, "source");
        var data = ExtractString(sourceObj, "data");
        if (!string.IsNullOrWhiteSpace(data))
        {
            // Roughly scale with base64 size, but cap to keep it bounded.
            return Math.Min(2000, Math.Max(0, data.Length / 256));
        }

        var url = ExtractString(sourceObj, "url");
        if (!string.IsNullOrWhiteSpace(url))
        {
            return encoding.Encode(url).Count;
        }

        return 0;
    }

    private static int EstimateDocumentTokens(object? blockObj, GptEncoding encoding)
    {
        var sourceObj = GetField(blockObj, "source");
        var sourceType = ExtractString(sourceObj, "type");
        var mediaType = ExtractString(sourceObj, "media_type");
        var data = ExtractString(sourceObj, "data");

        if (string.Equals(sourceType, "text", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(data))
        {
            return encoding.Encode(data).Count;
        }

        if (!string.IsNullOrWhiteSpace(mediaType) && mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(data))
        {
            return Math.Min(4000, Math.Max(0, data.Length / 256));
        }

        return CountJsonFallback(blockObj, encoding);
    }

    private static int CountSearchResultTokens(object? blockObj, GptEncoding encoding)
    {
        var total = 0;
        var title = ExtractString(blockObj, "title");
        var source = ExtractString(blockObj, "source");

        if (!string.IsNullOrWhiteSpace(title))
        {
            total += encoding.Encode(title).Count;
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            total += encoding.Encode(source).Count;
        }

        var contentObj = GetField(blockObj, "content");
        if (contentObj is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            total += CountContentBlocks(EnumerateJsonArray(element), encoding);
            return total;
        }

        if (contentObj is IEnumerable<object> list)
        {
            total += CountContentBlocks(list, encoding);
            return total;
        }

        total += CountJsonFallback(blockObj, encoding);
        return total;
    }

    private static object? GetField(object? obj, string name)
    {
        if (obj is null)
        {
            return null;
        }

        if (obj is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var prop))
            {
                return prop;
            }
        }

        if (obj is IDictionary<string, object?> dict && dict.TryGetValue(name, out var value))
        {
            return value;
        }

        return null;
    }

    private static string? ExtractString(object? obj, string property)
    {
        if (obj is JsonElement element && element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var prop))
        {
            return prop.GetString();
        }

        if (obj is IDictionary<string, object?> dict && dict.TryGetValue(property, out var value))
        {
            return value?.ToString();
        }

        return null;
    }

    private static bool? ExtractBool(object? obj, string property)
    {
        if (obj is JsonElement element && element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (prop.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        if (obj is IDictionary<string, object?> dict && dict.TryGetValue(property, out var value) && value is not null)
        {
            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (bool.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateJsonArray(JsonElement element)
    {
        foreach (var item in element.EnumerateArray())
        {
            yield return item;
        }
    }

    private static string ExtractTextValue(object? blockObj)
    {
        if (blockObj is JsonElement element && element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("text", out var textProp))
        {
            return textProp.GetString() ?? string.Empty;
        }

        if (blockObj is IDictionary<string, object?> dict && dict.TryGetValue("text", out var textValue))
        {
            return textValue?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? ExtractTextFromSystem(object? systemBlock)
    {
        if (systemBlock is null)
        {
            return null;
        }

        if (systemBlock is string systemText)
        {
            return systemText;
        }

        if (systemBlock is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Array => ExtractTextFromSystem(EnumerateJsonArray(element)),
                JsonValueKind.Object => null,
                _ => null
            };
        }

        if (systemBlock is IEnumerable<object> list)
        {
            var buffer = new List<string>();
            foreach (var item in list)
            {
                if (item is JsonElement itemElement)
                {
                    if (itemElement.ValueKind == JsonValueKind.Object &&
                        itemElement.TryGetProperty("type", out var typeProp) &&
                        typeProp.GetString() == "text" &&
                        itemElement.TryGetProperty("text", out var textProp))
                    {
                        buffer.Add(textProp.GetString() ?? string.Empty);
                    }
                }
                else if (item is Dictionary<string, object?> dict &&
                         dict.TryGetValue("type", out var typeObj) &&
                         string.Equals(typeObj?.ToString(), "text", StringComparison.OrdinalIgnoreCase) &&
                         dict.TryGetValue("text", out var textObj))
                {
                    buffer.Add(textObj?.ToString() ?? string.Empty);
                }
            }

            var combined = string.Join("\n\n", buffer).Trim();
            return string.IsNullOrWhiteSpace(combined) ? null : combined;
        }

        return null;
    }
}
