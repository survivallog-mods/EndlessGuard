using System;
using UnityEngine;

namespace SurvivalLog.EndlessGuard;

/// <summary>
/// 程序化木纹/瓦片贴图（fixwave3 W3B，作者 playtest 第 2 条「94 个纯色方块无贴图」的零资产改良）：
/// 128×128 Texture2D 运行时生成（FilterMode.Bilinear + wrap Repeat），两张：
/// - Wood：横向木板条（板高 21px + 2px 深色拼缝），每板亮度抖动 ±6%，每板 1-2 条竖向木纹暗痕，
///   细噪声 ±2%，纵向微渐变（顶部 +5% 亮）。三档木色（深/中/浅）不经三张贴图——贴图做灰度亮度图，
///   色相全由材质 _BaseColor 乘色承载（URP Lit _BaseMap × _BaseColor 同色相乘语义）。
/// - Roof：横向瓦垄（行高 14px + 行底 2px 深缝，交错行横向竖缝错半位 14px），同行抖动口径。
/// 铁件/石件不走贴图（Role.Flat 纯色）。确定性：全部噪声走整数哈希（Rnd），同参数同图，可复现。
/// Unity 侧 API，无纯逻辑单测（brief 裁定）；桶选择 VarBucket 的纯函数部分在 ArrowTowerFxMath 有单测。
/// 全程 try/catch（贴图建不出来返回 null，材质侧自动退纯色——宁纯色不炸塔）。
/// </summary>
internal static class TowerTextures
{
    /// <summary>贴图角色（fixwave3 W3B）：Flat=纯色（铁/石/旗/羽）；PlankBig/PlankSmall=木纹大/小平铺；
    /// Roof=瓦纹。角色决定 _BaseMap 选图与 _BaseMap_ST 平铺（brief 的"按部件组定平铺"落到枚举上）。</summary>
    internal enum Role
    {
        Flat,
        PlankBig,     // 木纹 (1.5,1.5)：塔身段/横带/平台/垛墙/檐口等大面
        PlankSmall,   // 木纹 (1,1)：柱/撑/梯/垛齿/弩机木件/箭杆等小件
        Roof          // 瓦纹 (1.5,1.5)：四坡顶锥面
    }

    private static Texture2D _wood;
    private static Texture2D _roof;
    private static bool _tried;

    /// <summary>木纹亮度图（懒建一次；失败=null=材质侧退纯色）。</summary>
    internal static Texture2D Wood
    {
        get { Ensure(); return _wood; }
    }

    /// <summary>瓦纹亮度图（懒建一次；失败=null=材质侧退纯色）。</summary>
    internal static Texture2D Roof
    {
        get { Ensure(); return _roof; }
    }

    /// <summary>角色 → 贴图（Flat=null）。</summary>
    internal static Texture2D TexOf(Role role)
    {
        switch (role)
        {
            case Role.PlankBig:
            case Role.PlankSmall: return Wood;
            case Role.Roof: return Roof;
            default: return null;
        }
    }

    /// <summary>角色 → _BaseMap_ST 平铺（brief：大件 1.5，小件 1，瓦面 1.5）。</summary>
    internal static Vector2 ScaleOf(Role role)
    {
        switch (role)
        {
            case Role.PlankBig:
            case Role.Roof: return new Vector2(1.5f, 1.5f);
            default: return new Vector2(1f, 1f);
        }
    }

    private static void Ensure()
    {
        if (_tried) return;
        _tried = true;
        try { _wood = PaintWood(); } catch (Exception e) { _wood = null; EndlessGuardPlugin.Trace("[Tower][tex] wood: " + e.Message); }
        try { _roof = PaintRoof(); } catch (Exception e) { _roof = null; EndlessGuardPlugin.Trace("[Tower][tex] roof: " + e.Message); }
        if (_wood != null || _roof != null)
            EndlessGuardPlugin.Trace("[Tower][tex] procedural textures built wood=" + (_wood != null) + " roof=" + (_roof != null) + " (128x128)");
    }

    // ── 木纹：横板条 + 拼缝 + 板间抖动 + 竖向木纹痕 + 微渐变 ─────────────────────────

    private const int Size = 128;
    private const int PlankH = 21;     // 板高 ~21px（128/21≈6 板）
    private const int GapH = 2;        // 拼缝 2px

