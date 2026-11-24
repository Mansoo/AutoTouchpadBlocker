using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace InputGuard;

/// <summary>
/// 로그인 시 관리자 권한으로 자동 시작하기 위한 작업 스케줄러 등록 (UAC 프롬프트 없이 승격 실행).
/// </summary>
public static class Autostart
{
    private const string TaskName = "InputGuard";

    // 작업 정의에 반드시 들어 있어야 하는 설정. 없으면 예전 방식(schtasks /SC ONLOGON 기본값)으로 등록된 작업이다.
    private const string NoBatteryStop = "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>";
    private const string NoTimeLimit = "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>";

    public static bool IsEnabled()
    {
        var (code, _) = Run($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static (bool ok, string message) Enable()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        string tmp = Path.Combine(Path.GetTempPath(), $"InputGuard.task.{Guid.NewGuid():N}.xml");
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            File.WriteAllText(tmp, BuildTaskXml(exe, id.Name, id.User?.Value ?? id.Name), Encoding.Unicode);
            var (code, output) = Run($"/Create /F /TN \"{TaskName}\" /XML \"{tmp}\"");
            return (code == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public static (bool ok, string message) Disable()
    {
        var (code, output) = Run($"/Delete /F /TN \"{TaskName}\"");
        return (code == 0, output.Trim());
    }

    /// <summary>
    /// 예전 버전이 등록한 작업은 schtasks 기본값 때문에 배터리 상태에서는 시작되지 않고(수동 실행도 Queued 에 머무름),
    /// 배터리 전환 시 중지·72시간 실행 제한 조건이 붙어 있다. 그런 작업이 남아 있으면 새 정의로 다시 등록한다.
    /// 한 일이 있으면 로그에 남길 문장을, 없으면 null 을 반환.
    /// </summary>
    public static string? UpgradeIfOutdated()
    {
        var (code, xml) = Run($"/Query /TN \"{TaskName}\" /XML");
        if (code != 0) return null; // 등록 안 됨
        if (xml.Contains(NoBatteryStop, StringComparison.OrdinalIgnoreCase) &&
            xml.Contains(NoTimeLimit, StringComparison.OrdinalIgnoreCase)) return null;

        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        if (!xml.Contains(exe, StringComparison.OrdinalIgnoreCase))
            return $"예전 방식으로 등록된 작업이 다른 실행 파일을 가리켜 그대로 둠 (메뉴에서 자동 시작을 껐다 켜면 갱신됨). 현재 exe={exe}";

        var (ok, msg) = Enable();
        return $"예전 방식 작업(배터리 조건/72시간 실행 제한)을 새 정의로 재등록 {(ok ? "성공" : "실패")}: {msg}";
    }

    /// <summary>
    /// schtasks /SC ONLOGON 기본값과 다른 점:
    /// 배터리에서도 시작하고 배터리로 바뀌어도 멈추지 않음, 실행 시간 제한 없음.
    /// RestartOnFailure 는 넣지 않는다: 프로세스가 외부에서 강제 종료(종료 코드 1)돼도 작업 스케줄러가 다시 띄우지 않는 것을 확인했다.
    /// </summary>
    private static string BuildTaskXml(string exe, string userName, string userSid) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>InputGuard: 외장 마우스가 연결되면 내장 터치패드를 끈다.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userName)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            {NoBatteryStop}
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            {NoTimeLimit}
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exe)}</Command>
            </Exec>
          </Actions>
        </Task>
        """;

    private static (int code, string output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
