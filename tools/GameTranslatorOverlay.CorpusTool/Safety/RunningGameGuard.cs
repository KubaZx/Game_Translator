using System.Diagnostics;
using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.CorpusTool.Safety;

public interface IProcessLister
{
    IReadOnlyCollection<string> RunningProcessNames();
}

public sealed class SystemProcessLister : IProcessLister
{
    public IReadOnlyCollection<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
            }
        }
        return names;
    }
}

public static class RunningGameGuard
{
    public static IReadOnlyList<string> CandidateProcessNames(GameProfile profile, string gameDirectory, string? gameRoot = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in profile.ProcessNames)
        {
            var bare = StripExe(name);
            if (bare.Length > 0) names.Add(bare);
        }
        foreach (var directory in new[] { gameDirectory, gameRoot })
        {
            if (directory is null || !Directory.Exists(directory)) continue;
            foreach (var exe in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                names.Add(StripExe(Path.GetFileName(exe)));
            }
        }
        return names.ToList();
    }

    public static IReadOnlyList<string> FindRunning(GameProfile profile, string gameDirectory, IProcessLister lister, string? gameRoot = null)
    {
        var running = new HashSet<string>(lister.RunningProcessNames().Select(StripExe), StringComparer.OrdinalIgnoreCase);
        return CandidateProcessNames(profile, gameDirectory, gameRoot).Where(running.Contains).OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string StripExe(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
