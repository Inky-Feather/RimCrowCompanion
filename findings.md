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

## 构建验证发现
- 用 1.7 的真实 Assembly-CSharp/UnityStubs 编译恢复源码，确定性复现 CS0246：StackTraceLogType 不存在；移除旧全局堆栈 API 后 1.7 构建成功。
- 1.6 原样源码构建成功。1.5.4409 的 DirectXmlToObject 没有 ValidateListNode，原先 nameof 在编译阶段失败；Legacy 只将该可选 Hook 的 nameof 换成相同字符串，不改变 1.6 运行行为，1.5 可按已有空值判断跳过。
- 自动检查将这一处编译兼容修正归一化后，与恢复基线的源码哈希比较，防止 Waylith 修改混入 Legacy。
- 复用本机已有 Krafs.Rimworld.Ref（1.5.4409 / 1.6.4871）和 Lib.Harmony 2.4.2，仅作编译引用，不随模组分发。
- 独立 .NET SDK 10.0.402 位于 C:/Path/To/dotnet-sdk；官方 ZIP 的 SHA512 与微软发布元数据一致，未替换系统 SDK。
- 版本路由参照实际安装的 Harmony 的 LoadFolders.xml；旧 Wiki 链接 404，新 Wiki 页面直接抓取 403，未声称已读到其正文。
