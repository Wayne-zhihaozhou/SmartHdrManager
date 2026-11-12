using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace HdrBrightControl
{
    public sealed class WindowsHdrSwitch : IHdrSwitch
    {
        private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_INVALID_PARAMETER = 87;
        private const int ERROR_NOT_SUPPORTED = 50;

        private const uint ADVANCED_COLOR_SUPPORTED = 0x1;
        private const uint ADVANCED_COLOR_ENABLED = 0x2;
        private const uint HDR_SUPPORTED = 0x10;
        private const uint HDR_USER_ENABLED = 0x20;
        private const int ACTIVE_COLOR_MODE_HDR = 2;

        private const int WINDOWS_24H2_BUILD = 26100;

        private readonly bool _isWindows24H2OrNewer = Environment.OSVersion.Version.Build >= WINDOWS_24H2_BUILD;

        private int _hdrApiKind = (int)HdrApiKind.Unknown;

        private enum DISPLAYCONFIG_DEVICE_INFO_TYPE
        {
            GET_SOURCE_NAME = 1,
            GET_TARGET_NAME = 2,
            GET_ADVANCED_COLOR_INFO = 9,
            SET_ADVANCED_COLOR_STATE = 10,
            GET_ADVANCED_COLOR_INFO_2 = 15,
            SET_HDR_STATE = 16
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public int colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public int colorEncoding;
            public uint bitsPerColorChannel;
            public int activeColorMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint enableAdvancedColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_HDR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public uint refreshRateNumerator;
            public uint refreshRateDenominator;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public ulong u0, u1, u2, u3, u4, u5;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
                                                   ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
                                                   IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE setPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_HDR_STATE setPacket);

        public string ApiDescription => ApiKindName[(HdrApiKind)Volatile.Read(ref _hdrApiKind)];

        private static readonly Dictionary<HdrApiKind, string> ApiKindName = new()
        {
            [HdrApiKind.Unknown] = "未知",
            [HdrApiKind.Classic] = "经典 ACM",
            [HdrApiKind.Modern] = "24H2 HDR",
        };

        public bool TryQuery(out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            if (QueryActivePaths(out var paths, out _) != ERROR_SUCCESS) return false;

            foreach (var path in paths)
            {
                if (!TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool pathSupported, out bool isOn, out _)) continue;
                if (!pathSupported) continue;
                supported = true;
                enabled = isOn;
                return true;
            }
            return true;
        }

        public HdrSwitchResult Set(bool enable)
        {
            int rc = QueryActivePaths(out var paths, out _);
            if (rc != ERROR_SUCCESS)
                return HdrSwitchResult.Failure($"读取显示配置失败 (code {rc})");

            foreach (var path in paths)
            {
                if (!TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out bool isOn, out bool useNewApi))
                    continue;
                if (!supported) continue;
                if (isOn == enable) return HdrSwitchResult.Success();

                int result = SetHdrOnDisplay(path.targetInfo.adapterId, path.targetInfo.id, enable, useNewApi);
                if (result == ERROR_SUCCESS) return HdrSwitchResult.Success();
            }

            return HdrSwitchResult.Failure("未找到支持 HDR 的显示器");
        }

        private bool TryGetHdrState(LUID adapterId, uint targetId, out bool supported, out bool enabled, out bool usedNewApi)
        {
            supported = false;
            enabled = false;
            usedNewApi = false;

            if (_hdrApiKind != (int)HdrApiKind.Classic)
            {
                var modern = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
                modern.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO_2;
                modern.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>();
                modern.header.adapterId = adapterId;
                modern.header.id = targetId;

                int rc = DisplayConfigGetDeviceInfo(ref modern);
                if (rc == ERROR_SUCCESS)
                {
                    Volatile.Write(ref _hdrApiKind, (int)HdrApiKind.Modern);
                    usedNewApi = true;
                    supported = (modern.value & HDR_SUPPORTED) != 0;
                    enabled = supported && ((modern.value & HDR_USER_ENABLED) != 0 || modern.activeColorMode == ACTIVE_COLOR_MODE_HDR);
                    return true;
                }

                if (_hdrApiKind == (int)HdrApiKind.Unknown && (rc == ERROR_INVALID_PARAMETER || rc == ERROR_NOT_SUPPORTED) && !_isWindows24H2OrNewer)
                    Volatile.Write(ref _hdrApiKind, (int)HdrApiKind.Classic);
                if (_hdrApiKind == (int)HdrApiKind.Modern)
                    return false;
            }

            var classic = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            classic.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO;
            classic.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            classic.header.adapterId = adapterId;
            classic.header.id = targetId;
            if (DisplayConfigGetDeviceInfo(ref classic) != ERROR_SUCCESS) return false;

            supported = (classic.value & ADVANCED_COLOR_SUPPORTED) != 0;
            enabled = supported && (classic.value & ADVANCED_COLOR_ENABLED) != 0;
            return true;
        }

        private int SetHdrOnDisplay(LUID adapterId, uint targetId, bool enable, bool useNewApi)
        {
            if (useNewApi)
            {
                var set = new DISPLAYCONFIG_SET_HDR_STATE();
                set.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.SET_HDR_STATE;
                set.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_HDR_STATE>();
                set.header.adapterId = adapterId;
                set.header.id = targetId;
                set.value = enable ? 1u : 0u;
                return DisplayConfigSetDeviceInfo(ref set);
            }

            var classic = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
            classic.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.SET_ADVANCED_COLOR_STATE;
            classic.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>();
            classic.header.adapterId = adapterId;
            classic.header.id = targetId;
            classic.enableAdvancedColor = enable ? 1u : 0u;
            return DisplayConfigSetDeviceInfo(ref classic);
        }

        private int QueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
        {
            int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (rc != ERROR_SUCCESS)
            {
                paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
                modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
                return rc;
            }

            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            return QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        }
    }
}
