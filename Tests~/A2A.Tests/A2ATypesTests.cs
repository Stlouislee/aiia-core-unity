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

            Assert.Equal("ROLE_USER", msg.Role);
            Assert.NotNull(msg.MessageId);
            Assert.Single(msg.Parts);
            Assert.Equal(PartKind.Text, msg.Parts[0].Kind);
            Assert.Equal("Hello, agent!", msg.Parts[0].Text);
        }

        [Fact]
        public void Message_RoundTrip()
        {
            var msg = new A2AMessage
            {
                MessageId = "test-123",
                Role = "ROLE_AGENT",
                Parts = new List<A2APart>
                {
                    A2APart.FromText("Sure, I can help with that."),
                    A2APart.FromData(new Dictionary<string, object> { ["key"] = "value" })
                },
                TaskId = "task-abc"
            };

            string json = JsonSerializer.Serialize(msg, s_options);
            A2AMessage deserialized = JsonSerializer.Deserialize<A2AMessage>(json, s_options);

            Assert.Equal("test-123", deserialized.MessageId);
            Assert.Equal("ROLE_AGENT", deserialized.Role);
            Assert.Equal(2, deserialized.Parts.Count);
            Assert.Equal(PartKind.Text, deserialized.Parts[0].Kind);
            Assert.Equal("Sure, I can help with that.", deserialized.Parts[0].Text);
            Assert.Equal(PartKind.Data, deserialized.Parts[1].Kind);
            Assert.Equal("task-abc", deserialized.TaskId);
        }

        [Fact]
        public void Part_FromText_CreatesTextPart()
        {
            A2APart part = A2APart.FromText("hello");

            Assert.Equal(PartKind.Text, part.Kind);
            Assert.Equal("hello", part.Text);
            Assert.Null(part.Raw);
            Assert.Null(part.Data);
        }

        // ───────────────────── Part v1.0 Member-Name Discriminator ─────────────────────

        [Fact]
        public void Part_TextPart_SerializesWithoutTypeField()
        {
            var part = A2APart.FromText("Hello, world!");
            string json = JsonSerializer.Serialize(part, s_options);

            // Must NOT contain "type" field
            Assert.DoesNotContain("\"type\"", json);
            // Must contain "text" as the discriminator member
            Assert.Contains("\"text\":\"Hello, world!\"", json);
        }

        [Fact]
        public void Part_TextPart_RoundTrip()
        {
            var part = A2APart.FromText("test content");
            string json = JsonSerializer.Serialize(part, s_options);
            A2APart deserialized = JsonSerializer.Deserialize<A2APart>(json, s_options);

            Assert.Equal(PartKind.Text, deserialized.Kind);
            Assert.Equal("test content", deserialized.Text);
        }

        [Fact]
        public void Part_FilePart_WithRaw_RoundTrip()
        {
            byte[] raw = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"
            var part = A2APart.FromRaw(raw, "test.txt", "text/plain");
            string json = JsonSerializer.Serialize(part, s_options);

            Assert.DoesNotContain("\"type\"", json);
            Assert.Contains("\"raw\"", json);
            Assert.Contains("\"filename\":\"test.txt\"", json);
            Assert.Contains("\"mediaType\":\"text/plain\"", json);

            A2APart deserialized = JsonSerializer.Deserialize<A2APart>(json, s_options);
            Assert.Equal(PartKind.File, deserialized.Kind);
            Assert.Equal(raw, deserialized.Raw);
            Assert.Equal("test.txt", deserialized.Filename);
            Assert.Equal("text/plain", deserialized.MediaType);
        }

        [Fact]
        public void Part_FilePart_WithUrl_RoundTrip()
        {
            var part = A2APart.FromUrl("https://example.com/image.png", "image.png", "image/png");
            string json = JsonSerializer.Serialize(part, s_options);

            Assert.DoesNotContain("\"type\"", json);
            Assert.Contains("\"url\":\"https://example.com/image.png\"", json);
            Assert.Contains("\"filename\":\"image.png\"", json);

            A2APart deserialized = JsonSerializer.Deserialize<A2APart>(json, s_options);
            Assert.Equal(PartKind.File, deserialized.Kind);
            Assert.Equal("https://example.com/image.png", deserialized.Url);
            Assert.Equal("image.png", deserialized.Filename);
        }

        [Fact]
        public void Part_DataPart_RoundTrip()
        {
            var data = new Dictionary<string, object>
            {
                ["name"] = "test",
                ["count"] = 42
            };
            var part = A2APart.FromData(data, "application/json");
            string json = JsonSerializer.Serialize(part, s_options);

            Assert.DoesNotContain("\"type\"", json);
            Assert.Contains("\"data\"", json);
            Assert.Contains("\"mediaType\":\"application/json\"", json);

            A2APart deserialized = JsonSerializer.Deserialize<A2APart>(json, s_options);
            Assert.Equal(PartKind.Data, deserialized.Kind);
            Assert.NotNull(deserialized.Data);
            Assert.Equal("application/json", deserialized.MediaType);
        }

        [Fact]
        public void Part_DeserializesV10JsonWithoutTypeField()
        {
            // v1.0 format: no "type" field, member name is the discriminator
            string textJson = @"{ ""text"": ""Hello from v1.0"" }";
            A2APart textPart = JsonSerializer.Deserialize<A2APart>(textJson, s_options);
            Assert.Equal(PartKind.Text, textPart.Kind);
            Assert.Equal("Hello from v1.0", textPart.Text);

            string dataJson = @"{ ""data"": { ""key"": ""value"" }, ""mediaType"": ""application/json"" }";
            A2APart dataPart = JsonSerializer.Deserialize<A2APart>(dataJson, s_options);
            Assert.Equal(PartKind.Data, dataPart.Kind);
            Assert.NotNull(dataPart.Data);
        }

        // ───────────────────── Role v1.0 SCREAMING_SNAKE_CASE ─────────────────────

        [Fact]
        public void Role_SerializesAsScreamingSnakeCase()
        {
            var msg = A2AMessage.CreateUserTextMessage("hi");
            string json = JsonSerializer.Serialize(msg, s_options);
            Assert.Contains("\"ROLE_USER\"", json);
            Assert.DoesNotContain("\"user\"", json);

            var agentMsg = A2AMessage.CreateAgentTextMessage("hello");
            string agentJson = JsonSerializer.Serialize(agentMsg, s_options);
            Assert.Contains("\"ROLE_AGENT\"", agentJson);
        }

        [Fact]
        public void Role_DeserializesLegacyUser()
        {
            string json = @"{ ""messageId"": ""m1"", ""role"": ""user"", ""parts"": [{ ""text"": ""hi"" }] }";
            A2AMessage msg = JsonSerializer.Deserialize<A2AMessage>(json, s_options);
            Assert.Equal("ROLE_USER", msg.Role);
        }

        [Fact]
        public void Role_DeserializesLegacyAgent()
        {
            string json = @"{ ""messageId"": ""m1"", ""role"": ""agent"", ""parts"": [{ ""text"": ""hi"" }] }";
            A2AMessage msg = JsonSerializer.Deserialize<A2AMessage>(json, s_options);
            Assert.Equal("ROLE_AGENT", msg.Role);
        }

        [Fact]
        public void Role_DeserializesV10Format()
        {
            string json = @"{ ""messageId"": ""m1"", ""role"": ""ROLE_USER"", ""parts"": [{ ""text"": ""hi"" }] }";
            A2AMessage msg = JsonSerializer.Deserialize<A2AMessage>(json, s_options);
            Assert.Equal("ROLE_USER", msg.Role);
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
                    ""role"": ""ROLE_AGENT"",
                    ""parts"": [{ ""text"": ""Done!"" }]
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
                Method = "SendMessage",
                Id = "req-1",
                Params = new MessageSendParams { Message = A2AMessage.CreateUserTextMessage("hello") }
            };

            string json = JsonSerializer.Serialize(req, s_options);
            JsonRpcRequest deserialized = JsonSerializer.Deserialize<JsonRpcRequest>(json, s_options);

            Assert.Equal("2.0", deserialized.JsonRpc);
            Assert.Equal("SendMessage", deserialized.Method);
            Assert.Equal("req-1", deserialized.Id);
            Assert.NotNull(deserialized.Params);
        }

        [Fact]
        public void JsonRpcRequest_HasCorrectWireFormat()
        {
            var req = new JsonRpcRequest
            {
                Method = "SendMessage",
                Id = "abc-123",
                Params = new MessageSendParams { Message = A2AMessage.CreateUserTextMessage("test") }
            };

            string json = JsonSerializer.Serialize(req, s_options);

            Assert.Contains("\"jsonrpc\":\"2.0\"", json);
            Assert.Contains("\"method\":\"SendMessage\"", json);
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
                    ""role"": ""ROLE_AGENT"",
                    ""parts"": [{ ""text"": ""Done!"" }]
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
                    ""role"": ""ROLE_AGENT"",
                    ""parts"": [{ ""text"": ""partial..."" }]
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
