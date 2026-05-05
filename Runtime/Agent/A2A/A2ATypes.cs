using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LiveLink.Agent.A2A
{
    /// <summary>
    /// A2A Agent Card — describes a remote agent's capabilities and endpoints.
    /// Fetched from /.well-known/agent-card.json per the A2A v1.0 spec.
    /// </summary>
    public class A2AAgentCard
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("supportedInterfaces")]
        public List<A2AInterface> SupportedInterfaces { get; set; } = new List<A2AInterface>();

        [JsonPropertyName("skills")]
        public List<A2ASkill> Skills { get; set; } = new List<A2ASkill>();

        [JsonPropertyName("capabilities")]
        public A2ACapabilities Capabilities { get; set; }

        [JsonPropertyName("defaultInputModes")]
        public List<string> DefaultInputModes { get; set; } = new List<string>();

        [JsonPropertyName("defaultOutputModes")]
        public List<string> DefaultOutputModes { get; set; } = new List<string>();
    }

    public class A2AInterface
    {
        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("protocolBinding")]
        public string ProtocolBinding { get; set; }

        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; }
    }

    public class A2ASkill
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class A2ACapabilities
    {
        [JsonPropertyName("streaming")]
        public bool Streaming { get; set; }

        [JsonPropertyName("pushNotifications")]
        public bool PushNotifications { get; set; }
    }

    /// <summary>
    /// A2A Message — the unit of communication between agents.
    /// </summary>
    public class A2AMessage
    {
        [JsonPropertyName("messageId")]
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("parts")]
        public List<A2APart> Parts { get; set; } = new List<A2APart>();

        [JsonPropertyName("taskId")]
        public string TaskId { get; set; }

        public static A2AMessage CreateUserTextMessage(string text)
        {
            return new A2AMessage
            {
                Role = "user",
                Parts = new List<A2APart> { A2APart.FromText(text) }
            };
        }

        public static A2AMessage CreateAgentTextMessage(string text)
        {
            return new A2AMessage
            {
                Role = "agent",
                Parts = new List<A2APart> { A2APart.FromText(text) }
            };
        }
    }

    public class A2APart
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        public string Text { get; set; }

        [JsonPropertyName("file")]
        public A2AFile File { get; set; }

        [JsonPropertyName("data")]
        public Dictionary<string, object> Data { get; set; }

        public static A2APart FromText(string text)
        {
            return new A2APart { Type = "text", Text = text };
        }
    }

    public class A2AFile
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; }

        [JsonPropertyName("bytes")]
        public string Bytes { get; set; }

        [JsonPropertyName("uri")]
        public string Uri { get; set; }
    }

    /// <summary>
    /// Wire-format request for the A2A HTTP+JSON binding.
    /// </summary>
    public class A2ASendMessageRequest
    {
        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }

        [JsonPropertyName("streaming")]
        public bool Streaming { get; set; }
    }

    /// <summary>
    /// Wire-format response for the A2A HTTP+JSON binding.
    /// </summary>
    public class A2ASendMessageResponse
    {
        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }

        [JsonPropertyName("error")]
        public A2AError Error { get; set; }
    }

    public class A2AError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("data")]
        public object Data { get; set; }
    }

    /// <summary>
    /// Represents an SSE event received during streaming.
    /// </summary>
    public class A2AStreamingEvent
    {
        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }

        [JsonPropertyName("taskId")]
        public string TaskId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }
    }
}
