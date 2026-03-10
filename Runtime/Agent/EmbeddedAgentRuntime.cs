using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using UnityEngine;
using UnityEngine.Events;

namespace LiveLink.Agent
{
    /// <summary>
    /// Hosts Microsoft Agent Framework inside the Unity application.
    /// The embedded agent consumes the first-party LiveLink MCP server by default,
    /// plus any downstream MCP servers configured by the user.
    /// </summary>
    [AddComponentMenu("LiveLink/Embedded Agent Runtime")]
    public class EmbeddedAgentRuntime : MonoBehaviour
    {
        private const int LocalServerReadinessTimeoutSeconds = 3;
        private const int LocalServerReadinessPollIntervalMs = 100;
        private const int ToolDiscoveryTimeoutSeconds = 15;
        private const int AgentSessionInitializationTimeoutSeconds = 30;

        [Serializable]
        public sealed class AgentTextEvent : UnityEvent<string>
        {
        }

        [SerializeField]
        private AgentRuntimeConfig _config;

        [SerializeField]
        private LiveLinkManager _liveLinkManager;

        [SerializeField]
        private bool _autoInitialize = true;

        [SerializeField]
        private bool _createSessionOnInitialize = true;

        [SerializeField]
        private bool _persistAcrossScenes = true;

        [Header("Events")]
        [SerializeField]
        private AgentTextEvent _onResponseReceived = new AgentTextEvent();

        [SerializeField]
        private AgentTextEvent _onError = new AgentTextEvent();

        [SerializeField]
        private AgentTextEvent _onStatusChanged = new AgentTextEvent();

        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _runLock = new SemaphoreSlim(1, 1);
        private readonly List<ConnectedMcpServer> _connectedServers = new List<ConnectedMcpServer>();
        private readonly List<string> _availableToolNames = new List<string>();

        private AIAgent _agent;
        private AgentSession _session;
        private bool _isInitialized;
        private bool _isBusy;
        private string _status = "Idle";
        private string _lastResponse;
        private string _lastError;

        private sealed class ConnectedMcpServer
        {
            public string DisplayName;
            public bool IsLocal;
            public McpClient Client;
            public List<AITool> Tools = new List<AITool>();
            public List<string> ToolNames = new List<string>();
            public string ServerInstructions;
            public string ServerName;
            public string ServerVersion;
        }

        public AgentRuntimeConfig Config => _config;
        public LiveLinkManager LiveLinkManager => _liveLinkManager;
        public bool IsInitialized => _isInitialized;
        public bool IsBusy => _isBusy;
        public string Status => _status;
        public string LastResponse => _lastResponse;
        public string LastError => _lastError;
        public int ConnectedServerCount => _connectedServers.Count;
        public IReadOnlyList<string> AvailableToolNames => _availableToolNames.AsReadOnly();

        private void Awake()
        {
            MainThreadDispatcher.Initialize();

            if (_persistAcrossScenes)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Start()
        {
            if (_autoInitialize)
            {
                RunBackgroundTask(() => InitializeAsync());
            }
        }

        private void OnDestroy()
        {
            _ = ShutdownAsync();
        }

        public void InitializeRuntime()
        {
            RunBackgroundTask(() => InitializeAsync());
        }

        public void ReinitializeRuntime()
        {
            RunBackgroundTask(() => ReinitializeAsync());
        }

        public async Task ReinitializeAsync()
        {
            await ShutdownAsync().ConfigureAwait(false);
            await InitializeAsync().ConfigureAwait(false);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isInitialized)
                {
                    return;
                }

                if (_config == null)
                {
                    throw new InvalidOperationException("EmbeddedAgentRuntime is missing an AgentRuntimeConfig.");
                }

                SetStatus("Initializing agent runtime...");
                _lastError = string.Empty;

                string apiKey = _config.ResolveOpenAIApiKey();
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException(
                        "No OpenAI API key was configured. Set one on the AgentRuntimeConfig asset or provide it through the configured environment variable.");
                }

                ResolveLiveLinkManagerReference();
                _connectedServers.Clear();
                _availableToolNames.Clear();

