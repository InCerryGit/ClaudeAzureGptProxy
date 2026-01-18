using System.Text.Json;
using ClaudeAzureGptProxy.Infrastructure;
using ClaudeAzureGptProxy.Models;

namespace ClaudeAzureGptProxy.Services;

public static class CursorRequestAdapter
{
    public static (JsonDocument Body, string InboundModel) BuildResponsesRequest(
        OpenAiChatCompletionsRequest request,
        NormalizedAzureOpenAiOptions azureOptions)
    {
        var inboundModel = request.Model?.Trim() ?? string.Empty;
        var effort = MapReasoningEffort(inboundModel);

        if (string.IsNullOrWhiteSpace(azureOptions.CursorAzureDeployment))
        {
            throw new InvalidOperationException("Missing CURSOR_AZURE_DEPLOYMENT.");
        }

        var (input, instructions) = MessagesToResponsesInputAndInstructions(request.Messages);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WriteString("model", azureOptions.CursorAzureDeployment);
            writer.WriteBoolean("stream", true);

            writer.WritePropertyName("input");
            input.WriteTo(writer);

            if (!string.IsNullOrWhiteSpace(instructions))
            {
                writer.WriteString("instructions", instructions);
            }

            if (request.Tools is { Count: > 0 })
            {
                writer.WritePropertyName("tools");
                WriteResponsesTools(writer, request.Tools);
            }

            if (request.ToolChoice is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } toolChoice)
            {
                writer.WritePropertyName("tool_choice");
                toolChoice.WriteTo(writer);
            }

            if (!string.IsNullOrWhiteSpace(request.User))
            {
                writer.WriteString("prompt_cache_key", request.User);
            }

            writer.WritePropertyName("reasoning");
            writer.WriteStartObject();
            writer.WriteString("effort", effort);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        stream.Position = 0;
        var doc = JsonDocument.Parse(stream);
        return (doc, inboundModel);
    }

    private static string MapReasoningEffort(string inboundModel)
    {
        // python: reasoning_effort = inbound_model.replace("gpt-", "").lower(); allow {high,medium,low,minimal}
        var effort = inboundModel.Replace("gpt-", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
        return effort switch
        {
            "high" => "high",
            "medium" => "medium",
            "low" => "low",
            "minimal" => "minimal",
            _ => throw new ArgumentException($"Invalid model '{inboundModel}'. Allowed: gpt-high|gpt-medium|gpt-low|gpt-minimal.")
        };
    }

    private static (JsonElement Input, string Instructions) MessagesToResponsesInputAndInstructions(List<OpenAiChatMessage> messages)
    {
        var instructionsParts = new List<string>();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();

            foreach (var m in messages)
            {
                var role = (m.Role ?? string.Empty).Trim();
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
                {
                    if (m.Content.ValueKind == JsonValueKind.String)
                    {
                        var s = m.Content.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            instructionsParts.Add(s);
                        }
                    }
                    continue;
                }

                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call_output");
                    writer.WriteString("status", "completed");
                    if (!string.IsNullOrWhiteSpace(m.ToolCallId))
                    {
                        writer.WriteString("call_id", m.ToolCallId);
                    }

                    writer.WritePropertyName("output");
                    WriteContentAsTextOrJson(writer, m.Content);

                    writer.WriteEndObject();
                    continue;
                }

                // user / assistant
                writer.WriteStartObject();
                writer.WriteString("role", role.ToLowerInvariant());

                writer.WritePropertyName("content");
                writer.WriteStartArray();
                writer.WriteStartObject();

                var contentType = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "output_text" : "input_text";
                writer.WriteString("type", contentType);
                writer.WritePropertyName("text");
                writer.WriteStringValue(ExtractContentText(m.Content));

                writer.WriteEndObject();
                writer.WriteEndArray();

                writer.WriteEndObject();

                // tool_calls on assistant messages -> Responses function_call items
                if (m.ToolCalls is { Count: > 0 })
                {
                    foreach (var tc in m.ToolCalls)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "function_call");
                        writer.WriteString("call_id", tc.Id);
                        writer.WriteString("name", tc.Function.Name);
                        writer.WritePropertyName("arguments");
                        writer.WriteStringValue(tc.Function.Arguments ?? string.Empty);
                        writer.WriteEndObject();
                    }
                }
            }

            writer.WriteEndArray();
        }

        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        return (doc.RootElement.Clone(), string.Join("\n\n", instructionsParts));
    }

    private static void WriteResponsesTools(Utf8JsonWriter writer, List<OpenAiTool> tools)
    {
        writer.WriteStartArray();
        foreach (var t in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteString("name", t.Function.Name);

            if (!string.IsNullOrWhiteSpace(t.Function.Description))
            {
                writer.WriteString("description", t.Function.Description);
            }

            if (t.Function.Parameters is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } parameters)
            {
                writer.WritePropertyName("parameters");
                parameters.WriteTo(writer);
            }

            writer.WriteBoolean("strict", false);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string ExtractContentText(JsonElement content)
    {
        // Keep minimal: if content is string -> return.
        // If array/object -> return its raw JSON string (Cursor project is looser here; adapter in python normalizes richer content).
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => content.GetRawText()
        };
    }

    private static void WriteContentAsTextOrJson(Utf8JsonWriter writer, JsonElement content)
    {
        // For tool outputs: python forwards text; we keep string when possible.
        if (content.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(content.GetString() ?? string.Empty);
            return;
        }

        if (content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        content.WriteTo(writer);
    }
}
