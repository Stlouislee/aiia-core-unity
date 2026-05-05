using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LiveLink.Agent.A2A
{
    /// <summary>
    /// HTTP server for A2A (Agent-to-Agent) hosting using raw TCP sockets.
    /// IL2CPP-compatible — no HttpListener dependency.
    ///
    /// Endpoints:
    ///   GET  /.well-known/agent-card.json  — agent discovery
    ///   POST /a2a                           — send message, receive response
    ///   GET  /a2a/stream                    — SSE streaming (future)
    ///   GET  /health                        — health check
    /// </summary>
    public class A2AHostServer : IDisposable
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private const int MaxHeaderBytes = 32 * 1024;

        private readonly A2AHostConfig _config;
        private readonly Func<string, CancellationToken, Task<string>> _handleMessageAsync;
        private readonly string _agentCardJson;

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private bool _isRunning;

        // Rate limiting: IP -> (windowStart, count)
        private readonly ConcurrentDictionary<string, (DateTime windowStart, int count)> _rateLimits =
            new ConcurrentDictionary<string, (DateTime, int)>();

        public bool IsRunning => _isRunning;
        public int Port => _isRunning && _listener != null
            ? ((IPEndPoint)_listener.LocalEndpoint).Port
            : _config.Port;

        /// <summary>
        /// Creates a new A2A host server.
        /// </summary>
        /// <param name="config">Hosting configuration.</param>
        /// <param name="handleMessageAsync">
        /// Callback that processes a user message and returns the agent's text response.
        /// Signature: (string userMessage, CancellationToken ct) => Task&lt;string&gt; agentResponse
        /// </param>
        public A2AHostServer(A2AHostConfig config, Func<string, CancellationToken, Task<string>> handleMessageAsync)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _handleMessageAsync = handleMessageAsync ?? throw new ArgumentNullException(nameof(handleMessageAsync));

            // Pre-build the agent card JSON once.
            _agentCardJson = A2AAgentCardBuilder.BuildCardJson(config, null);
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _listener = new TcpListener(IPAddress.Any, _config.Port);
                _listener.Start();
                _isRunning = true;

                _cts = new CancellationTokenSource();
                Task.Run(() => AcceptClientsAsync(_cts.Token));

                Debug.Log($"[LiveLink-A2A] Host server started on port {_config.Port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-A2A] Failed to start host server: {ex.Message}");
                _isRunning = false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-A2A] Error stopping host server: {ex.Message}");
            }

            Debug.Log("[LiveLink-A2A] Host server stopped");
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _listener = null;
        }

        // ──────────────────────── Request handling ────────────────────────

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            while (_isRunning && !ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client, ct));
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Debug.LogError($"[LiveLink-A2A] Error accepting client: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            NetworkStream stream = null;
            try
            {
                client.NoDelay = true;
                stream = client.GetStream();

                A2AHttpRequest request = await ReadHttpRequestAsync(stream);
                if (request == null) return;

                // CORS preflight
                if (request.Method == "OPTIONS")
                {
                    await WriteHttpResponseAsync(stream, 204);
                    return;
                }

                // Auth check
                if (!ValidateAuth(request))
                {
                    await WriteJsonResponseAsync(stream, 401, new { error = "Unauthorized" });
                    return;
                }

                // Rate limit check
                string clientIp = ((IPEndPoint)client.Client.RemoteEndPoint)?.Address?.ToString() ?? "unknown";
                if (!CheckRateLimit(clientIp))
                {
                    await WriteJsonResponseAsync(stream, 429, new { error = "Rate limit exceeded" });
                    return;
                }

                // Route
                await RouteRequestAsync(request, stream, ct);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-A2A] Error handling request: {ex.Message}");
                if (stream != null && stream.CanWrite)
                {
                    try { await WriteHttpResponseAsync(stream, 500); } catch { }
                }
            }
            finally
            {
                try { stream?.Close(); } catch { }
                try { client?.Close(); } catch { }
            }
        }

        private async Task RouteRequestAsync(A2AHttpRequest request, NetworkStream stream, CancellationToken ct)
        {
            string path = request.Path;

            // Agent card discovery
            if (path == "/.well-known/agent-card.json" && request.Method == "GET")
            {
                await WriteRawJsonResponseAsync(stream, 200, _agentCardJson);
                return;
            }

            // A2A message endpoint
            if ((path == "/a2a" || path == "/a2a/") && request.Method == "POST")
            {
                await HandleSendMessageAsync(request, stream, ct);
                return;
            }

            // Health check
            if ((path == "/health" || path == "/") && request.Method == "GET")
            {
                await WriteJsonResponseAsync(stream, 200, new
                {
                    status = "ok",
                    protocol = "A2A",
                    version = "1.0",
                    agent = _config.AgentName
                });
                return;
            }

            await WriteJsonResponseAsync(stream, 404, new { error = "Not found" });
        }

        private async Task HandleSendMessageAsync(A2AHttpRequest request, NetworkStream stream, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                await WriteJsonResponseAsync(stream, 400, new { error = "Empty request body" });
                return;
            }

            A2ASendMessageRequest sendRequest;
            try
            {
                sendRequest = JsonSerializer.Deserialize<A2ASendMessageRequest>(request.Body, s_jsonOptions);
            }
            catch (JsonException ex)
            {
                await WriteJsonResponseAsync(stream, 400, new { error = $"Invalid JSON: {ex.Message}" });
                return;
            }

            if (sendRequest?.Message == null)
            {
                await WriteJsonResponseAsync(stream, 400, new { error = "Missing 'message' field" });
                return;
            }

            // Extract text from the message parts.
            string userText = ExtractText(sendRequest.Message);
            if (string.IsNullOrWhiteSpace(userText))
            {
                await WriteJsonResponseAsync(stream, 400, new { error = "Message must contain at least one text part" });
                return;
            }

            // If streaming is requested and supported, use SSE.
            if (sendRequest.Streaming && _config.EnableStreaming)
            {
                await HandleStreamingRequestAsync(userText, stream, ct);
                return;
            }

            // Synchronous request.
            try
            {
                string agentResponse = await _handleMessageAsync(userText, ct);

                var response = new A2ASendMessageResponse
                {
                    Message = A2AMessage.CreateAgentTextMessage(agentResponse)
                };

                string responseJson = JsonSerializer.Serialize(response, s_jsonOptions);
                await WriteRawJsonResponseAsync(stream, 200, responseJson);
            }
            catch (OperationCanceledException)
            {
                await WriteJsonResponseAsync(stream, 504, new { error = "Agent request timed out" });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-A2A] Agent processing error: {ex.Message}");
                await WriteJsonResponseAsync(stream, 500, new { error = $"Agent error: {ex.Message}" });
            }
        }

        private async Task HandleStreamingRequestAsync(string userText, NetworkStream stream, CancellationToken ct)
        {
            // Write SSE headers
            await WriteSseHeadersAsync(stream);

            try
            {
                // For now, we send the complete response as a single SSE message event.
                // Future: integrate with the agent's streaming API when available.
                string agentResponse = await _handleMessageAsync(userText, ct);

                var sseMessage = new A2AMessage
                {
                    Role = "agent",
                    Parts = new List<A2APart> { A2APart.FromText(agentResponse) }
                };

                var sseEvent = new A2AStreamingEvent { Message = sseMessage };
                string eventJson = JsonSerializer.Serialize(sseEvent, s_jsonOptions);

                await WriteSseEventAsync(stream, "message", eventJson);

                // Send complete event
                var completeEvent = new A2AStreamingEvent { Status = "completed" };
                string completeJson = JsonSerializer.Serialize(completeEvent, s_jsonOptions);
                await WriteSseEventAsync(stream, "complete", completeJson);
            }
            catch (OperationCanceledException)
            {
                await WriteSseEventAsync(stream, "error", "{\"error\":\"Agent request timed out\"}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-A2A] SSE agent error: {ex.Message}");
                await WriteSseEventAsync(stream, "error", $"{{\"error\":\"{EscapeJson(ex.Message)}\"}}");
            }
        }

        // ──────────────────────── Auth & Rate Limiting ────────────────────────

        private bool ValidateAuth(A2AHttpRequest request)
        {
            if (string.IsNullOrEmpty(_config.AuthToken))
            {
                return true; // Auth disabled.
            }

            if (request.Headers.TryGetValue("Authorization", out string authHeader))
            {
                return authHeader == $"Bearer {_config.AuthToken}";
            }

            return false;
        }

        private bool CheckRateLimit(string clientIp)
        {
            if (_config.RateLimitPerMinute <= 0) return true;

            DateTime now = DateTime.UtcNow;
            var entry = _rateLimits.AddOrUpdate(
                clientIp,
                _ => (now, 1),
                (_, existing) =>
                {
                    if ((now - existing.windowStart).TotalMinutes >= 1)
                    {
                        return (now, 1);
                    }
                    return (existing.windowStart, existing.count + 1);
                });

            return entry.count <= _config.RateLimitPerMinute;
        }

        // ──────────────────────── HTTP I/O ────────────────────────

        private async Task<A2AHttpRequest> ReadHttpRequestAsync(NetworkStream stream)
        {
            using (var headerBuffer = new MemoryStream())
            {
                byte[] readBuffer = new byte[4096];

                while (true)
                {
                    int read = await stream.ReadAsync(readBuffer, 0, readBuffer.Length);
                    if (read == 0)
                    {
                        return headerBuffer.Length == 0 ? null
                            : throw new IOException("Connection closed before HTTP request completed.");
                    }

                    headerBuffer.Write(readBuffer, 0, read);

                    int headerEnd = FindHeaderEnd(headerBuffer.GetBuffer(), (int)headerBuffer.Length);
                    if (headerEnd >= 0)
                    {
                        return ParseHttpRequest(stream, headerBuffer.ToArray(), headerEnd);
                    }

                    if (headerBuffer.Length > MaxHeaderBytes)
                    {
                        throw new InvalidDataException("HTTP headers exceeded the allowed limit.");
                    }
                }
            }
        }

        private A2AHttpRequest ParseHttpRequest(NetworkStream stream, byte[] requestBytes, int headerEndIndex)
        {
            string headerText = Encoding.ASCII.GetString(requestBytes, 0, headerEndIndex);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) throw new InvalidDataException("HTTP request line was missing.");

            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2) throw new InvalidDataException("HTTP request line was invalid.");

            string method = parts[0].Trim().ToUpperInvariant();
            string path = parts[1].Trim();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                int sep = line.IndexOf(':');
                if (sep <= 0) continue;
                headers[line.Substring(0, sep).Trim()] = line.Substring(sep + 1).Trim();
            }

            // Read body
            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out string clValue))
            {
                int.TryParse(clValue, out contentLength);
            }

            int bodyStart = headerEndIndex + 4;
            int buffered = Math.Max(0, requestBytes.Length - bodyStart);
            byte[] bodyBytes = Array.Empty<byte>();

            if (contentLength > 0)
            {
                bodyBytes = new byte[contentLength];
                int copyLen = Math.Min(buffered, contentLength);
                if (copyLen > 0) Array.Copy(requestBytes, bodyStart, bodyBytes, 0, copyLen);

                int total = copyLen;
                while (total < contentLength)
                {
                    int read = stream.Read(bodyBytes, total, contentLength - total);
                    if (read == 0) throw new IOException("Connection closed while reading body.");
                    total += read;
                }
            }

            string body = bodyBytes.Length > 0 ? Encoding.UTF8.GetString(bodyBytes) : string.Empty;

            return new A2AHttpRequest(method, path, headers, body);
        }

        private async Task WriteHttpResponseAsync(
            NetworkStream stream, int statusCode,
            string contentType = null, string body = "")
        {
            byte[] bodyBytes = string.IsNullOrEmpty(body)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(body);

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(statusCode).Append(' ')
              .Append(GetStatusDescription(statusCode)).Append("\r\n");
            sb.Append("Server: UnityLiveLinkA2A\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, Accept, Authorization\r\n");

            if (!string.IsNullOrEmpty(contentType))
            {
                sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            }

            sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
            }
            await stream.FlushAsync();
        }

        private Task WriteJsonResponseAsync(NetworkStream stream, int statusCode, object obj)
        {
            string json = JsonSerializer.Serialize(obj, s_jsonOptions);
            return WriteHttpResponseAsync(stream, statusCode, "application/json", json);
        }

        private Task WriteRawJsonResponseAsync(NetworkStream stream, int statusCode, string rawJson)
        {
            return WriteHttpResponseAsync(stream, statusCode, "application/json", rawJson);
        }

        private async Task WriteSseHeadersAsync(NetworkStream stream)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Server: UnityLiveLinkA2A\r\n");
            sb.Append("Content-Type: text/event-stream\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("Connection: keep-alive\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            await stream.FlushAsync();
        }

        private async Task WriteSseEventAsync(NetworkStream stream, string eventType, string data)
        {
            string payload = $"event: {eventType}\ndata: {data}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        // ──────────────────────── Helpers ────────────────────────

        private static string ExtractText(A2AMessage message)
        {
            if (message?.Parts == null) return string.Empty;

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

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static string GetStatusDescription(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 504: return "Gateway Timeout";
                default: return "OK";
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        // ──────────────────────── Internal types ────────────────────────

        private sealed class A2AHttpRequest
        {
            public string Method { get; }
            public string Path { get; }
            public Dictionary<string, string> Headers { get; }
            public string Body { get; }

            public A2AHttpRequest(string method, string path, Dictionary<string, string> headers, string body)
            {
                Method = method;
                Path = path;
                Headers = headers;
                Body = body;
            }
        }
    }
}
