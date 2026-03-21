using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveLink.Tools
{
    public sealed class LiveLinkToolRegistry
    {
        private readonly Dictionary<string, LiveLinkToolDescriptor> _toolsByName = new Dictionary<string, LiveLinkToolDescriptor>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _duplicateTools = new List<string>();

        public IReadOnlyDictionary<string, LiveLinkToolDescriptor> ToolsByName
        {
            get { return _toolsByName; }
        }

        public IReadOnlyList<string> DuplicateTools
        {
            get { return _duplicateTools; }
        }

        public void Rebuild(IReadOnlyList<string> assemblyAllowList, IReadOnlyList<LiveLinkToolManifestAsset> manifestAssets = null)
        {
            _toolsByName.Clear();
            _duplicateTools.Clear();

            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < allAssemblies.Length; assemblyIndex++)
            {
                Assembly assembly = allAssemblies[assemblyIndex];
                if (!IsAssemblyAllowed(assembly, assemblyAllowList))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    Type type = types[typeIndex];
                    if (type == null)
                    {
                        continue;
                    }

                    BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                    MethodInfo[] methods = type.GetMethods(flags);
                    for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                    {
                        MethodInfo method = methods[methodIndex];
                        LiveLinkToolAttribute attribute = method.GetCustomAttribute<LiveLinkToolAttribute>(false);
                        if (attribute == null)
                        {
                            continue;
                        }

                        LiveLinkToolDescriptor descriptor = CreateDescriptor(type, method, attribute);
                        if (descriptor == null)
                        {
                            continue;
                        }

                        AddDescriptor(descriptor, type.FullName + "." + method.Name);
                    }
                }
            }

            List<LiveLinkToolDescriptor> manifestDescriptors = LiveLinkToolManifestResolver.ResolveFromAssets(manifestAssets);
            for (int i = 0; i < manifestDescriptors.Count; i++)
            {
                LiveLinkToolDescriptor descriptor = manifestDescriptors[i];
                string source = descriptor.DeclaringType != null
                    ? descriptor.DeclaringType.FullName + "." + descriptor.MethodName + " [manifest]"
                    : "manifest";
                AddDescriptor(descriptor, source);
            }
        }

        public bool TryGetTool(string toolName, out LiveLinkToolDescriptor descriptor)
        {
            return _toolsByName.TryGetValue(toolName, out descriptor);
        }

        private void AddDescriptor(LiveLinkToolDescriptor descriptor, string source)
        {
            if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Name))
            {
                return;
            }

            if (_toolsByName.ContainsKey(descriptor.Name))
            {
                string duplicate = descriptor.Name + " (" + source + ")";
                _duplicateTools.Add(duplicate);
                Debug.LogWarning("[LiveLink-MCP] Duplicate dynamic tool name ignored: " + duplicate);
                return;
            }

            _toolsByName.Add(descriptor.Name, descriptor);
        }

        private static bool IsAssemblyAllowed(Assembly assembly, IReadOnlyList<string> assemblyAllowList)
        {
            if (assembly == null)
            {
                return false;
            }

            string assemblyName = assembly.GetName().Name;
            if (string.IsNullOrEmpty(assemblyName))
            {
                return false;
            }

            if (assemblyName.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (assemblyAllowList == null || assemblyAllowList.Count == 0)
            {
                return true;
            }

            for (int i = 0; i < assemblyAllowList.Count; i++)
            {
                string allowed = assemblyAllowList[i];
                if (string.IsNullOrWhiteSpace(allowed))
                {
                    continue;
                }

                if (string.Equals(assemblyName, allowed.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static LiveLinkToolDescriptor CreateDescriptor(Type declaringType, MethodInfo method, LiveLinkToolAttribute attribute)
        {
            string toolName = attribute.Name != null ? attribute.Name.Trim() : string.Empty;
            if (string.IsNullOrEmpty(toolName))
            {
                Debug.LogWarning("[LiveLink-MCP] Ignoring tool with empty name on method: " + declaringType.FullName + "." + method.Name);
                return null;
            }

            bool isStatic = method.IsStatic;
            object target = null;
            if (!isStatic)
            {
                Debug.LogWarning("[LiveLink-MCP] Ignoring non-static tool method. Use static methods for dynamic tools: " + declaringType.FullName + "." + method.Name);
                return null;
            }

            var descriptor = new LiveLinkToolDescriptor
            {
                Name = toolName,
                Description = attribute.Description ?? string.Empty,
                Category = attribute.Category ?? string.Empty,
                Visibility = attribute.Visibility,
                RequiresMainThread = attribute.RequiresMainThread,
                IsMutation = attribute.IsMutation,
                DeclaringType = declaringType,
                MethodName = method.Name,
                Method = method,
                TargetInstance = target
            };

            if (attribute.Tags != null)
            {
                for (int i = 0; i < attribute.Tags.Length; i++)
                {
                    string tag = attribute.Tags[i];
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
                LiveLinkToolParameterAttribute paramAttribute = parameter.GetCustomAttribute<LiveLinkToolParameterAttribute>(false);
                string paramName = paramAttribute != null && !string.IsNullOrWhiteSpace(paramAttribute.Name)
                    ? paramAttribute.Name.Trim()
                    : parameter.Name;

                var parameterDescriptor = new LiveLinkToolParameterDescriptor
                {
                    Name = paramName,
                    Description = paramAttribute != null ? paramAttribute.Description : string.Empty,
                    ParameterType = parameter.ParameterType,
                    Required = paramAttribute != null && paramAttribute.Required,
                    HasDefaultValue = parameter.HasDefaultValue,
                    DefaultValue = parameter.HasDefaultValue ? parameter.DefaultValue : null,
                    Position = index
                };

                if (!parameterDescriptor.Required)
                {
                    parameterDescriptor.Required = !parameter.HasDefaultValue && !IsNullable(parameter.ParameterType);
                }

                descriptor.Parameters.Add(parameterDescriptor);
            }

            descriptor.InputSchema = BuildInputSchema(descriptor.Parameters);
            return descriptor;
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
