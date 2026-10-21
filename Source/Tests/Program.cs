using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

// 仅检查元数据，不加载游戏、Harmony 或模组，不会触发游戏初始化和日志轮转。
static void Check(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
}

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
bool isBuildOutput = args.Length > 1 && args[1] == "--build-output";
string[] versions = args.Length > 2 ? args[2].Split(',') : new[] { "1.5", "1.6", "1.7" };
var folders = XDocument.Load(Path.Combine(root, "LoadFolders.xml")).Root!;
var about = XDocument.Load(Path.Combine(root, "About", "About.xml")).Root!;
Check(about.Element("packageId")?.Value == "rimcrow.companion", "Package ID changed.");
Check(!Directory.Exists(Path.Combine(root, "Assemblies")) ||
      !Directory.EnumerateFiles(Path.Combine(root, "Assemblies"), "*.dll").Any(),
      "Root Assemblies must not contain a fallback DLL.");

var legacySource = File.ReadAllText(Path.Combine(root, "Source", "Legacy", "RimCrowCompanionInit.cs")).Replace("\r\n", "\n");
// 只允许将 1.5 中不存在的可选方法由 nameof 改为同名字符串，其余与恢复基线一致。
legacySource = legacySource.Replace("typeof(DirectXmlToObject), \"ValidateListNode\"",
                                    "typeof(DirectXmlToObject), nameof(DirectXmlToObject.ValidateListNode)");
Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacySource))) ==
      "9C9E12DA36C1A90C441F79A4781B657A78822AE80FE295E71B881B298015281F",
      "Frozen legacy source changed; review against the restored source baseline before updating this check.");

foreach (var version in versions)
{
    Check(about.Element("supportedVersions")!.Elements("li").Any(x => x.Value == version),
          $"About.xml does not declare {version}.");
    var routes = folders.Element("v" + version)!.Elements("li").Select(x => x.Value).ToArray();
    Check(routes.SequenceEqual(new[] { version }), $"{version} must load only its own version directory.");
    bool isWaylith = version == "1.7";
    string directory = isBuildOutput
        ? Path.Combine(root, "Source", isWaylith ? "Waylith" : "Legacy", "bin", "Release",
                       isWaylith ? "" : version)
        : Path.Combine(root, version, "Assemblies");
    string dll = Path.Combine(directory, "RimCrowCompanion.dll");
    Check(File.Exists(dll), $"Missing {dll}");
    Check(Directory.EnumerateFiles(directory, "*.dll").Count() == 1,
          $"Unexpected dependency DLLs in {directory}");
    using var stream = File.OpenRead(dll);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();
    Check(md.GetString(md.GetAssemblyDefinition().Name) == "RimCrowCompanion", "Assembly name changed.");
    var references = md.AssemblyReferences.Select(md.GetAssemblyReference)
        .ToDictionary(x => md.GetString(x.Name), x => x.Version);
    Check(references.ContainsKey("Assembly-CSharp") && references.ContainsKey("0Harmony"),
          $"{version}: missing game/Harmony references.");
    Check(references.ContainsKey(isWaylith ? "UnityStubs" : "UnityEngine.CoreModule"),
          $"{version}: wrong engine reference.");
    Check(!references.ContainsKey(isWaylith ? "UnityEngine.CoreModule" : "UnityStubs"),
          $"{version}: both engine reference sets were mixed.");
    bool hasOldStackType = md.TypeReferences.Select(md.GetTypeReference)
        .Any(x => md.GetString(x.Namespace) == "UnityEngine" && md.GetString(x.Name) == "StackTraceLogType");
    Check(hasOldStackType != isWaylith, $"{version}: wrong StackTraceLogType dependency.");
    if (isWaylith)
        Check(references.TryGetValue("System.Runtime", out var runtime) && runtime.Major == 10,
              "1.7 must target .NET 10.");
    else
        Check(references.ContainsKey("mscorlib"), $"{version}: must target .NET Framework.");
    Console.WriteLine($"PASS {version}: isolated load path, engine references, assembly identity and stack-type regression.");
}
