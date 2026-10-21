# 核验记录

## 环境与兼容性
- Player.log：游戏 1.7.4982.1280，运行时 .NET 10.0.13-waylithclr.dev。
- 已确认的启动错误：RimCrowCompanionMod 构造时访问 UnityEngine.StackTraceLogType，UnityStubs 缺少该类型导致 TypeLoadException。
- 1.7 的 Assembly-CSharp.dll 和 UnityStubs.dll 位于游戏根目录；旧项目引用的 Managed 路径已经不存在。
- 根 About.xml 已包含 1.5/1.6/1.7 和 Harmony Workshop 链接；日志中的依赖链接警告来自较早安装副本，不能据此重复修改当前文件。
- 官方公告确认 1.7 从 Unity 迁移至 Waylith，目前为 unstable；多数模组不需重写，应以实际缺失 API 为适配范围。
  https://ludeon.com/blog/2026/10/announcing-the-waylith-engine-and-update-1-7/

## 仓库
- 外层 Git 已存在，初始提交 c41a98e 的源码仅是 gitlink，不是完整源码快照。
- 当前暂存区已包含恢复后的源码，使用真实 git.exe 验证；RTK 的部分摘要不能代替 Git 原始输出。
- 所谓嵌套历史备份为空，旧提交 27b17f8 在外层对象库也不存在；后续以当前恢复源码和根 DLL 的可核验快照为基线。

## 方案
- Legacy 源码冻结，Waylith 源码独立；两项目保持相同程序集和命名空间，由加载目录互斥选择。
- 不为极少数差异引入跨引擎接口层、运行时版本探测或新的运行时依赖。
