using System.IO;
using System.Runtime.InteropServices;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 预加载 Oodle 压缩库（oo2core.dll）。
///
/// LibBundle3 通过裸名 <c>DllImport("oo2core.dll")</c> 调用它；本类只负责在进程内
/// 先从受控目录把库加载进来，保证后续 DllImport 解析到同一份文件。
///
/// 安全约束：只允许「程序目录」与「显式传入的游戏目录」。不从当前工作目录或
/// 裸名（PATH/CWD 解析）加载 —— 那等于允许从任意启动目录植入同名 DLL。
/// </summary>
internal static class OodleNative
{
    private static bool _initialized;
    private static readonly object _initLock = new();

    static OodleNative()
    {
        EnsureLoaded();
    }

    public static void EnsureLoaded(string? fallbackDirectory = null)
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            string[] searchPaths =
            [
                Path.Combine(AppContext.BaseDirectory, "oo2core.dll"),
                Path.Combine(AppContext.BaseDirectory, "libs", "oo2core.dll"),
                fallbackDirectory is not null ? Path.Combine(fallbackDirectory, "oo2core.dll") : ""
            ];

            foreach (var path in searchPaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                if (NativeLibrary.TryLoad(path, out _))
                {
                    _initialized = true;
                    return;
                }
            }
        }
    }
}
