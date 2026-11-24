using static InputGuard.NativeMethods;

namespace InputGuard;

/// <summary>
/// 판정 상태에 따라 내장 터치패드(설정에 따라 키보드도) 장치 노드를 끄고 켠다 (관리자 권한 필요).
/// CM_Disable_DevNode = 장치 관리자의 "사용 안 함". PERSIST 플래그는 쓰지 않지만, 재부팅 때 Windows 가 되돌려 주는지는
/// 확인하지 못했으므로 거기에 기대지 않는다. 꺼 둔 장치는 설정 파일에 기억해, 비정상 종료 후 다시 시작하면 먼저 복구한다.
/// </summary>
public sealed class DeviceGuard
{
    private const uint CM_DISABLE_UI_NOT_OK = 0x00000004;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    private readonly Func<Settings> _settings;
    private readonly Action<string> _log;
    private readonly Dictionary<string, DateTime> _failedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> true 면 아무것도 끄지 않고, 이미 꺼 둔 것은 모두 되돌린다. </summary>
    public bool Paused { get; set; }

    public DeviceGuard(Func<Settings> settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    public IReadOnlyList<DisabledEntry> Disabled => _settings().DisabledDevices;

    public bool IsDisabled(string instanceId) =>
        _settings().DisabledDevices.Any(e => e.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));

    public sealed class ApplyResult
    {
        public List<string> Disabled { get; } = new();
        public List<string> Enabled { get; } = new();
        public List<string> Errors { get; } = new();
        public bool Any => Disabled.Count > 0 || Enabled.Count > 0 || Errors.Count > 0;

        public string Text
        {
            get
            {
                var lines = new List<string>();
                if (Disabled.Count > 0) lines.Add("끔: " + string.Join(", ", Disabled));
                if (Enabled.Count > 0) lines.Add("켬: " + string.Join(", ", Enabled));
                if (Errors.Count > 0) lines.Add("실패: " + string.Join("; ", Errors));
                return string.Join("\n", lines);
            }
        }
    }

    /// <summary> 상태에 맞춰 내장 장치를 켜고 끈다. 멱등. </summary>
    public ApplyResult Apply(GuardState state, IEnumerable<PhysicalDevice> devices)
    {
        bool wantKb = !Paused && state.ExternalKeyboardPresent;
        bool wantMs = !Paused && state.ExternalMousePresent;
        var settings = _settings();
        var result = new ApplyResult();

        // 1) 더 이상 꺼둘 이유가 없는 장치는 다시 켠다
        foreach (var e in settings.DisabledDevices.ToList())
        {
            bool want = e.Kind == DeviceKind.Keyboard ? wantKb : e.Kind == DeviceKind.Mouse && wantMs;
            if (want) continue;
            if (Enable(e, result)) settings.DisabledDevices.Remove(e);
        }

        // 2) 꺼야 하는 내장 장치를 끈다.
        //    Raw Input 이 알려주는 것은 HID 컬렉션 하나(예: "HID 규격 마우스")인데, 정밀 터치패드는 별도의 "HID 규격 터치 패드" 컬렉션으로
        //    입력하므로 그 하나만 꺼서는 소용없다. HID 컬렉션이면 부모(I2C/USB HID 장치 전체)를 끄고, 부모가 거부하면 모든 하위 컬렉션을 끈다.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pd in devices.Where(d => d.Origin == DeviceOrigin.Internal && !d.Exclude))
        {
            foreach (var c in pd.Collections)
            {
                bool want = c.Kind == DeviceKind.Keyboard ? wantKb : c.Kind == DeviceKind.Mouse && wantMs;
                if (!want) continue;
                string target = ResolveTargetNode(c.InstanceId);
                if (!seen.Add(target) || IsDisabled(target)) continue;
                if (_failedAt.TryGetValue(target, out var t) && DateTime.Now - t < RetryInterval) continue; // 같은 실패 반복 방지

                var entry = new DisabledEntry { InstanceId = target, Name = pd.Name, Kind = c.Kind, DisabledAt = DateTime.Now };
                bool ok = Disable(entry, result);
                if (!ok && target != c.InstanceId)
                {
                    // 부모가 거부 → 하위 컬렉션을 하나씩
                    ok = false;
                    foreach (string childId in ChildrenOf(target))
                    {
                        if (IsDisabled(childId)) { ok = true; continue; }
                        var ce = new DisabledEntry { InstanceId = childId, Name = pd.Name, Kind = c.Kind, DisabledAt = DateTime.Now };
                        if (Disable(ce, result)) { settings.DisabledDevices.Add(ce); ok = true; }
                    }
                    entry = null;
                }
                if (ok) { if (entry != null) settings.DisabledDevices.Add(entry); _failedAt.Remove(target); }
                else _failedAt[target] = DateTime.Now;
            }
        }

        Save(settings);
        return result;
    }

