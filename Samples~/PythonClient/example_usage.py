#!/usr/bin/env python3
"""
Unity LiveLink MCP Client - 完整使用示例

演示所有 Tools、Resources 和 Prompts 的综合使用。
"""

import asyncio
from livelink_mcp_client import LiveLinkMCPClient


async def example_tools():
    """演示所有 Tools（工具）"""
    print("=" * 60)
    print("TOOLS（工具）示例")
    print("=" * 60)
    
    client = LiveLinkMCPClient(transport="http", host="localhost", port=3001)
    await client.connect()
    
    # 1. spawn_object
    print("\n[1] 生成对象 - spawn_object")
    result = await client.spawn_object(
        prefab_key="Cube",
        position=[0, 2, 0],
        rotation=[0, 0, 0, 1],  # 四元数 [x, y, z, w]
        scale=[1, 1, 1],
        name="MyTestCube"
    )
    cube_uuid = result.get("result", {}).get("content", [{}])[1].get("data", {}).get("uuid")
    print(f"✓ 生成立方体: {cube_uuid}")
    
    # 2. transform_object
    print("\n[2] 变换对象 - transform_object")
    await client.transform_object(
        uuid=cube_uuid,
        position=[0, 5, 0],
        scale=[2, 2, 2]
    )
    print(f"✓ 移动和缩放立方体")
    
    # 3. scene_dump
    print("\n[3] 场景快照 - scene_dump")
    result = await client.scene_dump(include_inactive=False)
    print(f"✓ 获取场景快照")
    
    # 4. list_spawnable_objects
    print("\n[4] 列出可生成对象 - list_spawnable_objects")
    result = await client.list_spawnable_objects()
    print(f"✓ 获取可生成的预制体列表")
    
    # 5. get_view_context
    print("\n[5] 获取视图上下文 - get_view_context")
    result = await client.get_view_context(
        camera_tag="MainCamera",
        include_visible_objects=True,
        raycast_distance=100
    )
    print(f"✓ 获取相机视图上下文")
    
    # 6. delete_object
    print("\n[6] 删除对象 - delete_object")
    await client.delete_object(uuid=cube_uuid)
    print(f"✓ 删除立方体")
    
    await client.disconnect()


async def example_resources():
    """演示所有 Resources（资源）"""
    print("\n" + "=" * 60)
    print("RESOURCES（资源）示例")
    print("=" * 60)
    
    client = LiveLinkMCPClient(transport="http", host="localhost", port=3001)
    await client.connect()
    
    # 1. unity://scene/active
    print("\n[1] 获取活跃场景 - unity://scene/active")
    result = await client.get_scene_info()
    scene_data = result.get("result", {}).get("data", {})
    print(f"✓ 场景名称: {scene_data.get('scene_name')}")
    print(f"  对象总数: {scene_data.get('object_count')}")
    
    # 2. unity://scene/hierarchy
    print("\n[2] 获取场景层级 - unity://scene/hierarchy")
    result = await client.get_scene_hierarchy(root="/", depth=2)
    hierarchy = result.get("result", {}).get("data", [])
    print(f"✓ 获取场景层级树（深度 2）")
    if hierarchy:
        print(f"  根节点数: {len(hierarchy)}")
    
    # 3. unity://selection
    print("\n[3] 获取当前选择 - unity://selection")
    result = await client.get_selection()
    print(f"✓ 获取编辑器中选择的对象")
    
    # 4. unity://events/recent
    print("\n[4] 获取最近事件 - unity://events/recent")
    result = await client.get_recent_events(count=10)
    print(f"✓ 获取最近 10 个场景事件")
    
    # 示例：获取 GameObject (需要 InstanceId)
    # 在实际使用中，可以从 hierarchy 或其他来源获得 InstanceId
    # result = await client.get_gameobject(instance_id=12345)
    # result = await client.get_gameobject_components(instance_id=12345)
    # result = await client.get_component(instance_id=12345, component_type="Transform")
    
    await client.disconnect()


