using Brim.Plugins;

namespace Brim.Ai.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void Brim_accepts_the_manifest()
    {
        var dir = Directory.CreateTempSubdirectory("brim-ai-").FullName;
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Manifest", "plugin.json"), Path.Combine(dir, "plugin.json"));
            File.WriteAllText(Path.Combine(dir, "BrimAi.exe"), "");

            var manifest = PluginManifest.Load(dir);

            Assert.Equal("de.brim.ai", manifest.Id);
            Assert.Equal(["assistant", "apps"], manifest.Widgets.Select(w => w.Id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
