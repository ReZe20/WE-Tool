using System.Text.Json.Nodes;
using WE_Tool.Models;
using WE_Tool.Service;

// 崩溃重启循环自动化测试:真实 RepkgCliService + 脚本化 FakeRePkg(编译产物即 RePKG_Re.exe)。
// FakeRePkg 按 fake_script.json 的 behaviors 演出(崩溃/成功),启动次数落盘 fake_counter.txt。
// 用法: TestBatchRestart [scenario...]  场景: 1..5 或 All(默认)

Console.OutputEncoding = System.Text.Encoding.UTF8;

var scenarios = args.Length > 0 ? args : new[] { "All" };
int failures = 0;

void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + msg);
    if (!cond) failures++;
}

var tempRoot = Path.Combine(Path.GetTempPath(), "repkg_batch_restart_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
Console.WriteLine($"临时目录: {tempRoot}");

// 壁纸夹具:0 字节 x.pkg 即可(服务只看扩展名判断 pkg 壁纸,假 exe 不解包)
var wpA = Path.Combine(tempRoot, "wpA");
var wpB = Path.Combine(tempRoot, "wpB");
Directory.CreateDirectory(wpA);
Directory.CreateDirectory(wpB);
File.WriteAllBytes(Path.Combine(wpA, "x.pkg"), Array.Empty<byte>());
File.WriteAllBytes(Path.Combine(wpB, "x.pkg"), Array.Empty<byte>());

var itemA = new WallpaperItem { Title = "A", FolderPath = wpA };
var itemB = new WallpaperItem { Title = "B", FolderPath = wpB };

var settings = new ExtractSettings
{
    OneFolder = 0,
    UseProjectName = true,   // 输出子目录 = Title
    CoverAllFiles = true,
    OutputMode = 0,
    OutProjectJSON = false,
    MaxConcurrentExtractions = 4
};

// 执行一次场景:写脚本 → 清计数 → 跑服务 → 收集进度与启动次数
async Task<(int Runs, List<string> Progress)> RunAsync(
    string name, JsonObject script, Action<CancellationTokenSource>? cancelAfterFirstStart = null)
{
    var scriptDir = Path.Combine(tempRoot, name);
    Directory.CreateDirectory(scriptDir);
    var scriptFile = Path.Combine(scriptDir, "fake_script.json");
    File.WriteAllText(scriptFile, script.ToJsonString());
    File.Delete(Path.Combine(scriptDir, "fake_counter.txt"));

    // 假 exe 通过环境变量定位脚本(默认取 exe 同目录,而 exe 在测试输出目录)
    Environment.SetEnvironmentVariable("FAKE_REPKG_SCRIPT", scriptFile);

    var progress = new List<string>();
    var cts = new CancellationTokenSource();
    var service = new RepkgCliService(AppContext.BaseDirectory); // FakeRePkg 的 RePKG_Re.exe 就在测试输出目录

    var outRoot = Path.Combine(tempRoot, name + "_out");
    var task = service.ExtractWallpapersAsync(
        new List<WallpaperItem> { itemA, itemB },
        outRoot, settings,
        msg =>
        {
            lock (progress) progress.Add(msg);
            if (cancelAfterFirstStart != null && msg.EndsWith("|开始|0"))
                cancelAfterFirstStart(cts);
        },
        cts.Token);
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
        // 用户取消:服务按设计传播异常,由调用方(UI)处理
    }

    int runs = 0;
    var counterPath = Path.Combine(scriptDir, "fake_counter.txt");
    if (File.Exists(counterPath))
        int.TryParse(File.ReadAllText(counterPath).Trim(), out runs);

    List<string> snapshot;
    lock (progress) snapshot = progress.ToList();
    return (runs, snapshot);
}

JsonObject Script(params object[] behaviorJson) =>
    JsonNode.Parse($"{{\"behaviors\":[{string.Join(",", behaviorJson)}]}}").AsObject();

string Crash(string ids, int entries = 2) =>
    $"{{\"crash\":true,\"processIds\":[\"{ids}\"],\"entriesPerWallpaper\":{entries}}}";

const string Success = "{\"crash\":false}";

