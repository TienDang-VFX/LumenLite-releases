using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace LumenLite;

public sealed record DisplayTarget(long Adapter, uint TargetId, string Name, bool Active, bool Primary);

/// <summary>
/// Turns individual monitors on and off in extended mode through the Windows
/// display configuration API, the same thing "Disconnect this display" does.
/// </summary>
internal static class Displays
{
    private const uint QDC_ALL_PATHS = 0x1;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
    private const uint MODE_IDX_INVALID = 0xFFFFFFFF;
    private const uint MODE_INFO_TYPE_SOURCE = 1;

    private const uint SDC_TOPOLOGY_SUPPLIED = 0x10;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    private const uint SDC_APPLY = 0x80;
    private const uint SDC_SAVE_TO_DATABASE = 0x200;
    private const uint SDC_ALLOW_CHANGES = 0x400;
    private const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint GET_SOURCE_NAME = 1;
    private const uint GET_TARGET_NAME = 2;

    public static List<DisplayTarget> Targets()
    {
        var all = Query(QDC_ALL_PATHS, out _);
        var active = Query(QDC_ONLY_ACTIVE_PATHS, out var modes);

        var result = new List<DisplayTarget>();
        var seen = new HashSet<(long, uint)>();
        foreach (var path in all)
        {
            var key = (Key(path.targetInfo.adapterId), path.targetInfo.id);
            if (path.targetInfo.targetAvailable == 0 || !seen.Add(key))
                continue;

            var activePath = active.FirstOrDefault(p => Key(p.targetInfo.adapterId) == key.Item1 && p.targetInfo.id == key.Item2);
            var isActive = activePath.flags != 0;
            var primary = false;
            if (isActive && activePath.sourceInfo.modeInfoIdx < modes.Length)
            {
                var mode = modes[activePath.sourceInfo.modeInfoIdx];
                primary = mode.infoType == MODE_INFO_TYPE_SOURCE && mode.positionX == 0 && mode.positionY == 0;
            }
            result.Add(new DisplayTarget(key.Item1, key.Item2, TargetName(path.targetInfo.adapterId, path.targetInfo.id), isActive, primary));
        }
        return result;
    }

