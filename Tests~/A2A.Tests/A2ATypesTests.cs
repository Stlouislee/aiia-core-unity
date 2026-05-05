using System.Collections.Generic;
using System.Text.Json;
using LiveLink.Agent.A2A;
using Xunit;

namespace A2A.Tests
{
    public class A2ATypesTests
    {
        private static readonly JsonSerializerOptions s_options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ───────────────────── Agent Card ─────────────────────

        [Fact]
        public void AgentCard_RoundTrip()
        {
            var card = new A2AAgentCard
            {
                Name = "TestAgent",
                Description = "A test agent",
                Version = "1.0.0",
                SupportedInterfaces = new List<A2AInterface>
                {
                    new A2AInterface
                    {
                        Url = "https://example.com/a2a/test",
                        ProtocolBinding = "HTTP+JSON",
                        ProtocolVersion = "1.0"
                    }
                },
                Skills = new List<A2ASkill>
                {
                    new A2ASkill { Id = "echo", Name = "Echo", Description = "Echoes back", Tags = new List<string> { "test" } }
                },
                Capabilities = new A2ACapabilities { Streaming = true },
                DefaultInputModes = new List<string> { "text/plain" },
                DefaultOutputModes = new List<string> { "text/plain" }
            };

            string json = JsonSerializer.Serialize(card, s_options);
            A2AAgentCard deserialized = JsonSerializer.Deserialize<A2AAgentCard>(json, s_options);

            Assert.Equal("TestAgent", deserialized.Name);
            Assert.Equal("A test agent", deserialized.Description);
            Assert.Equal("1.0.0", deserialized.Version);
            Assert.Single(deserialized.SupportedInterfaces);
            Assert.Equal("https://example.com/a2a/test", deserialized.SupportedInterfaces[0].Url);
            Assert.Single(deserialized.Skills);
            Assert.Equal("echo", deserialized.Skills[0].Id);
            Assert.True(deserialized.Capabilities.Streaming);
        }

        [Fact]
        public void AgentCard_DeserializesFromRealWorldJson()
        {
            // Simulates a real agent card response.
            string json = @"{
                ""name"": ""OpenClaw Agent"",
                ""description"": ""A helpful AI assistant"",
                ""version"": ""2.0.0"",
                ""supportedInterfaces"": [{
                    ""url"": ""https://openclaw.example.com/a2a"",
                    ""protocolBinding"": ""HTTP+JSON"",
                    ""protocolVersion"": ""1.0""
                }],
                ""skills"": [
                    { ""id"": ""chat"", ""name"": ""Chat"", ""description"": ""General conversation"", ""tags"": [""chat""] },
                    { ""id"": ""code"", ""name"": ""Code"", ""description"": ""Write code"", ""tags"": [""code"", ""dev""] }
                ],
                ""capabilities"": { ""streaming"": true, ""pushNotifications"": false }
            }";

            A2AAgentCard card = JsonSerializer.Deserialize<A2AAgentCard>(json, s_options);

            Assert.Equal("OpenClaw Agent", card.Name);
            Assert.Equal(2, card.Skills.Count);
            Assert.Equal("code", card.Skills[1].Id);
            Assert.True(card.Capabilities.Streaming);
            Assert.False(card.Capabilities.PushNotifications);
        }

        [Fact]
        public void AgentCard_HandlesNullFields()
        {
            string json = @"{ ""name"": ""Minimal"" }";
            A2AAgentCard card = JsonSerializer.Deserialize<A2AAgentCard>(json, s_options);

            Assert.Equal("Minimal", card.Name);
            Assert.Null(card.Description);
            Assert.NotNull(card.SupportedInterfaces);
            Assert.Empty(card.SupportedInterfaces);
        }

        // ───────────────────── Message ─────────────────────

        [Fact]
        public void Message_CreateUserTextMessage()
        {
            A2AMessage msg = A2AMessage.CreateUserTextMessage("Hello, agent!");

            Assert.Equal("user", msg.Role);
            Assert.NotNull(msg.MessageId);
            Assert.Single(msg.Parts);
            Assert.Equal("text", msg.Parts[0].Type);
            Assert.Equal("Hello, agent!", msg.Parts[0].Text);
        }

