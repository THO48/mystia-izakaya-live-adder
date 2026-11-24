using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace NightbirdLiveAdder;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "com.nightbird.liveadder";
    public const string PluginName = "Nightbird Live Adder (夜雀食堂实时添加器)";
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource LogSource { get; private set; }
    internal static ConfigEntry<string> ToggleKeyConfig { get; private set; }
    internal static ConfigEntry<int> DefaultAddAmountConfig { get; private set; }
    internal static ConfigEntry<bool> UnlockAlbumOnAddConfig { get; private set; }
    internal static ConfigEntry<bool> ShowStartupToastConfig { get; private set; }

    public override void Load()
    {
        LogSource = Log;

        ToggleKeyConfig = Config.Bind(
            "General",
            "ToggleKey",
            "F7",
            "打开/关闭实时添加面板的快捷键（如 F7、F8、F6、Insert、Home 等）。");

        DefaultAddAmountConfig = Config.Bind(
            "General",
            "DefaultAddAmount",
            99,
            "面板默认单次添加数量（必须大于 0）。");

        UnlockAlbumOnAddConfig = Config.Bind(
            "General",
            "UnlockAlbumOnAdd",
            false,
            "添加物品时是否同步解锁对应图鉴条目（默认关闭，可在面板内随时勾选）。");

        ShowStartupToastConfig = Config.Bind(
            "General",
            "ShowStartupToast",
            true,
            "启动游戏后在屏幕右上角短暂提示 Mod 快捷键（数秒后自动消失）。");

        try
        {
            CatalogService.Initialize();
            AddComponent<LiveAdderUI>();
            Log.LogInfo($"[{PluginName} v{PluginVersion}] Loaded! Press [{ToggleKeyConfig.Value}] in-game to open the Real-Time Adder panel.");
        }
        catch (Exception ex)
        {
            Log.LogError($"[{PluginName}] Failed to initialize: {ex}");
        }
    }
}