    private static Texture2D PaintWood()
    {
        var px = new Color32[Size * Size];
        var planks = Size / PlankH + 1;
        // 每板亮度抖动 ±6%（确定性哈希）
        var jit = new float[planks];
        for (var i = 0; i < planks; i++) jit[i] = 1f + (Rnd(i, 11) - 0.5f) * 0.12f;
        // 每板 1-2 条竖向暗痕（列位置/条数/宽 1-2px 全确定性）
        for (var p = 0; p < planks; p++)
        {
            StreakX[p * 2] = (int)(Rnd(p, 3) * Size);
            StreakX[p * 2 + 1] = (int)(Rnd(p, 5) * Size);
            StreakN[p] = Rnd(p, 7) < 0.55f ? 1 : 2;
            StreakW[p] = Rnd(p, 9) < 0.3f ? 1 : 0;   // 30% 概率加宽到 2px
        }
        for (var y = 0; y < Size; y++)
        {
            var row = y / PlankH;
            var ry = y % PlankH;
            var grad = 0.975f + 0.05f * (y / (float)(Size - 1));   // 底 0.975 → 顶 ~1.025（顶部 +5% 相对亮）
            for (var x = 0; x < Size; x++)
            {
                var l = jit[row] * grad;
                if (ry < GapH) l *= 0.55f;                          // 深色拼缝
                else
                {
                    if (StreakHit(row, x)) l *= 0.86f;              // 木纹暗痕
                    l *= 0.98f + 0.04f * Rnd(x, y + 9001);          // 细噪声 ±2%
                }
                px[y * Size + x] = Gray(l);
            }
        }
        return Finish(px);
    }

    private static readonly int[] StreakX = new int[64];
    private static readonly int[] StreakN = new int[32];
    private static readonly int[] StreakW = new int[32];

    private static bool StreakHit(int plank, int x)
    {
        if (plank < 0 || plank >= StreakN.Length) return false;
        for (var s = 0; s < StreakN[plank]; s++)
        {
            var sx = StreakX[plank * 2 + s];
            if (x >= sx && x <= sx + StreakW[plank]) return true;
        }
        return false;
    }

    // ── 瓦纹：横垄行 + 行底缝 + 交错竖缝 ──────────────────────────────────────────

    private const int RowH = 14;       // 行高 ~14px（128/14≈9 行）
    private const int SeamW = 2;       // 缝宽
    private const int ShingleW = 28;   // 瓦片横宽 28px，交错行错半位 14px

    private static Texture2D PaintRoof()
    {
        var px = new Color32[Size * Size];
        var rows = Size / RowH + 1;
        var jit = new float[rows];
        for (var i = 0; i < rows; i++) jit[i] = 1f + (Rnd(i, 21) - 0.5f) * 0.12f;   // 同 ±6% 抖动
        for (var y = 0; y < Size; y++)
        {
            var row = y / RowH;
            var ry = y % RowH;
            for (var x = 0; x < Size; x++)
            {
                var l = jit[row];
                if (ry >= RowH - SeamW) l *= 0.5f;                          // 行底横缝
                var off = (row % 2) * (ShingleW / 2);
                if (((x + off) % ShingleW) < SeamW) l *= 0.62f;             // 交错竖缝
                l *= 0.985f + 0.03f * Rnd(x, y + 13001);                    // 细噪声
                px[y * Size + x] = Gray(l);
            }
        }
        return Finish(px);
    }

    // ── 小工具 ────────────────────────────────────────────────────────────────────

    /// <summary>确定性哈希噪声（整数辗转混洗 → [0,1)）：同入参同输出，贴图可复现。</summary>
    private static float Rnd(int a, int b)
    {
        unchecked
        {
            var h = (uint)(a * 374761393 + b * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    private static Color32 Gray(float l)
    {
        var b = l <= 0f ? (byte)0 : l >= 1f ? (byte)255 : (byte)(l * 255f);
        return new Color32(b, b, b, 255);
    }

    private static Texture2D Finish(Color32[] px)
    {
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true);
        tex.wrapMode = TextureWrapMode.Repeat;      // _BaseMap_ST 平铺要 repeat
        tex.filterMode = FilterMode.Bilinear;
        tex.SetPixels32(px);
        tex.Apply(true, false);                     // 生成 mip；不标 readonly（克隆/升级链还要引用比对）
        return tex;
    }
}
