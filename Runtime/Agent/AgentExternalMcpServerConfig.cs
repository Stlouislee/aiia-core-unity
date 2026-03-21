using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveLink.Agent
{
    /// <summary>
    /// Configuration for a user-supplied downstream MCP server.
    /// </summary>
    [Serializable]
    public class AgentExternalMcpServerConfig
    {
        [SerializeField]
        private bool _enabled = true;

        [SerializeField]
        private string _displayName = "External MCP Server";

        [SerializeField]
        private AgentMcpTransportType _transportType = AgentMcpTransportType.Http;

        [SerializeField]
        private AgentMcpHttpTransportMode _httpTransportMode = AgentMcpHttpTransportMode.AutoDetect;

        [SerializeField]
        private string _endpoint = string.Empty;

        [SerializeField]
        private float _connectionTimeoutSeconds = 30f;

        [SerializeField]
        private List<AgentNamedValue> _headers = new List<AgentNamedValue>();

        [SerializeField]
        private string _command = string.Empty;

        [SerializeField]
        private List<string> _arguments = new List<string>();

        [SerializeField]
        private string _workingDirectory = string.Empty;

        [SerializeField]
        private List<AgentNamedValue> _environmentVariables = new List<AgentNamedValue>();

        [SerializeField]
        private bool _useToolAllowList = false;

        [SerializeField]
        private List<string> _allowedTools = new List<string>();

        public bool Enabled => _enabled;
        public string DisplayName => _displayName;
        public AgentMcpTransportType TransportType => _transportType;
        public AgentMcpHttpTransportMode HttpTransportMode => _httpTransportMode;
        public string Endpoint => _endpoint;
        public float ConnectionTimeoutSeconds => _connectionTimeoutSeconds;
        public IReadOnlyList<AgentNamedValue> Headers => _headers;
        public string Command => _command;
        public IReadOnlyList<string> Arguments => _arguments;
        public string WorkingDirectory => _workingDirectory;
        public IReadOnlyList<AgentNamedValue> EnvironmentVariables => _environmentVariables;
        public bool UseToolAllowList => _useToolAllowList;
        public IReadOnlyList<string> AllowedTools => _allowedTools;

        public bool IsToolAllowed(string toolName)
        {
            if (!_useToolAllowList)
                return true;

            if (string.IsNullOrEmpty(toolName))
                return false;

            for (int i = 0; i < _allowedTools.Count; i++)
            {
                if (string.Equals(_allowedTools[i], toolName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
