using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LiveLink.Network;
using LiveLink.Tools;

namespace LiveLink
{
    internal sealed class McpHttpRequest
    {
        public string Method { get; }
        public string Path { get; }
        public Dictionary<string, string> Headers { get; }
        public Dictionary<string, string> Query { get; }
        public string Body { get; }

        public McpHttpRequest(
            string method,
            string path,
            Dictionary<string, string> headers,
            Dictionary<string, string> query,
            string body)
        {
            Method = method;
            Path = path;
            Headers = headers;
            Query = query;
            Body = body;
        }
    }

    internal sealed class McpSseConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private bool _isDisposed;

        public bool IsConnected => _client != null && _client.Connected && !_isDisposed;

        public McpSseConnection(TcpClient client, NetworkStream stream)
        {
            _client = client;
            _stream = stream;
        }

        public async Task SendEventAsync(string eventType, string data)
        {
            await SendRawAsync($"event: {eventType}\ndata: {data}\n\n");
        }

        public async Task SendCommentAsync(string comment)
        {
            await SendRawAsync($": {comment}\n\n");
        }

        private async Task SendRawAsync(string payload)
        {
            if (_isDisposed)
            {
                return;
            }

            byte[] buffer = Encoding.UTF8.GetBytes(payload);
            await _writeLock.WaitAsync();
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                await _stream.WriteAsync(buffer, 0, buffer.Length);
                await _stream.FlushAsync();
            }
            catch
            {
                Close();
                throw;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Close()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
        }

