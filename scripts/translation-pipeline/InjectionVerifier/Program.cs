using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

// 在本地快照上模拟一次完整注入，逐表断言四条不变量：
//
//   1. 行数不变
//   2. 字符串区哨兵位置不变
//   3. 除 8 字节指针槽外，其余字节一个都不能变
//   4. 每个写入的指针都能解析回预期译文
//
// 这些断言覆盖了历史上导致客户端闪退的全部原因（写坏数值字段、写坏数组结构、
// 指针宽度错误）。任何一条不满足即视为不可发布。
//
// 通过环境变量接收参数：
//   FREIATLAS_VERIFY_SNAPSHOT  国际服快照目录
//   FREIATLAS_VERIFY_DICT      待验证的词典 .json.gz
namespace InjectionVerifier
{
    internal sealed class Dat
    {
        public int RowCount;
        public int RowLength;
        public byte[] Fixed = Array.Empty<byte>();
        public byte[] Variable = Array.Empty<byte>();

        public Dat(byte[] data)
        {
            RowCount = BitConverter.ToInt32(data, 0);
            int boundary = -1;
            for (int i = 0; i < data.Length - 8; i++)
            {
                if (data[i] != 0xBB || data[i + 1] != 0xBB || data[i + 2] != 0xBB || data[i + 3] != 0xBB) continue;
                if (data[i + 4] != 0xBB || data[i + 5] != 0xBB || data[i + 6] != 0xBB || data[i + 7] != 0xBB) continue;
                int offset = i - 4;
                if (RowCount > 0 && offset % RowCount == 0) { boundary = offset; break; }
            }
            if (boundary < 0) throw new InvalidDataException("未找到字符串区哨兵");

            RowLength = boundary / RowCount;
            Fixed = new byte[boundary];
            Array.Copy(data, 4, Fixed, 0, boundary);
            Variable = new byte[data.Length - 4 - boundary];
            Array.Copy(data, 4 + boundary, Variable, 0, Variable.Length);
        }
    }

    internal sealed class Catalogue
    {
        public Dictionary<string, TableDefinition> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class TableDefinition
    {
        public string FileName { get; set; } = "";
        public int RowSize { get; set; }
        public List<Patch> RowPatches { get; set; } = new();
    }

    internal sealed class Patch
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public string Text { get; set; } = "";
    }

