using System;
using System.Threading.Tasks;
namespace AuroraLauncher;
static class Program {
  static async Task Main(string[] args) {
    AppPaths.EnsureLayout();
    if (args.Length > 0 && args[0] == "vanilla") {
      try {
        var result = await MinecraftInstallService.InstallVanilla("1.20.1", true, "1.20.1 Test", default, false);
        Console.WriteLine("result=" + result.ToJsonString());
      } catch (Exception e) { Console.WriteLine("error: " + e); }
      return;
    }
    var m = await VersionService.Manifest(false);
    Console.WriteLine("manifest=" + (m["versions"] as System.Text.Json.Nodes.JsonArray)?.Count);
  }
}
