using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using UnityEngine;

namespace LiveLink.Agent.A2A
{
    /// <summary>
    /// Wraps a remote A2A agent as an AIFunction that the embedded agent can call as a tool.
    /// Tool name: {prefix}{sanitized_display_name}  (e.g., "ask_openclaw")
    /// Parameter: message (string) — the question/task to delegate.
    /// Returns: the remote agent's text response.
    /// </summary>
    public class A2AAgentToolWrapper : AIFunction
    {
        private static readonly JsonSerializerOptions s_argumentJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly string _name;
        private readonly string _description;
        private readonly A2AClient _client;
        private readonly A2AAgentCard _agentCard;
        private readonly bool _enableStreaming;
        private readonly Action<string, string> _onToolCall;

        public override string Name => _name;
        public override string Description => _description;

        public A2AAgentToolWrapper(
            string displayName,
            A2AClient client,
            A2AAgentCard agentCard,
            bool enableStreaming,
            Action<string, string> onToolCall = null,
            string toolNamePrefix = "ask_")
        {
            _name = SanitizeToolName(displayName, toolNamePrefix);
            _description = BuildDescription(agentCard, displayName);
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _agentCard = agentCard;
            _enableStreaming = enableStreaming;
            _onToolCall = onToolCall;
        }

        public override JsonElement JsonSchema
        {
            get
            {
                // Build a minimal JSON Schema for the "message" parameter.
                var schema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["message"] = new Dictionary<string, string>
                        {
                            ["type"] = "string",
                            ["description"] = "The message or question to delegate to the remote agent"
                        }
                    },
                    ["required"] = new[] { "message" }
                };

                string json = JsonSerializer.Serialize(schema, s_argumentJsonOptions);
                return JsonSerializer.Deserialize<JsonElement>(json);
            }
        }

        protected override async ValueTask<object> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            string message = GetStringArgument(arguments, "message");

            if (string.IsNullOrWhiteSpace(message))
            {
                return "Error: 'message' parameter is required.";
            }

            // Notify listeners (same pattern as ToolCallNotifyingFunction).
            _onToolCall?.Invoke(_name, JsonSerializer.Serialize(
                new { message }, s_argumentJsonOptions));

            try
            {
                A2AMessage a2aMessage = A2AMessage.CreateUserTextMessage(message);

                bool canStream = _enableStreaming
                    && _agentCard?.Capabilities?.Streaming == true;

                if (canStream)
                {
                    return await InvokeStreamingAsync(a2aMessage, cancellationToken)
                        .ConfigureAwait(false);
                }

                A2AMessage response = await _client
                    .SendMessageAsync(a2aMessage, cancellationToken)
                    .ConfigureAwait(false);

                return ExtractText(response);
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format(
                    "[LiveLink-A2A] Tool '{0}' failed: {1}", _name, ex.Message));
                return string.Format("Error communicating with remote agent: {0}", ex.Message);
            }
        }

        // ──────────────────────── private helpers ────────────────────────

        private async Task<string> InvokeStreamingAsync(
            A2AMessage message,
            CancellationToken cancellationToken)
        {
            var chunks = new List<string>();

            await _client.SendMessageStreamingAsync(
                message,
                onMessageChunk: chunk =>
                {
                    string text = ExtractText(chunk);
                    if (!string.IsNullOrEmpty(text))
                    {
                        chunks.Add(text);
                    }
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return string.Join("", chunks);
        }

        private static string ExtractText(A2AMessage message)
        {
            if (message?.Parts == null || message.Parts.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < message.Parts.Count; i++)
            {
                A2APart part = message.Parts[i];
                if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(part.Text))
                {
                    sb.Append(part.Text);
                }
            }

            return sb.ToString();
        }

        private static string GetStringArgument(AIFunctionArguments arguments, string name)
        {
            if (arguments == null) return null;

            foreach (KeyValuePair<string, object> entry in arguments)
            {
                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value?.ToString();
                }
            }

            return null;
        }

        /// <summary>
        /// Turn a display name into a safe tool name: lowercase, alphanumeric + underscores.
        /// Uses the provided prefix (e.g., "ask_") prepended to the sanitized name.
        /// </summary>
        private static string SanitizeToolName(string displayName, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) prefix = "ask_";

            if (string.IsNullOrWhiteSpace(displayName)) return prefix + "remote_agent";

            var sb = new StringBuilder(prefix);
            for (int i = 0; i < displayName.Length; i++)
            {
                char c = displayName[i];
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
                else if (c == ' ' || c == '-' || c == '_')
                {
                    // Avoid double underscores: skip if the last char is already '_'.
                    if (sb.Length > 0 && sb[sb.Length - 1] != '_')
                    {
                        sb.Append('_');
                    }
                }
            }

            // Trim trailing underscore.
            while (sb.Length > 0 && sb[sb.Length - 1] == '_')
            {
                sb.Length--;
            }

            return sb.Length > prefix.Length ? sb.ToString() : prefix + "remote_agent";
        }

        private static string BuildDescription(A2AAgentCard card, string displayName)
        {
            if (card != null && !string.IsNullOrWhiteSpace(card.Description))
            {
                return string.Format("Delegate a task to {0}: {1}", displayName, card.Description.Trim());
            }

            return string.Format("Delegate a task to the remote agent '{0}'.", displayName);
        }
    }
}
