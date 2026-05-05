using System.Collections.Generic;

namespace LiveLink.Agent
{
    /// <summary>
    /// Result of an LLM connectivity test.
    /// </summary>
    public class AgentLlmTestResult
    {
        public bool Success { get; set; }
        public string Model { get; set; }
        public string ResponseText { get; set; }
        public string ErrorMessage { get; set; }
        public long LatencyMs { get; set; }
    }
}
