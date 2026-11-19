# Dust2 Trainer

原版 CS2 沙二的单人训练服。两种练习使用同一套计时，点位从游戏内采集。训练用服务器建议单独安装，平时玩的 CS2 客户端照常启动。

## 安装和启动

1. 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。
2. 用 SteamCMD 在一个独立目录安装 CS2 专用服务器。例如在 SteamCMD 输入 `force_install_dir C:\cs2-server`、`login anonymous`、`app_update 730 validate`、`quit`。
3. 按 [CounterStrikeSharp 安装指南](https://docs.cssharp.dev/docs/guides/getting-started.html) 给该服务器装 Metamod 和 CounterStrikeSharp。插件项目引用 CounterStrikeSharp.API 1.0.380。服务器端用兼容版本并安装对应的 .NET 运行时，初次安装可用自带运行时的发行包。检查 `game/csgo/gameinfo.gi` 已加入 Metamod 搜索路径。
4. 在项目目录打开 PowerShell，运行 `./Start-LocalTrainer.ps1 -ServerRoot C:\cs2-server`。脚本编译并部署插件，然后在本机启动专用服务器。用服务器控制台的 `meta list` 和 `css_plugins list` 检查加载结果。
5. 照常打开 CS2，在游戏设置启用开发者控制台，输入 `connect 127.0.0.1:27015`，加入 T 方。该启动脚本面向单机训练。

## 首次在地图上标记位置

站好并朝向期望方向，然后在聊天输入对应命令。每条命令保存脚底坐标和视角，重复标记会覆盖该点。先加入 T 方，必要时用客户端控制台的 `noclip` 到达点位，关闭 noclip 并落地后再标记。

| 命令 | 站位 |
| --- | --- |
| `!mark bluecar-player` | A 大门内 T 方起点，面向出口 |
| `!mark bluecar-bot` | 蓝车旁的 CT 位置，脚下要有地面 |
| `!mark peek-player` | 玩家在 A 大的架枪点，面向 CT 出场方向 |
| `!mark peek-start` | CT 拐角掩体后，面向玩家方向 |
| `!mark peek-end` | CT 大拉结束的位置。从起点到终点需走同一段平直地面，路线全程有足够空间 |

点位写入 `game/csgo/addons/counterstrikesharp/plugins/Dust2Trainer/points.json`。进入蓝车练习前，先确认玩家起点隔墙看不到蓝车。大拉练习同理，起点应遮住 Bot，终点应清楚可见。

## 试玩

- `!bluecar` 开始蓝车练习。
- `!peek` 开始大拉练习，Bot 在 0.8–2.4 秒后沿标记路线移动。
- 重输模式命令可开始新一轮，`!stopdrill` 结束当前一轮。单轮最长 45 秒。
- 第一版使用 AK-47。服务端冻结 Bot AI，Bot 大拉用逐 tick 位置驱动，动画和实际走位观感需要在游戏内校验。

## 计时

每轮在同一个服务端时钟上采集首次可见、首次 AK 开枪、首次 AK 伤害和 AK 击杀 tick。可见性基于约 90° 视线锥和头胸射线遮挡检测，适用于无烟无闪的练习。它是服务端估计，与屏幕实际渲染或玩家察觉时间有偏差。预开枪会保留负时间差，缺少的事件显示为 `--`。

服务器在插件目录追加 `attempts.jsonl`。每行包含原始 tick、每 tick 毫秒数、出场 tick、结果、开枪次数、首个命中部位以及本轮点位。两个事件之间的耗时为 `(后一个 tick - 前一个 tick) × tickMs`。同一局内可以重复测试，跨局比较时使用时间差。

验收时先检查隔墙、露出、预开枪和一发爆头。再用大拉模式确认 Bot 出场前不可见、出场后才记首次可见，以及重开后旧记录没有带进新一轮。
