using System;
using System.Collections.Generic;
using System.Text.Json;
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
        [JsonConverter(typeof(RoleJsonConverter))]
        public string Role { get; set; } = "ROLE_USER";

        [JsonPropertyName("parts")]
        public List<A2APart> Parts { get; set; } = new List<A2APart>();

        [JsonPropertyName("taskId")]
        public string TaskId { get; set; }

        public static A2AMessage CreateUserTextMessage(string text)
        {
            return new A2AMessage
            {
                Role = "ROLE_USER",
                Parts = new List<A2APart> { A2APart.FromText(text) }
            };
        }

        public static A2AMessage CreateAgentTextMessage(string text)
        {
            return new A2AMessage
            {
                Role = "ROLE_AGENT",
                Parts = new List<A2APart> { A2APart.FromText(text) }
            };
        }
    }

    /// <summary>
    /// A2A Part — uses v1.0 member-name discriminator pattern.
    /// The JSON member name itself identifies the part type:
    ///   - TextPart:  { "text": "..." }
    ///   - FilePart:  { "raw": "base64...", "filename": "...", "mediaType": "..." }
    ///              or { "url": "https://...", "filename": "...", "mediaType": "..." }
    ///   - DataPart:  { "data": {...}, "mediaType": "application/json" }
    /// </summary>
    [JsonConverter(typeof(PartJsonConverter))]
    public class A2APart
    {
        /// <summary>
        /// Discriminator indicating which content variant this part holds.
        /// Not serialized to JSON — the JSON member name acts as the discriminator.
        /// </summary>
        public PartKind Kind { get; set; } = PartKind.Text;

        /// <summary>
        /// Text content (for TextPart).
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Raw file bytes, base64-encoded in JSON (for FilePart with raw content).
        /// </summary>
        public byte[] Raw { get; set; }

        /// <summary>
        /// URL pointing to file content (for FilePart with URL reference).
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// Structured data (for DataPart).
        /// </summary>
        public Dictionary<string, object> Data { get; set; }

        /// <summary>
        /// Optional filename for file parts.
        /// </summary>
        public string Filename { get; set; }

        /// <summary>
        /// MIME type. Available for all part types.
        /// </summary>
        public string MediaType { get; set; }

        public static A2APart FromText(string text)
        {
            return new A2APart { Kind = PartKind.Text, Text = text };
        }

        public static A2APart FromRaw(byte[] raw, string filename = null, string mediaType = null)
        {
            return new A2APart { Kind = PartKind.File, Raw = raw, Filename = filename, MediaType = mediaType };
        }

        public static A2APart FromUrl(string url, string filename = null, string mediaType = null)
        {
            return new A2APart { Kind = PartKind.File, Url = url, Filename = filename, MediaType = mediaType };
        }

        public static A2APart FromData(Dictionary<string, object> data, string mediaType = null)
        {
            return new A2APart { Kind = PartKind.Data, Data = data, MediaType = mediaType };
        }
    }

    public enum PartKind
    {
        Text,
        File,
        Data
    }

    // ───────────────────── TaskState Converter ─────────────────────

    /// <summary>
    /// Converts A2ATaskState to/from SCREAMING_SNAKE_CASE strings per A2A v1.0 spec.
    /// </summary>
    internal sealed class TaskStateJsonConverter : JsonConverter<A2ATaskState>
    {
        private static readonly Dictionary<A2ATaskState, string> s_toString = new Dictionary<A2ATaskState, string>
        {
            [A2ATaskState.Unspecified] = "TASK_STATE_UNSPECIFIED",
            [A2ATaskState.Submitted] = "TASK_STATE_SUBMITTED",
            [A2ATaskState.Working] = "TASK_STATE_WORKING",
            [A2ATaskState.Completed] = "TASK_STATE_COMPLETED",
            [A2ATaskState.Failed] = "TASK_STATE_FAILED",
            [A2ATaskState.Canceled] = "TASK_STATE_CANCELED",
            [A2ATaskState.InputRequired] = "TASK_STATE_INPUT_REQUIRED",
            [A2ATaskState.Rejected] = "TASK_STATE_REJECTED",
            [A2ATaskState.AuthRequired] = "TASK_STATE_AUTH_REQUIRED"
        };

        private static readonly Dictionary<string, A2ATaskState> s_fromString =
            new Dictionary<string, A2ATaskState>(StringComparer.OrdinalIgnoreCase)
            {
                ["TASK_STATE_UNSPECIFIED"] = A2ATaskState.Unspecified,
                ["TASK_STATE_SUBMITTED"] = A2ATaskState.Submitted,
                ["TASK_STATE_WORKING"] = A2ATaskState.Working,
                ["TASK_STATE_COMPLETED"] = A2ATaskState.Completed,
                ["TASK_STATE_FAILED"] = A2ATaskState.Failed,
                ["TASK_STATE_CANCELED"] = A2ATaskState.Canceled,
                ["TASK_STATE_INPUT_REQUIRED"] = A2ATaskState.InputRequired,
                ["TASK_STATE_REJECTED"] = A2ATaskState.Rejected,
                ["TASK_STATE_AUTH_REQUIRED"] = A2ATaskState.AuthRequired,
                // Also accept short names for convenience
                ["UNSPECIFIED"] = A2ATaskState.Unspecified,
                ["SUBMITTED"] = A2ATaskState.Submitted,
                ["WORKING"] = A2ATaskState.Working,
                ["COMPLETED"] = A2ATaskState.Completed,
                ["FAILED"] = A2ATaskState.Failed,
                ["CANCELED"] = A2ATaskState.Canceled,
                ["INPUT_REQUIRED"] = A2ATaskState.InputRequired,
                ["REJECTED"] = A2ATaskState.Rejected,
                ["AUTH_REQUIRED"] = A2ATaskState.AuthRequired
            };

        public override A2ATaskState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string value = reader.GetString();
            if (string.IsNullOrEmpty(value)) return A2ATaskState.Unspecified;

            if (s_fromString.TryGetValue(value, out A2ATaskState state))
                return state;

            throw new JsonException($"Unknown TaskState value: {value}");
        }

        public override void Write(Utf8JsonWriter writer, A2ATaskState value, JsonSerializerOptions options)
        {
            if (s_toString.TryGetValue(value, out string str))
                writer.WriteStringValue(str);
            else
                writer.WriteStringValue("TASK_STATE_UNSPECIFIED");
        }
    }

    // ───────────────────── Role Converter ─────────────────────

    /// <summary>
    /// Handles A2A v1.0 SCREAMING_SNAKE_CASE role values.
    /// Accepts both legacy ("user", "agent") and current ("ROLE_USER", "ROLE_AGENT") on read.
    /// Always emits current form on write.
    /// </summary>
    internal sealed class RoleJsonConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string value = reader.GetString();
            if (string.IsNullOrEmpty(value)) return value;

            // Accept legacy lowercase values for backward compatibility
            switch (value)
            {
                case "user": return "ROLE_USER";
                case "agent": return "ROLE_AGENT";
                default: return value; // Already ROLE_USER / ROLE_AGENT / ROLE_UNSPECIFIED
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    // ───────────────────── Part Converter ─────────────────────

    /// <summary>
    /// Custom JSON converter for A2APart implementing the v1.0 member-name discriminator pattern.
    /// No "type" field — the JSON member name itself (text/raw/url/data) identifies the part type.
    /// </summary>
    internal sealed class PartJsonConverter : JsonConverter<A2APart>
    {
        public override A2APart Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected StartObject token for Part");

            var part = new A2APart();
            string lastPropName = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    lastPropName = reader.GetString();
                    reader.Read();

                    switch (lastPropName)
                    {
                        case "text":
                            part.Text = reader.GetString();
                            part.Kind = PartKind.Text;
                            break;
                        case "raw":
                            part.Raw = reader.GetBytesFromBase64();
                            part.Kind = PartKind.File;
                            break;
                        case "url":
                            part.Url = reader.GetString();
                            part.Kind = PartKind.File;
                            break;
                        case "data":
                            part.Data = JsonSerializer.Deserialize<Dictionary<string, object>>(ref reader, options);
                            part.Kind = PartKind.Data;
                            break;
                        case "filename":
                            part.Filename = reader.GetString();
                            break;
                        case "mediaType":
                            part.MediaType = reader.GetString();
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
            }

            return part;
        }

        public override void Write(Utf8JsonWriter writer, A2APart value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            switch (value.Kind)
            {
                case PartKind.Text:
                    writer.WriteString("text", value.Text);
                    break;

                case PartKind.File:
                    if (value.Raw != null)
                    {
                        writer.WriteString("raw", Convert.ToBase64String(value.Raw));
                    }
                    if (!string.IsNullOrEmpty(value.Url))
                    {
                        writer.WriteString("url", value.Url);
                    }
                    break;

                case PartKind.Data:
                    if (value.Data != null)
                    {
                        writer.WritePropertyName("data");
                        JsonSerializer.Serialize(writer, value.Data, options);
                    }
                    break;
            }

            // Optional fields for file/data parts
            if (!string.IsNullOrEmpty(value.Filename))
            {
                writer.WriteString("filename", value.Filename);
            }
            if (!string.IsNullOrEmpty(value.MediaType))
            {
                writer.WriteString("mediaType", value.MediaType);
            }

            writer.WriteEndObject();
        }
    }

    // ───────────────────── A2A v1.0 Core Types ─────────────────────

    /// <summary>
    /// Task is the core unit of action in A2A. It has a current status
    /// and when results are created for the task they are stored in artifacts.
    /// Ref: A2A spec section 4.1.1
    /// </summary>
    public class A2ATask
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("contextId")]
        public string ContextId { get; set; }

        [JsonPropertyName("status")]
        public A2ATaskStatus Status { get; set; }

        [JsonPropertyName("artifacts")]
        public List<A2AArtifact> Artifacts { get; set; } = new List<A2AArtifact>();

        [JsonPropertyName("history")]
        public List<A2AMessage> History { get; set; } = new List<A2AMessage>();

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Defines the possible lifecycle states of a Task.
    /// Serialized as SCREAMING_SNAKE_CASE per A2A v1.0 spec.
    /// Ref: A2A spec section 4.1.2
    /// </summary>
    [JsonConverter(typeof(TaskStateJsonConverter))]
    public enum A2ATaskState
    {
        Unspecified = 0,
        Submitted = 1,
        Working = 2,
        Completed = 3,
        Failed = 4,
        Canceled = 5,
        InputRequired = 6,
        Rejected = 7,
        AuthRequired = 8
    }

    /// <summary>
    /// A container for the status of a task.
    /// Ref: A2A spec section 4.1.3
    /// </summary>
    public class A2ATaskStatus
    {
        [JsonPropertyName("state")]
        public A2ATaskState State { get; set; }

        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }
    }

    /// <summary>
    /// Artifacts represent task outputs.
    /// Ref: A2A spec section 4.1.7
    /// </summary>
    public class A2AArtifact
    {
        [JsonPropertyName("artifactId")]
        public string ArtifactId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("parts")]
        public List<A2APart> Parts { get; set; } = new List<A2APart>();

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }

        [JsonPropertyName("extensions")]
        public List<string> Extensions { get; set; } = new List<string>();
    }

    /// <summary>
    /// An event sent by the agent to notify the client of a change in a task's status.
    /// Ref: A2A spec section 4.1.8
    /// </summary>
    public class A2ATaskStatusUpdateEvent
    {
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; }

        [JsonPropertyName("contextId")]
        public string ContextId { get; set; }

        [JsonPropertyName("status")]
        public A2ATaskStatus Status { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// A task delta where an artifact has been generated.
    /// Ref: A2A spec section 4.1.9
    /// </summary>
    public class A2ATaskArtifactUpdateEvent
    {
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; }

        [JsonPropertyName("contextId")]
        public string ContextId { get; set; }

        [JsonPropertyName("artifact")]
        public A2AArtifact Artifact { get; set; }

        [JsonPropertyName("append")]
        public bool Append { get; set; }

        [JsonPropertyName("lastChunk")]
        public bool LastChunk { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// A wrapper object used in streaming operations (v1.0).
    /// Contains one of: Task, Message, TaskStatusUpdateEvent, TaskArtifactUpdateEvent.
    /// Ref: A2A spec section 5.2
    /// </summary>
    public class A2AStreamResponse
    {
        [JsonPropertyName("task")]
        public A2ATask Task { get; set; }

        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }

        [JsonPropertyName("statusUpdate")]
        public A2ATaskStatusUpdateEvent StatusUpdate { get; set; }

        [JsonPropertyName("artifactUpdate")]
        public A2ATaskArtifactUpdateEvent ArtifactUpdate { get; set; }
    }

    /// <summary>
    /// Response wrapper for the SendMessage method (v1.0).
    /// Contains one of: Task or Message.
    /// Ref: A2A spec section 9.4.1, proto SendMessageResponse
    /// </summary>
    public class A2ASendMessageResult
    {
        [JsonPropertyName("task")]
        public A2ATask Task { get; set; }

        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }
    }

    // ───────────────────── JSON-RPC 2.0 Envelope Types ─────────────────────

    /// <summary>
    /// JSON-RPC 2.0 request envelope. All A2A requests MUST use this format.
    /// </summary>
    public class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; }

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("params")]
        public object Params { get; set; }
    }

    /// <summary>
    /// JSON-RPC 2.0 response envelope. All A2A responses MUST use this format.
    /// </summary>
    public class JsonRpcResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError Error { get; set; }
    }

    /// <summary>
    /// JSON-RPC 2.0 error object.
    /// </summary>
    public class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("data")]
        public object Data { get; set; }
    }

    /// <summary>
    /// Params for the "SendMessage" JSON-RPC method.
    /// </summary>
    public class MessageSendParams
    {
        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }
    }

    /// <summary>
    /// Params for the "SendStreamingMessage" JSON-RPC method.
    /// </summary>
    public class MessageStreamParams
    {
        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }
    }

    // ───────────────────── Legacy Types (kept for backward compat) ─────────────────────

    /// <summary>
    /// Wire-format request for the A2A HTTP+JSON binding (legacy, pre-JSON-RPC).
    /// </summary>
    public class A2ASendMessageRequest
    {
        [JsonPropertyName("message")]
        public A2AMessage Message { get; set; }

        [JsonPropertyName("streaming")]
        public bool Streaming { get; set; }
    }

    /// <summary>
    /// Wire-format response for the A2A HTTP+JSON binding (legacy, pre-JSON-RPC).
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
