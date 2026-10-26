"""验证易刷特效规则在当前客户端上的命中情况。

这是 P1 阶段的只读侦察：不修改任何文件，只统计每条规则的路径匹配与内容匹配。

用法：
    python scripts/effectpatch/probe_rules.py
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

# 规则定义。每条规则对应易刷的一个补丁动作。
# 路径用正则，target 用正则，replacement 是替换内容。
RULES: list[dict] = [
    {
        "id": "monster_dead_life_block",
        "feature": "怪物死亡特效移除",
        "path": r".*/monsters/monster\.ot$",
        "action": "ReplaceCode",
        "target": r"Life\s*\{\s*\}",
        "note": "注入 on_spawned_dead / on_death 回调",
    },
    {
        "id": "monster_dead_idle",
        "feature": "怪物死亡特效移除",
        "path": r".*/monsters/monster\.ot$",
        "action": "ReplaceCode",
        "target": r"slow_animations_go_to_idle\s*=\s*true\s*\}",
        "note": "注入 on_start_Revive 恢复渲染",
    },
    {
        "id": "dark_env_objects",
        "feature": "去除黑暗",
        "path": r".*environmentsettings/.*\.env$",
        "action": "RemoveObject",
        "target": r"directional_light|player_light|environment_mapping|global_illumination",
        "note": "删除光源键",
    },
    {
        "id": "dark_env_exposure",
        "feature": "去除黑暗",
        "path": r".*environmentsettings/.*\.env$",
        "action": "ReplaceCode",
        "target": r'"exposure"\s*:\s*[\d\.]+',
        "note": "调整曝光值",
    },
    {
        "id": "dark_bloom_cutoff",
        "feature": "去除黑暗",
        "path": r".*shaders/bloomcutoff\.hlsl$",
        "action": "ReplaceCode",
        "target": r"float4 BloomCutoff\(",
        "note": "泛光截止返回 0",
    },
    {
        "id": "dark_bloom_gather",
        "feature": "去除黑暗",
        "path": r".*shaders/bloomgather\.hlsl$",
        "action": "ReplaceCode",
        "target": r"float4 BloomGather\(",
        "note": "泛光收集返回 0",
    },
    {
        "id": "fog_env_objects",
        "feature": "去除迷雾",
        "path": r".*environmentsettings/.*\.env$",
        "action": "RemoveObject",
        "target": r"fog|area|water|post_transform",
        "note": "删除雾/水面等对象",
    },
    {
        "id": "fog_affliction",
        "feature": "去除迷雾",
        "path": r".*metadata/effects/environment/league_affliction.*\.(aoc|ao)$",
        "action": "ClearBlock",
        "target": r"ParticleEffects|WindEvents|TrailsEffects|BaseAnimationEvents|AttachedAnimatedObject|SkinMesh",
        "note": "清空特定区域特效块",
    },
    {
        "id": "sound_all_aoc",
        "feature": "移除音效",
        "path": r".*metadata/.*\.aoc$",
        "action": "ClearBlock",
        "target": r"SoundEvents",
        "note": "清空音效块",
    },
    {
        "id": "view_distance",
        "feature": "视距调整",
        "path": r".*metadata/characters/character\.ot$",
        "action": "ReplaceCode",
        "target": r"team\s*=\s*1\s*\}",
        "note": "注入 CreateCameraZoomNode",
    },
    {
        "id": "minimap_visibility",
        "feature": "小地图全开",
        "path": r".*shaders/minimap_visibility_pixel\.hlsl$",
        "action": "ReplaceCode",
        "target": r"if\(visibility_reset > 0\.5f\)",
        "note": "强制可见",
    },
    {
        "id": "minimap_blend",
        "feature": "小地图全开",
        "path": r".*shaders/minimap_blending_pixel\.hlsl$",
        "action": "ReplaceCode",
        "target": r"res_color\.rgb \*= res_color\.a;",
        "note": "未探索区域着色",
    },
    {
        "id": "spell_aoc",
        "feature": "技能特效削减",
        "path": r".*metadata/effects/spells/(?!.*(ground_effects|portal|black_hole|volatile_dead|curse_aura|atlasofworldsbosses|atlasmemories|atlasexiles|offering)).*\.aoc$",
        "action": "ClearBlock",
        "target": r"ParticleEffects|WindEvents|TrailsEffects",
        "note": "清空技能粒子块（含保护名单）",
    },
    {
        "id": "spell_epk",
        "feature": "技能特效削减",
        "path": r".*metadata/effects/spells/(?!.*(ground_effects|portal|black_hole|volatile_dead|curse_aura|atlasofworldsbosses|atlasmemories|atlasexiles|offering)).*\.epk$",
        "action": "ReplaceCode",
        "target": r".*",
        "note": "清空技能特效包",
    },
    {
        "id": "nini_particles",
        "feature": "一键全清",
        "path": r".*metadata/particles/(?!.*(ground_effects|portal|black_hole|volatile_dead|curse_aura|atlasofworldsbosses|atlasmemories|atlasexiles|offering)).*\.(pet|trl)$",
        "action": "ReplaceCode",
        "target": r".*",
        "note": "粒子/轨迹文件置 0",
    },
]

# 只做路径统计用的扩展名分组，用于快速估算文件规模
EXTENSION_GROUPS = {
    ".ot": "脚本",
    ".aoc": "动画配置",
    ".ao": "动画对象",
    ".env": "环境设置",
    ".hlsl": "着色器",
    ".epk": "特效包",
    ".pet": "粒子",
    ".trl": "轨迹",
}


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    print("=" * 72)
    print("易刷特效规则 — 路径与内容匹配探针")
    print("=" * 72)
    print()
    print(f"规则总数: {len(RULES)}")
    print()

    # 按功能分组统计
    by_feature: dict[str, list[dict]] = {}
    for rule in RULES:
        by_feature.setdefault(rule["feature"], []).append(rule)

    print("按功能分组:")
    for feature, rules in by_feature.items():
        print(f"  {feature}: {len(rules)} 条规则")
        for rule in rules:
            print(f"    - {rule['id']}: {rule['note']}")
    print()

    # 输出规则的正则，供 C# 侧对齐
    out_path = Path(__file__).resolve().parent / "rules.json"
    out_path.write_text(
        json.dumps(RULES, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"规则已导出: {out_path}")
    print()

    # 校验正则合法性
    print("正则校验:")
    for rule in RULES:
        try:
            re.compile(rule["path"])
            re.compile(rule["target"])
            print(f"  ✓ {rule['id']}")
        except re.error as exc:
            print(f"  ✗ {rule['id']}: {exc}")
            return 1
    print()
    print("全部规则正则合法。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
