using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using UnityEditor;
using UnityEngine;

namespace LiveLink.Agent.Editor
{
    /// <summary>
    /// Play Mode chat window for testing the EmbeddedAgentRuntime interactively.
    /// Shows structured responses including tool calls, tool results, usage, and finish reason.
    /// </summary>
    public class EmbeddedAgentChatWindow : EditorWindow
    {
        // ───────────────────── Data Model ─────────────────────

        private enum ChatRole { User, Agent, ToolCall, ToolResult, Error, System }

        private sealed class ChatEntry
        {
            public ChatRole Role;
            public string Text;
            public string ToolName;
            public string ToolArgs;
            public string ToolCallId;
            public string ToolResult;
            public string FinishReason;
            public int? InputTokens;
            public int? OutputTokens;
            public TimeSpan? Duration;
            public bool Expanded;       // for tool call/result folding
            public bool IsStreaming;    // chunked text still arriving
        }

        // ───────────────────── State ─────────────────────

        private EmbeddedAgentRuntime _runtime;
        private Vector2 _scrollPos;
        private string _inputText = "";
        private bool _autoScroll = true;
        private bool _showUsage = true;
        private readonly List<ChatEntry> _entries = new List<ChatEntry>();
        private CancellationTokenSource _streamCts;

        // Reusable styles (lazy init)
        private GUIStyle _userStyle;
        private GUIStyle _agentStyle;
        private GUIStyle _toolCallStyle;
        private GUIStyle _toolResultStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _systemStyle;
        private GUIStyle _inputStyle;
        private bool _stylesInitialized;

        [MenuItem("LiveLink/Agent Chat", false, 30)]
        public static void ShowWindow()
        {
            var window = GetWindow<EmbeddedAgentChatWindow>("LiveLink Agent Chat");
            window.minSize = new Vector2(420, 300);
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            _streamCts?.Cancel();
            _streamCts?.Dispose();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _streamCts?.Cancel();
                _entries.Clear();
            }
            Repaint();
        }

        // ───────────────────── GUI ─────────────────────

        private void OnGUI()
        {
            InitStyles();

            DrawToolbar();
            EditorGUILayout.Space(2);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to chat with the embedded agent.", MessageType.Info);
                return;
            }

            if (_runtime == null)
            {
                _runtime = FindFirstObjectByType<EmbeddedAgentRuntime>();
                if (_runtime == null)
                {
                    EditorGUILayout.HelpBox("No EmbeddedAgentRuntime found in the scene.", MessageType.Warning);
                    return;
                }
            }

