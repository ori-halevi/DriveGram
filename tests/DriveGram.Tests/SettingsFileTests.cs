using System.Text.Json;
using DriveGram.Host;

namespace DriveGram.Tests;

/// <summary>
/// settings.json holds the language AND the look. Each is saved from its own place, so the real risk is
/// one of them quietly dropping the other (the language used to be written as the whole file). Runs
/// against an isolated DRIVEGRAM_ROOT (never the real one).
/// </summary>
[Collection("isolated-root")]
public sealed class SettingsFileTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "drivegram-tests", "settings-" + Guid.NewGuid().ToString("N"));
    readonly string? _previous = Environment.GetEnvironmentVariable("DRIVEGRAM_ROOT");
    readonly string _language;

    public SettingsFileTests()
    {
        Environment.SetEnvironmentVariable("DRIVEGRAM_ROOT", _root);
        _language = L.Preference;
    }

    public void Dispose()
    {
        L.Set(_language); // the test changes the process-wide language
        Environment.SetEnvironmentVariable("DRIVEGRAM_ROOT", _previous);
        L.Load();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void SavingOneSettingKeepsTheOthers()
    {
        AppSettings.Update(s => s.Theme = "dark");
        L.Set("he"); // written by a different part of the program

        var saved = AppSettings.Load();
        Assert.Equal("dark", saved.Theme);
        Assert.Equal("he", saved.Language);

        AppSettings.Update(s => s.Theme = "rose");
        Assert.Equal("he", AppSettings.Load().Language);
        Assert.Equal("rose", AppSettings.Load().Theme);
    }

    [Fact]
    public void FieldsOfANewerVersionSurviveASave()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(AppSettings.Path, """{"Language":"en","Theme":"dark","accentFromWallpaper":true}""");

        L.Load();
        Assert.Equal("en", L.Preference);
        L.Set("he");

        using var doc = JsonDocument.Parse(File.ReadAllText(AppSettings.Path));
        Assert.True(doc.RootElement.GetProperty("accentFromWallpaper").GetBoolean());
        Assert.Equal("dark", doc.RootElement.GetProperty("Theme").GetString());
        Assert.Equal("he", doc.RootElement.GetProperty("Language").GetString());
    }

    [Fact]
    public void TheCaseOfTheFieldNamesDoesNotMatter()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(AppSettings.Path, """{"language":"he","theme":"rose"}""");
        var s = AppSettings.Load();
        Assert.Equal("he", s.Language);
        Assert.Equal("rose", s.Theme);
    }

    [Fact]
    public void AnUnreadableFileLeavesTheDefaults()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(AppSettings.Path, "{ this is not json");

        var s = AppSettings.Load();
        Assert.Equal("auto", s.Language);
        Assert.Equal("auto", s.Theme);
    }
}
