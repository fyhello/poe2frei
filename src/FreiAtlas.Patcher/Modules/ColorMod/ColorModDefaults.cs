using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

#nullable enable

namespace FreiAtlas.Patcher.Modules.ColorMod;

public static class ColorModDefaults
{
    public static readonly byte[] ColorRed = new byte[3] { 227, 23, 13 };

    public static readonly byte[] ColorGreen = new byte[3] { 0, 255, 127 };

    public static readonly byte[] ColorBlue = new byte[3] { 14, 186, 255 };

    public static readonly byte[] ColorYellow = new byte[3] { 231, 179, 37 };

    public static readonly byte[] ColorPink = new byte[3] { 160, 32, 240 };

    public static readonly byte[] ColorGray = new byte[3] { 170, 158, 130 };

    public static readonly byte[] ColorNone = new byte[3];

    public static readonly string[] Groups = new string[4] { "通用", "地图", "石板", "终局地图" };

    public static readonly (string CsdFile, string Group)[] GroupCsdFiles = new (string, string)[10]
    {
        ("stat_descriptions.csd", "通用"),
        ("map_stat_descriptions.csd", "地图"),
        ("atlas_stat_descriptions.csd", "地图"),
        ("tablet_stat_descriptions.csd", "石板"),
        ("expedition_relic_stat_descriptions.csd", "石板"),
        ("sanctum_relic_stat_descriptions.csd", "石板"),
        ("chest_stat_descriptions.csd", "地图"),
        ("primordial_altar_stat_descriptions.csd", "地图"),
        ("monster_stat_descriptions.csd", "地图"),
        ("endgame_map_stat_descriptions.csd", "终局地图")
    };

    public static readonly (string Prefix, string Group)[] PrefixGroupMap = new (string, string)[3]
    {
        ("map_", "地图"),
        ("expedition_", "石板"),
        ("tablet_", "石板")
    };

    public static readonly ColorModStyleOption[] StyleOptions = new ColorModStyleOption[3]
    {
        new ColorModStyleOption(ColorModStyle.Whole, "整句染色"),
        new ColorModStyleOption(ColorModStyle.NumberOnly, "仅数字染色"),
        new ColorModStyleOption(ColorModStyle.FollowGame, "跟随游戏原版")
    };

    public static readonly Dictionary<string, byte[]> DefaultColorMap;

    public static readonly Dictionary<string, ColorModStyle> DefaultStyleMap;

    public static readonly Regex NumberExprRe;

    public static readonly Regex[] FollowGamePatterns;

    public static ColorModStyle DefaultStyleFor(string displayName)
    {
        if (!NumberExprRe.IsMatch(displayName ?? ""))
        {
            return ColorModStyle.Whole;
        }
        return ColorModStyle.NumberOnly;
    }

