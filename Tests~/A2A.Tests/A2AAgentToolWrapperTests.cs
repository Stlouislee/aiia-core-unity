using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveLink.Agent.A2A;
using Microsoft.Extensions.AI;
using Xunit;

namespace A2A.Tests
{
    public class A2AAgentToolWrapperTests
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly Uri s_endpoint = new Uri("https://agent.example.com/a2a");

        // ───────────────────── Name Sanitization ─────────────────────

        [Theory]
        [InlineData("OpenClaw", "ask_openclaw")]
        [InlineData("Hermes Agent", "ask_hermes_agent")]
        [InlineData("My-Cool-Agent!", "ask_my_cool_agent")]
        [InlineData("agent_v2.0", "ask_agent_v20")]
        [InlineData("ALLCAPS", "ask_allcaps")]
        [InlineData("  spaces  ", "ask_spaces")]
        [InlineData("---dashes---", "ask_dashes")]
        public void Name_Sanitizes_DisplayName(string displayName, string expectedToolName)
        {
            var wrapper = CreateWrapper(displayName);
            Assert.Equal(expectedToolName, wrapper.Name);
        }

        [Fact]
        public void Name_FallbackForEmptyDisplayName()
        {
            var wrapper = CreateWrapper("");
            Assert.Equal("ask_remote_agent", wrapper.Name);
        }

        [Fact]
        public void Name_FallbackForNullDisplayName()
        {
            var wrapper = CreateWrapper(null);
            Assert.Equal("ask_remote_agent", wrapper.Name);
        }

        [Fact]
        public void Name_UsesCustomPrefix()
        {
            var wrapper = CreateWrapper("OpenClaw", toolNamePrefix: "delegate_");
            Assert.Equal("delegate_openclaw", wrapper.Name);
        }

        [Fact]
        public void Name_CustomPrefix_FallbackForEmptyDisplayName()
        {
            var wrapper = CreateWrapper("", toolNamePrefix: "query_");
            Assert.Equal("query_remote_agent", wrapper.Name);
        }

        // ───────────────────── Description ─────────────────────

        [Fact]
        public void Description_IncludesAgentCardDescription()
        {
            var card = new A2AAgentCard { Description = "I help with coding tasks." };
            var wrapper = CreateWrapper("TestAgent", card);

            Assert.Contains("TestAgent", wrapper.Description);
            Assert.Contains("I help with coding tasks.", wrapper.Description);
        }

        [Fact]
        public void Description_FallbackWithoutCard()
        {
            var wrapper = CreateWrapper("MyAgent", agentCard: null);

            Assert.Contains("MyAgent", wrapper.Description);
            Assert.Contains("Delegate a task", wrapper.Description);
        }

        [Fact]
        public void Description_FallbackWithEmptyCardDescription()
        {
            var card = new A2AAgentCard { Description = "" };
            var wrapper = CreateWrapper("MyAgent", card);

            Assert.Contains("MyAgent", wrapper.Description);
        }

        // ───────────────────── JSON Schema ─────────────────────

        [Fact]
        public void JsonSchema_HasMessageProperty()
        {
            var wrapper = CreateWrapper("Test");
            JsonElement schema = wrapper.JsonSchema;

            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.True(schema.GetProperty("properties").TryGetProperty("message", out JsonElement messageProp));
            Assert.Equal("string", messageProp.GetProperty("type").GetString());
        }

        [Fact]
        public void JsonSchema_RequiresMessage()
        {
            var wrapper = CreateWrapper("Test");
            JsonElement schema = wrapper.JsonSchema;

            JsonElement required = schema.GetProperty("required");
            Assert.Equal(1, required.GetArrayLength());
            Assert.Equal("message", required[0].GetString());
        }

        // ───────────────────── Tool Invocation ─────────────────────

