using System.Runtime.InteropServices;
using System.Text;

namespace InputGuard;

internal static class NativeMethods
{
    // ---------- Raw Input ----------
    public const uint RIDEV_DEVNOTIFY = 0x00002000;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    public const int WM_INPUT = 0x00FF;
    public const uint RID_HEADER = 0x10000005;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    public const int GIDC_ARRIVAL = 1;
    public const int GIDC_REMOVAL = 2;

    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIDI_DEVICEINFO = 0x2000000B;

    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const uint RIM_TYPEHID = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_MOUSE
    {
        public uint dwId;
        public uint dwNumberOfButtons;
        public uint dwSampleRate;
        public int fHasHorizontalWheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_KEYBOARD
    {
        public uint dwType;
        public uint dwSubType;
        public uint dwKeyboardMode;
        public uint dwNumberOfFunctionKeys;
        public uint dwNumberOfIndicators;
        public uint dwNumberOfKeysTotal;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public RID_DEVICE_INFO_MOUSE mouse;
        [FieldOffset(8)] public RID_DEVICE_INFO_KEYBOARD keyboard;
        [FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    // ---------- CfgMgr32 ----------
    public const int CR_SUCCESS = 0;
    public const int CR_NEED_RESTART = 0x22;
    public const int CR_NOT_DISABLEABLE = 0x28;
    public const int CR_ACCESS_DENIED = 0x33;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    public const uint CM_LOCATE_DEVNODE_PHANTOM = 1;

    public const uint DN_STARTED = 0x00000008;
    public const uint DN_HAS_PROBLEM = 0x00000400;
    public const uint CM_PROB_DISABLED = 22;

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    public const uint CM_REMOVE_UI_NOT_OK = 0x00000001;
    public const uint CM_REMOVE_NO_RESTART = 0x00000002;
    public const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;

    /// <summary> 장치 노드를 제거한다(장치 관리자의 "디바이스 제거"). 재열거하면 다시 설치된다. </summary>
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Query_And_Remove_SubTreeW(uint dnAncestor, out int pVetoType, StringBuilder? pszVetoName, uint ulNameLength, uint ulFlags);

    /// <summary> 장치 노드 아래를 다시 열거한다(장치 관리자의 "하드웨어 변경 사항 검색"). </summary>
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    /// <summary> 장치 트리 루트. </summary>
    public static uint? RootDevNode() =>
        CM_Locate_DevNodeW(out uint root, null!, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS ? root : null;

    public static string CrName(int cr) => cr switch
    {
        CR_SUCCESS => "성공",
        CR_NEED_RESTART => "재시작 필요(CR_NEED_RESTART)",
        CR_NOT_DISABLEABLE => "비활성화 불가 장치(CR_NOT_DISABLEABLE)",
        CR_ACCESS_DENIED => "권한 없음(CR_ACCESS_DENIED) - 관리자 권한 필요",
        _ => $"CR=0x{cr:X}"
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(string guid, uint pid) { fmtid = new Guid(guid); this.pid = pid; }
    }

    public static readonly DEVPROPKEY DEVPKEY_NAME = new("b725f130-47ef-101a-a5f1-02608c9eebac", 10);
    public static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    public static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);

    public const uint DEVPROP_TYPE_STRING = 0x00000012;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string? pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey, out uint propertyType,
        byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    public static string? GetDevNodeString(uint devInst, DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0);
        if (cr != CR_SUCCESS || type != DEVPROP_TYPE_STRING) return null;
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    public static string? GetDeviceId(uint devInst)
    {
        var sb = new StringBuilder(512);
        return CM_Get_Device_IDW(devInst, sb, (uint)sb.Capacity, 0) == CR_SUCCESS ? sb.ToString() : null;
    }
}
