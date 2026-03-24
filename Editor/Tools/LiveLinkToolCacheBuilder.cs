using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using LiveLink.Tools;

namespace LiveLink.Editor.Tools
{
    /// <summary>
    /// Editor script that builds the tool cache at edit-time and before builds.
    /// Scans assemblies for [LiveLinkTool] attributes and generates a cache asset.
    /// </summary>
    [InitializeOnLoad]
    public class LiveLinkToolCacheBuilder : IPreprocessBuildWithReport
    {
        private const string CacheAssetPath = "Assets/LiveLink/Resources/LiveLinkToolCache.asset";
        private const string ResourcesPath = "Assets/LiveLink/Resources";
        private const string CacheAssetName = "LiveLinkToolCache";

        public int callbackOrder => 0;

        static LiveLinkToolCacheBuilder()
        {
            // Delay execution to avoid issues during assembly reload
            EditorApplication.delayCall += OnEditorReady;
        }

        private static void OnEditorReady()
        {
            // Auto-rebuild cache if it doesn't exist or is stale
            if (!HasValidCache())
            {
                Debug.Log("[LiveLink] Tool cache not found or stale. Rebuilding...");
                RebuildCache();
            }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            Debug.Log("[LiveLink] Preprocessing build - rebuilding tool cache...");
            RebuildCache();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Checks if a valid cache exists.
        /// </summary>
        public static bool HasValidCache()
        {
            var cache = LoadCacheAsset();
            if (cache == null)
                return false;

            return !cache.IsStale;
        }

        /// <summary>
        /// Loads the cache asset from Resources.
        /// </summary>
        public static LiveLinkToolCacheAsset LoadCacheAsset()
        {
            return AssetDatabase.LoadAssetAtPath<LiveLinkToolCacheAsset>(CacheAssetPath);
        }

        /// <summary>
        /// Rebuilds the tool cache by scanning assemblies for [LiveLinkTool] attributes.
        /// </summary>
        [MenuItem("LiveLink/Rebuild Tool Cache", false, 50)]
        public static void RebuildCache()
        {
            try
            {
                EditorUtility.DisplayProgressBar("LiveLink Tool Cache", "Scanning assemblies...", 0f);

                // Scan for tools
                var (tools, assemblies) = ScanAssembliesForTools();

                EditorUtility.DisplayProgressBar("LiveLink Tool Cache", "Building cache asset...", 0.5f);

                // Build cache asset
                var cache = GetOrCreateCacheAsset();
                var hashes = BuildAssemblyHashes(assemblies);
                cache.SetContent(DateTime.UtcNow.Ticks, hashes, tools);

                EditorUtility.SetDirty(cache);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"[LiveLink] Tool cache rebuilt successfully. Found {tools.Count} tools in {assemblies.Count} assemblies.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink] Failed to rebuild tool cache: {ex.Message}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Scans all non-system assemblies for [LiveLinkTool] attributes.
        /// </summary>
        private static (List<LiveLinkToolCacheEntry> tools, List<Assembly> assemblies) ScanAssembliesForTools()
        {
            var tools = new List<LiveLinkToolCacheEntry>();
            var assemblies = new List<Assembly>();

            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            float totalAssemblies = allAssemblies.Length;
            int processedAssemblies = 0;

            for (int i = 0; i < allAssemblies.Length; i++)
            {
                Assembly assembly = allAssemblies[i];
                processedAssemblies++;

                if (!IsAssemblyAllowed(assembly, null))
                    continue;

                assemblies.Add(assembly);

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
                    continue;

                for (int j = 0; j < types.Length; j++)
                {
                    Type type = types[j];
                    if (type == null)
                        continue;

                    BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                    MethodInfo[] methods = type.GetMethods(flags);

                    for (int k = 0; k < methods.Length; k++)
                    {
                        MethodInfo method = methods[k];
                        LiveLinkToolAttribute attribute = method.GetCustomAttribute<LiveLinkToolAttribute>(false);
                        if (attribute == null)
                            continue;

                        var entry = CreateCacheEntry(type, method, attribute);
                        if (entry != null)
                        {
                            tools.Add(entry);
                            Debug.Log($"[LiveLink] Found tool: {entry.ToolName} ({type.FullName}.{method.Name})");
                        }
                    }
                }

                // Update progress
                float progress = processedAssemblies / totalAssemblies * 0.5f;
                EditorUtility.DisplayProgressBar("LiveLink Tool Cache", $"Scanning {assembly.GetName().Name}...", progress);
            }

            return (tools, assemblies);
        }

        /// <summary>
        /// Creates a cache entry from a method and its attribute.
        /// </summary>
        private static LiveLinkToolCacheEntry CreateCacheEntry(Type declaringType, MethodInfo method, LiveLinkToolAttribute attribute)
        {
            string toolName = attribute.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(toolName))
            {
                Debug.LogWarning($"[LiveLink] Ignoring tool with empty name on method: {declaringType.FullName}.{method.Name}");
                return null;
            }

            if (!method.IsStatic)
            {
                Debug.LogWarning($"[LiveLink] Ignoring non-static tool method: {declaringType.FullName}.{method.Name}");
                return null;
            }

            // Build parameter cache
            var parameters = new List<LiveLinkToolParameterCache>();
            ParameterInfo[] methodParams = method.GetParameters();
            for (int i = 0; i < methodParams.Length; i++)
            {
                ParameterInfo param = methodParams[i];
                LiveLinkToolParameterAttribute paramAttr = param.GetCustomAttribute<LiveLinkToolParameterAttribute>(false);

                string paramName = paramAttr != null && !string.IsNullOrWhiteSpace(paramAttr.Name)
                    ? paramAttr.Name.Trim()
                    : param.Name;

                string defaultValueJson = null;
                if (param.HasDefaultValue && param.DefaultValue != null)
                {
                    try
                    {
                        defaultValueJson = Newtonsoft.Json.JsonConvert.SerializeObject(param.DefaultValue);
                    }
                    catch
                    {
                        defaultValueJson = param.DefaultValue?.ToString();
                    }
                }

                bool required = paramAttr != null && paramAttr.Required;
                if (!required)
                {
                    required = !param.HasDefaultValue && !IsNullable(param.ParameterType);
                }

                parameters.Add(new LiveLinkToolParameterCache(
                    paramName,
                    paramAttr?.Description ?? string.Empty,
                    param.ParameterType.AssemblyQualifiedName,
                    required,
                    param.HasDefaultValue,
                    defaultValueJson,
                    i
                ));
            }

            // Build input schema
            var inputSchema = BuildInputSchema(parameters);

            return new LiveLinkToolCacheEntry(
                toolName,
                attribute.Description ?? string.Empty,
                attribute.Category ?? string.Empty,
                attribute.Tags?.ToList() ?? new List<string>(),
                attribute.Visibility,
                attribute.RequiresMainThread,
                attribute.IsMutation,
                declaringType.Assembly.GetName().Name,
                declaringType.FullName,
                method.Name,
                inputSchema.ToString(Newtonsoft.Json.Formatting.None),
                parameters
            );
        }

        /// <summary>
        /// Builds the input schema JSON from parameter cache.
        /// </summary>
        private static Newtonsoft.Json.Linq.JObject BuildInputSchema(List<LiveLinkToolParameterCache> parameters)
        {
            var properties = new Newtonsoft.Json.Linq.JObject();
            var required = new Newtonsoft.Json.Linq.JArray();

            for (int i = 0; i < parameters.Count; i++)
            {
                var param = parameters[i];
                Newtonsoft.Json.Linq.JObject schema = BuildTypeSchema(param.ParameterTypeName);

                if (!string.IsNullOrWhiteSpace(param.Description))
                {
                    schema["description"] = param.Description;
                }

                properties[param.Name] = schema;

                if (param.Required)
                {
                    required.Add(param.Name);
                }
            }

            var result = new Newtonsoft.Json.Linq.JObject
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

        /// <summary>
        /// Builds a JSON schema for a type.
        /// </summary>
        private static Newtonsoft.Json.Linq.JObject BuildTypeSchema(string assemblyQualifiedName)
        {
            Type type = Type.GetType(assemblyQualifiedName);
            if (type == null)
            {
                return new Newtonsoft.Json.Linq.JObject { ["type"] = "object" };
            }

            type = Nullable.GetUnderlyingType(type) ?? type;

            if (type == typeof(string))
                return new Newtonsoft.Json.Linq.JObject { ["type"] = "string" };
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
                return new Newtonsoft.Json.Linq.JObject { ["type"] = "integer" };
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new Newtonsoft.Json.Linq.JObject { ["type"] = "number" };
            if (type == typeof(bool))
                return new Newtonsoft.Json.Linq.JObject { ["type"] = "boolean" };
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            {
                var itemType = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
                return new Newtonsoft.Json.Linq.JObject
                {
                    ["type"] = "array",
                    ["items"] = BuildTypeSchema(itemType.AssemblyQualifiedName)
                };
            }

            return new Newtonsoft.Json.Linq.JObject { ["type"] = "object" };
        }

        /// <summary>
        /// Builds assembly hashes for staleness detection.
        /// </summary>
        private static List<AssemblyHashEntry> BuildAssemblyHashes(List<Assembly> assemblies)
        {
            var hashes = new List<AssemblyHashEntry>();
            for (int i = 0; i < assemblies.Count; i++)
            {
                Assembly assembly = assemblies[i];
                string hash = LiveLinkToolCacheAsset.ComputeAssemblyHash(assembly);
                hashes.Add(new AssemblyHashEntry(assembly.GetName().Name, hash));
            }
            return hashes;
        }

        /// <summary>
        /// Gets or creates the cache asset.
        /// </summary>
        private static LiveLinkToolCacheAsset GetOrCreateCacheAsset()
        {
            var cache = LoadCacheAsset();
            if (cache != null)
                return cache;

            // Create directory if needed
            if (!Directory.Exists(ResourcesPath))
            {
                Directory.CreateDirectory(ResourcesPath);
            }

            cache = ScriptableObject.CreateInstance<LiveLinkToolCacheAsset>();
            AssetDatabase.CreateAsset(cache, CacheAssetPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[LiveLink] Created new tool cache asset at {CacheAssetPath}");
            return cache;
        }

        /// <summary>
        /// Checks if an assembly should be scanned.
        /// </summary>
        private static bool IsAssemblyAllowed(Assembly assembly, IReadOnlyList<string> allowList)
        {
            if (assembly == null)
                return false;

            string assemblyName = assembly.GetName().Name;
            if (string.IsNullOrEmpty(assemblyName))
                return false;

            // Skip system assemblies
            if (assemblyName.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("nunit", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("JetBrains", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // If allow list is specified, check against it
            if (allowList != null && allowList.Count > 0)
            {
                for (int i = 0; i < allowList.Count; i++)
                {
                    if (string.Equals(assemblyName, allowList[i]?.Trim(), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            return true;
        }

        private static bool IsNullable(Type type)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        }
    }
}
