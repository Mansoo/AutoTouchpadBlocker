using System.Diagnostics;

namespace InputGuard;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // --autostart on|off : 로그인 시 자동 시작(작업 스케줄러, 최고 권한) 등록/해제. 관리자 권한(매니페스트)으로 실행되므로 여기서 처리 가능.
        // --exit : 등록/해제만 하고 종료. 없으면 그대로 트레이 앱으로 계속 실행.
        bool exitAfter = args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));
        int idx = Array.FindIndex(args, a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            bool on = idx + 1 >= args.Length || !args[idx + 1].Equals("off", StringComparison.OrdinalIgnoreCase);
            var (ok, msg) = on ? Autostart.Enable() : Autostart.Disable();
            string line = $"[자동 시작] {(on ? "등록" : "해제")} {(ok ? "성공" : "실패")}: {msg} (exe={Environment.ProcessPath})";
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.AppendAllText(Paths.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { /* ignore */ }
            if (!ok)
                MessageBox.Show("자동 시작 " + (on ? "등록" : "해제") + " 실패:\n" + msg, "InputGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (exitAfter) return;
        }

        // 중복 실행 방지
        using var mutex = new Mutex(true, "InputGuard.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("InputGuard가 이미 실행 중입니다. 트레이 아이콘을 확인하세요.", "InputGuard",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayAppContext());
    }
}
