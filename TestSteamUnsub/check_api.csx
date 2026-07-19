#r "/root/.nuget/packages/facepunch.steamworks/2.3.3/lib/netstandard2.0/Facepunch.Steamworks.Win64.dll"
using System.Reflection;
using Steamworks;
using Steamworks.Ugc;

Console.WriteLine("=== SteamUGC static methods ===");
foreach (var m in typeof(SteamUGC).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
{
    var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"{m.ReturnType.Name} {m.Name}({pars})");
}

Console.WriteLine("\n=== Ugc.Item methods ===");
foreach (var m in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Instance))
{
    var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"{m.ReturnType.Name} {m.Name}({pars})");
}

Console.WriteLine("\n=== Ugc.Query type ===");
// Ugc.Query is a class with static properties
foreach (var p in typeof(Query).GetProperties(BindingFlags.Public | BindingFlags.Static))
{
    Console.WriteLine($"{p.PropertyType.Name} {p.Name}");
}
