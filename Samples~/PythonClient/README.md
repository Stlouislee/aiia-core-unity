# Unity LiveLink Python Client

统一 Python 客户端，完整支持 Unity LiveLink MCP 服务器的所有能力。

## 安装

```bash
pip install -r requirements.txt
```

## 使用方法

### 交互模式（默认）

```bash
python livelink_mcp_client.py
```

### 运行自动化测试

```bash
python livelink_mcp_client.py --test
```

### 指定服务器

```bash
# HTTP+SSE 模式（默认端口 3001）
python livelink_mcp_client.py --host 192.168.1.100 --port 3001

# WebSocket 模式（默认端口 8080）
python livelink_mcp_client.py --transport ws --host 192.168.1.100 --port 8080
```

## 功能特性

- **双传输支持**: HTTP+SSE（推荐）和 WebSocket
- **完整 MCP 协议**: Tools、Resources、Prompts 全覆盖
- **7 个 Tools**: spawn_object, transform_object, delete_object, scene_dump, list_spawnable_objects, get_view_context, spawn_gltf
- **7 个 Resources**: Scene info/hierarchy, GameObject metadata/components, Component snapshot, Selection, Recent events
- **4 个 Prompts**: Scene analysis, Intent-to-spawn, Object repair, Scene cleanup
- **交互模式**: 实时测试
- **自动化测试**: 快速验证所有功能

## 交互命令

```
spawn cube       - 生成一个立方体
spawn sphere     - 生成一个球体
list spawn       - 列出所有可生成的对象
scene            - 获取当前场景信息
hierarchy        - 获取场景层级树
view             - 获取相机视图上下文
events           - 获取最近场景事件
dump             - 获取完整场景快照
quit             - 退出
```

## API 概览

### Core Tools（核心工具）

```python
# 生成对象
await client.spawn_object(
    prefab_key="Cube",
    position=[0, 2, 0],
    rotation=[0, 0, 0, 1],  # [x, y, z, w]
    scale=[1, 1, 1],
    name="MyCube",
    parent_uuid="optional_parent_uuid"
)

# 变换对象
await client.transform_object(
    uuid="object_uuid",
    position=[0, 5, 0],
    rotation=[0, 0, 0, 1],
    scale=[2, 2, 2]
)

# 删除对象
await client.delete_object(uuid="object_uuid")

# 获取完整场景快照
await client.scene_dump(include_inactive=True)

# 列出所有可生成的对象
await client.list_spawnable_objects()

# 获取相机视图上下文
await client.get_view_context(
    camera_tag="MainCamera",
    include_visible_objects=True,
    raycast_distance=100
)

# 生成 glTF 资源
await client.spawn_gltf(
    url="https://example.com/model.glb",
    position=[0, 2, 0],
    name="MyGLTFModel"
)
```

### Scene Resources（场景资源）

```python
# 获取活跃场景信息
scene_info = await client.get_scene_info()

# 获取场景层级树
hierarchy = await client.get_scene_hierarchy(root="/", depth=3)

# 获取当前选择
selection = await client.get_selection()

# 获取最近事件
events = await client.get_recent_events(count=50)
```

### GameObject Resources（游戏对象资源）

```python
# 获取 GameObject 元数据（需要 InstanceId）
go_data = await client.get_gameobject(instance_id=12345)

# 获取 GameObject 的所有组件
components = await client.get_gameobject_components(instance_id=12345)

# 获取特定组件的快照
component = await client.get_component(
    instance_id=12345,
    component_type="Transform"
)
```

### Prompt Workflows（提示工作流）

```python
# 场景分析工作流
analysis = await client.run_scene_analysis(
    analysis_goal="Find performance improvements",
    include_inactive=False
)

# 意图生成工作流
spawn_plan = await client.run_spawn_from_intent(
    intent="Create a tower of 5 cubes in a grid",
    count=5,
    placement_strategy="grid"
)

# 对象修复工作流
repair = await client.run_object_repair(
    uuid="object_uuid",
    issue_description="Object is overlapping with ground",
    preserve_world_pose=True
)

# 场景清理工作流
cleanup = await client.run_scene_cleanup(
    scope="inactive_only",
    dry_run=True  # 只返回计划，不执行
)
```

### 低级 API

```python
# 列出所有可用工具
tools = await client.list_tools()

# 列出所有可用资源
resources = await client.list_resources()

# 直接调用工具
result = await client.call_tool(
    name="spawn_object",
    arguments={"prefab_key": "Cube"}
)

# 直接读取资源
result = await client.read_resource(uri="unity://scene/active")

# 列出所有可用提示
prompts = await client.list_prompts()

# 直接调用提示
result = await client.get_prompt(
    name="scene_analysis",
    arguments={"analysis_goal": "optimization"}
)
```

## 覆盖对应

本客户端完整覆盖 Unity LiveLink MCP 服务器的所有能力：

| 类别 | 数量 | 覆盖 |
|------|------|------|
| Tools (工具) | 7 | ✅ 完整 |
| Resources (资源) | 7 | ✅ 完整 |
| Prompts (提示) | 4 | ✅ 完整 |

### Tools 列表

1. `spawn_object` - 生成预制体对象
2. `transform_object` - 变换对象（位置/旋转/缩放）
3. `delete_object` - 删除对象
4. `scene_dump` - 获取完整场景快照
5. `list_spawnable_objects` - 列出可生成的对象
6. `get_view_context` - 获取相机视图上下文
7. `spawn_gltf` - 生成 glTF 资源

### Resources 列表

1. `unity://scene/active` - 活跃场景信息
2. `unity://scene/hierarchy` - 场景层级树
3. `unity://go/{instanceId}` - GameObject 元数据
4. `unity://go/{instanceId}/components` - GameObject 组件列表
5. `unity://component/{instanceId}/{componentType}` - 组件快照
6. `unity://selection` - 当前选择对象
7. `unity://events/recent` - 最近场景事件

### Prompts 列表

1. `scene_analysis` - 分析场景并生成改进建议
2. `spawn_from_intent` - 将自然语言意图转化为对象生成步骤
3. `object_repair` - 诊断并修复对象问题
4. `scene_cleanup` - 生成场景清理计划

## 旧文件（已废弃）

以下文件已废弃，保留仅供參考：
- `livelink_client.py` - 旧版 WebSocket 客户端
- `mcp_client_test.py` - 旧版 MCP WebSocket 测试
- `mcp_http_client_test.py` - 旧版 MCP HTTP 测试

请使用新的统一客户端 `livelink_mcp_client.py`。
