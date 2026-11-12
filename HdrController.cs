using System;
using System.Runtime.InteropServices;

namespace HdrBrightControl
{
    /// <summary>
    /// HDR control through the Windows display configuration API.
    /// Windows 11 24H2 separated real HDR from Auto Color Management (ACM): the newer request is preferred and the
    /// classic "advanced colour" request is only used when Windows does not know it. With the classic call an SDR
    /// monitor that only supports ACM looks HDR-capable and switching it would toggle ACM instead of HDR.
    /// </summary>
    public static class HdrController
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

        /// <summary>Windows 11 24H2 is build 26100; the HDR requests exist from there on.</summary>
        private const int WINDOWS_24H2_BUILD = 26100;

        private const int API_UNKNOWN = -1;
        private const int API_CLASSIC = 0;
        private const int API_MODERN = 1;

        private static readonly bool IsWindows24H2OrNewer = Environment.OSVersion.Version.Build >= WINDOWS_24H2_BUILD;

        private static volatile int _hdrApi = API_UNKNOWN;

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

        /// <summary>Which Windows HDR API is used (for the log).</summary>
        public static string HdrApiName => _hdrApi switch
        {
            API_MODERN => "modern HDR API (Windows 11 24H2 或更新版本)",
            API_CLASSIC => "classic 高级颜色 API",
            _ => "not determined yet"
        };

        /// <summary>Why the last <see cref="SetHdrState"/> call failed; empty after a success.</summary>
        public static string LastError { get; private set; } = string.Empty;

        /// <summary>Reads the HDR state of the display. Returns false when the configuration cannot be read.</summary>
        public static bool TryQueryHdrState(out bool supported, out bool enabled)
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

        /// <summary>Switches HDR on the HDR-capable display.</summary>
        public static bool SetHdrState(bool enable)
        {
            LastError = string.Empty;

            int rc = QueryActivePaths(out var paths, out _);
            if (rc != ERROR_SUCCESS)
            {
                LastError = $"读取显示配置失败 (code {rc})";
                return false;
            }

            foreach (var path in paths)
            {
                if (!TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out bool isOn, out bool useNewApi))
                    continue;
                if (!supported) continue;
                if (isOn == enable) return true;

                int result = SetHdrOnDisplay(path.targetInfo.adapterId, path.targetInfo.id, enable, useNewApi);
                if (result == ERROR_SUCCESS) return true;

                LastError = $"DisplayConfigSetDeviceInfo 失败 (code {result})";
                return false;
            }

            LastError = "未找到支持 HDR 的显示器";
            return false;
        }

        /// <summary>HDR support / state of one display, preferring the 24H2 API.</summary>
        private static bool TryGetHdrState(LUID adapterId, uint targetId, out bool supported, out bool enabled, out bool usedNewApi)
        {
            supported = false;
            enabled = false;
            usedNewApi = false;

            if (_hdrApi != API_CLASSIC)
            {
                var modern = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
                modern.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO_2;
                modern.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>();
                modern.header.adapterId = adapterId;
                modern.header.id = targetId;

                int rc = DisplayConfigGetDeviceInfo(ref modern);
                if (rc == ERROR_SUCCESS)
                {
                    _hdrApi = API_MODERN;
                    usedNewApi = true;
                    supported = (modern.value & HDR_SUPPORTED) != 0;
                    enabled = supported && ((modern.value & HDR_USER_ENABLED) != 0 || modern.activeColorMode == ACTIVE_COLOR_MODE_HDR);
                    return true;
                }

                if (_hdrApi == API_UNKNOWN && (rc == ERROR_INVALID_PARAMETER || rc == ERROR_NOT_SUPPORTED) && !IsWindows24H2OrNewer)
                    _hdrApi = API_CLASSIC;
                if (_hdrApi == API_MODERN)
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

        /// <summary>Switches HDR on one display with the same API that reported its state.</summary>
        private static int SetHdrOnDisplay(LUID adapterId, uint targetId, bool enable, bool useNewApi)
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

        /// <summary>Queries the active display paths.</summary>
        private static int QueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
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
