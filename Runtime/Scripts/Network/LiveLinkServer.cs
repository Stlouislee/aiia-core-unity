using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LiveLink.Network
{
    public class WebSocketConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly string _id;
        private bool _isDisposed;

        public string Id => _id;
        public bool IsConnected => _client != null && _client.Connected && !_isDisposed;

        public WebSocketConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
            _id = Guid.NewGuid().ToString("N");
        }

        public async Task SendAsync(string message)
        {
            if (!IsConnected) return;
            byte[] payload = Encoding.UTF8.GetBytes(message);
            await SendFrameAsync(payload, 0x1); // Text frame
        }

        public async Task CloseAsync()
        {
            if (!IsConnected) return;
            try
            {
                await SendFrameAsync(new byte[0], 0x8); // Close frame
            }
            catch { }
            Dispose();
        }

        private async Task SendFrameAsync(byte[] payload, int opcode)
        {
            using (var ms = new MemoryStream())
            {
                byte b1 = (byte)(0x80 | (opcode & 0x0F)); // FIN = 1
                ms.WriteByte(b1);

                byte b2 = 0; // Mask = 0 (Server doesn't mask)
                if (payload.Length <= 125)
                {
                    b2 |= (byte)payload.Length;
                    ms.WriteByte(b2);
                }
                else if (payload.Length <= 65535)
                {
                    b2 |= 126;
                    ms.WriteByte(b2);
                    ms.WriteByte((byte)(payload.Length >> 8));
                    ms.WriteByte((byte)(payload.Length & 0xFF));
                }
                else
                {
                    b2 |= 127;
                    ms.WriteByte(b2);
                    // Write 64-bit length (big endian)
                    long len = payload.Length;
                    for (int i = 7; i >= 0; i--)
                    {
                        ms.WriteByte((byte)((len >> (i * 8)) & 0xFF));
                    }
                }

                ms.Write(payload, 0, payload.Length);
                byte[] frame = ms.ToArray();
                await _stream.WriteAsync(frame, 0, frame.Length);
            }
        }

        public NetworkStream Stream => _stream;
        public TcpClient Client => _client;

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
        }
    }

    public class LiveLinkServer : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly List<WebSocketConnection> _connectedClients = new List<WebSocketConnection>();
        private readonly object _clientsLock = new object();
        private bool _isRunning = false;
        private int _port;

        public event Action<string, WebSocketConnection> OnMessageReceived;
        public event Action<WebSocketConnection> OnClientConnected;
        public event Action<WebSocketConnection> OnClientDisconnected;
        public event Action<Exception> OnError;

        public int ClientCount
        {
            get { lock (_clientsLock) return _connectedClients.Count; }
        }

        public bool IsRunning => _isRunning;
        public int Port => _port;

        public void StartServer(int port)
        {
            if (_isRunning) return;

            _port = port;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _isRunning = true;

                Debug.Log($"[LiveLink] WebSocket server started on port {port}");
                Task.Run(() => AcceptConnectionsAsync(_cancellationTokenSource.Token));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink] Failed to start server: {ex.Message}");
                OnError?.Invoke(ex);
            }
        }

        public void StopServer()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cancellationTokenSource?.Cancel();

            lock (_clientsLock)
            {
                foreach (var client in _connectedClients)
                {
                    client.Dispose();
                }
                _connectedClients.Clear();
            }

            try { _listener?.Stop(); } catch { }
            Debug.Log("[LiveLink] WebSocket server stopped.");
        }

        private async Task AcceptConnectionsAsync(CancellationToken token)
        {
            while (_isRunning && !token.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleNewClientAsync(tcpClient, token));
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning) Debug.LogError($"[LiveLink] Accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleNewClientAsync(TcpClient tcpClient, CancellationToken token)
        {
            WebSocketConnection wsConnection = null;
            try
            {
                var stream = tcpClient.GetStream();
                if (await PerformHandshakeAsync(stream))
                {
                    wsConnection = new WebSocketConnection(tcpClient);
                    
                    lock (_clientsLock)
                    {
                        _connectedClients.Add(wsConnection);
                    }

                    MainThreadDispatcher.EnqueueSafe(() =>
                    {
                        Debug.Log($"[LiveLink] Client connected. Total: {ClientCount}");
                    }, "OnClientConnected");

                    OnClientConnected?.Invoke(wsConnection);

                    await ReadLoopAsync(wsConnection, token);
                }
                else
                {
                    tcpClient.Close();
                }
            }
            catch (Exception ex)
            {
                // Debug.LogError($"[LiveLink] Client error: {ex.Message}");
            }
            finally
            {
                if (wsConnection != null)
                {
                    RemoveClient(wsConnection);
                }
                else
                {
                    tcpClient.Close();
                }
            }
        }

        private async Task<bool> PerformHandshakeAsync(NetworkStream stream)
        {
            byte[] buffer = new byte[4096];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            if (Regex.IsMatch(request, "^GET", RegexOptions.IgnoreCase) && request.Contains("Upgrade: websocket"))
            {
                string swk = Regex.Match(request, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
                string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                byte[] swkaSha1 = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
                string swkaBase64 = Convert.ToBase64String(swkaSha1);

                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                  "Connection: Upgrade\r\n" +
                                  "Upgrade: websocket\r\n" +
                                  "Sec-WebSocket-Accept: " + swkaBase64 + "\r\n\r\n";

                byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                return true;
            }
            return false;
        }

        private async Task ReadLoopAsync(WebSocketConnection connection, CancellationToken token)
        {
            var stream = connection.Stream;
            byte[] header = new byte[2];

            while (connection.IsConnected && !token.IsCancellationRequested)
            {
                // Read header
                int read = await ReadExactlyAsync(stream, header, 2);
                if (read < 2) break;

                bool fin = (header[0] & 0x80) != 0;
                int opcode = header[0] & 0x0F;
                bool mask = (header[1] & 0x80) != 0;
                long payloadLen = header[1] & 0x7F;

                if (opcode == 0x8) break; // Close

                // Read extended length
                if (payloadLen == 126)
                {
                    byte[] lenBytes = new byte[2];
                    await ReadExactlyAsync(stream, lenBytes, 2);
                    // Big endian
                    payloadLen = (lenBytes[0] << 8) | lenBytes[1];
                }
                else if (payloadLen == 127)
                {
                    byte[] lenBytes = new byte[8];
                    await ReadExactlyAsync(stream, lenBytes, 8);
                    // Big endian (ignoring high bits for simplicity as we can't handle > 2GB anyway)
                    payloadLen = 0;
                    for(int i=0; i<8; i++) payloadLen = (payloadLen << 8) | lenBytes[i];
                }

                // Read mask
                byte[] maskKey = new byte[4];
                if (mask)
                {
                    await ReadExactlyAsync(stream, maskKey, 4);
                }

                // Read payload
                if (payloadLen > 0)
                {
                    byte[] payload = new byte[payloadLen];
                    await ReadExactlyAsync(stream, payload, (int)payloadLen);

                    if (mask)
                    {
                        for (int i = 0; i < payload.Length; i++)
                        {
                            payload[i] = (byte)(payload[i] ^ maskKey[i % 4]);
                        }
                    }

                    if (opcode == 0x1) // Text
                    {
                        string msg = Encoding.UTF8.GetString(payload);
                        OnMessageReceived?.Invoke(msg, connection);
                    }
                }
            }
        }

        private async Task<int> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer, total, count - total);
                if (read == 0) return total;
                total += read;
            }
            return total;
        }

        private void RemoveClient(WebSocketConnection client)
        {
            lock (_clientsLock)
            {
                _connectedClients.Remove(client);
            }
            
            MainThreadDispatcher.EnqueueSafe(() =>
            {
                Debug.Log($"[LiveLink] Client disconnected. Total: {ClientCount}");
            }, "OnClientDisconnected");

            OnClientDisconnected?.Invoke(client);
            client.Dispose();
        }

        public async Task BroadcastAsync(string message)
        {
            List<WebSocketConnection> clients;
            lock (_clientsLock) clients = new List<WebSocketConnection>(_connectedClients);
            
            foreach (var client in clients)
            {
                try { await client.SendAsync(message); } catch { }
            }
        }

        public void Broadcast(string message)
        {
            Task.Run(() => BroadcastAsync(message));
        }

        public async Task SendAsync(WebSocketConnection client, string message)
        {
            await client.SendAsync(message);
        }

        public void Dispose()
        {
            StopServer();
        }
    }
}
