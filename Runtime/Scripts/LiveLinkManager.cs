using System;
using System.Collections.Generic;
// using System.Net.WebSockets;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LiveLink.MCP;
using LiveLink.Network;

namespace LiveLink
{
    /// <summary>
    /// Main manager component for Unity LiveLink.
    /// Handles WebSocket server, scene synchronization, and command processing.
    /// </summary>
    [AddComponentMenu("LiveLink/LiveLink Manager")]
    public class LiveLinkManager : MonoBehaviour
    {
        #region Inspector Fields

        [Header("Server Configuration")]
        [SerializeField]
        [Tooltip("Port number for the WebSocket server.")]
        private int _port = 8080;

        [SerializeField]
        [Tooltip("Automatically start the server when the component is enabled.")]
        private bool _autoStart = true;

        [Header("Sync Configuration")]
        [SerializeField]
        [Tooltip("Scope of scene objects to synchronize.")]
        private ScanScope _scope = ScanScope.WholeScene;

        [SerializeField]
        [Tooltip("Target root object when Scope is TargetObjectOnly.")]
        private Transform _targetRoot;

        [SerializeField]
        [Tooltip("Include inactive objects in synchronization.")]
        private bool _includeInactive = false;

        [SerializeField]
        [Tooltip("Sync frequency in Hz (times per second). 0 = manual only.")]
        [Range(0, 60)]
        private float _syncFrequency = 10f;

        [SerializeField]
        [Tooltip("Use delta sync to only send changed objects.")]
        private bool _useDeltaSync = true;

        [SerializeField]
        [Tooltip("Distance threshold for detecting position changes.")]
        private float _deltaThreshold = 0.001f;

        [Header("Spawning")]
        [SerializeField]
        [Tooltip("List of prefabs that can be spawned by external commands.")]
        private List<GameObject> _spawnablePrefabs = new List<GameObject>();

        [Header("Debug")]
        [SerializeField]
        [Tooltip("Log incoming and outgoing messages.")]
        private bool _debugLogging = false;

        #endregion

        #region Properties

        /// <summary>
        /// Gets whether the server is currently running.
        /// </summary>
        public bool IsServerRunning => _server?.IsRunning ?? false;

        /// <summary>
        /// Gets the number of connected clients.
        /// </summary>
        public int ClientCount => _server?.ClientCount ?? 0;

        /// <summary>
        /// Gets or sets the server port.
        /// </summary>
        public int Port
        {
            get => _port;
            set => _port = value;
        }

        /// <summary>
        /// Gets or sets the sync scope.
        /// </summary>
        public ScanScope Scope
        {
            get => _scope;
            set
            {
                _scope = value;
                if (_scanner != null) _scanner.Scope = value;
            }
        }

        /// <summary>
        /// Gets or sets the target root transform.
        /// </summary>
        public Transform TargetRoot
        {
            get => _targetRoot;
            set
            {
                _targetRoot = value;
                if (_scanner != null) _scanner.TargetRoot = value;
            }
        }

        #endregion

        #region Private Fields

        private LiveLinkServer _server;
        private SceneScanner _scanner;
        private Dictionary<string, GameObject> _prefabLookup;
        private McpResourceMapper _mcpResourceMapper;
        private List<McpTool> _mcpTools;
        private float _syncTimer = 0f;
        private float _cleanupTimer = 0f;
        private const float CLEANUP_INTERVAL = 1f;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            MainThreadDispatcher.Initialize();
            InitializePrefabLookup();
            InitializeScanner();
            InitializeMcp();
        }

        private void Start()
        {
            if (_autoStart)
            {
                StartServer();
            }
        }

        private void Update()
        {
            // Handle sync timer
            if (_syncFrequency > 0 && IsServerRunning && ClientCount > 0)
            {
                _syncTimer += Time.deltaTime;
                float syncInterval = 1f / _syncFrequency;

                if (_syncTimer >= syncInterval)
                {
                    _syncTimer = 0f;
                    SendSync();
                }
            }

            // Periodic cleanup
            _cleanupTimer += Time.deltaTime;
            if (_cleanupTimer >= CLEANUP_INTERVAL)
            {
                _cleanupTimer = 0f;
                _scanner?.CleanupDestroyedObjects();
            }
        }

