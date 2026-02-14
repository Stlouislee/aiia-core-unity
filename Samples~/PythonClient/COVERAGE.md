# Unity LiveLink MCP 客户端功能覆盖表

本文档详细记录 Python 客户端与 Unity 服务器端的功能对应关系。

## Tools（工具）覆盖

### ✅ spawn_object - 生成对象

**服务器定义** (`MCPToolHandler.cs` 行 156-167)
- `prefab_key` (string, 必需) - 预制体名称
- `position` (array, 可选) - 世界坐标 [x, y, z]
- `rotation` (array, 可选) - 四元数 [x, y, z, w]
- `scale` (array, 可选) - 缩放 [x, y, z]
- `name` (string, 可选) - 对象名称
- `parent_uuid` (string, 可选) - 父对象 UUID

**客户端实现** (`livelink_mcp_client.py` 行 287-309)
```python
async def spawn_object(self, prefab_key: str, position: list = None, 
                      rotation: list = None, scale: list = None,
                      name: str = None, parent_uuid: str = None) -> dict
```
✅ 参数完全对应

---

### ✅ transform_object - 变换对象

**服务器定义** (`MCPToolHandler.cs` 行 168-178)
- `uuid` (string, 必需) - 对象 UUID
- `position` (array, 可选) - 世界坐标 [x, y, z]
- `rotation` (array, 可选) - 四元数 [x, y, z, w]
- `scale` (array, 可选) - 缩放 [x, y, z]

**客户端实现** (`livelink_mcp_client.py` 行 311-327)
```python
async def transform_object(self, uuid: str, position: list = None,
                          rotation: list = None, scale: list = None) -> dict
```
✅ 参数完全对应

---

### ✅ delete_object - 删除对象

**服务器定义** (`MCPToolHandler.cs` 行 179-184)
- `uuid` (string, 必需) - 对象 UUID

**客户端实现** (`livelink_mcp_client.py` 行 329-331)
```python
async def delete_object(self, uuid: str) -> dict
```
✅ 参数完全对应

---

### ✅ scene_dump - 场景快照

**服务器定义** (`MCPToolHandler.cs` 行 185-190)
- `include_inactive` (boolean, 可选) - 是否包含非活跃对象

**客户端实现** (`livelink_mcp_client.py` 行 333-338)
```python
async def scene_dump(self, include_inactive: bool = False) -> dict
```
✅ 参数完全对应

---

### ✅ list_spawnable_objects - 列出可生成对象

**服务器定义** (`MCPToolHandler.cs` 行 191-196)
- 无参数

**客户端实现** (`livelink_mcp_client.py` 行 340-342)
```python
async def list_spawnable_objects(self) -> dict
```
✅ 参数完全对应

---

### ✅ get_view_context - 获取视图上下文

**服务器定义** (`MCPToolHandler.cs` 行 197-207)
- `camera_tag` (string, 可选) - 相机标签，默认 'MainCamera'
- `include_visible_objects` (boolean, 可选) - 包含相机视锥内的对象
- `raycast_distance` (number, 可选) - 光线投射距离，默认 100

**客户端实现** (`livelink_mcp_client.py` 行 344-359)
```python
async def get_view_context(self, camera_tag: str = "MainCamera",
                          include_visible_objects: bool = False,
                          raycast_distance: float = 100) -> dict
```
✅ 参数完全对应

---

### ✅ spawn_gltf - 生成 glTF 资源

**服务器定义** (`MCPToolHandler.cs` 行 208-225)
- `url` (string, 可选) - glTF/glb 文件 URL 或 file:// 路径
- `data_base64` (string, 可选) - Base64 编码的 glb 二进制数据
- `source_uri` (string, 可选) - 原始 URI（用于解析相对引用）
- `id` (string, 可选) - 指定生成根对象的 UUID
- `name` (string, 可选) - 生成根对象的名称
- `position` (array, 可选) - 世界坐标 [x, y, z]
- `rotation` (array, 可选) - 四元数 [x, y, z, w]
- `scale` (array, 可选) - 缩放 [x, y, z]
- `parent_uuid` (string, 可选) - 父对象 UUID

**客户端实现** (`livelink_mcp_client.py` 行 361-399)
```python
async def spawn_gltf(self, url: str = None, data_base64: str = None,
                    source_uri: str = None, id: str = None,
                    name: str = None, position: list = None,
                    rotation: list = None, scale: list = None,
                    parent_uuid: str = None) -> dict
```
✅ 参数完全对应

---

## Resources（资源）覆盖

### ✅ unity://scene/active - 活跃场景信息

**服务器定义** (`MCPResourceProvider.cs` 行 45-68)
返回 `SceneInfoDTO`：
- SceneName, ScenePath, IsLoaded, IsDirty
- RootCount, ObjectCount
- RenderPipeline, TimeScale, GameTime, RealTime, FrameCount
- QualityLevel, Platform, UnityVersion

**客户端实现** (`livelink_mcp_client.py` 行 403-407)
```python
async def get_scene_info(self) -> dict
```
✅ 资源完全对应

---

### ✅ unity://scene/hierarchy - 场景层级树

**服务器定义** (`MCPResourceProvider.cs` 行 52-56)
- 查询参数 `root` - 根路径，默认 '/'
- 查询参数 `depth` - 最大深度，默认 2，范围 1-50
返回 `HierarchyNodeDTO` 的树结构

**客户端实现** (`livelink_mcp_client.py` 行 409-419)
```python
async def get_scene_hierarchy(self, root: str = "/", depth: int = 2) -> dict
```
✅ 参数完全对应

---

### ✅ unity://selection - 当前选择

