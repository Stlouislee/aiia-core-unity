using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveLink.Agent.A2A
{
    /// <summary>
    /// Configuration for a remote A2A agent that the embedded agent can delegate to.
    /// Each entry becomes a callable tool (e.g., "ask_openclaw") available to the agent.
    /// </summary>
    [Serializable]
    public class AgentA2ARemoteConfig
    {
        [SerializeField]
        private bool _enabled = true;

        [SerializeField]
        private string _displayName = "Remote Agent";

        [SerializeField]
        private string _endpoint = "";

        [SerializeField]
        private bool _useAgentCardDiscovery = true;

        [SerializeField]
        private float _connectionTimeoutSeconds = 30f;

        [SerializeField]
        private List<AgentNamedValue> _headers = new List<AgentNamedValue>();

        [SerializeField]
        private bool _enableStreaming = true;

        [SerializeField]
        [Tooltip("Prefix for the tool name exposed to the embedded agent. Result: {prefix}{sanitized_display_name}")]
        private string _delegateToolPrefix = "ask_";

        public bool Enabled => _enabled;
        public string DisplayName => _displayName;
        public string Endpoint => _endpoint;
        public bool UseAgentCardDiscovery => _useAgentCardDiscovery;
        public float ConnectionTimeoutSeconds => _connectionTimeoutSeconds;
        public IReadOnlyList<AgentNamedValue> Headers => _headers;
        public bool EnableStreaming => _enableStreaming;
        public string DelegateToolPrefix => _delegateToolPrefix;

        /// <summary>
        /// Convert the serialized headers list to a dictionary for HttpClient.
        /// </summary>
        public Dictionary<string, string> GetHeadersDictionary()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_headers == null) return result;

            for (int i = 0; i < _headers.Count; i++)
            {
                AgentNamedValue header = _headers[i];
                if (header != null && !string.IsNullOrWhiteSpace(header.Name))
                {
                    result[header.Name] = header.Value ?? string.Empty;
                }
            }

            return result;
        }
    }
}
