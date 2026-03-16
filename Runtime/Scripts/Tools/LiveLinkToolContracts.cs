using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LiveLink.Tools
{
    public enum LiveLinkToolVisibility
    {
        Both = 0,
        AgentOnly = 1,
        ExternalOnly = 2
    }

    public enum LiveLinkToolConsumer
    {
        External = 0,
        EmbeddedAgent = 1
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class LiveLinkToolAttribute : Attribute
    {
        public LiveLinkToolAttribute(string name)
        {
            Name = name;
            Description = string.Empty;
            Category = string.Empty;
            Tags = Array.Empty<string>();
            Visibility = LiveLinkToolVisibility.Both;
            RequiresMainThread = false;
            IsMutation = false;
        }

        public string Name { get; private set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string[] Tags { get; set; }
        public LiveLinkToolVisibility Visibility { get; set; }
        public bool RequiresMainThread { get; set; }
        public bool IsMutation { get; set; }
    }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class LiveLinkToolParameterAttribute : Attribute
    {
        public LiveLinkToolParameterAttribute(string name)
        {
            Name = name;
            Description = string.Empty;
            Required = false;
        }

        public string Name { get; private set; }
        public string Description { get; set; }
        public bool Required { get; set; }
    }

    public sealed class LiveLinkToolParameterDescriptor
    {
        public string Name;
        public string Description;
        public Type ParameterType;
        public bool Required;
        public bool HasDefaultValue;
        public object DefaultValue;
        public int Position;
    }

    public sealed class LiveLinkToolDescriptor
    {
        public string Name;
        public string Description;
        public string Category;
        public List<string> Tags = new List<string>();
        public LiveLinkToolVisibility Visibility;
        public bool RequiresMainThread;
        public bool IsMutation;
        public Type DeclaringType;
        public string MethodName;
        public System.Reflection.MethodInfo Method;
        public object TargetInstance;
        public List<LiveLinkToolParameterDescriptor> Parameters = new List<LiveLinkToolParameterDescriptor>();
        public JObject InputSchema;
    }

    public sealed class LiveLinkToolExposurePolicy
    {
        public bool EnableDynamicTools = true;
        public bool ExposeToExternal = true;
        public bool ExposeToEmbeddedAgent = true;
        public bool AllowExternalMutationTools;
        public bool AllowEmbeddedAgentMutationTools = true;
        public HashSet<string> ExternalAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExternalDenyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AgentAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AgentDenyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllowedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsToolVisible(LiveLinkToolDescriptor descriptor, LiveLinkToolConsumer consumer)
        {
            if (!EnableDynamicTools || descriptor == null || string.IsNullOrEmpty(descriptor.Name))
            {
                return false;
            }

            if (!MatchesVisibility(descriptor.Visibility, consumer))
            {
                return false;
            }

            if (!MatchesConsumerToggles(descriptor, consumer))
            {
                return false;
            }

            if (!MatchesMutationRule(descriptor, consumer))
            {
                return false;
            }

            if (!MatchesAllowDenyLists(descriptor.Name, consumer))
            {
                return false;
            }

            if (!MatchesCategoryRule(descriptor.Category))
            {
                return false;
            }

            if (!MatchesTagRule(descriptor.Tags))
            {
                return false;
            }

            return true;
        }

        private static bool MatchesVisibility(LiveLinkToolVisibility visibility, LiveLinkToolConsumer consumer)
        {
            if (visibility == LiveLinkToolVisibility.Both)
            {
                return true;
            }

            if (visibility == LiveLinkToolVisibility.AgentOnly)
            {
                return consumer == LiveLinkToolConsumer.EmbeddedAgent;
            }

            return consumer == LiveLinkToolConsumer.External;
        }

        private bool MatchesConsumerToggles(LiveLinkToolDescriptor descriptor, LiveLinkToolConsumer consumer)
        {
            if (consumer == LiveLinkToolConsumer.EmbeddedAgent)
            {
                return ExposeToEmbeddedAgent;
            }

            return ExposeToExternal;
        }

        private bool MatchesMutationRule(LiveLinkToolDescriptor descriptor, LiveLinkToolConsumer consumer)
        {
            if (!descriptor.IsMutation)
            {
                return true;
            }

            if (consumer == LiveLinkToolConsumer.EmbeddedAgent)
            {
                return AllowEmbeddedAgentMutationTools;
            }

            return AllowExternalMutationTools;
        }

        private bool MatchesAllowDenyLists(string toolName, LiveLinkToolConsumer consumer)
        {
            HashSet<string> allowList = consumer == LiveLinkToolConsumer.EmbeddedAgent ? AgentAllowList : ExternalAllowList;
            HashSet<string> denyList = consumer == LiveLinkToolConsumer.EmbeddedAgent ? AgentDenyList : ExternalDenyList;

            if (denyList != null && denyList.Contains(toolName))
            {
                return false;
            }

            if (allowList != null && allowList.Count > 0)
            {
                return allowList.Contains(toolName);
            }

            return true;
        }

        private bool MatchesCategoryRule(string category)
        {
            if (AllowedCategories == null || AllowedCategories.Count == 0)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(category))
            {
                return false;
            }

            return AllowedCategories.Contains(category);
        }

        private bool MatchesTagRule(List<string> tags)
        {
            if (AllowedTags == null || AllowedTags.Count == 0)
            {
                return true;
            }

            if (tags == null || tags.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < tags.Count; i++)
            {
                if (AllowedTags.Contains(tags[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
