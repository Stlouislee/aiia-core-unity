using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveLink.Tools
{
    internal static class LiveLinkToolManifestResolver
    {
        internal static List<LiveLinkToolDescriptor> ResolveFromAssets(IReadOnlyList<LiveLinkToolManifestAsset> assets)
        {
            var descriptors = new List<LiveLinkToolDescriptor>();
            if (assets == null || assets.Count == 0)
            {
                return descriptors;
            }

            for (int assetIndex = 0; assetIndex < assets.Count; assetIndex++)
            {
                LiveLinkToolManifestAsset asset = assets[assetIndex];
                if (asset == null || asset.Tools == null)
                {
                    continue;
                }

                for (int entryIndex = 0; entryIndex < asset.Tools.Count; entryIndex++)
                {
                    LiveLinkToolManifestEntry entry = asset.Tools[entryIndex];
                    LiveLinkToolDescriptor descriptor = CreateDescriptor(entry);
                    if (descriptor != null)
                    {
                        descriptors.Add(descriptor);
                    }
                }
            }

            return descriptors;
        }

        private static LiveLinkToolDescriptor CreateDescriptor(LiveLinkToolManifestEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            string toolName = entry.ToolName != null ? entry.ToolName.Trim() : string.Empty;
            if (string.IsNullOrEmpty(toolName))
            {
                Debug.LogWarning("[LiveLink-MCP] Manifest entry ignored due to empty tool name.");
                return null;
            }

            MethodInfo method = ResolveMethod(entry);
            if (method == null)
            {
                return null;
            }

            var descriptor = new LiveLinkToolDescriptor
            {
                Name = toolName,
                Description = entry.Description ?? string.Empty,
                Category = entry.Category ?? string.Empty,
                Visibility = entry.Visibility,
                RequiresMainThread = entry.RequiresMainThread,
                IsMutation = entry.IsMutation,
                DeclaringType = method.DeclaringType,
                MethodName = method.Name,
                Method = method,
                TargetInstance = null
            };

            if (entry.Tags != null)
            {
                for (int i = 0; i < entry.Tags.Count; i++)
                {
                    string tag = entry.Tags[i];
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        descriptor.Tags.Add(tag.Trim());
                    }
                }
            }

            ParameterInfo[] parameters = method.GetParameters();
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                LiveLinkToolManifestParameterOverride parameterOverride = FindParameterOverride(entry.ParameterOverrides, parameter.Name);

                string exposedName = parameterOverride != null && !string.IsNullOrWhiteSpace(parameterOverride.ExposedParameterName)
                    ? parameterOverride.ExposedParameterName.Trim()
                    : parameter.Name;

                bool required;
                if (parameterOverride != null && parameterOverride.OverrideRequired)
                {
                    required = parameterOverride.Required;
                }
                else
                {
                    required = !parameter.HasDefaultValue && !IsNullable(parameter.ParameterType);
                }

                var parameterDescriptor = new LiveLinkToolParameterDescriptor
                {
                    Name = exposedName,
                    Description = parameterOverride != null ? parameterOverride.Description : string.Empty,
                    ParameterType = parameter.ParameterType,
                    Required = required,
                    HasDefaultValue = parameter.HasDefaultValue,
                    DefaultValue = parameter.HasDefaultValue ? parameter.DefaultValue : null,
                    Position = index
                };

                descriptor.Parameters.Add(parameterDescriptor);
            }

            descriptor.InputSchema = BuildInputSchema(descriptor.Parameters);
            return descriptor;
        }

        private static LiveLinkToolManifestParameterOverride FindParameterOverride(IReadOnlyList<LiveLinkToolManifestParameterOverride> overrides, string methodParameterName)
        {
            if (overrides == null || string.IsNullOrWhiteSpace(methodParameterName))
            {
                return null;
            }

            for (int i = 0; i < overrides.Count; i++)
            {
                LiveLinkToolManifestParameterOverride candidate = overrides[i];
                if (candidate == null)
                {
                    continue;
                }

                if (string.Equals(candidate.MethodParameterName, methodParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static MethodInfo ResolveMethod(LiveLinkToolManifestEntry entry)
        {
            Assembly assembly = ResolveAssembly(entry.AssemblyName);
            if (assembly == null)
            {
                Debug.LogWarning("[LiveLink-MCP] Manifest tool not loaded. Assembly not found: " + entry.AssemblyName + " (tool: " + entry.ToolName + ")");
                return null;
            }

            if (string.IsNullOrWhiteSpace(entry.TypeName))
            {
                Debug.LogWarning("[LiveLink-MCP] Manifest tool not loaded. Type name is empty for tool: " + entry.ToolName);
                return null;
            }

            Type type = assembly.GetType(entry.TypeName.Trim(), throwOnError: false);
            if (type == null)
            {
                Debug.LogWarning("[LiveLink-MCP] Manifest tool not loaded. Type not found: " + entry.TypeName + " (tool: " + entry.ToolName + ")");
                return null;
            }

            if (string.IsNullOrWhiteSpace(entry.MethodName))
            {
                Debug.LogWarning("[LiveLink-MCP] Manifest tool not loaded. Method name is empty for tool: " + entry.ToolName);
                return null;
            }

            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            List<MethodInfo> matchingMethods = new List<MethodInfo>();
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, entry.MethodName.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.ExpectedParameterCount >= 0 && method.GetParameters().Length != entry.ExpectedParameterCount)
                {
                    continue;
                }

                matchingMethods.Add(method);
            }

            if (matchingMethods.Count == 0)
            {
                Debug.LogWarning("[LiveLink-MCP] Manifest tool not loaded. Method not found: " + entry.TypeName + "." + entry.MethodName + " (tool: " + entry.ToolName + ")");
                return null;
            }

            if (matchingMethods.Count > 1)
            {
                Debug.LogWarning("[LiveLink-MCP] Manifest tool has ambiguous overloads; using first match: " + entry.TypeName + "." + entry.MethodName + " (tool: " + entry.ToolName + ")");
            }

            return matchingMethods[0];
        }

        private static Assembly ResolveAssembly(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return null;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null)
                {
                    continue;
                }

                string loadedName = assembly.GetName().Name;
                if (string.Equals(loadedName, assemblyName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }

            return null;
        }

        private static JObject BuildInputSchema(List<LiveLinkToolParameterDescriptor> parameters)
        {
            var properties = new JObject();
            var required = new JArray();

            for (int i = 0; i < parameters.Count; i++)
            {
                LiveLinkToolParameterDescriptor parameter = parameters[i];
                JObject schema = BuildTypeSchema(parameter.ParameterType);
                if (!string.IsNullOrWhiteSpace(parameter.Description))
                {
                    schema["description"] = parameter.Description;
                }

                properties[parameter.Name] = schema;

                if (parameter.Required)
                {
                    required.Add(parameter.Name);
                }
            }

            var inputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (required.Count > 0)
            {
                inputSchema["required"] = required;
            }

            return inputSchema;
        }

        private static JObject BuildTypeSchema(Type type)
        {
            Type normalized = Nullable.GetUnderlyingType(type) ?? type;

            if (normalized == typeof(string) || normalized.IsEnum)
            {
                return new JObject { ["type"] = "string" };
            }

            if (normalized == typeof(bool))
            {
                return new JObject { ["type"] = "boolean" };
            }

            if (normalized == typeof(byte) || normalized == typeof(sbyte) ||
                normalized == typeof(short) || normalized == typeof(ushort) ||
                normalized == typeof(int) || normalized == typeof(uint) ||
                normalized == typeof(long) || normalized == typeof(ulong))
            {
                return new JObject { ["type"] = "integer" };
            }

            if (normalized == typeof(float) || normalized == typeof(double) || normalized == typeof(decimal))
            {
                return new JObject { ["type"] = "number" };
            }

            if (normalized.IsArray)
            {
                Type elementType = normalized.GetElementType() ?? typeof(object);
                return new JObject
                {
                    ["type"] = "array",
                    ["items"] = BuildTypeSchema(elementType)
                };
            }

            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(normalized) && normalized != typeof(string))
            {
                return new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "object" }
                };
            }

            return new JObject { ["type"] = "object" };
        }

        private static bool IsNullable(Type type)
        {
            if (!type.IsValueType)
            {
                return true;
            }

            return Nullable.GetUnderlyingType(type) != null;
        }
    }
}
