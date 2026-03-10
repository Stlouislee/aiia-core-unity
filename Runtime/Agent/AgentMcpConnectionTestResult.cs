using System.Collections.Generic;

namespace LiveLink.Agent
{
    /// <summary>
    /// Lightweight test result for validating downstream MCP connectivity in the Editor.
    /// </summary>
    public sealed class AgentMcpConnectionTestResult
    {
        public bool Success { get; set; }
        public string DisplayName { get; set; }
        public string ServerName { get; set; }
        public string ServerVersion { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> ToolNames { get; set; } = new List<string>();
    }
}