                if (_config.EnableLocalLiveLinkMcp)
                {
                    SetStatus("Connecting to local LiveLink MCP...");
                    _connectedServers.Add(await ConnectLocalLiveLinkServerAsync(cancellationToken).ConfigureAwait(false));
                    SetStatus("Local LiveLink MCP connected.");
                }

                var warnings = new List<string>();
                IReadOnlyList<AgentExternalMcpServerConfig> externalConfigs = _config.ExternalMcpServers;
                for (int i = 0; i < externalConfigs.Count; i++)
                {
                    AgentExternalMcpServerConfig serverConfig = externalConfigs[i];
                    if (serverConfig == null || !serverConfig.Enabled)
                    {
                        continue;
                    }

                    try
                    {
                        SetStatus(string.Format("Connecting external MCP: {0}...", serverConfig.DisplayName));
                        ConnectedMcpServer connectedServer = await ConnectExternalServerAsync(serverConfig, cancellationToken).ConfigureAwait(false);
                        _connectedServers.Add(connectedServer);
                        SetStatus(string.Format("Connected external MCP: {0}", serverConfig.DisplayName));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(string.Format("Failed to connect MCP server '{0}': {1}", serverConfig.DisplayName, ex.Message));
                    }
                }

                SetStatus("Preparing agent tools...");
                List<AITool> tools = BuildToolList(warnings);
                string instructions = BuildInstructions(warnings);

                SetStatus("Creating OpenAI chat client...");
                var openAiClient = new OpenAIClient(apiKey);
#pragma warning disable OPENAI001
                IChatClient chatClient = openAiClient.GetChatClient(_config.OpenAIModel).AsIChatClient();
#pragma warning restore OPENAI001
                SetStatus("Creating embedded agent...");
                _agent = chatClient.AsAIAgent(
                    instructions: instructions,
                    name: string.IsNullOrWhiteSpace(_config.AgentName) ? "LiveLink Agent" : _config.AgentName,
                    tools: tools.ToArray());

                if (_createSessionOnInitialize)
                {
                    SetStatus("Creating agent session...");
                    _session = await WithTimeout(
                        _agent.CreateSessionAsync().AsTask(),
                        TimeSpan.FromSeconds(AgentSessionInitializationTimeoutSeconds),
                        "Timed out while creating the embedded agent session. Check model connectivity and API key configuration.")
                        .ConfigureAwait(false);
                }

                _isInitialized = true;
                SetStatus(string.Format("Ready. Connected {0} MCP server(s).", _connectedServers.Count));
            }
            catch (Exception ex)
            {
                _lastError = ex.ToString();
                Debug.LogError(string.Format("[LiveLink-Agent] Initialization failed: {0}", _lastError));
                SetStatus("Agent initialization failed.");
                DispatchToMainThread(() => _onError.Invoke(_lastError));
                await DisposeConnectionsAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        public void ResetSession()
        {
            _ = ResetSessionAsync();
        }

        public async Task ResetSessionAsync()
        {
            if (_agent == null)
            {
                return;
            }

            _session = await _agent.CreateSessionAsync().ConfigureAwait(false);
            SetStatus("Agent session reset.");
        }

        public void SubmitMessage(string message)
        {
            _ = SendMessageAsync(message);
        }

        public new void SendMessage(string message)
        {
            SubmitMessage(message);
        }

        public async Task<string> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("Message cannot be empty.", nameof(message));
            }

