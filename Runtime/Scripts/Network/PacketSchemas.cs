using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveLink.Network
{
    /// <summary>
    /// Data Transfer Objects (DTOs) for network communication.
    /// All classes are designed for JSON serialization via Newtonsoft.Json.
    /// </summary>

    #region Base Packet

    /// <summary>
    /// Base class for all network packets.
    /// </summary>
    [Serializable]
    public class BasePacket
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

        public BasePacket()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public BasePacket(string type) : this()
        {
            Type = type;
        }
    }

    #endregion

    #region Outgoing Packets (Unity -> External)

    /// <summary>
    /// Transform data for a scene object.
    /// </summary>
    [Serializable]
    public class TransformDTO
    {
        [JsonProperty("pos")]
        public float[] Position { get; set; } = new float[3];

        [JsonProperty("rot")]
        public float[] Rotation { get; set; } = new float[4]; // Quaternion [x,y,z,w]

        [JsonProperty("scale")]
        public float[] Scale { get; set; } = new float[3];
    }

    /// <summary>
    /// Represents a single scene object for serialization.
    /// </summary>
    [Serializable]
    public class SceneObjectDTO
    {
        [JsonProperty("uuid")]
        public string UUID { get; set; }

        [JsonProperty("parent_uuid")]
        public string ParentUUID { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("transform")]
        public TransformDTO Transform { get; set; }

        [JsonProperty("active")]
        public bool Active { get; set; } = true;

        [JsonProperty("layer")]
        public int Layer { get; set; }

        [JsonProperty("tag")]
        public string Tag { get; set; }

        [JsonProperty("children")]
        public List<string> Children { get; set; } = new List<string>();
    }

    /// <summary>
    /// Scene dump packet - contains full scene hierarchy.
    /// </summary>
    [Serializable]
    public class SceneDumpPacket : BasePacket
    {
        [JsonProperty("payload")]
        public SceneDumpPayload Payload { get; set; }

        public SceneDumpPacket() : base("scene_dump")
        {
            Payload = new SceneDumpPayload();
        }
    }

    [Serializable]
    public class SceneDumpPayload
    {
        [JsonProperty("root_id")]
        public string RootId { get; set; } = "scene_root";

        [JsonProperty("scene_name")]
        public string SceneName { get; set; }

        [JsonProperty("object_count")]
        public int ObjectCount { get; set; }

        [JsonProperty("objects")]
        public List<SceneObjectDTO> Objects { get; set; } = new List<SceneObjectDTO>();
    }

    /// <summary>
    /// Sync packet - contains scene state update (can be delta or full).
    /// </summary>
    [Serializable]
    public class SyncPacket : BasePacket
    {
        [JsonProperty("is_delta")]
        public bool IsDelta { get; set; }

        [JsonProperty("objects")]
        public List<SceneObjectDTO> Objects { get; set; } = new List<SceneObjectDTO>();

        public SyncPacket() : base("sync")
        {
        }
    }

    /// <summary>
    /// Heartbeat packet - keeps connection alive.
    /// </summary>
    [Serializable]
    public class HeartbeatPacket : BasePacket
    {
        [JsonProperty("client_count")]
        public int ClientCount { get; set; }

        public HeartbeatPacket() : base("heartbeat")
        {
        }
    }

    /// <summary>
    /// Response packet - sent after processing a command.
    /// </summary>
    [Serializable]
    public class ResponsePacket : BasePacket
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("request_id")]
        public string RequestId { get; set; }

        [JsonProperty("data")]
        public JObject Data { get; set; }

        public ResponsePacket() : base("response")
        {
        }

        public static ResponsePacket Ok(string message = "Success", string requestId = null, JObject data = null)
        {
            return new ResponsePacket
            {
                Success = true,
                Message = message,
                RequestId = requestId,
                Data = data
            };
        }

        public static ResponsePacket Error(string message, string requestId = null)
        {
            return new ResponsePacket
            {
                Success = false,
                Message = message,
                RequestId = requestId
            };
        }
    }

    /// <summary>
    /// Object spawned notification packet.
    /// </summary>
    [Serializable]
    public class ObjectSpawnedPacket : BasePacket
    {
        [JsonProperty("uuid")]
        public string UUID { get; set; }

        [JsonProperty("prefab")]
        public string Prefab { get; set; }

        [JsonProperty("object")]
        public SceneObjectDTO Object { get; set; }

        public ObjectSpawnedPacket() : base("object_spawned")
        {
        }
    }

    /// <summary>
    /// Object destroyed notification packet.
    /// </summary>
    [Serializable]
    public class ObjectDestroyedPacket : BasePacket
    {
        [JsonProperty("uuid")]
        public string UUID { get; set; }

        public ObjectDestroyedPacket() : base("object_destroyed")
        {
        }
    }

    #endregion

    #region Incoming Packets (External -> Unity)

    /// <summary>
    /// Generic command packet from external client.
    /// </summary>
    [Serializable]
    public class CommandPacket
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("request_id")]
        public string RequestId { get; set; }

        [JsonProperty("payload")]
        public JObject Payload { get; set; }

        /// <summary>
        /// Parses a JSON string into a CommandPacket.
        /// </summary>
        public static CommandPacket Parse(string json)
        {
            return JsonConvert.DeserializeObject<CommandPacket>(json);
        }

        /// <summary>
        /// Gets a typed payload.
        /// </summary>
        public T GetPayload<T>() where T : class
        {
            return Payload?.ToObject<T>();
        }
    }

    /// <summary>
    /// Spawn command payload.
    /// </summary>
    [Serializable]
    public class SpawnPayload
    {
        [JsonProperty("prefab_key")]
        public string PrefabKey { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("position")]
        public float[] Position { get; set; }

        [JsonProperty("rotation")]
        public float[] Rotation { get; set; }

        [JsonProperty("scale")]
        public float[] Scale { get; set; }

        [JsonProperty("parent_uuid")]
        public string ParentUUID { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// Transform command payload.
    /// </summary>
    [Serializable]
    public class TransformPayload
    {
        [JsonProperty("uuid")]
        public string UUID { get; set; }

        [JsonProperty("position")]
        public float[] Position { get; set; }

        [JsonProperty("rotation")]
        public float[] Rotation { get; set; }

        [JsonProperty("scale")]
        public float[] Scale { get; set; }

        [JsonProperty("local")]
        public bool UseLocalSpace { get; set; } = false;
    }

    /// <summary>
    /// Delete command payload.
    /// </summary>
    [Serializable]
    public class DeletePayload
    {
        [JsonProperty("uuid")]
        public string UUID { get; set; }

        [JsonProperty("include_children")]
        public bool IncludeChildren { get; set; } = true;
    }

    /// <summary>
    /// Rename command payload.
    /// </summary>
    [Serializable]
    public class RenamePayload
    {
        [JsonProperty("uuid")]
        public string UUID { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// Set parent command payload.
    /// </summary>
    [Serializable]
    public class SetParentPayload
    {
        [JsonProperty("uuid")]
        public string UUID { get; set; }

        [JsonProperty("parent_uuid")]
        public string ParentUUID { get; set; }

        [JsonProperty("world_position_stays")]
        public bool WorldPositionStays { get; set; } = true;
    }

    /// <summary>
    /// Set active command payload.
    /// </summary>
    [Serializable]
    public class SetActivePayload
    {
        [JsonProperty("uuid")]
        public string UUID { get; set; }

        [JsonProperty("active")]
        public bool Active { get; set; }
    }

    /// <summary>
    /// Request scene dump command payload.
    /// </summary>
    [Serializable]
    public class RequestSceneDumpPayload
    {
        [JsonProperty("include_inactive")]
        public bool IncludeInactive { get; set; } = false;
    }

    #endregion

    #region Serialization Helpers

    /// <summary>
    /// Helper class for JSON serialization.
    /// </summary>
    public static class PacketSerializer
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };

        public static string Serialize<T>(T packet)
        {
            return JsonConvert.SerializeObject(packet, Settings);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }

        public static CommandPacket ParseCommand(string json)
        {
            try
            {
                return JsonConvert.DeserializeObject<CommandPacket>(json);
            }
            catch
            {
                return null;
            }
        }
    }

    #endregion
}
