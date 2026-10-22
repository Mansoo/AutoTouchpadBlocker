using System.Runtime.InteropServices;
using static InputGuard.NativeMethods;

namespace InputGuard;

public enum DeviceKind { Keyboard, Mouse, Other }
public enum DeviceOrigin { Internal, External, Virtual, Unknown }

public sealed record InputDevice(
    IntPtr Handle,
    DeviceKind Kind,
    DeviceOrigin Origin,
    string Transport,      // USB / Bluetooth / PS/2 / I2C / Virtual / ?
    string Name,
    string InstanceId,
    string PhysicalKey,    // 같은 물리 장치의 HID 컬렉션들을 묶는 키
    string Detail,         // 키 개수 / 버튼 수 등 부가 정보
    int KeyCount)          // 키보드 컬렉션이 보고하는 총 키 수 (마우스는 0)
{
    public string ShortLabel => $"{Name} [{Transport}]";

    public override string ToString() =>
        $"{Kind,-8} {Origin,-8} {Transport,-9} {Name}  {Detail}  <{InstanceId}>  phys=<{PhysicalKey}>";
}

public static class DeviceEnumerator
{
    public static List<InputDevice> Enumerate()
    {
        var result = new List<InputDevice>();
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, size) == unchecked((uint)-1) || count == 0)
            return result;

        var list = new RAWINPUTDEVICELIST[count];
        uint got = GetRawInputDeviceList(list, ref count, size);
        if (got == unchecked((uint)-1)) return result;

        for (int i = 0; i < got; i++)
        {
            var dev = Describe(list[i].hDevice, list[i].dwType);
            if (dev != null) result.Add(dev);
        }
        return result;
    }

    public static InputDevice? Describe(IntPtr hDevice, uint rimType)
    {
        var kind = rimType switch
        {
            RIM_TYPEKEYBOARD => DeviceKind.Keyboard,
            RIM_TYPEMOUSE => DeviceKind.Mouse,
            _ => DeviceKind.Other
        };
        if (kind == DeviceKind.Other) return null; // 키보드/마우스만 관심

        string rawName = GetRawName(hDevice);
        if (string.IsNullOrEmpty(rawName)) return null;

        string instanceId = RawNameToInstanceId(rawName);
        var (detail, keyCount) = GetDetail(hDevice, kind);

        var (origin, transport, friendly, physicalKey) = Classify(instanceId);
        return new InputDevice(hDevice, kind, origin, transport, friendly, instanceId, physicalKey, detail, keyCount);
    }

    private static string GetRawName(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            uint r = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref size);
            if (r == unchecked((uint)-1)) return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static (string detail, int keyCount) GetDetail(IntPtr hDevice, DeviceKind kind)
    {
        var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
        uint size = info.cbSize;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(info, buf, false);
            uint r = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICEINFO, buf, ref size);
            if (r == unchecked((uint)-1)) return ("", 0);
            info = Marshal.PtrToStructure<RID_DEVICE_INFO>(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }

        return kind switch
        {
            DeviceKind.Keyboard => ($"keys={info.keyboard.dwNumberOfKeysTotal} fn={info.keyboard.dwNumberOfFunctionKeys} type={info.keyboard.dwType}", (int)info.keyboard.dwNumberOfKeysTotal),
            DeviceKind.Mouse => ($"buttons={info.mouse.dwNumberOfButtons} hwheel={info.mouse.fHasHorizontalWheel}", 0),
            _ => ("", 0)
        };
    }

    /// <summary>
    /// Raw Input 장치 이름(\\?\HID#VID_xxxx&PID_yyyy&MI_00#7&abc#{guid})을
    /// 장치 인스턴스 ID(HID\VID_xxxx&PID_yyyy&MI_00\7&abc)로 변환한다.
    /// </summary>
    public static string RawNameToInstanceId(string rawName)
    {
        string s = rawName;
        if (s.StartsWith(@"\\?\") || s.StartsWith(@"\??\")) s = s[4..];
        int guidIdx = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidIdx >= 0) s = s[..guidIdx];
        return s.Replace('#', '\\');
    }

    /// <summary>
    /// 장치 인스턴스 ID에서 부모 체인을 따라 올라가며 버스 종류를 판정한다.
    /// </summary>
    public static (DeviceOrigin origin, string transport, string name, string physicalKey) Classify(string instanceId)
    {
        var chain = new List<(uint devInst, string id)>();
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS)
        {
            uint cur = devInst;
            for (int depth = 0; depth < 12; depth++)
            {
                string id = GetDeviceId(cur) ?? "";
                chain.Add((cur, id));
                if (CM_Get_Parent(out uint parent, cur, 0) != CR_SUCCESS) break;
                cur = parent;
            }
        }

        if (chain.Count == 0)
            return (DeviceOrigin.Unknown, "?", instanceId, StripCollection(instanceId));

        string name = BestName(chain);

        // 1) Bluetooth (클래식/BLE). USB 블루투스 동글이라도 체인 앞쪽에 BTH 노드가 먼저 나온다.
        //    물리 키 = 라디오에 가장 가까운 BTH 노드(BTHENUM\Dev_xxx / BTHLE\Dev_xxx, 블루투스 주소 포함)
        //    (BTH\MS_BTHLE 같은 열거기 노드는 모든 BLE 장치가 공유하므로 키로 쓰면 안 된다)
        if (chain.Any(c => StartsWithAny(c.id, "BTHENUM\\", "BTHLE\\", "BTHLEDEVICE\\", "BTH\\")))
        {
            var devNodes = chain.Where(c => StartsWithAny(c.id, "BTHENUM\\", "BTHLE\\")).ToList();
            string key = devNodes.Count > 0 ? devNodes[^1].id : StripCollection(instanceId);
            return (DeviceOrigin.External, "Bluetooth", name, key);
        }

        // 2) USB (유선 또는 2.4GHz 수신기). 물리 키 = 복합 장치 부모 노드(USB\VID_xxxx&PID_yyyy\serial, &MI_ 없는 것)
        var usbNodes = chain.Where(c => c.id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (usbNodes.Count > 0 || chain.Any(c => c.id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)))
        {
            string key = usbNodes.FirstOrDefault(c => !c.id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).id
                         ?? (usbNodes.Count > 0 ? usbNodes[0].id : StripCollection(instanceId));
            return (DeviceOrigin.External, "USB", name, key);
        }

        // 3) 내장: PS/2(ACPI\PNP03xx) 또는 I2C HID (체인에 ACPI가 있고 USB/BTH가 없음)
        if (chain.Any(c => c.id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)))
        {
            string first = chain[0].id;
            string transport = first.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) ? "PS/2" : "I2C";
            return (DeviceOrigin.Internal, transport, name, StripCollection(instanceId));
        }

        // 4) 그 외 (ROOT\, SWD\, TERMINPUT_BUS\, VMBUS\ 등) → 가상 장치
        return (DeviceOrigin.Virtual, "Virtual", name, StripCollection(instanceId));
    }

    /// <summary> HID\XXX&Col02\5&abc&0&0001  ->  HID\XXX\5&abc&0  (컬렉션 번호 제거) </summary>
    private static string StripCollection(string instanceId)
    {
        int col = instanceId.IndexOf("&Col", StringComparison.OrdinalIgnoreCase);
        if (col < 0) return instanceId;
        int slash = instanceId.IndexOf('\\', col);
        string head = instanceId[..col];
        if (slash < 0) return head;
        string tail = instanceId[slash..];
        int lastAmp = tail.LastIndexOf('&');
        if (lastAmp > 0) tail = tail[..lastAmp];
        return head + tail;
    }

    private static bool StartsWithAny(string s, params string[] prefixes) =>
        prefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string BestName(List<(uint devInst, string id)> chain)
    {
        // Bluetooth: 체인에서 라디오에 가장 가까운 BTH 노드(BTHENUM\Dev_xxx, BTHLE\Dev_xxx)의 NAME 이 사용자에게 보이는 제품명.
        //            그 아래 서비스 노드는 "Bluetooth 저에너지 GATT 준수 HID 장치" 같은 일반 이름이라 건너뛴다.
        var btNodes = chain.Where(c => StartsWithAny(c.id, "BTHENUM\\", "BTHLE\\", "BTHLEDEVICE\\")).ToList();
        for (int i = btNodes.Count - 1; i >= 0; i--)
        {
            var n = GetDevNodeString(btNodes[i].devInst, DEVPKEY_NAME) ?? GetDevNodeString(btNodes[i].devInst, DEVPKEY_Device_FriendlyName);
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }

        // USB: 인터페이스 노드(&MI_xx)의 BusReportedDeviceDesc 는 인터페이스 문자열("System Control" 등)이라
        //      복합 장치 부모 노드(USB\VID_xxxx&PID_yyyy\serial)의 제품 문자열을 우선 사용한다.
        var usbNodes = chain.Where(c => c.id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var node in usbNodes.Where(c => !c.id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).Concat(usbNodes))
        {
            var n = GetDevNodeString(node.devInst, DEVPKEY_Device_BusReportedDeviceDesc);
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }
        var self = chain[0].devInst;
        return GetDevNodeString(self, DEVPKEY_Device_BusReportedDeviceDesc)
            ?? GetDevNodeString(self, DEVPKEY_NAME)
            ?? GetDevNodeString(self, DEVPKEY_Device_DeviceDesc)
            ?? chain[0].id;
    }
}
