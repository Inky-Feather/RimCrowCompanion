using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;
using Verse;

namespace RimCrowCompanion
{
    // ==========================================
    // 0. 全局常量与黑名单配置
    // ==========================================
    public static class RimCrowConstants
    {
        // 需要排除的底层系统级和框架级命名空间/Assembly，综合了 SystemPrefixes 和 assembliesToSkip 的底层与系统 DLL 忽略列表
        public static readonly string[] IgnoredAssemblies = {
            "mscorlib", "System", "Unity", "UnityEngine", "Mono", "MonoMod",
            "Assembly-CSharp", "Assembly-CSharp-firstpass", "0Harmony", "HarmonyLib",
            "netstandard", "Microsoft", "Multiplayer", "Prepatcher", "ICSharpCode",
            "Newtonsoft", "CombatAI", "NVorbis", "NAudio", "ISharpZipLib",
            "com.rlabrecque.steamworks.net", "SmashTools", "ModErrorChecker"
        };

        // 容易被误判为报错源头的基建/排错类 Mod 包名，防止它们替真正的报错 Mod 背锅
        public static readonly string[] InfrastructureMods = {
            "brrainz.harmony", "unlimitedhugs.hugslib", "startupprofiler.mypatches",
            "imranfish.xmlextensions", "taranchuk.performanceoptimizer", "krkr.rocketman",
            "taranchuk.moderrorchecker"
        };

        public static string TryGetModId(ModContentPack mod)
        {
            var id = mod?.PackageIdPlayerFacing ?? mod?.PackageId;
            return string.IsNullOrWhiteSpace(id) ? null : id.ToLowerInvariant();
        }
    }

    public static class ModCompatibilityDetector
    {
        private static readonly string[] VisualExceptionsHints =
        {
            "visualexceptions",
            "visual exceptions"
        };

        private static readonly string[] KnownDiagnosticsHints =
        {
            "visualexceptions",
            "visual exceptions",
            "moderrorchecker",
            "mod error checker"
        };

        public static bool HasVisualExceptions()
        {
            return HasLoadedModHint(VisualExceptionsHints);
        }

        public static bool HasKnownDiagnosticsMod()
        {
            return HasLoadedModHint(KnownDiagnosticsHints);
        }

        private static bool HasLoadedModHint(IEnumerable<string> hints)
        {
            var runningMods = LoadedModManager.RunningMods;
            if (runningMods == null) return false;

            return runningMods.Any(mod =>
            {
                if (mod == null) return false;

                string[] candidates =
                {
                    mod.Name ?? string.Empty,
                    mod.PackageId ?? string.Empty,
                    mod.PackageIdPlayerFacing ?? string.Empty
                };

                foreach (var candidate in candidates)
                {
                    foreach (var hint in hints)
                    {
                        if (!string.IsNullOrEmpty(hint) &&
                            candidate.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }

                return false;
            });
        }
    }

    // ==========================================
    // 1. XML 上下文追踪器 (借鉴 LogAfterDefError & BetterModSort)
    // ==========================================
    public static class XmlSourceTracker
    {
        [ThreadStatic] private static Stack<LoadableXmlAsset> assetStack;
        [ThreadStatic] private static Stack<XmlNode> defRootStack;

        public static void Push(LoadableXmlAsset asset, XmlNode defRoot)
        {
            if (asset == null || defRoot == null) return;

            if (assetStack == null) assetStack = new Stack<LoadableXmlAsset>();
            if (defRootStack == null) defRootStack = new Stack<XmlNode>();
            assetStack.Push(asset);
            defRootStack.Push(defRoot);
        }

        public static void Pop()
        {
            if (assetStack != null && assetStack.Count > 0)
                assetStack.Pop();

            if (defRootStack != null && defRootStack.Count > 0)
                defRootStack.Pop();
        }

        public static LoadableXmlAsset Peek()
        {
            return assetStack != null && assetStack.Count > 0 ? assetStack.Peek() : null;
        }

        public static XmlNode CurrentDefRoot
        {
            get
            {
                return defRootStack != null && defRootStack.Count > 0 ? defRootStack.Peek() : null;
            }
        }
    }

    public static class DefSourceMap
    {
        private static readonly ConcurrentDictionary<string, LoadableXmlAsset> assetByDefName =
            new ConcurrentDictionary<string, LoadableXmlAsset>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, string> parentByDefName =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public static void Add(string defName, LoadableXmlAsset asset, string parentName = null)
        {
            if (string.IsNullOrEmpty(defName) || asset == null) return;

            assetByDefName.TryAdd(defName, asset);
            if (!string.IsNullOrEmpty(parentName))
                parentByDefName.TryAdd(defName, parentName);
        }

        public static LoadableXmlAsset GetAssetByDefName(string defName)
        {
            return !string.IsNullOrEmpty(defName) && assetByDefName.TryGetValue(defName, out var asset)
                ? asset
                : null;
        }

        public static string GetParentNameByDefName(string defName)
        {
            return !string.IsNullOrEmpty(defName) && parentByDefName.TryGetValue(defName, out var parent)
                ? parent
                : null;
        }
    }

    public static class XmlBuckets
    {
        public readonly struct UsageSnapshot
        {
            public UsageSnapshot(string rootDefName, string nodePath)
            {
                RootDefName = rootDefName;
                NodePath = nodePath;
            }

            public string RootDefName { get; }
            public string NodePath { get; }
        }

        private static readonly ConcurrentDictionary<string, ConcurrentBag<UsageSnapshot>> nodesByDefName =
            new ConcurrentDictionary<string, ConcurrentBag<UsageSnapshot>>(StringComparer.Ordinal);

        public static void Add(string defName, XmlNode listRootNode)
        {
            if (string.IsNullOrWhiteSpace(defName) || listRootNode == null) return;

            var key = defName.Trim();
            var snapshot = BuildSnapshot(listRootNode);
            var bucket = nodesByDefName.GetOrAdd(key, _ => new ConcurrentBag<UsageSnapshot>());
            bucket.Add(snapshot);
        }

        public static IEnumerable<UsageSnapshot> Get(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName)) return Enumerable.Empty<UsageSnapshot>();

            return nodesByDefName.TryGetValue(defName.Trim(), out var bucket)
                ? bucket
                : Enumerable.Empty<UsageSnapshot>();
        }

        private static UsageSnapshot BuildSnapshot(XmlNode node)
        {
            return new UsageSnapshot(GetRootDefName(node), GetXmlNodePath(node));
        }

        private static string GetRootDefName(XmlNode node)
        {
            var current = node;
            while (current != null && current.NodeType != XmlNodeType.Document)
            {
                var childDefName = current["defName"]?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(childDefName))
                    return childDefName;

                current = current.ParentNode;
            }

            return null;
        }