async def example_prompts():
    """演示所有 Prompts（提示工作流）"""
    print("\n" + "=" * 60)
    print("PROMPTS（提示工作流）示例")
    print("=" * 60)
    
    client = LiveLinkMCPClient(transport="http", host="localhost", port=3001)
    await client.connect()
    
    # 1. scene_analysis
    print("\n[1] 场景分析 - scene_analysis")
    result = await client.run_scene_analysis(
        analysis_goal="Find performance improvements",
        include_inactive=False
    )
    print(f"✓ 运行场景分析工作流")
    
    # 2. spawn_from_intent
    print("\n[2] 意图生成 - spawn_from_intent")
    result = await client.run_spawn_from_intent(
        intent="Create 3 cubes in a row",
        count=3,
        placement_strategy="grid"
    )
    print(f"✓ 从设计意图生成对象")
    
    # 3. scene_cleanup (dry_run=True 安全演示)
    print("\n[3] 场景清理 - scene_cleanup (dry_run)")
    result = await client.run_scene_cleanup(
        scope="inactive_only",
        dry_run=True  # 仅返回计划，不执行删除
    )
    print(f"✓ 运行场景清理工作流（计划模式）")
    
    # 4. object_repair
    print("\n[4] 对象修复 - object_repair")
    # 需要一个真实的对象 UUID
    result = await client.run_object_repair(
        uuid="some-object-uuid",
        issue_description="Object seems misplaced",
        preserve_world_pose=True
    )
    print(f"✓ 运行对象修复工作流")
    
    await client.disconnect()


async def example_combined_workflow():
    """演示综合工作流：创建场景 → 分析 → 优化"""
    print("\n" + "=" * 60)
    print("综合工作流示例：场景构建与分析")
    print("=" * 60)
    
    client = LiveLinkMCPClient(transport="http", host="localhost", port=3001)
    await client.connect()
    
    print("\n步骤 1: 获取可用预制体")
    result = await client.list_spawnable_objects()
    print("✓ 已获取预制体列表")
    
    print("\n步骤 2: 生成多个对象")
    for i in range(3):
        result = await client.spawn_object(
            prefab_key="Cube" if i % 2 == 0 else "Sphere",
            position=[i * 2, 0, 0],
            name=f"Object_{i}"
        )
        print(f"✓ 生成对象 {i}")
    
    print("\n步骤 3: 获取更新后的场景信息")
    scene = await client.get_scene_info()
    print(f"✓ 场景现在包含 {scene.get('result', {}).get('data', {}).get('object_count')} 个对象")
    
    print("\n步骤 4: 获取场景层级树")
    hierarchy = await client.get_scene_hierarchy(depth=3)
    print("✓ 已获取新的场景层级")
    
    print("\n步骤 5: 运行场景分析")
    analysis = await client.run_scene_analysis(
        analysis_goal="Check newly created objects",
        include_inactive=False
    )
    print("✓ 完成场景分析")
    
    await client.disconnect()


async def main():
    """运行所有示例"""
    try:
        # 选择要运行的示例
        print("Unity LiveLink MCP Client - 完整使用示例")
        print("-" * 60)
        print("1. Tools（工具）")
        print("2. Resources（资源）")
        print("3. Prompts（提示工作流）")
        print("4. 综合工作流")
        print("0. 运行所有")
        
        choice = input("\n选择要运行的示例 (0-4): ").strip()
        
        if choice == "1":
            await example_tools()
        elif choice == "2":
            await example_resources()
        elif choice == "3":
            await example_prompts()
        elif choice == "4":
            await example_combined_workflow()
        elif choice == "0":
            await example_tools()
            await example_resources()
            await example_prompts()
            await example_combined_workflow()
        else:
            print("无效选择")
    
    except KeyboardInterrupt:
        print("\n\n中断")
    except Exception as e:
        print(f"\n错误: {e}")


if __name__ == "__main__":
    asyncio.run(main())
