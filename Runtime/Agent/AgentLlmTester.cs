using System;
using System.ClientModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;

namespace LiveLink.Agent
{
    /// <summary>
    /// Editor-safe helper that validates the LLM backend configuration
    /// by sending a minimal chat completion request.
    /// </summary>
    public static class AgentLlmTester
    {
        /// <summary>
        /// Tests the LLM connection by sending a single "ping" message.
        /// </summary>
        public static async Task<AgentLlmTestResult> TestConnectionAsync(AgentRuntimeConfig config, CancellationToken cancellationToken)
        {
            string apiKey = config.ResolveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new AgentLlmTestResult
                {
                    Success = false,
                    ErrorMessage = "No API key configured. Set one on the asset, call SetApiKey(), or provide it via the configured environment variable."
                };
            }

            string model = config.Model;
            if (string.IsNullOrWhiteSpace(model))
            {
                return new AgentLlmTestResult
                {
                    Success = false,
                    ErrorMessage = "No model configured."
                };
            }

            try
            {
                OpenAIClient openAiClient;
                string endpoint = config.ApiEndpoint;

                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
                    openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);
                }
                else
                {
                    openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey));
                }

                ChatClient chatClient = openAiClient.GetChatClient(model);

                var stopwatch = Stopwatch.StartNew();

                ChatCompletion completion = await chatClient.CompleteChatAsync(
                    new[] { ChatMessage.CreateUserMessage("Reply with exactly: pong") },
                    new ChatCompletionOptions
                    {
                        MaxOutputTokenCount = 16,
                        Temperature = 0f
                    },
                    cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();

                string responseText = completion.Content.Count > 0
                    ? completion.Content[0].Text
                    : "(empty response)";

                return new AgentLlmTestResult
                {
                    Success = true,
                    Model = model,
                    ResponseText = responseText.Trim(),
                    LatencyMs = stopwatch.ElapsedMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                return new AgentLlmTestResult
                {
                    Success = false,
                    Model = model,
                    ErrorMessage = "Request timed out."
                };
            }
            catch (ClientResultException ex)
            {
                return new AgentLlmTestResult
                {
                    Success = false,
                    Model = model,
                    ErrorMessage = string.Format("API error ({0}): {1}", ex.Status, ex.Message)
                };
            }
            catch (Exception ex)
            {
                return new AgentLlmTestResult
                {
                    Success = false,
                    Model = model,
                    ErrorMessage = string.Format("{0}: {1}", ex.GetType().Name, ex.Message)
                };
            }
        }
    }
}
