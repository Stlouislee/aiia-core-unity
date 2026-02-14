using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveLink.Network
{
    #region Scene Info DTOs

    /// <summary>
    /// Basic scene information for unity://scene/active
    /// </summary>
    [Serializable]
    public class SceneInfoDTO
    {
        [JsonProperty("scene_name")]
        public string SceneName { get; set; }

        [JsonProperty("scene_path")]
        public string ScenePath { get; set; }

        [JsonProperty("is_loaded")]
        public bool IsLoaded { get; set; }

        [JsonProperty("is_dirty")]
        public bool IsDirty { get; set; }

        [JsonProperty("root_count")]
        public int RootCount { get; set; }

        [JsonProperty("object_count")]
        public int ObjectCount { get; set; }

        [JsonProperty("render_pipeline")]
        public string RenderPipeline { get; set; }

        [JsonProperty("time_scale")]
        public float TimeScale { get; set; }

        [JsonProperty("game_time")]
        public float GameTime { get; set; }

        [JsonProperty("real_time")]
        public float RealTime { get; set; }

        [JsonProperty("frame_count")]
        public long FrameCount { get; set; }

        [JsonProperty("quality_level")]
        public int QualityLevel { get; set; }

        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("unity_version")]
        public string UnityVersion { get; set; }
    }

    #endregion

    #region Hierarchy DTOs

    /// <summary>
    /// Hierarchy node for unity://scene/hierarchy
    /// </summary>
    [Serializable]
    public class HierarchyNodeDTO
    {
        [JsonProperty("instance_id")]
        public int InstanceId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("active")]
        public bool Active { get; set; }

        [JsonProperty("layer")]
        public int Layer { get; set; }

        [JsonProperty("tag")]
        public string Tag { get; set; }

        [JsonProperty("is_static")]
        public bool IsStatic { get; set; }

        [JsonProperty("depth")]
        public int Depth { get; set; }

        [JsonProperty("child_count")]
        public int ChildCount { get; set; }

        [JsonProperty("children")]
        public List<HierarchyNodeDTO> Children { get; set; } = new List<HierarchyNodeDTO>();
    }

    #endregion

    #region GameObject Metadata DTOs

    /// <summary>
    /// GameObject metadata for unity://go/{instanceId}
    /// </summary>
    [Serializable]
    public class GameObjectMetadataDTO
    {
        [JsonProperty("instance_id")]
        public int InstanceId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("active")]
        public bool Active { get; set; }

        [JsonProperty("active_in_hierarchy")]
        public bool ActiveInHierarchy { get; set; }

        [JsonProperty("is_static")]
        public bool IsStatic { get; set; }

        [JsonProperty("layer")]
        public int Layer { get; set; }

        [JsonProperty("tag")]
        public string Tag { get; set; }

        [JsonProperty("scene")]
        public string SceneName { get; set; }

        [JsonProperty("transform")]
        public TransformMetadataDTO Transform { get; set; }

        [JsonProperty("parent")]
        public ParentInfoDTO Parent { get; set; }

        [JsonProperty("children")]
        public List<ChildInfoDTO> Children { get; set; } = new List<ChildInfoDTO>();

        [JsonProperty("component_count")]
        public int ComponentCount { get; set; }
    }

    [Serializable]
    public class TransformMetadataDTO
    {
        [JsonProperty("position")]
        public float[] Position { get; set; }

        [JsonProperty("rotation")]
        public float[] Rotation { get; set; }

        [JsonProperty("scale")]
        public float[] Scale { get; set; }

        [JsonProperty("local_position")]
        public float[] LocalPosition { get; set; }

        [JsonProperty("local_rotation")]
        public float[] LocalRotation { get; set; }

        [JsonProperty("local_scale")]
        public float[] LocalScale { get; set; }
    }

    [Serializable]
    public class ParentInfoDTO
    {
        [JsonProperty("instance_id")]
        public int InstanceId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    [Serializable]
    public class ChildInfoDTO
    {
        [JsonProperty("instance_id")]
        public int InstanceId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("active")]
        public bool Active { get; set; }
    }

    #endregion

    #region Component DTOs

    /// <summary>
    /// Component list for unity://go/{instanceId}/components
    /// </summary>
    [Serializable]
    public class ComponentListDTO
    {
        [JsonProperty("instance_id")]
        public int GameObjectInstanceId { get; set; }

        [JsonProperty("game_object_name")]
        public string GameObjectName { get; set; }

        [JsonProperty("components")]
        public List<ComponentInfoDTO> Components { get; set; } = new List<ComponentInfoDTO>();
    }

    [Serializable]
    public class ComponentInfoDTO
    {
        [JsonProperty("instance_id")]
        public int InstanceId { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("short_type")]
        public string ShortType { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Component field snapshot for unity://component/{instanceId}/{componentType}
    /// </summary>
    [Serializable]
    public class ComponentSnapshotDTO
    {
        [JsonProperty("instance_id")]
        public int InstanceId { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("short_type")]
        public string ShortType { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        [JsonProperty("fields")]
        public Dictionary<string, object> Fields { get; set; } = new Dictionary<string, object>();
    }

    #endregion

    #region Selection DTOs

    /// <summary>
    /// Selection info for unity://selection
    /// </summary>
    [Serializable]
    public class SelectionDTO
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("active_object")]
        public SelectedObjectDTO ActiveObject { get; set; }

        [JsonProperty("objects")]
        public List<SelectedObjectDTO> Objects { get; set; } = new List<SelectedObjectDTO>();
    }

    [Serializable]
    public class SelectedObjectDTO
    {
        [JsonProperty("instance_id")]
        public int InstanceId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("scene")]
        public string SceneName { get; set; }
    }

    #endregion

    #region Event DTOs

    /// <summary>
    /// Event types for tracking scene changes
    /// </summary>
    public enum SceneEventType
    {
        ObjectCreated,
        ObjectDestroyed,
        ObjectParentChanged,
        ObjectTransformChanged,
        ObjectActiveChanged,
        ObjectNameChanged,
        ComponentAdded,
        ComponentRemoved,
        ComponentEnabledChanged,
        SceneLoaded,
        SceneUnloaded
    }

    /// <summary>
    /// Scene event for unity://events/recent
    /// </summary>
    [Serializable]
    public class SceneEventDTO
    {
        [JsonProperty("event_id")]
        public string EventId { get; set; }

        [JsonProperty("event_type")]
        public string EventType { get; set; }

        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

        [JsonProperty("game_time")]
        public float GameTime { get; set; }

        [JsonProperty("data")]
        public JObject Data { get; set; }
    }

    /// <summary>
    /// Recent events response
    /// </summary>
    [Serializable]
    public class RecentEventsDTO
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("max_events")]
        public int MaxEvents { get; set; }

        [JsonProperty("events")]
        public List<SceneEventDTO> Events { get; set; } = new List<SceneEventDTO>();
    }

    #endregion
}
