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

        public IReadOnlyDictionary<string, LiveLinkToolDescriptor> ToolsByName { get { return _toolsByName; } }
        public IReadOnlyList<string> DuplicateTools { get { return _duplicateTools; } }

        /// <summary>
        /// Rebuilds the tool registry. Uses cache if available and valid, otherwise falls back to reflection scanning.
        /// </summary>
        /// <param name="assemblyAllowList">Optional assembly allow list for reflection scanning fallback.</param>
        /// <param name="manifestAssets">Manifest assets for zero-intrusion tool registration.</param>
        /// <param name="cacheAsset">Optional pre-computed cache asset. If null, will try to load from Resources.</param>
        public void Rebuild(
            IReadOnlyList<string> assemblyAllowList,
            IReadOnlyList<LiveLinkToolManifestAsset> manifestAssets = null,
            LiveLinkToolCacheAsset cacheAsset = null)
        {
            _toolsByName.Clear();
            _duplicateTools.Clear();

            // Try to use cache first
            bool usedCache = false;
            if (cacheAsset != null && !cacheAsset.IsStale)
            {
                usedCache = LoadFromCache(cacheAsset);
                if (usedCache)
                {
                    Debug.Log($"[LiveLink-MCP] Loaded {ToolsByName.Count} tools from cache (build-time pre-computed)");
                }
            }

            // Fallback to reflection scanning if no valid cache
            if (!usedCache)
            {
#if UNITY_EDITOR
                Debug.LogWarning("[LiveLink-MCP] No valid cache found, falling back to runtime reflection scanning. Consider rebuilding cache via LiveLink > Rebuild Tool Cache");
#endif
                ScanAssembliesForAttributes(assemblyAllowList);
            }

            // Always merge manifest-based tools (these are manually configured)
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

        /// <summary>
        /// Loads tools from a pre-computed cache asset.
        /// </summary>
        /// <returns>True if cache was loaded successfully, false otherwise.</returns>
        private bool LoadFromCache(LiveLinkToolCacheAsset cache)
        {
            if (cache == null || cache.Tools == null || cache.Tools.Count == 0)
                return false;

            for (int i = 0; i < cache.Tools.Count; i++)
            {
                LiveLinkToolCacheEntry entry = cache.Tools[i];
                LiveLinkToolDescriptor descriptor = CreateDescriptorFromCache(entry);
                if (descriptor != null)
                {
                    AddDescriptor(descriptor, "cache");
                }
            }

            return _toolsByName.Count > 0;
        }

        /// <summary>
        /// Creates a descriptor from a cache entry.
        /// </summary>
        private LiveLinkToolDescriptor CreateDescriptorFromCache(LiveLinkToolCacheEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.ToolName))
                return null;

            // Resolve method from assembly-qualified names
            MethodInfo method = ResolveMethodFromCache(entry);
            if (method == null)
            {
                Debug.LogWarning($"[LiveLink-MCP] Could not resolve method for cached tool: {entry.ToolName} ({entry.TypeName}.{entry.MethodName})");
                return null;
            }

            // Build parameter descriptors from cache
            var parameters = new List<LiveLinkToolParameterDescriptor>();
            if (entry.Parameters != null)
            {
                for (int i = 0; i < entry.Parameters.Count; i++)
                {
                    var paramCache = entry.Parameters[i];
                    Type paramType = Type.GetType(paramCache.ParameterTypeName);
                    if (paramType == null)
                    {
                        Debug.LogWarning($"[LiveLink-MCP] Could not resolve parameter type: {paramCache.ParameterTypeName}");
                        continue;
                    }

                    object defaultValue = null;
                    if (paramCache.HasDefaultValue && !string.IsNullOrEmpty(paramCache.DefaultValueJson))
                    {
                        try
                        {
                            defaultValue = Newtonsoft.Json.JsonConvert.DeserializeObject(paramCache.DefaultValueJson, paramType);
                        }
                        catch
                        {
                            // Ignore deserialization errors
                        }
                    }

                    parameters.Add(new LiveLinkToolParameterDescriptor
                    {
                        Name = paramCache.Name,
                        Description = paramCache.Description ?? string.Empty,
                        ParameterType = paramType,
                        Required = paramCache.Required,
                        HasDefaultValue = paramCache.HasDefaultValue,
                        DefaultValue = defaultValue,
                        Position = paramCache.Position
                    });
                }
            }

            // Parse input schema
            JObject inputSchema = null;
            if (!string.IsNullOrEmpty(entry.InputSchemaJson))
            {
                try
                {
                    inputSchema = JObject.Parse(entry.InputSchemaJson);
                }
                catch
                {
                    inputSchema = null;
                }
            }

            return new LiveLinkToolDescriptor
            {
                Name = entry.ToolName,
                Description = entry.Description ?? string.Empty,
                Category = entry.Category ?? string.Empty,
                Tags = entry.Tags?.ToList() ?? new List<string>(),
                Visibility = entry.Visibility,
                RequiresMainThread = entry.RequiresMainThread,
                IsMutation = entry.IsMutation,
                DeclaringType = method.DeclaringType,
                MethodName = method.Name,
                Method = method,
                TargetInstance = null,
                Parameters = parameters,
                InputSchema = inputSchema
            };
        }

        /// <summary>
        /// Resolves a MethodInfo from a cache entry.
        /// </summary>
        private MethodInfo ResolveMethodFromCache(LiveLinkToolCacheEntry entry)
        {
            if (string.IsNullOrEmpty(entry.AssemblyName) ||
                string.IsNullOrEmpty(entry.TypeName) ||
                string.IsNullOrEmpty(entry.MethodName))
            {
                return null;
            }

            try
            {
                Assembly assembly = null;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].GetName().Name == entry.AssemblyName)
                    {
                        assembly = assemblies[i];
                        break;
                    }
                }

                if (assembly == null)
                    return null;

                Type type = assembly.GetType(entry.TypeName, throwOnError: false);
                if (type == null)
                    return null;

                // Get the method (static, public or non-public)
                MethodInfo method = type.GetMethod(
                    entry.MethodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                );

                return method;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Scans assemblies for [LiveLinkTool] attributes (fallback when no cache available).
        /// </summary>
        private void ScanAssembliesForAttributes(IReadOnlyList<string> assemblyAllowList)
        {
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
            var result = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count > 0)
            {
                result["required"] = required;
            }
            return result;
        }

        private static JObject BuildTypeSchema(Type type)
        {
            Type nonNullable = Nullable.GetUnderlyingType(type) ?? type;
            if (nonNullable == typeof(string))
            {
                return new JObject { ["type"] = "string" };
            }
            if (nonNullable == typeof(int) || nonNullable == typeof(long) || nonNullable == typeof(short) || nonNullable == typeof(byte))
            {
                return new JObject { ["type"] = "integer" };
            }
            if (nonNullable == typeof(float) || nonNullable == typeof(double) || nonNullable == typeof(decimal))
            {
                return new JObject { ["type"] = "number" };
            }
            if (nonNullable == typeof(bool))
            {
                return new JObject { ["type"] = "boolean" };
            }
            if (nonNullable.IsArray || (nonNullable.IsGenericType && nonNullable.GetGenericTypeDefinition() == typeof(List<>)))
            {
                Type itemType = nonNullable.IsArray ? nonNullable.GetElementType() : nonNullable.GetGenericArguments()[0];
                return new JObject
                {
                    ["type"] = "array",
                    ["items"] = BuildTypeSchema(itemType)
                };
            }
            return new JObject { ["type"] = "object" };
        }

        private static bool IsNullable(Type type)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        }
    }
}
