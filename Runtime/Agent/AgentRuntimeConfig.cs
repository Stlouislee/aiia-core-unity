using System.Collections.Generic;
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

        [Header("Downstream MCP Servers")]
        [SerializeField]
        private List<AgentExternalMcpServerConfig> _externalMcpServers = new List<AgentExternalMcpServerConfig>();

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
        public IReadOnlyList<AgentExternalMcpServerConfig> ExternalMcpServers => _externalMcpServers;

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
