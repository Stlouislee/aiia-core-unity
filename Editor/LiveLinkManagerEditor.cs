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

        private SerializedProperty _port;
        private SerializedProperty _autoStart;
        private SerializedProperty _scope;
        private SerializedProperty _targetRoot;
        private SerializedProperty _includeInactive;
        private SerializedProperty _syncFrequency;
        private SerializedProperty _useDeltaSync;
        private SerializedProperty _deltaThreshold;
        private SerializedProperty _spawnablePrefabs;
        private SerializedProperty _debugLogging;

        private void OnEnable()
        {
            _manager = (LiveLinkManager)target;

            _port = serializedObject.FindProperty("_port");
            _autoStart = serializedObject.FindProperty("_autoStart");
            _scope = serializedObject.FindProperty("_scope");
            _targetRoot = serializedObject.FindProperty("_targetRoot");
            _includeInactive = serializedObject.FindProperty("_includeInactive");
            _syncFrequency = serializedObject.FindProperty("_syncFrequency");
            _useDeltaSync = serializedObject.FindProperty("_useDeltaSync");
            _deltaThreshold = serializedObject.FindProperty("_deltaThreshold");
            _spawnablePrefabs = serializedObject.FindProperty("_spawnablePrefabs");
            _debugLogging = serializedObject.FindProperty("_debugLogging");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            InitStyles();
            
            DrawStatusBox();
            EditorGUILayout.Space(10);
            
            DrawServerControls();
            EditorGUILayout.Space(10);
            
            DrawServerConfiguration();
            EditorGUILayout.Space(5);
            
            DrawSyncConfiguration();
            EditorGUILayout.Space(5);
            
            DrawSpawnablePrefabs();
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

        private void DrawDebugSection()
        {
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_debugLogging, new GUIContent("Debug Logging", "Log all network messages"));
        }

        [MenuItem("LiveLink/Create Manager", false, 10)]
        private static void CreateManager()
        {
            // Check if manager already exists
            var existing = Object.FindObjectOfType<LiveLinkManager>();
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

        [MenuItem("LiveLink/Documentation", false, 100)]
        private static void OpenDocumentation()
        {
            Application.OpenURL("https://github.com/Stlouislee/aiia-core-unity#readme");
        }
    }
}
