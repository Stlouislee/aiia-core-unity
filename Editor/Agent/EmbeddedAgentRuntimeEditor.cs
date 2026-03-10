using UnityEditor;
using UnityEngine;

namespace LiveLink.Agent.Editor
{
    /// <summary>
    /// Inspector for the embedded runtime component.
    /// </summary>
    [CustomEditor(typeof(EmbeddedAgentRuntime))]
    public class EmbeddedAgentRuntimeEditor : UnityEditor.Editor
    {
        private static string _inspectorTestPrompt =
            "Give me a quick orientation to this scene. Identify one object that looks important, " +
            "and tell me its name, whether it is active, and roughly where it is.";

        private SerializedProperty _config;
        private SerializedProperty _liveLinkManager;
        private SerializedProperty _autoInitialize;
        private SerializedProperty _createSessionOnInitialize;
        private SerializedProperty _persistAcrossScenes;
        private SerializedProperty _onResponseReceived;
        private SerializedProperty _onError;
        private SerializedProperty _onStatusChanged;

        private void OnEnable()
        {
            _config = serializedObject.FindProperty("_config");
            _liveLinkManager = serializedObject.FindProperty("_liveLinkManager");
            _autoInitialize = serializedObject.FindProperty("_autoInitialize");
            _createSessionOnInitialize = serializedObject.FindProperty("_createSessionOnInitialize");
            _persistAcrossScenes = serializedObject.FindProperty("_persistAcrossScenes");
            _onResponseReceived = serializedObject.FindProperty("_onResponseReceived");
            _onError = serializedObject.FindProperty("_onError");
            _onStatusChanged = serializedObject.FindProperty("_onStatusChanged");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EmbeddedAgentRuntime runtime = (EmbeddedAgentRuntime)target;

            DrawStatus(runtime);
            EditorGUILayout.Space(8);

            EditorGUILayout.PropertyField(_config);
            EditorGUILayout.PropertyField(_liveLinkManager);
            EditorGUILayout.PropertyField(_autoInitialize);
            EditorGUILayout.PropertyField(_createSessionOnInitialize);
            EditorGUILayout.PropertyField(_persistAcrossScenes);

            EditorGUILayout.Space(8);
            EditorGUILayout.PropertyField(_onResponseReceived);
            EditorGUILayout.PropertyField(_onError);
            EditorGUILayout.PropertyField(_onStatusChanged);

            serializedObject.ApplyModifiedProperties();

            DrawRuntimeControls(runtime);
        }

        private static void DrawStatus(EmbeddedAgentRuntime runtime)
        {
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Initialized", runtime.IsInitialized ? "Yes" : "No");
            EditorGUILayout.LabelField("Busy", runtime.IsBusy ? "Yes" : "No");
            EditorGUILayout.LabelField("Connected Servers", runtime.ConnectedServerCount.ToString());
            EditorGUILayout.LabelField("Status", string.IsNullOrEmpty(runtime.Status) ? "Idle" : runtime.Status);

            if (runtime.AvailableToolNames != null && runtime.AvailableToolNames.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Available Tools", string.Join(", ", runtime.AvailableToolNames));
            }

            if (!string.IsNullOrEmpty(runtime.LastError))
            {
                EditorGUILayout.HelpBox(runtime.LastError, MessageType.Error);
            }
            else if (!string.IsNullOrEmpty(runtime.LastResponse))
            {
                EditorGUILayout.HelpBox(runtime.LastResponse, MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawRuntimeControls(EmbeddedAgentRuntime runtime)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime Controls", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to initialize the embedded agent runtime.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Initialize"))
            {
                runtime.InitializeRuntime();
            }

            if (GUILayout.Button("Reinitialize"))
            {
                runtime.ReinitializeRuntime();
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset Session"))
            {
                runtime.ResetSession();
            }

            if (runtime.Config == null)
            {
                EditorGUILayout.HelpBox("Assign an AgentRuntimeConfig asset before starting the runtime.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Inspector Test", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This sends a sample user message to the embedded agent. The default prompt is written to naturally make the agent inspect the scene first.",
                MessageType.Info);

            _inspectorTestPrompt = EditorGUILayout.TextArea(_inspectorTestPrompt, GUILayout.MinHeight(56f));

            using (new EditorGUI.DisabledScope(runtime.IsBusy || string.IsNullOrWhiteSpace(_inspectorTestPrompt)))
            {
                if (GUILayout.Button("Run Suggested Test"))
                {
                    Debug.Log("[LiveLink-Agent] Running inspector test prompt.");
                    runtime.SubmitMessage(_inspectorTestPrompt);
                }
            }
        }

        [MenuItem("LiveLink/Create Embedded Agent Runtime", false, 20)]
        private static void CreateEmbeddedAgentRuntime()
        {
            GameObject go = new GameObject("LiveLink Embedded Agent");
            go.AddComponent<EmbeddedAgentRuntime>();
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Create Embedded Agent Runtime");
        }
    }
}
