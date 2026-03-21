using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
using UnityEngine;

namespace LiveLink.Agent.Editor
{
    /// <summary>
    /// Inspector UI for configuring downstream MCP servers used by the embedded agent.
    /// </summary>
    [CustomEditor(typeof(AgentRuntimeConfig))]
    public class AgentRuntimeConfigEditor : UnityEditor.Editor
    {
        private sealed class CreateConfigAssetAction : EndNameEditAction
        {
            public override void Action(int instanceId, string pathName, string resourceFile)
            {
                AgentRuntimeConfig asset = CreateInstance<AgentRuntimeConfig>();
                AssetDatabase.CreateAsset(asset, pathName);
                AssetDatabase.SaveAssets();
                ProjectWindowUtil.ShowCreatedAsset(asset);
            }
        }

        private SerializedProperty _agentName;
        private SerializedProperty _openAIModel;
        private SerializedProperty _preferEnvironmentApiKey;
        private SerializedProperty _openAIApiKeyEnvironmentVariable;
        private SerializedProperty _openAIApiKey;
        private SerializedProperty _systemInstructions;
        private SerializedProperty _enableLocalLiveLinkMcp;
        private SerializedProperty _autoStartLocalLiveLinkMcp;
        private SerializedProperty _localHttpTransportMode;
        private SerializedProperty _localConnectionTimeoutSeconds;
        private SerializedProperty _allowSceneMutationTools;
        private SerializedProperty _enablePersistentChatHistory;
        private SerializedProperty _chatHistoryConversationId;
        private SerializedProperty _chatHistoryStorageSubdirectory;
        private SerializedProperty _maxPersistedMessages;
        private SerializedProperty _maxHistoryFileSizeBytes;
        private SerializedProperty _externalMcpServers;

        private void OnEnable()
        {
            _agentName = serializedObject.FindProperty("_agentName");
            _openAIModel = serializedObject.FindProperty("_openAIModel");
            _preferEnvironmentApiKey = serializedObject.FindProperty("_preferEnvironmentApiKey");
            _openAIApiKeyEnvironmentVariable = serializedObject.FindProperty("_openAIApiKeyEnvironmentVariable");
            _openAIApiKey = serializedObject.FindProperty("_openAIApiKey");
            _systemInstructions = serializedObject.FindProperty("_systemInstructions");
            _enableLocalLiveLinkMcp = serializedObject.FindProperty("_enableLocalLiveLinkMcp");
            _autoStartLocalLiveLinkMcp = serializedObject.FindProperty("_autoStartLocalLiveLinkMcp");
            _localHttpTransportMode = serializedObject.FindProperty("_localHttpTransportMode");
            _localConnectionTimeoutSeconds = serializedObject.FindProperty("_localConnectionTimeoutSeconds");
            _allowSceneMutationTools = serializedObject.FindProperty("_allowSceneMutationTools");
            _enablePersistentChatHistory = serializedObject.FindProperty("_enablePersistentChatHistory");
            _chatHistoryConversationId = serializedObject.FindProperty("_chatHistoryConversationId");
            _chatHistoryStorageSubdirectory = serializedObject.FindProperty("_chatHistoryStorageSubdirectory");
            _maxPersistedMessages = serializedObject.FindProperty("_maxPersistedMessages");
            _maxHistoryFileSizeBytes = serializedObject.FindProperty("_maxHistoryFileSizeBytes");
            _externalMcpServers = serializedObject.FindProperty("_externalMcpServers");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawOpenAISection();
            EditorGUILayout.Space(8);
            DrawAgentBehaviorSection();
            EditorGUILayout.Space(8);
            DrawLocalLiveLinkSection();
            EditorGUILayout.Space(8);
            DrawChatHistoryPersistenceSection();
            EditorGUILayout.Space(8);
            DrawExternalServersSection();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawOpenAISection()
        {
            EditorGUILayout.LabelField("OpenAI Chat Backend", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_agentName);
            EditorGUILayout.PropertyField(_openAIModel);
            EditorGUILayout.PropertyField(_preferEnvironmentApiKey, new GUIContent("Prefer Environment API Key"));
            EditorGUILayout.PropertyField(_openAIApiKeyEnvironmentVariable, new GUIContent("API Key Environment Variable"));

            if (!_preferEnvironmentApiKey.boolValue)
            {
                EditorGUILayout.HelpBox("The embedded agent will use the API key stored in this asset.", MessageType.Warning);
            }

            EditorGUILayout.PropertyField(_openAIApiKey, new GUIContent("Fallback API Key"));
        }

        private void DrawAgentBehaviorSection()
        {
            EditorGUILayout.LabelField("Agent Behavior", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_systemInstructions);
            EditorGUILayout.PropertyField(_allowSceneMutationTools, new GUIContent("Allow Scene Mutation Tools"));
        }

        private void DrawLocalLiveLinkSection()
        {
            EditorGUILayout.LabelField("Local LiveLink MCP", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_enableLocalLiveLinkMcp, new GUIContent("Enable Local LiveLink MCP"));

            if (_enableLocalLiveLinkMcp.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "The built-in LiveLink MCP server is intended to be consumed through the /mcp endpoint using Streamable HTTP. " +
                    "Legacy SSE remains available only for compatibility with older external clients.",
                    MessageType.Info);

                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_autoStartLocalLiveLinkMcp, new GUIContent("Auto Start Local MCP"));
                EditorGUILayout.PropertyField(_localHttpTransportMode, new GUIContent("HTTP Transport Mode"));
                EditorGUILayout.PropertyField(_localConnectionTimeoutSeconds, new GUIContent("Connection Timeout (Seconds)"));
                EditorGUI.indentLevel--;

                if ((AgentMcpHttpTransportMode)_localHttpTransportMode.enumValueIndex == AgentMcpHttpTransportMode.Sse)
                {
                    EditorGUILayout.HelpBox(
                        "SSE is kept only for backward compatibility. StreamableHttp is the recommended mode for the built-in LiveLink MCP server.",
                        MessageType.Warning);
                }
            }
        }

