using System;
using System.Threading.Tasks;
namespace AuroraLauncher;
static class Program {
  static async Task Main(string[] args) {
    AppPaths.EnsureLayout();
    if (args.Length > 0 && args[0] == "news") {
      var items = await NewsService.List(true);
      Console.WriteLine("news=" + items.Count);
      foreach (var item in items) Console.WriteLine($"{item?["id"]} | {item?["title"]} | {item?["content"]?.GetValue<string>()?.Length ?? 0} | {item?["image"]}");
      return;
    }
    if (args.Length > 0 && args[0] == "repair") {
      AppPaths.UseRoot("D:\\Minecraft\\.minecraft");
      var repaired = InstanceArchiveService.ImportExisting(false);
      Console.WriteLine("repaired=" + repaired);
      return;
    }
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
