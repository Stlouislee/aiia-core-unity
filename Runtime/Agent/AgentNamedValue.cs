using System;
using UnityEngine;

namespace LiveLink.Agent
{
    /// <summary>
    /// Serializable key/value pair used for MCP headers and environment variables.
    /// </summary>
    [Serializable]
    public class AgentNamedValue
    {
        [SerializeField]
        private string _name;

        [SerializeField]
        private string _value;

        public string Name => _name;
        public string Value => _value;
    }
}
