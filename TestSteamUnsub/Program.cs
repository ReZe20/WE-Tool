#nullable disable
using Steamworks;
using Steamworks.Ugc;

// 要测试取消订阅的 Workshop ID
const ulong TestWorkshopId = 3258032485; // Ayanami Rei-凌波丽『night』

Console.WriteLine("============================================");
Console.WriteLine("  Unsubscribe 测试");
Console.WriteLine($"  目标 ID: {TestWorkshopId}");
Console.WriteLine("============================================");

try
{
    // 1. 初始化
    Console.Write("正在初始化 Steamworks... ");
    SteamClient.Init(431960, asyncCallbacks: true);
    Console.WriteLine("OK");
    Console.WriteLine($"用户: {SteamClient.Name} (SteamID: {SteamClient.SteamId})");
    Console.WriteLine($"登录: {(SteamClient.IsLoggedOn ? "是" : "否")}");

    // 2. 取消订阅前查询一次，确认它在列表里
    Console.WriteLine();
    Console.Write("查询当前订阅列表... ");
    var beforeQuery = Query.Items.WhereUserSubscribed(SteamClient.SteamId);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var beforePage = await beforeQuery.GetPageAsync(1).WaitAsync(cts.Token);
    
    bool wasSubscribed = false;
    if (beforePage.HasValue)
    {
        wasSubscribed = beforePage.Value.Entries?.Any(e => e.Id == TestWorkshopId) ?? false;
        beforePage.Value.Dispose();
    }
    Console.WriteLine(wasSubscribed ? "✅ 目标在订阅列表中" : "⚠ 目标不在订阅列表中");

    // 3. 执行取消订阅
    Console.WriteLine();
    Console.Write("正在取消订阅... ");
    var item = new Item(TestWorkshopId);
    using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    bool success = await item.Unsubscribe().WaitAsync(cts2.Token);
    
    if (success)
        Console.WriteLine("✅ Unsubscribe() 返回成功");
    else
    {
        // 再试一次看 Result
        await item.Unsubscribe().WaitAsync(cts2.Token);
        Console.WriteLine($"❌ Unsubscribe() 返回失败 (Result: {item.Result})");
    }

    // 4. 重新查询验证
    Console.WriteLine();
    Console.Write("重新查询验证... ");
    var afterQuery = Query.Items.WhereUserSubscribed(SteamClient.SteamId);
    var afterPage = await afterQuery.GetPageAsync(1).WaitAsync(cts.Token);
    
    bool stillSubscribed = false;
    int remainingCount = 0;
    if (afterPage.HasValue)
    {
        remainingCount = afterPage.Value.Entries?.Count() ?? 0;
        stillSubscribed = afterPage.Value.Entries?.Any(e => e.Id == TestWorkshopId) ?? false;
        afterPage.Value.Dispose();
    }
    
    Console.WriteLine($"剩余 {remainingCount} 个订阅");
    Console.WriteLine(stillSubscribed
        ? "⚠ 该项目仍在订阅列表中（Unsubscribe 可能未生效）"
        : "✅ 该项目已不在订阅列表中");

    // 5. 关闭
    SteamClient.Shutdown();
    Console.WriteLine();
    Console.WriteLine("Steamworks 已关闭");
    Console.WriteLine("请在 Steam 客户端中验证取消订阅是否生效。");
}
catch (OperationCanceledException)
{
    Console.WriteLine("❌ 超时：Steam 未响应");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ 错误: {ex.Message}");
}
