using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using LiveLink.Network;
using UnityEngine;

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
            var resources = new List<object>();
            var sceneObjects = _manager.Scanner.GetSceneObjects(false);
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            foreach (var dto in sceneObjects)
            {
                resources.Add(new {
                    uri = MCPResourceMapper.GetResourceURI(sceneName, dto.UUID),
                    name = dto.Name,
                    mimeType = "application/json",
                    description = $"Unity GameObject: {dto.Name} ({dto.UUID})"
                });
            }

            return CreateSuccessResponse(id, new { resources = resources });
        }

        private MCPResponse HandleReadResource(MCPRequest request)
        {
            string uri = request.Params?["uri"]?.ToString();
            if (string.IsNullOrEmpty(uri))
                return CreateErrorResponse(request.Id, -32602, "URI is required");

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
                default:
                    return null;
            }

            return command;
        }

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