        [Fact]
        public void Message_RoundTrip()
        {
            var msg = new A2AMessage
            {
                MessageId = "test-123",
                Role = "agent",
                Parts = new List<A2APart>
                {
                    A2APart.FromText("Sure, I can help with that."),
                    new A2APart { Type = "data", Data = new Dictionary<string, object> { ["key"] = "value" } }
                },
                TaskId = "task-abc"
            };

            string json = JsonSerializer.Serialize(msg, s_options);
            A2AMessage deserialized = JsonSerializer.Deserialize<A2AMessage>(json, s_options);

            Assert.Equal("test-123", deserialized.MessageId);
            Assert.Equal("agent", deserialized.Role);
            Assert.Equal(2, deserialized.Parts.Count);
            Assert.Equal("text", deserialized.Parts[0].Type);
            Assert.Equal("Sure, I can help with that.", deserialized.Parts[0].Text);
            Assert.Equal("data", deserialized.Parts[1].Type);
            Assert.Equal("task-abc", deserialized.TaskId);
        }

        [Fact]
        public void Part_FromText_CreatesTextPart()
        {
            A2APart part = A2APart.FromText("hello");

            Assert.Equal("text", part.Type);
            Assert.Equal("hello", part.Text);
            Assert.Null(part.File);
            Assert.Null(part.Data);
        }

        // ───────────────────── Request / Response ─────────────────────

        [Fact]
        public void SendMessageRequest_RoundTrip()
        {
            var req = new A2ASendMessageRequest
            {
                Message = A2AMessage.CreateUserTextMessage("test"),
                Streaming = true
            };

            string json = JsonSerializer.Serialize(req, s_options);
            A2ASendMessageRequest deserialized = JsonSerializer.Deserialize<A2ASendMessageRequest>(json, s_options);

            Assert.True(deserialized.Streaming);
            Assert.Equal("test", deserialized.Message.Parts[0].Text);
        }

        [Fact]
        public void SendMessageResponse_WithError()
        {
            string json = @"{
                ""error"": { ""code"": -32600, ""message"": ""Invalid Request"" }
            }";

            A2ASendMessageResponse resp = JsonSerializer.Deserialize<A2ASendMessageResponse>(json, s_options);

