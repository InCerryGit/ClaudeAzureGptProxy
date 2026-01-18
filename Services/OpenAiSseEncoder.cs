using System.Text.Json;

namespace ClaudeAzureGptProxy.Services;

public static class OpenAiSseEncoder
{
    public static string EncodeJson(JsonElement element)
    {
        return $"data: {element.GetRawText()}\n\n";
    }

    public static string Done()
    {
        return "data: [DONE]\n\n";
    }
}
