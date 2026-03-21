using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using UnityEngine;

namespace LiveLink.Agent
{
    /// <summary>
    /// Creates MCP client connections used by the embedded agent runtime.
    /// </summary>
    internal static class AgentMcpClientFactory
    {
        internal static Task<McpClient> CreateLocalClientAsync(string endpoint, AgentMcpHttpTransportMode transportMode, float timeoutSeconds, CancellationToken cancellationToken)
        {
            return CreateHttpClientAsync("LiveLink MCP", endpoint, transportMode, null, timeoutSeconds, cancellationToken);
        }

        internal static Task<McpClient> CreateExternalClientAsync(AgentExternalMcpServerConfig config, CancellationToken cancellationToken)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (config.TransportType == AgentMcpTransportType.Http)
            {
                return CreateHttpClientAsync(
                    config.DisplayName,
                    config.Endpoint,
                    config.HttpTransportMode,
                    config.Headers,
                    config.ConnectionTimeoutSeconds,
                    cancellationToken);
            }

            return CreateStdioClientAsync(config, cancellationToken);
        }

        public static async Task<AgentMcpConnectionTestResult> TestConnectionAsync(AgentExternalMcpServerConfig config, CancellationToken cancellationToken)
        {
            var result = new AgentMcpConnectionTestResult
            {
                DisplayName = config != null ? config.DisplayName : "External MCP Server"
            };

            if (config == null)
            {
                result.ErrorMessage = "Missing server configuration.";
                return result;
            }

            McpClient client = null;
            try
            {
                client = await CreateExternalClientAsync(config, cancellationToken).ConfigureAwait(false);
                result.Success = true;
                result.ServerName = client.ServerInfo.Name;
                result.ServerVersion = client.ServerInfo.Version;
                result.ToolNames = (await client.ListToolsAsync().ConfigureAwait(false))
                    .Cast<AITool>()
                    .Select(tool => tool.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                if (client != null)
                    await client.DisposeAsync().ConfigureAwait(false);
            }

            return result;
        }

        private static Task<McpClient> CreateHttpClientAsync(string displayName, string endpoint, AgentMcpHttpTransportMode transportMode, IReadOnlyList<AgentNamedValue> headers, float timeoutSeconds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new InvalidOperationException("HTTP MCP endpoint is required.");

            Debug.Log(string.Format(
                "[LiveLink-Agent] Creating HTTP MCP client '{0}' -> {1} ({2}, timeout={3:0.##}s)",
                string.IsNullOrWhiteSpace(displayName) ? "MCP HTTP" : displayName,
                endpoint,
                transportMode,
                Math.Max(1f, timeoutSeconds)));

            Dictionary<string, string> additionalHeaders = ToDictionary(headers);
            if (string.Equals(displayName, "LiveLink MCP", StringComparison.OrdinalIgnoreCase))
            {
                additionalHeaders["X-LiveLink-Consumer"] = "embedded-agent";
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = string.IsNullOrWhiteSpace(displayName) ? "MCP HTTP" : displayName,
                Endpoint = new Uri(endpoint),
                TransportMode = ConvertHttpTransportMode(transportMode),
                ConnectionTimeout = TimeSpan.FromSeconds(Math.Max(1f, timeoutSeconds)),
                AdditionalHeaders = additionalHeaders
            });

            return McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        }

        private static Task<McpClient> CreateStdioClientAsync(AgentExternalMcpServerConfig config, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.Command))
                throw new InvalidOperationException("Stdio MCP command is required.");

            if (!SupportsStdioTransport())
                throw new PlatformNotSupportedException("Stdio MCP servers are supported only in the Unity Editor or on standalone desktop players.");

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = string.IsNullOrWhiteSpace(config.DisplayName) ? "MCP Stdio" : config.DisplayName,
                Command = config.Command,
                Arguments = new List<string>(config.Arguments),
                WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? null : config.WorkingDirectory,
                EnvironmentVariables = ToNullableDictionary(config.EnvironmentVariables)
            });

            return McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        }

        private static bool SupportsStdioTransport()
        {
#if UNITY_EDITOR
            return true;
#else
            RuntimePlatform platform = Application.platform;
            return platform == RuntimePlatform.WindowsPlayer ||
                   platform == RuntimePlatform.OSXPlayer ||
                   platform == RuntimePlatform.LinuxPlayer;
#endif
        }

        private static HttpTransportMode ConvertHttpTransportMode(AgentMcpHttpTransportMode transportMode)
        {
            switch (transportMode)
            {
                case AgentMcpHttpTransportMode.StreamableHttp:
                    return HttpTransportMode.StreamableHttp;
                case AgentMcpHttpTransportMode.Sse:
                    return HttpTransportMode.Sse;
                default:
                    return HttpTransportMode.AutoDetect;
            }
        }

        private static Dictionary<string, string> ToDictionary(IReadOnlyList<AgentNamedValue> values)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
                return result;

            for (int i = 0; i < values.Count; i++)
            {
                AgentNamedValue value = values[i];
                if (value == null || string.IsNullOrWhiteSpace(value.Name))
                    continue;

                result[value.Name] = value.Value ?? string.Empty;
            }

            return result;
        }

        private static Dictionary<string, string> ToNullableDictionary(IReadOnlyList<AgentNamedValue> values)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
                return result;

            for (int i = 0; i < values.Count; i++)
            {
                AgentNamedValue value = values[i];
                if (value == null || string.IsNullOrWhiteSpace(value.Name))
                    continue;

                result[value.Name] = value.Value;
            }

            return result;
        }
    }
}