    /// <summary>Turns one monitor on or off. Returns a Win32 error code, 0 on success.</summary>
    public static int SetActive(DisplayTarget target, bool active)
    {
        var current = Query(QDC_ONLY_ACTIVE_PATHS, out var modes).ToList();
        bool IsTarget(PATH_INFO p) => Key(p.targetInfo.adapterId) == target.Adapter && p.targetInfo.id == target.TargetId;

        if (active)
        {
            if (current.Any(IsTarget))
                return 0;
            // Any source on the same adapter that no active path is using can drive the monitor.
            var used = current.Select(p => (Key(p.sourceInfo.adapterId), p.sourceInfo.id)).ToHashSet();
            var candidates = Query(QDC_ALL_PATHS, out _)
                .Where(p => IsTarget(p) && !used.Contains((Key(p.sourceInfo.adapterId), p.sourceInfo.id)))
                .ToList();
            if (candidates.Count == 0)
                return 1168; // ERROR_NOT_FOUND
            var path = candidates[0];
            path.flags = DISPLAYCONFIG_PATH_ACTIVE;
            path.sourceInfo.modeInfoIdx = MODE_IDX_INVALID;
            path.targetInfo.modeInfoIdx = MODE_IDX_INVALID;
            current.Add(path);
        }
        else
        {
            if (current.Count(p => !IsTarget(p)) == 0)
                return 5; // ERROR_ACCESS_DENIED: never turn off the last monitor
            current.RemoveAll(IsTarget);
        }

        // First let Windows restore the layout it remembers for this set of monitors.
        var topology = current.Select(p =>
        {
            p.sourceInfo.modeInfoIdx = MODE_IDX_INVALID;
            p.targetInfo.modeInfoIdx = MODE_IDX_INVALID;
            return p;
        }).ToArray();
        var error = SetDisplayConfig((uint)topology.Length, topology, 0, null,
            SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES);
        if (error == 0)
            return 0;

        // No remembered layout: keep the current modes and let Windows fill in the new monitor.
        var (paths, kept) = Compact(current, modes);
        return SetDisplayConfig((uint)paths.Length, paths, (uint)kept.Length, kept,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
    }

    /// <summary>Drops modes that no remaining path refers to and renumbers the references.</summary>
    private static (PATH_INFO[] Paths, MODE_INFO[] Modes) Compact(List<PATH_INFO> paths, MODE_INFO[] modes)
    {
        var kept = new List<MODE_INFO>();
        var map = new Dictionary<uint, uint>();
        uint Remap(uint index)
        {
            if (index == MODE_IDX_INVALID || index >= modes.Length)
                return MODE_IDX_INVALID;
            if (!map.TryGetValue(index, out var mapped))
            {
                mapped = (uint)kept.Count;
                kept.Add(modes[index]);
                map[index] = mapped;
            }
            return mapped;
        }

        var result = paths.Select(p =>
        {
            p.sourceInfo.modeInfoIdx = Remap(p.sourceInfo.modeInfoIdx);
            p.targetInfo.modeInfoIdx = Remap(p.targetInfo.modeInfoIdx);
            return p;
        }).ToArray();
        return (result, kept.ToArray());
    }

    private static PATH_INFO[] Query(uint flags, out MODE_INFO[] modes)
    {
        while (true)
        {
            var error = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (error != 0)
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {error}");
            var paths = new PATH_INFO[pathCount];
            modes = new MODE_INFO[modeCount];
            error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            // The layout can change between the two calls; ask again when it does.
            if (error == ERROR_INSUFFICIENT_BUFFER)
                continue;
            if (error != 0)
                throw new InvalidOperationException($"QueryDisplayConfig failed: {error}");
            Array.Resize(ref modes, (int)modeCount);
            Array.Resize(ref paths, (int)pathCount);
            return paths;
        }
    }

    private static string TargetName(LUID adapter, uint id)
    {
        var info = new TARGET_DEVICE_NAME
        {
            type = GET_TARGET_NAME,
            size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>(),
            adapterId = adapter,
            id = id,
        };
        return DisplayConfigGetDeviceInfo(ref info) == 0 && !string.IsNullOrWhiteSpace(info.monitorFriendlyDeviceName)
            ? info.monitorFriendlyDeviceName
            : L.T("Màn hình");
    }

    /// <summary>Friendly monitor names of the active displays, keyed by GDI name such as \.\DISPLAY1.</summary>
    public static Dictionary<string, string> NamesByGdiDevice()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Query(QDC_ONLY_ACTIVE_PATHS, out _))
        {
            var source = new SOURCE_DEVICE_NAME
            {
                type = GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            };
            if (DisplayConfigGetDeviceInfo(ref source) == 0 && !string.IsNullOrEmpty(source.viewGdiDeviceName))
                result.TryAdd(source.viewGdiDeviceName, TargetName(path.targetInfo.adapterId, path.targetInfo.id));
        }
        return result;
    }

    private static long Key(LUID luid) => ((long)luid.HighPart << 32) | luid.LowPart;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PATH_INFO[] paths,
        ref uint modeCount, [Out] MODE_INFO[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] PATH_INFO[]? paths, uint modeCount, [In] MODE_INFO[]? modes, uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_DEVICE_NAME
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshNumerator;
        public uint refreshDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO
    {
        public PATH_SOURCE_INFO sourceInfo;
        public PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>DISPLAYCONFIG_MODE_INFO; only the source-mode position is read, the rest is passed through.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(28)] public int positionX;
        [FieldOffset(32)] public int positionY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_DEVICE_NAME
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
}
