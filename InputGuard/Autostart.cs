using System.Diagnostics;

namespace InputGuard;

/// <summary>
/// 로그인 시 관리자 권한으로 자동 시작하기 위한 작업 스케줄러 등록 (UAC 프롬프트 없이 승격 실행).
/// </summary>
public static class Autostart
{
    private const string TaskName = "InputGuard";

    public static bool IsEnabled()
    {
        var (code, _) = Run($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static (bool ok, string message) Enable()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var (code, output) = Run($"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\"");
        return (code == 0, output.Trim());
    }

    public static (bool ok, string message) Disable()
    {
        var (code, output) = Run($"/Delete /F /TN \"{TaskName}\"");
        return (code == 0, output.Trim());
    }

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
