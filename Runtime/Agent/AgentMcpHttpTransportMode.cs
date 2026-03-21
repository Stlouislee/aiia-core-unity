namespace LiveLink.Agent
{
    /// <summary>
    /// HTTP transport modes supported by the MCP client SDK.
    /// </summary>
    public enum AgentMcpHttpTransportMode
    {
        AutoDetect,
        StreamableHttp,
        Sse
    }
}
