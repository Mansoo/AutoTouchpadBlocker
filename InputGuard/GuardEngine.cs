using System.Text.Json;
using System.Text.Json.Serialization;

namespace InputGuard;

/// <summary> 설정 파일에 저장되는 장치별 역할. </summary>
public sealed class DeviceRole
{
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "";
    public bool Keyboard { get; set; }
    public bool Mouse { get; set; }
    /// <summary> true 면 사용자가 직접 지정한 값. 학습으로 덮어쓰지 않는다. </summary>
    public bool Manual { get; set; }
    /// <summary> 내장 장치용: true 면 비활성화 대상에서 제외 (예: 핫키 전용 키보드) </summary>
    public bool Exclude { get; set; }
    public DateTime? LearnedAt { get; set; }
}

public enum DisableMethod
{
    /// <summary> CM_Disable_DevNode (장치 관리자 "사용 안 함") </summary>
    Disable,
    /// <summary> (사용 안 함, 복구용) 장치 노드를 제거하고 되돌릴 때 부모를 재열거 </summary>
    Remove,
}

/// <summary> InputGuard 가 꺼 둔 장치. 비정상 종료 후 재시작 시 복구에 쓴다. </summary>
public sealed class DisabledEntry
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    public DisableMethod Method { get; set; }
    /// <summary> Remove 방식일 때 재열거할 부모 노드 </summary>
    public string? ParentInstanceId { get; set; }
    public DateTime DisabledAt { get; set; }
}

public sealed class Settings
{
    /// <summary> 이 횟수 이상 키 이벤트(누름+뗌)가 들어오면 키보드로 인정 </summary>
    public int KeyboardEventThreshold { get; set; } = 6;
    /// <summary> 이 횟수 이상 마우스 이벤트(이동/버튼/휠)가 들어오면 마우스로 인정 </summary>
    public int MouseEventThreshold { get; set; } = 40;
    /// <summary> 내장 키보드 컬렉션의 키 수가 이보다 적으면 핫키 장치로 보고 기본 제외 </summary>
    public int MinInternalKeyboardKeys { get; set; } = 20;
    /// <summary>
    /// 외장 키보드가 있을 때 내장 키보드도 끌지 여부. 기본 false (마우스/터치패드만 처리).
    /// 많은 노트북의 내장 키보드(PS/2, I2C)는 Windows 가 비활성화를 거부하므로 true 로 해도 동작하지 않을 수 있다.
    /// </summary>
    public bool GuardKeyboard { get; set; } = false;
    public Dictionary<string, DeviceRole> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DisabledEntry> DisabledDevices { get; set; } = new();

    public static string FilePath => Path.Combine(Paths.DataDir, "InputGuard.settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null)
                {
                    s.Devices = new Dictionary<string, DeviceRole>(s.Devices, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { /* 손상된 파일은 무시하고 새로 시작 */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }
}

public static class Paths
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InputGuard");
    public static string LogPath => Path.Combine(DataDir, "InputGuard.log");
}

/// <summary> 같은 물리 장치에 속한 HID 컬렉션(키보드/마우스)들을 묶은 것. </summary>
public sealed class PhysicalDevice
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required DeviceOrigin Origin { get; init; }
    public required string Transport { get; init; }
    public List<InputDevice> Collections { get; } = new();

    public bool HasKeyboardCollection => Collections.Any(c => c.Kind == DeviceKind.Keyboard);
    public bool HasMouseCollection => Collections.Any(c => c.Kind == DeviceKind.Mouse);

    // 세션 내 입력 활동
    public int KeyboardEvents { get; set; }
    public int MouseEvents { get; set; }
    public DateTime? LastKeyboard { get; set; }
    public DateTime? LastMouse { get; set; }

    // 판정된 역할 (학습 또는 수동)
    public bool IsKeyboard { get; set; }
    public bool IsMouse { get; set; }
    public bool Manual { get; set; }
    /// <summary> 내장 장치: 비활성화 대상에서 제외 </summary>
    public bool Exclude { get; set; }

    public string RoleText
    {
        get
        {
            var parts = new List<string>();
            if (IsKeyboard) parts.Add("키보드");
            if (IsMouse) parts.Add("마우스");
            if (parts.Count == 0) return Origin == DeviceOrigin.External ? "미정(입력 대기)" : "없음";
            string how = Manual ? "수동" : Origin == DeviceOrigin.Internal ? "내장" : "학습";
            return $"{string.Join("+", parts)} ({how})";
        }
    }
}

/// <summary> 외장 키보드/마우스 존재 여부에 대한 판정 결과. </summary>
public sealed record GuardState(
    IReadOnlyList<PhysicalDevice> ExternalKeyboards,
    IReadOnlyList<PhysicalDevice> ExternalMice,
    IReadOnlyList<PhysicalDevice> InternalKeyboards,
    IReadOnlyList<PhysicalDevice> InternalPointers)
{
    public bool ExternalKeyboardPresent => ExternalKeyboards.Count > 0;
    public bool ExternalMousePresent => ExternalMice.Count > 0;

    public bool SameAs(GuardState? other) =>
        other != null &&
        ExternalKeyboardPresent == other.ExternalKeyboardPresent &&
        ExternalMousePresent == other.ExternalMousePresent &&
        ExternalKeyboards.Select(d => d.Key).SequenceEqual(other.ExternalKeyboards.Select(d => d.Key)) &&
        ExternalMice.Select(d => d.Key).SequenceEqual(other.ExternalMice.Select(d => d.Key));
}

/// <summary>
/// 장치 열거 → 물리 장치 묶기 → 입력 활동 집계 → 역할 학습 → 상태 판정.
/// UI 스레드에서만 호출한다.
/// </summary>
public sealed class GuardEngine
{
    public Settings Settings { get; private set; }
    public IReadOnlyDictionary<string, PhysicalDevice> Devices => _devices;

