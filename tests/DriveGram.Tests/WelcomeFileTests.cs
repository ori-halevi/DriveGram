using DriveGram.Host;

namespace DriveGram.Tests;

public class WelcomeFileTests
{
    [Fact]
    public void TheNoteIsWrittenOnceAndNeverOverwritesTheUsersCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-welcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(WelcomeFile.TryWrite(dir));
            var path = Path.Combine(dir, WelcomeFile.Name);
            Assert.StartsWith("# ", File.ReadAllText(path));
            File.WriteAllText(path, "edited by the user");
            Assert.True(WelcomeFile.TryWrite(dir));
            Assert.Equal("edited by the user", File.ReadAllText(path));
            Assert.True(WelcomeFile.Text.Length < 2500); // short: a warning, not a manual
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
