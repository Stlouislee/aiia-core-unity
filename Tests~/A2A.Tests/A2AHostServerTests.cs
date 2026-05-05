using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveLink.Agent.A2A;
using Xunit;

namespace A2A.Tests
{
    public class A2AHostServerTests
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private static A2AHostConfig CreateTestConfig(int port = 0, string authToken = null)
        {
            return new A2AHostConfig
            {
            };
        }

        // ───────────────────── Agent Card Builder ─────────────────────

        [Fact]
        public void AgentCardBuilder_BuildsValidCard()
        {
            var config = new A2AHostConfig();
            string json = A2AAgentCardBuilder.BuildCardJson(config, "https://my-agent.example.com");

            A2AAgentCard card = JsonSerializer.Deserialize<A2AAgentCard>(json, s_jsonOptions);

            Assert.NotNull(card);
            Assert.NotNull(card.Name);
            Assert.NotNull(card.Version);
            Assert.NotNull(card.SupportedInterfaces);
            Assert.Single(card.SupportedInterfaces);
            Assert.Equal("HTTP+JSON", card.SupportedInterfaces[0].ProtocolBinding);
            Assert.Contains("my-agent.example.com", card.SupportedInterfaces[0].Url);
            Assert.EndsWith("/a2a", card.SupportedInterfaces[0].Url);
        }

        [Fact]
        public void AgentCardBuilder_AppendsA2aPathIfMissing()
        {
            var config = new A2AHostConfig();
            string json = A2AAgentCardBuilder.BuildCardJson(config, "https://host.example.com/api");

            A2AAgentCard card = JsonSerializer.Deserialize<A2AAgentCard>(json, s_jsonOptions);

            Assert.EndsWith("/a2a", card.SupportedInterfaces[0].Url);
        }

        [Fact]
        public void AgentCardBuilder_DefaultUrlUsesLocalhost()
        {
            var config = new A2AHostConfig();
            string json = A2AAgentCardBuilder.BuildCardJson(config, null);

            A2AAgentCard card = JsonSerializer.Deserialize<A2AAgentCard>(json, s_jsonOptions);

            Assert.Contains("localhost", card.SupportedInterfaces[0].Url);
        }

        // ───────────────────── A2AMessage.CreateAgentTextMessage ─────────────────────

        [Fact]
        public void CreateAgentTextMessage_SetsCorrectRole()
        {
            A2AMessage msg = A2AMessage.CreateAgentTextMessage("I can help with that.");

            Assert.Equal("ROLE_AGENT", msg.Role);
            Assert.Single(msg.Parts);
            Assert.Equal(PartKind.Text, msg.Parts[0].Kind);
            Assert.Equal("I can help with that.", msg.Parts[0].Text);
        }

        // ───────────────────── Host Server Integration ─────────────────────

        [Fact]
        public async Task HostServer_StartsAndServesHealthCheck()
        {
            var config = CreateTestConfigWithPort(0);
            

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("echo: " + msg));

