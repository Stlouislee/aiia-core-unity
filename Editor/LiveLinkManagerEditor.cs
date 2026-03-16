using UnityEngine;
using UnityEditor;
using LiveLink;

namespace LiveLink.Editor
{
    /// <summary>
    /// Custom inspector for LiveLinkManager component.
    /// Provides status display and debug controls.
    /// </summary>
    [CustomEditor(typeof(LiveLinkManager))]
    public class LiveLinkManagerEditor : UnityEditor.Editor
    {
        private LiveLinkManager _manager;
        private GUIStyle _statusStyle;
        private GUIStyle _boxStyle;
        private bool _showPrefabs = true;
        private bool _showToolList = true;
        private readonly System.Collections.Generic.List<string> _toolListCache = new System.Collections.Generic.List<string>();

        private SerializedProperty _port;
        private SerializedProperty _mcpPort;
        private SerializedProperty _enableMCPServer;
        private SerializedProperty _autoStart;
        private SerializedProperty _scope;
        private SerializedProperty _targetRoot;
        private SerializedProperty _includeInactive;
        private SerializedProperty _syncFrequency;
        private SerializedProperty _useDeltaSync;
        private SerializedProperty _deltaThreshold;
        private SerializedProperty _spawnablePrefabs;
        private SerializedProperty _debugLogging;
        private SerializedProperty _enableDynamicMcpTools;
        private SerializedProperty _dynamicToolAssemblyAllowList;
        private SerializedProperty _dynamicToolManifestAssets;
        private SerializedProperty _exposeDynamicToolsToExternal;
        private SerializedProperty _exposeDynamicToolsToEmbeddedAgent;
        private SerializedProperty _allowDynamicMutationToolsForExternal;
        private SerializedProperty _allowDynamicMutationToolsForEmbeddedAgent;
        private SerializedProperty _dynamicExternalToolAllowList;
        private SerializedProperty _dynamicExternalToolDenyList;
        private SerializedProperty _dynamicAgentToolAllowList;
        private SerializedProperty _dynamicAgentToolDenyList;
        private SerializedProperty _dynamicAllowedCategories;
        private SerializedProperty _dynamicAllowedTags;

        private void OnEnable()
        {
            _manager = (LiveLinkManager)target;

            _port = serializedObject.FindProperty("_port");
            _mcpPort = serializedObject.FindProperty("_mcpPort");
            _enableMCPServer = serializedObject.FindProperty("_enableMCPServer");
            _autoStart = serializedObject.FindProperty("_autoStart");
            _scope = serializedObject.FindProperty("_scope");
            _targetRoot = serializedObject.FindProperty("_targetRoot");
            _includeInactive = serializedObject.FindProperty("_includeInactive");
            _syncFrequency = serializedObject.FindProperty("_syncFrequency");
            _useDeltaSync = serializedObject.FindProperty("_useDeltaSync");
            _deltaThreshold = serializedObject.FindProperty("_deltaThreshold");
            _spawnablePrefabs = serializedObject.FindProperty("_spawnablePrefabs");
            _debugLogging = serializedObject.FindProperty("_debugLogging");
            _enableDynamicMcpTools = serializedObject.FindProperty("_enableDynamicMcpTools");
            _dynamicToolAssemblyAllowList = serializedObject.FindProperty("_dynamicToolAssemblyAllowList");
            _dynamicToolManifestAssets = serializedObject.FindProperty("_dynamicToolManifestAssets");
            _exposeDynamicToolsToExternal = serializedObject.FindProperty("_exposeDynamicToolsToExternal");
            _exposeDynamicToolsToEmbeddedAgent = serializedObject.FindProperty("_exposeDynamicToolsToEmbeddedAgent");
            _allowDynamicMutationToolsForExternal = serializedObject.FindProperty("_allowDynamicMutationToolsForExternal");
            _allowDynamicMutationToolsForEmbeddedAgent = serializedObject.FindProperty("_allowDynamicMutationToolsForEmbeddedAgent");
            _dynamicExternalToolAllowList = serializedObject.FindProperty("_dynamicExternalToolAllowList");
            _dynamicExternalToolDenyList = serializedObject.FindProperty("_dynamicExternalToolDenyList");
            _dynamicAgentToolAllowList = serializedObject.FindProperty("_dynamicAgentToolAllowList");
            _dynamicAgentToolDenyList = serializedObject.FindProperty("_dynamicAgentToolDenyList");
            _dynamicAllowedCategories = serializedObject.FindProperty("_dynamicAllowedCategories");
            _dynamicAllowedTags = serializedObject.FindProperty("_dynamicAllowedTags");

            RefreshToolList();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            InitStyles();
            
            DrawStatusBox();
            EditorGUILayout.Space(10);
            
            DrawMCPStatusBox();
            EditorGUILayout.Space(10);
            
            DrawServerControls();
            EditorGUILayout.Space(10);
            
            DrawServerConfiguration();
            EditorGUILayout.Space(5);
            
            DrawSyncConfiguration();
            EditorGUILayout.Space(5);
            
            DrawSpawnablePrefabs();
            EditorGUILayout.Space(5);

            DrawDynamicToolConfiguration();
            EditorGUILayout.Space(5);

            DrawToolListSection();
            EditorGUILayout.Space(5);
            
            DrawDebugSection();

            serializedObject.ApplyModifiedProperties();

            // Repaint during play mode to update status
            if (Application.isPlaying)
            {
                Repaint();
            }
        }

