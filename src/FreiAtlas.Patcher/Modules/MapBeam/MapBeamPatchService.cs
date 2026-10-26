using System.Buffers.Binary;
using System.Text;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Modules.MapBeam;

/// <summary>
/// 异界神图掉落彩色光柱配置项
/// </summary>
public sealed record MapBeamTarget(string MapName, string DisplayName, string Color = "Farudin", bool Enabled = true);

/// <summary>
/// 光柱色彩定义
/// </summary>
public sealed record MapBeamColorDef(string Key, string Label, string PrimaryMat, string SecondaryMat, string HexColor);

/// <summary>
/// 异界神图掉落醒目光柱客户端补丁服务
/// </summary>
public sealed class MapBeamPatchService
{
    private readonly PatchEngine _engine;

    public static readonly MapBeamColorDef[] Colors =
    [
        new("Vaal", "瓦尔红", "lightbeam_Vaal.mat", "lightbeam_Vaal_Secondary.mat", "#E74C3C"),
        new("Red", "纯红", "lightbeam_Red.mat", "lightbeam_Red.mat", "#C0392B"),
        new("Green", "翠绿", "lightbeam_Green.mat", "lightbeam_Green.mat", "#4C9A5F"),
        new("Yellow", "金黄", "lightbeam_Yellow.mat", "lightbeam_Yellow.mat", "#E0B33C"),
        new("Ezomyte", "艾泽米特蓝", "lightbeam_Ezomyte.mat", "lightbeam_Ezomyte_Secondary.mat", "#5FA8D9"),
        new("Farudin", "法鲁丁金", "lightbeam_Farudin.mat", "lightbeam_Farudin_Secondary.mat", "#C9A84C")
    ];

    public static readonly MapBeamTarget[] DefaultTargets =
    [
        new("The Jade Isles", "青玉群岛", "Farudin", true),
        new("Sealed Vault", "封印宝库", "Farudin", true),
        new("Derelict Mansion", "遗弃宅邸", "Farudin", true),
        new("Sacred Reservoir", "神圣水库", "Farudin", true),
        new("Castaway", "颠沛领域", "Green", true),
        new("Untainted Paradise", "纯净乐园", "Yellow", true),
        new("Azmerian Ranges", "阿兹莫里山区", "Vaal", true)
    ];