            if (!_isInitialized)
            {
                await InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _isBusy = true;
                SetStatus("Running agent...");

                if (_session == null)
                {
                    _session = await _agent.CreateSessionAsync().ConfigureAwait(false);
                }

                var response = await _agent.RunAsync(message, _session).ConfigureAwait(false);
                _lastResponse = response != null ? response.ToString() : string.Empty;
                SetStatus("Response received.");
                DispatchToMainThread(() => _onResponseReceived.Invoke(_lastResponse));
                return _lastResponse;
            }
            catch (Exception ex)
            {
                _lastError = ex.ToString();
                Debug.LogError(string.Format("[LiveLink-Agent] Request failed: {0}", _lastError));
                SetStatus("Agent request failed.");
                DispatchToMainThread(() => _onError.Invoke(_lastError));
                throw;
            }
            finally
            {
                _isBusy = false;
                _runLock.Release();
            }
        }

        public async Task ShutdownAsync()
        {
            _isInitialized = false;
            _agent = null;
            _session = null;
            _availableToolNames.Clear();
            await DisposeConnectionsAsync().ConfigureAwait(false);
            SetStatus("Stopped.");
        }

        private async Task<ConnectedMcpServer> ConnectLocalLiveLinkServerAsync(CancellationToken cancellationToken)
        {
            if (_liveLinkManager == null)
            {
                throw new InvalidOperationException("No LiveLinkManager was found. Assign one to the EmbeddedAgentRuntime or place a LiveLinkManager in the scene.");
            }

            if (_config.AutoStartLocalLiveLinkMcp && !_liveLinkManager.IsMCPServerRunning)
            {
                DispatchToMainThread(_liveLinkManager.StartMCPServer);
                await WaitForLocalServerAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_liveLinkManager.IsMCPServerRunning)
            {
                throw new InvalidOperationException("The local LiveLink MCP server is not running.");
            }

            SetStatus("Waiting for local LiveLink MCP readiness...");
            await WaitForLocalServerReadinessAsync(cancellationToken).ConfigureAwait(false);

            AgentMcpHttpTransportMode localTransportMode = GetEffectiveLocalTransportMode();
            string localEndpoint = GetLocalEndpoint(localTransportMode);
            float localTimeoutSeconds = GetEffectiveLocalConnectionTimeoutSeconds();
            McpClient client = await AgentMcpClientFactory.CreateLocalClientAsync(
                localEndpoint,
                localTransportMode,
                localTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            return await CreateConnectedServerAsync("LiveLink MCP", true, client, null).ConfigureAwait(false);
        }

        private async Task<ConnectedMcpServer> ConnectExternalServerAsync(AgentExternalMcpServerConfig config, CancellationToken cancellationToken)
        {
            McpClient client = await AgentMcpClientFactory.CreateExternalClientAsync(config, cancellationToken).ConfigureAwait(false);
            return await CreateConnectedServerAsync(config.DisplayName, false, client, config).ConfigureAwait(false);
        }

        private async Task<ConnectedMcpServer> CreateConnectedServerAsync(string displayName, bool isLocal, McpClient client, AgentExternalMcpServerConfig config)
        {
            var connectedServer = new ConnectedMcpServer
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? "MCP Server" : displayName,
                IsLocal = isLocal,
                Client = client,
                ServerInstructions = client.ServerInstructions,
                ServerName = client.ServerInfo.Name,
                ServerVersion = client.ServerInfo.Version
            };

            IList<McpClientTool> discoveredTools = await WithTimeout(
                client.ListToolsAsync(cancellationToken: CancellationToken.None).AsTask(),
                TimeSpan.FromSeconds(ToolDiscoveryTimeoutSeconds),
                string.Format("Timed out while listing tools from MCP server '{0}'.", connectedServer.DisplayName))
                .ConfigureAwait(false);

            foreach (AITool tool in discoveredTools.Cast<AITool>())
            {
                if (isLocal && !_config.AllowSceneMutationTools && AgentToolNames.IsSceneMutationTool(tool.Name))
                {
                    continue;
                }

                if (!isLocal && config != null && !config.IsToolAllowed(tool.Name))
                {
                    continue;
                }

                connectedServer.Tools.Add(tool);
                connectedServer.ToolNames.Add(tool.Name);
            }

            connectedServer.ToolNames.Sort(StringComparer.OrdinalIgnoreCase);
            return connectedServer;
        }

        private List<AITool> BuildToolList(List<string> warnings)
        {
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tools = new List<AITool>();

            for (int i = 0; i < _connectedServers.Count; i++)
            {
                ConnectedMcpServer server = _connectedServers[i];
                for (int toolIndex = 0; toolIndex < server.Tools.Count; toolIndex++)
                {
                    AITool tool = server.Tools[toolIndex];
                    if (tool == null)
                    {
                        continue;
                    }

                    if (!seenNames.Add(tool.Name))
                    {
                        warnings.Add(string.Format(
                            "Skipped duplicate tool '{0}' from server '{1}'.",
                            tool.Name,
                            server.DisplayName));
                        continue;
                    }

                    tools.Add(tool);
                    _availableToolNames.Add(tool.Name);
                }
            }

            _availableToolNames.Sort(StringComparer.OrdinalIgnoreCase);
            return tools;
        }

        private string BuildInstructions(List<string> warnings)
        {
            var builder = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(_config.SystemInstructions))
            {
                builder.AppendLine(_config.SystemInstructions.Trim());
                builder.AppendLine();
            }

            if (_config.EnableLocalLiveLinkMcp)
            {
                builder.AppendLine("The local LiveLink MCP server exposes the current Unity scene and scene-editing tools.");
                builder.AppendLine("Use those first-party tools to inspect the scene before making assumptions.");
                if (!_config.AllowSceneMutationTools)
                {
                    builder.AppendLine("Scene mutation tools are disabled for this session.");
                }
                builder.AppendLine();
            }

            if (_connectedServers.Count > 0)
            {
                builder.AppendLine("Connected MCP servers:");
                for (int i = 0; i < _connectedServers.Count; i++)
                {
                    ConnectedMcpServer server = _connectedServers[i];
                    builder.Append("- ");
                    builder.Append(server.DisplayName);
                    if (!string.IsNullOrWhiteSpace(server.ServerName))
                    {
                        builder.Append(" [");
                        builder.Append(server.ServerName);
                        if (!string.IsNullOrWhiteSpace(server.ServerVersion))
                        {
                            builder.Append(" ");
                            builder.Append(server.ServerVersion);
                        }
                        builder.Append("]");
                    }
                    builder.AppendLine();

                    if (!string.IsNullOrWhiteSpace(server.ServerInstructions))
                    {
                        builder.AppendLine(server.ServerInstructions.Trim());
                    }
                }

                builder.AppendLine();
            }

            if (warnings != null && warnings.Count > 0)
            {
                builder.AppendLine("Connection notes:");
                for (int i = 0; i < warnings.Count; i++)
                {
                    builder.Append("- ");
                    builder.AppendLine(warnings[i]);
                }
            }

            return builder.ToString().Trim();
        }

        private void ResolveLiveLinkManagerReference()
        {
            if (_liveLinkManager != null)
            {
                return;
            }

            _liveLinkManager = FindLiveLinkManagerInScene();
        }

        private static LiveLinkManager FindLiveLinkManagerInScene()
        {
#if UNITY_2022_2_OR_NEWER
            return FindAnyObjectByType<LiveLinkManager>();
#else
            return FindObjectOfType<LiveLinkManager>();
#endif
        }

        private async Task WaitForLocalServerAsync(CancellationToken cancellationToken)
        {
            const int maxAttempts = 30;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_liveLinkManager != null && _liveLinkManager.IsMCPServerRunning)
                {
                    return;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for the local LiveLink MCP server to start.");
        }

        private async Task WaitForLocalServerReadinessAsync(CancellationToken cancellationToken)
        {
            Uri healthUri = BuildLocalHealthUri();
            DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(LocalServerReadinessTimeoutSeconds);
            Exception lastException = null;

            while (DateTime.UtcNow < deadlineUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (await ProbeLocalServerHealthAsync(healthUri).ConfigureAwait(false))
                    {
                        Debug.Log($"[LiveLink-Agent] Local LiveLink MCP is healthy: {healthUri}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                await Task.Delay(LocalServerReadinessPollIntervalMs, cancellationToken).ConfigureAwait(false);
            }

            if (lastException != null)
            {
                throw new TimeoutException(
                    $"Local LiveLink MCP did not become healthy within {LocalServerReadinessTimeoutSeconds} seconds ({healthUri}). Last error: {lastException.Message}",
                    lastException);
            }

            throw new TimeoutException(
                $"Local LiveLink MCP did not become healthy within {LocalServerReadinessTimeoutSeconds} seconds ({healthUri}).");
        }

        private Uri BuildLocalHealthUri()
        {
            Uri sseUri = new Uri(_liveLinkManager.LocalMCPHttpBaseUrl);
            var builder = new UriBuilder(sseUri)
            {
                Path = "/health",
                Query = string.Empty
            };
            return builder.Uri;
        }

        private string GetLocalEndpoint(AgentMcpHttpTransportMode transportMode)
        {
            return transportMode == AgentMcpHttpTransportMode.Sse
                ? _liveLinkManager.LocalMCPSseEndpoint
                : _liveLinkManager.LocalMCPEndpoint;
        }

        private AgentMcpHttpTransportMode GetEffectiveLocalTransportMode()
        {
            if (_config.LocalHttpTransportMode == AgentMcpHttpTransportMode.Sse)
            {
                Debug.LogWarning(
                    "[LiveLink-Agent] Legacy SSE transport is fragile with the current MCP C# SDK inside Unity. " +
                    "Using StreamableHttp against the local /mcp endpoint instead.");
                return AgentMcpHttpTransportMode.StreamableHttp;
            }

            return _config.LocalHttpTransportMode;
        }

        private static async Task<bool> ProbeLocalServerHealthAsync(Uri healthUri)
        {
            HttpWebRequest request = WebRequest.CreateHttp(healthUri);
            request.Method = "GET";
            request.Timeout = 500;
            request.ReadWriteTimeout = 500;

            using (WebResponse response = await request.GetResponseAsync().ConfigureAwait(false))
            {
                if (!(response is HttpWebResponse httpResponse))
                {
                    return false;
                }

                return (int)httpResponse.StatusCode >= 200 && (int)httpResponse.StatusCode < 300;
            }
        }

        private float GetEffectiveLocalConnectionTimeoutSeconds()
        {
            float configuredTimeout = Mathf.Max(1f, _config.LocalConnectionTimeoutSeconds);

#if UNITY_EDITOR
            float effectiveTimeout = Mathf.Clamp(configuredTimeout, 3f, 5f);
            if (!Mathf.Approximately(effectiveTimeout, configuredTimeout))
            {
                Debug.Log(
                    $"[LiveLink-Agent] Clamping local MCP connection timeout from {configuredTimeout:0.##}s to {effectiveTimeout:0.##}s in the Unity Editor for faster debugging.");
            }
            return effectiveTimeout;
#else
            return configuredTimeout;
#endif
        }

        private async Task DisposeConnectionsAsync()
        {
            for (int i = _connectedServers.Count - 1; i >= 0; i--)
            {
                ConnectedMcpServer server = _connectedServers[i];
                if (server != null && server.Client != null)
                {
                    try
                    {
                        await server.Client.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(string.Format("[LiveLink-Agent] Failed to dispose MCP client '{0}': {1}", server.DisplayName, ex.Message));
                    }
                }
            }

            _connectedServers.Clear();
        }

        private void SetStatus(string status)
        {
            _status = status;
            DispatchToMainThread(() => _onStatusChanged.Invoke(_status));
        }

        private static void DispatchToMainThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (MainThreadDispatcher.IsMainThread)
            {
                action();
                return;
            }

            try
            {
                MainThreadDispatcher.Enqueue(action);
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("[LiveLink-Agent] Failed to enqueue main-thread action: {0}", ex));
            }
        }

        private void RunBackgroundTask(Func<Task> taskFactory)
        {
            if (taskFactory == null)
            {
                return;
            }

            _ = RunBackgroundTaskInternal(taskFactory);
        }

        private async Task RunBackgroundTaskInternal(Func<Task> taskFactory)
        {
            try
            {
                await taskFactory().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("[LiveLink-Agent] Background task failed: {0}", ex));
            }
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string timeoutMessage)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            Task completedTask = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completedTask != task)
            {
                throw new TimeoutException(timeoutMessage);
            }

            return await task.ConfigureAwait(false);
        }
    }
}
