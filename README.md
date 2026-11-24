# 东方夜雀食堂 · 实时仓库添加器（Mystia's Izakaya Live Adder Mod）

适用于《东方夜雀食堂（Touhou Mystia's Izakaya）》的 **BepInEx 6 (Unity.IL2CPP)** 游戏内 Mod，专注于**纯实时添加食材与饮品**——无需退出游戏、无需修改存档文件、无需外部内存扫描。

---

## 功能特性

- **原生游戏内悬浮面板**：默认按 **`F7`** 随时打开/关闭（`Esc` 或右上角 `×` 关闭，支持拖动标题栏移动），适配游戏内置的 Unity 新版 Input System，带防点击穿透遮罩。
- **双仓库实时添加**：
  - 支持全部 **55 种食材**与 **41 种饮品**（含本篇 + DLC1~DLC4），启动后自动与游戏 `DataBaseCore` 同步，自动识别新条目并安全跳过未加载 DLC 的无效 ID。
  - 直接写入游戏运行时 `RunTimeStorage` 仓库字典，支持为**未持有**的物品直接新增条目，自动按 `MAX_OBJECT_COUNT` 安全截断，并自动刷新已打开的仓库界面。
- **高效检索与批量操作**：
  - 实时搜索（名称 / `#编号` / 描述 / 标签，支持中文输入法与 `Ctrl+V` 粘贴）。
  - 支持按 **DLC 模块**、**持有状态（全部 / 已持有 / 未持有）**、以及饮品专属 **21 种酒水风味标签**筛选。
  - 预设数量快捷按钮（`+1 / +10 / +50 / +99 / +999 / +9999`），支持单品 `+1` / `+N` 与「当前筛选全部 `+N`」一键批量实时添加。
  - 可选勾选「同步解锁图鉴」（默认关闭）。
- **单文件自包含**：全部物品数据表与游戏内像素图标均作为内嵌资源打包在单个 `NightbirdLiveAdder.dll` 中，部署与卸载都只需管理这一个文件。

---

## 安装方法

1. 下载并解压 **BepInEx 6 Bleeding Edge (`BepInEx-Unity.IL2CPP-win-x64`)** 到游戏根目录（即 `Touhou Mystia Izakaya.exe` 所在目录）：
   - 官方构建站：`https://builds.bepinex.dev/projects/bepinex_be`
2. 启动一次游戏（让 BepInEx 自动生成 `BepInEx/interop` 目录）后退出。
3. 从本仓库的 **Releases** 下载 `NightbirdLiveAdder.dll`（或按下方说明自行编译），放入：
   ```text
   <游戏目录>\BepInEx\plugins\NightbirdLiveAdder.dll
   ```
4. 正常启动游戏，读取存档后按 **`F7`** 即可打开实时添加面板。

---

## 配置说明

首次加载 Mod 后会自动生成配置文件：

```text
<游戏目录>\BepInEx\config\com.nightbird.liveadder.cfg
```

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `ToggleKey` | `F7` | 打开/关闭悬浮面板的快捷键（如 `F6`、`F7`、`F8`、`Insert`、`Home` 等） |
| `DefaultAddAmount` | `99` | 面板默认的单次添加数量 |
| `UnlockAlbumOnAdd` | `false` | 添加物品时是否同步解锁对应图鉴条目（也可在面板内实时切换） |
| `ShowStartupToast` | `true` | 启动游戏后在右上角短暂提示 Mod 快捷键 |

---

## 自行编译

环境要求：
- [.NET 8 SDK](https://dotnet.microsoft.com/)（或其他支持 `net6.0` 目标框架的 .NET SDK）
- 游戏目录已安装 BepInEx 6 IL2CPP 且已生成 `BepInEx/interop`

在项目根目录执行（将 `GameDir` 改为你的实际游戏路径）：

```powershell
dotnet build .\NightbirdLiveAdder\NightbirdLiveAdder.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Touhou Mystia Izakaya"
```

编译产物位于 `NightbirdLiveAdder\bin\Release\NightbirdLiveAdder.dll`，直接复制到 `<游戏目录>\BepInEx\plugins\` 即可。也可以直接编辑并运行 [build_and_install.bat](build_and_install.bat)。

---

## 目录结构

```text
├── NightbirdLiveAdder/
│   ├── NightbirdLiveAdder.csproj   # 项目文件（内嵌数据与像素图标）
│   ├── Plugin.cs                   # BepInEx 6 IL2CPP 插件入口与配置
│   ├── CatalogService.cs           # 内嵌物品目录、像素图标与运行时 DB 同步
│   ├── StorageService.cs           # 纯实时仓库添加引擎（RunTimeStorage）
│   └── LiveAdderUI.cs              # 基于 Unity.InputSystem 的游戏内悬浮界面
├── assets/
│   ├── data/                       # 食材与饮品数据表（ingredients.json / beverages.json）
│   └── icons/                      # 物品像素图标
├── build_and_install.bat           # Windows 一键编译部署脚本
├── LICENSE
└── README.md
```

---

## 卸载方式

删除 `<游戏目录>\BepInEx\plugins\NightbirdLiveAdder.dll` 即可完全移除本 Mod（不影响任何已有存档）。
