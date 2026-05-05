using System.Collections.Generic;
using LiveLink.Agent.A2A;
using UnityEngine;

namespace LiveLink.Agent
{
    /// <summary>
    /// ScriptableObject used to configure the embedded Agent Framework runtime.
    /// Supports any OpenAI-compatible chat backend (OpenAI, Azure OpenAI, local models, etc.).
    /// </summary>
    [CreateAssetMenu(fileName = "LiveLinkAgentRuntimeConfig", menuName = "LiveLink/Agent Runtime Config")]
    public class AgentRuntimeConfig : ScriptableObject
    {
        [Header("Chat Backend")]
        [SerializeField]
        private string _agentName = "LiveLink Agent";

        [Tooltip("API endpoint URL. Leave empty for default OpenAI. Set to a custom URL for OpenAI-compatible providers (e.g., Azure OpenAI, local LLMs).")]
        [SerializeField]
        private string _apiEndpoint = "";

        [Tooltip("Model identifier (e.g., gpt-4o-mini, deepseek-chat, llama-3).")]
        [SerializeField]
        private string _model = "gpt-4o-mini";

        [Tooltip("When enabled, reads the API key from the environment variable first.")]
        [SerializeField]
        private bool _preferEnvironmentApiKey = true;

        [Tooltip("Environment variable name to read the API key from.")]
        [SerializeField]
        private string _apiKeyEnvironmentVariable = "OPENAI_API_KEY";

        [Tooltip("API key stored in the asset. Used as fallback when environment variable is not set, or when 'Prefer Environment API Key' is disabled.")]
        [SerializeField]
        private string _apiKey = string.Empty;

        // Runtime-injected API key (not serialized). Takes highest priority when set.
        [System.NonSerialized]
        private string _runtimeApiKey;

        [Header("Agent Behavior")]
        [SerializeField]
        [TextArea(4, 10)]
        private string _systemInstructions =
            "You are a Unity scene assistant inside a LiveLink-enabled application. " +
            "Use the available MCP tools to inspect the current scene before answering it.";

        [SerializeField]
        private bool _enableLocalLiveLinkMcp = true;

        [SerializeField]
        private bool _autoStartLocalLiveLinkMcp = true;

        [SerializeField]
        private AgentMcpHttpTransportMode _localHttpTransportMode = AgentMcpHttpTransportMode.StreamableHttp;

        [SerializeField]
        private float _localConnectionTimeoutSeconds = 15f;

        [SerializeField]
        private float _localReadinessTimeoutSeconds = 10f;

        [SerializeField]
        private bool _allowSceneMutationTools = true;

        [Header("Chat History Persistence")]
        [SerializeField]
        private bool _enablePersistentChatHistory = false;

        [SerializeField]
        private string _chatHistoryConversationId = "default";

        [SerializeField]
        private string _chatHistoryStorageSubdirectory = "LiveLink/AgentHistory";

        [SerializeField]
        [Min(10)]
        private int _maxPersistedMessages = 200;

        [SerializeField]
        [Min(16384)]
        private int _maxHistoryFileSizeBytes = 1048576;

        [Header("Downstream MCP Servers")]
        [SerializeField]
        private List<AgentExternalMcpServerConfig> _externalMcpServers = new List<AgentExternalMcpServerConfig>();

        [Header("Remote A2A Agents")]
        [Tooltip("Remote A2A-compliant agents (OpenClaw, Hermes, etc.) that the embedded agent can delegate tasks to.")]
        [SerializeField]
        private List<AgentA2ARemoteConfig> _remoteA2AAgents = new List<AgentA2ARemoteConfig>();

        [Header("A2A Hosting")]
        [Tooltip("Configuration for hosting this agent as an A2A endpoint.")]
        [SerializeField]
        private A2AHostConfig _a2aHostConfig = new A2AHostConfig();

        // ── Public Properties ──────────────────────────────────────────

        public string AgentName => _agentName;
        public string ApiEndpoint => _apiEndpoint;
        public string Model => _model;
        public bool PreferEnvironmentApiKey => _preferEnvironmentApiKey;
        public string ApiKeyEnvironmentVariable => _apiKeyEnvironmentVariable;
        public string ApiKey => _apiKey;
        public string SystemInstructions => _systemInstructions;
        public bool EnableLocalLiveLinkMcp => _enableLocalLiveLinkMcp;
        public bool AutoStartLocalLiveLinkMcp => _autoStartLocalLiveLinkMcp;
        public AgentMcpHttpTransportMode LocalHttpTransportMode => _localHttpTransportMode;
        public float LocalConnectionTimeoutSeconds => _localConnectionTimeoutSeconds;

        /// <summary>
        /// Maximum seconds to wait for the local LiveLink MCP server to become healthy after starting.
        /// </summary>
        public float LocalReadinessTimeoutSeconds => Mathf.Max(3f, _localReadinessTimeoutSeconds);

        public bool AllowSceneMutationTools => _allowSceneMutationTools;
        public bool EnablePersistentChatHistory => _enablePersistentChatHistory;
        public string ChatHistoryConversationId => _chatHistoryConversationId;
        public string ChatHistoryStorageSubdirectory => _chatHistoryStorageSubdirectory;
        public int MaxPersistedMessages => _maxPersistedMessages;
        public int MaxHistoryFileSizeBytes => _maxHistoryFileSizeBytes;
        public IReadOnlyList<AgentExternalMcpServerConfig> ExternalMcpServers => _externalMcpServers;
        public IReadOnlyList<AgentA2ARemoteConfig> RemoteA2AAgents => _remoteA2AAgents;
        public A2AHostConfig A2AHostConfig => _a2aHostConfig;

        // ── API Key Resolution ─────────────────────────────────────────

        /// <summary>
        /// Set the API key at runtime (e.g., from a login flow, secure storage, or external provider).
        /// This takes highest priority over both the config asset value and the environment variable.
        /// Pass null or empty to clear the runtime key and fall back to config/env resolution.
        /// </summary>
        public void SetApiKey(string apiKey)
        {
            _runtimeApiKey = apiKey;
        }

        /// <summary>
        /// Resolves the API key with the following priority:
        /// 1. Runtime-injected key (via SetApiKey)
        /// 2. Environment variable (if PreferEnvironmentApiKey is enabled)
        /// 3. Config asset value
        /// </summary>
        public string ResolveApiKey()
        {
            // 1. Runtime key (highest priority)
            if (!string.IsNullOrWhiteSpace(_runtimeApiKey))
                return _runtimeApiKey;

            // 2. Environment variable
            if (_preferEnvironmentApiKey && !string.IsNullOrEmpty(_apiKeyEnvironmentVariable))
            {
                string envValue = System.Environment.GetEnvironmentVariable(_apiKeyEnvironmentVariable);
                if (!string.IsNullOrEmpty(envValue))
                    return envValue;
            }

            // 3. Config asset value
            return _apiKey;
        }
    }
}
