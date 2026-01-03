using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using LiveLink.Network;

namespace LiveLink
{
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
        private readonly List<HttpListenerResponse> _sseClients = new List<HttpListenerResponse>();
        private readonly object _sseClientsLock = new object();

        public bool IsRunning => _isRunning;
        public int Port => _port;
        public int ClientCount { get { lock(_sseClientsLock) { return _sseClients.Count; } } }

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
            
            lock (_sseClientsLock)
            {
                _sseClients.Clear();
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
                catch (Exception ex)
                {
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
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                // Handle OPTIONS preflight
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
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
                        else
                        {
                            response.StatusCode = 405; // Method Not Allowed
                            response.Close();
                        }
                        break;

                    case "/sse":
                    case "/sse/":
                        if (request.HttpMethod == "GET")
                        {
                            await HandleSSEConnectionAsync(response);
                        }
                        else
                        {
                            response.StatusCode = 405;
                            response.Close();
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
                        response.StatusCode = 404;
                        response.Close();
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error handling request: {ex.Message}");
                try
                {
                    response.StatusCode = 500;
                    response.Close();
                }
                catch { }
            }
        }

        private async Task HandleMCPRequestAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // Add MCP protocol version header
                response.AddHeader("MCP-Protocol-Version", "2024-11-05");

                // Read request body
                string requestBody;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                Debug.Log($"[LiveLink-MCP] Received request: {requestBody}");

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

                // Process request on main thread
                MCPResponse mcpResponse = null;
                var tcs = new TaskCompletionSource<MCPResponse>();
                
                MainThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        mcpResponse = _mcpHandler.HandleRequest(mcpRequest);
                        tcs.SetResult(mcpResponse);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[LiveLink-MCP] Error processing request: {ex.Message}");
                        tcs.SetException(ex);
                    }
                });

                mcpResponse = await tcs.Task;

                // For notifications (like initialized), no response is sent
                if (mcpResponse == null)
                {
                    response.StatusCode = 204; // No Content
                    response.Close();
                    return;
                }

                // Send response
                response.ContentType = "application/json";
                response.StatusCode = 200;
                string responseBody = PacketSerializer.Serialize(mcpResponse);
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

        private async Task HandleSSEConnectionAsync(HttpListenerResponse response)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            try
            {
                // Set SSE headers
                response.ContentType = "text/event-stream";
                response.AddHeader("Cache-Control", "no-cache");
                response.AddHeader("Connection", "keep-alive");
                response.StatusCode = 200;

                lock (_sseClientsLock)
                {
                    _sseClients.Add(response);
                }

                Debug.Log($"[LiveLink-MCP] SSE client connected (Session: {sessionId})");

                // Send initial endpoint event as per MCP spec
                // The URI should be where the client sends POST requests
                string endpointUri = $"/mcp?sessionId={sessionId}";
                await SendSSEEventAsync(response, "endpoint", endpointUri);

                // Keep connection alive
                while (_isRunning)
                {
                    await Task.Delay(30000); // Send heartbeat every 30 seconds
                    if (_isRunning)
                    {
                        // Send a comment as heartbeat to keep connection alive without triggering events
                        string heartbeat = ": heartbeat\n\n";
                        byte[] buffer = Encoding.UTF8.GetBytes(heartbeat);
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                        await response.OutputStream.FlushAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[LiveLink-MCP] SSE client disconnected: {ex.Message}");
            }
            finally
            {
                lock (_sseClientsLock)
                {
                    _sseClients.Remove(response);
                }
                try { response.Close(); } catch { }
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
            List<HttpListenerResponse> clientsCopy;
            lock (_sseClientsLock)
            {
                clientsCopy = new List<HttpListenerResponse>(_sseClients);
            }

            foreach (var client in clientsCopy)
            {
                _ = SendSSEEventAsync(client, eventType, data);
            }
        }

        public void Dispose()
        {
            Stop();
            _cancellationTokenSource?.Dispose();
            _listener = null;
        }
    }
}