        private void InitStyles()
        {
            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleCenter
                };
            }

            if (_boxStyle == null)
            {
                _boxStyle = new GUIStyle(GUI.skin.box)
                {
                    padding = new RectOffset(10, 10, 10, 10)
                };
            }
        }

        private void DrawStatusBox()
        {
            EditorGUILayout.BeginVertical(_boxStyle);
            
            EditorGUILayout.LabelField("LiveLink Status", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            bool isRunning = Application.isPlaying && _manager.IsServerRunning;
            int clientCount = Application.isPlaying ? _manager.ClientCount : 0;

            // Status indicator
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Server:", GUILayout.Width(60));
            
            Color originalColor = GUI.color;
            if (isRunning)
            {
                GUI.color = Color.green;
                EditorGUILayout.LabelField("● Running", _statusStyle);
            }
            else
            {
                GUI.color = Application.isPlaying ? Color.red : Color.gray;
                EditorGUILayout.LabelField(Application.isPlaying ? "● Stopped" : "● Not Playing", _statusStyle);
            }
            GUI.color = originalColor;
            EditorGUILayout.EndHorizontal();

            // Client count
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Clients:", GUILayout.Width(60));
            EditorGUILayout.LabelField(clientCount.ToString(), EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            // Port info
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Port:", GUILayout.Width(60));
            EditorGUILayout.LabelField(_port.intValue.ToString());
            EditorGUILayout.EndHorizontal();

            if (isRunning)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox($"WebSocket URL: ws://localhost:{_port.intValue}/", MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawMCPStatusBox()
        {
            EditorGUILayout.BeginVertical(_boxStyle);
            
            EditorGUILayout.LabelField("MCP Server Status", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            bool isMCPEnabled = _enableMCPServer.boolValue;
            bool isMCPRunning = Application.isPlaying && isMCPEnabled && _manager.IsServerRunning;
            
            // Check if MCP server is actually running using reflection
            var mcpServerField = typeof(LiveLinkManager).GetField("_mcpHttpServer", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            object mcpServer = mcpServerField?.GetValue(_manager);
            if (mcpServer != null)
            {
                var isRunningProp = mcpServer.GetType().GetProperty("IsRunning");
                isMCPRunning = Application.isPlaying && (bool)isRunningProp.GetValue(mcpServer);
            }
            else
            {
                isMCPRunning = false;
            }

            // Status indicator
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Server:", GUILayout.Width(60));
            
            Color originalColor = GUI.color;
            if (isMCPRunning)
            {
                GUI.color = Color.green;
                EditorGUILayout.LabelField("● Running", _statusStyle);
            }
            else if (!isMCPEnabled)
            {
                GUI.color = Color.gray;
                EditorGUILayout.LabelField("● Disabled", _statusStyle);
            }
            else
            {
                GUI.color = Application.isPlaying ? Color.red : Color.gray;
                EditorGUILayout.LabelField(Application.isPlaying ? "● Stopped" : "● Not Playing", _statusStyle);
            }
            GUI.color = originalColor;
            EditorGUILayout.EndHorizontal();

            // Port info
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Port:", GUILayout.Width(60));
            EditorGUILayout.LabelField(_mcpPort.intValue.ToString());
            EditorGUILayout.EndHorizontal();

            // Transport info
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Transport:", GUILayout.Width(60));
            EditorGUILayout.LabelField("HTTP + SSE");
            EditorGUILayout.EndHorizontal();

            // Session count
            if (isMCPRunning && Application.isPlaying)
            {
                int mcpClientCount = _manager.MCPClientCount;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Sessions:", GUILayout.Width(60));
                EditorGUILayout.LabelField(mcpClientCount.ToString(), EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();
            }

            if (isMCPRunning)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox($"MCP Endpoint: http://localhost:{_mcpPort.intValue}/mcp\nSSE Endpoint: http://localhost:{_mcpPort.intValue}/sse", MessageType.Info);
            }
            else if (!isMCPEnabled && Application.isPlaying)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox("MCP server is disabled. Enable it in Server Configuration.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawServerControls()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to control the server.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();

            GUI.enabled = !_manager.IsServerRunning;
            if (GUILayout.Button("Start Server", GUILayout.Height(30)))
            {
                _manager.StartServer();
            }

            GUI.enabled = _manager.IsServerRunning;
            if (GUILayout.Button("Stop Server", GUILayout.Height(30)))
            {
                _manager.StopServer();
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // MCP Server Controls
            if (_enableMCPServer.boolValue)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.BeginHorizontal();

                var mcpServerField = typeof(LiveLinkManager).GetField("_mcpHttpServer", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                object mcpServer = mcpServerField?.GetValue(_manager);
                bool isMCPRunning = false;
                if (mcpServer != null)
                {
                    var isRunningProp = mcpServer.GetType().GetProperty("IsRunning");
                    isMCPRunning = (bool)isRunningProp.GetValue(mcpServer);
                }

                GUI.enabled = !isMCPRunning;
                if (GUILayout.Button("Start MCP Server", GUILayout.Height(30)))
                {
                    _manager.StartMCPServer();
                }

                GUI.enabled = isMCPRunning;
                if (GUILayout.Button("Stop MCP Server", GUILayout.Height(30)))
                {
                    _manager.StopMCPServer();
                }

                GUI.enabled = true;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(5);

            GUI.enabled = _manager.IsServerRunning && _manager.ClientCount > 0;
            if (GUILayout.Button("Force Full Sync", GUILayout.Height(25)))
            {
                _manager.ForceFullSync();
            }
            GUI.enabled = true;
        }

        private void DrawServerConfiguration()
        {
            EditorGUILayout.LabelField("Server Configuration", EditorStyles.boldLabel);
            
            EditorGUI.BeginDisabledGroup(Application.isPlaying);
            
            EditorGUILayout.PropertyField(_port, new GUIContent("Port", "WebSocket server port"));
            EditorGUILayout.PropertyField(_mcpPort, new GUIContent("MCP Port", "MCP HTTP server port"));
            EditorGUILayout.PropertyField(_enableMCPServer, new GUIContent("Enable MCP Server", "Enable HTTP + SSE transport for MCP"));
            EditorGUILayout.PropertyField(_autoStart, new GUIContent("Auto Start", "Start server automatically on play"));
            
            EditorGUI.EndDisabledGroup();
        }

        private void DrawSyncConfiguration()
        {
            EditorGUILayout.LabelField("Sync Configuration", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_scope, new GUIContent("Scope", "What to synchronize"));
            
            // Show target root only when scope is TargetObjectOnly
            if ((ScanScope)_scope.enumValueIndex == ScanScope.TargetObjectOnly)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_targetRoot, new GUIContent("Target Root", "Root object to sync"));
                if (_targetRoot.objectReferenceValue == null)
                {
                    EditorGUILayout.HelpBox("Assign a Target Root when using TargetObjectOnly scope.", MessageType.Warning);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(_includeInactive, new GUIContent("Include Inactive", "Include inactive GameObjects"));
            EditorGUILayout.PropertyField(_syncFrequency, new GUIContent("Sync Frequency (Hz)", "How often to send updates (0 = manual)"));
            EditorGUILayout.PropertyField(_useDeltaSync, new GUIContent("Delta Sync", "Only send changed objects"));
            
            if (_useDeltaSync.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_deltaThreshold, new GUIContent("Threshold", "Distance threshold for change detection"));
                EditorGUI.indentLevel--;
            }
        }

        private void DrawSpawnablePrefabs()
        {
            EditorGUILayout.BeginHorizontal();
            _showPrefabs = EditorGUILayout.Foldout(_showPrefabs, "Spawnable Prefabs", true);
            
            if (_showPrefabs)
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("+", GUILayout.Width(25)))
                {
                    _spawnablePrefabs.arraySize++;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_showPrefabs)
            {
                EditorGUI.indentLevel++;
                
                for (int i = 0; i < _spawnablePrefabs.arraySize; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    
                    var element = _spawnablePrefabs.GetArrayElementAtIndex(i);
                    EditorGUILayout.PropertyField(element, GUIContent.none);
                    
                    if (GUILayout.Button("-", GUILayout.Width(25)))
                    {
                        _spawnablePrefabs.DeleteArrayElementAtIndex(i);
                        break;
                    }
                    
                    EditorGUILayout.EndHorizontal();
                }
                
                if (_spawnablePrefabs.arraySize == 0)
                {
                    EditorGUILayout.HelpBox("Add prefabs that can be spawned by external commands.", MessageType.Info);
                }
                
                EditorGUI.indentLevel--;
            }
        }

        private void DrawDynamicToolConfiguration()
        {
            EditorGUILayout.LabelField("Dynamic MCP Tools", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_enableDynamicMcpTools, new GUIContent("Enable Dynamic MCP Tools"));

            if (!_enableDynamicMcpTools.boolValue)
            {
                EditorGUILayout.HelpBox("Annotation-based dynamic tools are disabled.", MessageType.Info);
                return;
            }

            EditorGUILayout.PropertyField(_dynamicToolAssemblyAllowList, new GUIContent("Assembly Allow List"), true);
            EditorGUILayout.PropertyField(_dynamicToolManifestAssets, new GUIContent("Tool Manifest Assets"), true);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Embedded Agent", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(_exposeDynamicToolsToEmbeddedAgent, new GUIContent("Expose To Embedded Agent"));
            EditorGUILayout.PropertyField(_allowDynamicMutationToolsForEmbeddedAgent, new GUIContent("Allow Mutation Tools"));
            EditorGUILayout.PropertyField(_dynamicAgentToolAllowList, new GUIContent("Allow List"), true);
            EditorGUILayout.PropertyField(_dynamicAgentToolDenyList, new GUIContent("Deny List"), true);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("External MCP Clients", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(_exposeDynamicToolsToExternal, new GUIContent("Expose To External MCP"));
            EditorGUILayout.PropertyField(_allowDynamicMutationToolsForExternal, new GUIContent("Allow Mutation Tools"));
            EditorGUILayout.PropertyField(_dynamicExternalToolAllowList, new GUIContent("Allow List"), true);
            EditorGUILayout.PropertyField(_dynamicExternalToolDenyList, new GUIContent("Deny List"), true);

            EditorGUILayout.Space(2);
            EditorGUILayout.PropertyField(_dynamicAllowedCategories, new GUIContent("Allowed Categories"), true);
            EditorGUILayout.PropertyField(_dynamicAllowedTags, new GUIContent("Allowed Tags"), true);

            EditorGUILayout.HelpBox(
                "Dynamic tools come from methods marked with LiveLinkToolAttribute. " +
                "Allow/Deny lists apply after visibility and mutation rules.",
                MessageType.None);
        }

        private void DrawToolListSection()
        {
            EditorGUILayout.BeginVertical(_boxStyle);
            EditorGUILayout.LabelField("Available MCP Tools", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", GUILayout.Width(90)))
            {
                RefreshToolList();
            }
            EditorGUILayout.EndHorizontal();

            if (_toolListCache.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No cached tool list. Click Refresh to load current legacy and dynamic MCP tools.",
                    MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            _showToolList = EditorGUILayout.Foldout(_showToolList, "Tool List (" + _toolListCache.Count + ")", true);
            if (_showToolList)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < _toolListCache.Count; i++)
                {
                    EditorGUILayout.LabelField("- " + _toolListCache[i]);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void RefreshToolList()
        {
            _toolListCache.Clear();

            if (_manager == null)
            {
                return;
            }

            System.Collections.Generic.List<string> lines = _manager.GetInspectorMcpToolList();
            if (lines == null)
            {
                return;
            }

            _toolListCache.AddRange(lines);
        }

        private void DrawDebugSection()
        {
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_debugLogging, new GUIContent("Debug Logging", "Log all network messages"));
        }

        [MenuItem("LiveLink/Create Manager", false, 10)]
        private static void CreateManager()
        {
            // Check if manager already exists
            var existing = FindExistingManager();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing);
                Debug.Log("[LiveLink] LiveLinkManager already exists in the scene.");
                return;
            }

            // Create new manager
            var go = new GameObject("LiveLink Manager");
            go.AddComponent<LiveLinkManager>();
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create LiveLink Manager");

            Debug.Log("[LiveLink] Created LiveLinkManager in the scene.");
        }

        private static LiveLinkManager FindExistingManager()
        {
#if UNITY_2022_2_OR_NEWER
            return Object.FindAnyObjectByType<LiveLinkManager>();
#else
            return Object.FindObjectOfType<LiveLinkManager>();
#endif
        }

        [MenuItem("LiveLink/Documentation", false, 100)]
        private static void OpenDocumentation()
        {
            Application.OpenURL("https://github.com/Stlouislee/aiia-core-unity#readme");
        }
    }
}
