using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;

namespace VaultToFlashcard;

public static class AiChatProviderFactory
{
    public static IChatClient CreateChatClient(
        string provider,
        string apiKey,
        string model)
    {
        return provider.ToLowerInvariant() switch
        {
            "gemini" => CreateGeminiClient(apiKey, model),
            "anthropic" => CreateOpenAiCompatibleClient(apiKey, model, "https://api.anthropic.com/"),
            "minimax" => CreateOpenAiCompatibleClient(apiKey, model, "https://api.minimax.chat/"),
            "ollama" => CreateOpenAiCompatibleClient(apiKey, model, "http://localhost:11434/v1"),
            _ => throw new ArgumentException($"Unknown provider: '{provider}'. Valid options: gemini, anthropic, minimax, ollama")
        };
    }

    private static IChatClient CreateGeminiClient(string apiKey, string model)
    {
        var gemini = new GeminiChatClient(new GeminiClientOptions
        {
            ApiKey = apiKey,
            ModelId = model
        });
        return new ChatClientBuilder(gemini).Build();
    }

    private static IChatClient CreateOpenAiCompatibleClient(string apiKey, string model, string endpoint)
    {
        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint)
        });

        var chatClient = openAiClient.GetChatClient(model);
        return chatClient.AsIChatClient();
    }
}
