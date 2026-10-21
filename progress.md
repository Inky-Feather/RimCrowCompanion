# 执行记录

## 2026-10-21
- 接续已恢复的源码与外层 Git 初始化工作，未覆盖旧版 DLL。
- 核对日志、About.xml、旧 csproj 和本机 SDK。
- 验证官方 Waylith 公告；发现交接中的嵌套 Git 历史备份无效，记录此限制，不声称原历史完整保留。
- 完成基线提交 5084106；根 DLL 原始 SHA256 为 ABBE6762F871EDC5896F82BBE4EC980B8537C412D8661511319D8301C9819184，原始文件已由该提交保留。
- 两套源码与项目已建立，根 LoadFolders.xml 互斥选择 1.5 / 1.6 / 1.7。
- 1.7 构建先复现缺少 StackTraceLogType，再只在 Waylith 修复；1.6 构建成功，1.5 发现可选 Hook 的 nameof 问题，进行单行修复。
- 三版本 Release 构建均为 0 警告 / 0 错误，完整解决方案 Release 构建通过。
- 构建目录与发布目录各一次自动检查通过：版本路由互斥、包和程序集标识稳定、引擎引用不混用、1.7 不含旧 StackTraceLogType、Legacy 源码只含已说明的单行兼容修正。
- 反向检查通过：Legacy 拒绝 1.7 目标；Waylith 在缺少本地游戏引用时明确失败。
- 已创建 README 和根解决方案；Build.ps1 只复制目标模组 DLL，不打包 Harmony、游戏或 SDK 文件。
- 尚未覆盖 C:/Path/To/Mods/RimCrowCompanion，也未运行游戏；游戏内验收步骤见 README。
- 清理了 Waylith 中随全局堆栈设置一起失去用途的诊断提示匹配方法，Legacy 保持不变。
- 最终复核：All / Legacy / Waylith 三种构建入口分别通过，发布目录校验通过；git diff --cached --check 通过，本地路径配置与构建缓存均被忽略。
