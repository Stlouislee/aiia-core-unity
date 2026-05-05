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
    public class A2AClientTests
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly Uri s_testEndpoint = new Uri("https://test-agent.example.com/a2a");
        private static readonly Uri s_testHost = new Uri("https://test-agent.example.com");

        // ───────────────────── Agent Card Discovery ─────────────────────

        [Fact]
        public async Task GetAgentCardAsync_ReturnsCard()
        {
            string cardJson = JsonSerializer.Serialize(new A2AAgentCard
            {
                Name = "TestAgent",
                Version = "1.0.0",
                Description = "A test agent",
                SupportedInterfaces = new List<A2AInterface>
                {
                    new A2AInterface { Url = "https://test-agent.example.com/a2a", ProtocolBinding = "HTTP+JSON" }
                },
                Capabilities = new A2ACapabilities { Streaming = true }
            }, s_jsonOptions);

            var handler = new MockHttpHandler(req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.EndsWith("/.well-known/agent-card.json", req.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(cardJson, Encoding.UTF8, "application/json")
                };
            });

            A2AAgentCard card = await A2AClientTestExtensions.GetAgentCardAsync(s_testHost, handler: handler);

            Assert.Equal("TestAgent", card.Name);
            Assert.Equal("1.0.0", card.Version);
            Assert.True(card.Capabilities.Streaming);
        }

        [Fact]
        public async Task GetAgentCardAsync_ServerError_Throws()
        {
            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError));

            await Assert.ThrowsAsync<HttpRequestException>(
                () => A2AClientTestExtensions.GetAgentCardAsync(s_testHost, handler: handler));
        }

        // ───────────────────── Send Message (Synchronous) ─────────────────────

        [Fact]
        public async Task SendMessageAsync_ReturnsResponse()
        {
            var resultMessage = new A2AMessage
            {
                MessageId = "resp-1",
                Role = "ROLE_AGENT",
                Parts = new List<A2APart> { A2APart.FromText("Hello from agent!") }
            };

            string responseJson = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "test-req",
                Result = JsonSerializer.SerializeToElement(resultMessage, s_jsonOptions)
            }, s_jsonOptions);

            var handler = new MockHttpHandler(req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal(s_testEndpoint, req.RequestUri);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

            using (var client = new A2AClient(s_testEndpoint, handler))
            {
                A2AMessage response = await client.SendMessageAsync(
                    A2AMessage.CreateUserTextMessage("Hi!"));

                Assert.NotNull(response);
                Assert.Equal("ROLE_AGENT", response.Role);
                Assert.Equal("Hello from agent!", response.Parts[0].Text);
            }
        }

        [Fact]
        public async Task SendMessageAsync_ErrorResponse_Throws()
        {
            string errorJson = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "test-req",
                Error = new JsonRpcError { Code = -32600, Message = "Invalid Request" }
            }, s_jsonOptions);

            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
                });

            using (var client = new A2AClient(s_testEndpoint, handler))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => client.SendMessageAsync(A2AMessage.CreateUserTextMessage("Hi!")));

                Assert.Contains("-32600", ex.Message);
                Assert.Contains("Invalid Request", ex.Message);
            }
        }

        [Fact]
        public async Task SendMessageAsync_ServerError_Throws()
        {
            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            using (var client = new A2AClient(s_testEndpoint, handler))
            {
                await Assert.ThrowsAsync<HttpRequestException>(
                    () => client.SendMessageAsync(A2AMessage.CreateUserTextMessage("Hi!")));
            }
        }

        [Fact]
        public async Task SendMessageAsync_SendsCorrectPayload()
        {
            string capturedBody = null;

            var handler = new MockHttpHandler(req =>
            {
                capturedBody = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var resultMsg = A2AMessage.CreateUserTextMessage("ok");
                string responseJson = JsonSerializer.Serialize(new JsonRpcResponse
                {
                    Id = "test",
                    Result = JsonSerializer.SerializeToElement(resultMsg, s_jsonOptions)
                }, s_jsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

            using (var client = new A2AClient(s_testEndpoint, handler))
            {
                await client.SendMessageAsync(A2AMessage.CreateUserTextMessage("test payload"));
            }

            Assert.NotNull(capturedBody);
            using (JsonDocument doc = JsonDocument.Parse(capturedBody))
            {
                // Must be JSON-RPC 2.0 format with PascalCase method name
                Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
                Assert.Equal("SendMessage", doc.RootElement.GetProperty("method").GetString());
                Assert.True(doc.RootElement.TryGetProperty("id", out _));

                string text = doc.RootElement
                    .GetProperty("params")
                    .GetProperty("message")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();
                Assert.Equal("test payload", text);
            }
        }

        [Fact]
        public async Task SendMessageAsync_IncludesCustomHeaders()
        {
            string capturedAuth = null;

            var handler = new MockHttpHandler(req =>
            {
                capturedAuth = req.Headers.Contains("Authorization")
                    ? string.Join(",", req.Headers.GetValues("Authorization"))
                    : null;
                var resultMsg = A2AMessage.CreateUserTextMessage("ok");
                string responseJson = JsonSerializer.Serialize(new JsonRpcResponse
                {
                    Id = "test",
                    Result = JsonSerializer.SerializeToElement(resultMsg, s_jsonOptions)
                }, s_jsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer test-token-123"
            };

            using (var client = new A2AClient(s_testEndpoint, handler, headers))
            {
                await client.SendMessageAsync(A2AMessage.CreateUserTextMessage("hi"));
            }

            Assert.Equal("Bearer test-token-123", capturedAuth);
        }

        // ───────────────────── Streaming ─────────────────────

        [Fact]
        public async Task SendMessageStreamingAsync_ReceivesChunks()
        {
            string chunk1Json = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "stream-req",
                Result = JsonSerializer.SerializeToElement(new A2AMessage
                {
                    MessageId = "r1", Role = "ROLE_AGENT",
                    Parts = new List<A2APart> { A2APart.FromText("Hello") }
                }, s_jsonOptions)
            }, s_jsonOptions);

            string chunk2Json = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "stream-req",
                Result = JsonSerializer.SerializeToElement(new A2AMessage
                {
                    MessageId = "r1", Role = "ROLE_AGENT",
                    Parts = new List<A2APart> { A2APart.FromText(" World") }
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
                "data: {\"taskId\":\"t1\",\"status\":\"completed\"}\n" +
                "\n";

            var handler = new MockHttpHandler(req =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
                };
                return response;
            });

            using (var client = new A2AClient(s_testEndpoint, handler))
            {
                var chunks = new List<string>();

                List<A2AMessage> messages = await client.SendMessageStreamingAsync(
                    A2AMessage.CreateUserTextMessage("stream test"),
                    onMessageChunk: chunk =>
                    {
                        if (chunk.Parts != null)
                        {
                            foreach (var part in chunk.Parts)
                            {
                                if (part.Kind == PartKind.Text) chunks.Add(part.Text);
                            }
                        }
                    });

                Assert.Equal(2, messages.Count);
                Assert.Equal("Hello", messages[0].Parts[0].Text);
                Assert.Equal(" World", messages[1].Parts[0].Text);
                Assert.Equal(new[] { "Hello", " World" }, chunks.ToArray());
            }
        }

        [Fact]
        public async Task SendMessageStreamingAsync_EmptyStream_ReturnsEmptyList()
        {
            string ssePayload = "event: complete\ndata: {\"status\":\"completed\"}\n\n";

            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
                });

            using (var client = new A2AClient(s_testEndpoint, handler))
            {
                List<A2AMessage> messages = await client.SendMessageStreamingAsync(
                    A2AMessage.CreateUserTextMessage("test"));

                Assert.Empty(messages);
            }
        }

        [Fact]
        public async Task SendMessageStreamingAsync_MultiLineData_JoinsWithNewline()
        {
            var resultMsg = new A2AMessage
            {
                MessageId = "r1", Role = "ROLE_AGENT",
                Parts = new List<A2APart> { A2APart.FromText("hello world") }
            };
            string rpcJson = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "ml-req",
                Result = JsonSerializer.SerializeToElement(resultMsg, s_jsonOptions)
            }, s_jsonOptions);

            int splitPoint = rpcJson.IndexOf("\"id\"");
            string part1 = rpcJson.Substring(0, splitPoint);
            string part2 = rpcJson.Substring(splitPoint);

            string ssePayload =
                "event: message\n" +
                "data: " + part1 + "\n" +
                "data: " + part2 + "\n" +
                "\n" +
                "event: complete\n" +
                "data: {\"status\":\"completed\"}\n" +
                "\n";

            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
                });

            using (var client = new A2AClient(s_testEndpoint, handler))
            {
                List<A2AMessage> messages = await client.SendMessageStreamingAsync(
                    A2AMessage.CreateUserTextMessage("multi-line test"));

                Assert.Single(messages);
                Assert.Equal("hello world", messages[0].Parts[0].Text);
            }
        }

        // ───────────────────── Disposal ─────────────────────

        [Fact]
        public async Task Dispose_ThenSendMessage_ThrowsObjectDisposed()
        {
            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK));

            var client = new A2AClient(s_testEndpoint, handler);
            client.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => client.SendMessageAsync(A2AMessage.CreateUserTextMessage("hi")));
        }

        // ───────────────────── Reconnection ─────────────────────

        [Fact]
        public async Task SendMessageStreamingAsync_ConnectionDropped_ReconnectsAndCollectsAllChunks()
        {
            int callCount = 0;

            string chunk1Json = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "reconnect",
                Result = JsonSerializer.SerializeToElement(new A2AMessage
                {
                    MessageId = "r1", Role = "ROLE_AGENT",
                    Parts = new List<A2APart> { A2APart.FromText("chunk1") }
                }, s_jsonOptions)
            }, s_jsonOptions);

            string chunk2Json = JsonSerializer.Serialize(new JsonRpcResponse
            {
                Id = "reconnect",
                Result = JsonSerializer.SerializeToElement(new A2AMessage
                {
                    MessageId = "r2", Role = "ROLE_AGENT",
                    Parts = new List<A2APart> { A2APart.FromText("chunk2") }
                }, s_jsonOptions)
            }, s_jsonOptions);

            string completeSse =
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
            {
                callCount++;
                if (callCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(completeSse, Encoding.UTF8, "text/event-stream")
                };
            });

            using (var client = new A2AClient(s_testEndpoint, handler))
            {
                List<A2AMessage> messages = await client.SendMessageStreamingAsync(
                    A2AMessage.CreateUserTextMessage("reconnect test"));

                Assert.Equal(2, messages.Count);
                Assert.Equal("chunk1", messages[0].Parts[0].Text);
                Assert.Equal("chunk2", messages[1].Parts[0].Text);
                Assert.True(callCount >= 2, "Client should have reconnected at least once");
            }
        }

        // ───────────────────── Certificate Validation ─────────────────────

        [Fact]
        public void CreateHandlerWithCertificateValidation_NullValidator_ReturnsDefaultHandler()
        {
            var handler = A2AClient.CreateHandlerWithCertificateValidation(null);
            Assert.NotNull(handler);
            handler.Dispose();
        }

        [Fact]
        public void CreateHandlerWithCertificateValidation_WithValidator_SetsCallback()
        {
            var handler = A2AClient.CreateHandlerWithCertificateValidation(
                (request, cert, chain, errors) => true);

            Assert.NotNull(handler);
            handler.Dispose();
        }

        // ───────────────────── Helpers ─────────────────────

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
    }

    internal static class A2AClientTestExtensions
    {
        public static Task<A2AAgentCard> GetAgentCardAsync(
            Uri host,
            Dictionary<string, string> headers = null,
            float timeoutSeconds = 30f,
            HttpMessageHandler handler = null)
        {
            return A2AClient.GetAgentCardAsync(host, headers, timeoutSeconds, handler);
        }
    }
}
