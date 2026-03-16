using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    /// <summary>
    /// Represents an active MCP session tied to an SSE connection.
    /// </summary>
    internal class MCPSession
    {
        public SemaphoreSlim WriteLock { get; } = new SemaphoreSlim(1, 1);
        public string SessionId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public bool IsInitialized { get; set; }
        public HttpListenerResponse SseConnection { get; set; }
        public JObject ClientInfo { get; set; }

        public MCPSession(string sessionId, HttpListenerResponse sseConnection)
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
    /// Implements the official MCP specification for HTTP-based communication.
    /// </summary>
    public class MCPHttpServer : IDisposable
    {
        private HttpListener _listener;
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
        public int ClientCount { get { lock(_sessionsLock) { return _sessions.Count; } } }

        public MCPHttpServer(MCPToolHandler mcpHandler, int port = 8081)
        {
            _mcpHandler = mcpHandler;
            _port = port;
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                _isRunning = true;

                _cancellationTokenSource = new CancellationTokenSource();
                Task.Run(() => AcceptClientsAsync(_cancellationTokenSource.Token));
                Task.Run(() => SessionCleanupLoopAsync(_cancellationTokenSource.Token));

                Debug.Log($"[LiveLink-MCP] HTTP server started on port {_port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Failed to start HTTP server: {ex.Message}");
                _isRunning = false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cancellationTokenSource?.Cancel();
            
            lock (_sessionsLock)
            {
                _sessions.Clear();
            }

            try
            {
                _listener?.Stop();
                _listener?.Close();
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
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
                }
                catch (HttpListenerException)
                {
                    if (_isRunning)
                    {
                        Debug.LogWarning("[LiveLink-MCP] HTTP listener error");
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Listener was disposed during shutdown — expected, exit silently
                    break;
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break; // Shutting down, suppress errors
                    Debug.LogError($"[LiveLink-MCP] Error accepting client: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                // CORS headers
                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept, Origin, MCP-Protocol-Version, MCP-Session-Id, Last-Event-ID, X-LiveLink-Consumer");

                if (!IsAllowedOrigin(request))
                {
                    CloseEmptyResponse(response, 403);
                    return;
                }

                // Handle OPTIONS preflight
                if (request.HttpMethod == "OPTIONS")
                {
                    CloseEmptyResponse(response, 204);
                    return;
                }

                string path = request.Url.AbsolutePath;

                switch (path)
                {
                    case "/mcp":
                    case "/mcp/":
                        if (request.HttpMethod == "POST")
                        {
                            await HandleMCPRequestAsync(request, response);
                        }
                        else if (request.HttpMethod == "GET")
                        {
                            await HandleSSEConnectionAsync(request, response, emitLegacyEndpointEvent: false);
                        }
                        else
                        {
                            CloseEmptyResponse(response, 405);
                        }
                        break;

                    case "/sse":
                    case "/sse/":
                        if (request.HttpMethod == "GET")
                        {
                            await HandleSSEConnectionAsync(request, response, emitLegacyEndpointEvent: true);
                        }
                        else
                        {
                            CloseEmptyResponse(response, 405);
                        }
                        break;

                    case "/health":
                    case "/":
                        response.ContentType = "application/json";
                        response.StatusCode = 200;
                        byte[] healthData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                        {
                            status = "ok",
                            protocol = "MCP",
                            version = "1.0",
                            transport = "HTTP+SSE"
                        }));
                        await response.OutputStream.WriteAsync(healthData, 0, healthData.Length);
                        response.Close();
                        break;

                    default:
                        CloseEmptyResponse(response, 404);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error handling request: {ex.Message}");
                try
                {
                    CloseEmptyResponse(response, 500);
                }
                catch { }
            }
        }

        private async Task HandleMCPRequestAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // Add MCP protocol version header
                response.AddHeader("MCP-Protocol-Version", "2025-11-25");

                // Extract and validate sessionId from query string
                string sessionId = request.QueryString["sessionId"];
                MCPSession session = null;

                if (!string.IsNullOrEmpty(sessionId))
                {
                    lock (_sessionsLock)
                    {
                        if (_sessions.TryGetValue(sessionId, out session))
                        {
                            session.UpdateActivity();
                        }
                    }
                }

                // Read request body
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                Debug.Log($"[LiveLink-MCP] Received request (Session: {sessionId ?? "none"}): {requestBody}");

                // Parse MCP request
                var mcpRequest = PacketSerializer.ParseMCPRequest(requestBody);
                if (mcpRequest == null)
                {
                    response.StatusCode = 400;
                    byte[] errorData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = (object)null,
                        error = new { code = -32700, message = "Parse error" }
                    }));
                    await response.OutputStream.WriteAsync(errorData, 0, errorData.Length);
                    response.Close();
                    return;
                }

                // Session validation based on method
                string method = mcpRequest.Method;
                LiveLinkToolConsumer consumer = ParseConsumer(request);
                bool usesLegacySseSession = !string.IsNullOrEmpty(sessionId);
                bool requiresSession = usesLegacySseSession && method != "initialize";
                bool requiresInitialized = usesLegacySseSession && method != "initialize" && method != "notifications/initialized";

                if (requiresSession && session == null)
                {
                    response.StatusCode = 401;
                    byte[] errorData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = mcpRequest.Id,
                        error = new { code = -32001, message = "Session required. Connect to /sse first to obtain a sessionId." }
                    }));
                    await response.OutputStream.WriteAsync(errorData, 0, errorData.Length);
                    response.Close();
                    Debug.LogWarning($"[LiveLink-MCP] Rejected request without valid session: {method}");
                    return;
                }

                if (requiresInitialized && session != null && !session.IsInitialized)
                {
                    response.StatusCode = 403;
                    byte[] errorData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                    {
                        jsonrpc = "2.0",
                        id = mcpRequest.Id,
                        error = new { code = -32002, message = "Session not initialized. Send 'initialize' method first." }
                    }));
                    await response.OutputStream.WriteAsync(errorData, 0, errorData.Length);
                    response.Close();
                    Debug.LogWarning($"[LiveLink-MCP] Rejected request on uninitialized session: {method}");
                    return;
                }

                MCPResponse mcpResponse = null;

                // The MCP client handshake uses initialize before any Unity state access.
                // Handling it directly avoids depending on a frame tick during startup.
                if (method == "initialize" || method == "notifications/initialized")
                {
                    try
                    {
                        using (LiveLinkMcpRequestContext.PushConsumer(consumer))
                        {
                            mcpResponse = await _mcpHandler.HandleRequestAsync(mcpRequest);
                        }

                        MarkSessionInitializedIfNeeded(method, mcpRequest, mcpResponse, session, sessionId);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[LiveLink-MCP] Error processing request '{method}': {ex}");
                        mcpResponse = new MCPResponse
                        {
                            Id = mcpRequest?.Id,
                            Error = new MCPError { Code = -32603, Message = $"Internal error: {ex.Message}" }
                        };
                    }
                }
                else
                {
                    // Scene/resource/tool requests may touch Unity APIs, so those stay on the main thread.
                    var tcs = new TaskCompletionSource<MCPResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

                    MainThreadDispatcher.Enqueue(() =>
                    {
                        _ = ProcessRequestOnMainThreadAsync(method, mcpRequest, session, sessionId, consumer, tcs);
                    });

                    mcpResponse = await tcs.Task;
                }

                // For notifications (like initialized), no response is sent
                if (mcpResponse == null)
                {
                    CloseEmptyResponse(response, 202);
                    return;
                }

                string responseBody = PacketSerializer.Serialize(mcpResponse);

                if (usesLegacySseSession && session != null && session.SseConnection != null)
                {
                    await SendSSEMessageAsync(session, responseBody);
                    CloseEmptyResponse(response, 202);
                    Debug.Log($"[LiveLink-MCP] Sent SSE response: {responseBody}");
                    return;
                }

                response.ContentType = "application/json";
                response.StatusCode = 200;
                byte[] responseData = Encoding.UTF8.GetBytes(responseBody);
                await response.OutputStream.WriteAsync(responseData, 0, responseData.Length);
                response.Close();

                Debug.Log($"[LiveLink-MCP] Sent response: {responseBody}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error handling MCP request: {ex.Message}");
                response.StatusCode = 500;
                response.Close();
            }
        }

        private async Task HandleSSEConnectionAsync(HttpListenerRequest request, HttpListenerResponse response, bool emitLegacyEndpointEvent)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            MCPSession session = null;
            
            try
            {
                // Set SSE headers
                response.ContentType = "text/event-stream";
                response.AddHeader("Cache-Control", "no-cache");
                response.AddHeader("Connection", "keep-alive");
                response.StatusCode = 200;

                // Create and register session
                session = new MCPSession(sessionId, response);
                lock (_sessionsLock)
                {
                    _sessions[sessionId] = session;
                }

                Debug.Log($"[LiveLink-MCP] SSE client connected (Session: {sessionId})");

                // Send initial endpoint event as per MCP spec
                // The URI should be where the client sends POST requests
                if (emitLegacyEndpointEvent)
                {
                    string endpointUri = BuildSessionEndpointUri(request, sessionId);
                    Debug.Log($"[LiveLink-MCP] Sending endpoint event for session {sessionId}: {endpointUri}");
                    await SendSSEEventAsync(response, "endpoint", endpointUri);
                }

                // Keep connection alive
                while (_isRunning)
                {
                    await Task.Delay(30000); // Send heartbeat every 30 seconds
                    if (_isRunning)
                    {
                        await SendHeartbeatAsync(session);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[LiveLink-MCP] SSE client disconnected (Session: {sessionId}): {ex.Message}");
            }
            finally
            {
                // Clean up session when SSE connection closes
                if (session != null)
                {
                    lock (_sessionsLock)
                    {
                        _sessions.Remove(sessionId);
                    }
                    Debug.Log($"[LiveLink-MCP] Session {sessionId} removed (SSE disconnected)");
                }
                try { response.Close(); } catch { }
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

        private async Task SendSSEEventAsync(HttpListenerResponse response, string eventType, string data)
        {
            try
            {
                string message = $"event: {eventType}\ndata: {data}\n\n";
                byte[] buffer = Encoding.UTF8.GetBytes(message);
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                await response.OutputStream.FlushAsync();
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
                    _ = SendSessionEventAsync(session, eventType, data);
                }
            }
        }

        private static void CloseEmptyResponse(HttpListenerResponse response, int statusCode)
        {
            response.StatusCode = statusCode;
            response.ContentLength64 = 0;
            response.SendChunked = false;
            response.Close();
        }

        private static bool IsAllowedOrigin(HttpListenerRequest request)
        {
            string origin = request?.Headers["Origin"];
            if (string.IsNullOrWhiteSpace(origin))
            {
                return true;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri originUri))
            {
                return false;
            }

            return string.Equals(originUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(originUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }

        private async Task SendHeartbeatAsync(MCPSession session)
        {
            if (session == null || session.SseConnection == null)
            {
                return;
            }

            await session.WriteLock.WaitAsync();
            try
            {
                string heartbeat = ": heartbeat\n\n";
                byte[] buffer = Encoding.UTF8.GetBytes(heartbeat);
                await session.SseConnection.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                await session.SseConnection.OutputStream.FlushAsync();
            }
            finally
            {
                session.WriteLock.Release();
            }
        }

        private Task SendSSEMessageAsync(MCPSession session, string data)
        {
            if (session == null || session.SseConnection == null)
            {
                throw new InvalidOperationException("Cannot send an SSE message without an active session.");
            }

            return SendSessionEventAsync(session, "message", data);
        }

        private async Task SendSessionEventAsync(MCPSession session, string eventType, string data)
        {
            await session.WriteLock.WaitAsync();
            try
            {
                await SendSSEEventAsync(session.SseConnection, eventType, data);
            }
            finally
            {
                session.WriteLock.Release();
            }
        }

        private async Task ProcessRequestOnMainThreadAsync(string method, MCPRequest mcpRequest, MCPSession session, string sessionId, LiveLinkToolConsumer consumer, TaskCompletionSource<MCPResponse> tcs)
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
                Debug.LogError($"[LiveLink-MCP] Error processing request '{method}' on main thread: {ex}");
                tcs.TrySetResult(new MCPResponse
                {
                    Id = mcpRequest?.Id,
                    Error = new MCPError { Code = -32603, Message = $"Internal error: {ex.Message}" }
                });
            }
        }

        private void MarkSessionInitializedIfNeeded(string method, MCPRequest mcpRequest, MCPResponse mcpResponse, MCPSession session, string sessionId)
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

        private string BuildSessionEndpointUri(HttpListenerRequest request, string sessionId)
        {
            Uri requestUrl = request != null ? request.Url : null;
            if (requestUrl == null)
            {
                return $"/mcp?sessionId={sessionId}";
            }

            string authority = requestUrl.GetLeftPart(UriPartial.Authority);
            return $"{authority}/mcp?sessionId={sessionId}";
        }

        private static LiveLinkToolConsumer ParseConsumer(HttpListenerRequest request)
        {
            if (request == null)
            {
                return LiveLinkToolConsumer.External;
            }

            string consumerHeader = request.Headers["X-LiveLink-Consumer"];
            if (string.Equals(consumerHeader, "embedded-agent", StringComparison.OrdinalIgnoreCase))
            {
                return LiveLinkToolConsumer.EmbeddedAgent;
            }

            return LiveLinkToolConsumer.External;
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource?.Dispose();
            _listener = null;
        }
    }
}
