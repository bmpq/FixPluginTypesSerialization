using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using FixPluginTypesSerialization.Patchers;
using FixPluginTypesSerialization.Util;
using Mono.Cecil;

namespace FixPluginTypesSerialization
{
    internal static class FixPluginTypesSerializationPatcher
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static List<string> PluginPaths = new List<string>();
        public static List<string> PluginNames = new List<string>();

        private static HashSet<string> _availableAssemblyNames;

        public static void Patch(AssemblyDefinition ass) { }

        public static void Initialize()
        {
            Log.Init();
            try
            {
                InitializeInternal();
            }
            catch (Exception e)
            {
                Log.Error($"Failed to initialize plugin types serialization fix: ({e.GetType()}) {e.Message}.");
                Log.Error(e);
            }
        }

        private static void InitializeInternal()
        {
            PopulateValidPlugins();
            DetourUnityPlayer();
        }

        private static void PopulateValidPlugins()
        {
            _availableAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in Directory.GetFiles(BepInEx.Paths.ManagedPath, "*.dll"))
                _availableAssemblyNames.Add(Path.GetFileNameWithoutExtension(f));

            // ALL bepinex assemblies (core, plugins, patchers)
            var allBepInExFiles = Directory.GetFiles(BepInEx.Paths.BepInExRootPath, "*.dll", SearchOption.AllDirectories);
            foreach (var f in allBepInExFiles)
            {
                _availableAssemblyNames.Add(Path.GetFileNameWithoutExtension(f));
            }

            var injectionCandidatePlugins = Directory.GetFiles(BepInEx.Paths.PluginPath, "*.dll", SearchOption.AllDirectories);

            foreach (var file in injectionCandidatePlugins)
            {
                if (!IsNetAssembly(file)) continue;

                if (HasMissingDependencies(file))
                {
                    continue;
                }

                PluginPaths.Add(file);
                PluginNames.Add(Path.GetFileName(file));
            }

            Log.Info($"Injected {PluginPaths.Count} plugins into Unity Native Serialization.");
        }

        private static bool HasMissingDependencies(string filePath)
        {
            try
            {
                var references = Assembly.LoadFile(filePath).GetReferencedAssemblies();

                foreach (var refName in references)
                {
                    if (!_availableAssemblyNames.Contains(refName.Name))
                    {
                        Log.Warning($"Excluding '{Path.GetFileName(filePath)}' from Serialization Fix. It references {refName.Name} which doesn't exist.");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not verify dependencies for {Path.GetFileName(filePath)}: {ex.Message}");
                return true;
            }
        }

        public static bool IsNetAssembly(string fileName)
        {
            try
            {
                AssemblyName.GetAssemblyName(fileName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static unsafe void DetourUnityPlayer()
        {
            var unityDllPath = Path.Combine(BepInEx.Paths.GameRootPath, "UnityPlayer.dll");
            //Older Unity builds had all functionality in .exe instead of UnityPlayer.dll
            if (!File.Exists(unityDllPath))
            {
                unityDllPath = BepInEx.Paths.ExecutablePath;
            }

            static bool IsUnityPlayer(ProcessModule p)
            {
                return p.ModuleName.ToLowerInvariant().Contains("unityplayer");
            }

            var proc = Process.GetCurrentProcess().Modules
                .Cast<ProcessModule>()
                .FirstOrDefault(IsUnityPlayer) ?? Process.GetCurrentProcess().MainModule;

            var patternDiscoverer = new PatternDiscoverer(proc.BaseAddress, unityDllPath);
            CommonUnityFunctions.Init(patternDiscoverer);

            var awakeFromLoadPatcher = new AwakeFromLoad();
            var isAssemblyCreatedPatcher = new IsAssemblyCreated();
            var isFileCreatedPatcher = new IsFileCreated();
            var scriptingManagerDeconstructorPatcher = new ScriptingManagerDeconstructor();
            var convertSeparatorsToPlatformPatcher = new ConvertSeparatorsToPlatform();
            
            awakeFromLoadPatcher.Patch(patternDiscoverer, Config.MonoManagerAwakeFromLoadOffset);
            isAssemblyCreatedPatcher.Patch(patternDiscoverer, Config.MonoManagerIsAssemblyCreatedOffset);
            if (!IsAssemblyCreated.IsApplied)
            {
                isFileCreatedPatcher.Patch(patternDiscoverer, Config.IsFileCreatedOffset);
            }
            convertSeparatorsToPlatformPatcher.Patch(patternDiscoverer, Config.ConvertSeparatorsToPlatformOffset);
            scriptingManagerDeconstructorPatcher.Patch(patternDiscoverer, Config.ScriptingManagerDeconstructorOffset);
        }
    }
}
