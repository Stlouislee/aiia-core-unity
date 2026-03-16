using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveLink.Tools
{
    [CreateAssetMenu(fileName = "LiveLinkToolManifest", menuName = "LiveLink/Tool Manifest")]
    public sealed class LiveLinkToolManifestAsset : ScriptableObject
    {
        [SerializeField]
        private List<LiveLinkToolManifestEntry> _tools = new List<LiveLinkToolManifestEntry>();

        public IReadOnlyList<LiveLinkToolManifestEntry> Tools
        {
            get { return _tools; }
        }
    }

    [Serializable]
    public sealed class LiveLinkToolManifestEntry
    {
        [SerializeField]
        private string _toolName = string.Empty;

        [SerializeField]
        [TextArea(2, 5)]
        private string _description = string.Empty;

        [SerializeField]
        private string _assemblyName = string.Empty;

        [SerializeField]
        private string _typeName = string.Empty;

        [SerializeField]
        private string _methodName = string.Empty;

        [SerializeField]
        private int _expectedParameterCount = -1;

        [SerializeField]
        private LiveLinkToolVisibility _visibility = LiveLinkToolVisibility.Both;

        [SerializeField]
        private bool _requiresMainThread;

        [SerializeField]
        private bool _isMutation;

        [SerializeField]
        private string _category = string.Empty;

        [SerializeField]
        private List<string> _tags = new List<string>();

        [SerializeField]
        private List<LiveLinkToolManifestParameterOverride> _parameterOverrides = new List<LiveLinkToolManifestParameterOverride>();

        public string ToolName => _toolName;
        public string Description => _description;
        public string AssemblyName => _assemblyName;
        public string TypeName => _typeName;
        public string MethodName => _methodName;
        public int ExpectedParameterCount => _expectedParameterCount;
        public LiveLinkToolVisibility Visibility => _visibility;
        public bool RequiresMainThread => _requiresMainThread;
        public bool IsMutation => _isMutation;
        public string Category => _category;
        public IReadOnlyList<string> Tags => _tags;
        public IReadOnlyList<LiveLinkToolManifestParameterOverride> ParameterOverrides => _parameterOverrides;
    }

    [Serializable]
    public sealed class LiveLinkToolManifestParameterOverride
    {
        [SerializeField]
        private string _methodParameterName = string.Empty;

        [SerializeField]
        private string _exposedParameterName = string.Empty;

        [SerializeField]
        private string _description = string.Empty;

        [SerializeField]
        private bool _overrideRequired;

        [SerializeField]
        private bool _required;

        public string MethodParameterName => _methodParameterName;
        public string ExposedParameterName => _exposedParameterName;
        public string Description => _description;
        public bool OverrideRequired => _overrideRequired;
        public bool Required => _required;
    }
}