            DrawStatusBar();
            EditorGUILayout.Space(2);
            DrawChatArea();
            DrawInputArea();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(44)))
            {
                _entries.Clear();
            }

            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto-scroll", EditorStyles.toolbarButton, GUILayout.Width(70));
            _showUsage = GUILayout.Toggle(_showUsage, "Usage", EditorStyles.toolbarButton, GUILayout.Width(50));

            GUILayout.FlexibleSpace();

            if (_runtime != null)
            {
                GUILayout.Label(_runtime.IsInitialized ? "● Ready" : "○ Not initialized", EditorStyles.toolbarButton);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatusBar()
        {
            if (_runtime == null) return;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            GUILayout.Label(string.Format("Status: {0}", _runtime.Status ?? "Idle"), GUILayout.ExpandWidth(false));

            if (_runtime.IsBusy)
            {
                var rect = GUILayoutUtility.GetRect(16, 16, GUILayout.ExpandWidth(false));
                DrawSpinner(rect);
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label(string.Format("Tools: {0}", _runtime.AvailableToolNames?.Count ?? 0), GUILayout.ExpandWidth(false));
            GUILayout.Label(string.Format("Servers: {0}", _runtime.ConnectedServerCount), GUILayout.ExpandWidth(false));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawChatArea()
        {
            // Reserve remaining height for chat area
            float inputHeight = 60f;
            float chatHeight = position.height - 140f; // toolbar + status + input
            chatHeight = Mathf.Max(chatHeight, 100f);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(chatHeight));

            if (_entries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Type a message below to start chatting.\n\n" +
                    "The agent can use MCP tools to inspect and modify the scene. " +
                    "Tool calls and results are shown inline.",
                    MessageType.Info);
            }

            foreach (var entry in _entries)
            {
                DrawEntry(entry);
            }

            if (_autoScroll && Event.current.type == EventType.Repaint)
            {
                // Scroll to bottom after layout
                _scrollPos.y = float.MaxValue;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawEntry(ChatEntry entry)
        {
            switch (entry.Role)
            {
                case ChatRole.User:
                    DrawUserMessage(entry);
                    break;
                case ChatRole.Agent:
                    DrawAgentMessage(entry);
                    break;
                case ChatRole.ToolCall:
                    DrawToolCall(entry);
                    break;
                case ChatRole.ToolResult:
                    DrawToolResult(entry);
                    break;
                case ChatRole.Error:
                    DrawErrorMessage(entry);
                    break;
                case ChatRole.System:
                    DrawSystemMessage(entry);
                    break;
            }
        }

        private void DrawUserMessage(ChatEntry entry)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginVertical(GUILayout.MaxWidth(position.width * 0.7f));
            EditorGUILayout.LabelField("You", _systemStyle);
            EditorGUILayout.TextArea(entry.Text, _userStyle);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void DrawAgentMessage(ChatEntry entry)
        {
            EditorGUILayout.BeginVertical(GUILayout.MaxWidth(position.width * 0.85f));
            EditorGUILayout.LabelField("Agent", _systemStyle);

            string displayText = entry.Text;
            if (entry.IsStreaming)
                displayText += " ▌";

            EditorGUILayout.TextArea(displayText, _agentStyle);

            // Show usage/finish info if enabled
            if (_showUsage && (entry.InputTokens.HasValue || entry.FinishReason != null))
            {
                EditorGUILayout.BeginHorizontal();
                if (entry.InputTokens.HasValue)
                {
                    GUILayout.Label(
                        string.Format("↑{0} ↓{1}", entry.InputTokens.Value, entry.OutputTokens ?? 0),
                        EditorStyles.miniLabel);
                }
                if (entry.Duration.HasValue)
                {
                    GUILayout.Label(string.Format("{0:F1}s", entry.Duration.Value.TotalSeconds), EditorStyles.miniLabel);
                }
                if (entry.FinishReason != null)
                {
                    GUILayout.Label(string.Format("[{0}]", entry.FinishReason), EditorStyles.miniLabel);
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        private void DrawToolCall(ChatEntry entry)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            entry.Expanded = EditorGUILayout.Foldout(entry.Expanded,
                string.Format("🔧 Tool Call: {0}", entry.ToolName), true);

            if (entry.Expanded)
            {
                EditorGUI.indentLevel++;
                if (!string.IsNullOrEmpty(entry.ToolArgs))
                {
                    EditorGUILayout.LabelField("Arguments:", EditorStyles.miniLabel);
                    EditorGUILayout.TextArea(FormatJson(entry.ToolArgs), EditorStyles.miniLabel);
                }
                if (!string.IsNullOrEmpty(entry.ToolCallId))
                {
                    EditorGUILayout.LabelField(string.Format("Call ID: {0}", entry.ToolCallId), EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawToolResult(ChatEntry entry)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            entry.Expanded = EditorGUILayout.Foldout(entry.Expanded,
                string.Format("📋 Result: {0}", entry.ToolName), true);

            if (entry.Expanded)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.TextArea(entry.ToolResult ?? "(empty)", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawErrorMessage(ChatEntry entry)
        {
            EditorGUILayout.HelpBox(entry.Text, MessageType.Error);
        }

        private void DrawSystemMessage(ChatEntry entry)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(entry.Text, EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawInputArea()
        {
            EditorGUILayout.Space(2);

            bool isRunning = _runtime != null && _runtime.IsBusy;
            bool canSend = Application.isPlaying && !isRunning && !string.IsNullOrWhiteSpace(_inputText);

            EditorGUILayout.BeginHorizontal();

            // Text field — submit on Enter, newline on Shift+Enter
            GUI.SetNextControlName("ChatInput");

            bool enterPressed = Event.current.type == EventType.KeyDown
                && Event.current.keyCode == KeyCode.Return
                && !Event.current.shift;

            string newInput = EditorGUILayout.TextArea(_inputText, GUILayout.MinHeight(38), GUILayout.ExpandHeight(true));

            if (newInput != _inputText)
            {
                _inputText = newInput;
            }

            if (enterPressed && canSend)
            {
                GUI.FocusControl(null);
                SendMessage(_inputText);
                _inputText = "";
                Event.current.Use();
            }

            EditorGUILayout.BeginVertical(GUILayout.Width(60));

            using (new EditorGUI.DisabledScope(!canSend))
            {
                if (GUILayout.Button("Send", GUILayout.Height(20)))
                {
                    SendMessage(_inputText);
                    _inputText = "";
                    GUI.FocusControl(null);
                }
            }

            if (isRunning && GUILayout.Button("Stop", GUILayout.Height(18)))
            {
                _streamCts?.Cancel();
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // Hint
            GUILayout.Label("Enter to send · Shift+Enter for newline", EditorStyles.miniLabel);
        }

        // ───────────────────── Message Sending ─────────────────────

        private async void SendMessage(string text)
        {
            if (_runtime == null || string.IsNullOrWhiteSpace(text)) return;

            // Add user message
            _entries.Add(new ChatEntry { Role = ChatRole.User, Text = text.Trim() });
            _entries.Add(new ChatEntry { Role = ChatRole.System, Text = "Agent is thinking..." });
            Repaint();

            // Use streaming for rich tool call visibility
            _streamCts = new CancellationTokenSource();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Remove "thinking" placeholder
            int thinkingIndex = _entries.Count - 1;

            try
            {
                var agentEntry = new ChatEntry { Role = ChatRole.Agent, Text = "", IsStreaming = true };
                _entries[thinkingIndex] = agentEntry;

                int inputTokens = 0;
                int outputTokens = 0;
                string finishReason = null;

                await foreach (AgentResponseUpdate update in _runtime.RunStreamingAsync(text.Trim(), _streamCts.Token))
                {
                    // Text content
                    if (update.Text != null)
                    {
                        agentEntry.Text += update.Text;
                    }

                    // Content items
                    if (update.Contents != null)
                    {
                        foreach (AIContent content in update.Contents)
                        {
                            switch (content)
                            {
                                case FunctionCallContent fcc:
                                    // Insert tool call before the current agent entry
                                    int insertAt = _entries.IndexOf(agentEntry);
                                    _entries.Insert(insertAt, new ChatEntry
                                    {
                                        Role = ChatRole.ToolCall,
                                        ToolName = fcc.Name ?? "(unknown)",
                                        ToolArgs = fcc.Arguments != null ? SerializeArguments(fcc.Arguments) : null,
                                        ToolCallId = fcc.CallId,
                                        Expanded = false
                                    });
                                    break;

                                case FunctionResultContent frc:
                                    string resultName = "(result)";
                                    // Try to find matching tool call for the name
                                    for (int i = _entries.Count - 1; i >= 0; i--)
                                    {
                                        if (_entries[i].Role == ChatRole.ToolCall && _entries[i].ToolCallId == frc.CallId)
                                        {
                                            resultName = _entries[i].ToolName;
                                            break;
                                        }
                                    }
                                    int insertResultAt = _entries.IndexOf(agentEntry);
                                    _entries.Insert(insertResultAt, new ChatEntry
                                    {
                                        Role = ChatRole.ToolResult,
                                        ToolName = resultName,
                                        ToolResult = frc.Result?.ToString() ?? "(null)",
                                        ToolCallId = frc.CallId,
                                        Expanded = false
                                    });
                                    break;

                                case UsageContent uc:
                                    inputTokens = (int)(uc.Details?.InputTokenCount ?? 0);
                                    outputTokens = (int)(uc.Details?.OutputTokenCount ?? 0);
                                    break;
                            }
                        }
                    }

                    if (update.Contents != null)
                    {
                        foreach (AIContent content in update.Contents)
                        {
                            if (content is UsageContent uc)
                            {
                                inputTokens = (int)(uc.Details?.InputTokenCount ?? 0);
                                outputTokens = (int)(uc.Details?.OutputTokenCount ?? 0);
                            }
                        }
                    }

                    if (update.FinishReason.HasValue)
                    {
                        finishReason = update.FinishReason.Value.ToString();
                    }

                    Repaint();
                }

                stopwatch.Stop();
                agentEntry.IsStreaming = false;
                agentEntry.InputTokens = inputTokens;
                agentEntry.OutputTokens = outputTokens;
                agentEntry.FinishReason = finishReason;
                agentEntry.Duration = stopwatch.Elapsed;

                if (string.IsNullOrWhiteSpace(agentEntry.Text))
                {
                    agentEntry.Text = "(no text response)";
                }
            }
            catch (OperationCanceledException)
            {
                _entries[thinkingIndex] = new ChatEntry
                {
                    Role = ChatRole.System,
                    Text = "Cancelled by user."
                };
            }
            catch (Exception ex)
            {
                _entries[thinkingIndex] = new ChatEntry
                {
                    Role = ChatRole.Error,
                    Text = string.Format("{0}: {1}", ex.GetType().Name, ex.Message)
                };
            }
            finally
            {
                _streamCts?.Dispose();
                _streamCts = null;
                Repaint();
            }
        }

        // ───────────────────── Helpers ─────────────────────

        private void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _userStyle = new GUIStyle(EditorStyles.helpBox)
            {
                wordWrap = true,
                padding = new RectOffset(8, 8, 6, 6),
                normal = { background = MakeTex(1, 1, new Color(0.2f, 0.4f, 0.7f, 0.25f)) }
            };

            _agentStyle = new GUIStyle(EditorStyles.helpBox)
            {
                wordWrap = true,
                padding = new RectOffset(8, 8, 6, 6),
                normal = { background = MakeTex(1, 1, new Color(0.3f, 0.3f, 0.3f, 0.2f)) }
            };

            _toolCallStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontStyle = FontStyle.Italic,
                fontSize = 10
            };

            _toolResultStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 10
            };

            _errorStyle = new GUIStyle(EditorStyles.helpBox)
            {
                normal = { background = MakeTex(1, 1, new Color(0.7f, 0.2f, 0.2f, 0.2f)) }
            };

            _systemStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Italic
            };

            _inputStyle = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true
            };
        }

        private static string FormatJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            try
            {
                var obj = UnityEngine.JsonUtility.FromJson<object>(json);
                return json; // Unity's JsonUtility is limited; just return as-is
            }
            catch
            {
                return json;
            }
        }

        private static string SerializeArguments(IEnumerable<KeyValuePair<string, object>> arguments)
        {
            if (arguments == null) return "{}";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in arguments)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.AppendFormat("\"{0}\": {1}", kv.Key,
                    kv.Value is string s ? string.Format("\"{0}\"", s) : kv.Value ?? "null");
            }
            sb.Append("}");
            return sb.ToString();
        }

        private void DrawSpinner(Rect rect)
        {
            // Simple animated spinner
            float angle = (float)(EditorApplication.timeSinceStartup * 180 % 360);
            Handles.BeginGUI();
            Handles.color = GUI.color;
            Vector3 center = new Vector3(rect.center.x, rect.center.y, 0);
            Handles.DrawSolidArc(center, Vector3.forward, Quaternion.Euler(0, 0, -angle) * Vector3.right, 270, 5);
            Handles.EndGUI();
            Repaint(); // keep animating
        }

        private static Texture2D MakeTex(int width, int height, Color color)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            var tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
