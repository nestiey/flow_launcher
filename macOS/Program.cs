using System.Diagnostics;
using Flow.Launcher;

try
{
    if (!OperatingSystem.IsMacOS())
        throw new PlatformNotSupportedException("이 시험판은 macOS에서 실행합니다.");

    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "FLOW Launcher");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var folder = Path.Combine(root, "instances", "sunlit-valley");
    IProgress<string> progress = new ConsoleProgress();
    await PackDownload.InstallAsync(folder, progress, stop.Token);
    await JavaRuntime.PrepareAsync(root);
    Console.WriteLine("브라우저에서 Microsoft 계정에 로그인하세요.");
    var session = await MinecraftLogin.SignInAsync(LoginSetup.GetClientId(), stop.Token);
    return await GameLaunch.RunAsync(root, session, 8, progress, () => { }, stop.Token, stop.Token);
}
catch (OperationCanceledException) { return 130; }
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}

namespace Flow.Launcher
{
    internal static class LoginSetup
    {
        public static string GetClientId() => "d7e3b8e8-c39b-489c-810e-8d9daa388fc3";
    }

    internal static class LoginCache
    {
        public static string? Read() => null;
        public static void Clear() { }
        public static void Write(string token) { }
    }

    internal static class JavaRuntime
    {
        private static string? java;
        public static async Task<string> PrepareAsync(string root)
        {
            if (java is not null) return java;
            var start = new ProcessStartInfo("/usr/libexec/java_home")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-F");
            start.ArgumentList.Add("-v");
            start.ArgumentList.Add("17");
            using var process = Process.Start(start)!;
            var home = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var candidate = Path.Combine(home.Trim(), "bin", "java");
            if (process.ExitCode != 0 || !File.Exists(candidate))
                throw new IOException("macOS용 Java 17을 설치한 뒤 다시 실행하세요.");
            return java = candidate;
        }
    }
}
