using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
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

        /// <summary>
        /// Maximum number of SSE reconnection attempts before giving up.
        /// </summary>
        private const int MaxReconnectAttempts = 3;

        /// <summary>
        /// Base delay in milliseconds for exponential backoff on SSE reconnection.
        /// </summary>
        private const int ReconnectBaseDelayMs = 1000;

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
        /// Creates an HttpClientHandler that optionally accepts custom certificates
        /// (e.g., self-signed or enterprise CA).
        /// </summary>
        internal static HttpClientHandler CreateHandlerWithCertificateValidation(
            Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> validator = null)
        {
            var handler = new HttpClientHandler();

            if (validator != null)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (request, cert, chain, errors) => validator(request, cert, chain, errors);
            }

            return handler;
        }

        /// <summary>
        /// Fetch the agent card from /.well-known/agent-card.json.
        /// Accepts an optional handler for testability; when null, a new HttpClient is created.
        /// </summary>
        public static async Task<A2AAgentCard> GetAgentCardAsync(
            Uri host,
            Dictionary<string, string> headers = null,
            float timeoutSeconds = 30f,
            HttpMessageHandler handler = null)
        {
            // Build the well-known URI.
            Uri cardUri = new Uri(host, "/.well-known/agent-card.json");

            // Reuse the caller-supplied handler when available to avoid socket exhaustion.
            // Ownership stays with the caller — we do NOT dispose a passed-in handler.
            bool ownsHandler = handler == null;
            if (ownsHandler)
            {
                handler = new HttpClientHandler();
            }

            try
            {
                using (var client = new HttpClient(handler, disposeHandler: ownsHandler))
                {
                    client.Timeout = TimeSpan.FromSeconds(Math.Max(1f, timeoutSeconds));

                    if (headers != null)
                    {
                        foreach (KeyValuePair<string, string> header in headers)
                        {
                            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }

                    Log("Fetching agent card from {0}", cardUri);

                    HttpResponseMessage response = await client.GetAsync(cardUri).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    A2AAgentCard card = JsonSerializer.Deserialize<A2AAgentCard>(json, s_jsonOptions);

                    Log("Discovered agent: {0} v{1}",
                        card?.Name ?? "(unknown)", card?.Version ?? "(unknown)");

                    return card;
                }
            }
            catch
            {
                // If we created the handler and an error occurred, dispose it to avoid leaks.
                if (ownsHandler)
                {
                    handler?.Dispose();
                }
                throw;
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

            Log("Sending message to {0} ({1})", _endpoint, message.MessageId);

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
        /// Automatically reconnects on connection drops (up to <see cref="MaxReconnectAttempts"/>).
        /// Returns the aggregated list of message chunks.
        /// </summary>
        public async Task<List<A2AMessage>> SendMessageStreamingAsync(
            A2AMessage message,
            Action<A2AMessage> onMessageChunk = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            string json = JsonSerializer.Serialize(new A2ASendMessageRequest
            {
                Message = message,
                Streaming = true
            }, s_jsonOptions);

            Log("Sending streaming message to {0} ({1})", _endpoint, message.MessageId);

            var messages = new List<A2AMessage>();
            int attempt = 0;

            while (attempt <= MaxReconnectAttempts && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    bool connected = await ConnectAndReadStreamAsync(
                        json, messages, onMessageChunk, cancellationToken).ConfigureAwait(false);

                    if (connected)
                    {
                        // Stream completed normally (server sent complete event or closed).
                        return messages;
                    }

                    // Connection dropped unexpectedly — attempt reconnect.
                    attempt++;
                    if (attempt <= MaxReconnectAttempts)
                    {
                        int delayMs = ReconnectBaseDelayMs * (1 << (attempt - 1)); // exponential backoff
                        LogWarning("SSE connection dropped. Reconnecting in {0}ms (attempt {1}/{2})...",
                            delayMs, attempt, MaxReconnectAttempts);

                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // User cancelled — propagate immediately.
                    throw;
                }
                catch (Exception ex)
                {
                    attempt++;
                    if (attempt <= MaxReconnectAttempts)
                    {
                        int delayMs = ReconnectBaseDelayMs * (1 << (attempt - 1));
                        LogWarning("SSE error: {0}. Reconnecting in {1}ms (attempt {2}/{3})...",
                            ex.Message, delayMs, attempt, MaxReconnectAttempts);

                        try
                        {
                            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                    }
                    else
                    {
                        LogError("SSE connection failed after {0} attempts: {1}", MaxReconnectAttempts + 1, ex.Message);
                        throw;
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

        /// <summary>
        /// Single SSE connection attempt. Returns true if the stream completed normally,
        /// false if the connection was dropped (caller should reconnect).
        /// </summary>
        private async Task<bool> ConnectAndReadStreamAsync(
            string requestJson,
            List<A2AMessage> messages,
            Action<A2AMessage> onMessageChunk,
            CancellationToken cancellationToken)
        {
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

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
                    var dataLines = new List<string>();

                    // Register cancellation to unblock ReadLineAsync when the token fires.
                    using (cancellationToken.Register(() => { try { reader.Dispose(); } catch { } }))
                    {
                        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                        {
                            string line;
                            try
                            {
                                line = await reader.ReadLineAsync().ConfigureAwait(false);
                            }
                            catch (ObjectDisposedException)
                            {
                                // Reader was disposed by the cancellation callback.
                                break;
                            }

                            if (line == null)
                            {
                                // Server closed the connection cleanly.
                                return true;
                            }

                            if (line.StartsWith("event: ", StringComparison.Ordinal))
                            {
                                eventType = line.Substring(7).Trim();
                            }
                            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                            {
                                // SSE spec: multiple data lines are joined with newlines.
                                dataLines.Add(line.Substring(6));
                            }
                            else if (string.IsNullOrEmpty(line) && dataLines.Count > 0)
                            {
                                // Blank line = end of SSE event. Join multi-line data with \n.
                                string data = string.Join("\n", dataLines);
                                bool isComplete = ProcessSseEvent(eventType, data, messages, onMessageChunk);
                                eventType = null;
                                dataLines.Clear();

                                if (isComplete)
                                {
                                    return true;
                                }
                            }
                        }
                    }

                    // Flush any trailing event.
                    if (dataLines.Count > 0)
                    {
                        string data = string.Join("\n", dataLines);
                        ProcessSseEvent(eventType, data, messages, onMessageChunk);
                    }
                }
            }

            // If we got here without cancellation, the connection was dropped.
            return !cancellationToken.IsCancellationRequested;
        }

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

        /// <summary>
        /// Processes a single SSE event. Returns true if the event was a "complete" event.
        /// </summary>
        private static bool ProcessSseEvent(
            string eventType,
            string data,
            List<A2AMessage> messages,
            Action<A2AMessage> onMessageChunk)
        {
            if (string.IsNullOrEmpty(data)) return false;

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

                    return false;
                }
                else if (string.Equals(eventType, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Streaming complete");
                    return true;
                }
                else if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    LogError("Streaming error: {0}", data);
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogWarning("Failed to parse SSE event: {0}", ex.Message);
            }

            return false;
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(A2AClient));
            }
        }

        // ──────────────────────── Thread-safe logging ────────────────────────
        // Unity's Debug.Log is NOT thread-safe on all platforms (especially Android IL2CPP).
        // These helpers suppress logs from non-main threads to prevent crashes.

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private static void Log(string format, params object[] args)
        {
            try
            {
                Debug.Log("[LiveLink-A2A] " + string.Format(format, args));
            }
            catch
            {
                // Swallow — logging should never break runtime behavior.
            }
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private static void LogWarning(string format, params object[] args)
        {
            try
            {
                Debug.LogWarning("[LiveLink-A2A] " + string.Format(format, args));
            }
            catch { }
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private static void LogError(string format, params object[] args)
        {
            try
            {
                Debug.LogError("[LiveLink-A2A] " + string.Format(format, args));
            }
            catch { }
        }
    }
}
