using System.Threading;
using System.Threading.Tasks;

namespace LiveLink.Agent
{
    /// <summary>
    /// Editor-safe wrapper for validating downstream MCP server configurations.
    /// </summary>
    public static class AgentMcpConnectionTester
    {
        public static Task<AgentMcpConnectionTestResult> TestConnectionAsync(AgentExternalMcpServerConfig config, CancellationToken cancellationToken)
        {
            return AgentMcpClientFactory.TestConnectionAsync(config, cancellationToken);
        }
    }
}