    internal static class Program
    {
        private static int Main()
        {
            string snapshotDir = Environment.GetEnvironmentVariable("FREIATLAS_VERIFY_SNAPSHOT") ?? "";
            string dictPath = Environment.GetEnvironmentVariable("FREIATLAS_VERIFY_DICT") ?? "";

            if (snapshotDir.Length == 0 || dictPath.Length == 0)
            {
                Console.Error.WriteLine("缺少环境变量：FREIATLAS_VERIFY_SNAPSHOT / FREIATLAS_VERIFY_DICT");
                return 2;
            }
            if (!Directory.Exists(snapshotDir))
            {
                Console.Error.WriteLine($"快照目录不存在：{snapshotDir}");
                return 3;
            }

            Catalogue catalogue;
            using (var file = File.OpenRead(dictPath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                catalogue = JsonSerializer.Deserialize<Catalogue>(
                    gzip, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Catalogue();
            }

            int patchTotal = catalogue.Tables.Values.Sum(t => t.RowPatches.Count);
            Console.WriteLine($"词典：{catalogue.Tables.Count} 表，{patchTotal} 条");

            int passedTables = 0;
            int passedPatches = 0;
            var failures = new List<string>();

            foreach (var (tableName, definition) in catalogue.Tables)
            {
                string path = Path.Combine(snapshotDir, tableName);
                if (!File.Exists(path))
                {
                    failures.Add($"{tableName}: 快照中缺少该表");
                    continue;
                }

                byte[] source = File.ReadAllBytes(path);
                Dat original;
                try
                {
                    original = new Dat(source);
                }
                catch (Exception ex)
                {
                    failures.Add($"{tableName}: 源文件解析失败（{ex.Message}）");
                    continue;
                }

                if (original.RowLength != definition.RowSize)
                {
                    failures.Add($"{tableName}: 行宽 {original.RowLength} != 词典声明 {definition.RowSize}");
                    continue;
                }

                int sentinel = 4 + original.RowCount * original.RowLength;

                byte[] patched;
                var writableSlots = new List<int>();
                var dedup = new Dictionary<string, ulong>(StringComparer.Ordinal);
                bool outOfRange = false;

                using (var buffer = new MemoryStream(source.Length + 8 * 1024 * 1024))
                {
                    buffer.Write(source, 0, source.Length);

                    foreach (var patch in definition.RowPatches)
                    {
                        if (patch.Row < 0 || patch.Row >= original.RowCount
                            || patch.Col < 0 || patch.Col + 8 > original.RowLength)
                        {
                            outOfRange = true;
                            break;
                        }

                        if (!dedup.TryGetValue(patch.Text, out ulong offset))
                        {
                            // 字符串区必须保持 2 字节对齐
                            if ((buffer.Length - sentinel) % 2 != 0) buffer.WriteByte(0);
                            offset = (ulong)(buffer.Length - sentinel);
                            byte[] encoded = Encoding.Unicode.GetBytes(patch.Text);
                            buffer.Write(encoded, 0, encoded.Length);
                            buffer.WriteByte(0);
                            buffer.WriteByte(0);
                            buffer.WriteByte(0);
                            buffer.WriteByte(0);
                            dedup[patch.Text] = offset;
                        }

                        int slot = 4 + patch.Row * original.RowLength + patch.Col;
                        long resume = buffer.Position;
                        buffer.Position = slot;
                        buffer.Write(BitConverter.GetBytes(offset), 0, 8);
                        buffer.Position = resume;
                        writableSlots.Add(slot);
                    }

                    patched = buffer.ToArray();
                }

                if (outOfRange)
                {
                    failures.Add($"{tableName}: 存在越界的行/列");
                    continue;
                }

                // 不变量 1：行数不变
                if (BitConverter.ToInt32(patched, 0) != original.RowCount)
                {
                    failures.Add($"{tableName}: 行数被改变");
                    continue;
                }

                // 不变量 2：哨兵位置不变
                if (ReadSentinelPosition(patched, original.RowCount) != sentinel)
                {
                    failures.Add($"{tableName}: 字符串区哨兵位置被改变");
                    continue;
                }

                // 不变量 3：只允许指针槽的字节变化
                var writable = new HashSet<int>();
                foreach (int slot in writableSlots)
                {
                    for (int i = 0; i < 8 && slot + i < source.Length; i++) writable.Add(slot + i);
                }
                int violations = 0;
                for (int i = 0; i < source.Length; i++)
                {
                    if (source[i] != patched[i] && !writable.Contains(i))
                    {
                        violations++;
                        if (violations > 3) break;
                    }
                }
                if (violations > 0)
                {
                    failures.Add($"{tableName}: 越界写入了 {violations} 个字节");
                    continue;
                }

                // 不变量 4：指针能解析回译文
                int mismatched = 0;
                foreach (var patch in definition.RowPatches)
                {
                    ulong pointer = BitConverter.ToUInt64(patched, 4 + patch.Row * original.RowLength + patch.Col);
                    if (pointer == 0 || pointer >= (ulong)(patched.Length - sentinel))
                    {
                        mismatched++;
                        continue;
                    }
                    if (ReadZeroTerminated(patched, sentinel + (int)pointer).Trim() != patch.Text.Trim())
                    {
                        mismatched++;
                    }
                }
                if (mismatched > 0)
                {
                    failures.Add($"{tableName}: {mismatched}/{definition.RowPatches.Count} 条指针无法解析回译文");
                    continue;
                }

                passedTables++;
                passedPatches += definition.RowPatches.Count;
            }

            Console.WriteLine();
            Console.WriteLine($"结果：{passedTables}/{catalogue.Tables.Count} 表通过，{passedPatches} 条补丁");
            if (failures.Count == 0)
            {
                Console.WriteLine("验证通过");
                return 0;
            }

            Console.Error.WriteLine($"失败 {failures.Count} 项：");
            foreach (var message in failures.Take(20)) Console.Error.WriteLine("  " + message);
            return 5;
        }

        private static int ReadSentinelPosition(byte[] data, int rowCount)
        {
            for (int i = 4; i < data.Length - 8; i++)
            {
                if (data[i] != 0xBB || data[i + 1] != 0xBB || data[i + 2] != 0xBB || data[i + 3] != 0xBB) continue;
                if (data[i + 4] != 0xBB || data[i + 5] != 0xBB || data[i + 6] != 0xBB || data[i + 7] != 0xBB) continue;
                if (rowCount > 0 && (i - 4) % rowCount == 0) return i;
            }
            return -1;
        }

        private static string ReadZeroTerminated(byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length - 1) return "";
            int end = offset;
            while (end < data.Length - 1 && !(data[end] == 0 && data[end + 1] == 0)) end += 2;
            return Encoding.Unicode.GetString(data, offset, end - offset);
        }
    }
}