        public void Dispose()
        {
            Close();
        }
    }

    /// <summary>
    /// Represents an active MCP session tied to an SSE connection.
    /// </summary>
    internal class MCPSession
    {
        public string SessionId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public bool IsInitialized { get; set; }
        public McpSseConnection SseConnection { get; set; }
        public JObject ClientInfo { get; set; }

        public MCPSession(string sessionId, McpSseConnection sseConnection)
        {
            SessionId = sessionId;
            CreatedAt = DateTime.UtcNow;
            LastActivityAt = DateTime.UtcNow;
            IsInitialized = false;
            SseConnection = sseConnection;
        }

        public bool IsExpired(TimeSpan timeout)
        {
            return DateTime.UtcNow - LastActivityAt > timeout;
        }

        public void UpdateActivity()
        {
            LastActivityAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// HTTP server for MCP (Model Context Protocol) using HTTP + SSE transport.
    /// Uses raw sockets instead of HttpListener so it works on IL2CPP/mobile targets.
    /// </summary>
    public class MCPHttpServer : IDisposable
    {
        private const int MaxHeaderBytes = 32 * 1024;
        private const string SupportedMcpProtocolVersion = "2025-11-25";

        private TcpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly MCPToolHandler _mcpHandler;
        private readonly int _port;
        private bool _isRunning;
        private readonly Dictionary<string, MCPSession> _sessions = new Dictionary<string, MCPSession>();
        private readonly object _sessionsLock = new object();
        private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromSeconds(30);

        public bool IsRunning => _isRunning;
        public int Port => _port;
        public int ClientCount { get { lock (_sessionsLock) { return _sessions.Count; } } }

        public MCPHttpServer(MCPToolHandler mcpHandler, int port = 8081)
        {
            _mcpHandler = mcpHandler;
            _port = port;
        }

        public void Start()
        {
            if (_isRunning)
            {
                return;
            }

            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _isRunning = true;

                _cancellationTokenSource = new CancellationTokenSource();
                Task.Run(() => AcceptClientsAsync(_cancellationTokenSource.Token));
                Task.Run(() => SessionCleanupLoopAsync(_cancellationTokenSource.Token));

                Debug.Log($"[LiveLink-MCP] HTTP+SSE server started on port {_port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Failed to start HTTP server: {ex.Message}");
                _isRunning = false;
            }
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _cancellationTokenSource?.Cancel();

            List<MCPSession> sessionsToClose;
            lock (_sessionsLock)
            {
                sessionsToClose = _sessions.Values.ToList();
                _sessions.Clear();
            }

            foreach (var session in sessionsToClose)
            {
                try { session.SseConnection?.Close(); } catch { }
            }

            try
            {
                _listener?.Stop();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error stopping HTTP server: {ex.Message}");
            }

            Debug.Log("[LiveLink-MCP] HTTP server stopped");
        }

        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_isRunning)
                    {
                        break;
                    }

                    Debug.LogError($"[LiveLink-MCP] Error accepting client: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            bool keepConnectionOpen = false;
            NetworkStream stream = null;

            try
            {
                client.NoDelay = true;
                stream = client.GetStream();

                var request = await ReadHttpRequestAsync(stream);
                if (request == null)
                {
                    return;
                }

                if (request.Method == "OPTIONS")
                {
                    await WriteHttpResponseAsync(stream, 204);
                    return;
                }

                switch (request.Path)
                {
                    case "/mcp":
                    case "/mcp/":
                        if (request.Method == "POST")
                        {
                            await HandleMCPRequestAsync(request, stream);
                        }
                        else if (request.Method == "GET")
                        {
                            await WriteSseHeadersAsync(stream);
                            keepConnectionOpen = true;
                            await HandleSSEConnectionAsync(new McpSseConnection(client, stream), cancellationToken, false);
                        }
                        else
                        {
                            await WriteHttpResponseAsync(stream, 405);
                        }
                        break;

                    case "/sse":
                    case "/sse/":
                        if (request.Method == "GET")
                        {
                            await WriteSseHeadersAsync(stream);
                            keepConnectionOpen = true;
                            await HandleSSEConnectionAsync(new McpSseConnection(client, stream), cancellationToken, true);
                        }
                        else
                        {
                            await WriteHttpResponseAsync(stream, 405);
                        }
                        break;

                    case "/health":
                    case "/":
                        await WriteHttpResponseAsync(
                            stream,
                            200,
                            "application/json",
                            JsonConvert.SerializeObject(new
                            {
                                status = "ok",
                                protocol = "MCP",
                                version = "1.0",
                                transport = "HTTP+SSE"
                            }));
                        break;

                    default:
                        await WriteHttpResponseAsync(stream, 404);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error handling request: {ex.Message}");
                if (!keepConnectionOpen && stream != null && stream.CanWrite)
                {
                    try
                    {
                        await WriteHttpResponseAsync(stream, 500);
                    }
                    catch { }
                }
            }
            finally
            {
                if (!keepConnectionOpen)
                {
                    try { stream?.Close(); } catch { }
                    try { client?.Close(); } catch { }
                }
            }
        }

        private async Task HandleMCPRequestAsync(McpHttpRequest request, NetworkStream stream)
        {
            var mcpHeaders = new Dictionary<string, string>
            {
                { "MCP-Protocol-Version", SupportedMcpProtocolVersion }
            };

            try
            {
                string sessionId = null;
                request.Query?.TryGetValue("sessionId", out sessionId);
                LiveLinkToolConsumer consumer = ParseConsumer(request);
                bool usesLegacySseSession = !string.IsNullOrEmpty(sessionId);

                MCPSession session = null;
                if (usesLegacySseSession)
                {
                    lock (_sessionsLock)
                    {
                        if (_sessions.TryGetValue(sessionId, out session))
                        {
                            session.UpdateActivity();
                        }
                    }
                }

                string requestBody = request.Body ?? string.Empty;
                Debug.Log($"[LiveLink-MCP] Received request (Session: {sessionId ?? "none"}): {requestBody}");

                var mcpRequest = PacketSerializer.ParseMCPRequest(requestBody);
                if (mcpRequest == null)
                {
                    await WriteHttpResponseAsync(
                        stream,
                        400,
                        "application/json",
                        JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            id = (object)null,
                            error = new { code = -32700, message = "Parse error" }
                        }),
                        mcpHeaders);
                    return;
                }

                string method = mcpRequest.Method;
                bool requiresSession = usesLegacySseSession && method != "initialize";
                bool requiresInitialized = usesLegacySseSession && method != "initialize" && method != "notifications/initialized";

                if (requiresSession && session == null)
                {
                    await WriteHttpResponseAsync(
                        stream,
                        401,
                        "application/json",
                        JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            id = mcpRequest.Id,
                            error = new { code = -32001, message = "Session required. Connect to /sse first to obtain a sessionId." }
                        }),
                        mcpHeaders);
                    Debug.LogWarning($"[LiveLink-MCP] Rejected request without valid session: {method}");
                    return;
                }

                if (requiresInitialized && session != null && !session.IsInitialized)
                {
                    await WriteHttpResponseAsync(
                        stream,
                        403,
                        "application/json",
                        JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            id = mcpRequest.Id,
                            error = new { code = -32002, message = "Session not initialized. Send 'initialize' method first." }
                        }),
                        mcpHeaders);
                    Debug.LogWarning($"[LiveLink-MCP] Rejected request on uninitialized session: {method}");
                    return;
                }

                MCPResponse mcpResponse = null;
                {
                    var tcs = new TaskCompletionSource<MCPResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        _ = ProcessRequestOnMainThreadAsync(method, mcpRequest, session, sessionId, consumer, tcs);
                    });
                    mcpResponse = await tcs.Task;
                }

                if (mcpResponse == null)
                {
                    await WriteHttpResponseAsync(stream, 204, headers: mcpHeaders);
                    return;
                }

                string responseBody = PacketSerializer.Serialize(mcpResponse);
                await WriteHttpResponseAsync(stream, 200, "application/json", responseBody, mcpHeaders);
                Debug.Log($"[LiveLink-MCP] Sent response: {responseBody}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error handling MCP request: {ex.Message}");
                await WriteHttpResponseAsync(stream, 500, headers: mcpHeaders);
            }
        }

        private async Task HandleSSEConnectionAsync(McpSseConnection connection, CancellationToken cancellationToken, bool emitLegacyEndpointEvent)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            MCPSession session = null;

            try
            {
                session = new MCPSession(sessionId, connection);
                lock (_sessionsLock)
                {
                    _sessions[sessionId] = session;
                }

                Debug.Log($"[LiveLink-MCP] SSE client connected (Session: {sessionId})");

                if (emitLegacyEndpointEvent)
                {
                    string endpointUri = $"/mcp?sessionId={sessionId}";
                    await SendSSEEventAsync(connection, "endpoint", endpointUri);
                }

                while (_isRunning && !cancellationToken.IsCancellationRequested && connection.IsConnected)
                {
                    await Task.Delay(30000, cancellationToken);
                    if (_isRunning && connection.IsConnected)
                    {
                        await connection.SendCommentAsync("heartbeat");
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.Log($"[LiveLink-MCP] SSE client disconnected (Session: {sessionId}): {ex.Message}");
            }
            finally
            {
                if (session != null)
                {
                    lock (_sessionsLock)
                    {
                        _sessions.Remove(sessionId);
                    }
                    Debug.Log($"[LiveLink-MCP] Session {sessionId} removed (SSE disconnected)");
                }

                connection.Close();
            }
        }

        private async Task SessionCleanupLoopAsync(CancellationToken cancellationToken)
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_cleanupInterval, cancellationToken);

                    List<string> expiredSessions = new List<string>();
                    lock (_sessionsLock)
                    {
                        foreach (var kvp in _sessions)
                        {
                            if (kvp.Value.IsExpired(_sessionTimeout))
                            {
                                expiredSessions.Add(kvp.Key);
                            }
                        }

                        foreach (var sessionId in expiredSessions)
                        {
                            var session = _sessions[sessionId];
                            _sessions.Remove(sessionId);
                            try { session.SseConnection?.Close(); } catch { }
                        }
                    }

                    if (expiredSessions.Count > 0)
                    {
                        Debug.Log($"[LiveLink-MCP] Cleaned up {expiredSessions.Count} expired session(s)");
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiveLink-MCP] Error in session cleanup loop: {ex.Message}");
                }
            }
        }

        private async Task SendSSEEventAsync(McpSseConnection connection, string eventType, string data)
        {
            try
            {
                await connection.SendEventAsync(eventType, data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiveLink-MCP] Failed to send SSE event: {ex.Message}");
            }
        }

        public void BroadcastSSE(string eventType, string data)
        {
            List<MCPSession> activeSessions;
            lock (_sessionsLock)
            {
                activeSessions = _sessions.Values.ToList();
            }

            foreach (var session in activeSessions)
            {
                if (session.SseConnection != null)
                {
                    _ = SendSSEEventAsync(session.SseConnection, eventType, data);
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource?.Dispose();
            _listener = null;
        }

        private async Task ProcessRequestOnMainThreadAsync(
            string method,
            MCPRequest mcpRequest,
            MCPSession session,
            string sessionId,
            LiveLinkToolConsumer consumer,
            TaskCompletionSource<MCPResponse> tcs)
        {
            try
            {
                MCPResponse mcpResponse;
                using (LiveLinkMcpRequestContext.PushConsumer(consumer))
                {
                    mcpResponse = await _mcpHandler.HandleRequestAsync(mcpRequest);
                }

                MarkSessionInitializedIfNeeded(method, mcpRequest, mcpResponse, session, sessionId);
                tcs.TrySetResult(mcpResponse);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error processing request '{method}': {ex}");
                tcs.TrySetResult(new MCPResponse
                {
                    Id = mcpRequest?.Id,
                    Error = new MCPError { Code = -32603, Message = $"Internal error: {ex.Message}" }
                });
            }
        }

        private void MarkSessionInitializedIfNeeded(
            string method,
            MCPRequest mcpRequest,
            MCPResponse mcpResponse,
            MCPSession session,
            string sessionId)
        {
            if (method != "initialize" || mcpResponse == null || mcpResponse.Error != null || session == null)
            {
                return;
            }

            lock (_sessionsLock)
            {
                session.IsInitialized = true;
                if (mcpRequest.Params != null && mcpRequest.Params["clientInfo"] != null)
                {
                    session.ClientInfo = mcpRequest.Params["clientInfo"] as JObject;
                }
            }

            Debug.Log($"[LiveLink-MCP] Session {sessionId} initialized");
        }

        private static LiveLinkToolConsumer ParseConsumer(McpHttpRequest request)
        {
            if (request?.Headers == null)
            {
                return LiveLinkToolConsumer.External;
            }

            if (!request.Headers.TryGetValue("X-LiveLink-Consumer", out string consumerValue))
            {
                return LiveLinkToolConsumer.External;
            }

            if (string.Equals(consumerValue, "embedded-agent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(consumerValue, "embedded_agent", StringComparison.OrdinalIgnoreCase))
            {
                return LiveLinkToolConsumer.EmbeddedAgent;
            }

            return LiveLinkToolConsumer.External;
        }

        private async Task<McpHttpRequest> ReadHttpRequestAsync(NetworkStream stream)
        {
            using (var headerBuffer = new MemoryStream())
            {
                byte[] readBuffer = new byte[4096];

                while (true)
                {
                    int read = await stream.ReadAsync(readBuffer, 0, readBuffer.Length);
                    if (read == 0)
                    {
                        if (headerBuffer.Length == 0)
                        {
                            return null;
                        }

                        throw new IOException("Connection closed before the HTTP request completed.");
                    }

                    headerBuffer.Write(readBuffer, 0, read);

                    int headerEndIndex = FindHeaderEnd(headerBuffer.GetBuffer(), (int)headerBuffer.Length);
                    if (headerEndIndex >= 0)
                    {
                        return await ParseHttpRequestAsync(stream, headerBuffer.ToArray(), headerEndIndex);
                    }

                    if (headerBuffer.Length > MaxHeaderBytes)
                    {
                        throw new InvalidDataException("HTTP headers exceeded the allowed limit.");
                    }
                }
            }
        }

        private async Task<McpHttpRequest> ParseHttpRequestAsync(NetworkStream stream, byte[] requestBytes, int headerEndIndex)
        {
            string headerText = Encoding.ASCII.GetString(requestBytes, 0, headerEndIndex);
            string[] headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (headerLines.Length == 0)
            {
                throw new InvalidDataException("HTTP request line was missing.");
            }

            string[] requestLineParts = headerLines[0].Split(' ');
            if (requestLineParts.Length < 2)
            {
                throw new InvalidDataException("HTTP request line was invalid.");
            }

            string method = requestLineParts[0].Trim().ToUpperInvariant();
            string requestTarget = requestLineParts[1].Trim();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < headerLines.Length; i++)
            {
                string line = headerLines[i];
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                int separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, separatorIndex).Trim();
                string value = line.Substring(separatorIndex + 1).Trim();
                headers[name] = value;
            }

            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out string contentLengthValue))
            {
                int.TryParse(contentLengthValue, out contentLength);
            }

            if (headers.TryGetValue("Transfer-Encoding", out string transferEncoding) &&
                transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new NotSupportedException("Chunked transfer encoding is not supported by this transport.");
            }

            int bodyStartIndex = headerEndIndex + 4;
            int bufferedBodyLength = Math.Max(0, requestBytes.Length - bodyStartIndex);
            byte[] bodyBytes = Array.Empty<byte>();

            if (contentLength > 0)
            {
                bodyBytes = new byte[contentLength];
                int copyLength = Math.Min(bufferedBodyLength, contentLength);
                if (copyLength > 0)
                {
                    Array.Copy(requestBytes, bodyStartIndex, bodyBytes, 0, copyLength);
                }

                int totalBodyBytes = copyLength;
                while (totalBodyBytes < contentLength)
                {
                    int read = await stream.ReadAsync(bodyBytes, totalBodyBytes, contentLength - totalBodyBytes);
                    if (read == 0)
                    {
                        throw new IOException("Connection closed while reading the HTTP request body.");
                    }

                    totalBodyBytes += read;
                }
            }

            Uri requestUri = CreateRequestUri(requestTarget);
            string body = bodyBytes.Length > 0 ? Encoding.UTF8.GetString(bodyBytes) : string.Empty;

            return new McpHttpRequest(
                method,
                requestUri.AbsolutePath,
                headers,
                ParseQueryString(requestUri.Query),
                body);
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == '\r' &&
                    buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' &&
                    buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static Uri CreateRequestUri(string requestTarget)
        {
            if (string.IsNullOrEmpty(requestTarget) || requestTarget == "*")
            {
                return new Uri("http://localhost/");
            }

            if (requestTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                requestTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(requestTarget);
            }

            return new Uri($"http://localhost{requestTarget}");
        }

        private static Dictionary<string, string> ParseQueryString(string queryString)
        {
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(queryString))
            {
                return query;
            }

            string trimmed = queryString.TrimStart('?');
            if (string.IsNullOrEmpty(trimmed))
            {
                return query;
            }

            string[] pairs = trimmed.Split('&');
            foreach (var pair in pairs)
            {
                if (string.IsNullOrEmpty(pair))
                {
                    continue;
                }

                int separatorIndex = pair.IndexOf('=');
                string key;
                string value;

                if (separatorIndex >= 0)
                {
                    key = pair.Substring(0, separatorIndex);
                    value = pair.Substring(separatorIndex + 1);
                }
                else
                {
                    key = pair;
                    value = string.Empty;
                }

                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }

            return query;
        }

        private async Task WriteHttpResponseAsync(
            NetworkStream stream,
            int statusCode,
            string contentType = null,
            string body = "",
            Dictionary<string, string> headers = null)
        {
            byte[] bodyBytes = string.IsNullOrEmpty(body)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(body);

            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 ")
                .Append(statusCode)
                .Append(' ')
                .Append(GetStatusDescription(statusCode))
                .Append("\r\n");
            builder.Append("Server: UnityLiveLinkMCP\r\n");
            builder.Append("Access-Control-Allow-Origin: *\r\n");
            builder.Append("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
            builder.Append("Access-Control-Allow-Headers: Content-Type, Accept, Origin, MCP-Protocol-Version, MCP-Session-Id, Last-Event-ID, X-LiveLink-Consumer\r\n");

            if (!string.IsNullOrEmpty(contentType))
            {
                builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
            }

            if (headers != null)
            {
                foreach (var header in headers)
                {
                    builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }
            }

            builder.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            builder.Append("Connection: close\r\n");
            builder.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);

            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
            }

            await stream.FlushAsync();
        }

        private async Task WriteSseHeadersAsync(NetworkStream stream)
        {
            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 200 OK\r\n");
            builder.Append("Server: UnityLiveLinkMCP\r\n");
            builder.Append("Content-Type: text/event-stream\r\n");
            builder.Append("Cache-Control: no-cache\r\n");
            builder.Append("Connection: keep-alive\r\n");
            builder.Append("Access-Control-Allow-Origin: *\r\n");
            builder.Append("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
            builder.Append("Access-Control-Allow-Headers: Content-Type, Accept, Origin, MCP-Protocol-Version, MCP-Session-Id, Last-Event-ID, X-LiveLink-Consumer\r\n");
            builder.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            await stream.FlushAsync();
        }

        private static string GetStatusDescription(int statusCode)
        {
            switch (statusCode)
            {
                case 200:
                    return "OK";
                case 204:
                    return "No Content";
                case 400:
                    return "Bad Request";
                case 401:
                    return "Unauthorized";
                case 403:
                    return "Forbidden";
                case 404:
                    return "Not Found";
                case 405:
                    return "Method Not Allowed";
                case 500:
                    return "Internal Server Error";
                default:
                    return "OK";
            }
        }
    }
}
