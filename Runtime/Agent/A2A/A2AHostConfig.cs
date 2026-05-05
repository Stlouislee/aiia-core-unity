using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveLink.Agent.A2A
{
    /// <summary>
    /// Configuration for hosting this Unity agent as an A2A endpoint.
    /// Allows external A2A clients to discover and communicate with the embedded agent.
    /// </summary>
    [Serializable]
    public class A2AHostConfig
    {
        [SerializeField]
        [Tooltip("Enable A2A hosting — makes this agent discoverable by external A2A clients.")]
        private bool _enabled;

        [SerializeField]
        [Tooltip("Port for the A2A HTTP server. Default 8082 (MCP uses 8081).")]
        private int _port = 8082;

        [SerializeField]
        [Tooltip("Agent name shown in the agent card.")]
        private string _agentName = "Unity Agent";

        [SerializeField]
        [Tooltip("Agent description shown in the agent card.")]
        [TextArea(2, 5)]
        private string _agentDescription = "A Unity-based AI agent powered by LiveLink.";

        [SerializeField]
        [Tooltip("Agent version string.")]
        private string _agentVersion = "1.0.0";

        [SerializeField]
        [Tooltip("Whether this agent supports SSE streaming responses.")]
        private bool _enableStreaming = true;

        [SerializeField]
        [Tooltip("Optional Bearer token for authenticating incoming requests. Leave empty to disable auth.")]
        private string _authToken = "";

        [SerializeField]
        [Tooltip("Maximum requests per minute per client IP. 0 = unlimited.")]
        private int _rateLimitPerMinute;

        [SerializeField]
        [Tooltip("Skills this agent exposes to A2A clients. Each skill maps to a set of capabilities.")]
        private List<A2AHostSkill> _skills = new List<A2AHostSkill>();

        public bool Enabled => _enabled;
        public int Port => _port;
        public string AgentName => _agentName;
        public string AgentDescription => _agentDescription;
        public string AgentVersion => _agentVersion;
        public bool EnableStreaming => _enableStreaming;
        public string AuthToken => _authToken;
        public int RateLimitPerMinute => _rateLimitPerMinute;
        public IReadOnlyList<A2AHostSkill> Skills => _skills;
    }

    /// <summary>
    /// A skill exposed by this agent via the A2A agent card.
    /// </summary>
    [Serializable]
    public class A2AHostSkill
    {
        [SerializeField]
        private string _id = "";

        [SerializeField]
        private string _name = "";

        [SerializeField]
        [TextArea(1, 3)]
        private string _description = "";

        [SerializeField]
        private List<string> _tags = new List<string>();

        public string Id => _id;
        public string Name => _name;
        public string Description => _description;
        public IReadOnlyList<string> Tags => _tags;
    }
}
