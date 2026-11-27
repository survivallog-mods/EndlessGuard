using System;
using System.Collections.Generic;
using System.IO;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 本 mod 注入的防御设备总表——新增设备在这里登记一行，通用处理自动覆盖：专属图标落盘、被僵尸咬掉血、
/// 失联模型自愈。设备各自的四表注入与模型供给仍在自己的类里（ArrowTower / TeslaCoil）。
/// 模型本身的选取/耐久契约见 DeviceModel（模型供给后必须调 Finalize）。
/// </summary>
internal static class ModDevices
{
    internal sealed class Def
    {
        /// <param name="id">设备配置 ID（四表统一）</param>
        /// <param name="iconFile">内嵌图标文件名（Resources\ 下），落盘到 WebUI\Res\Icon\ 同名</param>
        internal Def(int id, string iconFile)
        {
            Id = id;
            IconFile = iconFile;
            IconResource = "SurvivalLog.EndlessGuard.Resources." + iconFile;
        }

        internal readonly int Id;
        internal readonly string IconFile;
        internal readonly string IconResource;

        /// <summary>图标在 WebUI 里的相对路径：制作等界面把 &lt;img src=product.icon&gt; 直绑相对路径，
        /// 按 &lt;游戏根&gt;/SurvivalLog_Data/StreamingAssets/WebUI/ 下的普通文件解析。</summary>
        internal string IconWeb => "../../Res/Icon/" + IconFile;
    }

    internal static readonly Def Arrow = new Def(ArrowTower.Id, "eg_arrow_tower.png");
    internal static readonly Def Coil = new Def(TeslaCoil.Id, "eg_tesla_coil.png");
    internal static readonly Def[] All = { Arrow, Coil };

    private static readonly Dictionary<int, bool> IconReady = new();

    /// <summary>配置 ID（含派生等级行）是否属于本 mod 的设备。</summary>
    internal static bool IsModDevice(int cfgId)
    {
        var baseCfg = DeviceXpMath.BaseCfgOf(cfgId);
        foreach (var d in All)
            if (d.Id == baseCfg) return true;
        return false;
    }

    /// <summary>把设备的内嵌图标写到 WebUI\Res\Icon\，返回 WebUI 相对路径；游戏目录契约不成立、内嵌资源缺失、
    /// 写入失败都返回 null（调用方退占位图标）。成败均本会话定版，不反复重试刷盘。</summary>
    internal static string EnsureIcon(Def d)
    {
        if (IconReady.TryGetValue(d.Id, out var ready)) return ready ? d.IconWeb : null;
        var ok = false;
        try
        {
            var root = Path.GetFullPath(Path.Combine(BepInEx.Paths.BepInExRootPath, ".."));   // BepInEx 上溯一级=游戏根
            var resDir = Path.Combine(root, "SurvivalLog_Data", "StreamingAssets", "WebUI", "Res");
            if (!Directory.Exists(resDir))
                EndlessGuardPlugin.Trace("[Device] icon: WebUI/Res 目录不存在（" + resDir + "），" + d.IconFile + " 用占位图标");
            else
            {
                Directory.CreateDirectory(Path.Combine(resDir, "Icon"));   // 只在已验证的 Res 下补子目录
                var target = Path.Combine(resDir, "Icon", d.IconFile);
                var bytes = ReadEmbeddedIcon(d.IconResource);
                if (bytes == null)
                    EndlessGuardPlugin.Trace("[Device] icon: 内嵌资源缺失（" + d.IconResource + "，查 csproj EmbeddedResource）");
                else
                {
                    var fi = new FileInfo(target);
                    if (!fi.Exists || fi.Length != bytes.Length)             // 缺失或过期才写；一致则零写入
                    {
                        var tmp = target + ".tmp";
                        File.WriteAllBytes(tmp, bytes);
                        File.Move(tmp, target, true);
                        EndlessGuardPlugin.Trace("[Device] icon file written: " + target + " (" + bytes.Length + " bytes)");
                    }
                    ok = true;
                }
            }
        }
        catch (Exception e) { EndlessGuardPlugin.Trace("[Device] icon: 落盘失败（" + e.Message + "）"); }
        IconReady[d.Id] = ok;
        return ok ? d.IconWeb : null;
    }

    private static byte[] ReadEmbeddedIcon(string resourceName)
    {
        try
        {
            using var s = typeof(ModDevices).Assembly.GetManifestResourceStream(resourceName);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
