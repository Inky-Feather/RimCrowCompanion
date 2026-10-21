# RimCrow Companion

RimCrow 的 RimWorld 伴生模组：记录游戏日志，并补充模组、XML / Def 与补丁来源，帮助排查模组加载和运行错误。依赖 Harmony，建议尽早加载，以便记录更完整的加载过程。

## 游戏版本

| RimWorld | 源码 | 引擎 / 目标框架 | 加载目录 | 验证状态 |
| --- | --- | --- | --- | --- |
| 1.5 | `Source/Legacy` | Unity / .NET Framework 4.7.2 | `1.5/Assemblies` | 已编译及静态检查，待游戏内回归 |
| 1.6 | `Source/Legacy` | Unity / .NET Framework 4.7.2 | `1.6/Assemblies` | 已编译及静态检查，待游戏内回归 |
| 1.7 | `Source/Waylith` | Waylith / .NET 10 | `1.7/Assemblies` | 实验性适配，待游戏内回归 |

1.5 / 1.6 共用源码，分别使用对应版本的参考程序集编译。1.7 使用独立源码和 DLL，不替换旧版实现。`LoadFolders.xml` 按游戏版本只选择一套加载内容，不加载根目录的 `Assemblies`；包 ID `rimcrow.companion` 与程序集名称保持不变。

1.7 当前验证依据为 1.7.4982.1280 / Waylith .NET 10.0.13。游戏更新后需重新验证；版本声明不代表已完成游戏内验收，也不保证后续版本自动兼容。

## 安装与运行

将 `About/`、`LoadFolders.xml` 和对应版本目录放入模组目录，在游戏中启用 Harmony 与 RimCrow Companion，并将本模组放在 Harmony 之后。日志选项可在模组设置中调整；运行日志写入 `RimCrow_Realtime.log`，上一份日志保留为 `RimCrow_Realtime-prev.log`。

## 源码组织

```text
About/                         模组描述与预览
LoadFolders.xml                按游戏版本选择加载目录
1.5/、1.6/、1.7/                各版本的模组 DLL
Source/
  Legacy/                      1.5 / 1.6 源码
  Waylith/                     1.7 源码
  Tests/                       版本隔离与程序集静态检查
  RimCrowCompanion.sln         解决方案入口
  Build.ps1                    构建、校验与更新模组 DLL
  Build.Local.props.example    本机配置示例
```

## 构建与检查

需要 .NET 10 SDK 和 PowerShell。在仓库根目录执行：

```powershell
if (!(Test-Path Source/Build.Local.props)) {
    Copy-Item Source/Build.Local.props.example Source/Build.Local.props
}
```

编辑本地配置，填写 RimWorld 1.7 游戏根目录与 Harmony 1.7 的 `0Harmony.dll` 路径，然后执行：

```powershell
./Source/Build.ps1
```

可只构建一条代码线，或指定独立 SDK 的可执行文件：

```powershell
./Source/Build.ps1 -Target Legacy
./Source/Build.ps1 -Target Waylith
./Source/Build.ps1 -DotNet 'C:/Path/To/dotnet.exe'
```

Legacy 使用 NuGet 编译引用 `Krafs.Rimworld.Ref` 1.5.4409 / 1.6.4871 与 `Lib.Harmony` 2.4.2；仅构建 Legacy 不需要本地 1.7 游戏。Waylith 使用本地游戏的 `Assembly-CSharp.dll`、`UnityStubs.dll` 和对应 Harmony，需自行安装，仓库不分发这些依赖。

脚本先编译到各项目的 `bin/Release`，检查通过后只复制本模组 DLL 到对应版本目录，再检查发布目录；不会切换 Steam 分支、修改其他安装副本或启动游戏。直接构建解决方案不会更新这些发布目录，且默认仅构建 Legacy 的 1.6 目标；完整构建请使用脚本。

只检查仓库内已有 DLL：

```powershell
dotnet run --project Source/Tests/Verify.csproj -c Release -- .
```

检查覆盖版本路由、包与程序集标识、运行时和引擎引用，以及 Legacy 源码冻结基线。1.7 不应再引用缺失的 `StackTraceLogType`。这些检查不加载游戏，不能替代游戏内回归。

## 参考

- [Ludeon：Waylith 与 1.7 官方公告](https://ludeon.com/blog/2026/10/announcing-the-waylith-engine-and-update-1-7/)
- [RimRef 编译引用](https://github.com/krafs/RimRef)
- 参考项目与致谢见 [About/About.xml](About/About.xml)。
