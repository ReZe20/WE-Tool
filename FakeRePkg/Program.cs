using System.Text.Json;
using System.Text.Json.Nodes;

// 模拟 RePKG_Re.exe batch --manifest <path> —— 崩溃重启测试的替身子进程。
// 行为由脚本文件驱动:环境变量 FAKE_REPKG_SCRIPT 或 exe 同目录 fake_script.json。
// 脚本格式:
//   { "behaviors": [
//       { "crash": true,  "processIds": ["0"], "entriesPerWallpaper": 3, "exitCode": 42 },
//       { "crash": false }
//   ] }
// 每次启动按"启动次数"消费一个 behavior(计数落盘在脚本同目录 fake_counter.txt,从 0 开始);
// 超过 behaviors 长度时重复最后一个。
// crash:true  → 只对 processIds 的壁纸发 start + 部分 entry 事件,然后以 exitCode 退出(模拟崩溃)
// crash:false → 所有壁纸完整事件流 + batch done,exit 0

const int TotalEntries = 4;

var scriptPath = Environment.GetEnvironmentVariable("FAKE_REPKG_SCRIPT");
if (string.IsNullOrEmpty(scriptPath))
    scriptPath = Path.Combine(AppContext.BaseDirectory, "fake_script.json");
if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine("fake repkg: script not found: " + scriptPath);
    return 2;
}

// 解析 manifest(仅取壁纸 id 列表)
string? manifestPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--manifest" && i + 1 < args.Length)
        manifestPath = args[i + 1];
}
if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
{
    Console.Error.WriteLine("fake repkg: manifest not found: " + manifestPath);
    return 2;
}

var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
var wallpaperIds = manifest["wallpapers"]!.AsArray()
    .Select(w => (string)w!["id"]!)
    .ToList();

var script = JsonNode.Parse(File.ReadAllText(scriptPath))!;
var behaviors = script["behaviors"]!.AsArray();

// 启动次数计数(落盘,跨进程保持)
var counterPath = Path.Combine(Path.GetDirectoryName(scriptPath)!, "fake_counter.txt");
int runIndex = 0;
if (File.Exists(counterPath) && int.TryParse(File.ReadAllText(counterPath).Trim(), out var existing))
    runIndex = existing;
File.WriteAllText(counterPath, (runIndex + 1).ToString());

var behavior = runIndex < behaviors.Count ? behaviors[runIndex]! : behaviors[behaviors.Count - 1]!;
bool crash = behavior["crash"]?.GetValue<bool>() ?? false;

void EmitStart(string id) =>
    Console.WriteLine($"{{\"id\":\"{id}\",\"type\":\"wallpaper\",\"action\":\"start\",\"total_entries\":{TotalEntries}}}");
void EmitEntry(string id, int pos) =>
    Console.WriteLine($"{{\"id\":\"{id}\",\"type\":\"entry\",\"entry\":\"txt/fake_{pos}.txt\",\"pos\":{pos},\"total\":{TotalEntries}}}");
void EmitDone(string id) =>
    Console.WriteLine($"{{\"id\":\"{id}\",\"type\":\"wallpaper\",\"action\":\"done\"}}");

if (!crash)
{
    // 成功:全部壁纸完整事件流 + batch done
    foreach (var id in wallpaperIds)
    {
        EmitStart(id);
        for (int i = 1; i <= TotalEntries; i++)
            EmitEntry(id, i);
        EmitDone(id);
    }
    Console.WriteLine("{\"type\":\"batch\",\"action\":\"done\"}");
    return 0;
}

// 崩溃:只对 processIds 的壁纸发部分事件,然后非 0 退出
var processIds = behavior["processIds"]?.AsArray().Select(p => (string)p!).ToList()
    ?? new List<string> { wallpaperIds.FirstOrDefault() ?? "0" };
int entriesPerWallpaper = behavior["entriesPerWallpaper"]?.GetValue<int>() ?? 2;
int exitCode = behavior["exitCode"]?.GetValue<int>() ?? 42;

foreach (var id in processIds)
{
    if (!wallpaperIds.Contains(id)) continue;
    EmitStart(id);
    for (int i = 1; i <= Math.Min(entriesPerWallpaper, TotalEntries); i++)
        EmitEntry(id, i);
}

return exitCode;
