using System;
using System.Collections.Generic;
// using System.Net.WebSockets;
using UnityEngine;
using Newtonsoft.Json.Linq;
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
        [Tooltip("Port number for the MCP HTTP server.")]
        private int _mcpPort = 8081;

        [SerializeField]
        [Tooltip("Enable MCP HTTP server (HTTP + SSE transport).")]
        private bool _enableMCPServer = true;

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

        /// <summary>
        /// Gets the scene scanner.
        /// </summary>
        public SceneScanner Scanner => _scanner;

        #endregion

        #region Private Fields

        private LiveLinkServer _server;
        private MCPHttpServer _mcpHttpServer;
        private SceneScanner _scanner;
        private MCPToolHandler _mcpHandler;
        private Dictionary<string, GameObject> _prefabLookup;
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
            _mcpHandler = new MCPToolHandler(this);
        }

        private void Start()
        {
            if (_autoStart)
            {
                StartServer();
                if (_enableMCPServer)
                {
                    StartMCPServer();
                }
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
            StopMCPServer();
        }

        private void OnDestroy()
        {
            StopServer();
            StopMCPServer();
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

        /// <summary>
        /// Starts the MCP HTTP server.
        /// </summary>
        public void StartMCPServer()
        {
            if (_mcpHttpServer != null && _mcpHttpServer.IsRunning)
            {
                Debug.LogWarning("[LiveLink-MCP] HTTP server is already running.");
                return;
            }

            _mcpHttpServer = new MCPHttpServer(_mcpHandler, _mcpPort);
            _mcpHttpServer.Start();
        }

        /// <summary>
        /// Stops the MCP HTTP server.
        /// </summary>
        public void StopMCPServer()
        {
            if (_mcpHttpServer != null)
            {
                _mcpHttpServer.Dispose();
                _mcpHttpServer = null;
            }
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
            // Try MCP first
            var mcpRequest = PacketSerializer.ParseMCPRequest(message);
            if (mcpRequest != null)
            {
                ProcessMcpRequestAsync(mcpRequest, client);
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
                ResponsePacket response = ExecuteCommandInternal(command);
                
                // Special handling for scene dump to maintain backward compatibility
                if (command.Type.ToLowerInvariant() == "scene_dump" || 
                    command.Type.ToLowerInvariant() == "get_scene" || 
                    command.Type.ToLowerInvariant() == "refresh")
                {
                    var payload = command.GetPayload<RequestSceneDumpPayload>();
                    bool includeInactive = payload?.IncludeInactive ?? _includeInactive;
                    _scanner.IncludeInactive = includeInactive;
                    var sceneDump = _scanner.ScanFullScene();
                    SendToClient(client, PacketSerializer.Serialize(sceneDump));
                }
                else if (command.Type.ToLowerInvariant() == "ping")
                {
                    SendResponse(client, ResponsePacket.Ok("pong", requestId));
                }
                else
                {
                    SendResponse(client, response);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink] Error processing command '{command.Type}': {ex.Message}\n{ex.StackTrace}");
                SendResponse(client, ResponsePacket.Error($"Error: {ex.Message}", requestId));
            }
        }

        internal ResponsePacket ExecuteCommandInternal(CommandPacket command)
        {
            switch (command.Type.ToLowerInvariant())
            {
                case "spawn":
                    return HandleSpawn(command);

                case "transform":
                case "move":
                    return HandleTransform(command);

                case "delete":
                case "destroy":
                    return HandleDelete(command);

                case "rename":
                    return HandleRename(command);

                case "set_parent":
                case "reparent":
                    return HandleSetParent(command);

                case "set_active":
                    return HandleSetActive(command);

                case "scene_dump":
                case "get_scene":
                case "refresh":
                    // For internal use, we return the dump in the response data
                    var dumpPayload = command.GetPayload<RequestSceneDumpPayload>();
                    _scanner.IncludeInactive = dumpPayload?.IncludeInactive ?? _includeInactive;
                    var sceneDump = _scanner.ScanFullScene();
                    return ResponsePacket.Ok("Scene dump", command.RequestId, JObject.FromObject(sceneDump.Payload));

                case "list_prefabs":
                    var prefabs = GetRegisteredPrefabNames();
                    var prefabData = new JObject
                    {
                        ["prefabs"] = JArray.FromObject(prefabs),
                        ["count"] = prefabs.Count
                    };
                    return ResponsePacket.Ok("List of spawnable prefabs", command.RequestId, prefabData);

                case "get_view_context":
                    return HandleGetViewContext(command);

                default:
                    return ResponsePacket.Error($"Unknown command type: {command.Type}", command.RequestId);
            }
        }

        #endregion

        private async void ProcessMcpRequestAsync(MCPRequest request, WebSocketConnection client)
        {
            try
            {
                var response = await _mcpHandler.HandleRequestAsync(request);
                if (response != null)
                {
                    SendToClient(client, PacketSerializer.Serialize(response));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error processing MCP request: {ex.Message}\n{ex.StackTrace}");
                var errorResponse = new MCPResponse
                {
                    Id = request?.Id,
                    Error = new MCPError { Code = -32603, Message = $"Internal error: {ex.Message}" }
                };
                SendToClient(client, PacketSerializer.Serialize(errorResponse));
            }
        }

        #region Command Handlers

        private ResponsePacket HandleSpawn(CommandPacket command, WebSocketConnection client = null)
        {
            var payload = command.GetPayload<SpawnPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.PrefabKey))
            {
                return ResponsePacket.Error("Missing prefab_key", command.RequestId);
            }

            if (!_prefabLookup.TryGetValue(payload.PrefabKey, out GameObject prefab))
            {
                return ResponsePacket.Error($"Prefab not found: {payload.PrefabKey}", command.RequestId);
            }

            // Parse position
            Vector3 position = Vector3.zero;
            if (payload.Position != null && payload.Position.Length >= 3)
            {
                position = new Vector3(payload.Position[0], payload.Position[1], payload.Position[2]);
            }

            // Parse rotation
            Quaternion rotation = Quaternion.identity;
            if (payload.Rotation != null && payload.Rotation.Length >= 4)
            {
                rotation = new Quaternion(payload.Rotation[0], payload.Rotation[1], payload.Rotation[2], payload.Rotation[3]);
            }

            // Spawn
            GameObject spawned = Instantiate(prefab, position, rotation);

            // Set name if provided
            if (!string.IsNullOrEmpty(payload.Name))
            {
                spawned.name = payload.Name;
            }

            // Set parent if provided
            if (!string.IsNullOrEmpty(payload.ParentUUID))
            {
                var parent = _scanner.GetGameObjectByUUID(payload.ParentUUID);
                if (parent != null)
                {
                    spawned.transform.SetParent(parent.transform, true);
                }
            }

            // Register with UUID
            string uuid = !string.IsNullOrEmpty(payload.Id) ? payload.Id : Guid.NewGuid().ToString("N").Substring(0, 12);
            _scanner.RegisterWithUUID(spawned, uuid);

            // Apply scale if provided
            if (payload.Scale != null && payload.Scale.Length >= 3)
            {
                spawned.transform.localScale = new Vector3(payload.Scale[0], payload.Scale[1], payload.Scale[2]);
            }

            Debug.Log($"[LiveLink] Spawned '{prefab.name}' with UUID: {uuid}");

            // Send notification
            var notification = new ObjectSpawnedPacket
            {
                UUID = uuid,
                Prefab = payload.PrefabKey,
                Object = CreateSceneObjectDTO(spawned, uuid)
            };
            Broadcast(PacketSerializer.Serialize(notification));

            // Send success response
            var responseData = new JObject
            {
                ["uuid"] = uuid,
                ["name"] = spawned.name
            };
            return ResponsePacket.Ok("Object spawned", command.RequestId, responseData);
        }

        private ResponsePacket HandleTransform(CommandPacket command)
        {
            var payload = command.GetPayload<TransformPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                return ResponsePacket.Error("Missing uuid", command.RequestId);
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                return ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId);
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

            return ResponsePacket.Ok("Transform updated", command.RequestId);
        }

        private ResponsePacket HandleDelete(CommandPacket command)
        {
            var payload = command.GetPayload<DeletePayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                return ResponsePacket.Error("Missing uuid", command.RequestId);
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                return ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId);
            }

            string uuid = payload.UUID;
            _scanner.Unregister(obj);
            Destroy(obj);

            Debug.Log($"[LiveLink] Deleted object with UUID: {uuid}");

            // Send notification
            var notification = new ObjectDestroyedPacket { UUID = uuid };
            Broadcast(PacketSerializer.Serialize(notification));

            return ResponsePacket.Ok("Object deleted", command.RequestId);
        }

        private ResponsePacket HandleRename(CommandPacket command)
        {
            var payload = command.GetPayload<RenamePayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                return ResponsePacket.Error("Missing uuid", command.RequestId);
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                return ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId);
            }

            obj.name = payload.Name ?? "Renamed Object";
            return ResponsePacket.Ok("Object renamed", command.RequestId);
        }

        private ResponsePacket HandleSetParent(CommandPacket command)
        {
            var payload = command.GetPayload<SetParentPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                return ResponsePacket.Error("Missing uuid", command.RequestId);
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                return ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId);
            }

            Transform newParent = null;
            if (!string.IsNullOrEmpty(payload.ParentUUID))
            {
                var parentObj = _scanner.GetGameObjectByUUID(payload.ParentUUID);
                if (parentObj == null)
                {
                    return ResponsePacket.Error($"Parent not found: {payload.ParentUUID}", command.RequestId);
                }
                newParent = parentObj.transform;
            }

            obj.transform.SetParent(newParent, payload.WorldPositionStays);
            return ResponsePacket.Ok("Parent changed", command.RequestId);
        }

        private ResponsePacket HandleSetActive(CommandPacket command)
        {
            var payload = command.GetPayload<SetActivePayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                return ResponsePacket.Error("Missing uuid", command.RequestId);
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                return ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId);
            }

            obj.SetActive(payload.Active);
            return ResponsePacket.Ok($"Object {(payload.Active ? "activated" : "deactivated")}", command.RequestId);
        }

        private ResponsePacket HandleGetViewContext(CommandPacket command)
        {
            var payload = command.GetPayload<GetViewContextPayload>();
            string cameraTag = payload?.CameraTag ?? "MainCamera";
            bool includeVisible = payload?.IncludeVisibleObjects ?? false;
            float raycastDist = payload?.RaycastDistance ?? 100f;

            // Find camera
            Camera cam = null;
            if (cameraTag == "MainCamera")
            {
                cam = Camera.main;
            }
            else
            {
                var cameras = FindObjectsOfType<Camera>();
                foreach (var c in cameras)
                {
                    if (c.tag == cameraTag || c.name == cameraTag)
                    {
                        cam = c;
                        break;
                    }
                }
            }

            if (cam == null)
            {
                return ResponsePacket.Error($"Camera not found with tag/name: {cameraTag}", command.RequestId);
            }

            // Build context data
            Transform camTransform = cam.transform;
            Vector3 position = camTransform.position;
            Vector3 forward = camTransform.forward;
            Vector3 right = camTransform.right;
            Vector3 up = camTransform.up;
            Quaternion rotation = camTransform.rotation;

            var contextData = new JObject
            {
                ["camera_name"] = cam.name,
                ["camera_tag"] = cam.tag,
                ["position"] = JArray.FromObject(new[] { position.x, position.y, position.z }),
                ["rotation"] = JArray.FromObject(new[] { rotation.x, rotation.y, rotation.z, rotation.w }),
                ["forward"] = JArray.FromObject(new[] { forward.x, forward.y, forward.z }),
                ["right"] = JArray.FromObject(new[] { right.x, right.y, right.z }),
                ["up"] = JArray.FromObject(new[] { up.x, up.y, up.z }),
                ["field_of_view"] = cam.fieldOfView,
                ["orthographic"] = cam.orthographic,
                ["near_clip"] = cam.nearClipPlane,
                ["far_clip"] = cam.farClipPlane
            };

            // Raycast from camera center
            Ray ray = new Ray(position, forward);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, raycastDist))
            {
                string hitUuid = _scanner.GetOrCreateUUID(hit.collider.gameObject);
                contextData["looking_at"] = new JObject
                {
                    ["object_name"] = hit.collider.gameObject.name,
                    ["object_uuid"] = hitUuid,
                    ["hit_point"] = JArray.FromObject(new[] { hit.point.x, hit.point.y, hit.point.z }),
                    ["hit_distance"] = hit.distance,
                    ["hit_normal"] = JArray.FromObject(new[] { hit.normal.x, hit.normal.y, hit.normal.z })
                };
            }
            else
            {
                contextData["looking_at"] = null;
            }

            // Optional: include visible objects in frustum
            if (includeVisible)
            {
                var visibleObjects = new JArray();
                var allObjects = _scanner.GetSceneObjects(false);
                
                foreach (var dto in allObjects)
                {
                    var obj = _scanner.GetGameObjectByUUID(dto.UUID);
                    if (obj != null)
                    {
                        Renderer renderer = obj.GetComponent<Renderer>();
                        if (renderer != null && renderer.isVisible)
                        {
                            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
                            if (GeometryUtility.TestPlanesAABB(planes, renderer.bounds))
                            {
                                visibleObjects.Add(new JObject
                                {
                                    ["uuid"] = dto.UUID,
                                    ["name"] = dto.Name,
                                    ["distance"] = Vector3.Distance(position, obj.transform.position)
                                });
                            }
                        }
                    }
                }
                
                contextData["visible_objects"] = visibleObjects;
                contextData["visible_count"] = visibleObjects.Count;
            }

            return ResponsePacket.Ok("View context", command.RequestId, contextData);
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

        internal void BroadcastInternal(string message)
        {
            Broadcast(message);
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

        internal SceneObjectDTO CreateSceneObjectDTO(GameObject obj, string uuid)
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
