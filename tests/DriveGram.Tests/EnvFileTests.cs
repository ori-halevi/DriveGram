using DriveGram.Host;

namespace DriveGram.Tests;

public class EnvFileTests
{
    [Fact]
    public void KeysInAnyCaseQuotesCommentsAndExportAreRead()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, ["# a comment", "api_id=12345", "# api_hash=old", "export API_HASH=\"0123456789abcdef0123456789abcdef\"", "OTHER=x # note"]);
            var d = EnvFile.Read(path).ToDictionary(x => x.Key, x => x.Value);
            Assert.Equal("12345", d["API_ID"]);
            Assert.Equal("0123456789abcdef0123456789abcdef", d["API_HASH"]);
            Assert.Equal("x", d["OTHER"]);
            Assert.Equal(3, d.Count);
        }
        finally { File.Delete(path); }
    }
}