    // 已知地图的默认模型回退路径（当 datc64 结构变动时无缝保底）
    private static readonly Dictionary<string, string> FallbackAoPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Jade Isles"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/TheJadeIsles.ao",
        ["青玉群岛"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/TheJadeIsles.ao",
        ["Sealed Vault"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/SealedVault.ao",
        ["封印宝库"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/SealedVault.ao",
        ["Derelict Mansion"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/DerelictMansion.ao",
        ["遗弃宅邸"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/DerelictMansion.ao",
        ["Sacred Reservoir"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/SacredReservoir.ao",
        ["神圣水库"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/SacredReservoir.ao",
        ["Castaway"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/Castaway.ao",
        ["颠沛领域"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/Castaway.ao",
        ["Untainted Paradise"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/UntaintedParadise.ao",
        ["纯净乐园"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/UntaintedParadise.ao",
        ["Azmerian Ranges"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/AzmerianRanges.ao",
        ["阿兹莫里山区"] = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/AzmerianRanges.ao"
    };

    public MapBeamPatchService(PatchEngine engine)
    {
        _engine = engine;
    }

    public static MapBeamColorDef GetColor(string? key)
    {
        return Colors.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Colors[0];
    }

    public static string Fnv1a32Hex8(string text)
    {
        uint hash = 2166136261u;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash.ToString("x8");
    }

    public static string GetCopyFilePath(string originalAoPath)
    {
        return "Metadata/Terrain/WorldMaps/Maps/Doodads/Pins/EF_SceneryChild_" + Fnv1a32Hex8(originalAoPath) + ".ao";
    }

    public static string GenerateBeamWrapperContent(string copyFilePath, MapBeamColorDef color)
    {
        return "version 3\n" +
               "extends \"Metadata/FmtParent\"\n\n" +
               "AttachedAnimatedObject\n" +
               "{\n" +
               $"\tattached_object = \"<root> {copyFilePath}\"\n" +
               "}\n\n" +
               "client\n" +
               "{\n" +
               "\tBaseAnimationEvents\n" +
               "\t{\n" +
               "\t\ton_wake =\n" +
               "\t\t{\n" +
               "\t\t\tHideMeshSegment( EGM_MapPin01Shape );\n\n" +
               "\t\t\tIf( this.node_completed,\n" +
               "\t\t\t{\n" +
               "\t\t\t\tHideMeshSegment( loftedSurfaceShape9 );\n" +
               "\t\t\t\tHideMeshSegment( loftedSurfaceShape8 );\n" +
               "\t\t\t\tHideMeshSegment( loftedSurfaceShape7 );\n" +
               "\t\t\t},\n" +
               "\t\t\t{\n" +
               "\t\t\t\tShowMeshSegment( loftedSurfaceShape9 );\n" +
               "\t\t\t\tShowMeshSegment( loftedSurfaceShape8 );\n" +
               "\t\t\t\tShowMeshSegment( loftedSurfaceShape7 );\n" +
               "\t\t\t} );\n" +
               "\t\t}\n" +
               "\t}\n\n" +
               "\tFixedMesh\n" +
               "\t{\n" +
               "\t\tfixed_mesh = \"Art/Models/Terrain/WorldMap/Maps/Decorations/CitadelPins/VaalCitadelMap_pin_01.fmt\"\n\n" +
               $"\t\tloftedSurfaceShape9 = \"Art/Models/Terrain/WorldMap/Maps/Textures/{color.PrimaryMat}:0\"\n" +
               $"\t\tloftedSurfaceShape8 = \"Art/Models/Terrain/WorldMap/Maps/Textures/{color.SecondaryMat}:0\"\n" +
               $"\t\tloftedSurfaceShape7 = \"Art/Models/Terrain/WorldMap/Maps/Textures/{color.PrimaryMat}:0\"\n\n" +
               "\t\tEGM_MapPin01Shape = \"Art/Models/Terrain/WorldMap/Maps/Textures/MapCoin01_White.mat:0\"\n" +
               "\t}\n" +
               "}\n";
    }

    /// <summary>
    /// 应用神图掉落光柱补丁
    /// </summary>
    public bool ApplyPatch(
        GameModeInfo info,
        IEnumerable<MapBeamTarget>? targets = null,
        Action<string>? progress = null)
    {
        var targetList = (targets ?? DefaultTargets).Where(t => t.Enabled).ToList();
        if (targetList.Count == 0)
        {
            progress?.Invoke("未启用任何神图光柱目标。");
            return true;
        }

        progress?.Invoke("正在解析游戏客户端地图目录与装饰物模型...");
        var mapCatalog = BuildCatalog(info);

        var filesToWrite = new List<(string VirtualPath, byte[] Data)>();
        int appliedCount = 0;

        foreach (var target in targetList)
        {
            // 尝试通过地图目录匹配，若未匹配到则通过已知回退路径匹配
            string? aoPath = null;
            if (mapCatalog.TryGetValue(target.MapName, out var foundPath))
            {
                aoPath = foundPath;
            }
            else if (!string.IsNullOrEmpty(target.DisplayName) && mapCatalog.TryGetValue(target.DisplayName, out foundPath))
            {
                aoPath = foundPath;
            }
            else if (FallbackAoPaths.TryGetValue(target.MapName, out var fbPath))
            {
                aoPath = fbPath;
            }
            else if (!string.IsNullOrEmpty(target.DisplayName) && FallbackAoPaths.TryGetValue(target.DisplayName, out fbPath))
            {
                aoPath = fbPath;
            }

            if (string.IsNullOrWhiteSpace(aoPath))
            {
                progress?.Invoke($"地图 [{target.DisplayName ?? target.MapName}] 未找到对应模型路径，已跳过。");
                continue;
            }

            // 读取原始 .ao 文件
            byte[]? originalAoData = _engine.ReadGameFile(info, aoPath);
            if (originalAoData == null || originalAoData.Length == 0)
            {
                // 如果原文件不存在或为空，生成一份最小骨架避免崩溃
                originalAoData = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("version 3\nextends \"nothing\"\n")).ToArray();
            }

            // 检查是否已经是光柱（避免重复包装）
            string currentText = Encoding.Unicode.GetString(originalAoData);
            if (currentText.Contains("EF_SceneryChild_", StringComparison.Ordinal))
            {
                progress?.Invoke($"地图 [{target.DisplayName ?? target.MapName}] 已处于光柱状态，直接更新色彩。");
            }
            else
            {
                // 1. 产生备份副本 EF_SceneryChild_*.ao
                string copyPath = GetCopyFilePath(aoPath);
                filesToWrite.Add((copyPath, originalAoData));
            }

            // 2. 包装原始路径为光柱
            var colorDef = GetColor(target.Color);
            string copyFilePath = GetCopyFilePath(aoPath);
            string wrapperCode = GenerateBeamWrapperContent(copyFilePath, colorDef);
            byte[] wrapperBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(wrapperCode)).ToArray();

            filesToWrite.Add((aoPath, wrapperBytes));
            appliedCount++;
            progress?.Invoke($"已生成 [{target.DisplayName ?? target.MapName}] 的 {colorDef.Label} 醒目光柱。");
        }

        if (filesToWrite.Count == 0)
        {
            progress?.Invoke("没有有效的光柱文件需要写入。");
            return false;
        }

        // 副本路径 EF_SceneryChild_*.ao 在当前客户端中并不存在，属于本模块新建。
        // 必须显式放行，否则 PatchEngine 的「只写已存在路径」保护会把副本丢掉，
        // 而原路径的 wrapper 已经写进去并引用它 —— 结果是模型引用不存在的资源。
        var allowedNewPaths = new HashSet<string>(
            filesToWrite
                .Select(f => f.VirtualPath.Replace('\\', '/').TrimStart('/'))
                .Where(p => p.Contains("EF_SceneryChild_", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        progress?.Invoke($"正在将 {appliedCount} 个神图光柱注入游戏客户端...");
        bool writeSuccess = _engine.WriteGameFiles(info, filesToWrite, allowedNewPaths);
        if (writeSuccess)
        {
            progress?.Invoke($"成功应用 {appliedCount} 个异界神图醒目光柱！掉落时将冲天显现。");
            return true;
        }

        string errorDetail = PatchEngine.LastWriteException != null ? $" ({PatchEngine.LastWriteException.Message})" : "";
        progress?.Invoke($"写入游戏文件失败{errorDetail}，请检查客户端文件访问权限或备份状态。");
        return false;
    }

    /// <summary>
    /// 解析游戏客户端内所有异界地图及其对应的装饰物路径
    /// </summary>
    public Dictionary<string, string> BuildCatalog(GameModeInfo info)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            byte[]? waData = _engine.ReadGameFile(info, "data/balance/worldareas.datc64");
            byte[]? emData = _engine.ReadGameFile(info, "data/balance/endgamemaps.datc64");
            byte[]? edData = _engine.ReadGameFile(info, "data/balance/endgamemapdecorations.datc64");

            if (waData == null || emData == null || edData == null)
            {
                return result;
            }

            byte[]? waZhData = _engine.ReadGameFile(info, "data/balance/simplified chinese/worldareas.datc64");

            var waLayout = ParseLayout(waData);
            var emLayout = ParseLayout(emData);
            var edLayout = ParseLayout(edData);
            var waZhLayout = waZhData != null ? ParseLayout(waZhData) : null;

            if (!waLayout.HasValue || !emLayout.HasValue || !edLayout.HasValue)
            {
                return result;
            }

            int edRows = edLayout.Value.Rows;
            int edRowSize = edLayout.Value.RowSize;
            int edSentinel = edLayout.Value.SentinelPos;

            string[] decoIds = new string[edRows];
            string[] decoAos = new string[edRows];
            for (int i = 0; i < edRows; i++)
            {
                int rPos = 4 + i * edRowSize;
                decoIds[i] = ReadStr64(edData, edSentinel, rPos);
                decoAos[i] = ReadStr64(edData, edSentinel, rPos + 8);
            }

            int decoCol = FindDecorationsColumn(emData, emLayout.Value, waData, waLayout.Value, decoIds, decoAos);
            if (decoCol < 0)
            {
                return result;
            }

            int waRows = waLayout.Value.Rows;
            int waRowSize = waLayout.Value.RowSize;
            int waSentinel = waLayout.Value.SentinelPos;

            for (int i = 0; i < waRows; i++)
            {
                int rPos = 4 + i * waRowSize;
                string mapName = ReadStr64(waData, waSentinel, rPos + 8);
                string mapId = ReadStr64(waData, waSentinel, rPos);

                if (string.IsNullOrWhiteSpace(mapName) || string.IsNullOrWhiteSpace(mapId))
                    continue;

                int act = BinaryPrimitives.ReadInt32LittleEndian(waData.AsSpan(rPos + 16));
                uint isMap = BinaryPrimitives.ReadUInt32LittleEndian(waData.AsSpan(rPos + 44));

                // 异界地图 Act = 10, isMap = 1 且不为剧情副本
                if (act != 10 || isMap != 1 || mapId.Contains("_Quest", StringComparison.OrdinalIgnoreCase))
                    continue;

                int emRow = FindEndgameMapRow(emData, emLayout.Value, i);
                if (emRow < 0) continue;

                uint decoIndex = BinaryPrimitives.ReadUInt32LittleEndian(emData.AsSpan(4 + emRow * emLayout.Value.RowSize + decoCol));
                if (decoIndex >= decoAos.Length) continue;

                string aoPath = decoAos[decoIndex];
                if (string.IsNullOrWhiteSpace(aoPath) || !aoPath.StartsWith("Metadata/", StringComparison.OrdinalIgnoreCase))
                    continue;

                result[mapName] = aoPath;

                // 国服中文名称
                if (waZhData != null && waZhLayout.HasValue && i < waZhLayout.Value.Rows)
                {
                    string mapZh = ReadStr64(waZhData, waZhLayout.Value.SentinelPos, 4 + i * waZhLayout.Value.RowSize + 8);
                    if (!string.IsNullOrWhiteSpace(mapZh))
                    {
                        result[mapZh] = aoPath;
                    }
                }
            }
        }
        catch
        {
            // 若动态解析过程受阻，直接平稳降级
        }

        return result;
    }

    private static int FindDecorationsColumn(byte[] emData, (int Rows, int RowSize, int SentinelPos) em, byte[] waData, (int Rows, int RowSize, int SentinelPos) wa, string[] decoIds, string[] decoAos)
    {
        int bestCol = -1;
        int maxMatches = 0;
        int maxTotal = 0;

        for (int c = 4; c + 4 <= em.RowSize; c += 4)
        {
            int matches = 0;
            int total = 0;
            for (int r = 0; r < em.Rows; r++)
            {
                int rPos = 4 + r * em.RowSize;
                int waRow = BinaryPrimitives.ReadInt32LittleEndian(emData.AsSpan(rPos));
                if (waRow < 0 || waRow >= wa.Rows) continue;

                string waId = ReadStr64(waData, wa.SentinelPos, 4 + waRow * wa.RowSize);
                if (waId.Length == 0) continue;

                uint decoIdx = BinaryPrimitives.ReadUInt32LittleEndian(emData.AsSpan(rPos + c));
                if (decoIdx >= decoAos.Length) continue;

                string decoId = decoIds[decoIdx];
                if (!string.IsNullOrEmpty(decoId))
                {
                    total++;
                    if (string.Equals(decoId, waId, StringComparison.OrdinalIgnoreCase))
                    {
                        matches++;
                    }
                }
            }

            if (matches > maxMatches)
            {
                maxMatches = matches;
                maxTotal = total;
                bestCol = c;
            }
        }

        if (maxMatches < 10 || maxTotal <= 0)
        {
            return -1;
        }

        return bestCol;
    }

    private static int FindEndgameMapRow(byte[] emData, (int Rows, int RowSize, int SentinelPos) em, int targetWaRow)
    {
        for (int i = 0; i < em.Rows; i++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(emData.AsSpan(4 + i * em.RowSize)) == targetWaRow)
            {
                return i;
            }
        }
        return -1;
    }

    private static (int Rows, int RowSize, int SentinelPos)? ParseLayout(byte[] data)
    {
        if (data.Length < 16) return null;
        int rows = BitConverter.ToInt32(data, 0);
        if (rows <= 0) return null;

        int sentinel = DatBinaryUtils.FindSentinel(data, rows);
        if (sentinel < 0) return null;
        return (rows, (sentinel - 4) / rows, sentinel);
    }

    private static string ReadStr64(byte[] data, int sentinelPos, int ptrPos)
    {
        if (ptrPos + 8 > sentinelPos) return "";
        ulong offset = BitConverter.ToUInt64(data, ptrPos);
        if (offset < 8 || offset == 18374403900871474942uL || offset >= (ulong)(data.Length - sentinelPos))
            return "";

        var sb = new StringBuilder();
        for (int i = sentinelPos + (int)offset; i + 1 < data.Length; i += 2)
        {
            if (sb.Length >= 256) break;
            int c = data[i] | (data[i + 1] << 8);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }
}