**服务器定义** (`MCPResourceProvider.cs` 行 80-85)
返回当前在 Unity 编辑器中选择的对象

**客户端实现** (`livelink_mcp_client.py` 行 421-423)
```python
async def get_selection(self) -> dict
```
✅ 资源完全对应

---

### ✅ unity://events/recent - 最近事件

**服务器定义** (`MCPResourceProvider.cs` 行 87-91)
- 查询参数 `count` - 最近事件数量，默认 50
返回最近的场景事件（创建、删除、属性变更等）

**客户端实现** (`livelink_mcp_client.py` 行 425-433)
```python
async def get_recent_events(self, count: int = 50) -> dict
```
✅ 参数完全对应

---

### ✅ unity://go/{instanceId} - GameObject 元数据

**服务器定义** (`MCPResourceProvider.cs` 行 59-65)
返回 `GameObjectMetadataDTO`：
- InstanceId, Name, Active, Layer, Tag, IsStatic
- ParentId, Children (instanceId 数组)
- ComponentCount, CreatedTime, ModifiedTime

**客户端实现** (`livelink_mcp_client.py` 行 436-441)
```python
async def get_gameobject(self, instance_id: int) -> dict
```
**注意**：参数使用 `instance_id` (Unity 内部 ID)，不是 UUID

✅ 资源完全对应，但注意参数类型

---

### ✅ unity://go/{instanceId}/components - GameObject 组件列表

**服务器定义** (`MCPResourceProvider.cs` 行 66-72)
返回 GameObject 上所有组件的列表

**客户端实现** (`livelink_mcp_client.py` 行 443-449)
```python
async def get_gameobject_components(self, instance_id: int) -> dict
```
✅ 资源完全对应

---

### ✅ unity://component/{instanceId}/{componentType} - 组件快照

**服务器定义** (`MCPResourceProvider.cs` 行 73-79)
返回特定组件的公开字段和属性快照

**客户端实现** (`livelink_mcp_client.py` 行 451-458)
```python
async def get_component(self, instance_id: int, component_type: str) -> dict
```
**参数**：
- `instance_id` - 组件的 InstanceId
- `component_type` - 组件类型名 (e.g., 'Transform', 'MeshRenderer')

✅ 资源完全对应

---

## Prompts（提示）覆盖

### ✅ scene_analysis - 场景分析工作流

**服务器定义** (`MCPToolHandler.cs` 行 380-395)
- `analysis_goal` (string, 可选) - 优化/检查目标
- `include_inactive` (boolean, 可选) - 包含非活跃对象
- `focus_query` (string, 可选) - 关键词过滤

**客户端实现** (`livelink_mcp_client.py` 行 461-475)
```python
async def run_scene_analysis(self, analysis_goal: str = None,
                            include_inactive: bool = False,
                            focus_query: str = None) -> dict
```
✅ 参数完全对应

---

### ✅ spawn_from_intent - 意图生成工作流

**服务器定义** (`MCPToolHandler.cs` 行 396-408)
- `intent` (string, 必需) - 设计意图描述
- `count` (int, 可选) - 生成对象目标数量
- `placement_strategy` (string, 可选) - 放置策略

**客户端实现** (`livelink_mcp_client.py` 行 477-489)
```python
async def run_spawn_from_intent(self, intent: str, count: int = None,
                               placement_strategy: str = None) -> dict
```
✅ 参数完全对应

---

### ✅ object_repair - 对象修复工作流

**服务器定义** (`MCPToolHandler.cs` 行 409-421)
- `uuid` (string, 必需) - 目标对象 UUID
- `issue_description` (string, 可选) - 问题描述
- `preserve_world_pose` (boolean, 可选) - 重新父化时保持世界空间坐标

**客户端实现** (`livelink_mcp_client.py` 行 491-504)
```python
async def run_object_repair(self, uuid: str, issue_description: str = None,
                           preserve_world_pose: bool = False) -> dict
```
✅ 参数完全对应

---

### ✅ scene_cleanup - 场景清理工作流

**服务器定义** (`MCPToolHandler.cs` 行 422-435)
- `scope` (string, 可选) - 清理范围：all, inactive_only, 或 name_pattern
- `name_pattern` (string, 可选) - 名称匹配模式
- `dry_run` (boolean, 可选) - 仅返回计划不执行

**客户端实现** (`livelink_mcp_client.py` 行 506-521)
```python
async def run_scene_cleanup(self, scope: str = None,
                           name_pattern: str = None,
                           dry_run: bool = False) -> dict
```
✅ 参数完全对应

---

## 总体覆盖统计

| 类别 | 数量 | 覆盖 | 状态 |
|------|------|------|------|
| **Tools** | 7 | 7 | ✅ 100% |
| **Resources** | 7 | 7 | ✅ 100% |
| **Prompts** | 4 | 4 | ✅ 100% |
| **总计** | **18** | **18** | **✅ 完整** |

---

## 关键注意事项

1. **UUID vs InstanceId**
   - Tools 中使用 `uuid` (字符串)
   - Resources 中 `unity://go/{instanceId}` 使用 `instanceId` (整数)

2. **参数数据类型**
   - 位置/缩放：[x, y, z] 格式的数字数组
   - 旋转：四元数 [x, y, z, w] 格式

3. **可选参数处理**
   - 服务器接受 null/缺失的可选参数
   - Python 客户端使用 `None` 作为默认值

4. **资源查询参数**
   - 使用 URI 查询字符串格式：`uri?param1=value1&param2=value2`
   - 客户端自动拼接

---

## 依赖关系

- `spawn_object` → 返回 UUID，可用于后续 transform/delete
- Prompts → 可内部调用 Tools 和读取 Resources
- Resources → GameObject 操作前需要获取 InstanceId
