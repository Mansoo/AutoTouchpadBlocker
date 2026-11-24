using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using static InputGuard.NativeMethods;

namespace InputGuard;

/// <summary>
/// 트레이 아이콘 + 숨은 메시지 창.
/// 장치 연결 변화(WM_INPUT_DEVICE_CHANGE)와 실제 입력(WM_INPUT)을 받아 GuardEngine 에 넘기고,
/// 외장 마우스 판정 상태가 바뀌면 DeviceGuard 로 내장 터치패드를 끄고 켠 뒤 알림을 띄운다. (키보드는 설정 GuardKeyboard 가 true 일 때만)
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly DeviceWatcherWindow _watcher;
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly GuardEngine _engine;
    private readonly DeviceGuard _guard;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly bool _isAdmin;
    private GuardState? _lastState;
    private bool _exiting;
    private bool _displayOn = true;

    public TrayAppContext()
    {
        Directory.CreateDirectory(Paths.DataDir);
        _engine = new GuardEngine(Log);
        _guard = new DeviceGuard(() => _engine.Settings, Log);
        _isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        var menu = new ContextMenuStrip();
        menu.Items.Add("장치 목록 / 상태 보기", null, (_, _) => ShowDeviceList());
        menu.Items.Add("지금 다시 검사", null, (_, _) => Rescan(showAlways: true));
        _pauseItem = new ToolStripMenuItem("일시 중지 (터치패드 켬)", null, (_, _) => TogglePause()) { CheckOnClick = false };
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        _autostartItem = new ToolStripMenuItem("로그인 시 자동 시작 (관리자)", null, (_, _) => ToggleAutostart());
        menu.Items.Add(_autostartItem);
        menu.Items.Add("설정 파일 열기", null, (_, _) => OpenFile(Settings.FilePath, "{\n}"));
        menu.Items.Add("설정 다시 읽기", null, (_, _) => { _engine.ReloadSettings(); EvaluateState("설정 다시 읽음"); });
        menu.Items.Add("학습된 역할 초기화", null, (_, _) => ResetRoles());
        menu.Items.Add("로그 열기", null, (_, _) => OpenFile(Paths.LogPath, ""));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료 (터치패드 켬)", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => _autostartItem.Checked = Autostart.IsEnabled();

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "InputGuard",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowDeviceList();

        _debounce = new System.Windows.Forms.Timer { Interval = 700 };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rescan(showAlways: false); };

        // 안전장치: 어떤 경로로 끝나든 내장 장치를 되돌린다
        Application.ThreadException += (_, e) => { Log("[예외] " + e.Exception); SafeEnableAll("처리되지 않은 예외"); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log("[예외] " + e.ExceptionObject); SafeEnableAll("치명적 예외"); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeEnableAll("프로세스 종료");
        SystemEvents.SessionEnding += (_, _) => SafeEnableAll("로그오프/시스템 종료");

        _watcher = new DeviceWatcherWindow(
            onDeviceChange: () => { _debounce.Stop(); _debounce.Start(); },
            onRawInput: OnRawInput,
            onDisplayState: OnDisplayState,
            onSuspend: () => EnterHold("절전 진입"));

        Log($"=== InputGuard 시작 (관리자: {_isAdmin}) ===");
        if (!_isAdmin)
            Log("[경고] 관리자 권한이 아니어서 장치 비활성화가 실패할 수 있습니다.");
        else if (Autostart.UpgradeIfOutdated() is string upgraded)
            Log("[자동 시작] " + upgraded);
        if (!_watcher.PowerNotifyRegistered)
            Log("[경고] 화면 상태 알림 등록 실패: 절전 대비(화면이 꺼질 때 터치패드 켜 두기)가 동작하지 않습니다.");

        // 1) 이전 실행이 비정상 종료되어 꺼진 채 남은 장치가 있으면 먼저 복구
        var recovered = _guard.EnableAll("시작 시 복구");

        // 2) 현재 장치 열거 → 상태 판정 → 적용
        _engine.Rescan();
        foreach (var d in _engine.Devices.Values) LogPhysical("  ", d);
        _lastState = _engine.ComputeState();
        var applied = _guard.Apply(_lastState, _engine.Devices.Values);
        UpdateTrayText(_lastState);

        var sb = new StringBuilder();
        if (recovered.Any) sb.AppendLine("이전 실행에서 꺼진 장치 복구: " + recovered.Text);
        sb.Append(StateText(_lastState));
        if (applied.Any) sb.Append('\n').Append(applied.Text);
        if (!_isAdmin) sb.Append("\n⚠ 관리자 권한 아님: 비활성화가 동작하지 않을 수 있음");
        _tray.ShowBalloonTip(6000, "InputGuard 시작", sb.ToString(), applied.Errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    // ---------- 이벤트 ----------

    private void OnRawInput(IntPtr hDevice, uint rimType)
    {
        var learned = _engine.OnRawInput(hDevice, rimType, out bool externalMouseInput);

        // 절전 대비 해제: 화면이 켜진 상태에서 외장 마우스가 실제로 움직였을 때만. (화면이 꺼진 동안의 입력은 가방 속에서 눌린 것일 수 있다)
        if (externalMouseInput && _guard.Hold && _displayOn)
        {
            _guard.Hold = false;
            EvaluateState("외장 마우스 입력 확인: 절전 대비 해제", quiet: true);
        }

        if (learned != null)
            EvaluateState($"입력 활동으로 역할 판정: {learned.Name} [{learned.Transport}] → {learned.RoleText}");
    }

    private void OnDisplayState(bool on)
    {
        _displayOn = on;
        if (!on) EnterHold("화면 꺼짐");
        else if (_guard.Hold) EvaluateState("화면 켜짐", quiet: true); // 외장 마우스가 이미 없으면 여기서 해제된다
    }

    /// <summary>
    /// 화면이 꺼지거나 절전에 들어갈 때 터치패드를 미리 켜 둔다. 최신 대기 모드에서는 이 프로세스가 멈추기 때문에,
    /// 그 사이 마우스가 끊기면 깨어난 뒤에야 터치패드를 켤 수 있고 그동안 포인터를 쓸 수 없다.
    /// 깨어난 뒤 외장 마우스 입력이 들어오면 OnRawInput 에서 해제한다.
    /// </summary>
    private void EnterHold(string reason)
    {
        if (_exiting || _guard.Hold) return;
        _guard.Hold = true;
        EvaluateState($"{reason}: 외장 마우스 입력이 들어올 때까지 터치패드를 켜 둠", quiet: true);
    }

    private void Rescan(bool showAlways)
    {
        if (_exiting) return;
        var r = _engine.Rescan();
        var sb = new StringBuilder();
        // 내장 장치는 우리가 끄고 켤 때마다 목록에서 사라졌다 나타나므로 알림에는 외장만 보여준다
        foreach (var d in r.Added)
        {
            LogPhysical("[연결] ", d);
            if (d.Origin == DeviceOrigin.External) sb.AppendLine($"+ 연결: {d.Name} [{d.Transport}] ({d.RoleText})");
        }
        foreach (var d in r.Removed)
        {
            LogPhysical("[해제] ", d);
            if (d.Origin == DeviceOrigin.External) sb.AppendLine($"- 해제: {d.Name} [{d.Transport}]");
        }

        bool notified = EvaluateState(sb.Length > 0 ? sb.ToString().TrimEnd() : null);

        if (!notified && (showAlways || sb.Length > 0))
        {
            string title = sb.Length > 0 ? "입력 장치 변경 (판정 상태 동일)" : "변화 없음";
            _tray.ShowBalloonTip(4000, title, (sb.Length > 0 ? sb + "\n" : "") + StateText(_engine.ComputeState()), ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// 상태를 다시 계산해 내장 장치에 적용하고, 상태가 바뀌었거나 실제로 켜고 끈 것이 있으면 알림을 띄운다. 알림을 띄웠으면 true.
    /// quiet 면 로그만 남긴다 (화면이 꺼지고 켜질 때마다 알림이 뜨지 않도록).
    /// </summary>
    private bool EvaluateState(string? reason, bool quiet = false)
    {
        if (_exiting) return false;
        var state = _engine.ComputeState();
        // 화면이 켜져 있고 외장 장치가 하나도 없으면 절전 대비는 할 일이 없다. 이후에 연결되는 마우스는 평소처럼 처리한다.
        if (_guard.Hold && _displayOn && !state.ExternalMousePresent && !state.ExternalKeyboardPresent)
            _guard.Hold = false;
        UpdateTrayText(state);

        bool changed = !state.SameAs(_lastState);
        var prev = _lastState;
        _lastState = state;

        var applied = _guard.Apply(state, _engine.Devices.Values);
        if (quiet)
        {
            Log($"[절전] {reason} | {StateText(state).Replace('\n', ' ')}" + (applied.Any ? " | " + applied.Text.Replace('\n', ' ') : ""));
            return false;
        }
        if (!changed && !applied.Any) return false;

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(reason)) sb.AppendLine(reason);
        sb.Append(StateText(state));
        if (applied.Any) sb.Append('\n').Append(applied.Text);

        string title;
        bool kbOn = state.ExternalKeyboardPresent, msOn = state.ExternalMousePresent;
        bool kbWas = prev?.ExternalKeyboardPresent ?? false, msWas = prev?.ExternalMousePresent ?? false;
        if (_guard.Paused) title = "일시 중지 중";
        else if (_guard.Hold) title = "절전 대비 중 (터치패드 켜 둠)";
        else if ((kbOn && !kbWas) || (msOn && !msWas)) title = "외장 마우스 감지 → 터치패드 끔";
        else if ((!kbOn && kbWas) || (!msOn && msWas)) title = "외장 마우스 없음 → 터치패드 켬";
        else title = "상태 적용";

        Log($"[상태] {title} | {StateText(state).Replace('\n', ' ')}" + (applied.Any ? " | " + applied.Text.Replace('\n', ' ') : ""));
        _tray.ShowBalloonTip(6000, title, sb.ToString(), applied.Errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        return true;
    }

    private void TogglePause()
    {
        _guard.Paused = !_guard.Paused;
        _pauseItem.Checked = _guard.Paused;
        Log(_guard.Paused ? "[일시 중지] 내장 장치 모두 켬" : "[재개]");
        EvaluateState(_guard.Paused ? "일시 중지" : "재개");
    }

    private void SafeEnableAll(string reason)
    {
        try
        {
            var r = _guard.EnableAll(reason);
            if (r.Any) Log($"[복구 결과] {r.Text.Replace('\n', ' ')}");
        }
        catch (Exception ex) { Log("[복구 실패] " + ex.Message); }
    }

    // ---------- 표시 ----------

    private string StateText(GuardState s)
    {
        string ms = s.ExternalMousePresent ? string.Join(", ", s.ExternalMice.Select(d => d.Name)) : "없음";
        string plan;
        if (_guard.Paused) plan = "→ 일시 중지: 터치패드 켜짐";
        else if (_guard.Hold) plan = "→ 절전 대비: 외장 마우스를 움직일 때까지 터치패드 켜짐";
        else
        {
            var targets = new List<string>();
            if (s.ExternalMousePresent) targets.Add("내장 터치패드");
            if (s.ExternalKeyboardPresent) targets.Add("내장 키보드");
            plan = targets.Count > 0 ? $"→ {string.Join(", ", targets)} 끔" : "→ 외장 마우스 없음: 터치패드 켬";
        }
        string kbLine = _engine.Settings.GuardKeyboard
            ? $"외장 키보드: {(s.ExternalKeyboardPresent ? string.Join(", ", s.ExternalKeyboards.Select(d => d.Name)) : "없음")}\n"
            : "";
        return $"{kbLine}외장 마우스: {ms}\n{plan}";
    }

    private void UpdateTrayText(GuardState s)
    {
        string text = _guard.Paused
            ? "InputGuard | 일시 중지"
            : _guard.Hold ? "InputGuard | 절전 대비 → 터치패드 켜짐"
            : $"InputGuard | 외장 마우스 {(s.ExternalMousePresent ? "있음 → 터치패드 꺼짐" : "없음 → 터치패드 켜짐")}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowDeviceList()
    {
        var sb = new StringBuilder();
        sb.AppendLine(StateText(_engine.ComputeState()));
        sb.AppendLine($"관리자 권한: {(_isAdmin ? "예" : "아니오")}   일시 중지: {(_guard.Paused ? "예" : "아니오")}   현재 꺼둔 장치: {_guard.Disabled.Count}개");
        sb.AppendLine();
        sb.AppendLine($"임계치: 키보드 이벤트 {_engine.Settings.KeyboardEventThreshold}회, 마우스 이벤트 {_engine.Settings.MouseEventThreshold}회");
        sb.AppendLine($"설정 파일: {Settings.FilePath}");
        sb.AppendLine("  외장: \"Keyboard\"/\"Mouse\" 를 손으로 고치고 \"Manual\": true 로 두면 학습이 덮어쓰지 않음");
        sb.AppendLine("  내장: \"Exclude\": true 로 두면 비활성화 대상에서 제외.  \"GuardKeyboard\": true 면 내장 키보드도 대상 (이 PC는 Windows 가 거부)");
        sb.AppendLine();

        foreach (var grp in _engine.Devices.Values.GroupBy(d => d.Origin).OrderBy(g => g.Key))
        {
            sb.AppendLine($"[{OriginKo(grp.Key)}]");
            foreach (var d in grp.OrderBy(d => d.Name))
            {
                string excl = d.Origin == DeviceOrigin.Internal && d.Exclude ? "  [비활성화 대상 제외]" : "";
                sb.AppendLine($"  {d.Name} [{d.Transport}]  역할: {d.RoleText}{excl}");
                if (d.Origin == DeviceOrigin.External)
                    sb.AppendLine($"      활동: 키 {d.KeyboardEvents}회{Last(d.LastKeyboard)}, 마우스 {d.MouseEvents}회{Last(d.LastMouse)}");
                sb.AppendLine($"      물리 키: {d.Key}");
                foreach (var c in d.Collections)
                    sb.AppendLine($"        - {KindKo(c.Kind)} 컬렉션 ({c.Detail})  {c.InstanceId}");
            }
            sb.AppendLine();
        }

        if (_guard.Disabled.Count > 0)
        {
            sb.AppendLine("[현재 꺼둔 장치]");
            foreach (var e in _guard.Disabled)
                sb.AppendLine($"  {e.Name} ({KindKo(e.Kind)})  {e.DisabledAt:HH:mm:ss}  {e.InstanceId}");
        }

        using var f = new Form
        {
            Text = "InputGuard - 장치 / 상태",
            Width = 1000, Height = 700, StartPosition = FormStartPosition.CenterScreen,
            Icon = SystemIcons.Shield,
        };
        var tb = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Dock = DockStyle.Fill, Font = new Font("Consolas", 10), Text = sb.ToString(),
        };
        f.Controls.Add(tb);
        f.Shown += (_, _) => tb.Select(0, 0);
        f.ShowDialog();

        static string Last(DateTime? t) => t.HasValue ? $" (최근 {t:HH:mm:ss})" : "";
    }

    private void ResetRoles()
    {
        var r = MessageBox.Show("학습된 외장 장치 역할을 모두 지우고 다시 입력을 기다릴까요?\n(수동 지정한 항목은 유지, 내장 장치는 즉시 다시 켜짐)",
            "InputGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        _engine.ResetLearnedRoles();
        EvaluateState("학습된 역할 초기화");
    }

    private void ToggleAutostart()
    {
        bool on = Autostart.IsEnabled();
        var (ok, msg) = on ? Autostart.Disable() : Autostart.Enable();
        Log($"[자동 시작] {(on ? "해제" : "등록")} {(ok ? "성공" : "실패")}: {msg}");
        _autostartItem.Checked = ok ? !on : on;
        if (!ok) MessageBox.Show("작업 스케줄러 등록/해제 실패:\n" + msg, "InputGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else _tray.ShowBalloonTip(3000, "InputGuard", on ? "로그인 시 자동 시작을 해제했습니다." : $"로그인 시 자동 시작을 등록했습니다.\n{Environment.ProcessPath}", ToolTipIcon.Info);
    }

    private static void OpenFile(string path, string initialContent)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, initialContent);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "InputGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LogPhysical(string prefix, PhysicalDevice d)
    {
        Log($"{prefix}{d.Origin,-8} {d.Transport,-9} {d.Name}  역할={d.RoleText}{(d.Exclude ? " [제외]" : "")}  phys=<{d.Key}>");
        foreach (var c in d.Collections) Log($"      - {c.Kind,-8} {c.Detail}  <{c.InstanceId}>");
    }

    private static readonly object LogLock = new();
    private static void Log(string line)
    {
        lock (LogLock)
        {
            try { File.AppendAllText(Paths.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
            catch { /* ignore */ }
        }
    }

    private static string KindKo(DeviceKind k) => k switch
    {
        DeviceKind.Keyboard => "키보드",
        DeviceKind.Mouse => "마우스",
        _ => "기타"
    };

    private static string OriginKo(DeviceOrigin o) => o switch
    {
        DeviceOrigin.Internal => "내장",
        DeviceOrigin.External => "외장",
        DeviceOrigin.Virtual => "가상",
        _ => "불명"
    };

    protected override void ExitThreadCore()
    {
        _exiting = true;
        _debounce.Stop();
        SafeEnableAll("종료");
        _tray.Visible = false;
        _tray.Dispose();
        _watcher.DestroyHandle();
        Log("=== InputGuard 종료 ===");
        base.ExitThreadCore();
    }
}

/// <summary>
/// Raw Input 의 WM_INPUT_DEVICE_CHANGE(장치 연결/해제)와 WM_INPUT(실제 입력, 포커스 없어도 수신)을 받는 숨은 창.
/// </summary>
internal sealed class DeviceWatcherWindow : NativeWindow
{
    private readonly Action _onDeviceChange;
    private readonly Action<IntPtr, uint> _onRawInput;
    private readonly Action<bool> _onDisplayState;
    private readonly Action _onSuspend;
    private readonly IntPtr _buf;
    private readonly uint _headerSize;
    private IntPtr _powerNotify;

    public DeviceWatcherWindow(Action onDeviceChange, Action<IntPtr, uint> onRawInput, Action<bool> onDisplayState, Action onSuspend)
    {
        _onDeviceChange = onDeviceChange;
        _onRawInput = onRawInput;
        _onDisplayState = onDisplayState;
        _onSuspend = onSuspend;
        _headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        _buf = Marshal.AllocHGlobal((int)_headerSize);

        CreateHandle(new CreateParams { Caption = "InputGuardWatcher", X = 0, Y = 0, Width = 0, Height = 0, Style = 0, ExStyle = 0 });

        uint flags = RIDEV_DEVNOTIFY | RIDEV_INPUTSINK;
        var rid = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = flags, hwndTarget = Handle }, // keyboard
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = flags, hwndTarget = Handle }, // mouse
        };
        if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException("RegisterRawInputDevices 실패: " + Marshal.GetLastWin32Error());

        // 화면 상태 알림 (등록 직후 현재 상태가 한 번 통지된다). 실패해도 나머지 기능은 그대로 동작한다.
        Guid displayState = GUID_CONSOLE_DISPLAY_STATE;
        _powerNotify = RegisterPowerSettingNotification(Handle, ref displayState, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
    }

    public bool PowerNotifyRegistered => _powerNotify != IntPtr.Zero;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_INPUT_DEVICE_CHANGE:
                _onDeviceChange();
                return;

            case WM_INPUT:
            {
                uint size = _headerSize;
                uint r = GetRawInputData(m.LParam, RID_HEADER, _buf, ref size, _headerSize);
                if (r != unchecked((uint)-1) && r >= _headerSize)
                {
                    var h = Marshal.PtrToStructure<RAWINPUTHEADER>(_buf);
                    _onRawInput(h.hDevice, h.dwType);
                }
                break; // DefWindowProc 가 버퍼 정리를 하도록 base 로 넘긴다
            }

            case WM_POWERBROADCAST:
            {
                int evt = (int)m.WParam;
                if (evt == PBT_APMSUSPEND) _onSuspend();
                else if (evt == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero &&
                         Marshal.PtrToStructure<Guid>(m.LParam) == GUID_CONSOLE_DISPLAY_STATE)
                {
                    int state = Marshal.ReadInt32(m.LParam, POWERBROADCAST_SETTING_DATA_OFFSET);
                    if (state == 0) _onDisplayState(false);      // 꺼짐
                    else if (state == 1) _onDisplayState(true);  // 켜짐 (2 = 어두워짐은 무시)
                }
                break;
            }
        }
        base.WndProc(ref m);
    }

    public override void DestroyHandle()
    {
        if (_powerNotify != IntPtr.Zero) { UnregisterPowerSettingNotification(_powerNotify); _powerNotify = IntPtr.Zero; }
        base.DestroyHandle();
        Marshal.FreeHGlobal(_buf);
    }
}
