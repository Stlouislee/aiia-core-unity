using System;
using System.Collections.Generic;
using System.Net.WebSockets;
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

        private void HandleMessage(string message, WebSocket client)
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

        private void ProcessCommand(string message, WebSocket client)
        {
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

        #region Command Handlers

        private void HandleSpawn(CommandPacket command, WebSocket client)
        {
            var payload = command.GetPayload<SpawnPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.PrefabKey))
            {
                SendResponse(client, ResponsePacket.Error("Missing prefab_key", command.RequestId));
                return;
            }

            if (!_prefabLookup.TryGetValue(payload.PrefabKey, out GameObject prefab))
            {
                SendResponse(client, ResponsePacket.Error($"Prefab not found: {payload.PrefabKey}", command.RequestId));
                return;
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
            SendResponse(client, ResponsePacket.Ok("Object spawned", command.RequestId, responseData));
        }

        private void HandleTransform(CommandPacket command, WebSocket client)
        {
            var payload = command.GetPayload<TransformPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                SendResponse(client, ResponsePacket.Error("Missing uuid", command.RequestId));
                return;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                SendResponse(client, ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId));
                return;
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

            SendResponse(client, ResponsePacket.Ok("Transform updated", command.RequestId));
        }

        private void HandleDelete(CommandPacket command, WebSocket client)
        {
            var payload = command.GetPayload<DeletePayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                SendResponse(client, ResponsePacket.Error("Missing uuid", command.RequestId));
                return;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                SendResponse(client, ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId));
                return;
            }

            string uuid = payload.UUID;
            _scanner.Unregister(obj);
            Destroy(obj);

            Debug.Log($"[LiveLink] Deleted object with UUID: {uuid}");

            // Send notification
            var notification = new ObjectDestroyedPacket { UUID = uuid };
            Broadcast(PacketSerializer.Serialize(notification));

            SendResponse(client, ResponsePacket.Ok("Object deleted", command.RequestId));
        }

        private void HandleRename(CommandPacket command, WebSocket client)
        {
            var payload = command.GetPayload<RenamePayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                SendResponse(client, ResponsePacket.Error("Missing uuid", command.RequestId));
                return;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                SendResponse(client, ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId));
                return;
            }

            obj.name = payload.Name ?? "Renamed Object";
            SendResponse(client, ResponsePacket.Ok("Object renamed", command.RequestId));
        }

        private void HandleSetParent(CommandPacket command, WebSocket client)
        {
            var payload = command.GetPayload<SetParentPayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                SendResponse(client, ResponsePacket.Error("Missing uuid", command.RequestId));
                return;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                SendResponse(client, ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId));
                return;
            }

            Transform newParent = null;
            if (!string.IsNullOrEmpty(payload.ParentUUID))
            {
                var parentObj = _scanner.GetGameObjectByUUID(payload.ParentUUID);
                if (parentObj == null)
                {
                    SendResponse(client, ResponsePacket.Error($"Parent not found: {payload.ParentUUID}", command.RequestId));
                    return;
                }
                newParent = parentObj.transform;
            }

            obj.transform.SetParent(newParent, payload.WorldPositionStays);
            SendResponse(client, ResponsePacket.Ok("Parent changed", command.RequestId));
        }

        private void HandleSetActive(CommandPacket command, WebSocket client)
        {
            var payload = command.GetPayload<SetActivePayload>();
            if (payload == null || string.IsNullOrEmpty(payload.UUID))
            {
                SendResponse(client, ResponsePacket.Error("Missing uuid", command.RequestId));
                return;
            }

            var obj = _scanner.GetGameObjectByUUID(payload.UUID);
            if (obj == null)
            {
                SendResponse(client, ResponsePacket.Error($"Object not found: {payload.UUID}", command.RequestId));
                return;
            }

            obj.SetActive(payload.Active);
            SendResponse(client, ResponsePacket.Ok($"Object {(payload.Active ? "activated" : "deactivated")}", command.RequestId));
        }

        private void HandleSceneDump(CommandPacket command, WebSocket client)
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

        private void SendToClient(WebSocket client, string message)
        {
            if (_debugLogging)
            {
                Debug.Log($"[LiveLink] Sending: {message.Substring(0, Math.Min(100, message.Length))}...");
            }
            _ = _server?.SendAsync(client, message);
        }

        private void SendResponse(WebSocket client, ResponsePacket response)
        {
            string json = PacketSerializer.Serialize(response);
            SendToClient(client, json);
        }

        private void OnClientConnected(WebSocket client)
        {
            MainThreadDispatcher.EnqueueSafe(() =>
            {
                // Send initial scene dump to new client
                var sceneDump = _scanner.ScanFullScene();
                string json = PacketSerializer.Serialize(sceneDump);
                _ = _server?.SendAsync(client, json);
            }, "OnClientConnected");
        }

        private void OnClientDisconnected(WebSocket client)
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
            return new List<string>(_prefabLookup?.Keys ?? Array.Empty<string>());
        }

        #endregion
    }
}
