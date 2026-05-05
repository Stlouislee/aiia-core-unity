using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using LiveLink.Agent.A2A;
using ModelContextProtocol.Client;
using OpenAI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

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

        [Serializable]
        public sealed class AgentToolCallEvent : UnityEvent<string, string>
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
        [FormerlySerializedAs("_onResponseReceived")]
        public AgentTextEvent OnResponseReceived = new AgentTextEvent();

        [FormerlySerializedAs("_onError")]
        public AgentTextEvent OnError = new AgentTextEvent();

        [FormerlySerializedAs("_onStatusChanged")]
        public AgentTextEvent OnStatusChanged = new AgentTextEvent();

        public AgentToolCallEvent OnToolCall = new AgentToolCallEvent();

        /// <summary>Fired when an MCP server connection is lost.</summary>
        public AgentTextEvent OnConnectionLost = new AgentTextEvent();

        /// <summary>Fired when an MCP server connection is restored after reconnection.</summary>
        public AgentTextEvent OnConnectionRestored = new AgentTextEvent();

        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _runLock = new SemaphoreSlim(1, 1);
        private readonly List<ConnectedMcpServer> _connectedServers = new List<ConnectedMcpServer>();
        private readonly List<ConnectedA2AAgent> _connectedA2AAgents = new List<ConnectedA2AAgent>();
        private readonly List<string> _availableToolNames = new List<string>();

        private AIAgent _agent;
        private AgentSession _session;
        private A2AHostServer _a2aHostServer;
        private CancellationTokenSource _heartbeatCts;
        private volatile bool _isInitialized;
        private volatile bool _isBusy;
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
            public AgentExternalMcpServerConfig ExternalConfig; // stored for reconnection
        }

        private sealed class ConnectedA2AAgent
        {
            public string DisplayName;
            public A2AClient Client;
            public A2AAgentCard AgentCard;
            public A2AAgentToolWrapper Tool;
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
                RunBackgroundTask(() => InitializeWithRetryAsync());
            }
        }

        private void OnDestroy()
        {
            _ = ShutdownAsync();
        }

        public void InitializeRuntime()
        {
            RunBackgroundTask(() => InitializeWithRetryAsync());
        }

        public void ReinitializeRuntime()
        {
            RunBackgroundTask(() => ReinitializeAsync());
        }

        public async Task ReinitializeAsync()
        {
            await ShutdownAsync().ConfigureAwait(false);
            await InitializeWithRetryAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Initialize with exponential backoff retry. Retries up to <paramref name="maxRetries"/> times.
        /// </summary>
        private async Task InitializeWithRetryAsync(int maxRetries = 3)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    await InitializeAsync().ConfigureAwait(false);
                    return; // Success
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    int delayMs = 1000 * (1 << attempt); // 1s, 2s, 4s
                    SetStatus(string.Format("Initialization attempt {0} failed: {1}. Retrying in {2}ms...",
                        attempt + 1, ex.Message, delayMs));

                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }
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

                await ResolveLiveLinkManagerReferenceAsync(cancellationToken).ConfigureAwait(false);
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

                // Connect to remote A2A agents (OpenClaw, Hermes, etc.)
                if (_config.RemoteA2AAgents != null)
                {
                    for (int i = 0; i < _config.RemoteA2AAgents.Count; i++)
                    {
                        AgentA2ARemoteConfig a2aConfig = _config.RemoteA2AAgents[i];
                        if (a2aConfig == null || !a2aConfig.Enabled)
                        {
                            continue;
                        }

                        try
                        {
                            SetStatus(string.Format("Connecting A2A agent: {0}...", a2aConfig.DisplayName));
                            ConnectedA2AAgent connected = await ConnectA2AAgentAsync(a2aConfig, cancellationToken)
                                .ConfigureAwait(false);
                            _connectedA2AAgents.Add(connected);
                            SetStatus(string.Format("Connected A2A agent: {0}", a2aConfig.DisplayName));
                        }
                        catch (Exception ex)
                        {
                            warnings.Add(string.Format("Failed to connect A2A agent '{0}': {1}",
                                a2aConfig.DisplayName, ex.Message));
                        }
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

                ChatHistoryProvider chatHistoryProvider = null;
                if (_config.EnablePersistentChatHistory)
                {
                    string historyPath = ResolveChatHistoryStoragePath(_config.ChatHistoryStorageSubdirectory);
                    chatHistoryProvider = new FileChatHistoryProvider(
                        historyPath,
                        _config.ChatHistoryConversationId,
                        _config.MaxPersistedMessages,
                        _config.MaxHistoryFileSizeBytes);
                }

                SetStatus("Creating embedded agent...");
                _agent = chatClient.AsAIAgent(new ChatClientAgentOptions
                {
                    Name = string.IsNullOrWhiteSpace(_config.AgentName) ? "LiveLink Agent" : _config.AgentName,
                    ChatOptions = new ChatOptions
                    {
                        Instructions = instructions,
                        Tools = tools
                    },
                    ChatHistoryProvider = chatHistoryProvider
                });

                if (_createSessionOnInitialize)
                {
                    SetStatus("Creating agent session...");
                    _session = await WithTimeout(
                        _agent.CreateSessionAsync().AsTask(),
                        TimeSpan.FromSeconds(AgentSessionInitializationTimeoutSeconds),
                        "Timed out while creating the embedded agent session. Check model connectivity and API key configuration.")
                        .ConfigureAwait(false);
                }

                // Start A2A host server if configured.
                if (_config.A2AHostConfig != null && _config.A2AHostConfig.Enabled)
                {
                    try
                    {
                        SetStatus("Starting A2A host server...");
                        _a2aHostServer = new A2AHostServer(
                            _config.A2AHostConfig,
                            (userMessage, ct) => SendMessageAsync(userMessage, ct));
                        _a2aHostServer.Start();
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(string.Format("Failed to start A2A host server: {0}", ex.Message));
                    }
                }

                _isInitialized = true;

                // Start MCP connection heartbeat.
                StartMcpHeartbeat();

                SetStatus(string.Format("Ready. Connected {0} MCP server(s), {1} A2A agent(s).",
                    _connectedServers.Count, _connectedA2AAgents.Count));
            }
            catch (Exception ex)
            {
                _lastError = ex.ToString();
                Debug.LogError(string.Format("[LiveLink-Agent] Initialization failed: {0}", _lastError));
                SetStatus("Agent initialization failed.");
                DispatchToMainThread(() => OnError.Invoke(_lastError));
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
                DispatchToMainThread(() => OnResponseReceived.Invoke(_lastResponse));
                return _lastResponse;
            }
            catch (Exception ex)
            {
                _lastError = ex.ToString();
                Debug.LogError(string.Format("[LiveLink-Agent] Request failed: {0}", _lastError));
                SetStatus("Agent request failed.");
                DispatchToMainThread(() => OnError.Invoke(_lastError));
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

            // Stop MCP heartbeat.
            StopMcpHeartbeat();

            // Stop A2A host server.
            if (_a2aHostServer != null)
            {
                try { _a2aHostServer.Dispose(); } catch { }
                _a2aHostServer = null;
            }

            await DisposeConnectionsAsync().ConfigureAwait(false);
            SetStatus("Stopped.");
        }

        private async Task<ConnectedA2AAgent> ConnectA2AAgentAsync(AgentA2ARemoteConfig config, CancellationToken cancellationToken)
        {
            Uri endpoint = new Uri(config.Endpoint);
            Dictionary<string, string> headers = config.GetHeadersDictionary();

            // Create a custom handler that accepts self-signed certs if configured.
            HttpMessageHandler certHandler = config.AcceptSelfSignedCertificates
                ? A2AClient.CreateHandlerWithCertificateValidation((_, _, _, _) => true)
                : null;

            A2AAgentCard card = null;
            if (config.UseAgentCardDiscovery)
            {
                SetStatus(string.Format("Discovering A2A agent card: {0}...", config.DisplayName));
                card = await A2AClient.GetAgentCardAsync(
                        endpoint, headers, config.ConnectionTimeoutSeconds, certHandler)
                    .ConfigureAwait(false);
            }

            // If agent card exposes a specific HTTP+JSON endpoint URL, use it.
            Uri agentEndpoint = endpoint;
            if (card?.SupportedInterfaces != null)
            {
                for (int i = 0; i < card.SupportedInterfaces.Count; i++)
                {
                    A2AInterface iface = card.SupportedInterfaces[i];
                    if (!string.IsNullOrEmpty(iface?.Url)
                        && string.Equals(iface.ProtocolBinding, "HTTP+JSON", StringComparison.OrdinalIgnoreCase))
                    {
                        agentEndpoint = new Uri(iface.Url);
                        break;
                    }
                }
            }

            // Pass the cert handler to the client so it applies to all requests.
            var client = certHandler != null
                ? new A2AClient(agentEndpoint, certHandler, headers, config.ConnectionTimeoutSeconds)
                : new A2AClient(agentEndpoint, headers, config.ConnectionTimeoutSeconds);

            try
            {
                var tool = new A2AAgentToolWrapper(
                    config.DisplayName, client, card, config.EnableStreaming, EmitToolCall,
                    toolNamePrefix: config.DelegateToolPrefix);

                return new ConnectedA2AAgent
                {
                    DisplayName = config.DisplayName,
                    Client = client,
                    AgentCard = card,
                    Tool = tool
                };
            }
            catch
            {
                client.Dispose();
                throw;
            }
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
                ServerVersion = client.ServerInfo.Version,
                ExternalConfig = config
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

                connectedServer.Tools.Add(WrapToolForEvent(tool));
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

            // Add A2A remote agent tools.
            for (int i = 0; i < _connectedA2AAgents.Count; i++)
            {
                ConnectedA2AAgent agent = _connectedA2AAgents[i];
                if (agent.Tool != null && seenNames.Add(agent.Tool.Name))
                {
                    tools.Add(agent.Tool);
                    _availableToolNames.Add(agent.Tool.Name);
                }
                else if (agent.Tool != null)
                {
                    warnings.Add(string.Format(
                        "Skipped duplicate A2A tool '{0}' from agent '{1}'.",
                        agent.Tool.Name, agent.DisplayName));
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

            if (_connectedA2AAgents.Count > 0)
            {
                builder.AppendLine("Connected remote A2A agents (use their delegate tools to ask questions or assign tasks):");
                for (int i = 0; i < _connectedA2AAgents.Count; i++)
                {
                    ConnectedA2AAgent agent = _connectedA2AAgents[i];
                    builder.Append("- ");
                    builder.Append(agent.DisplayName);
                    if (agent.AgentCard != null)
                    {
                        if (!string.IsNullOrWhiteSpace(agent.AgentCard.Version))
                        {
                            builder.Append(" v");
                            builder.Append(agent.AgentCard.Version);
                        }
                        if (!string.IsNullOrWhiteSpace(agent.AgentCard.Description))
                        {
                            builder.Append(" — ");
                            builder.AppendLine(agent.AgentCard.Description.Trim());
                        }
                        else
                        {
                            builder.AppendLine();
                        }

                        if (agent.AgentCard.Skills != null && agent.AgentCard.Skills.Count > 0)
                        {
                            builder.AppendLine("  Skills:");
                            for (int s = 0; s < agent.AgentCard.Skills.Count; s++)
                            {
                                A2ASkill skill = agent.AgentCard.Skills[s];
                                builder.Append("    - ");
                                builder.Append(skill.Name);
                                if (!string.IsNullOrWhiteSpace(skill.Description))
                                {
                                    builder.Append(": ");
                                    builder.Append(skill.Description);
                                }
                                builder.AppendLine();
                            }
                        }
                    }
                    else
                    {
                        builder.AppendLine();
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

        private async Task ResolveLiveLinkManagerReferenceAsync(CancellationToken cancellationToken)
        {
            if (_liveLinkManager != null)
            {
                return;
            }

            var tcs = new TaskCompletionSource<LiveLinkManager>(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatchToMainThread(() =>
            {
                try
                {
                    tcs.TrySetResult(FindLiveLinkManagerInScene());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                _liveLinkManager = await tcs.Task.ConfigureAwait(false);
            }
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

        private static string ResolveChatHistoryStoragePath(string configuredSubdirectory)
        {
            string basePath = Application.persistentDataPath;
            string relativePath = string.IsNullOrWhiteSpace(configuredSubdirectory)
                ? Path.Combine("LiveLink", "AgentHistory")
                : configuredSubdirectory.Trim();

            relativePath = relativePath.Replace('\\', '/');
            string[] segments = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string combined = basePath;
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i].Trim();
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    continue;
                }

                combined = Path.Combine(combined, segment);
            }

            return combined;
        }

        private AgentMcpHttpTransportMode GetEffectiveLocalTransportMode()
        {
            if (_config.LocalHttpTransportMode == AgentMcpHttpTransportMode.Sse)
            {
                string message =
                    "[LiveLink-Agent] SSE transport selected in Inspector but silently overridden to StreamableHttp. " +
                    "The legacy SSE transport is fragile with the current MCP C# SDK inside Unity. " +
                    "Please change the 'Local HTTP Transport' field to 'StreamableHttp' in the AgentRuntimeConfig Inspector to suppress this warning.";

                Debug.LogWarning(message);
                SetStatus("Warning: SSE overridden to StreamableHttp (see console).");

                return AgentMcpHttpTransportMode.StreamableHttp;
            }

            return _config.LocalHttpTransportMode;
        }

        /// <summary>
        /// Shared HttpClient for health probes and lightweight HTTP calls.
        /// Static to avoid socket exhaustion. Cross-platform safe (works on Android/iOS/WebGL IL2CPP).
        /// Uses default handler — no platform-specific HttpClientHandler configuration.
        /// </summary>
        private static readonly HttpClient s_healthProbeClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(500)
        };

        private static async Task<bool> ProbeLocalServerHealthAsync(Uri healthUri)
        {
            try
            {
                using (HttpResponseMessage response = await s_healthProbeClient.GetAsync(healthUri).ConfigureAwait(false))
                {
                    return (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
                }
            }
            catch
            {
                return false;
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

        // ──────────────────────── MCP Heartbeat & Reconnection ────────────────────────

        private void StartMcpHeartbeat()
        {
            StopMcpHeartbeat();
            _heartbeatCts = new CancellationTokenSource();
            _ = McpHeartbeatLoopAsync(_heartbeatCts.Token);
        }

        private void StopMcpHeartbeat()
        {
            if (_heartbeatCts != null)
            {
                try { _heartbeatCts.Cancel(); } catch { }
                _heartbeatCts.Dispose();
                _heartbeatCts = null;
            }
        }

        /// <summary>
        /// Periodically health-checks connected MCP servers. On failure, attempts reconnection.
        /// Runs every 30 seconds. Only checks external (non-local) servers since local servers
        /// are managed by the LiveLinkManager lifecycle.
        /// </summary>
        private async Task McpHeartbeatLoopAsync(CancellationToken ct)
        {
            const int heartbeatIntervalMs = 30000;
            const int maxReconnectAttempts = 3;

            while (!ct.IsCancellationRequested && _isInitialized)
            {
                try
                {
                    await Task.Delay(heartbeatIntervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (!_isInitialized) break;

                // Snapshot the server list.
                List<ConnectedMcpServer> servers;
                lock (_connectedServers)
                {
                    servers = new List<ConnectedMcpServer>(_connectedServers);
                }

                foreach (ConnectedMcpServer server in servers)
                {
                    if (server.IsLocal || server.Client == null) continue;

                    try
                    {
                        // Lightweight health check — list tools.
                        await server.Client.ListToolsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        DispatchToMainThread(() => OnConnectionLost.Invoke(
                            string.Format("MCP server '{0}' connection lost: {1}", server.DisplayName, ex.Message)));

                        // Attempt reconnection.
                        bool restored = false;
                        if (server.ExternalConfig != null)
                        {
                            for (int attempt = 0; attempt < maxReconnectAttempts && !ct.IsCancellationRequested; attempt++)
                            {
                                try
                                {
                                    int delayMs = 1000 * (1 << attempt);
                                    await Task.Delay(delayMs, ct).ConfigureAwait(false);

                                    // Re-create the MCP client from stored config.
                                    McpClient newClient = await AgentMcpClientFactory.CreateExternalClientAsync(
                                        server.ExternalConfig, ct).ConfigureAwait(false);

                                    if (newClient != null)
                                    {
                                        server.Client = newClient;
                                        restored = true;
                                        DispatchToMainThread(() => OnConnectionRestored.Invoke(
                                            string.Format("MCP server '{0}' reconnected.", server.DisplayName)));
                                        break;
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch { }
                            }
                        }

                        if (!restored)
                        {
                            DispatchToMainThread(() => OnConnectionLost.Invoke(
                                string.Format("MCP server '{0}' reconnection failed after {1} attempts.",
                                    server.DisplayName, maxReconnectAttempts)));
                        }
                    }
                }
            }
        }

        private async Task DisposeConnectionsAsync()
        {
            // Dispose A2A clients.
            for (int i = _connectedA2AAgents.Count - 1; i >= 0; i--)
            {
                ConnectedA2AAgent agent = _connectedA2AAgents[i];
                if (agent?.Client != null)
                {
                    try
                    {
                        agent.Client.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(string.Format("[LiveLink-Agent] Failed to dispose A2A client '{0}': {1}",
                            agent.DisplayName, ex.Message));
                    }
                }
            }

            _connectedA2AAgents.Clear();

            // Dispose MCP clients.
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
            DispatchToMainThread(() => OnStatusChanged.Invoke(_status));
        }

        private AITool WrapToolForEvent(AITool tool)
        {
            if (tool is AIFunction function)
            {
                return new ToolCallNotifyingFunction(function, EmitToolCall);
            }

            return tool;
        }

        private void EmitToolCall(string toolName, string jsonParameters)
        {
            string safeToolName = string.IsNullOrWhiteSpace(toolName) ? "unknown_tool" : toolName;
            string safeJson = string.IsNullOrWhiteSpace(jsonParameters) ? "{}" : jsonParameters;
            DispatchToMainThread(() => OnToolCall.Invoke(safeToolName, safeJson));
        }

        private sealed class ToolCallNotifyingFunction : DelegatingAIFunction
        {
            private readonly Action<string, string> _onInvoked;

            internal ToolCallNotifyingFunction(AIFunction innerFunction, Action<string, string> onInvoked)
                : base(innerFunction)
            {
                _onInvoked = onInvoked;
            }

            protected override ValueTask<object> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            {
                _onInvoked?.Invoke(Name, SerializeArguments(arguments));
                return base.InvokeCoreAsync(arguments, cancellationToken);
            }

            private static string SerializeArguments(AIFunctionArguments arguments)
            {
                if (arguments == null || arguments.Count == 0)
                {
                    return "{}";
                }

                var payload = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> entry in arguments)
                {
                    payload[entry.Key] = entry.Value;
                }

                try
                {
                    return JsonSerializer.Serialize(payload);
                }
                catch
                {
                    try
                    {
                        var fallback = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, object> entry in payload)
                        {
                            fallback[entry.Key] = entry.Value != null ? entry.Value.ToString() : "null";
                        }

                        return JsonSerializer.Serialize(fallback);
                    }
                    catch
                    {
                        return "{}";
                    }
                }
            }
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