            try
            {
                server.Start();
                Assert.True(server.IsRunning);
                int port = server.Port;

                using (var client = new HttpClient())
                {
                    HttpResponseMessage response = await client.GetAsync($"http://localhost:{port}/health");
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();

                    var health = JsonSerializer.Deserialize<JsonElement>(body);
                    Assert.Equal("ok", health.GetProperty("status").GetString());
                    Assert.Equal("A2A", health.GetProperty("protocol").GetString());
                }
            }
            finally
            {
                server.Dispose();
                Assert.False(server.IsRunning);
            }
        }

        [Fact]
        public async Task HostServer_ServesAgentCard()
        {
            var config = CreateTestConfigWithPort(0);

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("ok"));

            try
            {
                server.Start();
                int port = server.Port;

                using (var client = new HttpClient())
                {
                    HttpResponseMessage response = await client.GetAsync(
                        $"http://localhost:{port}/.well-known/agent-card.json");
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();

                    A2AAgentCard card = JsonSerializer.Deserialize<A2AAgentCard>(body, s_jsonOptions);
                    Assert.NotNull(card);
                    Assert.NotNull(card.Name);
                    Assert.NotNull(card.Capabilities);
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        [Fact]
        public async Task HostServer_HandlesSendMessage()
        {
            var config = CreateTestConfigWithPort(0);

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("You said: " + msg));

            try
            {
                server.Start();
                int port = server.Port;

                var rpcRequest = new JsonRpcRequest
                {
                    Method = "SendMessage",
                    Id = "test-req-1",
                    Params = new MessageSendParams { Message = A2AMessage.CreateUserTextMessage("Hello agent!") }
                };

                string requestJson = JsonSerializer.Serialize(rpcRequest, s_jsonOptions);

                using (var client = new HttpClient())
                {
                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(
                        $"http://localhost:{port}/a2a", content);
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();

                    JsonRpcResponse result = JsonSerializer
                        .Deserialize<JsonRpcResponse>(body, s_jsonOptions);

                    Assert.NotNull(result);
                    Assert.Equal("2.0", result.JsonRpc);
                    Assert.Equal("test-req-1", result.Id);
                    Assert.Null(result.Error);
                    Assert.True(result.Result.HasValue);

                    A2AMessage msg = result.Result.Value.Deserialize<A2AMessage>(s_jsonOptions);
                    Assert.Equal("ROLE_AGENT", msg.Role);
                    Assert.Equal("You said: Hello agent!", msg.Parts[0].Text);
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        [Fact]
        public async Task HostServer_ReturnsJsonRpcErrorForEmptyBody()
        {
            var config = CreateTestConfigWithPort(0);

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("ok"));

            try
            {
                server.Start();
                int port = server.Port;

                using (var client = new HttpClient())
                {
                    var content = new StringContent("", Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(
                        $"http://localhost:{port}/a2a", content);

                    string body = await response.Content.ReadAsStringAsync();
                    JsonRpcResponse result = JsonSerializer.Deserialize<JsonRpcResponse>(body, s_jsonOptions);

                    Assert.NotNull(result);
                    Assert.Equal("2.0", result.JsonRpc);
                    Assert.NotNull(result.Error);
                    Assert.Equal(-32700, result.Error.Code);
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        [Fact]
        public async Task HostServer_Returns404ForUnknownPath()
        {
            var config = CreateTestConfigWithPort(0);

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("ok"));

            try
            {
                server.Start();
                int port = server.Port;

                using (var client = new HttpClient())
                {
                    HttpResponseMessage response = await client.GetAsync(
                        $"http://localhost:{port}/nonexistent");
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        [Fact]
        public async Task HostServer_Auth_RejectsWithoutToken()
        {
            var config = CreateTestConfigWithPort(0, authToken: "secret-token");

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("ok"));

            try
            {
                server.Start();
                int port = server.Port;

                using (var client = new HttpClient())
                {
                    HttpResponseMessage response = await client.GetAsync(
                        $"http://localhost:{port}/health");
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        [Fact]
        public async Task HostServer_Auth_AcceptsWithValidToken()
        {
            var config = CreateTestConfigWithPort(0, authToken: "secret-token");

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("ok"));

            try
            {
                server.Start();
                int port = server.Port;

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token");

                    HttpResponseMessage response = await client.GetAsync(
                        $"http://localhost:{port}/health");
                    response.EnsureSuccessStatusCode();
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        [Fact]
        public async Task HostServer_HandlesStreamingRequest()
        {
            var config = CreateTestConfigWithPort(0);

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("streaming response"));

            try
            {
                server.Start();
                int port = server.Port;

                var rpcRequest = new JsonRpcRequest
                {
                    Method = "SendStreamingMessage",
                    Id = "stream-req-1",
                    Params = new MessageStreamParams { Message = A2AMessage.CreateUserTextMessage("stream test") }
                };

                string requestJson = JsonSerializer.Serialize(rpcRequest, s_jsonOptions);

                using (var client = new HttpClient())
                {
                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(
                        $"http://localhost:{port}/a2a", content);
                    response.EnsureSuccessStatusCode();

                    string body = await response.Content.ReadAsStringAsync();
                    Assert.Contains("event: message", body);
                    Assert.Contains("streaming response", body);
                    Assert.Contains("event: complete", body);
                    Assert.Contains("\"jsonrpc\":\"2.0\"", body);
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        [Fact]
        public async Task HostServer_RejectsUnknownMethod()
        {
            var config = CreateTestConfigWithPort(0);

            var server = new A2AHostServer(config, (msg, ct) => Task.FromResult("ok"));

            try
            {
                server.Start();
                int port = server.Port;

                var rpcRequest = new JsonRpcRequest
                {
                    Method = "UnknownMethod",
                    Id = "test-unknown",
                    Params = new { }
                };

                string requestJson = JsonSerializer.Serialize(rpcRequest, s_jsonOptions);

                using (var client = new HttpClient())
                {
                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(
                        $"http://localhost:{port}/a2a", content);

                    string body = await response.Content.ReadAsStringAsync();
                    JsonRpcResponse result = JsonSerializer.Deserialize<JsonRpcResponse>(body, s_jsonOptions);

                    Assert.NotNull(result);
                    Assert.Equal("2.0", result.JsonRpc);
                    Assert.NotNull(result.Error);
                    Assert.Equal(-32601, result.Error.Code); // Method not found
                }
            }
            finally
            {
                server.Dispose();
            }
        }

        // ───────────────────── Helpers ─────────────────────

        private static A2AHostConfig CreateTestConfigWithPort(int port, string authToken = null)
        {
            var config = new A2AHostConfig();
            SetPort(config, port);
            if (authToken != null)
            {
                SetAuthToken(config, authToken);
            }
            return config;
        }

        private static void SetPort(A2AHostConfig config, int port)
        {
            typeof(A2AHostConfig).GetField("_port",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(config, port);
        }

        private static void SetAuthToken(A2AHostConfig config, string token)
        {
            typeof(A2AHostConfig).GetField("_authToken",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(config, token);
        }
    }
}
