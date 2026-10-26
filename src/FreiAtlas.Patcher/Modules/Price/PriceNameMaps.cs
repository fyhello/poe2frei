using System;
using System.Collections.Generic;

namespace FreiAtlas.Patcher.Modules.Price;

/// <summary>
/// 物价补丁使用的名称映射表。
///
/// 这些是纯数据：英文名到 Metadata 路径、英文名到中文名。
/// 与 PricePatchService 的解析、扫描、写入逻辑无关，
/// 单独成文件后可脱离 1500 行的服务类独立核对。
///
/// PricePatchService 上保留同名转发属性，既有调用点（含测试）无需改动。
/// </summary>
public static class PriceNameMaps
{
    public static readonly Dictionary<string, string> EnglishToMetadataMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mirror of Kalandra"] = "Metadata/Items/Currency/CurrencyDuplicate",
        ["Divine Orb"] = "Metadata/Items/Currency/CurrencyModValues",
        ["Exalted Orb"] = "Metadata/Items/Currency/CurrencyAddModToRare",
        ["Chaos Orb"] = "Metadata/Items/Currency/CurrencyRerollRare",
        ["Perfect Jeweller's Orb"] = "Metadata/Items/Currency/CurrencySocketCount6",
        ["Greater Jeweller's Orb"] = "Metadata/Items/Currency/CurrencySocketCount5",
        ["Jeweller's Orb"] = "Metadata/Items/Currency/CurrencyRerollSocketNumbers",
        ["Orb of Annulment"] = "Metadata/Items/Currency/CurrencyRemoveMod",
        ["Orb of Chance"] = "Metadata/Items/Currency/CurrencyUpgradeRandomly",
        ["Orb of Alchemy"] = "Metadata/Items/Currency/CurrencyUpgradeToRare",
        ["Regal Orb"] = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare",
        ["Vaal Orb"] = "Metadata/Items/Currency/CurrencyVaal",
        ["Gemcutter's Prism"] = "Metadata/Items/Currency/CurrencyGemQuality",
        ["Glassblower's Bauble"] = "Metadata/Items/Currency/CurrencyFlaskQuality",
        ["Blacksmith's Whetstone"] = "Metadata/Items/Currency/CurrencyWeaponQuality",
        ["Armourer's Scrap"] = "Metadata/Items/Currency/CurrencyArmourQuality",
        ["Orb of Transmutation"] = "Metadata/Items/Currency/CurrencyUpgradeToMagic",
        ["Orb of Augmentation"] = "Metadata/Items/Currency/CurrencyAddModToMagic",
        ["Orb of Alteration"] = "Metadata/Items/Currency/CurrencyRerollMagic",
        ["Scroll of Wisdom"] = "Metadata/Items/Currency/CurrencyIdentification",
        ["Portal Scroll"] = "Metadata/Items/Currency/CurrencyPortal",
        ["Artificer's Orb"] = "Metadata/Items/Currency/CurrencyItemRerollSocketColours",
        ["Lesser Jeweller's Orb"] = "Metadata/Items/Currency/CurrencySocketCount3",
        ["Ancient Orb"] = "Metadata/Items/Currency/CurrencyRerollUnique",
        ["Orb of Unmaking"] = "Metadata/Items/Currency/CurrencyPassiveRefund",
        ["Albino Rhoa Feather"] = "Metadata/Items/Currency/CurrencyAlbinoRhoaFeather",
        ["Fracturing Orb"] = "Metadata/Items/Currency/CurrencyFractureItem",
        ["Greater Iron Rune"] = "Metadata/Items/Runes/RuneGreaterIron",
        ["Iron Rune"] = "Metadata/Items/Runes/RuneIron",
        ["Desert Rune"] = "Metadata/Items/Runes/RuneDesert",
        ["Glacial Rune"] = "Metadata/Items/Runes/RuneGlacial",
        ["Storm Rune"] = "Metadata/Items/Runes/RuneStorm",
        ["Stone Rune"] = "Metadata/Items/Runes/RuneStone",
        ["Vision Rune"] = "Metadata/Items/Runes/RuneVision",
        ["Body Rune"] = "Metadata/Items/Runes/RuneBody",
        ["Mind Rune"] = "Metadata/Items/Runes/RuneMind",
        ["Rebirth Rune"] = "Metadata/Items/Runes/RuneRebirth",
        ["Inspiration Rune"] = "Metadata/Items/Runes/RuneInspiration"
    };

    public static readonly Dictionary<string, string> EnglishToChineseMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mirror of Kalandra"] = "卡兰德的魔镜",
        ["Divine Orb"] = "神圣石",
        ["Exalted Orb"] = "崇高石",
        ["Chaos Orb"] = "混沌石",
        ["Orb of Annulment"] = "无效石",
        ["Orb of Chance"] = "机会石",
        ["Orb of Alchemy"] = "点金石",
        ["Regal Orb"] = "富豪石",
        ["Vaal Orb"] = "瓦尔克石",
        ["Gemcutter's Prism"] = "宝石匠的棱镜",
        ["Glassblower's Bauble"] = "玻璃弹珠",
        ["Blacksmith's Whetstone"] = "磨刀石",
        ["Armourer's Scrap"] = "护甲片",
        ["Orb of Transmutation"] = "蜕变石",
        ["Orb of Augmentation"] = "增幅石",
        ["Orb of Alteration"] = "改造石",
        ["Ancient Orb"] = "远古石",
        ["Ancient Infuser"] = "远古灌注器",
        ["Greater Iron Rune"] = "高级铁符文",
        ["Iron Rune"] = "铁符文",
        ["Uncut Skill Gem"] = "未切割的技能宝石",
        ["Uncut Support Gem"] = "未切割的辅助宝石",
        ["Uncut Spirit Gem"] = "未切割的精魂宝石"
    };
}