    /// <summary> HID 컬렉션 노드(HID\...)면 그 부모(HID 장치 전체)의 인스턴스 ID, 아니면 자기 자신. </summary>
    private static string ResolveTargetNode(string instanceId)
    {
        if (!instanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)) return instanceId;
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return instanceId;
        if (CM_Get_Parent(out uint parent, devInst, 0) != CR_SUCCESS) return instanceId;
        string? parentId = GetDeviceId(parent);
        // 부모가 루트/버스 컨트롤러처럼 너무 위면 안 됨: 부모 ID 가 HID 전송 장치(ACPI\, USB\, BTH...)일 때만 올라간다
        if (string.IsNullOrEmpty(parentId) || parentId.StartsWith("HTREE\\", StringComparison.OrdinalIgnoreCase)) return instanceId;
        return parentId;
    }

    private static IEnumerable<string> ChildrenOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) yield break;
        if (CM_Get_Child(out uint child, devInst, 0) != CR_SUCCESS) yield break;
        while (true)
        {
            string? id = GetDeviceId(child);
            if (!string.IsNullOrEmpty(id)) yield return id;
            if (CM_Get_Sibling(out uint next, child, 0) != CR_SUCCESS) break;
            child = next;
        }
    }

    /// <summary> 기억해 둔 장치를 전부 다시 켠다 (시작/종료/충돌/일시중지). </summary>
    public ApplyResult EnableAll(string reason)
    {
        var settings = _settings();
        var result = new ApplyResult();
        if (settings.DisabledDevices.Count == 0) return result;

        _log($"[복구] {reason}: {settings.DisabledDevices.Count}개 장치 켜기 시도");
        foreach (var e in settings.DisabledDevices.ToList())
            if (Enable(e, result)) settings.DisabledDevices.Remove(e);
        Save(settings);
        return result;
    }

    // ---------- 끄기 ----------

    private bool Disable(DisabledEntry e, ApplyResult result)
    {
        string label = $"{e.Name}({KindKo(e.Kind)})";
        int cr = CM_Locate_DevNodeW(out uint devInst, e.InstanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (cr != CR_SUCCESS)
        {
            Fail(result, $"{label} 찾기 실패: {CrName(cr)}");
            return false;
        }

        cr = CM_Disable_DevNode(devInst, CM_DISABLE_UI_NOT_OK);
        if (cr == CR_SUCCESS || cr == CR_NEED_RESTART)
        {
            e.Method = DisableMethod.Disable;
            _log($"[끔:disable] {label} <{e.InstanceId}> {CrName(cr)} / {StatusText(devInst)}");
            result.Disabled.Add(label);
            return true;
        }
        if (cr != CR_NOT_DISABLEABLE)
        {
            Fail(result, $"{label} 비활성화 실패: {CrName(cr)}");
            return false;
        }

        // Windows 가 비활성화를 거부하는 장치 (내장 PS/2·I2C 키보드가 흔함). 제거(uninstall) 방식은 드라이버가 거부하거나
        // 재부팅 전까지 적용되지 않아 실시간 토글에 쓸 수 없으므로 실패로 보고만 한다.
        Fail(result, $"{label} Windows 가 비활성화를 거부함({CrName(cr)})");
        return false;
    }

    // ---------- 켜기 ----------

    private bool Enable(DisabledEntry e, ApplyResult result)
    {
        string label = $"{e.Name}({KindKo(e.Kind)})";
        return e.Method == DisableMethod.Remove
            ? EnableByReenumerate(e, label, result) // 이전 버전이 남긴 항목 복구용
            : EnableByCm(e, label, result);
    }

    private bool EnableByCm(DisabledEntry e, string label, ApplyResult result)
    {
        int cr = CM_Locate_DevNodeW(out uint devInst, e.InstanceId, CM_LOCATE_DEVNODE_PHANTOM);
        if (cr != CR_SUCCESS)
        {
            // 장치 노드 자체가 사라졌으면 더 이상 우리가 켤 것이 없다
            _log($"[켬] {label} <{e.InstanceId}> 노드 없음({CrName(cr)}) - 목록에서 제거");
            return true;
        }
        cr = CM_Enable_DevNode(devInst, 0);
        if (cr != CR_SUCCESS && cr != CR_NEED_RESTART)
        {
            Fail(result, $"{label} 켜기 실패: {CrName(cr)}");
            return false;
        }
        _log($"[켬:enable] {label} <{e.InstanceId}> {CrName(cr)} / {StatusText(devInst)}");
        result.Enabled.Add(label);
        return true;
    }

    private bool EnableByReenumerate(DisabledEntry e, string label, ApplyResult result)
    {
        // 이미 돌아와 있으면 할 일 없음
        if (IsPresent(e.InstanceId))
        {
            _log($"[켬] {label} <{e.InstanceId}> 이미 존재함 - 목록에서 제거");
            result.Enabled.Add(label);
            return true;
        }

        // 1) 부모 노드 재열거
        bool ok = false;
        if (!string.IsNullOrEmpty(e.ParentInstanceId) &&
            CM_Locate_DevNodeW(out uint parent, e.ParentInstanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
        {
            int cr = CM_Reenumerate_DevNode(parent, CM_REENUMERATE_SYNCHRONOUS);
            _log($"[켬:reenum] {label} 부모 <{e.ParentInstanceId}> 재열거: {CrName(cr)}");
            ok = cr == CR_SUCCESS && IsPresent(e.InstanceId);
        }
        // 2) 안 돌아오면 루트부터 재열거 (장치 관리자의 "하드웨어 변경 사항 검색")
        if (!ok && RootDevNode() is uint root)
        {
            int cr = CM_Reenumerate_DevNode(root, CM_REENUMERATE_SYNCHRONOUS);
            _log($"[켬:reenum] {label} 루트 재열거: {CrName(cr)}");
            ok = cr == CR_SUCCESS;
        }
        if (!ok)
        {
            Fail(result, $"{label} 재열거 실패 (장치 관리자에서 '하드웨어 변경 사항 검색' 필요)");
            return false;
        }

        bool back = IsPresent(e.InstanceId);
        _log($"[켬:reenum] {label} <{e.InstanceId}> 재설치 {(back ? "확인" : "대기 중(비동기 설치)")}");
        result.Enabled.Add(label + (back ? "" : "(설치 중)"));
        return true;
    }

    private static bool IsPresent(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return false;
        if (CM_Get_DevNode_Status(out uint status, out _, devInst, 0) != CR_SUCCESS) return false;
        return (status & DN_STARTED) != 0;
    }

    private static string StatusText(uint devInst)
    {
        if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) != CR_SUCCESS) return "status=?";
        bool started = (status & DN_STARTED) != 0;
        bool hasProblem = (status & DN_HAS_PROBLEM) != 0;
        string prob = hasProblem ? (problem == CM_PROB_DISABLED ? "disabled" : $"problem={problem}") : "ok";
        return $"started={started} {prob}";
    }

    private void Fail(ApplyResult result, string msg)
    {
        _log("[오류] " + msg);
        result.Errors.Add(msg);
    }

    private void Save(Settings s)
    {
        try { s.Save(); } catch (Exception ex) { _log("설정 저장 실패: " + ex.Message); }
    }

    private static string KindKo(DeviceKind k) => k == DeviceKind.Keyboard ? "키보드" : k == DeviceKind.Mouse ? "터치패드" : "기타";
}
