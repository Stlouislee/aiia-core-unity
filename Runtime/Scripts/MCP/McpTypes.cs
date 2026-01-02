using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveLink.MCP
{
    /// <summary>
    /// Basic JSON-RPC 2.0 request envelope.
    /// </summary>
    public class JsonRpcRequest
    {
        [JsonProperty("jsonrpc")]
        public string Version { get; set; } = McpJsonRpc.Version;

        [JsonProperty("method")]
        public string Method { get; set; }

        [JsonProperty("id")]
        public JToken Id { get; set; }

        [JsonProperty("params")]
        public JObject Params { get; set; }
    }

    /// <summary>
    /// Basic JSON-RPC 2.0 response envelope.
    /// </summary>
    public class JsonRpcResponse
    {
        [JsonProperty("jsonrpc")]
        public string Version { get; set; } = McpJsonRpc.Version;

        [JsonProperty("id")]
        public JToken Id { get; set; }

        [JsonProperty("result")]
        public object Result { get; set; }

        [JsonProperty("error")]
        public JsonRpcError Error { get; set; }
    }

    /// <summary>
    /// JSON-RPC 2.0 error payload.
    /// </summary>
    public class JsonRpcError
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public JToken Data { get; set; }
    }

    /// <summary>
    /// MCP resource description.
    /// </summary>
    public class McpResource
    {
        [JsonProperty("uri")]
        public string Uri { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("mimeType")]
        public string MimeType { get; set; }

        [JsonProperty("attributes")]
        public JObject Attributes { get; set; }
    }

    /// <summary>
    /// MCP resource content payload.
    /// </summary>
    public class McpContent
    {
        [JsonProperty("uri")]
        public string Uri { get; set; }

        [JsonProperty("mimeType")]
        public string MimeType { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }
    }

    /// <summary>
    /// MCP tool description.
    /// </summary>
    public class McpTool
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("inputSchema")]
        public JObject InputSchema { get; set; }

        [JsonProperty("readOnly")]
        public bool ReadOnly { get; set; }
    }

    /// <summary>
    /// Helpers for constructing JSON-RPC responses.
    /// </summary>
    public static class McpJsonRpc
    {
        public const string Version = "2.0";

        public static JsonRpcResponse Success(JToken id, object result)
        {
            return new JsonRpcResponse
            {
                Id = id,
                Result = result
            };
        }

        public static JsonRpcResponse Error(JToken id, int code, string message, JToken data = null)
        {
            return new JsonRpcResponse
            {
                Id = id,
                Error = new JsonRpcError
                {
                    Code = code,
                    Message = message,
                    Data = data
                }
            };
        }
    }
}