    private Dictionary<string, PhysicalDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<IntPtr, (PhysicalDevice dev, DeviceKind kind)> _byHandle = new();
    private readonly Action<string> _log;

    public GuardEngine(Action<string> log)
    {
        _log = log;
        Settings = Settings.Load();
    }

    public sealed record RescanResult(List<PhysicalDevice> Added, List<PhysicalDevice> Removed);

    /// <summary> 장치를 다시 열거해 물리 장치 맵을 갱신한다. 기존 장치의 활동 카운터는 유지. </summary>
    public RescanResult Rescan()
    {
        var collections = DeviceEnumerator.Enumerate();
        var fresh = new Dictionary<string, PhysicalDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in collections)
        {
            if (!fresh.TryGetValue(c.PhysicalKey, out var pd))
            {
                pd = new PhysicalDevice { Key = c.PhysicalKey, Name = c.Name, Origin = c.Origin, Transport = c.Transport };
                if (_devices.TryGetValue(c.PhysicalKey, out var old))
                {
                    pd.KeyboardEvents = old.KeyboardEvents; pd.MouseEvents = old.MouseEvents;
                    pd.LastKeyboard = old.LastKeyboard; pd.LastMouse = old.LastMouse;
                }
                fresh[pd.Key] = pd;
            }
            pd.Collections.Add(c);
        }
        // 역할 적용은 컬렉션이 모두 모인 뒤에 (내장 장치는 컬렉션 종류로 역할을 정하므로)
        bool dirty = false;
        foreach (var pd in fresh.Values) dirty |= ApplyRole(pd);
        if (dirty) { try { Settings.Save(); } catch (Exception ex) { _log("설정 저장 실패: " + ex.Message); } }

        var added = fresh.Values.Where(d => !_devices.ContainsKey(d.Key)).ToList();
        var removed = _devices.Values.Where(d => !fresh.ContainsKey(d.Key)).ToList();

        _devices = fresh;
        _byHandle = new Dictionary<IntPtr, (PhysicalDevice, DeviceKind)>();
        foreach (var pd in fresh.Values)
            foreach (var c in pd.Collections)
                _byHandle[c.Handle] = (pd, c.Kind);

        return new RescanResult(added, removed);
    }

    /// <summary> 설정의 역할을 장치에 적용. 설정 파일에 새 항목을 만들었으면 true. </summary>
    private bool ApplyRole(PhysicalDevice pd)
    {
        if (Settings.Devices.TryGetValue(pd.Key, out var role))
        {
            pd.IsKeyboard = role.Keyboard;
            pd.IsMouse = role.Mouse;
            pd.Manual = role.Manual;
            pd.Exclude = role.Exclude;
            return false;
        }
        if (pd.Origin == DeviceOrigin.Internal)
        {
            // 내장 장치는 컬렉션 종류 그대로 역할로 본다 (학습 불필요).
            // 키 수가 아주 적은 키보드(핫키 전용 등)는 기본으로 비활성화 대상에서 제외하고, 설정 파일에서 바꿀 수 있게 항목을 만들어 둔다.
            pd.IsKeyboard = pd.HasKeyboardCollection;
            pd.IsMouse = pd.HasMouseCollection;
            pd.Exclude = pd.HasKeyboardCollection && !pd.HasMouseCollection &&
                         pd.Collections.Where(c => c.Kind == DeviceKind.Keyboard).All(c => c.KeyCount < Settings.MinInternalKeyboardKeys);
            Settings.Devices[pd.Key] = new DeviceRole
            {
                Name = pd.Name, Transport = pd.Transport,
                Keyboard = pd.IsKeyboard, Mouse = pd.IsMouse, Manual = false, Exclude = pd.Exclude,
            };
            if (pd.Exclude) _log($"[제외] {pd.Name} [{pd.Transport}] 키 수가 적어 비활성화 대상에서 제외 (설정 파일 Exclude 로 변경 가능)");
            return true;
        }
        // 외장 장치는 실제 입력이 들어올 때까지 역할 미정 (세션 중 이미 학습했으면 유지)
        pd.IsKeyboard = pd.IsKeyboard || pd.KeyboardEvents >= Settings.KeyboardEventThreshold;
        pd.IsMouse = pd.IsMouse || pd.MouseEvents >= Settings.MouseEventThreshold;
        return false;
    }