        private void OnEnable()
        {
            if (_autoStart && _server == null)
            {
                StartServer();
            }
        }

        private void OnDisable()
        {
            StopServer();
        }

        private void OnDestroy()
        {
            StopServer();
        }

        #endregion

        #region Initialization

        private void InitializePrefabLookup()
        {
            _prefabLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var prefab in _spawnablePrefabs)
            {
                if (prefab != null)
                {
                    _prefabLookup[prefab.name] = prefab;
                }
            }
        }

        private void InitializeScanner()
        {
            _scanner = new SceneScanner
            {
                Scope = _scope,
                TargetRoot = _targetRoot,
                IncludeInactive = _includeInactive,
                DeltaThreshold = _deltaThreshold
            };
        }

                private void InitializeMcp()
                {
                        _mcpResourceMapper = new McpResourceMapper(_scanner);
                        _mcpTools = BuildMcpTools();
                }

                private List<McpTool> BuildMcpTools()
                {
                        return new List<McpTool>
                        {
                                new McpTool
                                {
                                        Name = "spawn_object",
                                        Description = "Instantiate a spawnable prefab in the active scene.",
                                        InputSchema = BuildSpawnSchema(),
                                        ReadOnly = false
                                },
                                new McpTool
                                {
                                        Name = "transform_object",
                                        Description = "Update position, rotation or scale of an object.",
                                        InputSchema = BuildTransformSchema(),
                                        ReadOnly = false
                                },
                                new McpTool
                                {
                                        Name = "delete_object",
                                        Description = "Delete an object by UUID.",
                                        InputSchema = BuildDeleteSchema(),
                                        ReadOnly = false
                                },
                                new McpTool
                                {
                                        Name = "set_object_parent",
                                        Description = "Re-parent an object to a new parent or to the scene root.",
                                        InputSchema = BuildSetParentSchema(),
                                        ReadOnly = false
                                },
                                new McpTool
                                {
                                        Name = "set_object_active",
                                        Description = "Toggle the active state of an object.",
                                        InputSchema = BuildSetActiveSchema(),
                                        ReadOnly = false
                                },
                                new McpTool
                                {
                                        Name = "rename_object",
                                        Description = "Rename an object.",
                                        InputSchema = BuildRenameSchema(),
                                        ReadOnly = false
                                }
                        };
                }

                private static JObject BuildSpawnSchema()
                {
                        return JObject.Parse(@"{
    \"type\": \"object\",
    \"properties\": {
        \"prefab_key\": { \"type\": \"string\", \"description\": \"Prefab name registered in LiveLinkManager\" },
        \"id\": { \"type\": \"string\", \"description\": \"Optional UUID override\" },
        \"position\": { \"type\": \"array\", \"items\": { \"type\": \"number\" }, \"minItems\": 3, \"maxItems\": 3 },
        \"rotation\": { \"type\": \"array\", \"items\": { \"type\": \"number\" }, \"minItems\": 4, \"maxItems\": 4 },
        \"scale\": { \"type\": \"array\", \"items\": { \"type\": \"number\" }, \"minItems\": 3, \"maxItems\": 3 },
        \"parent_uuid\": { \"type\": \"string\" },
        \"name\": { \"type\": \"string\" }
    },
    \"required\": [\"prefab_key\"]
}");
                }

                private static JObject BuildTransformSchema()
                {
                        return JObject.Parse(@"{
    \"type\": \"object\",
    \"properties\": {
        \"uuid\": { \"type\": \"string\" },
        \"position\": { \"type\": \"array\", \"items\": { \"type\": \"number\" }, \"minItems\": 3, \"maxItems\": 3 },
        \"rotation\": { \"type\": \"array\", \"items\": { \"type\": \"number\" }, \"minItems\": 4, \"maxItems\": 4 },
        \"scale\": { \"type\": \"array\", \"items\": { \"type\": \"number\" }, \"minItems\": 3, \"maxItems\": 3 },
        \"local\": { \"type\": \"boolean\", \"description\": \"Apply transform in local space\" }
    },
    \"required\": [\"uuid\"]
}");
                }

                private static JObject BuildDeleteSchema()
                {
                        return JObject.Parse(@"{
    \"type\": \"object\",
    \"properties\": {
        \"uuid\": { \"type\": \"string\" },
        \"include_children\": { \"type\": \"boolean\" }
    },
    \"required\": [\"uuid\"]
}");
                }

                private static JObject BuildSetParentSchema()
                {
                        return JObject.Parse(@"{
    \"type\": \"object\",
    \"properties\": {
        \"uuid\": { \"type\": \"string\" },
        \"parent_uuid\": { \"type\": \"string\", \"description\": \"Leave empty or null to unparent\" },
        \"world_position_stays\": { \"type\": \"boolean\" }
    },
    \"required\": [\"uuid\"]
}");
                }

                private static JObject BuildSetActiveSchema()
                {
                        return JObject.Parse(@"{
    \"type\": \"object\",
    \"properties\": {
        \"uuid\": { \"type\": \"string\" },
        \"active\": { \"type\": \"boolean\" }
    },
    \"required\": [\"uuid\", \"active\"]
}");
                }

                private static JObject BuildRenameSchema()
                {
                        return JObject.Parse(@"{
    \"type\": \"object\",
    \"properties\": {
        \"uuid\": { \"type\": \"string\" },
        \"name\": { \"type\": \"string\" }
    },
    \"required\": [\"uuid\", \"name\"]
}");
                }

        #endregion

        #region Server Control

        /// <summary>
        /// Starts the WebSocket server.
        /// </summary>
        public void StartServer()
        {
            if (_server != null && _server.IsRunning)
            {
                Debug.LogWarning("[LiveLink] Server is already running.");
                return;
            }

            _server = new LiveLinkServer();
            _server.OnMessageReceived += HandleMessage;
            _server.OnClientConnected += OnClientConnected;
            _server.OnClientDisconnected += OnClientDisconnected;
            _server.StartServer(_port);
        }

        /// <summary>
        /// Stops the WebSocket server.
        /// </summary>
        public void StopServer()
        {
            if (_server != null)
            {
                _server.OnMessageReceived -= HandleMessage;
                _server.OnClientConnected -= OnClientConnected;
                _server.OnClientDisconnected -= OnClientDisconnected;
                _server.Dispose();
                _server = null;
            }
        }

        /// <summary>
        /// Restarts the WebSocket server.
        /// </summary>
        public void RestartServer()
        {
            StopServer();
            StartServer();
        }

        #endregion

        #region Message Handling

        private void HandleMessage(string message, WebSocketConnection client)
        {
            if (_debugLogging)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.Log($"[LiveLink] Received: {message}");
                });
            }

            // Parse command on main thread to access Unity API
            MainThreadDispatcher.EnqueueSafe(() =>
            {
                ProcessCommand(message, client);
            }, "ProcessCommand");
        }

        private void ProcessCommand(string message, WebSocketConnection client)
        {
            if (TryHandleMcp(message, client))
            {
                return;
            }

            CommandPacket command = null;
            try
            {
                command = PacketSerializer.ParseCommand(message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink] Failed to parse command: {ex.Message}");
                SendResponse(client, ResponsePacket.Error($"Invalid JSON: {ex.Message}"));
                return;
            }

            if (command == null || string.IsNullOrEmpty(command.Type))
            {
                SendResponse(client, ResponsePacket.Error("Invalid command format"));
                return;
            }

            string requestId = command.RequestId;

            try
            {
                switch (command.Type.ToLowerInvariant())
                {
                    case "spawn":
                        HandleSpawn(command, client);
                        break;

                    case "transform":
                    case "move":
                        HandleTransform(command, client);
                        break;

                    case "delete":
                    case "destroy":
                        HandleDelete(command, client);
                        break;

                    case "rename":
                        HandleRename(command, client);
                        break;

                    case "set_parent":
                    case "reparent":
                        HandleSetParent(command, client);
                        break;

                    case "set_active":
                        HandleSetActive(command, client);
                        break;

                    case "scene_dump":
                    case "get_scene":
                    case "refresh":
                        HandleSceneDump(command, client);
                        break;

                    case "ping":
                        SendResponse(client, ResponsePacket.Ok("pong", requestId));
                        break;

                    default:
                        SendResponse(client, ResponsePacket.Error($"Unknown command type: {command.Type}", requestId));
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink] Error processing command '{command.Type}': {ex.Message}\n{ex.StackTrace}");
                SendResponse(client, ResponsePacket.Error($"Error: {ex.Message}", requestId));
            }
        }

        #endregion

        #region MCP Handling

        private bool TryHandleMcp(string message, WebSocketConnection client)
        {
            JObject raw;
            try
            {
                raw = JObject.Parse(message);
            }
            catch
            {
                return false; // Not MCP / JSON-RPC
            }

            string jsonrpc = raw.Value<string>("jsonrpc");
            if (!string.Equals(jsonrpc, McpJsonRpc.Version, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var request = raw.ToObject<JsonRpcRequest>();
            if (request == null || string.IsNullOrEmpty(request.Method))
            {
                SendJsonRpcResponse(client, McpJsonRpc.Error(null, -32600, "Invalid MCP request"));
                return true;
            }

            try
            {
                switch (request.Method)
                {
                    case "resources/list":
                        HandleMcpResourceList(request, client);
                        break;
                    case "resources/read":
                        HandleMcpResourceRead(request, client);
                        break;
                    case "tools/list":
                        HandleMcpToolsList(request, client);
                        break;
                    case "tools/call":
                        HandleMcpToolsCall(request, client);
                        break;
                    case "ping":
                        SendJsonRpcResponse(client, McpJsonRpc.Success(request.Id, new JObject { ["message"] = "pong" }));
                        break;
                    default:
                        SendJsonRpcResponse(client, McpJsonRpc.Error(request.Id, -32601, $"Unknown MCP method: {request.Method}"));
                        break;
                }
            }
            catch (Exception ex)
            {
                SendJsonRpcResponse(client, McpJsonRpc.Error(request.Id, -32603, ex.Message));
            }

            return true;
        }

        private void HandleMcpResourceList(JsonRpcRequest request, WebSocketConnection client)
        {
            var sceneDump = _scanner.ScanFullScene();
            var resources = _mcpResourceMapper.BuildResources(sceneDump);
            var result = new JObject { ["resources"] = JArray.FromObject(resources) };
            SendJsonRpcResponse(client, McpJsonRpc.Success(request.Id, result));
        }

        private void HandleMcpResourceRead(JsonRpcRequest request, WebSocketConnection client)
        {
            string uri = request.Params?.Value<string>("uri");
            if (string.IsNullOrEmpty(uri))
            {
                SendJsonRpcResponse(client, McpJsonRpc.Error(request.Id, -32602, "Missing resource uri"));
                return;
            }

            if (!_mcpResourceMapper.TryBuildContent(uri, out McpContent content))
            {
                SendJsonRpcResponse(client, McpJsonRpc.Error(request.Id, -32004, "Resource not found"));
                return;
            }

            var result = new JObject
            {
                ["contents"] = new JArray(JObject.FromObject(content))
            };

            SendJsonRpcResponse(client, McpJsonRpc.Success(request.Id, result));
        }

        private void HandleMcpToolsList(JsonRpcRequest request, WebSocketConnection client)
        {
            var result = new JObject { ["tools"] = JArray.FromObject(_mcpTools) };
            SendJsonRpcResponse(client, McpJsonRpc.Success(request.Id, result));
        }

        private void HandleMcpToolsCall(JsonRpcRequest request, WebSocketConnection client)
        {
            string toolName = request.Params?.Value<string>("name");
            var arguments = request.Params != null ? request.Params["arguments"] as JObject : null;
            arguments ??= new JObject();

            if (string.IsNullOrEmpty(toolName))
            {
                SendJsonRpcResponse(client, McpJsonRpc.Error(request.Id, -32602, "Missing tool name"));
                return;
            }

            if (!ExecuteMcpTool(toolName, arguments, out JObject toolResult, out string errorMessage, out int errorCode))
            {
                SendJsonRpcResponse(client, McpJsonRpc.Error(request.Id, errorCode, errorMessage ?? "Tool execution failed"));
                return;
            }

            SendJsonRpcResponse(client, McpJsonRpc.Success(request.Id, toolResult ?? new JObject()));
        }

        private bool ExecuteMcpTool(string toolName, JObject arguments, out JObject result, out string errorMessage, out int errorCode)
        {
            result = null;
            errorMessage = null;
            errorCode = -32002;

            switch (toolName.ToLowerInvariant())
            {
                case "spawn_object":
                    var spawnPayload = arguments?.ToObject<SpawnPayload>();
                    if (!TrySpawn(spawnPayload, out string spawnedUuid, out GameObject spawned, out string spawnError))
                    {
                        errorMessage = spawnError;
                        errorCode = -32602;
                        return false;
                    }

                    result = new JObject
                    {
                        ["uuid"] = spawnedUuid,
                        ["name"] = spawned?.name
                    };
                    return true;

                case "transform_object":
                    var transformPayload = arguments?.ToObject<TransformPayload>();
                    if (!TryTransform(transformPayload, out string transformError))
                    {
                        errorMessage = transformError;
                        errorCode = -32602;
                        return false;
                    }

                    result = new JObject { ["uuid"] = transformPayload?.UUID };
                    return true;

                case "delete_object":
                    var deletePayload = arguments?.ToObject<DeletePayload>();
                    if (!TryDelete(deletePayload, true, out string deleteError))
                    {
                        errorMessage = deleteError;
                        errorCode = -32602;
                        return false;
                    }

                    result = new JObject { ["uuid"] = deletePayload?.UUID };
                    return true;

                case "set_object_parent":
                    var setParentPayload = arguments?.ToObject<SetParentPayload>();
                    if (!TrySetParent(setParentPayload, out string parentError))
                    {
                        errorMessage = parentError;
                        errorCode = -32602;
                        return false;
                    }

                    result = new JObject
                    {
                        ["uuid"] = setParentPayload?.UUID,
                        ["parent_uuid"] = setParentPayload?.ParentUUID
                    };
                    return true;

                case "set_object_active":
                    var setActivePayload = arguments?.ToObject<SetActivePayload>();
                    if (!TrySetActive(setActivePayload, out string activeError))
                    {
                        errorMessage = activeError;
                        errorCode = -32602;
                        return false;
                    }

                    result = new JObject
                    {
                        ["uuid"] = setActivePayload?.UUID,
                        ["active"] = setActivePayload?.Active ?? false
                    };
                    return true;

                case "rename_object":
                    var renamePayload = arguments?.ToObject<RenamePayload>();
                    if (!TryRename(renamePayload, out string renameError))
                    {
                        errorMessage = renameError;
                        errorCode = -32602;
                        return false;
                    }

                    result = new JObject
                    {
                        ["uuid"] = renamePayload?.UUID,
                        ["name"] = renamePayload?.Name
                    };
                    return true;

                default:
                    errorMessage = $"Unknown tool: {toolName}";
                    errorCode = -32601;
                    return false;
            }
        }

        private void SendJsonRpcResponse(WebSocketConnection client, JsonRpcResponse response)
        {
            if (client == null || response == null) return;
            string json = JsonConvert.SerializeObject(response, Formatting.None);
            SendToClient(client, json);
        }

        #endregion

        #region Command Handlers

        private bool TrySpawn(SpawnPayload payload, out string uuid, out GameObject spawned, out string errorMessage)
        {
            uuid = null;
            spawned = null;
            errorMessage = null;

            if (payload == null || string.IsNullOrEmpty(payload.PrefabKey))
            {
                errorMessage = "Missing prefab_key";
                return false;
            }

            if (!_prefabLookup.TryGetValue(payload.PrefabKey, out GameObject prefab))
            {
                errorMessage = $"Prefab not found: {payload.PrefabKey}";
                return false;
            }

            Vector3 position = Vector3.zero;
            if (payload.Position != null && payload.Position.Length >= 3)
            {
                position = new Vector3(payload.Position[0], payload.Position[1], payload.Position[2]);
            }

            Quaternion rotation = Quaternion.identity;
            if (payload.Rotation != null && payload.Rotation.Length >= 4)
            {
                rotation = new Quaternion(payload.Rotation[0], payload.Rotation[1], payload.Rotation[2], payload.Rotation[3]);
            }

            spawned = Instantiate(prefab, position, rotation);

            if (!string.IsNullOrEmpty(payload.Name))
            {
                spawned.name = payload.Name;
            }

            if (!string.IsNullOrEmpty(payload.ParentUUID))
            {
                var parent = _scanner.GetGameObjectByUUID(payload.ParentUUID);
                if (parent != null)
                {
                    spawned.transform.SetParent(parent.transform, true);
                }
            }

            uuid = !string.IsNullOrEmpty(payload.Id) ? payload.Id : Guid.NewGuid().ToString("N").Substring(0, 12);
            _scanner.RegisterWithUUID(spawned, uuid);

            if (payload.Scale != null && payload.Scale.Length >= 3)
            {
                spawned.transform.localScale = new Vector3(payload.Scale[0], payload.Scale[1], payload.Scale[2]);
            }

            Debug.Log($"[LiveLink] Spawned '{prefab.name}' with UUID: {uuid}");

            var notification = new ObjectSpawnedPacket
            {
                UUID = uuid,
                Prefab = payload.PrefabKey,
                Object = CreateSceneObjectDTO(spawned, uuid)
            };
            Broadcast(PacketSerializer.Serialize(notification));

            return true;
        }

        private bool TryTransform(TransformPayload payload, out string errorMessage)
        {
            errorMessage = null;

            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                errorMessage = "Missing uuid";
                return false;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                errorMessage = $"Object not found: {payload.UUID}";
                return false;
            }

            Transform transform = obj.transform;

            if (payload.Position != null && payload.Position.Length >= 3)
            {
                Vector3 newPos = new Vector3(payload.Position[0], payload.Position[1], payload.Position[2]);
                if (payload.UseLocalSpace)
                    transform.localPosition = newPos;
                else
                    transform.position = newPos;
            }

            if (payload.Rotation != null && payload.Rotation.Length >= 4)
            {
                Quaternion newRot = new Quaternion(payload.Rotation[0], payload.Rotation[1], payload.Rotation[2], payload.Rotation[3]);
                if (payload.UseLocalSpace)
                    transform.localRotation = newRot;
                else
                    transform.rotation = newRot;
            }

            if (payload.Scale != null && payload.Scale.Length >= 3)
            {
                transform.localScale = new Vector3(payload.Scale[0], payload.Scale[1], payload.Scale[2]);
            }

            return true;
        }

        private bool TryDelete(DeletePayload payload, bool broadcast, out string errorMessage)
        {
            errorMessage = null;

            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                errorMessage = "Missing uuid";
                return false;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                errorMessage = $"Object not found: {payload.UUID}";
                return false;
            }

            _scanner.Unregister(obj);
            Destroy(obj);

            Debug.Log($"[LiveLink] Deleted object with UUID: {payload.UUID}");

            if (broadcast)
            {
                var notification = new ObjectDestroyedPacket { UUID = payload.UUID };
                Broadcast(PacketSerializer.Serialize(notification));
            }

            return true;
        }

        private bool TryRename(RenamePayload payload, out string errorMessage)
        {
            errorMessage = null;

            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                errorMessage = "Missing uuid";
                return false;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                errorMessage = $"Object not found: {payload.UUID}";
                return false;
            }

            if (string.IsNullOrEmpty(payload.Name))
            {
                errorMessage = "Missing name";
                return false;
            }

            obj.name = payload.Name;
            return true;
        }

        private bool TrySetParent(SetParentPayload payload, out string errorMessage)
        {
            errorMessage = null;

            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                errorMessage = "Missing uuid";
                return false;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                errorMessage = $"Object not found: {payload.UUID}";
                return false;
            }

            Transform newParent = null;
            if (!string.IsNullOrEmpty(payload.ParentUUID))
            {
                var parentObj = _scanner.GetGameObjectByUUID(payload.ParentUUID);
                if (parentObj == null)
                {
                    errorMessage = $"Parent not found: {payload.ParentUUID}";
                    return false;
                }
                newParent = parentObj.transform;
            }

            obj.transform.SetParent(newParent, payload.WorldPositionStays);
            return true;
        }

        private bool TrySetActive(SetActivePayload payload, out string errorMessage)
        {
            errorMessage = null;

            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                errorMessage = "Missing uuid";
                return false;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                errorMessage = $"Object not found: {payload.UUID}";
                return false;
            }

            obj.SetActive(payload.Active);
            return true;
        }

        private void HandleSpawn(CommandPacket command, WebSocketConnection client)
        {
            var payload = command.GetPayload<SpawnPayload>();
            if (!TrySpawn(payload, out string uuid, out GameObject spawned, out string errorMessage))
            {
                SendResponse(client, ResponsePacket.Error(errorMessage ?? "Spawn failed", command.RequestId));
                return;
            }

            var responseData = new JObject
            {
                ["uuid"] = uuid,
                ["name"] = spawned?.name
            };
            SendResponse(client, ResponsePacket.Ok("Object spawned", command.RequestId, responseData));
        }

        private void HandleTransform(CommandPacket command, WebSocketConnection client)
        {
            var payload = command.GetPayload<TransformPayload>();
            if (!TryTransform(payload, out string errorMessage))
            {
                SendResponse(client, ResponsePacket.Error(errorMessage ?? "Transform failed", command.RequestId));
                return;
            }

            SendResponse(client, ResponsePacket.Ok("Transform updated", command.RequestId));
        }

        private void HandleDelete(CommandPacket command, WebSocketConnection client)
        {
            var payload = command.GetPayload<DeletePayload>();
            if (!TryDelete(payload, true, out string errorMessage))
            {
                SendResponse(client, ResponsePacket.Error(errorMessage ?? "Delete failed", command.RequestId));
                return;
            }

            SendResponse(client, ResponsePacket.Ok("Object deleted", command.RequestId));
        }

        private void HandleRename(CommandPacket command, WebSocketConnection client)
        {
            var payload = command.GetPayload<RenamePayload>();
            if (!TryRename(payload, out string errorMessage))
            {
                SendResponse(client, ResponsePacket.Error(errorMessage ?? "Rename failed", command.RequestId));
                return;
            }

            SendResponse(client, ResponsePacket.Ok("Object renamed", command.RequestId));
        }

        private void HandleSetParent(CommandPacket command, WebSocketConnection client)
        {
            var payload = command.GetPayload<SetParentPayload>();
            if (!TrySetParent(payload, out string errorMessage))
            {
                SendResponse(client, ResponsePacket.Error(errorMessage ?? "Set parent failed", command.RequestId));
                return;
            }

            SendResponse(client, ResponsePacket.Ok("Parent changed", command.RequestId));
        }

        private void HandleSetActive(CommandPacket command, WebSocketConnection client)
        {
            var payload = command.GetPayload<SetActivePayload>();
            if (!TrySetActive(payload, out string errorMessage))
            {
                SendResponse(client, ResponsePacket.Error(errorMessage ?? "Set active failed", command.RequestId));
                return;
            }

            SendResponse(client, ResponsePacket.Ok($"Object {(payload.Active ? "activated" : "deactivated")}", command.RequestId));
        }

        private void HandleSceneDump(CommandPacket command, WebSocketConnection client)
        {
            var payload = command.GetPayload<RequestSceneDumpPayload>();
            bool includeInactive = payload?.IncludeInactive ?? _includeInactive;

            _scanner.IncludeInactive = includeInactive;
            var sceneDump = _scanner.ScanFullScene();

            string json = PacketSerializer.Serialize(sceneDump);
            SendToClient(client, json);

            Debug.Log($"[LiveLink] Sent scene dump: {sceneDump.Payload.ObjectCount} objects");
        }

        #endregion

        #region Sync

        /// <summary>
        /// Sends a sync update to all connected clients.
        /// </summary>
        public void SendSync()
        {
            if (!IsServerRunning || ClientCount == 0) return;

            string json;
            if (_useDeltaSync)
            {
                var syncPacket = _scanner.ScanDelta();
                if (syncPacket.Objects.Count == 0) return; // No changes
                json = PacketSerializer.Serialize(syncPacket);
            }
            else
            {
                var sceneDump = _scanner.ScanFullScene();
                json = PacketSerializer.Serialize(sceneDump);
            }

            if (_debugLogging)
            {
                Debug.Log($"[LiveLink] Sending sync: {json.Length} bytes");
            }

            Broadcast(json);
        }

        /// <summary>
        /// Forces a full scene sync to all clients.
        /// </summary>
        public void ForceFullSync()
        {
            if (!IsServerRunning || ClientCount == 0) return;

            var sceneDump = _scanner.ScanFullScene();
            string json = PacketSerializer.Serialize(sceneDump);
            Broadcast(json);

            Debug.Log($"[LiveLink] Force sync: {sceneDump.Payload.ObjectCount} objects");
        }

        #endregion

        #region Helpers

        private void Broadcast(string message)
        {
            if (_debugLogging)
            {
                Debug.Log($"[LiveLink] Broadcasting: {message.Substring(0, Math.Min(100, message.Length))}...");
            }
            _server?.Broadcast(message);
        }

        private void SendToClient(WebSocketConnection client, string message)
        {
            if (_debugLogging)
            {
                Debug.Log($"[LiveLink] Sending: {message.Substring(0, Math.Min(100, message.Length))}...");
            }
            _ = _server?.SendAsync(client, message);
        }

        private void SendResponse(WebSocketConnection client, ResponsePacket response)
        {
            string json = PacketSerializer.Serialize(response);
            SendToClient(client, json);
        }

        private void OnClientConnected(WebSocketConnection client)
        {
            MainThreadDispatcher.EnqueueSafe(() =>
            {
                // Send initial scene dump to new client
                var sceneDump = _scanner.ScanFullScene();
                string json = PacketSerializer.Serialize(sceneDump);
                _ = _server?.SendAsync(client, json);
            }, "OnClientConnected");
        }

        private void OnClientDisconnected(WebSocketConnection client)
        {
            // Clean up if needed
        }

        private SceneObjectDTO CreateSceneObjectDTO(GameObject obj, string uuid)
        {
            string parentUuid = null;
            if (obj.transform.parent != null)
            {
                parentUuid = _scanner.GetOrCreateUUID(obj.transform.parent.gameObject);
            }

            return new SceneObjectDTO
            {
                UUID = uuid,
                ParentUUID = parentUuid,
                Name = obj.name,
                Active = obj.activeSelf,
                Layer = obj.layer,
                Tag = obj.tag,
                Transform = new TransformDTO
                {
                    Position = new float[] { obj.transform.position.x, obj.transform.position.y, obj.transform.position.z },
                    Rotation = new float[] { obj.transform.rotation.x, obj.transform.rotation.y, obj.transform.rotation.z, obj.transform.rotation.w },
                    Scale = new float[] { obj.transform.localScale.x, obj.transform.localScale.y, obj.transform.localScale.z }
                }
            };
        }

        #endregion

        #region Editor Support

        /// <summary>
        /// Refreshes the prefab lookup dictionary.
        /// </summary>
        public void RefreshPrefabLookup()
        {
            InitializePrefabLookup();
        }

        /// <summary>
        /// Gets a list of registered prefab names.
        /// </summary>
        public List<string> GetRegisteredPrefabNames()
        {
            if (_prefabLookup == null)
            {
                return new List<string>();
            }
            return new List<string>(_prefabLookup.Keys);
        }

        #endregion
    }
}
