using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[assembly: InternalsVisibleTo("A2A.Tests")]

namespace LiveLink.Agent.A2A
{
    /// <summary>
    /// Lightweight A2A protocol client for Unity (IL2CPP-compatible).
    /// Uses raw HttpClient — no external NuGet dependencies beyond System.Text.Json.
    /// Implements the A2A v1.0 HTTP+JSON binding.
    /// </summary>
    public class A2AClient : IDisposable
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly Uri _endpoint;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, string> _headers;
        private bool _isDisposed;

        public Uri Endpoint => _endpoint;

        public A2AClient(Uri endpoint, Dictionary<string, string> headers = null, float timeoutSeconds = 30f)
            : this(endpoint, null, headers, timeoutSeconds)
        {
        }

        /// <summary>
        /// Internal constructor for testing — allows injecting a custom HttpMessageHandler.
        /// </summary>
        internal A2AClient(Uri endpoint, HttpMessageHandler handler, Dictionary<string, string> headers = null, float timeoutSeconds = 30f)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _headers = headers != null
                ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            _httpClient = handler != null
                ? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(1f, timeoutSeconds)) }
                : new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1f, timeoutSeconds)) };
        }

        /// <summary>
        /// Fetch the agent card from /.well-known/agent-card.json.
        /// </summary>
        public static async Task<A2AAgentCard> GetAgentCardAsync(
            Uri host,
            Dictionary<string, string> headers = null,
            float timeoutSeconds = 30f)
        {
            // Build the well-known URI.
            Uri cardUri = new Uri(host, "/.well-known/agent-card.json");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1f, timeoutSeconds));

                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> header in headers)
                    {
                        client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                Debug.Log(string.Format("[LiveLink-A2A] Fetching agent card from {0}", cardUri));

                HttpResponseMessage response = await client.GetAsync(cardUri).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                A2AAgentCard card = JsonSerializer.Deserialize<A2AAgentCard>(json, s_jsonOptions);

                Debug.Log(string.Format("[LiveLink-A2A] Discovered agent: {0} v{1}",
                    card?.Name ?? "(unknown)", card?.Version ?? "(unknown)"));

                return card;
            }
        }

        /// <summary>
        /// Send a synchronous message to the remote A2A agent.
        /// </summary>
        public async Task<A2AMessage> SendMessageAsync(
            A2AMessage message,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var request = new A2ASendMessageRequest
            {
                Message = message,
                Streaming = false
            };

            string json = JsonSerializer.Serialize(request, s_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            Debug.Log(string.Format("[LiveLink-A2A] Sending message to {0} ({1})",
                _endpoint, message.MessageId));

            using (HttpRequestMessage httpRequest = BuildPostRequest(content))
            {
                HttpResponseMessage response = await _httpClient
                    .SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                A2ASendMessageResponse result = JsonSerializer
                    .Deserialize<A2ASendMessageResponse>(responseJson, s_jsonOptions);

                if (result?.Error != null)
                {
                    throw new InvalidOperationException(string.Format(
                        "A2A error {0}: {1}", result.Error.Code, result.Error.Message));
                }

                return result?.Message;
            }
        }

        /// <summary>
        /// Send a streaming message. Chunks arrive via the callback as SSE events.
        /// Returns the aggregated list of message chunks.
        /// </summary>
        public async Task<List<A2AMessage>> SendMessageStreamingAsync(
            A2AMessage message,
            Action<A2AMessage> onMessageChunk = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var request = new A2ASendMessageRequest
            {
                Message = message,
                Streaming = true
            };

            string json = JsonSerializer.Serialize(request, s_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            Debug.Log(string.Format("[LiveLink-A2A] Sending streaming message to {0} ({1})",
                _endpoint, message.MessageId));

            var messages = new List<A2AMessage>();

            using (HttpRequestMessage httpRequest = BuildPostRequest(content))
            {
                httpRequest.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                HttpResponseMessage response = await _httpClient
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                {
                    string eventType = null;
                    string dataBuffer = null;

                    while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;

                        if (line.StartsWith("event: ", StringComparison.Ordinal))
                        {
                            eventType = line.Substring(7).Trim();
                        }
                        else if (line.StartsWith("data: ", StringComparison.Ordinal))
                        {
                            dataBuffer = line.Substring(6);
                        }
                        else if (string.IsNullOrEmpty(line) && dataBuffer != null)
                        {
                            // Blank line = end of SSE event.
                            ProcessSseEvent(eventType, dataBuffer, messages, onMessageChunk);
                            eventType = null;
                            dataBuffer = null;
                        }
                    }

                    // Flush any trailing event.
                    if (dataBuffer != null)
                    {
                        ProcessSseEvent(eventType, dataBuffer, messages, onMessageChunk);
                    }
                }
            }

            return messages;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _httpClient?.Dispose();
        }

        // ──────────────────────── private helpers ────────────────────────

        private HttpRequestMessage BuildPostRequest(HttpContent content)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Content = content;

            foreach (KeyValuePair<string, string> header in _headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return request;
        }

        private static void ProcessSseEvent(
            string eventType,
            string data,
            List<A2AMessage> messages,
            Action<A2AMessage> onMessageChunk)
        {
            if (string.IsNullOrEmpty(data)) return;

            try
            {
                if (string.Equals(eventType, "message", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(eventType))
                {
                    A2AStreamingEvent evt = JsonSerializer
                        .Deserialize<A2AStreamingEvent>(data, s_jsonOptions);

                    if (evt?.Message != null)
                    {
                        messages.Add(evt.Message);
                        onMessageChunk?.Invoke(evt.Message);
                    }
                }
                else if (string.Equals(eventType, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log("[LiveLink-A2A] Streaming complete");
                }
                else if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError(string.Format("[LiveLink-A2A] Streaming error: {0}", data));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(string.Format(
                    "[LiveLink-A2A] Failed to parse SSE event: {0}", ex.Message));
            }
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(A2AClient));
            }
        }
    }
}