    /// <summary>
    /// WM_INPUT 한 건. 역할이 새로 학습되면 그 장치를 반환, 아니면 null.
    /// </summary>
    public PhysicalDevice? OnRawInput(IntPtr hDevice, uint rimType)
    {
        if (hDevice == IntPtr.Zero) return null; // SendInput 등 주입 입력
        if (!_byHandle.TryGetValue(hDevice, out var entry))
        {
            // 재열거 전에 새 장치에서 입력이 들어온 경우: 즉석에서 설명해서 등록
            var c = DeviceEnumerator.Describe(hDevice, rimType);
            if (c == null) return null;
            if (!_devices.TryGetValue(c.PhysicalKey, out var pd))
            {
                pd = new PhysicalDevice { Key = c.PhysicalKey, Name = c.Name, Origin = c.Origin, Transport = c.Transport };
                _devices[pd.Key] = pd;
            }
            pd.Collections.Add(c);
            ApplyRole(pd);
            entry = (pd, c.Kind);
            _byHandle[hDevice] = entry;
        }

        var (dev, kind) = entry;
        var now = DateTime.Now;
        if (kind == DeviceKind.Keyboard) { dev.KeyboardEvents++; dev.LastKeyboard = now; }
        else if (kind == DeviceKind.Mouse) { dev.MouseEvents++; dev.LastMouse = now; }
        else return null;

        if (dev.Manual || dev.Origin != DeviceOrigin.External) return null;

        bool learned = false;
        if (kind == DeviceKind.Keyboard && !dev.IsKeyboard && dev.KeyboardEvents >= Settings.KeyboardEventThreshold)
        { dev.IsKeyboard = true; learned = true; }
        if (kind == DeviceKind.Mouse && !dev.IsMouse && dev.MouseEvents >= Settings.MouseEventThreshold)
        { dev.IsMouse = true; learned = true; }

        if (!learned) return null;
        PersistRole(dev);
        _log($"[학습] {dev.Name} [{dev.Transport}] → {dev.RoleText}  (kb={dev.KeyboardEvents}, ms={dev.MouseEvents})");
        return dev;
    }

    private void PersistRole(PhysicalDevice dev)
    {
        Settings.Devices[dev.Key] = new DeviceRole
        {
            Name = dev.Name, Transport = dev.Transport,
            Keyboard = dev.IsKeyboard, Mouse = dev.IsMouse, Manual = dev.Manual, Exclude = dev.Exclude,
            LearnedAt = DateTime.Now,
        };
        try { Settings.Save(); } catch (Exception ex) { _log("설정 저장 실패: " + ex.Message); }
    }

    /// <summary> 학습된 역할을 모두 지운다(수동 지정은 유지). </summary>
    public void ResetLearnedRoles()
    {
        foreach (var k in Settings.Devices.Where(kv => !kv.Value.Manual).Select(kv => kv.Key).ToList())
            Settings.Devices.Remove(k);
        try { Settings.Save(); } catch (Exception ex) { _log("설정 저장 실패: " + ex.Message); }
        foreach (var pd in _devices.Values)
        {
            if (pd.Manual || pd.Origin != DeviceOrigin.External) continue;
            pd.IsKeyboard = false; pd.IsMouse = false;
            pd.KeyboardEvents = 0; pd.MouseEvents = 0;
        }
        _log("[초기화] 학습된 역할을 모두 지웠습니다.");
    }

    public void ReloadSettings()
    {
        Settings = Settings.Load();
        foreach (var pd in _devices.Values) ApplyRole(pd);
    }

    public GuardState ComputeState()
    {
        var ext = _devices.Values.Where(d => d.Origin == DeviceOrigin.External).OrderBy(d => d.Name).ToList();
        var inn = _devices.Values.Where(d => d.Origin == DeviceOrigin.Internal).OrderBy(d => d.Name).ToList();
        bool kb = Settings.GuardKeyboard; // 키보드를 다루지 않으면 상태에서 아예 뺀다 (알림/비활성화 모두 무시)
        return new GuardState(
            kb ? ext.Where(d => d.IsKeyboard).ToList() : new List<PhysicalDevice>(),
            ext.Where(d => d.IsMouse).ToList(),
            kb ? inn.Where(d => d.IsKeyboard).ToList() : new List<PhysicalDevice>(),
            inn.Where(d => d.IsMouse).ToList());
    }
}
