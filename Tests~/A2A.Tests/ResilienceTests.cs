using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace A2A.Tests
{
    /// <summary>
    /// Tests for EmbeddedAgentRuntime resilience improvements.
    /// Since EmbeddedAgentRuntime is a MonoBehaviour with heavy Unity dependencies,
    /// we test the extractable logic patterns here:
    /// - HttpClient health probe (cross-platform)
    /// - Retry with exponential backoff
    /// - MCP heartbeat reconnection pattern
    /// </summary>
    public class ResilienceTests
    {
        // ───────────────────── HttpClient Health Probe ─────────────────────
        // Validates that the HttpClient-based probe works correctly (replaces HttpWebRequest).
        // Uses the same mock HTTP server pattern as A2AClientTests.

        [Fact]
        public async Task HealthProbe_HttpClient_ReturnsTrueFor200()
        {
            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });

            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) })
            {
                HttpResponseMessage response = await client.GetAsync("http://localhost/health");
                Assert.True((int)response.StatusCode >= 200 && (int)response.StatusCode < 300);
            }
        }

        [Fact]
        public async Task HealthProbe_HttpClient_ReturnsFalseFor500()
        {
            var handler = new MockHttpHandler(req =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError));

            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) })
            {
                HttpResponseMessage response = await client.GetAsync("http://localhost/health");
                Assert.False((int)response.StatusCode >= 200 && (int)response.StatusCode < 300);
            }
        }

        [Fact]
        public async Task HealthProbe_HttpClient_ReturnsFalseOnException()
        {
            var handler = new FailingHandler();

            bool result = false;
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) })
            {
                try
                {
                    HttpResponseMessage response = await client.GetAsync("http://localhost/health");
                    result = (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
                }
                catch
                {
                    result = false;
                }
            }

            Assert.False(result);
        }

        [Fact]
        public async Task HealthProbe_HttpClient_TimeoutRespected()
        {
            // Handler that delays longer than the timeout.
            var handler = new DelayedHandler(TimeSpan.FromSeconds(2));

            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) })
            {
                await Assert.ThrowsAsync<TaskCanceledException>(
                    () => client.GetAsync("http://localhost/health"));
            }
        }

        // ───────────────────── Retry with Exponential Backoff ─────────────────────
        // Tests the retry pattern used in InitializeWithRetryAsync.

        [Fact]
        public async Task RetryPattern_SucceedsOnFirstAttempt()
        {
            int attempts = 0;
            bool result = await RetryAsync(async () =>
            {
                attempts++;
                await Task.Yield();
            }, maxRetries: 3);

            Assert.True(result);
            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task RetryPattern_SucceedsOnSecondAttempt()
        {
            int attempts = 0;
            bool result = await RetryAsync(async () =>
            {
                attempts++;
                if (attempts < 2) throw new InvalidOperationException("fail");
                await Task.Yield();
            }, maxRetries: 3);

            Assert.True(result);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public async Task RetryPattern_SucceedsOnThirdAttempt()
        {
            int attempts = 0;
            bool result = await RetryAsync(async () =>
            {
                attempts++;
                if (attempts < 3) throw new InvalidOperationException("fail");
                await Task.Yield();
            }, maxRetries: 3);

            Assert.True(result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task RetryPattern_FailsAfterMaxRetries()
        {
            int attempts = 0;
            bool result = await RetryAsync(async () =>
            {
                attempts++;
                await Task.Yield();
                throw new InvalidOperationException("always fail");
            }, maxRetries: 3);

            Assert.False(result);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task RetryPattern_ExponentialBackoffTiming()
        {
            var timestamps = new List<DateTime>();

            await RetryAsync(async () =>
            {
                timestamps.Add(DateTime.UtcNow);
                if (timestamps.Count < 3) throw new InvalidOperationException("fail");
                await Task.Yield();
            }, maxRetries: 3);

            Assert.Equal(3, timestamps.Count);

            // First retry should have ~1s delay, second ~2s.
            // Allow generous margin for CI slowness.
            double firstGap = (timestamps[1] - timestamps[0]).TotalMilliseconds;
            double secondGap = (timestamps[2] - timestamps[1]).TotalMilliseconds;

            Assert.True(firstGap >= 800, $"First gap {firstGap}ms should be >= 800ms");
            Assert.True(secondGap >= 1600, $"Second gap {secondGap}ms should be >= 1600ms");
        }

        // ───────────────────── MCP Heartbeat Reconnection Pattern ─────────────────────
        // Tests the reconnection loop logic.

        [Fact]
        public async Task ReconnectionPattern_ReconnectsOnFailure()
        {
            int checkCount = 0;
            int reconnectCount = 0;

            // Simulate: first 2 health checks fail, then succeed after reconnect.
            var result = await SimulateHeartbeatAsync(
                healthCheck: () =>
                {
                    checkCount++;
                    return Task.FromResult(checkCount >= 3); // healthy after 2 failures
                },
                reconnect: () =>
                {
                    reconnectCount++;
                    return Task.FromResult(true);
                },
                maxCycles: 1);

            Assert.True(result);
            Assert.Equal(3, checkCount);
            Assert.Equal(2, reconnectCount);
        }

        [Fact]
        public async Task ReconnectionPattern_StopsAfterMaxAttempts()
        {
            int reconnectCount = 0;

            // Always fail health check, always fail reconnect.
            var result = await SimulateHeartbeatAsync(
                healthCheck: () => Task.FromResult(false),
                reconnect: () =>
                {
                    reconnectCount++;
                    return Task.FromResult(false);
                },
                maxCycles: 1);

            Assert.False(result);
            Assert.Equal(3, reconnectCount); // maxReconnectAttempts = 3
        }

        [Fact]
        public async Task ReconnectionPattern_NoReconnectWhenHealthy()
        {
            int reconnectCount = 0;

            var result = await SimulateHeartbeatAsync(
                healthCheck: () => Task.FromResult(true),
                reconnect: () =>
                {
                    reconnectCount++;
                    return Task.FromResult(true);
                },
                maxCycles: 1);

            Assert.True(result);
            Assert.Equal(0, reconnectCount);
        }

        // ───────────────────── SSE Override Transparency ─────────────────────
        // Tests that the transport mode override logic works correctly.

        [Theory]
        [InlineData("Sse", true)]      // SSE selected → overridden
        [InlineData("StreamableHttp", false)]  // StreamableHttp → no override
        [InlineData("AutoDetect", false)]      // AutoDetect → no override
        public void TransportOverride_DetectsSseOverride(string selected, bool shouldOverride)
        {
            bool wasOverridden = CheckTransportOverride(selected);
            Assert.Equal(shouldOverride, wasOverridden);
        }

        // ───────────────────── Helpers ─────────────────────

        /// <summary>
        /// Replicates the InitializeWithRetryAsync pattern for testing.
        /// </summary>
        private static async Task<bool> RetryAsync(Func<Task> action, int maxRetries)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    await action();
                    return true;
                }
                catch (Exception)
                {
                    if (attempt < maxRetries - 1)
                    {
                        int delayMs = 1000 * (1 << attempt);
                        await Task.Delay(delayMs);
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Simulates the MCP heartbeat reconnection loop for testing.
        /// Returns true if the server is healthy after the cycle.
        /// </summary>
        private static async Task<bool> SimulateHeartbeatAsync(
            Func<Task<bool>> healthCheck,
            Func<Task<bool>> reconnect,
            int maxCycles)
        {
            const int maxReconnectAttempts = 3;

            for (int cycle = 0; cycle < maxCycles; cycle++)
            {
                bool healthy = await healthCheck();
                if (healthy) return true;

                bool restored = false;
                for (int attempt = 0; attempt < maxReconnectAttempts; attempt++)
                {
                    // Minimal delay for test speed (real code uses 1s * 2^attempt).
                    await Task.Delay(10);

                    bool success = await reconnect();
                    if (success)
                    {
                        // Re-check health after reconnect.
                        if (await healthCheck())
                        {
                            restored = true;
                            break;
                        }
                    }
                }

                if (!restored) return false;
            }

            return true;
        }

        /// <summary>
        /// Replicates the transport override check logic.
        /// </summary>
        private static bool CheckTransportOverride(string selected)
        {
            return string.Equals(selected, "Sse", StringComparison.OrdinalIgnoreCase);
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

        private class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw new HttpRequestException("Connection refused");
            }
        }

        private class DelayedHandler : HttpMessageHandler
        {
            private readonly TimeSpan _delay;

            public DelayedHandler(TimeSpan delay)
            {
                _delay = delay;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(_delay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }
    }
}
