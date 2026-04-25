using System.Diagnostics;
using Xunit;

namespace SmartCovers.Tests;

public class UtilityMethodTests
{
    [Fact]
    public void CleanupTemp_ExistingFile_DeletesIt()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"cleanup-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmpFile, "test");
        Assert.True(File.Exists(tmpFile));

        CoverImageProvider.CleanupTemp(tmpFile);
        Assert.False(File.Exists(tmpFile));
    }

    [Fact]
    public void CleanupTemp_NonexistentFile_DoesNotThrow()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.tmp");
        CoverImageProvider.CleanupTemp(tmpFile);
        // Should not throw
    }

    [Fact]
    public void TryKill_AlreadyExitedProcess_DoesNotThrow()
    {
        // Start a process that exits immediately
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "echo",
            ArgumentList = { "test" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        process.WaitForExit(5000);

        // Process has already exited - TryKill should not throw
        CoverImageProvider.TryKill(process);
    }

    [Fact]
    public void TryKill_RunningProcess_KillsIt()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "sleep",
            ArgumentList = { "60" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        Assert.False(process.HasExited);

        CoverImageProvider.TryKill(process);
        process.WaitForExit(5000);
        Assert.True(process.HasExited);
    }
}
