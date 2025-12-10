using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LiveLink.Network
{
    /// <summary>
    /// WebSocket server that handles bidirectional communication with external clients.
    /// Uses System.Net.WebSockets for native .NET WebSocket support.
    /// </summary>
    public class LiveLinkServer : IDisposable
    {
        private HttpListener _httpListener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly List<WebSocket> _connectedClients = new List<WebSocket>();
        private readonly object _clientsLock = new object();
        private bool _isRunning = false;
        private int _port;

        /// <summary>
        /// Event fired when a message is received from any client.
        /// Called on a background thread - use MainThreadDispatcher to interact with Unity API.
        /// </summary>
        public event Action<string, WebSocket> OnMessageReceived;

        /// <summary>
        /// Event fired when a client connects.
        /// </summary>
        public event Action<WebSocket> OnClientConnected;

        /// <summary>
        /// Event fired when a client disconnects.
        /// </summary>
        public event Action<WebSocket> OnClientDisconnected;

        /// <summary>
        /// Event fired when an error occurs.
        /// </summary>
        public event Action<Exception> OnError;

        /// <summary>
        /// Gets the number of currently connected clients.
        /// </summary>
        public int ClientCount
        {
            get
            {
                lock (_clientsLock)
                {
                    return _connectedClients.Count;
                }
            }
        }

        /// <summary>
        /// Gets whether the server is currently running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets the port the server is listening on.
        /// </summary>
        public int Port => _port;

        /// <summary>
        /// Starts the WebSocket server on the specified port.
        /// </summary>
        /// <param name="port">The port to listen on.</param>
        public void StartServer(int port)
        {
            if (_isRunning)
            {
                Debug.LogWarning("[LiveLink] Server is already running.");
                return;
            }

            _port = port;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                _httpListener = new HttpListener();
                // Try wildcard first for external access
                _httpListener.Prefixes.Add($"http://+:{port}/");
                _httpListener.Start();
                _isRunning = true;

                Debug.Log($"[LiveLink] WebSocket server started on port {port}");

                // Start accepting connections
                Task.Run(() => AcceptConnectionsAsync(_cancellationTokenSource.Token));
            }
            catch (Exception)
            {
                // Fallback if wildcard fails (requires admin on Windows or port conflict)
                try
                {
                    _httpListener?.Close();
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://localhost:{port}/");
                    _httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _httpListener.Start();
                    _isRunning = true;

                    Debug.Log($"[LiveLink] WebSocket server started on port {port} (localhost only)");
                    Task.Run(() => AcceptConnectionsAsync(_cancellationTokenSource.Token));
                }
                catch (Exception innerEx)
                {
                    Debug.LogError($"[LiveLink] Failed to start server: {innerEx.Message}");
                    OnError?.Invoke(innerEx);
                }
            }
        }

        /// <summary>
        /// Stops the WebSocket server and disconnects all clients.
        /// </summary>
        public void StopServer()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cancellationTokenSource?.Cancel();

            // Close all client connections
            lock (_clientsLock)
            {
                foreach (var client in _connectedClients)
                {
                    try
                    {
                        if (client.State == WebSocketState.Open)
                        {
                            client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None).Wait(1000);
                        }
                        client.Dispose();
                    }
                    catch { }
                }
                _connectedClients.Clear();
            }

            try
            {
                _httpListener?.Stop();
                _httpListener?.Close();
            }
            catch { }

            _httpListener = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            Debug.Log("[LiveLink] WebSocket server stopped.");
        }

        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();

                    if (context.Request.IsWebSocketRequest)
                    {
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        var webSocket = wsContext.WebSocket;

                        lock (_clientsLock)
                        {
                            _connectedClients.Add(webSocket);
                        }

                        MainThreadDispatcher.EnqueueSafe(() =>
                        {
                            Debug.Log($"[LiveLink] Client connected. Total clients: {ClientCount}");
                        }, "OnClientConnected");

                        OnClientConnected?.Invoke(webSocket);

                        // Handle this client in a separate task
                        _ = Task.Run(() => HandleClientAsync(webSocket, cancellationToken));
                    }
                    else
                    {
                        // Return a simple HTTP response for non-WebSocket requests
                        context.Response.StatusCode = 200;
                        var responseBytes = Encoding.UTF8.GetBytes("LiveLink WebSocket Server. Connect via WebSocket protocol.");
                        context.Response.ContentLength64 = responseBytes.Length;
                        await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                        context.Response.Close();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Server was stopped
                    break;
                }
                catch (HttpListenerException)
                {
                    // Server was stopped
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        MainThreadDispatcher.EnqueueSafe(() =>
                        {
                            Debug.LogError($"[LiveLink] Error accepting connection: {ex.Message}");
                        }, "AcceptConnection");
                    }
                }
            }
        }

        private async Task HandleClientAsync(WebSocket webSocket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];

            try
            {
                while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var messageBuilder = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client requested close", CancellationToken.None);
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        }
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var message = messageBuilder.ToString();
                    if (!string.IsNullOrEmpty(message))
                    {
                        OnMessageReceived?.Invoke(message, webSocket);
                    }
                }
            }
            catch (WebSocketException)
            {
                // Client disconnected unexpectedly
            }
            catch (OperationCanceledException)
            {
                // Server is shutting down
            }
            catch (Exception ex)
            {
                MainThreadDispatcher.EnqueueSafe(() =>
                {
                    Debug.LogError($"[LiveLink] Error handling client: {ex.Message}");
                }, "HandleClient");
            }
            finally
            {
                RemoveClient(webSocket);
            }
        }

        private void RemoveClient(WebSocket webSocket)
        {
            lock (_clientsLock)
            {
                _connectedClients.Remove(webSocket);
            }

            try
            {
                webSocket.Dispose();
            }
            catch { }

            MainThreadDispatcher.EnqueueSafe(() =>
            {
                Debug.Log($"[LiveLink] Client disconnected. Total clients: {ClientCount}");
            }, "OnClientDisconnected");

            OnClientDisconnected?.Invoke(webSocket);
        }

        /// <summary>
        /// Sends a message to all connected clients.
        /// </summary>
        /// <param name="message">The message to broadcast.</param>
        public async Task BroadcastAsync(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            var buffer = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(buffer);

            List<WebSocket> clientsCopy;
            lock (_clientsLock)
            {
                clientsCopy = new List<WebSocket>(_connectedClients);
            }

            var deadClients = new List<WebSocket>();

            foreach (var client in clientsCopy)
            {
                try
                {
                    if (client.State == WebSocketState.Open)
                    {
                        await client.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else
                    {
                        deadClients.Add(client);
                    }
                }
                catch
                {
                    deadClients.Add(client);
                }
            }

            // Clean up dead clients
            foreach (var client in deadClients)
            {
                RemoveClient(client);
            }
        }

        /// <summary>
        /// Sends a message to a specific client.
        /// </summary>
        /// <param name="client">The WebSocket client to send to.</param>
        /// <param name="message">The message to send.</param>
        public async Task SendAsync(WebSocket client, string message)
        {
            if (client == null || string.IsNullOrEmpty(message)) return;

            try
            {
                if (client.State == WebSocketState.Open)
                {
                    var buffer = Encoding.UTF8.GetBytes(message);
                    await client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                MainThreadDispatcher.EnqueueSafe(() =>
                {
                    Debug.LogError($"[LiveLink] Error sending message: {ex.Message}");
                }, "SendAsync");
            }
        }

        /// <summary>
        /// Synchronous broadcast wrapper for use from main thread.
        /// </summary>
        /// <param name="message">The message to broadcast.</param>
        public void Broadcast(string message)
        {
            Task.Run(async () => await BroadcastAsync(message));
        }

        public void Dispose()
        {
            StopServer();
        }
    }
}
