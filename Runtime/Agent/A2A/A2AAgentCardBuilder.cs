using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveLink.Agent.A2A
{
    /// <summary>
    /// Builds an A2A agent card from host configuration and live tool information.
    /// The agent card is served at /.well-known/agent-card.json per the A2A v1.0 spec.
    /// </summary>
    public static class A2AAgentCardBuilder
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        /// <summary>
        /// Build an agent card JSON string from the host config.
        /// </summary>
        public static string BuildCardJson(A2AHostConfig config, string publicUrl)
        {
            A2AAgentCard card = BuildCard(config, publicUrl);
            return JsonSerializer.Serialize(card, s_jsonOptions);
        }

        /// <summary>
        /// Build an A2AAgentCard object from the host config.
        /// </summary>
        public static A2AAgentCard BuildCard(A2AHostConfig config, string publicUrl)
        {
            var skills = new List<A2ASkill>();
            if (config.Skills != null)
            {
                for (int i = 0; i < config.Skills.Count; i++)
                {
                    A2AHostSkill skill = config.Skills[i];
                    skills.Add(new A2ASkill
                    {
                        Id = skill.Id,
                        Name = skill.Name,
                        Description = skill.Description,
                        Tags = skill.Tags != null ? new List<string>(skill.Tags) : new List<string>()
                    });
                }
            }

            // Build the endpoint URL for the A2A HTTP interface.
            string endpointUrl = NormalizeUrl(publicUrl, config.Port);

            var card = new A2AAgentCard
            {
                Name = config.AgentName,
                Description = config.AgentDescription,
                Version = config.AgentVersion,
                SupportedInterfaces = new List<A2AInterface>
                {
                    new A2AInterface
                    {
                        Url = endpointUrl,
                        ProtocolBinding = "HTTP+JSON",
                        ProtocolVersion = "1.0"
                    }
                },
                Skills = skills,
                Capabilities = new A2ACapabilities
                {
                    Streaming = config.EnableStreaming,
                    PushNotifications = false
                },
                DefaultInputModes = new List<string> { "text/plain" },
                DefaultOutputModes = new List<string> { "text/plain" }
            };

            return card;
        }

        private static string NormalizeUrl(string publicUrl, int port)
        {
            if (string.IsNullOrWhiteSpace(publicUrl))
            {
                return $"http://localhost:{port}/a2a";
            }

            // Ensure the URL ends with /a2a
            string trimmed = publicUrl.TrimEnd('/');
            if (!trimmed.EndsWith("/a2a"))
            {
                trimmed += "/a2a";
            }

            return trimmed;
        }
    }
}