    public static List<(int S, int E)> FollowGameSpans(string text)
    {
        List<(int, int)> list = new List<(int, int)>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }
        Regex[] followGamePatterns = FollowGamePatterns;
        for (int i = 0; i < followGamePatterns.Length; i++)
        {
            foreach (Match item in followGamePatterns[i].Matches(text))
            {
                list.Add((item.Index, item.Index + item.Length));
            }
        }
        if (list.Count == 0)
        {
            return list;
        }
        list.Sort();
        List<(int, int)> list2 = new List<(int, int)> { list[0] };
        foreach (var item2 in list.Skip(1))
        {
            (int, int) tuple = list2[list2.Count - 1];
            if (item2.Item1 <= tuple.Item2)
            {
                list2[list2.Count - 1] = (tuple.Item1, Math.Max(tuple.Item2, item2.Item2));
            }
            else
            {
                list2.Add(item2);
            }
        }
        return list2;
    }


    public static string DetermineGroup(string statId, string csdGroup)
    {
        (string, string)[] prefixGroupMap = PrefixGroupMap;
        for (int i = 0; i < prefixGroupMap.Length; i++)
        {
            var (value, result) = prefixGroupMap[i];
            if (statId.StartsWith(value, StringComparison.Ordinal))
            {
                return result;
            }
        }
        return csdGroup;
    }

    public static string CleanDisplayText(string raw)
    {
        return CleanDisplayTextCore(raw).Trim();
    }

    public static string CleanDisplayTextCore(string raw)
    {
        return Regex.Replace(Regex.Replace(Regex.Replace(Regex.Replace(raw, "<[^>]+>", ""), "\\[[^\\|]*?\\|", "").Replace("]", ""), "%[\\u4e00-\\u9fff]+%", "").Replace("{{", "").Replace("}}", ""), "\\{[0-9](?::[^}]*)?\\}", "#").Replace("{}%", "#%").Replace("{}", "#");
    }

    public static List<CsdDescriptionBlock> ParseDescriptionBlocks(string text, string targetLang)
    {
        List<CsdDescriptionBlock> result = new List<CsdDescriptionBlock>();
        string[] array = text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.None);
        string[]? currentStatIds = null;
        int num = 0;
        string? defaultDisplay = null;
        byte[]? defaultColor = null;
        string defaultColorTag = "";
        string? targetDisplay = null;
        byte[]? targetColor = null;
        string targetColorTag = "";
        string? text2 = null;
        foreach (string text3 in array)
        {
            string text4 = text3.TrimStart('\t', ' ');
            if (text4.StartsWith("lang "))
            {
                string text5 = text4.Substring(5).TrimEnd(':', ' ', '"').Trim('"');
                if (text2 == null && currentStatIds != null)
                {
                    _ = defaultDisplay;
                }
                text2 = text5;
                num = 0;
            }
            else if (text4.StartsWith("description"))
            {
                FlushBlock();
                currentStatIds = null;
                num = 0;
                defaultDisplay = null;
                defaultColor = null;
                defaultColorTag = "";
                targetDisplay = null;
                targetColor = null;
                targetColorTag = "";
                text2 = null;
            }
            else
            {
                if (text4.StartsWith("no_description") || text4.StartsWith("include "))
                {
                    continue;
                }
                if (currentStatIds == null)
                {
                    string[] array2 = text4.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (array2.Length >= 2 && int.TryParse(array2[0], out var _))
                    {
                        currentStatIds = array2[1..];
                    }
                    continue;
                }
                Match match = Regex.Match(text3, "\"([^\"]{1,150})\"");
                if (!match.Success && num == 0)
                {
                    string[] array3 = text4.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (array3.Length != 0 && int.TryParse(array3[0], out var result3) && result3 > 0)
                    {
                        num = result3;
                        continue;
                    }
                }
                if (match.Success && currentStatIds != null)
                {
                    string text6 = CleanDisplayText(match.Groups[1].Value);
                    byte[]? array4 = null;
                    string text7 = "";
                    Match match2 = Regex.Match(text3, "<rgb\\((\\d+),(\\d+),(\\d+)\\)>");
                    if (match2.Success)
                    {
                        array4 = new byte[3]
                        {
                            byte.Parse(match2.Groups[1].Value),
                            byte.Parse(match2.Groups[2].Value),
                            byte.Parse(match2.Groups[3].Value)
                        };
                    }
                    else
                    {
                        Match match3 = Regex.Match(text3, "<([A-Z][A-Z0-9]*)>");
                        if (match3.Success)
                        {
                            text7 = match3.Groups[1].Value;
                            array4 = MapNamedColor(text7);
                        }
                    }
                    if (text2 == null)
                    {
                        if (defaultDisplay == null)
                        {
                            defaultDisplay = text6;
                            defaultColor = array4;
                            defaultColorTag = text7;
                        }
                    }
                    else if (targetLang != null && text2 == targetLang && targetDisplay == null)
                    {
                        targetDisplay = text6;
                        targetColor = array4;
                        targetColorTag = text7;
                    }
                }
                num--;
            }
        }
        FlushBlock();
        return result;
        void FlushBlock()
        {
            if (currentStatIds != null)
            {
                string? text8 = targetDisplay ?? defaultDisplay;
                byte[]? currentColor = targetColor ?? defaultColor;
                string currentColorTag = ((targetDisplay != null) ? targetColorTag : defaultColorTag);
                if (text8 != null)
                {
                    result.Add(new CsdDescriptionBlock(currentStatIds, text8, currentColor, currentColorTag));
                }
            }
        }
    }

    private static byte[]? MapNamedColor(string tag)
    {
        return tag switch
        {
            "DF1" => new byte[3] { 0, 255, 127 }, 
            "DF2" => new byte[3] { 0, 255, 127 }, 
            "DF3" => new byte[3] { 0, 255, 127 }, 
            "DF4" => new byte[3] { 0, 255, 127 }, 
            "AT1" => new byte[3] { 231, 179, 37 }, 
            "AT2" => new byte[3] { 231, 179, 37 }, 
            "AT3" => new byte[3] { 231, 179, 37 }, 
            "AT4" => new byte[3] { 231, 179, 37 }, 
            "LUK1" => new byte[3] { 170, 158, 130 }, 
            "LUK2" => new byte[3] { 170, 158, 130 }, 
            "LUK3" => new byte[3] { 170, 158, 130 }, 
            "LUK4" => new byte[3] { 170, 158, 130 }, 
            "DA1" => new byte[3] { 227, 23, 13 }, 
            "DA2" => new byte[3] { 227, 23, 13 }, 
            "DA3" => new byte[3] { 227, 23, 13 }, 
            "DA4" => new byte[3] { 227, 23, 13 }, 
            "SP1" => new byte[3] { 160, 32, 240 }, 
            "SP2" => new byte[3] { 160, 32, 240 }, 
            "SP3" => new byte[3] { 160, 32, 240 }, 
            "SP4" => new byte[3] { 160, 32, 240 }, 
            _ => null, 
        };
    }

    public static List<ColorModEntry> BuildEntriesFromBlocks(List<CsdDescriptionBlock> blocks, string csdGroup)
    {
        HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
        List<ColorModEntry> list = new List<ColorModEntry>();
        foreach (CsdDescriptionBlock block in blocks)
        {
            string[] statIds = block.StatIds;
            foreach (string text in statIds)
            {
                if (!hashSet.Contains(text))
                {
                    hashSet.Add(text);
                    string text2 = DetermineGroup(text, csdGroup);
                    // hasDef 为 false 时 value 必为 null；下方用三元表达式已避免取值，
                    // 这里只需让编译器知道它可空。
                    bool hasDef = DefaultColorMap.TryGetValue(text, out byte[]? value);
                    bool isDanger = hasDef && value is not null
                        && (value.SequenceEqual(ColorRed) || value.SequenceEqual(ColorPink));
                    list.Add(new ColorModEntry
                    {
                        StatId = text,
                        DisplayName = block.DisplayName,
                        Color = hasDef && value is not null ? value : ColorNone,
                        Group = text2,
                        Enabled = hasDef,
                        Style = DefaultStyleFor(block.DisplayName),
                        IsDanger = isDanger,
                        CurrentColor = block.CurrentColor,
                        CurrentColorTag = block.CurrentColorTag
                    });
                }
            }
        }
        return list;
    }

    static ColorModDefaults()
    {
        Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        dictionary["%_chance_to_duplicate_dropped_currency"] = new byte[3] { 0, 255, 127 };
        dictionary["%_chance_to_duplicate_dropped_divination_cards"] = new byte[3] { 0, 255, 127 };
        dictionary["%_chance_to_duplicate_dropped_maps"] = new byte[3] { 0, 255, 127 };
        dictionary["%_chance_to_duplicate_dropped_scarabs"] = new byte[3] { 231, 179, 37 };
        dictionary["map_expedition2_remnants_have_at_least_x_slots"] = new byte[3] { 227, 23, 13 };
        dictionary["map_logbook_has_at_least_1_expedition2_remnant_with_at_least_x_slots"] = new byte[3] { 227, 23, 13 };
        dictionary["add_endurance_charge_on_skill_hit_%"] = new byte[3] { 231, 179, 37 };
        dictionary["add_frenzy_charge_on_kill_%_chance"] = new byte[3] { 231, 179, 37 };
        dictionary["add_frenzy_charge_on_skill_hit_%"] = new byte[3] { 231, 179, 37 };
        dictionary["add_power_charge_on_critical_strike_%"] = new byte[3] { 231, 179, 37 };
        dictionary["add_power_charge_on_kill_%_chance"] = new byte[3] { 231, 179, 37 };
        dictionary["add_power_charge_on_skill_hit_%"] = new byte[3] { 231, 179, 37 };
        dictionary["add_x_grasping_vines_on_hit"] = new byte[3] { 160, 32, 240 };
        dictionary["additional_maximum_all_elemental_resistances_%"] = new byte[3] { 0, 255, 127 };
        dictionary["additional_maximum_all_resistances_%"] = new byte[3] { 0, 255, 127 };
        dictionary["additional_maximum_block_%"] = new byte[3] { 0, 255, 127 };
        dictionary["all_damage_can_freeze"] = new byte[3] { 227, 23, 13 };
        dictionary["all_damage_can_ignite"] = new byte[3] { 160, 32, 240 };
        dictionary["all_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["always_crit"] = new byte[3] { 160, 32, 240 };
        dictionary["always_freeze"] = new byte[3] { 227, 23, 13 };
        dictionary["attack_maximum_added_cold_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["attack_maximum_added_fire_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["attack_maximum_added_lightning_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["attack_maximum_added_physical_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["attack_minimum_added_cold_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["attack_minimum_added_fire_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["attack_minimum_added_lightning_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["attack_minimum_added_physical_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["attack_speed_+%_during_flask_effect"] = new byte[3] { 231, 179, 37 };
        dictionary["base_avoid_ignite_%"] = new byte[3] { 160, 32, 240 };
        dictionary["base_bleed_duration_+%"] = new byte[3] { 231, 179, 37 };
        dictionary["base_cannot_have_life_leeched_from"] = new byte[3] { 160, 32, 240 };
        dictionary["base_cannot_have_mana_leeched_from"] = new byte[3] { 160, 32, 240 };
        dictionary["base_chance_to_freeze_%"] = new byte[3] { 227, 23, 13 };
        dictionary["base_cooldown_speed_+%"] = new byte[3] { 231, 179, 37 };
        dictionary["base_damage_taken_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["base_maximum_chaos_damage_resistance_%"] = new byte[3] { 0, 255, 127 };
        dictionary["base_maximum_cold_damage_resistance_%"] = new byte[3] { 0, 255, 127 };
        dictionary["base_maximum_fire_damage_resistance_%"] = new byte[3] { 0, 255, 127 };
        dictionary["base_maximum_lightning_damage_resistance_%"] = new byte[3] { 0, 255, 127 };
        dictionary["base_minimum_endurance_charges"] = new byte[3] { 231, 179, 37 };
        dictionary["base_minimum_frenzy_charges"] = new byte[3] { 231, 179, 37 };
        dictionary["base_minimum_power_charges"] = new byte[3] { 231, 179, 37 };
        dictionary["base_number_of_crossbow_bolts"] = new byte[3] { 231, 179, 37 };
        dictionary["base_number_of_raging_spirits_allowed"] = new byte[3] { 231, 179, 37 };
        dictionary["base_number_of_skeletons_allowed"] = new byte[3] { 231, 179, 37 };
        dictionary["base_number_of_spectres_allowed"] = new byte[3] { 231, 179, 37 };
        dictionary["base_penetrate_elemental_resistances_%"] = new byte[3] { 227, 23, 13 };
        dictionary["blight_arc_tower_additional_chains"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_arc_tower_additional_repeats"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_empowering_tower_grant_%_chance_to_deal_double_damage"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_empowering_tower_grant_cast_speed_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_empowering_tower_grant_damage_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_fireball_tower_projectiles_nova"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_imbuing_tower_grant_critical_strike_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_imbuing_tower_grant_damage_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_lightningstorm_tower_area_of_effect_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_lightningstorm_tower_storms_on_enemies"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_scout_tower_additional_minions_+"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_seismic_tower_additional_cascades_+"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_seismic_tower_cascade_range_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_seismic_tower_stun_duration_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_shocknova_tower_shock_effect_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_shocknova_tower_shock_repeats_with_area_effect_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_stonegaze_tower_cooldown_recovery_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_summoning_tower_minion_life_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_summoning_tower_minions_summoned_+"] = new byte[3] { 170, 158, 130 };
        dictionary["blight_tower_damage_per_tower_type_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["bow_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["cannot_have_life_leeched_from"] = new byte[3] { 160, 32, 240 };
        dictionary["cast_speed_+%_during_flask_effect"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_awakened_sextant"] = new byte[3] { 0, 255, 127 };
        dictionary["chance_%_to_drop_additional_cleansing_currency"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_currency"] = new byte[3] { 170, 158, 130 };
        dictionary["chance_%_to_drop_additional_divination_cards"] = new byte[3] { 0, 255, 127 };
        dictionary["chance_%_to_drop_additional_divination_cards_currency_basic"] = new byte[3] { 0, 255, 127 };
        dictionary["chance_%_to_drop_additional_divination_cards_gives_other_divination_cards"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_divination_cards_unique_jewellery"] = new byte[3] { 0, 255, 127 };
        dictionary["chance_%_to_drop_additional_divine_orb"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_exalted_orb"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_grand_eldritch_ember"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_grand_eldritch_ichor"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_greater_eldritch_ember"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_greater_eldritch_ichor"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_maps"] = new byte[3] { 0, 255, 127 };
        dictionary["chance_%_to_drop_additional_scarab"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_abyss_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_beasts_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_blight_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_breach_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_divination_cards_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_elder_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_harbinger_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_legion_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_maps_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_metamorph_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_perandus_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_shaper_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_strongbox_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_sulphite_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_torment_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_scarab_uniques_gilded"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_%_to_drop_additional_tangled_currency"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_to_gain_endurance_charge_on_block_%"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_to_gain_endurance_charge_on_crit_%"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_to_gain_endurance_charge_on_hit_%_vs_bleeding_enemy"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_to_gain_frenzy_charge_on_block_attack_%"] = new byte[3] { 231, 179, 37 };
        dictionary["chance_to_gain_power_charge_when_block_%"] = new byte[3] { 231, 179, 37 };
        dictionary["chaos_resistance_+_while_using_flask"] = new byte[3] { 0, 255, 127 };
        dictionary["chaos_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["chaos_spell_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["chest_display_cast_random_curse"] = new byte[3] { 227, 23, 13 };
        dictionary["chest_display_caustic_clouds"] = new byte[3] { 227, 23, 13 };
        dictionary["chest_display_freeze"] = new byte[3] { 227, 23, 13 };
        dictionary["chest_display_ice_nova"] = new byte[3] { 227, 23, 13 };
        dictionary["chest_display_ignite"] = new byte[3] { 227, 23, 13 };
        dictionary["chest_drop_additional_normal_items_up_to"] = new byte[3] { 170, 158, 130 };
        dictionary["chest_drop_additional_number_of_uniques_of_drop_pool_types"] = new byte[3] { 170, 158, 130 };
        dictionary["chest_drop_additional_unique_maps"] = new byte[3] { 170, 158, 130 };
        dictionary["chest_drop_additional_unqiue_items"] = new byte[3] { 170, 158, 130 };
        dictionary["chest_item_quantity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["chest_item_quantity_+%_final_from_mod"] = new byte[3] { 170, 158, 130 };
        dictionary["chest_item_rarity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["chest_spawn_rogue_exiles"] = new byte[3] { 227, 23, 13 };
        dictionary["chill_attackers_for_4_seconds_on_block_%_chance"] = new byte[3] { 0, 255, 127 };
        dictionary["cold_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["cold_spell_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["consecrated_ground_on_death"] = new byte[3] { 227, 23, 13 };
        dictionary["corrupted_spell_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["create_enemy_meteor_daemon_on_flask_use_%_chance"] = new byte[3] { 227, 23, 13 };
        dictionary["curses_reflected_to_self"] = new byte[3] { 227, 23, 13 };
        dictionary["damage_+%_per_15_dex"] = new byte[3] { 231, 179, 37 };
        dictionary["damage_+%_per_15_int"] = new byte[3] { 231, 179, 37 };
        dictionary["damage_+%_per_15_strength"] = new byte[3] { 231, 179, 37 };
        dictionary["dexterity_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["display_altar_chaos_aura"] = new byte[3] { 160, 32, 240 };
        dictionary["display_map_has_oxygen"] = new byte[3] { 227, 23, 13 };
        dictionary["display_monster_spike_nova_on_death_text"] = new byte[3] { 227, 23, 13 };
        dictionary["drain_x_flask_charges_over_time_on_hit_for_6_seconds"] = new byte[3] { 160, 32, 240 };
        dictionary["dropped_items_are_fully_linked"] = new byte[3] { 170, 158, 130 };
        dictionary["dropped_items_have_additional_sockets"] = new byte[3] { 170, 158, 130 };
        dictionary["endurance_charge_on_kill_%"] = new byte[3] { 231, 179, 37 };
        dictionary["enemies_explode_for_%_life_as_physical_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["enemies_explode_on_death_by_attack_for_10%_life_as_physical_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["enemies_hitting_you_drop_burning_ground_%"] = new byte[3] { 227, 23, 13 };
        dictionary["energy_shield_recovery_rate_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["expedition_chest_currency_drop_quantity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["expedition_chest_logbook_chance_%"] = new byte[3] { 170, 158, 130 };
        dictionary["expedition_elite_%_to_drop_currency"] = new byte[3] { 170, 158, 130 };
        dictionary["expedition_elite_%_to_drop_maps"] = new byte[3] { 170, 158, 130 };
        dictionary["expedition_elite_%_to_drop_scarabs"] = new byte[3] { 170, 158, 130 };
        dictionary["expedition_elite_%_to_drop_stackeddecks"] = new byte[3] { 170, 158, 130 };
        dictionary["expedition_monsters_currency_drop_quantity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["expedition_monsters_logbook_chance_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["expedition_twinned_elites"] = new byte[3] { 0, 255, 127 };
        dictionary["explode_enemies_for_10%_life_as_physical_on_kill_chance_%"] = new byte[3] { 231, 179, 37 };
        dictionary["explode_enemies_for_25%_life_as_chaos_on_kill_chance_%"] = new byte[3] { 231, 179, 37 };
        dictionary["explode_enemies_for_500%_life_as_fire_on_kill_%_chance"] = new byte[3] { 231, 179, 37 };
        dictionary["explode_on_kill_%_chaos_damage_to_deal"] = new byte[3] { 231, 179, 37 };
        dictionary["explode_on_kill_%_fire_damage_to_deal"] = new byte[3] { 231, 179, 37 };
        dictionary["fire_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["fire_spell_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["flasks_%_chance_to_not_consume_charges"] = new byte[3] { 0, 255, 127 };
        dictionary["from_league_item_quantity_+%_permyriad"] = new byte[3] { 170, 158, 130 };
        dictionary["gain_elusive_on_crit_%_chance"] = new byte[3] { 0, 255, 127 };
        dictionary["gain_endurance_charge_per_second_if_have_been_hit_recently"] = new byte[3] { 231, 179, 37 };
        dictionary["gain_frenzy_charge_on_hitting_rare_or_unique_enemy_%"] = new byte[3] { 231, 179, 37 };
        dictionary["global_always_hit"] = new byte[3] { 160, 32, 240 };
        dictionary["heist_alert_level_gained_per_10_sec"] = new byte[3] { 227, 23, 13 };
        dictionary["heist_currency_alchemy_drops_as_divine_%"] = new byte[3] { 170, 158, 130 };
        dictionary["heist_currency_chaos_drops_as_divine_%"] = new byte[3] { 170, 158, 130 };
        dictionary["heist_lockdown_is_instant"] = new byte[3] { 227, 23, 13 };
        dictionary["heist_player_additional_maximum_resistances_%_per_25%_alert_level"] = new byte[3] { 227, 23, 13 };
        dictionary["heist_player_fire_resistance_%_per_25%_alert_level"] = new byte[3] { 227, 23, 13 };
        dictionary["heist_side_reward_room_monsters_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["hexproof"] = new byte[3] { 160, 32, 240 };
        dictionary["hits_ignore_enemy_monster_physical_damage_reduction_%_chance"] = new byte[3] { 227, 23, 13 };
        dictionary["immune_to_curses"] = new byte[3] { 160, 32, 240 };
        dictionary["intelligence_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["life_recovery_rate_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["lightning_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["lightning_spell_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_additional_charm_slots"] = new byte[3] { 0, 255, 127 };
        dictionary["local_charm_slots"] = new byte[3] { 0, 255, 127 };
        dictionary["local_critical_strike_chance"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_nearby_enemy_chaos_damage_resistance_%"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_nearby_enemy_cold_damage_resistance_%"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_nearby_enemy_elemental_damage_taken_+%"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_nearby_enemy_fire_damage_resistance_%"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_nearby_enemy_lightning_damage_resistance_%"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_nearby_enemy_physical_damage_taken_+%"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_socketed_attacks_additional_critical_strike_chance"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_socketed_attacks_critical_strike_multiplier_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_socketed_spells_additional_critical_strike_chance"] = new byte[3] { 231, 179, 37 };
        dictionary["local_display_socketed_spells_critical_strike_multiplier_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_explode_on_kill_with_crit_%_physical_damage_to_deal"] = new byte[3] { 231, 179, 37 };
        dictionary["local_flask_gain_X_charges_when_hit"] = new byte[3] { 0, 255, 127 };
        dictionary["local_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_maximum_added_chaos_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_maximum_added_cold_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_maximum_added_fire_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_maximum_added_lightning_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_maximum_added_physical_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_minimum_added_chaos_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_minimum_added_cold_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_minimum_added_fire_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_minimum_added_lightning_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_minimum_added_physical_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_active_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_active_skill_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_area_of_effect_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_area_of_effect_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_aura_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_aura_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_bow_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_bow_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_chaos_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_chaos_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_cold_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_cold_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_curse_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_dexterity_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_dexterity_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_duration_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_elemental_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_fire_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_fire_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_golem_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_herald_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_hex_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_intelligence_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_intelligence_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_lightning_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_lightning_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_melee_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_melee_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_minion_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_minion_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_movement_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_non_vaal_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_projectile_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_projectile_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_spell_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_strength_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_strength_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_support_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_support_gem_quality_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_trap_and_mine_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_trap_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_vaal_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["local_socketed_warcry_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["mana_%_gained_on_block"] = new byte[3] { 0, 255, 127 };
        dictionary["map_additional_player_maximum_resistances_%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_adds_X_extra_synthesis_mods"] = new byte[3] { 231, 179, 37 };
        dictionary["map_allow_shrines"] = new byte[3] { 227, 23, 13 };
        dictionary["map_always_has_weather"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ambush_chests"] = new byte[3] { 227, 23, 13 };
        dictionary["map_base_ground_desecration_damage_to_deal_per_minute"] = new byte[3] { 227, 23, 13 };
        dictionary["map_base_ground_fire_damage_to_deal_per_10_seconds"] = new byte[3] { 227, 23, 13 };
        dictionary["map_base_ground_fire_damage_to_deal_per_minute"] = new byte[3] { 227, 23, 13 };
        dictionary["map_beyond_rules"] = new byte[3] { 227, 23, 13 };
        dictionary["map_boss_additional_awakened_sextant_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_cleansing_currency_to_drop"] = new byte[3] { 231, 179, 37 };
        dictionary["map_boss_additional_currency_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_divination_cards_currency_basic_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_divination_cards_gives_other_divination_cards_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_divination_cards_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_divination_cards_unique_jewellery_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_divine_orb_to_drop"] = new byte[3] { 231, 179, 37 };
        dictionary["map_boss_additional_eldritch_exalted_orb_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_eldritch_orb_of_annulment_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_exalted_orb_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_grand_eldritch_ember_to_drop"] = new byte[3] { 231, 179, 37 };
        dictionary["map_boss_additional_grand_eldritch_ichor_to_drop"] = new byte[3] { 231, 179, 37 };
        dictionary["map_boss_additional_greater_eldritch_ember_to_drop"] = new byte[3] { 231, 179, 37 };
        dictionary["map_boss_additional_greater_eldritch_ichor_to_drop"] = new byte[3] { 231, 179, 37 };
        dictionary["map_boss_additional_maps_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_orb_of_annulment_to_drop"] = new byte[3] { 0, 255, 127 };
        dictionary["map_boss_additional_tangled_currency_to_drop"] = new byte[3] { 231, 179, 37 };
        dictionary["map_boss_area_of_effect_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_boss_attack_and_cast_speed_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_boss_damage_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_boss_damage_+%_final_from_boss_drops_guardian_map_sextant"] = new byte[3] { 227, 23, 13 };
        dictionary["map_boss_dropped_item_quantity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["map_boss_dropped_unique_items_+"] = new byte[3] { 170, 158, 130 };
        dictionary["map_boss_drops_additional_conqueror_map"] = new byte[3] { 170, 158, 130 };
        dictionary["map_boss_drops_additional_currency_items"] = new byte[3] { 170, 158, 130 };
        dictionary["map_boss_drops_additional_elder_guardian_map"] = new byte[3] { 170, 158, 130 };
        dictionary["map_boss_drops_additional_shaper_guardian_map"] = new byte[3] { 170, 158, 130 };
        dictionary["map_boss_is_possessed"] = new byte[3] { 227, 23, 13 };
        dictionary["map_boss_item_rarity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["map_boss_maximum_life_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_breach_rules"] = new byte[3] { 170, 158, 130 };
        dictionary["map_chest_item_quantity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["map_chest_item_rarity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["map_contains_cleansed_boss"] = new byte[3] { 170, 158, 130 };
        dictionary["map_contains_master"] = new byte[3] { 0, 255, 127 };
        dictionary["map_currency_divine_stack_4_permillage_chance"] = new byte[3] { 231, 179, 37 };
        dictionary["map_damage_+%_per_poison_stack"] = new byte[3] { 0, 255, 127 };
        dictionary["map_display_unique_boss_drops_X_maps"] = new byte[3] { 170, 158, 130 };
        dictionary["map_dropped_maps_are_corrupted_with_8_mods"] = new byte[3] { 0, 255, 127 };
        dictionary["map_duplicate_captured_beasts_chance_%"] = new byte[3] { 0, 255, 127 };
        dictionary["map_elemental_weakness_curse_zones"] = new byte[3] { 227, 23, 13 };
        dictionary["map_enfeeble_curse_zones"] = new byte[3] { 227, 23, 13 };
        dictionary["map_experience_gain_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["map_ground_consecrated"] = new byte[3] { 160, 32, 240 };
        dictionary["map_ground_consecrated_life_regeneration_rate_per_minute_%"] = new byte[3] { 160, 32, 240 };
        dictionary["map_ground_ice"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ground_ice_base_magnitude"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ground_lightning"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ground_lightning_base_magnitude"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ground_tar_movement_speed_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_harvest_double_lifeforce_dropped"] = new byte[3] { 231, 179, 37 };
        dictionary["map_humanoid_monster_potency_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["map_incursion_token_chance_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["map_invasion_monster_packs"] = new byte[3] { 227, 23, 13 };
        dictionary["map_is_unidentified"] = new byte[3] { 170, 158, 130 };
        dictionary["map_item_drop_quantity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["map_item_drop_rarity_+%"] = new byte[3] { 170, 158, 130 };
        dictionary["map_items_drop_corrupted_%"] = new byte[3] { 160, 32, 240 };
        dictionary["map_items_drop_corrupted_in_corrupted_maps_%_chance"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monster_drop_higher_level_gear"] = new byte[3] { 170, 158, 130 };
        dictionary["map_monster_item_rarity_+%_final"] = new byte[3] { 170, 158, 130 };
        dictionary["map_monster_melee_attacks_apply_random_curses"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monster_skills_chain_X_additional_times"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_%_physical_damage_to_convert_to_chaos"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_additional_number_of_projecitles"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_ailment_threshold_+%"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_always_ignite"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_are_hexproof"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_are_immune_to_curses"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_area_of_effect_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_attack_speed_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_avoid_ailments_%"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_avoid_elemental_ailments_%"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_avoid_ignite_%"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_avoid_poison_bleed_impale_%"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_cannot_be_leeched_from"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_cannot_be_stunned"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_cast_speed_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_chance_to_impale_%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_critical_strike_chance_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_critical_strike_multiplier_+"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_damage_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_drop_ground_fire_on_death_base_radius"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_have_onslaught"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_life_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_maximum_life_%_to_add_to_maximum_energy_shield"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_movement_speed_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_penetrate_elemental_resistances_%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_poison_on_hit"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_reflect_%_elemental_damage"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_reflect_%_physical_damage"] = new byte[3] { 160, 32, 240 };
        dictionary["map_monsters_reflect_curses"] = new byte[3] { 227, 23, 13 };
        dictionary["map_monsters_stun_threshold_+%"] = new byte[3] { 160, 32, 240 };
        dictionary["map_movement_velocity_+%_per_poison_stack"] = new byte[3] { 0, 255, 127 };
        dictionary["map_no_refills_in_town"] = new byte[3] { 227, 23, 13 };
        dictionary["map_num_extra_abysses"] = new byte[3] { 0, 255, 127 };
        dictionary["map_num_extra_invasion_bosses"] = new byte[3] { 227, 23, 13 };
        dictionary["map_num_extra_strongboxes"] = new byte[3] { 170, 158, 130 };
        dictionary["map_number_of_additional_mods"] = new byte[3] { 0, 255, 127 };
        dictionary["map_number_of_magic_packs_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["map_number_of_rare_packs_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["map_pack_size_+%"] = new byte[3] { 0, 255, 127 };
        dictionary["map_player_base_chaos_damage_taken_per_minute"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_buff_time_passed_+%_only_buff_category"] = new byte[3] { 160, 32, 240 };
        dictionary["map_player_create_enemy_meteor_daemon_on_flask_use_%_chance"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_blood_magic_keystone"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_chaos_inoculation_keystone"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_conductivity"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_despair"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_elemental_weakness"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_enfeeble"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_flammability"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_frostbite"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_punishment"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_silence"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_temporal_chains"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_level_X_vulnerability"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_has_random_level_X_curse_every_10_seconds"] = new byte[3] { 227, 23, 13 };
        dictionary["map_player_no_regeneration"] = new byte[3] { 160, 32, 240 };
        dictionary["map_players_additional_number_of_projectiles"] = new byte[3] { 227, 23, 13 };
        dictionary["map_players_and_monsters_curses_are_reflected"] = new byte[3] { 227, 23, 13 };
        dictionary["map_players_and_monsters_damage_taken_+%_while_stationary"] = new byte[3] { 227, 23, 13 };
        dictionary["map_players_and_monsters_have_resolute_technique"] = new byte[3] { 227, 23, 13 };
        dictionary["map_players_are_poisoned_while_moving_chaos_damage_per_second"] = new byte[3] { 227, 23, 13 };
        dictionary["map_players_cannot_gain_endurance_charges"] = new byte[3] { 160, 32, 240 };
        dictionary["map_players_cannot_gain_frenzy_charges"] = new byte[3] { 160, 32, 240 };
        dictionary["map_players_cannot_gain_power_charges"] = new byte[3] { 160, 32, 240 };
        dictionary["map_players_cannot_take_reflected_damage"] = new byte[3] { 0, 255, 127 };
        dictionary["map_players_gain_rampage_stacks"] = new byte[3] { 227, 23, 13 };
        dictionary["map_players_no_regeneration_including_es"] = new byte[3] { 160, 32, 240 };
        dictionary["map_players_resist_all_%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_possessed_monsters_drop_gilded_scarab_chance_%"] = new byte[3] { 0, 255, 127 };
        dictionary["map_regret_to_divine_%"] = new byte[3] { 231, 179, 37 };
        dictionary["map_ritual_additional_reward_rerolls"] = new byte[3] { 0, 255, 127 };
        dictionary["map_ritual_contains_divine_orbs"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_headhunter"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_kalandras_touch"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_mageblood"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_omen_chance"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_omen_dextral_annulment"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_omen_dextral_erasure"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_omen_sinistral_annulment"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_omen_sinistral_erasure"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_omen_whittling"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_perfect_chaos"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_contains_perfect_exalt"] = new byte[3] { 227, 23, 13 };
        dictionary["map_ritual_omen_chance_+%"] = new byte[3] { 231, 179, 37 };
        dictionary["map_spawn_affliction_mirror"] = new byte[3] { 227, 23, 13 };
        dictionary["map_spawn_betrayals"] = new byte[3] { 227, 23, 13 };
        dictionary["map_spawn_exile_per_area_%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_spawn_extra_exiles"] = new byte[3] { 227, 23, 13 };
        dictionary["map_spawn_harbingers"] = new byte[3] { 0, 255, 127 };
        dictionary["map_spawn_perandus_chests"] = new byte[3] { 0, 255, 127 };
        dictionary["map_spawn_talismans"] = new byte[3] { 0, 255, 127 };
        dictionary["map_spawn_tormented_spirits"] = new byte[3] { 227, 23, 13 };
        dictionary["map_spawn_two_bosses"] = new byte[3] { 170, 158, 130 };
        dictionary["map_strongbox_monsters_attack_speed_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_tempest_base_ground_desecration_damage_to_deal_per_minute"] = new byte[3] { 227, 23, 13 };
        dictionary["map_tempest_base_ground_fire_damage_to_deal_per_minute"] = new byte[3] { 227, 23, 13 };
        dictionary["map_tempest_ground_ice"] = new byte[3] { 227, 23, 13 };
        dictionary["map_tempest_ground_lightning"] = new byte[3] { 227, 23, 13 };
        dictionary["map_tempest_ground_tar_movement_speed_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_temporal_chains_curse_zones"] = new byte[3] { 227, 23, 13 };
        dictionary["map_unique_monster_num_additional_modifiers"] = new byte[3] { 231, 179, 37 };
        dictionary["map_unstable_breach_enrage_x_additional_rare_monsters"] = new byte[3] { 231, 179, 37 };
        dictionary["max_endurance_charges"] = new byte[3] { 231, 179, 37 };
        dictionary["max_frenzy_charges"] = new byte[3] { 231, 179, 37 };
        dictionary["max_power_charges"] = new byte[3] { 231, 179, 37 };
        dictionary["melee_attack_number_of_spirit_strikes"] = new byte[3] { 231, 179, 37 };
        dictionary["melee_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["minion_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["modifiers_to_map_item_drop_quantity_also_apply_to_map_item_drop_rarity"] = new byte[3] { 170, 158, 130 };
        dictionary["monster_base_block_%"] = new byte[3] { 160, 32, 240 };
        dictionary["monster_casts_bleed_nova_text"] = new byte[3] { 227, 23, 13 };
        dictionary["monster_grants_no_flask_charges"] = new byte[3] { 160, 32, 240 };
        dictionary["movement_speed_+%_during_flask_effect"] = new byte[3] { 0, 255, 127 };
        dictionary["never_take_critical_strike"] = new byte[3] { 160, 32, 240 };
        dictionary["num_charm_slots"] = new byte[3] { 0, 255, 127 };
        dictionary["number_of_additional_arrows"] = new byte[3] { 231, 179, 37 };
        dictionary["number_of_additional_curses_allowed"] = new byte[3] { 227, 23, 13 };
        dictionary["number_of_additional_totems_allowed"] = new byte[3] { 231, 179, 37 };
        dictionary["number_of_endurance_charges_to_gain_every_4_seconds_while_stationary"] = new byte[3] { 231, 179, 37 };
        dictionary["physical_damage_taken_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["physical_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["physical_spell_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["power_frenzy_or_endurance_charge_on_kill_%"] = new byte[3] { 231, 179, 37 };
        dictionary["projectile_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["quality_display_base_number_of_crossbow_bolts_is_gem"] = new byte[3] { 231, 179, 37 };
        dictionary["raise_spectre_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["raise_zombie_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["random_projectile_direction"] = new byte[3] { 160, 32, 240 };
        dictionary["recover_%_of_maximum_life_on_block"] = new byte[3] { 0, 255, 127 };
        dictionary["remove_%_of_current_energy_shield_on_hit"] = new byte[3] { 160, 32, 240 };
        dictionary["remove_%_of_current_mana_on_hit"] = new byte[3] { 160, 32, 240 };
        dictionary["remove_chill_and_freeze_on_flask_use"] = new byte[3] { 0, 255, 127 };
        dictionary["remove_ignite_and_burning_on_flask_use"] = new byte[3] { 0, 255, 127 };
        dictionary["remove_shock_on_flask_use"] = new byte[3] { 0, 255, 127 };
        dictionary["sanctum_merchant_additional_options"] = new byte[3] { 170, 158, 130 };
        dictionary["shock_attackers_for_4_seconds_on_block_%_chance"] = new byte[3] { 0, 255, 127 };
        dictionary["spell_additional_critical_strike_chance_permyriad"] = new byte[3] { 231, 179, 37 };
        dictionary["spell_maximum_added_cold_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["spell_maximum_added_fire_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["spell_maximum_added_lightning_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["spell_minimum_added_cold_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["spell_minimum_added_fire_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["spell_minimum_added_lightning_damage"] = new byte[3] { 231, 179, 37 };
        dictionary["spell_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["strength_skill_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["summon_skeleton_gem_level_+"] = new byte[3] { 231, 179, 37 };
        dictionary["tailwind_if_have_crit_recently"] = new byte[3] { 231, 179, 37 };
        dictionary["zombie_explode_on_kill_%_fire_damage_to_deal"] = new byte[3] { 231, 179, 37 };
        dictionary["map_spawn_extra_torment_spirits"] = new byte[3] { 227, 23, 13 };
        dictionary["maps_with_powerful_bosses_additional_spirit_+"] = new byte[3] { 227, 23, 13 };
        dictionary["map_tormented_spirit_chance_+%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_tormented_spirit_chance_%"] = new byte[3] { 227, 23, 13 };
        dictionary["map_boss_is_possessed"] = new byte[3] { 227, 23, 13 };
        dictionary["tamed_beasts_randomly_possessed_every_x_ms"] = new byte[3] { 227, 23, 13 };
        DefaultColorMap = dictionary;
        DefaultStyleMap = new Dictionary<string, ColorModStyle>(StringComparer.Ordinal)
        {
            ["map_spawn_extra_torment_spirits"] = ColorModStyle.Whole,
            ["maps_with_powerful_bosses_additional_spirit_+"] = ColorModStyle.Whole,
            ["map_tormented_spirit_chance_+%"] = ColorModStyle.Whole,
            ["map_tormented_spirit_chance_%"] = ColorModStyle.Whole,
            ["map_boss_is_possessed"] = ColorModStyle.Whole,
            ["tamed_beasts_randomly_possessed_every_x_ms"] = ColorModStyle.Whole,
            ["map_expedition2_remnants_have_at_least_x_slots"] = ColorModStyle.Whole,
            ["map_logbook_has_at_least_1_expedition2_remnant_with_at_least_x_slots"] = ColorModStyle.Whole
        };
        // 超时用于兜底灾难性回溯：NumberExprRe 含嵌套量词
        // (?:...)(?:...)*，输入异常长时正则引擎可能指数级回溯。
        // 正常文本只有几十个字符，2 秒足够，超时说明输入有问题。
        var regexTimeout = TimeSpan.FromSeconds(2);
        NumberExprRe = new Regex("(?:#|\\{[0-9][^}]*\\})(?:\\s*[-–—～~到至%·,，.。、/:：]?\\s*(?:#|\\{[0-9][^}]*\\}))*(?:%+|[:+][+\\-.:a-zA-Z0-9%]*)?", RegexOptions.Compiled, regexTimeout);
        FollowGamePatterns = new Regex[5]
        {
            NumberExprRe,
            new Regex("\\d+", RegexOptions.Compiled, regexTimeout),
            new Regex("【[^】]*】", RegexOptions.Compiled, regexTimeout),
            new Regex("\\[[^\\]|]*\\|?[^\\]]*\\]", RegexOptions.Compiled, regexTimeout),
            new Regex("额外|額外|附加|必定|无法|無法|不能|不再|随机|隨機|期间|期間|几率|機率|阈值|閾值|上限|下限|恢复|恢復|减少|減少|提高|降低|所有|总是|總是|賦予|赋予", RegexOptions.Compiled, regexTimeout)
        };
    }
}