// ---------- 场景 1:首崩 → 重启成功 ----------
if (scenarios.Contains("1") || scenarios.Contains("All"))
{
    Console.WriteLine("\n[场景 1] 首崩 → 重启成功");
    var (runs, progress) = await RunAsync("s1", Script(Crash("0"), Success));
    Check(runs == 2, $"进程启动 2 次(实际 {runs})");
    Check(progress.Count(m => m == "A|完成|100") == 1, "A 完成恰好 1 次(首崩无 done,重启后才完成)");
    Check(progress.Count(m => m == "B|完成|100") == 1, "B 完成 1 次(未受影响)");
    Check(!progress.Any(m => m.Contains("失败")), "无失败消息");
    Check(progress.LastOrDefault() == "提取完成，共 2 个壁纸", $"汇总消息正确(实际: {progress.LastOrDefault()})");
}

// ---------- 场景 2:第二击跳壁纸 ----------
if (scenarios.Contains("2") || scenarios.Contains("All"))
{
    Console.WriteLine("\n[场景 2] 第二击跳壁纸(同一壁纸两次崩溃 → 跳过,不再第三次跑它)");
    var (runs, progress) = await RunAsync("s2", Script(Crash("0"), Crash("0"), Success));
    Check(runs == 3, $"进程启动 3 次(实际 {runs})");
    Check(progress.Any(m => m == "A|失败|100"), "A 被标记失败(第二击跳过)");
    Check(progress.Count(m => m == "B|完成|100") == 1, "B 完成 1 次");
    Check(!progress.Any(m => m == "A|完成|100"), "A 无完成消息");
    Check(progress.LastOrDefault() == "提取完成，共 2 个壁纸(因崩溃跳过 1 个)",
        $"汇总含跳过数(实际: {progress.LastOrDefault()})");
}

// ---------- 场景 3:连续崩溃整体放弃 ----------
if (scenarios.Contains("3") || scenarios.Contains("All"))
{
    Console.WriteLine("\n[场景 3] 连续崩溃 → 3 次重启后整体放弃");
    // 每次崩溃的嫌疑壁纸交替(0→1→0→1),永不触发第二击,才能走到"整体放弃"
    var (runs, progress) = await RunAsync("s3", Script(Crash("0"), Crash("1"), Crash("0"), Crash("1")));
    Check(runs == 4, $"进程启动 4 次(1 原跑 + 3 重启,实际 {runs})");
    Check(progress.Any(m => m == "A|失败|100") && progress.Any(m => m == "B|失败|100"),
        "A、B 都被标记失败(放弃时剩余全部失败)");
    Check(!progress.Any(m => m.EndsWith("|完成|100")), "无任何完成消息");
    Check(progress.LastOrDefault() == "提取失败:批处理连续崩溃,剩余 2 个壁纸未提取",
        $"汇总为失败消息(实际: {progress.LastOrDefault()})");
}

// ---------- 场景 4:干净运行不重启 ----------
if (scenarios.Contains("4") || scenarios.Contains("All"))
{
    Console.WriteLine("\n[场景 4] 干净运行(无崩溃)");
    var (runs, progress) = await RunAsync("s4", Script(Success));
    Check(runs == 1, $"进程启动 1 次(实际 {runs})");
    Check(progress.Count(m => m == "A|完成|100") == 1 && progress.Count(m => m == "B|完成|100") == 1,
        "A、B 都完成");
    Check(!progress.Any(m => m.Contains("失败")), "无失败消息");
    Check(progress.LastOrDefault() == "提取完成，共 2 个壁纸", "汇总消息正确");
}

// ---------- 场景 5:用户取消不重启 ----------
if (scenarios.Contains("5") || scenarios.Contains("All"))
{
    Console.WriteLine("\n[场景 5] 用户取消不触发重启");
    var (runs, progress) = await RunAsync("s5", Script(Crash("0")), cts => cts.Cancel());
    Check(runs == 1, $"进程启动 1 次(取消后不重启,实际 {runs})");
    Check(!progress.Any(m => m.StartsWith("提取完成")), "无汇总消息(取消)");
    Check(!progress.Any(m => m.EndsWith("|完成|100")), "无完成消息");
}

Console.WriteLine(failures == 0
    ? $"\n===== 全部通过 ({scenarios.Length} 个场景) ====="
    : $"\n===== {failures} 个断言失败 =====");
return failures == 0 ? 0 : 1;
