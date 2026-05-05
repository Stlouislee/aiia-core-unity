using System.Collections.Generic;
using LiveLink.Agent.A2A;
using UnityEngine;

namespace LiveLink.Agent
{
    /// <summary>
    /// ScriptableObject used to configure the embedded Agent Framework runtime.
    /// </summary>
    [CreateAssetMenu(fileName = "LiveLinkAgentRuntimeConfig", menuName = "LiveLink/Agent Runtime Config")]
    public class AgentRuntimeConfig : ScriptableObject
    {
        [Header("OpenAI Chat Backend")]
        [SerializeField]
        private string _agentName = "LiveLink Agent";

        [SerializeField]
        private string _openAIModel = "gpt-4o-mini";

        [SerializeField]
        private bool _preferEnvironmentApiKey = true;

        [SerializeField]
        private string _openAIApiKeyEnvironmentVariable = "OPENAI_API_KEY";

        [SerializeField]
        private string _openAIApiKey = string.Empty;

        [Header("Agent Behavior")]
        [SerializeField]
        [TextArea(4, 10)]
        private string _systemInstructions =
            "You are a Unity scene assistant inside a LiveLink-enabled application. " +
            "Use the available MCP tools to inspect the current scene before answering questions about it.";

        [SerializeField]
        private bool _enableLocalLiveLinkMcp = true;

        [SerializeField]
        private bool _autoStartLocalLiveLinkMcp = true;

        [SerializeField]
        private AgentMcpHttpTransportMode _localHttpTransportMode = AgentMcpHttpTransportMode.StreamableHttp;

        [SerializeField]
        private float _localConnectionTimeoutSeconds = 15f;

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

        public string AgentName => _agentName;
        public string OpenAIModel => _openAIModel;
        public bool PreferEnvironmentApiKey => _preferEnvironmentApiKey;
        public string OpenAIApiKeyEnvironmentVariable => _openAIApiKeyEnvironmentVariable;
        public string OpenAIApiKey => _openAIApiKey;
        public string SystemInstructions => _systemInstructions;
        public bool EnableLocalLiveLinkMcp => _enableLocalLiveLinkMcp;
        public bool AutoStartLocalLiveLinkMcp => _autoStartLocalLiveLinkMcp;
        public AgentMcpHttpTransportMode LocalHttpTransportMode => _localHttpTransportMode;
        public float LocalConnectionTimeoutSeconds => _localConnectionTimeoutSeconds;
        public bool AllowSceneMutationTools => _allowSceneMutationTools;
        public bool EnablePersistentChatHistory => _enablePersistentChatHistory;
        public string ChatHistoryConversationId => _chatHistoryConversationId;
        public string ChatHistoryStorageSubdirectory => _chatHistoryStorageSubdirectory;
        public int MaxPersistedMessages => _maxPersistedMessages;
        public int MaxHistoryFileSizeBytes => _maxHistoryFileSizeBytes;
        public IReadOnlyList<AgentExternalMcpServerConfig> ExternalMcpServers => _externalMcpServers;
        public IReadOnlyList<AgentA2ARemoteConfig> RemoteA2AAgents => _remoteA2AAgents;

        public string ResolveOpenAIApiKey()
        {
            if (_preferEnvironmentApiKey && !string.IsNullOrEmpty(_openAIApiKeyEnvironmentVariable))
            {
                string envValue = System.Environment.GetEnvironmentVariable(_openAIApiKeyEnvironmentVariable);
                if (!string.IsNullOrEmpty(envValue))
                    return envValue;
            }

            return _openAIApiKey;
        }
    }
}