        private static string GetXmlNodePath(XmlNode node)
        {
            var parts = new List<string>();
            var current = node;
            while (current != null && current.NodeType != XmlNodeType.Document)
            {
                var nodeName = current.Name;
                var defNameAttribute = current.Attributes?["defName"]?.Value;
                var defNameChild = current["defName"]?.InnerText;
                var identifier = !string.IsNullOrEmpty(defNameAttribute) ? defNameAttribute : defNameChild;

                if (!string.IsNullOrEmpty(identifier))
                    nodeName = string.Format("{0}[defName={1}]", nodeName, identifier);

                parts.Add(nodeName);
                current = current.ParentNode;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }

    public readonly struct PatchInfo
    {
        public PatchInfo(ModContentPack mod, string operationType, string valueText, string findModTarget = null)
        {
            Mod = mod;
            OperationType = operationType;
            ValueText = valueText;
            FindModTarget = findModTarget;
        }

        public ModContentPack Mod { get; }
        public string OperationType { get; }
        public string ValueText { get; }
        public string FindModTarget { get; }
    }

    public static class PatchSourceMap
    {
        private static readonly ConcurrentDictionary<string, ConcurrentBag<PatchInfo>> patchesByDefName =
            new ConcurrentDictionary<string, ConcurrentBag<PatchInfo>>(StringComparer.Ordinal);

        private static readonly Regex DefNamePattern =
            new Regex(@"defName\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled);

        private static readonly Regex NameAttrPattern =
            new Regex(@"(?<!\w)@?Name\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled);

        private static readonly FieldInfo XPathField = typeof(PatchOperationPathed)
            .GetField("xpath", BindingFlags.NonPublic | BindingFlags.Instance);

        private static bool collected;

        public static void CollectFromRunningMods()
        {
            if (collected) return;

            var runningMods = LoadedModManager.RunningMods;
            if (runningMods == null) return;

            foreach (var mod in runningMods)
            {
                if (mod == null) continue;

                foreach (var patch in mod.Patches)
                    CollectPatchOperation(patch, mod);
            }

            collected = true;
        }

        public static IEnumerable<PatchInfo> Get(string defName)
        {
            return !string.IsNullOrEmpty(defName) && patchesByDefName.TryGetValue(defName, out var bag)
                ? bag
                : Enumerable.Empty<PatchInfo>();
        }

        private static void CollectPatchOperation(PatchOperation patch, ModContentPack mod, string findModTarget = null)
        {
            if (patch == null || mod == null) return;

            if (patch is PatchOperationPathed)
                Add(patch, mod, findModTarget);

            var patchType = patch.GetType();

            var modsField = patchType.GetField("mods", BindingFlags.NonPublic | BindingFlags.Instance);
            var findMods = modsField?.GetValue(patch) as List<string>;
            var nestedFindTarget = findMods != null && findMods.Count > 0
                ? string.Join(", ", findMods)
                : findModTarget;

            var operationsField = patchType.GetField("operations", BindingFlags.NonPublic | BindingFlags.Instance);
            if (operationsField?.GetValue(patch) is List<PatchOperation> operations)
            {
                foreach (var nestedOperation in operations)
                    CollectPatchOperation(nestedOperation, mod, nestedFindTarget);
            }

            var matchField = patchType.GetField("match", BindingFlags.NonPublic | BindingFlags.Instance);
            if (matchField?.GetValue(patch) is PatchOperation matchOperation)
                CollectPatchOperation(matchOperation, mod, nestedFindTarget);

            var noMatchField = patchType.GetField("nomatch", BindingFlags.NonPublic | BindingFlags.Instance);
            if (noMatchField?.GetValue(patch) is PatchOperation noMatchOperation)
                CollectPatchOperation(noMatchOperation, mod, nestedFindTarget);
        }

        private static void Add(PatchOperation operation, ModContentPack mod, string findModTarget)
        {
            if (XPathField == null || operation == null || mod == null) return;

            var xpath = XPathField.GetValue(operation) as string;
            if (string.IsNullOrEmpty(xpath)) return;

            var identifiers = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in DefNamePattern.Matches(xpath))
            {
                var defName = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(defName))
                    identifiers.Add(defName);
            }

            foreach (Match match in NameAttrPattern.Matches(xpath))
            {
                var defName = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(defName))
                    identifiers.Add(defName);
            }

            if (identifiers.Count == 0) return;

            var patchInfo = new PatchInfo(
                mod,
                operation.GetType().Name ?? "PatchOperation",
                TryExtractPatchValueText(operation),
                findModTarget);
            foreach (var identifier in identifiers)
            {
                var bucket = patchesByDefName.GetOrAdd(identifier, _ => new ConcurrentBag<PatchInfo>());
                bucket.Add(patchInfo);
            }
        }

        private static string TryExtractPatchValueText(PatchOperation operation)
        {
            if (operation == null) return null;

            try
            {
                var valueField = operation.GetType().GetField("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (valueField == null) return null;

                var value = valueField.GetValue(operation);
                if (value == null) return null;

                if (value is XmlContainer container)
                    return container.node?.OuterXml;

                if (value is XmlNode node)
                    return node.OuterXml;

                var nodeField = value.GetType().GetField("node", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (nodeField?.GetValue(value) is XmlNode reflectedNode)
                    return reflectedNode.OuterXml;

                var nodeProperty = value.GetType().GetProperty("node", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (nodeProperty?.GetValue(value, null) is XmlNode propertyNode)
                    return propertyNode.OuterXml;

                return value.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    // ==========================================
    // 2. 错误深度分析引擎 (借鉴 BetterModSort)
    // ==========================================
    public static class ErrorAnalyzer
    {
        public static void AnalyzeStackTrace(StackTrace stackTrace, out HashSet<string> involvedMods)
        {
            involvedMods = new HashSet<string>(StringComparer.Ordinal);
            string infrastructureFallback = null;

            foreach (var frame in stackTrace.GetFrames() ?? new StackFrame[0])
            {
                bool foundPrimary;
                AnalyzeMethod(frame.GetMethod(), involvedMods, ref infrastructureFallback, out foundPrimary);
                if (foundPrimary) break;
            }

            AddInfrastructureFallbackIfNeeded(involvedMods, infrastructureFallback);
        }

        public static void AnalyzeStackTraceText(string stackTraceText, out HashSet<string> involvedMods)
        {
            involvedMods = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(stackTraceText)) return;

            string infrastructureFallback = null;
            CollectHarmonyPatchOwnersFromText(stackTraceText, involvedMods, ref infrastructureFallback);

            bool foundPrimary = false;

            foreach (Match match in StackFrameMethodRegex.Matches(stackTraceText))
            {
                var method = ResolveMethod(match.Groups["type"].Value, match.Groups["method"].Value);
                if (method != null)
                {
                    AnalyzeMethod(method, involvedMods, ref infrastructureFallback, out foundPrimary);
                }
                else
                {
                    foundPrimary = AnalyzeType(match.Groups["type"].Value, involvedMods, ref infrastructureFallback);
                }

                if (foundPrimary) break;
            }

            if (!foundPrimary)
            {
                foreach (Match match in StackFrameTypeRegex.Matches(stackTraceText))
                {
                    foundPrimary = AnalyzeType(match.Groups["type"].Value, involvedMods, ref infrastructureFallback);
                    if (foundPrimary) break;
                }
            }

            AddInfrastructureFallbackIfNeeded(involvedMods, infrastructureFallback);
        }

        // 兼容两类堆栈文本：
        // 1. 正常堆栈里的 `Namespace.Type.Method(...)`
        // 2. Harmony 增强输出里的 `Namespace.Type:Method(...)`
        private static readonly Regex StackFrameMethodRegex = new Regex(
            @"(?:^|\n)\s*(?:at\s+)?(?<type>[A-Za-z0-9_`<>+]+(?:\.[A-Za-z0-9_`<>+]+)+)(?::|\.)(?<method>[A-Za-z0-9_`<>]+)\s*(?:\(|$)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex StackFrameTypeRegex = new Regex(
            @"(?:^|\n)\s*(?:at\s+)?(?<type>[A-Za-z0-9_`<>+]+(?:\.[A-Za-z0-9_`<>+]+)+)(?::|\.[A-Za-z0-9_`<>]+\()",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Harmony 的增强堆栈通常会在 `Prefixes:` / `Postfixes:` 等行里直接列出 patch 方法。
        // 这些方法本身就是“参与异常链条的模组代码”，即使拿不到原始方法，也应该纳入参与方归因。
        private static readonly Regex HarmonyPatchSectionRegex = new Regex(
            @"(?:^|\n)\s*(?:Prefixes|Postfixes|Transpilers|Finalizers):\s*(?<body>.+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex InlineMethodRegex = new Regex(
            @"(?<type>[A-Za-z0-9_`<>+]+(?:\.[A-Za-z0-9_`<>+]+)+)(?::|\.)(?<method>[A-Za-z0-9_`<>]+)",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, Type> ResolvedTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodBase> ResolvedMethodCache = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<MethodBase, string[]> PatchOwnerCache =
            new ConcurrentDictionary<MethodBase, string[]>();
        private static readonly ConcurrentDictionary<string, string> ModIdByAssemblyCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> ModIdByHarmonyOwnerCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static void AnalyzeMethod(MethodBase method, HashSet<string> involvedMods, ref string infrastructureFallback, out bool foundPrimary)
        {
            foundPrimary = false;
            if (method?.DeclaringType == null) return;

            CollectHarmonyPatchOwnersFromMethod(method, involvedMods, ref infrastructureFallback);

            if (ShouldIgnoreTypeForPrimaryAttribution(method.DeclaringType)) return;

            var modId = TryGetModIdFromAssembly(method.DeclaringType.Assembly);
            if (string.IsNullOrEmpty(modId)) return;

            foundPrimary = TryAddModId(involvedMods, modId, ref infrastructureFallback);
        }

        private static bool AnalyzeType(string typeName, HashSet<string> involvedMods, ref string infrastructureFallback)
        {
            var type = ResolveType(typeName);
            if (type == null || type.Assembly == null || ShouldIgnoreTypeForPrimaryAttribution(type))
                return false;

            var modId = TryGetModIdFromAssembly(type.Assembly);
            if (string.IsNullOrEmpty(modId)) return false;

            return TryAddModId(involvedMods, modId, ref infrastructureFallback);
        }

        private static bool ShouldIgnoreTypeForPrimaryAttribution(Type type)
        {
            if (type?.Assembly == null) return true;

            var assemblyName = type.Assembly.GetName().Name;
            if (RimCrowConstants.IgnoredAssemblies.Any(p => assemblyName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (assemblyName == "RimCrowCompanion" &&
                !string.IsNullOrEmpty(type.Namespace) &&
                type.Namespace.StartsWith("RimCrowCompanion", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static void CollectHarmonyPatchOwnersFromMethod(MethodBase method, HashSet<string> involvedMods, ref string infrastructureFallback)
        {
            foreach (var modId in GetPatchOwnerModIds(method))
            {
                TryAddModId(involvedMods, modId, ref infrastructureFallback);
            }
        }

        private static void CollectHarmonyPatchOwnersFromText(string stackTraceText, HashSet<string> involvedMods, ref string infrastructureFallback)
        {
            foreach (Match sectionMatch in HarmonyPatchSectionRegex.Matches(stackTraceText))
            {
                var body = sectionMatch.Groups["body"].Value;
                foreach (Match methodMatch in InlineMethodRegex.Matches(body))
                {
                    var patchMethod = ResolveMethod(methodMatch.Groups["type"].Value, methodMatch.Groups["method"].Value);
                    if (patchMethod?.DeclaringType?.Assembly != null)
                    {
                        var patchMethodModId = TryGetModIdFromAssembly(patchMethod.DeclaringType.Assembly);
                        TryAddModId(involvedMods, patchMethodModId, ref infrastructureFallback);
                    }
                    else
                    {
                        AnalyzeType(methodMatch.Groups["type"].Value, involvedMods, ref infrastructureFallback);
                    }
                }
            }
        }

        private static string[] GetPatchOwnerModIds(MethodBase method)
        {
            if (method == null) return new string[0];

            return PatchOwnerCache.GetOrAdd(method, targetMethod =>
            {
                try
                {
                    var patchInfo = Harmony.GetPatchInfo(targetMethod);
                    if (patchInfo == null) return new string[0];

                    var modIds = new HashSet<string>(StringComparer.Ordinal);
                    CollectPatchMods(patchInfo.Prefixes, modIds);
                    CollectPatchMods(patchInfo.Postfixes, modIds);
                    CollectPatchMods(patchInfo.Transpilers, modIds);
                    CollectPatchMods(patchInfo.Finalizers, modIds);
                    return modIds.ToArray();
                }
                catch
                {
                    return new string[0];
                }
            });
        }

        private static void CollectPatchMods(IEnumerable<Patch> patches, HashSet<string> modIds)
        {
            if (patches == null) return;

            foreach (var patch in patches)
            {
                var modId = TryGetModIdFromPatch(patch);
                if (!string.IsNullOrEmpty(modId))
                    modIds.Add(modId);
            }
        }

        private static string TryGetModIdFromPatch(Patch patch)
        {
            if (patch == null) return null;

            var patchMethodAssembly = patch.PatchMethod?.DeclaringType?.Assembly;
            var modId = TryGetModIdFromAssembly(patchMethodAssembly);
            if (!string.IsNullOrEmpty(modId))
                return modId;

            return TryGetModIdFromHarmonyOwner(patch.owner);
        }

        private static string TryGetModIdFromHarmonyOwner(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner)) return null;

            var cached = ModIdByHarmonyOwnerCache.GetOrAdd(owner, currentOwner =>
            {
                var runningMods = LoadedModManager.RunningMods;
                if (runningMods == null) return string.Empty;

                foreach (var mod in runningMods)
                {
                    if (mod == null) continue;

                    var packageId = mod.PackageId ?? string.Empty;
                    var playerFacingId = mod.PackageIdPlayerFacing ?? string.Empty;

                    if (string.Equals(packageId, currentOwner, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(playerFacingId, currentOwner, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(packageId) && currentOwner.IndexOf(packageId, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(playerFacingId) && currentOwner.IndexOf(playerFacingId, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return NormalizeModId(mod);
                    }
                }

                return string.Empty;
            });

            return string.IsNullOrEmpty(cached) ? null : cached;
        }

        private static string TryGetModIdFromAssembly(Assembly assembly)
        {
            if (assembly == null) return null;

            var cacheKey = assembly.FullName ?? assembly.GetName().Name;
            var cached = ModIdByAssemblyCache.GetOrAdd(cacheKey, _ =>
            {
                var runningMods = LoadedModManager.RunningMods;
                var mod = runningMods?.FirstOrDefault(m => m?.assemblies?.loadedAssemblies != null && m.assemblies.loadedAssemblies.Contains(assembly));
                return NormalizeModId(mod) ?? string.Empty;
            });

            return string.IsNullOrEmpty(cached) ? null : cached;
        }

        private static string NormalizeModId(ModContentPack mod)
        {
            return RimCrowConstants.TryGetModId(mod);
        }

        private static bool TryAddModId(HashSet<string> involvedMods, string modId, ref string infrastructureFallback)
        {
            if (string.IsNullOrEmpty(modId)) return false;

            if (RimCrowConstants.InfrastructureMods.Contains(modId))
            {
                if (string.IsNullOrEmpty(infrastructureFallback))
                    infrastructureFallback = modId;
                return false;
            }

            involvedMods.Add(modId);
            return true;
        }

        private static void AddInfrastructureFallbackIfNeeded(HashSet<string> involvedMods, string infrastructureFallback)
        {
            if (involvedMods.Count == 0 && !string.IsNullOrEmpty(infrastructureFallback))
                involvedMods.Add(infrastructureFallback);
        }

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            lock (ResolvedTypeCache)
            {
                if (ResolvedTypeCache.TryGetValue(typeName, out var cachedType))
                    return cachedType;

                var resolvedType = AccessTools.TypeByName(typeName);
                if (resolvedType == null && RimCrowCompanionMod.CanRunDeepAnalysis())
                {
                    try
                    {
                        resolvedType = GenTypes.AllTypes.FirstOrDefault(t => string.Equals(t.FullName, typeName, StringComparison.Ordinal));
                    }
                    catch
                    {
                    }
                }

                ResolvedTypeCache[typeName] = resolvedType;
                return resolvedType;
            }
        }

        private static MethodBase ResolveMethod(string typeName, string methodName)
        {
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName)) return null;

            string cacheKey = typeName + "|" + methodName;
            lock (ResolvedMethodCache)
            {
                if (ResolvedMethodCache.TryGetValue(cacheKey, out var cachedMethod))
                    return cachedMethod;

                MethodBase resolvedMethod = null;
                var type = ResolveType(typeName);
                if (type != null)
                {
                    resolvedMethod = AccessTools.DeclaredMethod(type, methodName);

                    // 某些栈文本只保留方法名，且存在重载；这里退化为“第一个同名方法”，
                    // 至少还能拿到 Harmony patch owners，而不是完全失去多模组参与信息。
                    if (resolvedMethod == null)
                    {
                        resolvedMethod = AccessTools.GetDeclaredMethods(type)
                            .FirstOrDefault(method => string.Equals(method.Name, methodName, StringComparison.Ordinal));
                    }
                }

                ResolvedMethodCache[cacheKey] = resolvedMethod;
                return resolvedMethod;
            }
        }
    }

    public class LogCaptureContext
    {
        public string Level { get; set; }
        public string Text { get; set; }
        public string StackText { get; set; }
    }

    public interface ILogEnricher
    {
        int Priority { get; }

        bool CanEnrich(LogCaptureContext captureContext);

        ILogEnrichmentData Collect(LogCaptureContext captureContext);
    }

    public interface ILogEnrichmentData
    {
        string InferredType { get; }

        IEnumerable<string> GetInvolvedModIds();

        IEnumerable<string> GetRelatedFiles();
    }

    public static class LogEnricherPipeline
    {
        private static readonly List<ILogEnricher> enrichers = new List<ILogEnricher>
        {
            new DefConfigLogEnricher(),
            new CrossRefLogEnricher(),
            new XmlLogEnricher()
        };

        public static ILogEnrichmentData TryCollect(LogCaptureContext captureContext)
        {
            foreach (var enricher in enrichers.OrderBy(e => e.Priority))
            {
                if (!enricher.CanEnrich(captureContext)) continue;

                var result = enricher.Collect(captureContext);
                if (result != null) return result;
            }

            return null;
        }
    }

    public class XmlLogEnricher : ILogEnricher
    {
        public int Priority => 50;

        public bool CanEnrich(LogCaptureContext captureContext)
        {
            return XmlSourceTracker.Peek() != null;
        }

        public ILogEnrichmentData Collect(LogCaptureContext captureContext)
        {
            var asset = XmlSourceTracker.Peek();
            if (asset == null) return null;

            string relativePath = asset.FullFilePath ?? asset.name ?? "";
            if (asset.mod != null && !string.IsNullOrEmpty(relativePath) && relativePath.StartsWith(asset.mod.RootDir))
                relativePath = relativePath.Substring(asset.mod.RootDir.Length).TrimStart('\\', '/');

            var rootNode = XmlSourceTracker.CurrentDefRoot?.Name ?? "";

            return new XmlLogEnrichmentData(
                asset.mod,
                relativePath,
                rootNode);
        }
    }

    public class XmlLogEnrichmentData : ILogEnrichmentData
    {
        private readonly ModContentPack mod;
        private readonly string relativePath;
        private readonly string rootNode;

        public XmlLogEnrichmentData(ModContentPack mod, string relativePath, string rootNode)
        {
            this.mod = mod;
            this.relativePath = relativePath;
            this.rootNode = rootNode;
        }

        public string InferredType => "XMLSyntaxError";

        public IEnumerable<string> GetInvolvedModIds()
        {
            if (mod == null) yield break;
            var modId = RimCrowConstants.TryGetModId(mod);
            if (!string.IsNullOrEmpty(modId))
                yield return modId;
        }

        public IEnumerable<string> GetRelatedFiles()
        {
            if (!string.IsNullOrEmpty(relativePath))
                yield return relativePath;
            else if (!string.IsNullOrEmpty(rootNode))
                yield return $"<root:{rootNode}>";
        }
    }

    public class DefConfigLogEnricher : ILogEnricher
    {
        public int Priority => 40;

        public bool CanEnrich(LogCaptureContext captureContext)
        {
            return captureContext.Text.StartsWith("Config error in ") ||
                   captureContext.Text.StartsWith("Exception in ConfigErrors() of ");
        }

        public ILogEnrichmentData Collect(LogCaptureContext captureContext)
        {
            string defName = TryExtractDefName(captureContext.Text);
            if (string.IsNullOrEmpty(defName)) return null;

            var sourceAsset = DefSourceMap.GetAssetByDefName(defName);
            var parentChain = BuildParentChain(defName);

            var allDefNames = new List<string> { defName };
            allDefNames.AddRange(parentChain.Select(p => p.parentName));

            var patches = allDefNames
                .SelectMany(def => PatchSourceMap.Get(def).Select(patch => (defName: def, patch)))
                .GroupBy(x => (pkgId: x.patch.Mod?.PackageId ?? "", patchType: x.patch.OperationType ?? "", x.defName))
                .Select(g => g.First())
                .ToList();

            if (sourceAsset == null && parentChain.Count == 0 && patches.Count == 0) return null;

            return new DefConfigLogEnrichmentData(defName, sourceAsset, parentChain, patches);
        }

        private static string TryExtractDefName(string text)
        {
            int startIndex = text.StartsWith("Config error in ") ? 16 : 31;
            int endIndex = text.IndexOf(':', startIndex);
            if (endIndex <= startIndex) return null;

            return text.Substring(startIndex, endIndex - startIndex).Trim();
        }

        private static List<(string parentName, LoadableXmlAsset asset, int depth)> BuildParentChain(string defName, int maxDepth = 10)
        {
            var result = new List<(string parentName, LoadableXmlAsset asset, int depth)>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { defName };

            string currentName = defName;
            int depth = 0;

            while (depth < maxDepth)
            {
                string parentName = DefSourceMap.GetParentNameByDefName(currentName);
                if (string.IsNullOrEmpty(parentName) || !visited.Add(parentName))
                    break;

                result.Add((parentName, DefSourceMap.GetAssetByDefName(parentName), depth));
                currentName = parentName;
                depth++;
            }

            return result;
        }
    }

    public class CrossRefLogEnricher : ILogEnricher
    {
        public int Priority => 45;

        public bool CanEnrich(LogCaptureContext captureContext)
        {
            return captureContext.Text.StartsWith("Could not resolve cross-reference to") ||
                   captureContext.Text.Contains("Could not resolve reference to object with loadID");
        }

        public ILogEnrichmentData Collect(LogCaptureContext captureContext)
        {
            bool isLoadIdTarget;
            var targetToken = TryExtractCrossRefTarget(captureContext.Text, out isLoadIdTarget);
            if (string.IsNullOrEmpty(targetToken)) return null;

            var usages = new List<CrossRefUsage>();
            var patches = new List<CrossRefPatch>();

            // `named xxx` 属于 Def 级交叉引用，可以继续回溯到 XML 和 Patch。
            // `loadID xxx` 属于运行期存档对象引用，不一定能映射回 Def/XML，这里至少把 loadID 本身保留下来，
            // 避免之前“声称支持但实际直接返回 null”的情况。
            if (!isLoadIdTarget)
            {
                foreach (var usageSnapshot in XmlBuckets.Get(targetToken))
                {
                    var rootDefName = usageSnapshot.RootDefName;
                    var sourceAsset = !string.IsNullOrEmpty(rootDefName)
                        ? DefSourceMap.GetAssetByDefName(rootDefName)
                        : null;

                    usages.Add(new CrossRefUsage(
                        sourceAsset?.mod,
                        sourceAsset?.FullFilePath ?? sourceAsset?.name,
                        usageSnapshot.NodePath,
                        rootDefName));
                }

                foreach (var usage in usages)
                {
                    if (string.IsNullOrEmpty(usage.RootDefName)) continue;

                    var patchMatches = PatchSourceMap.Get(usage.RootDefName)
                        .Where(p => !string.IsNullOrEmpty(p.ValueText) &&
                                    p.ValueText.IndexOf(targetToken, StringComparison.OrdinalIgnoreCase) >= 0)
                        .GroupBy(p => new
                        {
                            PackageId = p.Mod?.PackageId ?? "",
                            PatchType = p.OperationType ?? "",
                            FindModTarget = p.FindModTarget ?? ""
                        })
                        .Select(g => g.First());

                    foreach (var patch in patchMatches)
                    {
                        patches.Add(new CrossRefPatch(
                            usage.RootDefName,
                            patch.Mod,
                            patch.OperationType ?? "PatchOperation",
                            patch.FindModTarget));
                    }
                }
            }

            if (usages.Count == 0 && patches.Count == 0 && !isLoadIdTarget) return null;

            return new CrossRefLogEnrichmentData(targetToken, usages, patches, isLoadIdTarget);
        }

        private static string TryExtractCrossRefTarget(string text, out bool isLoadIdTarget)
        {
            isLoadIdTarget = false;
            if (string.IsNullOrEmpty(text)) return null;

            const string namedMarker = " named ";
            int namedIndex = text.IndexOf(namedMarker, StringComparison.Ordinal);
            if (namedIndex >= 0)
            {
                return ExtractCrossRefToken(text, namedIndex + namedMarker.Length);
            }

            const string loadIdMarker = "loadID ";
            int loadIdIndex = text.IndexOf(loadIdMarker, StringComparison.OrdinalIgnoreCase);
            if (loadIdIndex >= 0)
            {
                isLoadIdTarget = true;
                return ExtractCrossRefToken(text, loadIdIndex + loadIdMarker.Length);
            }

            return null;
        }

        private static string ExtractCrossRefToken(string text, int startIndex)
        {
            if (startIndex < 0 || startIndex >= text.Length) return null;

            int endIndex = text.IndexOfAny(new[] { ' ', '\n', '\r', ',', ';', ')', ']' }, startIndex);
            if (endIndex < 0) endIndex = text.Length;

            var token = text.Substring(startIndex, endIndex - startIndex).Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }

    }

    public class CrossRefUsage
    {
        public CrossRefUsage(ModContentPack mod, string filePath, string nodePath, string rootDefName)
        {
            Mod = mod;
            FilePath = filePath;
            NodePath = nodePath;
            RootDefName = rootDefName;
        }

        public ModContentPack Mod { get; }
        public string FilePath { get; }
        public string NodePath { get; }
        public string RootDefName { get; }
    }

    public class CrossRefPatch
    {
        public CrossRefPatch(string targetDefName, ModContentPack mod, string opType, string findModTarget)
        {
            TargetDefName = targetDefName;
            Mod = mod;
            OpType = opType;
            FindModTarget = findModTarget;
        }

        public string TargetDefName { get; }
        public ModContentPack Mod { get; }
        public string OpType { get; }
        public string FindModTarget { get; }
    }

    public class CrossRefLogEnrichmentData : ILogEnrichmentData
    {
        private readonly string targetToken;
        private readonly List<CrossRefUsage> usages;
        private readonly List<CrossRefPatch> patches;
        private readonly bool isLoadIdTarget;

        public CrossRefLogEnrichmentData(string targetToken, List<CrossRefUsage> usages, List<CrossRefPatch> patches, bool isLoadIdTarget)
        {
            this.targetToken = targetToken;
            this.usages = usages;
            this.patches = patches;
            this.isLoadIdTarget = isLoadIdTarget;
        }

        public string InferredType => "CrossRefError";

        public IEnumerable<string> GetInvolvedModIds()
        {
            foreach (var usage in usages)
            {
                var modId = RimCrowConstants.TryGetModId(usage.Mod);
                if (!string.IsNullOrEmpty(modId))
                    yield return modId;
            }

            foreach (var patch in patches)
            {
                var modId = RimCrowConstants.TryGetModId(patch.Mod);
                if (!string.IsNullOrEmpty(modId))
                    yield return modId;
            }
        }

        public IEnumerable<string> GetRelatedFiles()
        {
            foreach (var usage in usages)
            {
                if (!string.IsNullOrEmpty(usage.FilePath))
                    yield return usage.FilePath;

                if (!string.IsNullOrEmpty(usage.NodePath))
                    yield return string.Format("xref:{0}:{1}", targetToken, usage.NodePath);
            }

            foreach (var patch in patches)
            {
                yield return string.Format("patch:{0}:{1}", patch.TargetDefName, patch.OpType);
            }

            if (isLoadIdTarget && usages.Count == 0 && patches.Count == 0)
            {
                yield return string.Format("loadID:{0}", targetToken);
            }
        }
    }

    public class DefConfigLogEnrichmentData : ILogEnrichmentData
    {
        private readonly string defName;
        private readonly LoadableXmlAsset sourceAsset;
        private readonly List<(string parentName, LoadableXmlAsset asset, int depth)> parentChain;
        private readonly List<(string defName, PatchInfo patch)> patches;

        public DefConfigLogEnrichmentData(
            string defName,
            LoadableXmlAsset sourceAsset,
            List<(string parentName, LoadableXmlAsset asset, int depth)> parentChain,
            List<(string defName, PatchInfo patch)> patches)
        {
            this.defName = defName;
            this.sourceAsset = sourceAsset;
            this.parentChain = parentChain;
            this.patches = patches;
        }

        public string InferredType => "DefConfigError";

        public IEnumerable<string> GetInvolvedModIds()
        {
            var sourceModId = RimCrowConstants.TryGetModId(sourceAsset?.mod);
            if (!string.IsNullOrEmpty(sourceModId))
                yield return sourceModId;

            foreach (var (_, asset, _) in parentChain)
            {
                var modId = RimCrowConstants.TryGetModId(asset?.mod);
                if (!string.IsNullOrEmpty(modId))
                    yield return modId;
            }

            foreach (var (_, patch) in patches)
            {
                var modId = RimCrowConstants.TryGetModId(patch.Mod);
                if (!string.IsNullOrEmpty(modId))
                    yield return modId;
            }
        }

        public IEnumerable<string> GetRelatedFiles()
        {
            if (!string.IsNullOrEmpty(sourceAsset?.FullFilePath))
                yield return sourceAsset.FullFilePath;

            foreach (var (_, asset, _) in parentChain)
            {
                if (!string.IsNullOrEmpty(asset?.FullFilePath))
                    yield return asset.FullFilePath;
            }

            foreach (var (targetDefName, patch) in patches)
            {
                string patchType = patch.OperationType ?? "PatchOperation";
                yield return $"patch:{targetDefName}:{patchType}";
            }

            if (patches.Count == 0 && sourceAsset == null)
                yield return $"def:{defName}";
        }
    }

    public enum RimCrowLogOutputLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    public class RimCrowCompanionSettings : ModSettings
    {
        public bool enableAssemblyScan;
        public RimCrowLogOutputLevel minimumLogOutputLevel = RimCrowLogOutputLevel.Info;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enableAssemblyScan, "enableAssemblyScan", false);

            int minimumLevelValue = (int)minimumLogOutputLevel;
            Scribe_Values.Look(ref minimumLevelValue, "minimumLogOutputLevel", (int)RimCrowLogOutputLevel.Info);
            minimumLogOutputLevel = RimCrowCompanionMod.ClampLogOutputLevel(minimumLevelValue);

            base.ExposeData();
        }
    }

    // ==========================================
    // 3. 主入口与拦截器
    // ==========================================
    public class RimCrowCompanionMod : Mod
    {
        private static RimCrowCompanionSettings settings;
        private readonly string settingsCategoryName;
        private static string dataRootPath;
        private static string logFilePath;
        private static string prevLogFilePath; 
        private static readonly object fileLock = new object();
        private static readonly MethodInfo GetStackTraceLogTypeMethod = typeof(Application).GetMethod("GetStackTraceLogType", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo ExecuteWhenFinishedMethod = AccessTools.Method(typeof(LongEventHandler), "ExecuteWhenFinished", new Type[] { typeof(Action) });
        private static readonly PropertyInfo SaveDataFolderPathProperty = AccessTools.Property(typeof(GenFilePaths), "SaveDataFolderPath");
        private static readonly MethodInfo FolderUnderSaveDataMethod = AccessTools.Method(typeof(GenFilePaths), "FolderUnderSaveData", new Type[] { typeof(string) });

        private static DateTime lastWriteTime = DateTime.MinValue;
        private static int mainThreadId;
        private static volatile bool deepAnalysisReady;
        // 正则表达式：提取 [Package.Id]
        private static readonly Regex PackageIdRegex = new Regex(@"\[([a-zA-Z0-9\-_]+(?:\.[a-zA-Z0-9\-_]+)+)\]", RegexOptions.Compiled);
        // 正则表达式：提取 assembly name: xxx
        private static readonly Regex AssemblyNameRegex = new Regex(@"assembly name:\s*([a-zA-Z0-9\-_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public RimCrowCompanionMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<RimCrowCompanionSettings>();
            settingsCategoryName = content?.Name ?? "RimCrow Companion";
            mainThreadId = Environment.CurrentManagedThreadId;
            deepAnalysisReady = false;
            dataRootPath = ResolveRuntimeDataRootPath();
            logFilePath = Path.Combine(dataRootPath, "RimCrow_Realtime.log");
            prevLogFilePath = Path.Combine(dataRootPath, "RimCrow_Realtime-prev.log");
            string logInitializationIssue = InitializeLogFiles();
            TryScheduleDeepAnalysisReady();

            if (ShouldAdjustUnityStackTraceSettings())
            {
                EnsureStackTraceLogTypeAtLeastScriptOnly(LogType.Error);
                EnsureStackTraceLogTypeAtLeastScriptOnly(LogType.Exception);
            }

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            try
            {
                var harmony = new Harmony("com.rimcrow.companion.core");
                var logErrorPrefix = CreateObserverHarmonyMethod(nameof(LogErrorPrefix));
                var logWarningPrefix = CreateObserverHarmonyMethod(nameof(LogWarningPrefix));
                var logMessagePrefix = CreateObserverHarmonyMethod(nameof(LogMessagePrefix));

                // Hook 1: 基础日志拦截
                // 这是纯观察型 Prefix，不会拦截原方法；把优先级放到较后面，
                // 尽量读取其它模组修饰后的最终日志文本，减少抢顺序带来的兼容问题。
                harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new Type[] { typeof(string) }),
                    prefix: logErrorPrefix);
                harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new Type[] { typeof(string) }),
                    prefix: logWarningPrefix);
                harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Message), new Type[] { typeof(string) }),
                    prefix: logMessagePrefix);

                // Hook 2: XML 加载精准定位 (LogAfterDefError 思想)
                var m_DefFromNode_14 = AccessTools.Method(typeof(DirectXmlLoader), "DefFromNode");
                // Probe optional Verse internals by name so older/newer RimWorld builds can skip them safely.
                var directXmlToObjectNewType = AccessTools.TypeByName("Verse.DirectXmlToObjectNew");
                var m_DefFromNode_15 = directXmlToObjectNewType != null
                    ? AccessTools.Method(directXmlToObjectNewType, "DefFromNodeNew")
                    : null;

                var prefixXml = new HarmonyMethod(typeof(RimCrowCompanionMod), nameof(Prefix_DefFromNode));
                var finalizerXml = new HarmonyMethod(typeof(RimCrowCompanionMod), nameof(Finalizer_DefFromNode));
                var m_ApplyPatches = AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches));
                var m_ValidateListNode = AccessTools.Method(typeof(DirectXmlToObject), nameof(DirectXmlToObject.ValidateListNode));

                if (m_DefFromNode_14 != null) harmony.Patch(m_DefFromNode_14, prefix: prefixXml, finalizer: finalizerXml);
                if (m_DefFromNode_15 != null) harmony.Patch(m_DefFromNode_15, prefix: prefixXml, finalizer: finalizerXml);
                if (m_ApplyPatches != null)
                    harmony.Patch(m_ApplyPatches, prefix: CreateObserverHarmonyMethod(nameof(Prefix_ApplyPatches)));
                if (m_ValidateListNode != null)
                    harmony.Patch(m_ValidateListNode, postfix: new HarmonyMethod(typeof(RimCrowCompanionMod), nameof(Postfix_ValidateListNode)));

                ProcessLog("INFO", "[RimCrow Companion] 深度智能诊断 Hook 注入成功！", "");
                if (!string.IsNullOrEmpty(logInitializationIssue))
                    ProcessLog("WARNING", $"[RimCrow Companion] 日志初始化时发生异常，已使用降级策略: {logInitializationIssue}", "");
            }
            catch (Exception e)
            {
                ProcessLog("ERROR", $"[RimCrow Companion] 深度 Hook 失败: {e.Message}", e.StackTrace);
            }

            // --- 3. Unity 原生崩溃监听 ---
            Application.logMessageReceivedThreaded += OnUnityLogMessage;
        }

        internal static RimCrowCompanionSettings Settings => settings ?? (settings = LoadedModManager.GetMod<RimCrowCompanionMod>()?.GetSettings<RimCrowCompanionSettings>() ?? new RimCrowCompanionSettings());

        internal static RimCrowLogOutputLevel ClampLogOutputLevel(int value)
        {
            if (value <= (int)RimCrowLogOutputLevel.Info) return RimCrowLogOutputLevel.Info;
            if (value >= (int)RimCrowLogOutputLevel.Error) return RimCrowLogOutputLevel.Error;
            return (RimCrowLogOutputLevel)value;
        }

        private static bool ShouldWriteLevel(string level)
        {
            int current = GetLevelSeverity(level);
            int minimum = (int)Settings.minimumLogOutputLevel;
            return current >= minimum;
        }

        private static int GetLevelSeverity(string level)
        {
            if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
                return (int)RimCrowLogOutputLevel.Error;

            if (string.Equals(level, "WARNING", StringComparison.OrdinalIgnoreCase))
                return (int)RimCrowLogOutputLevel.Warning;

            return (int)RimCrowLogOutputLevel.Info;
        }

        private static string GetLogLevelLabel(RimCrowLogOutputLevel level)
        {
            switch (level)
            {
                case RimCrowLogOutputLevel.Error:
                    return "仅错误";
                case RimCrowLogOutputLevel.Warning:
                    return "警告及以上";
                default:
                    return "信息及以上";
            }
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled(
                "启用高成本 Assembly 扫描",
                ref Settings.enableAssemblyScan,
                "会额外遍历已加载程序集并尝试发现反射/IL 层面的潜在错误。启动期更慢，也更容易产生日志噪声。");

            listing.GapLine();
            listing.Label($"最低日志输出等级: {GetLogLevelLabel(Settings.minimumLogOutputLevel)}");

            float levelValue = listing.Slider((float)Settings.minimumLogOutputLevel, (float)RimCrowLogOutputLevel.Info, (float)RimCrowLogOutputLevel.Error);
            Settings.minimumLogOutputLevel = ClampLogOutputLevel(Mathf.RoundToInt(levelValue));

            listing.Label("信息及以上 = 记录 INFO / WARNING / ERROR");
            listing.Label("警告及以上 = 仅记录 WARNING / ERROR");
            listing.Label("仅错误 = 仅记录 ERROR");
            listing.End();
        }

        public override string SettingsCategory()
        {
            return settingsCategoryName;
        }

        private static HarmonyMethod CreateObserverHarmonyMethod(string methodName)
        {
            return new HarmonyMethod(typeof(RimCrowCompanionMod), methodName)
            {
                priority = Priority.Last
            };
        }

        private static void TryScheduleDeepAnalysisReady()
        {
            if (ExecuteWhenFinishedMethod == null)
            {
                deepAnalysisReady = true;
                return;
            }

            try
            {
                ExecuteWhenFinishedMethod.Invoke(null, new object[] { (Action)(() => deepAnalysisReady = true) });
            }
            catch
            {
                // 当前版本如果没有这个回调，至少不要把功能永久禁用。
                deepAnalysisReady = true;
            }
        }

        internal static void ExecuteAfterLongEvents(Action action)
        {
            if (action == null) return;

            if (ExecuteWhenFinishedMethod == null)
            {
                action();
                return;
            }

            try
            {
                ExecuteWhenFinishedMethod.Invoke(null, new object[] { action });
            }
            catch
            {
                action();
            }
        }

        internal static bool CanRunDeepAnalysis()
        {
            return deepAnalysisReady && Environment.CurrentManagedThreadId == mainThreadId;
        }

        private static void CaptureObservedLog(string level, string text, string providedStack = null, bool forceShallow = false)
        {
            if (!ShouldWriteLevel(level))
                return;

            try
            {
                ProcessLog(level, text, providedStack, forceShallow);
            }
            catch (Exception ex)
            {
                TryWriteObserverFailure(level, text, providedStack, ex);
            }
        }

        private static string GetRuntimeDataRootPath()
        {
            if (string.IsNullOrWhiteSpace(dataRootPath))
                dataRootPath = ResolveRuntimeDataRootPath();

            return dataRootPath;
        }

        internal static string GetDataDirectoryFilePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return GetRuntimeDataRootPath();

            return Path.Combine(GetRuntimeDataRootPath(), fileName);
        }

        private static string ResolveRuntimeDataRootPath()
        {
            string candidate =
                TryGetSaveDataRootFromVerse() ??
                TryGetSaveDataRootFromCommandLine() ??
                Application.persistentDataPath;

            return NormalizePath(candidate) ?? Application.persistentDataPath;
        }

        private static string TryGetSaveDataRootFromVerse()
        {
            try
            {
                if (SaveDataFolderPathProperty != null)
                {
                    string fromProperty = SaveDataFolderPathProperty.GetValue(null, null) as string;
                    if (!string.IsNullOrWhiteSpace(fromProperty))
                        return fromProperty;
                }
            }
            catch
            {
            }

            try
            {
                if (FolderUnderSaveDataMethod != null)
                {
                    string fromMethod = FolderUnderSaveDataMethod.Invoke(null, new object[] { string.Empty }) as string;
                    if (!string.IsNullOrWhiteSpace(fromMethod))
                        return fromMethod;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string TryGetSaveDataRootFromCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                const string optionName = "-savedatafolder";

                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith(optionName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string value = null;
                    if (arg.Length > optionName.Length)
                    {
                        char separator = arg[optionName.Length];
                        if (separator == '=' || separator == ':')
                            value = arg.Substring(optionName.Length + 1);
                    }

                    if (string.IsNullOrWhiteSpace(value) && i + 1 < args.Length)
                    {
                        string nextArg = args[i + 1];
                        if (!string.IsNullOrWhiteSpace(nextArg) && !nextArg.StartsWith("-", StringComparison.Ordinal))
                            value = nextArg;
                    }

                    value = NormalizePath(value);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string trimmed = path.Trim().Trim('"');
            if (trimmed.Length == 0)
                return null;

            try
            {
                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return trimmed;
            }
        }

        private static void TryWriteObserverFailure(string level, string text, string providedStack, Exception ex)
        {
            try
            {
                var fallbackStack = new StringBuilder();

                if (!string.IsNullOrWhiteSpace(providedStack))
                    fallbackStack.Append(providedStack.Trim());

                if (fallbackStack.Length > 0)
                    fallbackStack.Append("\n\n");

                fallbackStack.Append("[RimCrow Companion] Diagnostic pipeline failure: ");
                fallbackStack.Append(ex.GetType().FullName);
                fallbackStack.Append(": ");
                fallbackStack.Append(ex.Message);

                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    fallbackStack.Append("\n");
                    fallbackStack.Append(ex.StackTrace.Trim());
                }

                WriteLogWithContext(
                    level,
                    text ?? string.Empty,
                    fallbackStack.ToString(),
                    !string.IsNullOrWhiteSpace(providedStack),
                    new HashSet<string>(StringComparer.Ordinal),
                    new List<string>(),
                    "ObserverFailure");
            }
            catch
            {
            }
        }

        private static bool ShouldAdjustUnityStackTraceSettings()
        {
            // `Application.SetStackTraceLogType()` 是全局开关，会影响整个 RimWorld 进程。
            // 一旦玩家已安装 Visual Exceptions 这类诊断模组，就尽量不要抢着改全局设置。
            if (ModCompatibilityDetector.HasKnownDiagnosticsMod())
            {
                ProcessLog("INFO", "[RimCrow Companion] 检测到其他诊断模组，跳过全局 StackTrace 设置以减少冲突。", "");
                return false;
            }

            return true;
        }

        private static void EnsureStackTraceLogTypeAtLeastScriptOnly(LogType logType)
        {
            try
            {
                var current = TryGetStackTraceLogType(logType);

                // 如果别的模组或玩家已经要求更完整的 Full 栈，就不要把它降级成 ScriptOnly。
                if (current.HasValue && current.Value == StackTraceLogType.Full)
                    return;

                Application.SetStackTraceLogType(logType, StackTraceLogType.ScriptOnly);
            }
            catch (Exception ex)
            {
                ProcessLog("WARNING", $"[RimCrow Companion] 设置 {logType} 的 StackTraceLogType 失败: {ex.Message}", ex.StackTrace);
            }
        }

        private static StackTraceLogType? TryGetStackTraceLogType(LogType logType)
        {
            if (GetStackTraceLogTypeMethod == null) return null;

            try
            {
                object result = GetStackTraceLogTypeMethod.Invoke(null, new object[] { logType });
                return result is StackTraceLogType stackTraceType ? stackTraceType : (StackTraceLogType?)null;
            }
            catch
            {
                return null;
            }
        }

        // --- Context Hook Methods ---
        public static void Prefix_DefFromNode(XmlNode node, LoadableXmlAsset loadingAsset)
        {
            XmlSourceTracker.Push(loadingAsset, node);

            if (loadingAsset == null || node == null) return;

            string defName = node["defName"]?.InnerText?.Trim();
            string parentName = node.Attributes?["ParentName"]?.Value?.Trim();

            if (string.IsNullOrEmpty(defName))
                defName = node.Attributes?["Name"]?.Value?.Trim();

            if (!string.IsNullOrEmpty(defName))
                DefSourceMap.Add(defName, loadingAsset, parentName);
        }

        public static Exception Finalizer_DefFromNode(Exception __exception)
        {
            XmlSourceTracker.Pop();
            return __exception;
        }

        public static void Prefix_ApplyPatches()
        {
            PatchSourceMap.CollectFromRunningMods();
        }

        public static void Postfix_ValidateListNode(ref bool __result, XmlNode listEntryNode, XmlNode listRootNode, Type listItemType)
        {
            if (!__result || listEntryNode == null || listRootNode == null || listItemType == null) return;

            if (listItemType.IsGenericType && listItemType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                var args = listItemType.GetGenericArguments();
                var keyType = args[0];
                var valueType = args[1];

                if (GenTypes.IsDef(keyType))
                {
                    var keyName = listEntryNode["key"]?.InnerText;
                    if (!string.IsNullOrEmpty(keyName))
                        XmlBuckets.Add(keyName, listRootNode);
                }

                if (GenTypes.IsDef(valueType))
                {
                    var valueName = listEntryNode["value"]?.InnerText;
                    if (!string.IsNullOrEmpty(valueName))
                        XmlBuckets.Add(valueName, listRootNode);
                }

                return;
            }

            if (GenTypes.IsDef(listItemType))
            {
                var valueName = listEntryNode.InnerText;
                if (!string.IsNullOrEmpty(valueName))
                    XmlBuckets.Add(valueName, listRootNode);
            }
        }

        // --- Log Hook 实现 ---
        public static bool LogErrorPrefix(string text) { CaptureObservedLog("ERROR", text); return true; }
        public static bool LogWarningPrefix(string text) { CaptureObservedLog("WARNING", text); return true; }
        public static bool LogMessagePrefix(string text) { CaptureObservedLog("INFO", text); return true; }

        private static void OnUnityLogMessage(string condition, string stackTrace, LogType type)
        {
            // 解决双重输出：如果堆栈中包含 Verse.Log，说明我们的 Harmony 已经拦截并处理过它了，直接丢弃
            if (stackTrace != null && stackTrace.Contains("Verse.Log:")) return;

            // 仅保留真正的 Unity 引擎崩溃 或 未被 Verse.Log 接管的异常
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
            {
                CaptureObservedLog("ERROR", condition, stackTrace, forceShallow: true);
            }
        }

        // ==========================================
        // 4. 数据拆分与深层富化
        // ==========================================
        private static void ProcessLog(string level, string text, string providedStack = null, bool forceShallow = false)
        {
            if (!ShouldWriteLevel(level))
                return;

            if (text == null) text = "";
            string stackText = providedStack ?? "";
            bool hasReliableStack = !string.IsNullOrEmpty(stackText);
            bool allowDeepAnalysis = !forceShallow && CanRunDeepAnalysis();

            // --- A. 智能文本分割器 (把混在 Message 里的堆栈切分出来) ---
            string[] splitMarkers = { "\n[Ref ", "\n  at ", "\n(wrapper ", "\nPrefixes:", "\nTranspilers:", "\nstatic ", "\n---" };
            int splitIndex = -1;

            foreach (var marker in splitMarkers)
            {
                int idx = text.IndexOf(marker);
                if (idx != -1 && (splitIndex == -1 || idx < splitIndex))
                {
                    splitIndex = idx;
                }
            }

            if (splitIndex != -1)
            {
                // 将分割点之后的内容转移到堆栈区
                string extractedStack = text.Substring(splitIndex + 1).Trim();
                text = text.Substring(0, splitIndex).Trim();
                stackText = string.IsNullOrEmpty(stackText) ? extractedStack : (extractedStack + "\n" + stackText);
                hasReliableStack = !string.IsNullOrEmpty(stackText);
            }

            // 如果原始日志没有附带堆栈，就生成一份“当前记录点”的回退堆栈用于落盘。
            // 注意：这类堆栈只能帮助人类排查“是谁在记日志”，不能当作“谁真正抛错”的证据，
            // 所以下面做 Mod 归因时只信任原始日志自带/拆分出来的可靠堆栈。
            if (!hasReliableStack && (level == "ERROR" || level == "WARNING"))
            {
                stackText = new StackTrace(2, true).ToString();
            }
            HashSet<string> involvedMods = new HashSet<string>();
            List<string> involvedFiles = new List<string>();
            string inferredType = "";

            // --- B. 基于 DLL 领空的堆栈分析 ---
            if (allowDeepAnalysis && hasReliableStack && !string.IsNullOrEmpty(stackText))
            {
                // 只有可靠堆栈才参与 DLL 领空分析，避免把日志包装层误判成真正的报错源头。
                HashSet<string> stackMods;
                ErrorAnalyzer.AnalyzeStackTraceText(stackText, out stackMods);
                foreach (var m in stackMods) involvedMods.Add(m);
            }

            // --- C. 专项富化管线 ---
            ILogEnrichmentData enrichment = null;
            if (allowDeepAnalysis)
            {
                enrichment = LogEnricherPipeline.TryCollect(new LogCaptureContext
                {
                    Level = level,
                    Text = text,
                    StackText = stackText
                });
            }

            if (enrichment != null)
            {
                foreach (var modId in enrichment.GetInvolvedModIds())
                    involvedMods.Add(modId);

                foreach (var file in enrichment.GetRelatedFiles())
                    involvedFiles.Add(file);

                if (string.IsNullOrEmpty(inferredType))
                    inferredType = enrichment.InferredType;
            }

            // --- D. 深度内容扫描与正则提取 ---
            string combinedText = hasReliableStack ? (text + "\n" + stackText) : text;
            var runningMods = allowDeepAnalysis ? LoadedModManager.RunningMods : null;

            if (runningMods != null)
            {
                // 1. 提取 [PackageId] 格式的 Mod
                foreach (Match match in PackageIdRegex.Matches(combinedText))
                {
                    string potentialId = match.Groups[1].Value.ToLowerInvariant();
                    // 绝对校验：这个字符串必须是真实运行的 Mod，防止把 "[1.6]" 认成 Mod！
                    if (runningMods.Any(m => string.Equals(RimCrowConstants.TryGetModId(m), potentialId, StringComparison.Ordinal)))
                    {
                        involvedMods.Add(potentialId);
                    }
                }

                // 2. 提取 "assembly name: XXX" 并反查 Mod
                Match asmMatch = AssemblyNameRegex.Match(combinedText);
                if (asmMatch.Success)
                {
                    string asmName = asmMatch.Groups[1].Value;
                    var ownerMod = runningMods.FirstOrDefault(m => m?.assemblies?.loadedAssemblies != null && m.assemblies.loadedAssemblies.Any(a => a.GetName().Name.Equals(asmName, StringComparison.OrdinalIgnoreCase)));
                    var ownerModId = RimCrowConstants.TryGetModId(ownerMod);
                    if (!string.IsNullOrEmpty(ownerModId))
                    {
                        involvedMods.Add(ownerModId);
                    }
                }
            }

            // --- E. 基础特征分类 (借鉴 rw-log-check) ---
            if (string.IsNullOrEmpty(inferredType))
            {
                if (text.StartsWith("Config error in")) inferredType = "DefConfigError";
                else if (text.Contains("cross-reference") || text.Contains("Could not resolve reference to object with loadID")) inferredType = "CrossRefError";
                else if (text.Contains("MissingMethodException") || text.Contains("ReflectionTypeLoadException") || text.Contains("TypeLoadException")) inferredType = "AssemblyConflict";
                else if (text.Contains("Exception ticking")) inferredType = "TickException";
                else if (text.Contains("Exception drawing")) inferredType = "DrawException";
                else if (text.Contains("NullReferenceException")) inferredType = "NullReference";
                else if (text.Contains("System.OutOfMemoryException") || text.Contains("Could not allocate memory")) inferredType = "OutOfMemory";
                else if (text.Contains("Failed to find any texture") || text.Contains("Could not load UnityEngine.Texture2D")) inferredType = "MissingTexture";
            }

            // --- F. 动态将 Mod 名称注入到 Message 前方 ---
            if (allowDeepAnalysis && involvedMods.Count > 0)
            {
                // 将 PackageId 转换为可读的 Mod 名称
                var modNames = involvedMods.Select(id =>
                {
                    var m = runningMods?.FirstOrDefault(x => string.Equals(RimCrowConstants.TryGetModId(x), id, StringComparison.Ordinal));
                    return m != null ? m.Name : id;
                }).OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
                string prefix = $"[{string.Join(", ", modNames)}]";
                // 避免重复添加 (比如 MeleeAnim 已经自己加了前缀的情况)
                if (!text.StartsWith(prefix))
                {
                    text = $"{prefix} {text}";
                }
            }

            WriteLogWithContext(level, text, stackText, hasReliableStack, involvedMods, involvedFiles.Distinct().ToList(), inferredType);
        }

        // ==========================================
        // 4. 哈希缓存与节流写入引擎 (解决 A-B-A-B 交错轰炸)
        // ==========================================
        private class LogCacheEntry
        {
            public string Fingerprint;
            public string Level, Message, Stack, InferredType;
            public HashSet<string> Mods;
            public List<string> Files;
            public int PendingCount;
            public DateTime LastFlushTime;
            public bool HasReliableStack;
        }

        // 缓存池，最多记录 100 种不同的错误，防止内存泄漏
        private static Dictionary<string, LogCacheEntry> logCache = new Dictionary<string, LogCacheEntry>(StringComparer.Ordinal);
        private static readonly int MAX_CACHE_SIZE = 100;
        private static readonly Regex HexAddressRegex = new Regex(@"0x[0-9a-fA-F]+", RegexOptions.Compiled);
        private static readonly Regex RuntimeInstanceTokenRegex = new Regex(@"\b([A-Za-z][A-Za-z0-9]*_)[A-Za-z0-9]*\d+\b", RegexOptions.Compiled);
        private static readonly Regex NumberRegex = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex MultiWhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private static string BuildLogFingerprint(string level, string text, string inferredType)
        {
            // 指纹只用于“短时间重复聚合”，所以要尽量抹平实例号、内存地址、运行时编号等噪声，
            // 但仍保留错误类型和主要语义，避免把不同类别的日志硬合并到一起。
            string normalizedType = (inferredType ?? string.Empty).Trim().ToLowerInvariant();
            string normalizedLevel = (level ?? string.Empty).Trim().ToLowerInvariant();
            string normalizedMessage = NormalizeLogMessageForFingerprint(text);
            return string.Format("{0}|{1}|{2}", normalizedLevel, normalizedType, normalizedMessage);
        }

        private static string NormalizeLogMessageForFingerprint(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;

            string normalized = message.Trim();

            // 抹平十六进制地址：例如 `0x000001A3B4`。
            normalized = HexAddressRegex.Replace(normalized, "<HEX>");

            // 抹平运行期实例名：例如 `Thing_HiTechResearchBench724742`、`Mech_Fabricor485758`、`Job_5395984`。
            // 这里保留前缀 `Thing_` / `Job_` 等语义标签，方便区分对象类型。
            normalized = RuntimeInstanceTokenRegex.Replace(normalized, "${1}<ID>");

            // 最后统一所有纯数字，避免坐标、tick、loadID、数量等差异打散聚合。
            normalized = NumberRegex.Replace(normalized, "<NUM>");
            normalized = MultiWhitespaceRegex.Replace(normalized, " ");

            return normalized.Trim().ToLowerInvariant();
        }

        private static void MergeCacheEntry(LogCacheEntry entry, string stack, bool hasReliableStack, HashSet<string> mods, List<string> files, string inferredType)
        {
            if (entry == null) return;

            // 后续重复日志如果带来了更可靠、更完整的堆栈，就用新的结果覆盖旧缓存，
            // 避免“第一次命中信息太弱，之后永远弱下去”。
            if (ShouldReplaceStack(entry.Stack, entry.HasReliableStack, stack, hasReliableStack))
            {
                entry.Stack = stack;
                entry.HasReliableStack = hasReliableStack;
            }

            if (entry.Mods == null) entry.Mods = new HashSet<string>();
            if (mods != null)
            {
                foreach (var mod in mods)
                {
                    if (!string.IsNullOrEmpty(mod))
                        entry.Mods.Add(mod);
                }
            }

            if (entry.Files == null) entry.Files = new List<string>();
            if (files != null)
            {
                foreach (var file in files)
                {
                    if (string.IsNullOrEmpty(file)) continue;
                    if (!entry.Files.Any(existing => string.Equals(existing, file, StringComparison.Ordinal)))
                        entry.Files.Add(file);
                }
            }

            if (string.IsNullOrEmpty(entry.InferredType) && !string.IsNullOrEmpty(inferredType))
                entry.InferredType = inferredType;
        }

        private static bool ShouldReplaceStack(string existingStack, bool existingReliable, string incomingStack, bool incomingReliable)
        {
            if (string.IsNullOrWhiteSpace(incomingStack)) return false;
            if (string.IsNullOrWhiteSpace(existingStack)) return true;

            if (incomingReliable != existingReliable)
                return incomingReliable;

            return incomingStack.Length > existingStack.Length;
        }

        private static void WriteLogWithContext(string level, string text, string stack, bool hasReliableStack, HashSet<string> mods, List<string> files, string inferredType)
        {
            if (!ShouldWriteLevel(level))
                return;

            try
            {
                lock (fileLock)
                {
                    // 使用“归一化指纹”代替原始字符串哈希：
                    // 同一类日志即使实例号不同，也能在短时间内合并计数。
                    string fingerprint = BuildLogFingerprint(level, text, inferredType);

                    if (logCache.TryGetValue(fingerprint, out var entry))
                    {
                        MergeCacheEntry(entry, stack, hasReliableStack, mods, files, inferredType);
                        entry.PendingCount++;

                        // 如果距离上次写入磁盘已经超过 1 秒，将这段时间积累的次数集中写入一次
                        if ((DateTime.Now - entry.LastFlushTime).TotalSeconds >= 1.0)
                        {
                            FlushToFile(entry.Level, entry.Message, entry.Stack, entry.PendingCount, entry.Mods, entry.Files, entry.InferredType);
                            entry.PendingCount = 0; // 重置等待写入的次数
                            entry.LastFlushTime = DateTime.Now;
                        }
                    }
                    else
                    {
                        // 防止遇到随机字符串报错导致字典无限膨胀
                        if (logCache.Count > MAX_CACHE_SIZE)
                        {
                            FlushPendingCacheEntries_NoLock();
                            logCache.Clear();
                        }

                        var newEntry = new LogCacheEntry
                        {
                            Fingerprint = fingerprint,
                            Level = level,
                            Message = text,
                            Stack = stack,
                            Mods = mods != null ? new HashSet<string>(mods) : new HashSet<string>(),
                            Files = files != null ? files.Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList() : new List<string>(),
                            InferredType = inferredType,
                            PendingCount = 0, // 第一次直接写入，所以 pending 为 0
                            LastFlushTime = DateTime.Now,
                            HasReliableStack = hasReliableStack
                        };
                        logCache[fingerprint] = newEntry;

                        // 第一次遇到这个错误，立即无延迟写入磁盘 (Count = 1)
                        FlushToFile(level, text, stack, 1, mods, files, inferredType);
                    }
                }
            }
            catch { }
        }

        private static string InitializeLogFiles()
        {
            List<string> issues = new List<string>();

            try
            {
                var logDirectory = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(logDirectory)) Directory.CreateDirectory(logDirectory);
            }
            catch (Exception ex)
            {
                issues.Add($"create dir failed: {ex.Message}");
            }

            if (File.Exists(logFilePath))
            {
                try
                {
                    File.Copy(logFilePath, prevLogFilePath, true);
                }
                catch (Exception ex)
                {
                    issues.Add($"backup copy failed: {ex.Message}");
                }
            }

            try
            {
                using (var stream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                }
            }
            catch (Exception ex)
            {
                issues.Add($"reset current log failed: {ex.Message}");
            }

            return issues.Count > 0 ? string.Join(" | ", issues) : null;
        }

        private static void FlushPendingCacheEntries()
        {
            lock (fileLock)
            {
                FlushPendingCacheEntries_NoLock();
            }
        }

        private static void FlushPendingCacheEntries_NoLock()
        {
            foreach (var entry in logCache.Values)
            {
                if (entry.PendingCount <= 0) continue;

                FlushToFile(
                    entry.Level,
                    entry.Message,
                    entry.Stack,
                    entry.PendingCount,
                    entry.Mods,
                    entry.Files,
                    entry.InferredType);
                entry.PendingCount = 0;
                entry.LastFlushTime = DateTime.Now;
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            FlushPendingCacheEntries();
        }


        private static void FlushToFile(string level, string text, string stack, int count, HashSet<string> mods, List<string> files, string inferredType)
        {
            if (!ShouldWriteLevel(level))
                return;

            if (count <= 0) return; // 防御性判断
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string safeText = EscapeJson(text);
            string safeStack = EscapeJson(stack);

            // 构建 Context JSON
            string modArray = "[" + string.Join(",", mods.Select(m => "\"" + EscapeJson(m) + "\"")) + "]";
            string fileArray = "[" + string.Join(",", files.Select(f => "\"" + EscapeJson(f) + "\"")) + "]";
            string contextJson = $"{{\"relatedModIds\":{modArray},\"relatedFiles\":{fileArray},\"inferredType\":\"{EscapeJson(inferredType)}\"}}";

            // 最终的标准化 Payload (完美匹配 Python 和 Vue 的契约)
            string jsonLine = $"{{\"timestamp\":\"{timestamp}\",\"level\":\"{level}\",\"message\":\"{safeText}\",\"details\":\"{safeStack}\",\"count\":{count},\"context\":{contextJson}}}";

            using (StreamWriter sw = File.AppendText(logFilePath))
            {
                sw.WriteLine(jsonLine);
                sw.Flush();
            }
            lastWriteTime = DateTime.Now;
        }

        private static string EscapeJson(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str.Replace("\\", "\\\\")
                      .Replace("\"", "\\\"")
                      .Replace("\n", "\\n")
                      .Replace("\r", "")
                      .Replace("\t", "\\t");
        }
    }

    // ==========================================
    // 4. 主动错误检查器 (吸收自 ModErrorChecker)
    // ==========================================
    [StaticConstructorOnStartup]
    public static class RimCrowProactiveChecker
    {
        static RimCrowProactiveChecker()
        {
            // 主动检查既依赖主线程，也依赖 DefDatabase / LoadedModManager 已初始化完成。
            // 因此改成等长事件队列结束后，再在主线程执行，避免扫到半初始化对象。
            RimCrowCompanionMod.ExecuteAfterLongEvents(RunProactiveChecks);
        }

        private static void RunProactiveChecks()
        {
            var runningMods = LoadedModManager.RunningMods;
            if (runningMods == null)
            {
                Log.Warning("[RimCrow Diagnostics] 主动检查已跳过：LoadedModManager.RunningMods 尚未初始化。");
                return;
            }

            try
            {
                // 智能判定：如果玩家自己装了原版 ModErrorChecker，我们就不白费力气了
                bool hasModErrorChecker = runningMods.Any(m =>
                    string.Equals(RimCrowConstants.TryGetModId(m), "taranchuk.moderrorchecker", StringComparison.Ordinal));

                if (hasModErrorChecker)
                {
                    Log.Message("[RimCrow Diagnostics] 检测到玩家已安装 ModErrorChecker，跳过内置的主动体检。");
                    return;
                }

                TryRunDiagnosticsPhase("XML/Def scan", CheckXmlAndDefs);

                // 全量 IL 扫描成本高、误报也多，所以改成显式 opt-in，
                // 并在检测到 Visual Exceptions 这类重度诊断 Mod 时默认避让。
                if (ShouldRunAssemblyScan())
                    TryRunDiagnosticsPhase("assembly scan", CheckAssemblies);
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimCrow Diagnostics] 诊断过程发生异常: {ex}");
            }
        }

        private static void TryRunDiagnosticsPhase(string phaseName, Action action)
        {
            if (action == null) return;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warning($"[RimCrow Diagnostics] 主动检查阶段 '{phaseName}' 发生异常: {ex}");
            }
        }

        private static bool ShouldRunAssemblyScan()
        {
            return RimCrowCompanionMod.Settings.enableAssemblyScan;
        }

        private static void CheckXmlAndDefs()
        {
            // 检查 WorkGiver
            foreach (var def in DefDatabase<WorkGiverDef>.AllDefs)
            {
                if (def == null) continue;
                if (def.giverClass == null) LogError(def, "is missing worker class (giverClass)");
            }

            // 检查 RoomRoleDef
            foreach (var def in DefDatabase<RoomRoleDef>.AllDefs)
            {
                if (def == null) continue;
                if (def.workerClass == null) LogError(def, "doesn't have a specified role class (workerClass)");
            }

            // 检查 ThingDef
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def == null) continue;

                try
                {
                    if (def.thingClass == null) LogError(def, "is missing thing class");

                    if (def.IsApparel && (def.thingClass == null || !typeof(RimWorld.Apparel).IsAssignableFrom(def.thingClass)))
                        LogError(def, $"is set as apparel, but its thingClass ({def.thingClass}) does not inherit from Apparel");

                    if (def.comps != null)
                    {
                        foreach (var comp in def.comps)
                        {
                            if (comp == null || comp.compClass == null)
                                LogError(def, "is missing compClass from one of its comps");
                        }
                    }

                    var recipes = def.AllRecipes;
                    if (recipes == null) continue;

                    foreach (var recipe in recipes)
                    {
                        if (recipe == null || recipe.products == null) continue;

                        foreach (var product in recipe.products)
                        {
                            if (product == null || product.thingDef == null)
                                LogError(recipe, "has an empty product. This will error out on map generation.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RimCrow Diagnostics] 检查 ThingDef '{def.defName ?? "<null>"}' 时发生异常: {ex}");
                }
            }

            // 检查声音
            foreach (var def in DefDatabase<SoundDef>.AllDefs)
            {
                if (def == null) continue;

                try
                {
                    if (def.modContentPack != null && def.modContentPack.IsOfficialMod) continue;
                    if (def.subSounds == null) continue;

                    foreach (var subSound in def.subSounds)
                    {
                        if (subSound?.grains == null) continue;

                        foreach (var grain in subSound.grains)
                        {
                            if (grain == null) continue;

                            var resolvedGrains = grain.GetResolvedGrains();
                            if (resolvedGrains == null || !resolvedGrains.Any())
                                LogError(def, "sound is missing resolved grains");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RimCrow Diagnostics] 检查 SoundDef '{def.defName ?? "<null>"}' 时发生异常: {ex}");
                }
            }
        }

        private static void LogError(Def def, string detail)
        {
            if (def == null)
            {
                Log.Error($"[Proactive Check] Def <null> {detail} and will cause issues in-game.");
                return;
            }

            string modName = def.modContentPack != null ? def.modContentPack.Name : "Unknown";
            string defName = string.IsNullOrEmpty(def.defName) ? "<null>" : def.defName;
            Log.Error($"[Proactive Check] Def {defName} (Mod: {modName}) {detail} and will cause issues in-game.");
        }

        private static void CheckAssemblies()
        {
            var methodsToCheck = new HashSet<MethodInfo>();

            // 使用统一的常量黑名单 IgnoredAssemblies 进行过滤
            foreach (var type in GenTypes.AllTypes.Where(t =>
            {
                var assemblyName = t?.Assembly?.FullName ?? string.Empty;
                return t?.Assembly != null && !RimCrowConstants.IgnoredAssemblies.Any(skip => assemblyName.IndexOf(skip, StringComparison.OrdinalIgnoreCase) >= 0);
            }))
            {
                try
                {
                    foreach (var method in AccessTools.GetDeclaredMethods(type))
                    {
                        if (method == null || method.DeclaringType == null) continue;

                        if (MethodBaseExtensions.HasMethodBody(method) && !method.DeclaringType.IsConstructedGenericType && !method.IsGenericMethod && !method.ContainsGenericParameters)
                        {
                            methodsToCheck.Add(method);
                        }
                    }
                }
                catch { }
            }

            foreach (var method in methodsToCheck)
            {
                try
                {
                    PatchProcessor.GetOriginalInstructions(method, null);
                }
                catch (Exception ex)
                {
                    if (!(ex is TypeLoadException) && !(ex is FileNotFoundException))
                    {
                        var declaringType = method?.DeclaringType;
                        var assembly = declaringType?.Assembly;
                        if (declaringType == null || assembly == null) continue;

                        var runningMods = LoadedModManager.RunningMods;
                        var mod = runningMods?.FirstOrDefault(x => x?.assemblies?.loadedAssemblies != null && x.assemblies.loadedAssemblies.Contains(assembly));
                        string modName = mod != null ? mod.Name : "Unknown";
                        string pkgId = RimCrowConstants.TryGetModId(mod) ?? "Unknown";

                        Log.Error($"[Proactive Check] Error in {modName} [{pkgId}], assembly name: {assembly.GetName().Name}, method: {declaringType.Name}:{method.Name}, exception: {ex}");
                    }
                }
            }
        }
    }
}