            Assert.Null(resp.Message);
            Assert.NotNull(resp.Error);
            Assert.Equal(-32600, resp.Error.Code);
            Assert.Equal("Invalid Request", resp.Error.Message);
        }

        [Fact]
        public void SendMessageResponse_WithMessage()
        {
            string json = @"{
                ""message"": {
                    ""messageId"": ""r1"",
                    ""role"": ""agent"",
                    ""parts"": [{ ""type"": ""text"", ""text"": ""Done!"" }]
                }
            }";

            A2ASendMessageResponse resp = JsonSerializer.Deserialize<A2ASendMessageResponse>(json, s_options);

            Assert.Null(resp.Error);
            Assert.NotNull(resp.Message);
            Assert.Equal("Done!", resp.Message.Parts[0].Text);
        }

        // ───────────────────── JSON-RPC 2.0 Envelope ─────────────────────

        [Fact]
        public void JsonRpcRequest_RoundTrip()
        {
            var req = new JsonRpcRequest
            {
                Method = "message/send",
                Id = "req-1",
                Params = new MessageSendParams { Message = A2AMessage.CreateUserTextMessage("hello") }
            };

            string json = JsonSerializer.Serialize(req, s_options);
            JsonRpcRequest deserialized = JsonSerializer.Deserialize<JsonRpcRequest>(json, s_options);

            Assert.Equal("2.0", deserialized.JsonRpc);
            Assert.Equal("message/send", deserialized.Method);
            Assert.Equal("req-1", deserialized.Id);
            Assert.NotNull(deserialized.Params);
        }

        [Fact]
        public void JsonRpcRequest_HasCorrectWireFormat()
        {
            var req = new JsonRpcRequest
            {
                Method = "message/send",
                Id = "abc-123",
                Params = new MessageSendParams { Message = A2AMessage.CreateUserTextMessage("test") }
            };

            string json = JsonSerializer.Serialize(req, s_options);

            Assert.Contains("\"jsonrpc\":\"2.0\"", json);
            Assert.Contains("\"method\":\"message/send\"", json);
            Assert.Contains("\"id\":\"abc-123\"", json);
            Assert.Contains("\"params\":", json);
        }

        [Fact]
        public void JsonRpcResponse_WithError()
        {
            string json = @"{
                ""jsonrpc"": ""2.0"",
                ""id"": ""req-1"",
                ""error"": { ""code"": -32600, ""message"": ""Invalid Request"" }
            }";

            JsonRpcResponse resp = JsonSerializer.Deserialize<JsonRpcResponse>(json, s_options);

            Assert.Equal("2.0", resp.JsonRpc);
            Assert.Equal("req-1", resp.Id);
            Assert.False(resp.Result.HasValue);
            Assert.NotNull(resp.Error);
            Assert.Equal(-32600, resp.Error.Code);
            Assert.Equal("Invalid Request", resp.Error.Message);
        }

        [Fact]
        public void JsonRpcResponse_WithResult()
        {
            string json = @"{
                ""jsonrpc"": ""2.0"",
                ""id"": ""req-1"",
                ""result"": {
                    ""messageId"": ""r1"",
                    ""role"": ""agent"",
                    ""parts"": [{ ""type"": ""text"", ""text"": ""Done!"" }]
                }
            }";

            JsonRpcResponse resp = JsonSerializer.Deserialize<JsonRpcResponse>(json, s_options);

            Assert.Equal("2.0", resp.JsonRpc);
            Assert.Equal("req-1", resp.Id);
            Assert.Null(resp.Error);
            Assert.True(resp.Result.HasValue);

            A2AMessage msg = resp.Result.Value.Deserialize<A2AMessage>(s_options);
            Assert.Equal("Done!", msg.Parts[0].Text);
        }

        [Fact]
        public void MessageSendParams_RoundTrip()
        {
            var p = new MessageSendParams { Message = A2AMessage.CreateUserTextMessage("hi") };
            string json = JsonSerializer.Serialize(p, s_options);
            MessageSendParams d = JsonSerializer.Deserialize<MessageSendParams>(json, s_options);

            Assert.Equal("hi", d.Message.Parts[0].Text);
        }

        [Fact]
        public void MessageStreamParams_RoundTrip()
        {
            var p = new MessageStreamParams { Message = A2AMessage.CreateUserTextMessage("stream") };
            string json = JsonSerializer.Serialize(p, s_options);
            MessageStreamParams d = JsonSerializer.Deserialize<MessageStreamParams>(json, s_options);

            Assert.Equal("stream", d.Message.Parts[0].Text);
        }

        // ───────────────────── Streaming Event ─────────────────────

        [Fact]
        public void StreamingEvent_Deserializes()
        {
            string json = @"{
                ""message"": {
                    ""messageId"": ""chunk1"",
                    ""role"": ""agent"",
                    ""parts"": [{ ""type"": ""text"", ""text"": ""partial..."" }]
                }
            }";

            A2AStreamingEvent evt = JsonSerializer.Deserialize<A2AStreamingEvent>(json, s_options);

            Assert.NotNull(evt.Message);
            Assert.Equal("partial...", evt.Message.Parts[0].Text);
        }

        [Fact]
        public void StreamingEvent_CompleteStatus()
        {
            string json = @"{ ""taskId"": ""t1"", ""status"": ""completed"" }";

            A2AStreamingEvent evt = JsonSerializer.Deserialize<A2AStreamingEvent>(json, s_options);

            Assert.Equal("t1", evt.TaskId);
            Assert.Equal("completed", evt.Status);
            Assert.Null(evt.Message);
        }
    }
}
