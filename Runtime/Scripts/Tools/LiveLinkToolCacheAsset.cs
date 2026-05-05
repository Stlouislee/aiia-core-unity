using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("LiveLink.Editor")]

namespace LiveLink.Tools
{
    /// <summary>
    /// Pre-computed cache asset for dynamic MCP tools.
    /// Generated at edit-time to avoid runtime reflection scanning.
    /// </summary>
    [CreateAssetMenu(fileName = "LiveLinkToolCache", menuName = "LiveLink/Tool Cache")]
    public sealed class LiveLinkToolCacheAsset : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Timestamp when this cache was built.")]
        private long _buildTimestamp;

        [SerializeField]
        [Tooltip("Assembly names and their hash values at build time. Used to detect stale cache.")]
        private List<AssemblyHashEntry> _assemblyHashes = new List<AssemblyHashEntry>();

        [SerializeField]
        [Tooltip("Pre-computed tool descriptors from attribute scanning.")]
        private List<LiveLinkToolCacheEntry> _tools = new List<LiveLinkToolCacheEntry>();

        /// <summary>
        /// Timestamp when this cache was built (DateTime.UtcNow.Ticks).
        /// </summary>
        public long BuildTimestamp => _buildTimestamp;

        /// <summary>
        /// Assembly hashes for staleness detection.
        /// </summary>
        public IReadOnlyList<AssemblyHashEntry> AssemblyHashes => _assemblyHashes;

        /// <summary>
        /// Pre-computed tool entries.
        /// </summary>
        public IReadOnlyList<LiveLinkToolCacheEntry> Tools => _tools;

        /// <summary>
        /// Checks if the cache is stale (assemblies have changed).
        /// </summary>
        public bool IsStale
        {
            get
            {
                if (_assemblyHashes == null || _assemblyHashes.Count == 0)
                    return true;

                var currentAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < _assemblyHashes.Count; i++)
                {
                    var entry = _assemblyHashes[i];
                    if (string.IsNullOrEmpty(entry.AssemblyName))
                        continue;

                    // Find the assembly
                    Assembly assembly = null;
                    for (int j = 0; j < currentAssemblies.Length; j++)
                    {
                        if (currentAssemblies[j].GetName().Name == entry.AssemblyName)
                        {
                            assembly = currentAssemblies[j];
                            break;
                        }
                    }

                    if (assembly == null)
                        continue; // Assembly not loaded, skip

                    // Check if hash changed
                    string currentHash = ComputeAssemblyHash(assembly);
                    if (currentHash != entry.Hash)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Sets the cache content (editor-only).
        /// </summary>
        internal void SetContent(long timestamp, List<AssemblyHashEntry> hashes, List<LiveLinkToolCacheEntry> tools)
        {
            _buildTimestamp = timestamp;
            _assemblyHashes = hashes ?? new List<AssemblyHashEntry>();
            _tools = tools ?? new List<LiveLinkToolCacheEntry>();
        }

        /// <summary>
        /// Computes a simple hash for an assembly based on its location and modification time.
        /// </summary>
        internal static string ComputeAssemblyHash(Assembly assembly)
        {
            if (assembly == null)
                return string.Empty;

            try
            {
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location))
                    return assembly.GetName().Version?.ToString() ?? assembly.GetName().Name;

                var fileInfo = new System.IO.FileInfo(location);
                return $"{fileInfo.LastWriteTimeUtc.Ticks:X8}";
            }
            catch
            {
                return assembly.GetName().Name ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Assembly name and its hash for staleness detection.
    /// </summary>
    [Serializable]
    public sealed class AssemblyHashEntry
    {
        [SerializeField] private string _assemblyName;
        [SerializeField] private string _hash;

        public string AssemblyName => _assemblyName;
        public string Hash => _hash;

        public AssemblyHashEntry(string assemblyName, string hash)
        {
            _assemblyName = assemblyName;
            _hash = hash;
        }
    }

    /// <summary>
    /// Pre-computed tool descriptor entry.
    /// </summary>
    [Serializable]
    public sealed class LiveLinkToolCacheEntry
    {
        [SerializeField] private string _toolName;
        [SerializeField] [TextArea(1, 5)] private string _description;
        [SerializeField] private string _category;
        [SerializeField] private List<string> _tags = new List<string>();
        [SerializeField] private LiveLinkToolVisibility _visibility;
        [SerializeField] private bool _requiresMainThread;
        [SerializeField] private bool _isMutation;

        // Method reference (assembly-qualified)
        [SerializeField] private string _assemblyName;
        [SerializeField] private string _typeName;
        [SerializeField] private string _methodName;

        // Pre-computed schema
        [SerializeField] [TextArea(1, 10)] private string _inputSchemaJson;

        // Parameter descriptors
        [SerializeField] private List<LiveLinkToolParameterCache> _parameters = new List<LiveLinkToolParameterCache>();

        public string ToolName => _toolName;
        public string Description => _description;
        public string Category => _category;
        public IReadOnlyList<string> Tags => _tags;
        public LiveLinkToolVisibility Visibility => _visibility;
        public bool RequiresMainThread => _requiresMainThread;
        public bool IsMutation => _isMutation;
        public string AssemblyName => _assemblyName;
        public string TypeName => _typeName;
        public string MethodName => _methodName;
        public string InputSchemaJson => _inputSchemaJson;
        public IReadOnlyList<LiveLinkToolParameterCache> Parameters => _parameters;

        public LiveLinkToolCacheEntry(
            string toolName,
            string description,
            string category,
            List<string> tags,
            LiveLinkToolVisibility visibility,
            bool requiresMainThread,
            bool isMutation,
            string assemblyName,
            string typeName,
            string methodName,
            string inputSchemaJson,
            List<LiveLinkToolParameterCache> parameters)
        {
            _toolName = toolName;
            _description = description;
            _category = category;
            _tags = tags ?? new List<string>();
            _visibility = visibility;
            _requiresMainThread = requiresMainThread;
            _isMutation = isMutation;
            _assemblyName = assemblyName;
            _typeName = typeName;
            _methodName = methodName;
            _inputSchemaJson = inputSchemaJson;
            _parameters = parameters ?? new List<LiveLinkToolParameterCache>();
        }
    }

    /// <summary>
    /// Cached parameter descriptor.
    /// </summary>
    [Serializable]
    public sealed class LiveLinkToolParameterCache
    {
        [SerializeField] private string _name;
        [SerializeField] private string _description;
        [SerializeField] private string _parameterTypeName; // Assembly-qualified type name
        [SerializeField] private bool _required;
        [SerializeField] private bool _hasDefaultValue;
        [SerializeField] private string _defaultValueJson; // JSON-serialized default value
        [SerializeField] private int _position;

        public string Name => _name;
        public string Description => _description;
        public string ParameterTypeName => _parameterTypeName;
        public bool Required => _required;
        public bool HasDefaultValue => _hasDefaultValue;
        public string DefaultValueJson => _defaultValueJson;
        public int Position => _position;

        public LiveLinkToolParameterCache(
            string name,
            string description,
            string parameterTypeName,
            bool required,
            bool hasDefaultValue,
            string defaultValueJson,
            int position)
        {
            _name = name;
            _description = description;
            _parameterTypeName = parameterTypeName;
            _required = required;
            _hasDefaultValue = hasDefaultValue;
            _defaultValueJson = defaultValueJson;
            _position = position;
        }
    }
}