        private void DrawChatHistoryPersistenceSection()
        {
            EditorGUILayout.LabelField("Chat History Persistence", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_enablePersistentChatHistory, new GUIContent("Enable Persistent Chat History"));

            if (!_enablePersistentChatHistory.boolValue)
            {
                EditorGUILayout.HelpBox(
                    "When disabled, chat history only lives in memory for the current play session.",
                    MessageType.Info);
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_chatHistoryConversationId, new GUIContent("Conversation ID"));
            EditorGUILayout.PropertyField(_chatHistoryStorageSubdirectory, new GUIContent("Storage Subdirectory"));
            EditorGUILayout.PropertyField(_maxPersistedMessages, new GUIContent("Max Persisted Messages"));
            EditorGUILayout.PropertyField(_maxHistoryFileSizeBytes, new GUIContent("Max File Size (Bytes)"));
            EditorGUI.indentLevel--;

            EditorGUILayout.HelpBox(
                "History is stored as local files under Application.persistentDataPath. " +
                "Conversation ID controls which history stream is resumed across restarts.",
                MessageType.None);
        }

        private void DrawExternalServersSection()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Downstream MCP Servers", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Add Server", GUILayout.Width(100)))
            {
                _externalMcpServers.arraySize++;
            }
            EditorGUILayout.EndHorizontal();

            if (_externalMcpServers.arraySize == 0)
            {
                EditorGUILayout.HelpBox("Add downstream MCP servers here. These servers are available only to the embedded agent and are not re-exposed through LiveLink MCP.", MessageType.Info);
                return;
            }

            for (int i = 0; i < _externalMcpServers.arraySize; i++)
            {
                SerializedProperty serverProperty = _externalMcpServers.GetArrayElementAtIndex(i);
                SerializedProperty displayName = serverProperty.FindPropertyRelative("_displayName");
                string title = string.IsNullOrWhiteSpace(displayName.stringValue)
                    ? string.Format("Server {0}", i + 1)
                    : displayName.stringValue;

                serverProperty.isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(serverProperty.isExpanded, title);
                if (serverProperty.isExpanded)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    DrawServer(serverProperty, i);
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
                EditorGUILayout.Space(4);
            }
        }

        private void DrawServer(SerializedProperty serverProperty, int index)
        {
            SerializedProperty enabledProp = serverProperty.FindPropertyRelative("_enabled");
            SerializedProperty displayNameProp = serverProperty.FindPropertyRelative("_displayName");
            SerializedProperty transportTypeProp = serverProperty.FindPropertyRelative("_transportType");
            SerializedProperty httpTransportModeProp = serverProperty.FindPropertyRelative("_httpTransportMode");
            SerializedProperty endpointProp = serverProperty.FindPropertyRelative("_endpoint");
            SerializedProperty timeoutProp = serverProperty.FindPropertyRelative("_connectionTimeoutSeconds");
            SerializedProperty headersProp = serverProperty.FindPropertyRelative("_headers");
            SerializedProperty commandProp = serverProperty.FindPropertyRelative("_command");
            SerializedProperty argumentsProp = serverProperty.FindPropertyRelative("_arguments");
            SerializedProperty workingDirectoryProp = serverProperty.FindPropertyRelative("_workingDirectory");
            SerializedProperty envProp = serverProperty.FindPropertyRelative("_environmentVariables");
            SerializedProperty useAllowListProp = serverProperty.FindPropertyRelative("_useToolAllowList");
            SerializedProperty allowedToolsProp = serverProperty.FindPropertyRelative("_allowedTools");

            EditorGUILayout.PropertyField(enabledProp);
            EditorGUILayout.PropertyField(displayNameProp);
            EditorGUILayout.PropertyField(transportTypeProp);
            EditorGUILayout.PropertyField(timeoutProp, new GUIContent("Connection Timeout (Seconds)"));

            if ((AgentMcpTransportType)transportTypeProp.enumValueIndex == AgentMcpTransportType.Http)
            {
                EditorGUILayout.PropertyField(endpointProp);
                EditorGUILayout.PropertyField(httpTransportModeProp, new GUIContent("HTTP Transport Mode"));
                EditorGUILayout.PropertyField(headersProp, new GUIContent("Headers"), true);
            }
            else
            {
                EditorGUILayout.HelpBox("Stdio MCP servers are intended for the Unity Editor and standalone desktop players.", MessageType.Info);
                EditorGUILayout.PropertyField(commandProp);
                EditorGUILayout.PropertyField(argumentsProp, new GUIContent("Arguments"), true);
                EditorGUILayout.PropertyField(workingDirectoryProp);
                EditorGUILayout.PropertyField(envProp, new GUIContent("Environment Variables"), true);
            }

            EditorGUILayout.PropertyField(useAllowListProp, new GUIContent("Use Tool Allow List"));
            if (useAllowListProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(allowedToolsProp, new GUIContent("Allowed Tools"), true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Test Connection"))
            {
                serializedObject.ApplyModifiedProperties();
                RunConnectionTest(index);
            }

            if (GUILayout.Button("Remove"))
            {
                _externalMcpServers.DeleteArrayElementAtIndex(index);
                serializedObject.ApplyModifiedProperties();
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void RunConnectionTest(int index)
        {
            if (index < 0 || index >= _externalMcpServers.arraySize)
            {
                return;
            }

            AgentRuntimeConfig config = (AgentRuntimeConfig)target;
            AgentExternalMcpServerConfig server = config.ExternalMcpServers[index];
            AgentMcpConnectionTestResult result = null;

            try
            {
                EditorUtility.DisplayProgressBar("Testing MCP Connection", "Connecting to configured server...", 0.5f);
                result = AgentMcpConnectionTester.TestConnectionAsync(server, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (result == null)
            {
                EditorUtility.DisplayDialog("MCP Connection Test", "The connection test did not return a result.", "OK");
                return;
            }

            if (!result.Success)
            {
                EditorUtility.DisplayDialog(
                    "MCP Connection Failed",
                    string.Format("{0}\n\n{1}", result.DisplayName, result.ErrorMessage),
                    "OK");
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine(string.Format("Connected to: {0}", result.ServerName));
            if (!string.IsNullOrEmpty(result.ServerVersion))
            {
                builder.AppendLine(string.Format("Version: {0}", result.ServerVersion));
            }
            builder.AppendLine();
            builder.AppendLine("Discovered tools:");
            for (int i = 0; i < result.ToolNames.Count; i++)
            {
                builder.Append("- ");
                builder.AppendLine(result.ToolNames[i]);
            }

            EditorUtility.DisplayDialog("MCP Connection Successful", builder.ToString(), "OK");
        }

        [MenuItem("LiveLink/Create Agent Runtime Config", false, 21)]
        private static void CreateAgentRuntimeConfigAsset()
        {
            const string defaultFileName = "LiveLinkAgentRuntimeConfig.asset";
            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(
                0,
                CreateInstance<CreateConfigAssetAction>(),
                defaultFileName,
                EditorGUIUtility.IconContent("ScriptableObject Icon").image as Texture2D,
                null);
        }
    }
}
