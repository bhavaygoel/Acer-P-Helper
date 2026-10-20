using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace PredatorControlApp
{
    // Recognises a game in the foreground without the user listing it. A foreground app counts
    // as a game when Windows' own Game Bar has it on record, when it lives in a game library
    // folder (Steam, Epic, GOG, Xbox...), or when it is fullscreen and loading the GPU.
    [SupportedOSPlatform("windows")]
    internal static class GameDetector
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int FullscreenGpuLoad = 40;

        private static readonly string[] LibraryFolders =
        {
            @"\steamapps\common\", @"\Epic Games\", @"\GOG Galaxy\Games\", @"\GOG Games\",
            @"\XboxGames\", @"\Riot Games\", @"\Ubisoft Game Launcher\games\",
            @"\EA Games\", @"\Electronic Arts\", @"\Battle.net\", @"\Rockstar Games\",
        };

        // Launchers, updaters and helpers that live inside library folders but aren't games
        private static readonly string[] HelperNameParts =
        {
            "launcher", "client", "crash", "setup", "unins", "update", "installer", "helper", "service",
            "redist", "vcredist", "dxsetup", "overlay", "bootstrap",
        };

        private static readonly HashSet<string> NeverGames = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vlc", "mpc-hc64", "PotPlayerMini64",
            "Spotify", "Discord", "steam", "steamwebhelper", "EpicGamesLauncher", "PredatorControlApp",
            "ApplicationFrameHost", "SearchHost", "StartMenuExperienceHost", "LockApp", "Taskmgr",
            "obs64", "Teams", "ms-teams", "WINWORD", "EXCEL", "POWERPNT", "Code", "devenv",
        };

        private static HashSet<string> _gameBarExes = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _gameBarLoaded = DateTime.MinValue;

        public readonly record struct Candidate(int Pid, string Path, string Name);

        /// <summary>Returns the foreground app if it looks like a game, otherwise null.</summary>
        public static Candidate? DetectForegroundGame()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;

            string? path = GetProcessPath(pid);
            if (string.IsNullOrEmpty(path)) return null;

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (NeverGames.Contains(name)) return null;
            if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                return null;

            var candidate = new Candidate((int)pid, path, name);

            if (IsKnownToGameBar(path)) return candidate;
            if (IsInGameLibrary(path, name)) return candidate;
            if (IsFullscreen(hwnd) && Nvml.GpuUtilization() >= FullscreenGpuLoad) return candidate;

            return null;
        }

        public static bool IsRunning(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsInGameLibrary(string path, string name)
        {
            if (!LibraryFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))) return false;
            return !HelperNameParts.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsKnownToGameBar(string path)
        {
            if (DateTime.UtcNow - _gameBarLoaded > TimeSpan.FromMinutes(1))
            {
                _gameBarExes = LoadGameBarExes();
                _gameBarLoaded = DateTime.UtcNow;
            }
            return _gameBarExes.Contains(path);
        }

        // Windows records every game Game Bar recognises under GameConfigStore
        private static HashSet<string> LoadGameBarExes()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
                if (children == null) return set;
                foreach (var sub in children.GetSubKeyNames())
                {
                    using var key = children.OpenSubKey(sub);
                    if (key?.GetValue("MatchedExeFullPath") is string exe && !string.IsNullOrWhiteSpace(exe))
                        set.Add(exe);
                }
            }
            catch { }
            return set;
        }

        private static bool IsFullscreen(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var r)) return false;
            var screen = Screen.FromHandle(hwnd).Bounds;
            return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
        }

        private static string? GetProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }
}