        [Fact]
        public async Task InvokeAsync_ReturnsAgentResponse()
        {
            var resultMsg = new A2AMessage
            {
                Role = "ROLE_AGENT",
                Parts = new List<A2APart> { A2APart.FromText("42") }
            };
            string responseJson = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "test",
                Result = JsonSerializer.SerializeToElement(resultMsg, s_jsonOptions)
            }, s_jsonOptions);

            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                });

            var wrapper = CreateWrapper("MathAgent", handler: handler);

            var args = new AIFunctionArguments(new Dictionary<string, object>
            {
                ["message"] = "What is 6 * 7?"
            });

            object result = await wrapper.InvokeAsync(args);

            Assert.Equal("42", result);
        }

        [Fact]
        public async Task InvokeAsync_MissingMessage_ReturnsError()
        {
            var wrapper = CreateWrapper("Test");

            var args = new AIFunctionArguments(new Dictionary<string, object>());

            object result = await wrapper.InvokeAsync(args);

            Assert.Contains("required", result.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task InvokeAsync_EmptyMessage_ReturnsError()
        {
            var wrapper = CreateWrapper("Test");

            var args = new AIFunctionArguments(new Dictionary<string, object>
            {
                ["message"] = "   "
            });

            object result = await wrapper.InvokeAsync(args);

            Assert.Contains("required", result.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task InvokeAsync_ServerError_ReturnsErrorMessage()
        {
            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var wrapper = CreateWrapper("Test", handler: handler);

            var args = new AIFunctionArguments(new Dictionary<string, object>
            {
                ["message"] = "hello"
            });

            object result = await wrapper.InvokeAsync(args);

            Assert.Contains("Error", result.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task InvokeAsync_FiresOnToolCall()
        {
            string capturedToolName = null;
            string capturedArgs = null;

            var resultMsg = A2AMessage.CreateUserTextMessage("ok");
            string responseJson = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "test",
                Result = JsonSerializer.SerializeToElement(resultMsg, s_jsonOptions)
            }, s_jsonOptions);

            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                });

            var wrapper = CreateWrapper("Test", handler: handler, onToolCall: (name, args) =>
            {
                capturedToolName = name;
                capturedArgs = args;
            });

            var invokeArgs = new AIFunctionArguments(new Dictionary<string, object>
            {
                ["message"] = "trigger callback"
            });

            await wrapper.InvokeAsync(invokeArgs);

            Assert.Equal("ask_test", capturedToolName);
            Assert.Contains("trigger callback", capturedArgs);
        }

        [Fact]
        public async Task InvokeAsync_AgentCardWithStreaming_UsesStreaming()
        {
            string chunk1Json = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "stream",
                Result = JsonSerializer.SerializeToElement(new A2AMessage
                {
                    MessageId = "r1", Role = "ROLE_AGENT",
                    Parts = new List<A2APart> { A2APart.FromText("chunk1") }
                }, s_jsonOptions)
            }, s_jsonOptions);

            string chunk2Json = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "stream",
                Result = JsonSerializer.SerializeToElement(new A2AMessage
                {
                    MessageId = "r1", Role = "ROLE_AGENT",
                    Parts = new List<A2APart> { A2APart.FromText("chunk2") }
                }, s_jsonOptions)
            }, s_jsonOptions);

            string ssePayload =
                "event: message\n" +
                "data: " + chunk1Json + "\n" +
                "\n" +
                "event: message\n" +
                "data: " + chunk2Json + "\n" +
                "\n" +
                "event: complete\n" +
                "data: {\"status\":\"completed\"}\n" +
                "\n";

            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
                });

            var card = new A2AAgentCard
            {
                Name = "StreamAgent",
                Capabilities = new A2ACapabilities { Streaming = true }
            };

            var wrapper = CreateWrapper("StreamAgent", agentCard: card, handler: handler, enableStreaming: true);

            var args = new AIFunctionArguments(new Dictionary<string, object>
            {
                ["message"] = "stream test"
            });

            object result = await wrapper.InvokeAsync(args);

            Assert.Equal("chunk1chunk2", result);
        }

        [Fact]
        public async Task InvokeAsync_MultipleTextParts_Concatenates()
        {
            var resultMsg = new A2AMessage
            {
                Role = "ROLE_AGENT",
                Parts = new List<A2APart>
                {
                    A2APart.FromText("Part 1. "),
                    A2APart.FromText("Part 2.")
                }
            };
            string responseJson = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "test",
                Result = JsonSerializer.SerializeToElement(resultMsg, s_jsonOptions)
            }, s_jsonOptions);

            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                });

            var wrapper = CreateWrapper("Test", handler: handler);

            var args = new AIFunctionArguments(new Dictionary<string, object>
            {
                ["message"] = "multi-part test"
            });

            object result = await wrapper.InvokeAsync(args);

            Assert.Equal("Part 1. Part 2.", result);
        }

        // ───────────────────── Helpers ─────────────────────

        private static A2AAgentToolWrapper CreateWrapper(
            string displayName,
            A2AAgentCard agentCard = null,
            HttpMessageHandler handler = null,
            bool enableStreaming = false,
            Action<string, string> onToolCall = null,
            string toolNamePrefix = null)
        {
            var client = new A2AClient(s_endpoint, handler ?? new NoOpHandler());
            return new A2AAgentToolWrapper(displayName, client, agentCard, enableStreaming, onToolCall,
                toolNamePrefix: toolNamePrefix ?? "ask_");
        }

        private class MockHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }

        private class NoOpHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
