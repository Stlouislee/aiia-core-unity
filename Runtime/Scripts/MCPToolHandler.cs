using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using LiveLink.Network;
using UnityEngine;
#if LIVELINK_GLTFAST
using GLTFast;
#endif

namespace LiveLink
{
    /// <summary>
    /// Handles MCP (Model Context Protocol) tool calls and resource requests.
    /// </summary>
    public class MCPToolHandler
    {
        private readonly LiveLinkManager _manager;

        public MCPToolHandler(LiveLinkManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// Processes an MCP request and returns an MCP response.
        /// </summary>
        public MCPResponse HandleRequest(MCPRequest request)
        {
            if (request == null) return null;

            // NOTE: Prefer HandleRequestAsync. This synchronous entrypoint is kept for
            // backward compatibility, but cannot safely execute long-running tasks.
            if (request.Method == "tools/call")
            {
                string toolName = request.Params?["name"]?.ToString();
                if (string.Equals(toolName, "spawn_gltf", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateErrorResponse(request.Id, -32603, "spawn_gltf is asynchronous; call via HandleRequestAsync");
                }
            }

            try
            {
                switch (request.Method)
                {
                    case "initialize":
                        return HandleInitialize(request.Id, request.Params);
                    case "notifications/initialized":
                        // Client is ready, no response needed
                        return null;
                    case "tools/list":
                        return HandleListTools(request.Id);
                    case "tools/call":
                        return HandleCallTool(request);
                    case "resources/list":
                        return HandleListResources(request.Id);
                    case "resources/read":
                        return HandleReadResource(request);
                    case "prompts/list":
                        return HandleListPrompts(request.Id);
                    case "prompts/get":
                        return HandleGetPrompt(request);
                    default:
                        return CreateErrorResponse(request.Id, -32601, $"Method not found: {request.Method}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error handling request {request.Method}: {ex.Message}");
                return CreateErrorResponse(request.Id, -32603, $"Internal error: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes an MCP request asynchronously. Required for tools that perform
        /// runtime loading (e.g. glTF import).
        /// </summary>
        public async Task<MCPResponse> HandleRequestAsync(MCPRequest request)
        {
            if (request == null) return null;

            try
            {
                switch (request.Method)
                {
                    case "initialize":
                        return HandleInitialize(request.Id, request.Params);
                    case "notifications/initialized":
                        return null;
                    case "tools/list":
                        return HandleListTools(request.Id);
                    case "tools/call":
                        return await HandleCallToolAsync(request);
                    case "resources/list":
                        return HandleListResources(request.Id);
                    case "resources/read":
                        return HandleReadResource(request);
                    case "prompts/list":
                        return HandleListPrompts(request.Id);
                    case "prompts/get":
                        return HandleGetPrompt(request);
                    default:
                        return CreateErrorResponse(request.Id, -32601, $"Method not found: {request.Method}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error handling request {request.Method}: {ex.Message}");
                return CreateErrorResponse(request.Id, -32603, $"Internal error: {ex.Message}");
            }
        }

        private MCPResponse HandleInitialize(object id, JObject parameters)
        {
            string protocolVersion = parameters?["protocolVersion"]?.ToString();
            Debug.Log($"[LiveLink-MCP] Initialize request - Protocol version: {protocolVersion}");

            // Return server capabilities
            var serverCapabilities = new
            {
                resources = new
                {
                    subscribe = false
                },
                tools = new
                {
                    listChanged = true
                },
                prompts = new
                {
                    listChanged = false
                },
                logging = new
                {
                }
            };

            var serverInfo = new
            {
                name = "Unity LiveLink MCP Server",
                version = "1.0.0",
                description = "MCP server for Unity LiveLink - provides scene resources and tools",
                websiteUrl = "https://github.com/Stlouislee/aiia-core-unity"
            };

            return CreateSuccessResponse(id, new
            {
                protocolVersion = "2024-11-05",
                capabilities = serverCapabilities,
                serverInfo = serverInfo,
                instructions = "You can interact with Unity scene objects using the provided tools and resources."
            });
        }

        private MCPResponse HandleListTools(object id)
        {
            var tools = new List<object>
            {
                new {
                    name = "spawn_object",
                    description = "Spawn a new object from a prefab in the Unity scene.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            prefab_key = new { type = "string", description = "Name of the prefab to spawn (e.g., 'Cube', 'Sphere')" },
                            position = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3, description = "World position [x, y, z]" },
                            rotation = new { type = "array", items = new { type = "number" }, minItems = 4, maxItems = 4, description = "Quaternion rotation [x, y, z, w]" },
                            scale = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3, description = "Local scale [x, y, z]" },
                            name = new { type = "string", description = "Optional name for the spawned object" },
                            parent_uuid = new { type = "string", description = "Optional UUID of the parent object" }
                        },
                        required = new[] { "prefab_key" }
                    }
                },
                new {
                    name = "transform_object",
                    description = "Update the position, rotation, or scale of an existing object.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            uuid = new { type = "string", description = "UUID of the object to transform" },
                            position = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 },
                            rotation = new { type = "array", items = new { type = "number" }, minItems = 4, maxItems = 4 },
                            scale = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 }
                        },
                        required = new[] { "uuid" }
                    }
                },
                new {
                    name = "delete_object",
                    description = "Delete an object from the Unity scene.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            uuid = new { type = "string", description = "UUID of the object to delete" }
                        },
                        required = new[] { "uuid" }
                    }
                },
                new {
                    name = "scene_dump",
                    description = "Get a full dump of the current scene hierarchy.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            include_inactive = new { type = "boolean", description = "Whether to include inactive objects" }
                        }
                    }
                },
                new {
                    name = "list_spawnable_objects",
                    description = "Get a list of all prefab names that can be spawned using spawn_object.",
                    inputSchema = new {
                        type = "object",
                        properties = new { }
                    }
                },
                new {
                    name = "get_view_context",
                    description = "Get the current camera/player view context including position, orientation, and forward direction. Useful for spatial commands like 'spawn in front of me'.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            camera_tag = new { type = "string", description = "Camera tag to query (default: 'MainCamera')" },
                            include_visible_objects = new { type = "boolean", description = "Include list of objects visible in camera frustum" },
                            raycast_distance = new { type = "number", description = "Distance to raycast from camera center (default: 100)" }
                        }
                    }
                },
                new {
                    name = "spawn_gltf",
                    description = "Spawn a glTF asset at runtime via Unity glTFast. Provide either a URL (url) or a base64-encoded .glb (data_base64).",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            url = new { type = "string", description = "URL or file:// path to a .gltf/.glb" },
                            data_base64 = new { type = "string", description = "Base64 encoded binary glTF (.glb)" },
                            source_uri = new { type = "string", description = "Original URI for resolving relative references when using data_base64 (optional)" },
                            id = new { type = "string", description = "Optional UUID to assign to the spawned root" },
                            name = new { type = "string", description = "Optional name for the spawned root object" },
                            position = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3, description = "World position [x, y, z]" },
                            rotation = new { type = "array", items = new { type = "number" }, minItems = 4, maxItems = 4, description = "Quaternion rotation [x, y, z, w]" },
                            scale = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3, description = "Local scale [x, y, z]" },
                            parent_uuid = new { type = "string", description = "Optional UUID of the parent object" }
                        }
                    }
                }
            };

            return CreateSuccessResponse(id, new { tools = tools });
        }

        private MCPResponse HandleCallTool(MCPRequest request)
        {
            string toolName = request.Params?["name"]?.ToString();
            JObject arguments = request.Params?["arguments"] as JObject;

            if (string.IsNullOrEmpty(toolName))
                return CreateErrorResponse(request.Id, -32602, "Tool name is required");

            // Map MCP tool call to LiveLink command
            CommandPacket command = MapToolToCommand(toolName, arguments);
            if (command == null)
                return CreateErrorResponse(request.Id, -32601, $"Tool not supported: {toolName}");

            // Execute command via manager
            var result = _manager.ExecuteCommandInternal(command);
            
            if (result.Success)
            {
                // For commands that return useful data, include it in the response
                if (result.Data != null)
                {
                    string dataText;
                    
                    // For scene_dump, create a simplified version to reduce context size
                    if (toolName == "scene_dump")
                    {
                        dataText = CreateSimplifiedSceneDump(result.Data);
                    }
                    else
                    {
                        dataText = Newtonsoft.Json.JsonConvert.SerializeObject(result.Data, Newtonsoft.Json.Formatting.Indented);
                    }
                    
                    return CreateSuccessResponse(request.Id, new { 
                        content = new[] { 
                            new { type = "text", text = $"Successfully executed {toolName}: {result.Message}" },
                            new { type = "text", text = $"Data: {dataText}" }
                        },
                        isError = false
                    });
                }
                else
                {
                    return CreateSuccessResponse(request.Id, new { 
                        content = new[] { 
                            new { type = "text", text = $"Successfully executed {toolName}: {result.Message}" } 
                        },
                        isError = false
                    });
                }
            }
            else
            {
                return CreateSuccessResponse(request.Id, new {
                    content = new[] {
                        new { type = "text", text = $"Error executing {toolName}: {result.Message}" }
                    },
                    isError = true
                });
            }
        }

        private MCPResponse HandleListResources(object id)
        {
            var resourceProvider = _manager.ResourceProvider;
            if (resourceProvider == null)
            {
                return CreateErrorResponse(id, -32603, "Resource provider not initialized");
            }

            var resourceTemplates = resourceProvider.GetResourceTemplates();

            return CreateSuccessResponse(id, new { resources = resourceTemplates });
        }

        private MCPResponse HandleReadResource(MCPRequest request)
        {
            string uri = request.Params?["uri"]?.ToString();
            if (string.IsNullOrEmpty(uri))
                return CreateErrorResponse(request.Id, -32602, "URI is required");

            // Try new unity:// scheme first
            if (MCPResourceMapper.IsUnityScheme(uri))
            {
                var resourceProvider = _manager.ResourceProvider;
                if (resourceProvider == null)
                {
                    return CreateErrorResponse(request.Id, -32603, "Resource provider not initialized");
                }

                var result = resourceProvider.ReadResource(uri);
                if (result == null)
                {
                    return CreateErrorResponse(request.Id, -32004, $"Resource not found: {uri}");
                }

                string jsonText = Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented);

                return CreateSuccessResponse(request.Id, new
                {
                    contents = new[]
                    {
                        new
                        {
                            uri = uri,
                            mimeType = "application/json",
                            text = jsonText
                        }
                    }
                });
            }

            // Fallback: legacy mcp://unity scheme
            string uuid = MCPResourceMapper.GetUUIDFromURI(uri);
            if (string.IsNullOrEmpty(uuid))
                return CreateErrorResponse(request.Id, -32602, "Invalid resource URI");

            var obj = _manager.Scanner.GetGameObjectByUUID(uuid);
            if (obj == null)
                return CreateErrorResponse(request.Id, -32004, $"Resource not found: {uuid}");

            var dto = _manager.CreateSceneObjectDTO(obj, uuid);
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var mcpResource = MCPResourceMapper.ToMCPResource(dto, sceneName);

            return CreateSuccessResponse(request.Id, new {
                contents = new[] {
                    new {
                        uri = uri,
                        mimeType = "application/json",
                        text = Newtonsoft.Json.JsonConvert.SerializeObject(mcpResource)
                    }
                }
            });
        }

        private MCPResponse HandleListPrompts(object id)
        {
            var prompts = new List<object>
            {
                new
                {
                    name = "scene_analysis",
                    title = "Scene Analysis Workflow",
                    description = "Analyze the active Unity scene, summarize hierarchy hotspots, and suggest concrete tool calls.",
                    arguments = new[]
                    {
                        new { name = "analysis_goal", description = "What to optimize or inspect (performance, organization, gameplay setup, etc.)", required = false },
                        new { name = "include_inactive", description = "Whether to include inactive objects in the analysis", required = false },
                        new { name = "focus_query", description = "Optional object/type keyword to focus on", required = false }
                    }
                },
                new
                {
                    name = "spawn_from_intent",
                    title = "Intent-to-Spawn Workflow",
                    description = "Turn a natural-language level design intent into concrete Unity object spawns and transforms.",
                    arguments = new[]
                    {
                        new { name = "intent", description = "What should be created in the scene", required = true },
                        new { name = "count", description = "Preferred number of spawned objects", required = false },
                        new { name = "placement_strategy", description = "e.g. front_of_camera, grid, random_scatter", required = false }
                    }
                },
                new
                {
                    name = "object_repair",
                    title = "Object Repair Workflow",
                    description = "Diagnose and repair transform/parenting issues for a target object.",
                    arguments = new[]
                    {
                        new { name = "uuid", description = "Target object UUID", required = true },
                        new { name = "issue_description", description = "Describe the observed issue", required = false },
                        new { name = "preserve_world_pose", description = "Preserve world-space pose when reparenting", required = false }
                    }
                },
                new
                {
                    name = "scene_cleanup",
                    title = "Scene Cleanup Workflow",
                    description = "Find redundant/noisy objects and produce a safe cleanup plan using MCP tools.",
                    arguments = new[]
                    {
                        new { name = "scope", description = "all, inactive_only, or name_pattern", required = false },
                        new { name = "name_pattern", description = "Regex-like substring filter for candidate names", required = false },
                        new { name = "dry_run", description = "If true, only return plan and do not execute delete calls", required = false }
                    }
                }
            };

            return CreateSuccessResponse(id, new { prompts });
        }

        private MCPResponse HandleGetPrompt(MCPRequest request)
        {
            string promptName = request.Params?["name"]?.ToString();
            JObject arguments = request.Params?["arguments"] as JObject;

            if (string.IsNullOrEmpty(promptName))
                return CreateErrorResponse(request.Id, -32602, "Prompt name is required");

            if (arguments == null) arguments = new JObject();

            object result;
            switch (promptName)
            {
                case "scene_analysis":
                    result = BuildSceneAnalysisPrompt(arguments);
                    break;
                case "spawn_from_intent":
                    result = BuildSpawnFromIntentPrompt(arguments);
                    break;
                case "object_repair":
                    result = BuildObjectRepairPrompt(arguments);
                    break;
                case "scene_cleanup":
                    result = BuildSceneCleanupPrompt(arguments);
                    break;
                default:
                    return CreateErrorResponse(request.Id, -32601, $"Prompt not found: {promptName}");
            }

            return CreateSuccessResponse(request.Id, result);
        }

        private object BuildSceneAnalysisPrompt(JObject arguments)
        {
            string analysisGoal = GetStringArgument(arguments, "analysis_goal", "Find structural, gameplay, and performance improvements.");
            bool includeInactive = GetBoolArgument(arguments, "include_inactive", false);
            string focusQuery = GetStringArgument(arguments, "focus_query", "");

            string userText =
                "Analyze the current Unity scene and produce an actionable engineering report.\n" +
                $"Goal: {analysisGoal}\n" +
                $"Include inactive objects: {includeInactive}\n" +
                (string.IsNullOrEmpty(focusQuery) ? "" : $"Focus query: {focusQuery}\n") +
                "Workflow:\n" +
                "1) Call resources/read with uri 'unity://scene/active' to get scene overview.\n" +
                "2) Call resources/read with uri 'unity://scene/hierarchy?depth=3' to get the hierarchy tree.\n" +
                "3) For suspicious objects, call resources/read with uri 'unity://go/{instanceId}' and 'unity://go/{instanceId}/components'.\n" +
                "4) If needed, read specific component details via 'unity://component/{instanceId}/{componentType}'.\n" +
                "5) Check 'unity://events/recent' for recent changes that may relate to issues.\n" +
                "6) Return: findings, ranked fixes, and exact tool calls to apply fixes.";

            return new
            {
                description = "Analyze scene state and return prioritized fixes.",
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { type = "text", text = userText }
                        }
                    }
                }
            };
        }

        private object BuildSpawnFromIntentPrompt(JObject arguments)
        {
            string intent = GetStringArgument(arguments, "intent", "Create a small playable layout near the player view.");
            int count = GetIntArgument(arguments, "count", 3);
            string placement = GetStringArgument(arguments, "placement_strategy", "front_of_camera");

            string userText =
                "Convert the design intent into concrete Unity object creation actions.\n" +
                $"Intent: {intent}\n" +
                $"Target count: {count}\n" +
                $"Placement strategy: {placement}\n" +
                "Workflow:\n" +
                "1) Call tools/call(list_spawnable_objects) to learn available prefabs.\n" +
                "2) Call tools/call(get_view_context) to anchor placement.\n" +
                "3) Call resources/read with uri 'unity://scene/active' to understand the current scene.\n" +
                "4) Choose matching prefabs and call tools/call(spawn_object) multiple times.\n" +
                "5) Optionally call tools/call(transform_object) for alignment and spacing.\n" +
                "6) Return a concise build log with created UUIDs and final layout rationale.";

            return new
            {
                description = "Plan and execute object spawning from natural-language intent.",
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { type = "text", text = userText }
                        }
                    }
                }
            };
        }

        private object BuildObjectRepairPrompt(JObject arguments)
        {
            string uuid = GetStringArgument(arguments, "uuid", "");
            string issueDescription = GetStringArgument(arguments, "issue_description", "Object appears misplaced, rotated incorrectly, or attached to wrong parent.");
            bool preserveWorldPose = GetBoolArgument(arguments, "preserve_world_pose", true);

            if (string.IsNullOrEmpty(uuid))
            {
                return new
                {
                    description = "Repair object transform/parenting issues.",
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = new[]
                            {
                                new { type = "text", text = "Missing required argument: uuid. Ask for a target UUID, then rerun prompts/get for object_repair." }
                            }
                        }
                    }
                };
            }

            string userText =
                "Diagnose and repair a Unity object issue.\n" +
                $"Target UUID: {uuid}\n" +
                $"Issue description: {issueDescription}\n" +
                $"Preserve world pose on parent changes: {preserveWorldPose}\n" +
                "Workflow:\n" +
                "1) Call resources/read with uri 'unity://scene/hierarchy?depth=3' to understand the tree.\n" +
                "2) Find the target object's instanceId and call resources/read with uri 'unity://go/{instanceId}' to inspect its state.\n" +
                "3) Call resources/read with uri 'unity://go/{instanceId}/components' to check components.\n" +
                "4) Apply tools/call(transform_object) and, when supported by workflow, reparent via command tools.\n" +
                "5) Re-read resource and verify issue is resolved.\n" +
                "6) Return before/after summary and exact operations performed.";

            return new
            {
                description = "Repair object transform/parenting issues.",
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { type = "text", text = userText }
                        }
                    }
                }
            };
        }

        private object BuildSceneCleanupPrompt(JObject arguments)
        {
            string scope = GetStringArgument(arguments, "scope", "inactive_only");
            string namePattern = GetStringArgument(arguments, "name_pattern", "");
            bool dryRun = GetBoolArgument(arguments, "dry_run", true);

            string userText =
                "Generate and execute a safe scene cleanup workflow.\n" +
                $"Scope: {scope}\n" +
                (string.IsNullOrEmpty(namePattern) ? "" : $"Name pattern: {namePattern}\n") +
                $"Dry run: {dryRun}\n" +
                "Workflow:\n" +
                "1) Call resources/read with uri 'unity://scene/active' for overview.\n" +
                "2) Call resources/read with uri 'unity://scene/hierarchy?depth=10' to get the full hierarchy.\n" +
                "3) Identify deletion candidates according to scope and pattern.\n" +
                "4) For each candidate, call resources/read with uri 'unity://go/{instanceId}' to check children/dependencies.\n" +
                "5) Return candidate list with risk notes (parent/child impact).\n" +
                "6) If dry_run is false, call tools/call(delete_object) for approved candidates only.\n" +
                "7) Return final summary: removed count, skipped count, and unresolved risks.";

            return new
            {
                description = "Identify and optionally execute scene cleanup operations.",
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { type = "text", text = userText }
                        }
                    }
                }
            };
        }

        private string GetStringArgument(JObject arguments, string key, string defaultValue)
        {
            if (arguments == null) return defaultValue;
            return arguments[key]?.ToString() ?? defaultValue;
        }

        private bool GetBoolArgument(JObject arguments, string key, bool defaultValue)
        {
            if (arguments == null || arguments[key] == null) return defaultValue;

            var token = arguments[key];
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.String && bool.TryParse(token.ToString(), out bool parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        private int GetIntArgument(JObject arguments, string key, int defaultValue)
        {
            if (arguments == null || arguments[key] == null) return defaultValue;

            var token = arguments[key];
            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            if (token.Type == JTokenType.String && int.TryParse(token.ToString(), out int parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        private string CreateSimplifiedSceneDump(Newtonsoft.Json.Linq.JObject data)
        {
            try
            {
                var simplifiedDump = new
                {
                    scene_name = data["scene_name"]?.ToString(),
                    object_count = data["object_count"]?.Value<int>(),
                    objects = new List<object>()
                };

                var objects = data["objects"] as Newtonsoft.Json.Linq.JArray;
                if (objects != null)
                {
                    foreach (var obj in objects)
                    {
                        var simplifiedObject = new
                        {
                            uuid = obj["uuid"]?.ToString(),
                            name = obj["name"]?.ToString(),
                            parent_uuid = obj["parent_uuid"]?.ToString(),
                            active = obj["active"]?.Value<bool>() ?? true,
                            children_count = (obj["children"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0,
                            // Simplified transform - only position
                            position = obj["transform"]?["pos"]?.ToObject<float[]>() ?? new float[3]
                        };
                        simplifiedDump.objects.Add(simplifiedObject);
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(simplifiedDump, Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveLink-MCP] Error creating simplified scene dump: {ex.Message}");
                // Fallback to original data
                return Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
            }
        }

        private CommandPacket MapToolToCommand(string toolName, JObject args)
        {
            CommandPacket command = new CommandPacket();
            command.RequestId = Guid.NewGuid().ToString("N").Substring(0, 8);

            switch (toolName)
            {
                case "spawn_object":
                    command.Type = "spawn";
                    command.Payload = args;
                    break;
                case "transform_object":
                    command.Type = "transform";
                    command.Payload = args;
                    break;
                case "delete_object":
                    command.Type = "delete";
                    command.Payload = args;
                    break;
                case "scene_dump":
                    command.Type = "scene_dump";
                    command.Payload = args;
                    break;
                case "list_spawnable_objects":
                    command.Type = "list_prefabs";
                    command.Payload = args;
                    break;
                case "get_view_context":
                    command.Type = "get_view_context";
                    command.Payload = args;
                    break;
                default:
                    return null;
            }

            return command;
        }

        private async Task<MCPResponse> HandleCallToolAsync(MCPRequest request)
        {
            string toolName = request.Params?["name"]?.ToString();
            JObject arguments = request.Params?["arguments"] as JObject;

            if (string.IsNullOrEmpty(toolName))
                return CreateErrorResponse(request.Id, -32602, "Tool name is required");

            if (string.Equals(toolName, "spawn_gltf", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleSpawnGltfToolAsync(request.Id, arguments);
            }

            // Default: existing synchronous command execution
            CommandPacket command = MapToolToCommand(toolName, arguments);
            if (command == null)
                return CreateErrorResponse(request.Id, -32601, $"Tool not supported: {toolName}");

            var result = _manager.ExecuteCommandInternal(command);
            if (result.Success)
            {
                if (result.Data != null)
                {
                    string dataText;
                    if (toolName == "scene_dump")
                    {
                        dataText = CreateSimplifiedSceneDump(result.Data);
                    }
                    else
                    {
                        dataText = Newtonsoft.Json.JsonConvert.SerializeObject(result.Data, Newtonsoft.Json.Formatting.Indented);
                    }

                    return CreateSuccessResponse(request.Id, new
                    {
                        content = new[]
                        {
                            new { type = "text", text = $"Successfully executed {toolName}: {result.Message}" },
                            new { type = "text", text = $"Data: {dataText}" }
                        },
                        isError = false
                    });
                }

                return CreateSuccessResponse(request.Id, new
                {
                    content = new[]
                    {
                        new { type = "text", text = $"Successfully executed {toolName}: {result.Message}" }
                    },
                    isError = false
                });
            }

            return CreateSuccessResponse(request.Id, new
            {
                content = new[]
                {
                    new { type = "text", text = $"Error executing {toolName}: {result.Message}" }
                },
                isError = true
            });
        }

    #if LIVELINK_GLTFAST
        private async Task<MCPResponse> HandleSpawnGltfToolAsync(object id, JObject args)
        {
            if (args == null) args = new JObject();

            string url = args["url"]?.ToString();
            string dataBase64 = args["data_base64"]?.ToString();
            string sourceUriString = args["source_uri"]?.ToString();
            string name = args["name"]?.ToString();
            string parentUuid = args["parent_uuid"]?.ToString();
            string desiredUuid = args["id"]?.ToString();

            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(dataBase64))
            {
                return CreateSuccessResponse(id, new
                {
                    content = new[] { new { type = "text", text = "Error executing spawn_gltf: Provide either 'url' or 'data_base64'." } },
                    isError = true
                });
            }

            // Parse transform inputs
            Vector3 position = Vector3.zero;
            var posArr = args["position"] as JArray;
            if (posArr != null && posArr.Count >= 3)
            {
                position = new Vector3((float)posArr[0], (float)posArr[1], (float)posArr[2]);
            }

            Quaternion rotation = Quaternion.identity;
            var rotArr = args["rotation"] as JArray;
            if (rotArr != null && rotArr.Count >= 4)
            {
                rotation = new Quaternion((float)rotArr[0], (float)rotArr[1], (float)rotArr[2], (float)rotArr[3]);
            }

            Vector3 scale = Vector3.one;
            var scaleArr = args["scale"] as JArray;
            if (scaleArr != null && scaleArr.Count >= 3)
            {
                scale = new Vector3((float)scaleArr[0], (float)scaleArr[1], (float)scaleArr[2]);
            }

            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentUuid))
            {
                parent = _manager.Scanner.GetGameObjectByUUID(parentUuid);
                if (parent == null)
                {
                    return CreateSuccessResponse(id, new
                    {
                        content = new[] { new { type = "text", text = $"Error executing spawn_gltf: Parent not found: {parentUuid}" } },
                        isError = true
                    });
                }
            }

            var root = new GameObject(string.IsNullOrEmpty(name) ? "glTF" : name);
            try
            {
                if (parent != null)
                {
                    root.transform.SetParent(parent.transform, true);
                }

                root.transform.position = position;
                root.transform.rotation = rotation;
                root.transform.localScale = scale;

                var gltf = new GltfImport();
                bool loadSuccess;
                string sourceLabel;

                if (!string.IsNullOrEmpty(url))
                {
                    sourceLabel = url;
                    loadSuccess = await gltf.Load(url);
                }
                else
                {
                    sourceLabel = "(memory glb)";
                    byte[] data;
                    try
                    {
                        data = Convert.FromBase64String(dataBase64);
                    }
                    catch (FormatException)
                    {
                        UnityEngine.Object.Destroy(root);
                        return CreateSuccessResponse(id, new
                        {
                            content = new[] { new { type = "text", text = "Error executing spawn_gltf: data_base64 is not valid base64." } },
                            isError = true
                        });
                    }

                    Uri sourceUri;
                    if (!string.IsNullOrEmpty(sourceUriString) && Uri.TryCreate(sourceUriString, UriKind.Absolute, out var parsed))
                    {
                        sourceUri = parsed;
                    }
                    else
                    {
                        // Fallback absolute URI; helps resolve relative references in some cases.
                        sourceUri = new Uri("file:///memory.glb");
                    }

                    loadSuccess = await gltf.LoadGltfBinary(data, sourceUri);
                }

                if (!loadSuccess)
                {
                    UnityEngine.Object.Destroy(root);
                    return CreateSuccessResponse(id, new
                    {
                        content = new[] { new { type = "text", text = $"Error executing spawn_gltf: Failed to load glTF from {sourceLabel}" } },
                        isError = true
                    });
                }

                bool instantiateSuccess = await gltf.InstantiateMainSceneAsync(root.transform);
                if (!instantiateSuccess)
                {
                    UnityEngine.Object.Destroy(root);
                    return CreateSuccessResponse(id, new
                    {
                        content = new[] { new { type = "text", text = "Error executing spawn_gltf: Failed to instantiate glTF scene." } },
                        isError = true
                    });
                }

                string uuid = !string.IsNullOrEmpty(desiredUuid) ? desiredUuid : Guid.NewGuid().ToString("N").Substring(0, 12);
                _manager.Scanner.RegisterWithUUID(root, uuid);

                // Broadcast like other spawns (prefab field is used as a label here)
                var notification = new ObjectSpawnedPacket
                {
                    UUID = uuid,
                    Prefab = "gltf",
                    Object = _manager.CreateSceneObjectDTO(root, uuid)
                };
                _manager.BroadcastInternal(PacketSerializer.Serialize(notification));

                var responseData = new JObject
                {
                    ["uuid"] = uuid,
                    ["name"] = root.name,
                    ["source"] = !string.IsNullOrEmpty(url) ? url : "data_base64"
                };
                string dataText = Newtonsoft.Json.JsonConvert.SerializeObject(responseData, Newtonsoft.Json.Formatting.Indented);

                return CreateSuccessResponse(id, new
                {
                    content = new[]
                    {
                        new { type = "text", text = "Successfully executed spawn_gltf: Object spawned" },
                        new { type = "text", text = $"Data: {dataText}" }
                    },
                    isError = false
                });
            }
            catch (Exception ex)
            {
                UnityEngine.Object.Destroy(root);
                return CreateSuccessResponse(id, new
                {
                    content = new[] { new { type = "text", text = $"Error executing spawn_gltf: {ex.Message}" } },
                    isError = true
                });
            }
        }
#else
        private Task<MCPResponse> HandleSpawnGltfToolAsync(object id, JObject args)
        {
            return Task.FromResult(CreateSuccessResponse(id, new
            {
                content = new[]
                {
                    new { type = "text", text = "Error executing spawn_gltf: Unity glTFast package not found. Install 'com.unity.cloud.gltfast' and ensure this assembly can reference it." }
                },
                isError = true
            }));
        }
#endif

        private MCPResponse CreateSuccessResponse(object id, object result)
        {
            return new MCPResponse { Id = id, Result = result };
        }

        private MCPResponse CreateErrorResponse(object id, int code, string message)
        {
            return new MCPResponse { Id = id, Error = new MCPError { Code = code, Message = message } };
        }
    }
}
