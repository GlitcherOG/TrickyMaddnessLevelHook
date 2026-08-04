using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection.Emit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TrickyMaddnessLevelHook
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Harmony harmony;
        public static MenuManager menuManager;

        //Static handle on the BepInEx logger so the patch classes can log too
        public static ManualLogSource Log;

        public static AssetBundle myLoadedAssetBundle;
        public static string LoadedAssetBundle = "";
        public static string LoadedTrack;
        public static string LoadedTrackName;

        public static List<string> TrackNames = new List<string>();
        public static List<string> Paths = new List<string>();

        //When the shader remap runs. See ShaderRemapEnabled() for the reasoning
        //behind Auto.
        public enum ShaderRemapMode { Auto, Always, Never }
        public static ConfigEntry<ShaderRemapMode> ShaderRemap;

        //The game's own shaders, snapshotted before any bundle is loaded so
        //nothing in here can be bundle-origin. name -> shader, the set of their
        //instance IDs (what tells a game shader from a bundle one), and per
        //shader the union of keywords seen on the game's own materials.
        public static Dictionary<string, Shader> GameShaders;
        public static HashSet<int> GameShaderIds;
        public static Dictionary<int, HashSet<string>> GameShaderKeywords;
        internal static ConfigEntry<bool> levelSelectScroll;
        internal static ConfigEntry<bool> fixRailProjection;
        internal static ConfigEntry<bool> mapGravity;

        public static string MapsDir;

        // ── Per-map sidecar ──────────────────────────────────────────────────
        // A map may ship an optional plain-text file next to its .asset —
        // "MyMap.asset" -> "MyMap.hook.txt" — whose lines are "<key> <value>"
        // settings describing the map to the Hook. Nothing is required: a map
        // without a sidecar, or with a sidecar that declares none of these keys,
        // behaves exactly as it does today. Unrecognised lines are ignored, so
        // the file can carry other per-map data too.
        //
        // Recognised here:
        //   platform <windows|mac|linux>  which platform the bundle's shaders
        //                                 were compiled for. See ShaderRemapEnabled().
        //   source <slug>                 where the level came from, logged only.
        //   mapversion <v>                which build of the level this is, logged only.
        //   gravity <multiplier>          scales the rider's gravity while this map
        //                                 is loaded. See ScaledGravity().
        static readonly HashSet<string> SidecarKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "platform", "source", "mapversion", "gravity" };

        // Sidecar names tried in order. ".hook.txt" is the name to use. The
        // ".aipaths.txt" fallback is not decoration: a family of converted maps
        // already ships that file with exactly this header block in it, so
        // reading it costs one array entry and means those maps declare their
        // platform to this Hook instead of guessing wrong on somebody's Windows box.
        static readonly string[] SidecarExtensions = { ".hook.txt", ".aipaths.txt" };

        //Hoisted so the line-splitter isn't reallocated per line of a large sidecar.
        static readonly char[] KeySeparators = { ' ', '\t' };

        // The only platform slugs that mean anything. A value outside this set is
        // NOT a declaration — see ReadSidecar. That matters more than it looks:
        // treating an unknown slug as "declared, and not native" would turn a typo
        // (or a slug a future Hook understands and this one doesn't) into "remap a
        // Windows-native bundle on Windows", which is strictly worse than the guess
        // this feature replaces.
        static readonly HashSet<string> KnownPlatforms =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "windows", "mac", "linux" };

        // Platform the active custom map declared its shaders were built for, or
        // null for every map that declares nothing — which is every map that
        // exists today, and is why this is purely additive.
        public static string BundleNativePlatform;

        // Provenance and version of the active custom map. Neither drives any
        // behaviour; they exist so a pasted log answers "which build of which
        // level was this?" without having to ask.
        public static string BundleSource;
        public static string BundleMapVersion;

        // Gravity multiplier the active custom map declared, or 1 for every map
        // that declares nothing — which is every map that exists today. This is
        // hard-reset to 1 on built-in level selection and on every abort path out
        // of the load prefix, which is what keeps built-in levels provably stock:
        // the transpilers below are always installed, but at 1 they hand back the
        // literal they were given.
        public static float MapGravityMultiplier = 1f;

        private void Awake()
        {
            // Plugin startup logic
            Log = Logger;
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            ShaderRemap = Config.Bind("Rendering", "ShaderRemap", ShaderRemapMode.Auto,
                "Rebind a custom map's materials to the game's own shaders when the map's shaders were compiled for another platform (they render magenta otherwise). "
                + "Auto: only when the game is NOT running on Windows, since maps are overwhelmingly Windows-built. "
                + "Always: also on Windows - use this if a Mac/Linux-built map renders magenta. "
                + "Never: disable entirely.");
            //"by default" because in Auto a map that declares its own platform
            //overrides this per map — so this line is no longer the whole story,
            //and it's the line people paste into bug reports.
            Logger.LogInfo($"Shader remap: mode {ShaderRemap.Value} on {Application.platform} -> {(ShaderRemapEnabled() ? "active" : "inactive")} by default");
            levelSelectScroll = Config.Bind("UI", "LevelSelectScroll", true,
                "Scroll the level-select card strip when more cards exist than fit " +
                "on screen, auto-scrolling to the selected card. When all cards fit, " +
                "the menu is left pixel-identical to vanilla. Set false to disable.");
            fixRailProjection = Config.Bind("Gameplay", "FixRailProjection", true,
                "Bug fix in the GAME's own Rail.GetClosestPosition: it builds the "
                + "closest point on a rail segment as 'start + segmentVector * t' "
                + "where t is a distance in metres, so the projection is only correct "
                + "on segments exactly 1 m long - and no rail is, stock levels "
                + "included. The rider can be snapped metres off the rail and, at the "
                + "tail, the method reports 'still on the rail' when they have run off "
                + "it, so the game re-attaches them behind where they were (the "
                + "rail-end snap-back loop; CPU riders never escape it). "
                + "On by default. Set false to restore the stock behaviour - this is "
                + "the one thing this Hook changes on built-in levels too.");
            mapGravity = Config.Bind("Gameplay", "MapGravity", true,
                "Honour the 'gravity' line in a custom map's sidecar, which lets a "
                + "map ride heavier or floatier than stock. Only maps that ask for "
                + "it are affected: built-in levels, and every map that declares no "
                + "gravity, ride at stock gravity either way. Set false to ignore "
                + "the setting entirely and leave the game's gravity code "
                + "completely untouched.");
            MapsDir = ResolveMapsDir();
            Logger.LogInfo($"Maps directory resolved to: {MapsDir}");
            if (!Directory.Exists(MapsDir))
            {
                Directory.CreateDirectory(MapsDir);
            }
            SceneManager.sceneLoaded += OnSceneLoaded;
            AddTracks();
            DoPatching();
        }

        // GameRootPath points at the executable's folder. On macOS that is
        // TrickyMadness.app/Contents/MacOS; walk up to the folder that holds the
        // .app (next to run_bepinex.sh) so Maps sits where it does on Windows.
        private static string ResolveMapsDir()
        {
            string root = BepInEx.Paths.GameRootPath;
            int idx = root.IndexOf(".app" + Path.DirectorySeparatorChar + "Contents",
                StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                idx = root.IndexOf(".app/Contents", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int slash = root.LastIndexOfAny(new[] { '/', '\\' }, idx);
                if (slash > 0) root = root.Substring(0, slash);
            }
            return Path.Combine(root, "Maps");
        }

        public void AddTracks()
        {
            var List = Directory.GetFiles(MapsDir, "*.asset");
            for (int i = 0; i < List.Length; i++)
            {
                Paths.Add(List[i]);
                TrackNames.Add(Path.GetFileNameWithoutExtension(List[i]));
            }
        }

        public static void DoPatching()
        {
            harmony = new Harmony("com.glitcherog.patch");
            //Level appears
            var mOriginal = AccessTools.Method(typeof(MenuManager), "RegisterLevels"); // if possible use nameof() here
            var mPrefix = SymbolExtensions.GetMethodInfo(() => RegisterLevels());

            harmony.Patch(mOriginal, null, new HarmonyMethod(mPrefix));

            mOriginal = AccessTools.Method(typeof(MakerLevelData), "Awake"); // if possible use nameof() here
            mPrefix = SymbolExtensions.GetMethodInfo(() => LoadLevelManager());
            harmony.Patch(mOriginal, null, new HarmonyMethod(mPrefix));

            harmony.PatchAll();
        }

        public static void RegisterLevels()
        {
            menuManager = MenuManager.Instance;

            for (int i = 0; i < Paths.Count; i++)
            {
                LevelEntries.RegisterLevel(new LevelEntry
                {
                    name = TrackNames[i],
                    sceneName = "$" + Paths[i],
                    scoreThresholds = new float[]
                    {
                    100000f,
                    150000f,
                    200000f
                    },
                    timeThresholds = new float[]
                    {
                    110f,
                    100f,
                    95f
                    },
                    freestyleTimeLimit = 150f
                });
            }
        }

        public static void LoadLevelManager()
        {
            //Find Main Starting Objects
            var levelManagerMaker = FindAnyObjectByType<MakerLevelData>();
            var MakerSpawnPoint = FindAnyObjectByType<MakerSpawnPoint>();
            var MakerRespawnPoint = FindAnyObjectByType<MakerRespawnPoints>();
            //Find AI Paths Object

            //Update Level Entry
            var LevelEntry = menuManager.lastLevelEntry;

            LevelEntry.timeThresholds[2] = levelManagerMaker.GoldTime;
            LevelEntry.timeThresholds[1] = levelManagerMaker.SilverTime;
            LevelEntry.timeThresholds[0] = levelManagerMaker.BronzeTime;

            LevelEntry.scoreThresholds[2] = levelManagerMaker.GoldPoints;
            LevelEntry.scoreThresholds[1] = levelManagerMaker.SilverPoints;
            LevelEntry.scoreThresholds[0] = levelManagerMaker.BronzePoints;

            LevelEntry.freestyleTimeLimit = levelManagerMaker.TimeLeft;

            Traverse.Create(menuManager).Field("lastLevelEntry").SetValue(LevelEntry);

            //Insert Starting Gate
            var StartingGate = Resources.Load<GameObject>("game/startinggate");
            GameObject StartingGateObject = Instantiate(StartingGate);
            StartingGateObject.transform.position = MakerSpawnPoint.transform.TransformPoint(new Vector3(-0.5f,0,0.5f));
            StartingGateObject.transform.rotation = MakerSpawnPoint.transform.rotation;

            //Insert Level Manager
            var LevelManagerObject = Resources.Load<GameObject>("game/levelmanager");
            var gameObject = Instantiate(LevelManagerObject, levelManagerMaker.transform);

            var levelManager = gameObject.GetComponent<LevelManager>();

            //Set Level Manager Data Points
            Traverse.Create(levelManager).Field("startingGate").SetValue(MakerSpawnPoint.transform);
            Traverse.Create(levelManager).Field("respawnPointsParent").SetValue(MakerRespawnPoint.transform);
            Traverse.Create(levelManager).Field("snowflakes").SetValue(new GameObject("Temp")); //FIX to get all snowflakes and apply

            //Create UI Hooks
            var TempObject = Traverse.Create(levelManager).Field("speedLabel").GetValue() as TextMeshProUGUI;
            var CanvasObjects = FindObjectsByType(typeof(MakerCanvas), FindObjectsSortMode.None) as MakerCanvas[];
            for (int i = 0; i < CanvasObjects.Length; i++)
            {
                GameObject gameObject1 = Instantiate(TempObject.transform.gameObject, levelManager.transform.GetChild(0));
                gameObject1.SetActive(true);
                gameObject1.GetComponent<TextMeshProUGUI>().text = "NewText";
                CanvasObjects[i].Generate(gameObject1.GetComponent<TextMeshProUGUI>());
            }

            //Convert From Make Rails to Game Rails
            var RailObjects = FindObjectsByType(typeof(MakerRails), FindObjectsSortMode.None) as MakerRails[];
            for (int i = 0; i < RailObjects.Length; i++)
            {
                var NewRail = RailObjects[i].gameObject.AddComponent<RailInvis>();

                var Segments = RailObjects[i].GetSegments();
                List<Rail.Segment> NewSegments = new List<Rail.Segment>();

                for (int j = 0; j < Segments.Count; j++)
                {
                    Rail.Segment NewSegment = new Rail.Segment(Vector3.zero, Vector3.up);

                    NewSegment.position = Segments[j].Point;
                    NewSegment.rotation = Segments[j].Rotation;
                    NewSegment.distance = Segments[j].Distance;

                    NewSegments.Add(NewSegment);
                }

                Enum.TryParse(RailObjects[i].Materials.ToString(), out Rail.RailMaterial material1);

                NewRail.material = material1;

                Traverse.Create(NewRail).Field("segments").SetValue(NewSegments);
            }

            GameObject AIParent = new GameObject("AI Paths Parent");
            AIParent.transform.parent = levelManager.transform;

            // MakerAIPath.m_points is a List of a user-defined [Serializable]
            // nested class, and Unity fails to deserialize those for
            // runtime-loaded (BepInEx) assemblies — on custom maps the list
            // frequently comes back empty (engine limitation; lists of engine
            // value types, like the race line's List<Vector3> below, are
            // unaffected). The AI enumerates DIRECT children of pathsParent, so
            // every AIPath must sit immediately under AIParent.
            var MakerAIPaths = FindObjectsByType(typeof(MakerAIPath), FindObjectsSortMode.None) as MakerAIPath[];
            for (int i = 0; i < MakerAIPaths.Length; i++)
            {
                // An empty maker path would become an empty AIPath, which is what
                // sends AI into the 1.5s respawn loop — skip it entirely.
                if (MakerAIPaths[i].m_points == null || MakerAIPaths[i].m_points.Count == 0)
                    continue;

                //For all path object
                var NewPath = MakerAIPaths[i].gameObject.AddComponent<AIPath>();

                MakerAIPaths[i].transform.parent = AIParent.transform;

                NewPath.m_points = new List<AIPath.Point>();

                for (int j = 0; j < MakerAIPaths[i].m_points.Count; j++)
                {
                    var TempPoint = new AIPath.Point();
                    TempPoint.position = MakerAIPaths[i].m_points[j].position;
                    TempPoint.windup = MakerAIPaths[i].m_points[j].windup;
                    NewPath.m_points.Add(TempPoint);
                }

                NewPath.CalculateSegments();
            }
            Traverse.Create(levelManager).Field("pathsParent").SetValue(AIParent.transform);


            GameObject AIPDividerParent = new GameObject("AI Paths Divider Parent");
            AIPDividerParent.transform.parent = levelManager.transform;
            var MakerAIPathsDivider = FindObjectsByType(typeof(MakerAIPathDivider), FindObjectsSortMode.None) as MakerAIPathDivider[];
            for (int i = 0; i < MakerAIPathsDivider.Length; i++)
            {
                var NewPath = MakerAIPathsDivider[i].gameObject.AddComponent<AIPathDivider>();

                // The game enumerates DIRECT children of dividersParent — parent
                // dividers under the transform actually registered as
                // dividersParent (they used to go under AIParent and were never
                // found).
                MakerAIPathsDivider[i].transform.parent = AIPDividerParent.transform;

                NewPath.m_points = MakerAIPathsDivider[i].m_points;
            }
            Traverse.Create(levelManager).Field("dividersParent").SetValue(AIPDividerParent.transform);

            var TempMakerRaceLine = FindAnyObjectByType<MakerRaceLine>();
            var TempRaceLine = TempMakerRaceLine.gameObject.AddComponent<RaceLine>();

            TempRaceLine.m_points = new List<RaceLine.Point>();

            for (int i = 0;i < TempMakerRaceLine.m_points.Count;i++)
            {
                var RaceLinePoint = new RaceLine.Point();

                RaceLinePoint.position = TempMakerRaceLine.m_points[i];

                TempRaceLine.m_points.Add(RaceLinePoint);
            }

            TempRaceLine.CalculateSegments();

            Traverse.Create(levelManager).Field("raceLine").SetValue(TempRaceLine);
        }

        // ---------------------------------------------------------------------
        // Cross-platform shader remap
        //
        // An AssetBundle carries shader BYTECODE for the platform it was built
        // on. A Windows-built map on the macOS/Linux build of the game (Metal /
        // Vulkan) has no usable bytecode for any of its materials and the whole
        // map renders magenta. The game itself ships platform-correct shaders
        // with the SAME names (Universal Render Pipeline/Lit etc.), so the fix is
        // to rebind the map's materials onto the game's copies.
        //
        // Deciding WHEN to distrust a bundle's own shaders is the hard part:
        //  * Shader.isSupported is not usable — it reports true for foreign
        //    platform bytecode, which is exactly the case we need to catch.
        //  * There is no managed API that reports which platform a loaded
        //    AssetBundle was built for (the target is buried in the bundle's
        //    compressed serialized-file header; parsing that ourselves is a lot
        //    of fragile format code for one bit of information).
        //  * Asking map authors to declare it (a sidecar or a marker asset) only
        //    helps maps built after such a convention exists — every map already
        //    published would still need a guess.
        // So we guess from the running platform, which costs the user nothing:
        // custom maps are overwhelmingly built on Windows, so a map running on
        // Windows is native (leave it completely alone — remapping is lossy: it
        // resets keywords and can drop shader features the map actually shipped),
        // while a map running on macOS or Linux is almost certainly foreign and
        // gets remapped. The minority case a guess cannot cover — a Mac-built map
        // on Windows — is what the ShaderRemap config entry is for.
        public static bool ShaderRemapEnabled()
        {
            if (ShaderRemap == null) return false;
            if (ShaderRemap.Value == ShaderRemapMode.Never) return false;
            if (ShaderRemap.Value == ShaderRemapMode.Always) return true;
            // Auto. If the map itself declared which platform its shaders were
            // compiled for, believe it over the guess below — it is the one
            // authority that actually knows. That is what lets a Mac- or
            // Linux-built map be remapped ON WINDOWS (the case the guess cannot
            // cover and the config entry exists to work around), and equally
            // lets a genuinely Mac-native map be left alone on a Mac.
            // Gate on the slug being one we understand, not merely on it being
            // present, so an unknown or future slug falls through to the guess
            // below — the same answer this Hook gives today.
            if (BundleNativePlatform != null && KnownPlatforms.Contains(BundleNativePlatform))
                return !DeclaredPlatformIsNative(BundleNativePlatform);
            //Auto: everything except Windows, whose bundles are already native.
            var platform = Application.platform;
            return platform != RuntimePlatform.WindowsPlayer
                && platform != RuntimePlatform.WindowsEditor;
        }

        // True when a bundle declaring `declared` was built for the platform this
        // process is running on — i.e. its compiled shader bytecode is native and
        // must not be touched, because remapping is lossy (it resets keywords and
        // can drop shader features the map actually shipped). Only ever called
        // with a slug from KnownPlatforms; it does its own case-insensitive
        // comparison rather than trusting the caller to have normalised, since
        // BundleNativePlatform is public and anything could assign it.
        static bool DeclaredPlatformIsNative(string declared)
        {
            var p = Application.platform;
            if (string.Equals(declared, "windows", StringComparison.OrdinalIgnoreCase))
                return p == RuntimePlatform.WindowsPlayer || p == RuntimePlatform.WindowsEditor;
            if (string.Equals(declared, "mac", StringComparison.OrdinalIgnoreCase))
                return p == RuntimePlatform.OSXPlayer || p == RuntimePlatform.OSXEditor;
            if (string.Equals(declared, "linux", StringComparison.OrdinalIgnoreCase))
                return p == RuntimePlatform.LinuxPlayer || p == RuntimePlatform.LinuxEditor;
            //Unreachable via ReadSidecar, and "no declaration" is the safe answer.
            return false;
        }

        // Reads a map's sidecar settings, keyed by the map's .asset path. Returns
        // an empty set for a map with no sidecar, and never throws: an unreadable
        // or malformed sidecar has to degrade to "map declares nothing", because
        // the alternative is a level that used to load refusing to.
        public static Dictionary<string, string> ReadSidecar(string bundlePath)
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(bundlePath)) return settings;

            string sidecar = null;
            foreach (var ext in SidecarExtensions)
            {
                string candidate = Path.ChangeExtension(bundlePath, ext);
                if (File.Exists(candidate)) { sidecar = candidate; break; }
            }
            if (sidecar == null) return settings;

            try
            {
                //Streamed rather than ReadAllLines: a sidecar carrying other
                //per-map data can run to tens of thousands of lines, and the
                //settings are a handful of them.
                using (var reader = new StreamReader(sidecar))
                {
                    string raw;
                    while ((raw = reader.ReadLine()) != null)
                    {
                        string line = raw.Trim();
                        //'#' comments out a WHOLE line only. It is deliberately not
                        //honoured mid-line, so a value is never silently truncated.
                        if (line.Length == 0 || line[0] == '#') continue;
                        int sp = line.IndexOfAny(KeySeparators);
                        if (sp <= 0) continue;
                        string key = line.Substring(0, sp);
                        if (!SidecarKeys.Contains(key)) continue;
                        string value = line.Substring(sp + 1).Trim();
                        if (value.Length == 0) continue;
                        // A platform we don't understand is not a declaration. Say
                        // so — a silent fallback here reads to the author as "the
                        // Hook ignored my file", which is the bug report nobody
                        // can act on.
                        if (string.Equals(key, "platform", StringComparison.OrdinalIgnoreCase)
                            && !KnownPlatforms.Contains(value))
                        {
                            Log.LogWarning($"[Sidecar] {Path.GetFileName(sidecar)}: "
                                + $"'platform {value}' is not one of windows/mac/linux — ignoring it. "
                                + "Shader remap falls back to guessing from the running platform.");
                            continue;
                        }
                        //Last one wins, so a hand-appended line overrides a generated one.
                        settings[key] = value;
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[Sidecar] could not read {Path.GetFileName(sidecar)}: {e.Message}");
            }
            return settings;
        }

        // Called from the LoadScene prefix immediately BEFORE the bundle is
        // loaded, so everything captured here is guaranteed game-origin. The
        // game's shaders never change, so this only has to run once a session.
        public static void SnapshotGameShaders()
        {
            GameShaders = new Dictionary<string, Shader>();
            GameShaderIds = new HashSet<int>();
            foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (sh == null) continue;
                GameShaderIds.Add(sh.GetInstanceID());
                if (!GameShaders.ContainsKey(sh.name)) GameShaders[sh.name] = sh;
            }
            GameShaderKeywords = new Dictionary<int, HashSet<string>>();
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null) continue;
                int id = m.shader.GetInstanceID();
                if (!GameShaderIds.Contains(id)) continue;
                HashSet<string> set;
                if (!GameShaderKeywords.TryGetValue(id, out set))
                {
                    set = new HashSet<string>();
                    GameShaderKeywords[id] = set;
                }
                foreach (var k in m.shaderKeywords) set.Add(k);
            }
            Log.LogInfo($"[ShaderRemap] snapshotted {GameShaderIds.Count} game shader(s), keyword unions for {GameShaderKeywords.Count}");
        }

        // Scene name of the active custom map, derived from LoadedTrack the same
        // way the LoadScene prefix does (never Path.* — on macOS '\' is an
        // ordinary filename character). Null when no custom bundle is active.
        public static string CustomSceneName()
        {
            if (string.IsNullOrEmpty(LoadedAssetBundle) || string.IsNullOrEmpty(LoadedTrack))
                return null;
            var parts = LoadedTrack.Split('\\', '/');
            return parts[parts.Length - 1].Split('.')[0];
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ShaderRemapEnabled()) return;

            // Only ever touch the custom bundle's OWN scene. Gating on "a custom
            // bundle is loaded" is not enough: returning to the menu does not go
            // through MenuManager.LoadScene(LevelEntry), so that state is still
            // set and we would rebind MENU materials while the bundle's
            // duplicate-named shaders are still resident — shader lookup by name
            // is unspecified with duplicates, so that can leave a pink menu until
            // the game is restarted.
            string customScene = CustomSceneName();
            if (customScene == null || scene.name != customScene) return;

            try
            {
                int scanned = 0, remapped = 0, litFallbacks = 0, pruned = 0;
                var seen = new HashSet<Material>();
                var missing = new HashSet<string>();

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var mat in r.sharedMaterials)
                        {
                            if (mat == null || !seen.Add(mat)) continue;
                            scanned++;
                            FixMaterial(mat, missing, ref remapped, ref litFallbacks, ref pruned);
                        }
                    }
                }

                string miss = missing.Count == 0 ? "none" : string.Join(", ", missing);
                Log.LogInfo($"[ShaderRemap] scene '{scene.name}': scanned {scanned} material(s), remapped {remapped}, lit-fallbacks {litFallbacks}, keyword-pruned {pruned}, unfixable: {miss}");
            }
            catch (Exception e)
            {
                //A throw in a sceneLoaded handler would break entering the level;
                //a map that renders wrong is still better than one you can't play.
                Log.LogError($"[ShaderRemap] failed on scene '{scene.name}': {e}");
            }
        }

        private static void FixMaterial(Material mat, HashSet<string> missing,
            ref int remapped, ref int litFallbacks, ref int pruned)
        {
            var sh = mat.shader;
            if (sh == null) return;
            //Shipped with the game, so it runs here by definition.
            if (GameShaderIds != null && GameShaderIds.Contains(sh.GetInstanceID())) return;

            // A bundle built with a broken shader bakes in Unity's error shader,
            // whose name the game ALSO has — name-matching it would rebind
            // magenta to magenta. Send it to the Lit fallback instead.
            bool bakedError = sh.name.Contains("FallbackError");

            // Assigning Material.shader RESETS renderQueue to the new shader's
            // default (documented Unity behavior). Maps rely on custom queues for
            // their transparent/cutout materials; losing them drops a ZWrite-off
            // blend material into the opaque pass where later draws overwrite it,
            // and fences/foliage go invisible. Restore it at BOTH assignments.
            int queue = mat.renderQueue;

            Shader replacement;
            if (!bakedError && GameShaders != null && GameShaders.TryGetValue(sh.name, out replacement))
            {
                mat.shader = replacement;
                mat.renderQueue = queue;
                remapped++;
                if (PruneKeywords(mat, replacement)) pruned++;
                return;
            }

            // No same-named shader in the game build: substitute URP Lit. Property
            // values (_BaseMap/_BaseColor/_BumpMap and tiling) live on the material
            // and survive the shader swap, so this is plain but not pink.
            if (GameShaders != null && GameShaders.TryGetValue("Universal Render Pipeline/Lit", out replacement))
            {
                mat.shader = replacement;
                mat.renderQueue = queue;
                //Down to the base variant, which every URP build has, plus the
                //safe-listed feature keywords — clearing those un-cuts cutout
                //foliage.
                var keep = new List<string>();
                foreach (var k in mat.shaderKeywords)
                    if (SafeKeywords.Contains(k)) keep.Add(k);
                mat.shaderKeywords = keep.ToArray();
                litFallbacks++;
            }
            else
            {
                missing.Add(sh.name);
            }
        }

        // Feature keywords the game build is KNOWN to compile even though the
        // snapshot cannot see them: the game materials that use them live in the
        // built-in LEVEL scenes (trees carry _ALPHATEST_ON, glass carries
        // _SURFACE_TYPE_TRANSPARENT), and the snapshot is taken from the menu,
        // where no level scene is loaded. Without this allow-list the prune below
        // strips alpha clip from every cutout material in the map and foliage
        // renders as solid blocks.
        private static readonly HashSet<string> SafeKeywords = new HashSet<string>
        {
            "_ALPHATEST_ON", "_SURFACE_TYPE_TRANSPARENT",
        };

        // Drop material keywords never observed on any game material using the
        // same shader: a build only compiles the shader variants its own content
        // uses, so a keyword combination from outside that set can select a
        // variant that was stripped (magenta at draw time). Losing a keyword
        // costs one feature; keeping a stripped combination costs the whole
        // material. Returns true if anything was removed.
        private static bool PruneKeywords(Material mat, Shader gameShader)
        {
            HashSet<string> union = null;
            if (GameShaderKeywords != null)
                GameShaderKeywords.TryGetValue(gameShader.GetInstanceID(), out union);

            var kws = mat.shaderKeywords;
            if (kws == null || kws.Length == 0) return false;
            var kept = new List<string>();
            bool removed = false;
            foreach (var k in kws)
            {
                if (SafeKeywords.Contains(k) || (union != null && union.Contains(k))) kept.Add(k);
                else removed = true;
            }
            if (!removed) return false;
            mat.shaderKeywords = kept.ToArray();
            return true;
        }

        // The corrected multiply for Rail.GetClosestPosition's closest-point
        // formula: the game scales the raw segment vector by a distance in
        // metres, where only the unit direction may be scaled. See
        // Rail_GetClosestPosition_Projection for the full story.
        internal static Vector3 RailProject(Vector3 segmentDelta, float distanceAlong)
        {
            return segmentDelta.normalized * distanceAlong;
        }

        // Every patched gravity literal is routed through here at runtime. At the
        // default multiplier of 1 this returns its argument unchanged, so the
        // patched methods compute bit-identical results to stock — which is what
        // built-in levels and every map that declares no gravity always see.
        public static float ScaledGravity(float baseGravity)
        {
            return baseGravity * MapGravityMultiplier;
        }

        // Routes the gravity literals of one method through ScaledGravity.
        //
        // Snowboarder.Gravity and SoftGravity are compile-time consts, so there is
        // no field to write - the values are baked into each use site as ldc.r4
        // and can only be caught as literals. Each call site therefore passes only
        // the values that ARE gravity in that particular method, plus how many it
        // expects to find: a method can legitimately contain the same number in an
        // unrelated role (a windup multiplier, say), and blindly scaling those
        // would break handling in ways nobody would connect back to a gravity
        // setting.
        //
        // Fail-to-stock: if the match count is not exactly `expected`, the method
        // is returned untouched and the reason is logged. A game update that
        // changes these methods then costs the feature, never correctness.
        internal static IEnumerable<CodeInstruction> ScaleGravityLiterals(
            IEnumerable<CodeInstruction> instructions, string label, float[] values, int expected)
        {
            //Materialized rather than a lazy iterator: the count has to be known
            //before deciding whether to emit anything at all.
            var code = new List<CodeInstruction>(instructions);
            if (mapGravity != null && !mapGravity.Value)
            {
                Log.LogInfo($"[Gravity] MapGravity off - leaving {label} stock");
                return code;
            }

            var hits = new List<int>();
            for (int i = 0; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Ldc_R4 && code[i].operand is float f
                    && Array.IndexOf(values, f) >= 0)
                    hits.Add(i);

            if (hits.Count != expected)
            {
                Log.LogWarning($"[Gravity] {label}: expected {expected} gravity literal(s), "
                    + $"found {hits.Count} - game updated? Leaving the method stock, so "
                    + "maps that declare a gravity multiplier will ride at stock gravity.");
                return code;
            }

            var scaled = AccessTools.Method(typeof(Plugin), nameof(ScaledGravity));
            //Walk backwards so each insertion can't shift the indices still to come.
            for (int i = hits.Count - 1; i >= 0; i--)
                code.Insert(hits[i] + 1, new CodeInstruction(OpCodes.Call, scaled));

            Log.LogDebug($"[Gravity] {label}: routed {hits.Count} gravity literal(s) through ScaledGravity");
            return code;
        }
    }


    // Rail.GetClosestPosition - fix the closest-point projection.
    //
    // IL-verified against Assembly-CSharp. Per segment i the game computes
    //     d   = seg[i+1].position - seg[i].position      (NOT normalized)
    //     dir = d.normalized
    //     len = d.magnitude
    //     t   = clamp(Dot(rider - seg[i].position, dir), 0, len)   // metres
    // and then takes the closest point on that segment as
    //     seg[i].position + d * t                        // <-- wrong term
    // where it must be dir * t. The two agree only when |d| == 1 m. Nothing
    // resamples rails to unit length - not the game's own authoring tool
    // (MakerRails.GenerateSegments emits one Segment per PointList child, no
    // resampling), and measured spacing is 1.8-7.0 m median across every course
    // we have looked at, so this is wrong on every rail in the game, built-in
    // levels included. The rotation and distance lerps further down the same
    // method both use the correct normalized parameter t/len; the position line
    // is the lone outlier, which is what makes this read as a typo rather than
    // as intent.
    //
    // Consequence: each segment's candidate point overshoots by a factor of
    // |d|, so a segment the rider is nowhere near can win the nearest-segment
    // contest. Measured on one 13-point rail (4.9-5.9 m spans, 65.1 m long) by
    // walking the rider exactly along the true polyline: the shipped formula
    // snaps up to 3.55 m off the rail line, and 5% past the tail it returns
    // end = 0 where the corrected one returns end = 1. `end` and `distance` are
    // only committed for the winning segment, so picking the wrong segment
    // corrupts both. Snowboarder gates re-attachment on
    // `if (end == railDirection) return;` - with end stuck at 0 the game never
    // learns the rider ran off the end and re-attaches them behind where they
    // were. That is the rail-end snap-back loop; jumping escapes it because
    // jumping is a separate exit path, which is why CPU riders (who don't jump
    // out) stay stuck.
    //
    // Surgical fix: the method contains exactly ONE Vector3 * float multiply and
    // it is that term, so reroute that one call through Plugin.RailProject
    // (which normalizes first). No local-variable rewriting, nothing else
    // touched. Bails out unchanged and loudly if the match count isn't exactly
    // 1, so a future game update degrades to stock behaviour rather than to
    // corrupted rails.
    [HarmonyPatch(typeof(Rail), "GetClosestPosition")]
    public class Rail_GetClosestPosition_Projection
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            if (!Plugin.fixRailProjection.Value)
            {
                Plugin.Log.LogInfo("[Rail] FixRailProjection off - leaving Rail.GetClosestPosition stock");
                return code;
            }

            var mul = AccessTools.Method(typeof(Vector3), "op_Multiply",
                new[] { typeof(Vector3), typeof(float) });
            var fix = AccessTools.Method(typeof(Plugin), nameof(Plugin.RailProject));

            var hits = new List<int>();
            for (int i = 0; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Call && Equals(code[i].operand, mul))
                    hits.Add(i);

            if (hits.Count != 1)
            {
                Plugin.Log.LogWarning($"[Rail] GetClosestPosition: expected exactly 1 Vector3*float multiply, found {hits.Count} - game updated? Leaving the method stock.");
                return code;
            }

            code[hits[0]].operand = fix;
            Plugin.Log.LogInfo("[Rail] GetClosestPosition: closest-point projection corrected (scale the unit direction, not the raw segment vector)");
            return code;
        }
    }

    // SnowTrail (the board's ground-snapped ribbon decal) ground-snaps via a
    // raycast that only hits Unity layer "Default" (IL-verified: SnowTrail.Start
    // sets its static layerMask to just 1 << LayerMask.NameToLayer("Default")).
    // Rideable ground can legitimately be Default(0)/Ice(6)/Rock(7)/Metal(14) -
    // those are the four layers Snowboarder itself treats as ground - so on any
    // custom terrain typed onto Ice/Rock/Metal the raycast misses every frame
    // and the trail never renders. Widen the mask once, right after Start() sets
    // it, to exactly those four layers.
    [HarmonyPatch(typeof(SnowTrail), "Start")]
    public class SnowTrail_Start_LayerMask
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            int rideableMask = (1 << 0) | (1 << 6) | (1 << 7) | (1 << 14);
            Traverse.Create(typeof(SnowTrail)).Field("layerMask").SetValue(rideableMask);
        }
    }


    // Per-map gravity. Snowboarder.Gravity (20) and SoftGravity (17) are consts,
    // so they are inlined at their use sites and there is no field to write - see
    // Plugin.ScaleGravityLiterals. Exactly three methods carry them, IL-verified
    // against the shipped Assembly-CSharp:
    //
    //   Snowboarder.Integrate      1x 20 (base) + 1x 17 (while rising), out of 36
    //                              ldc.r4 in the method
    //   Snowboarder.UpdateGrinding 1x 17, the pull holding the rider onto a rail.
    //                              Its single 20 is a windup-spring stiffness and
    //                              is deliberately NOT in this method's value list
    //   Caster.Cast                1x 20 + 1x 17, the trajectory prediction. It
    //                              mirrors Integrate exactly (fixedDeltaTime
    //                              instead of deltaTime) and MUST be scaled with
    //                              it, or predicted landings drift off the real ones
    //
    // Nothing else in the assembly inlines either value as gravity. LaunchPad's
    // boost - in Snowboarder.OnTriggerEnter and mirrored in Caster.OnEnterTrigger -
    // also inlines 20, and is left alone on purpose: it is a tag mechanic, not
    // gravity, and both live in methods this patch never touches.
    [HarmonyPatch(typeof(Snowboarder), "Integrate")]
    public class Snowboarder_Integrate_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Plugin.ScaleGravityLiterals(instructions, "Snowboarder.Integrate", new[] { 20f, 17f }, 2);
        }
    }

    [HarmonyPatch(typeof(Snowboarder), "UpdateGrinding")]
    public class Snowboarder_UpdateGrinding_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Plugin.ScaleGravityLiterals(instructions, "Snowboarder.UpdateGrinding", new[] { 17f }, 1);
        }
    }

    [HarmonyPatch(typeof(Caster), "Cast")]
    public class Caster_Cast_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Plugin.ScaleGravityLiterals(instructions, "Caster.Cast", new[] { 20f, 17f }, 2);
        }
    }

    // The level-load entry point is MenuManager.LoadScene(LevelEntry), NOT
    // SelectLevel. Retail MenuManager exposes SelectLevel(int index) — a different
    // signature — so a patch aimed at SelectLevel(LevelEntry) silently never binds
    // and every custom map loads to a black screen. Do not "fix" this back.
    [HarmonyPatch(typeof(MenuManager), "LoadScene", new System.Type[] { typeof(LevelEntry) })]
    public class MenuManager_LoadScene
    {
        // Returning false skips MenuManager.LoadScene entirely — the clean no-op
        // abort for any failure: the menu simply stays up instead of loading a
        // black screen. Every abort path logs why first. State (myLoadedAssetBundle,
        // LoadedAssetBundle, LoadedTrack) is only committed AFTER a bundle has been
        // confirmed loaded with at least one scene, so a failed load can never
        // leave a null handle behind for a later selection to NRE on.
        [HarmonyPrefix]
        public static bool Prefix(ref LevelEntry levelEntry)
        {
            //Built-in level (a null sceneName is not ours either)
            if (levelEntry.sceneName == null || !levelEntry.sceneName.Contains("$"))
            {
                //No custom bundle should stay loaded. Drive the unload off the
                //handle, not off LoadedAssetBundle — the two can be out of sync.
                Plugin.LoadedAssetBundle = "";
                Plugin.LoadedTrack = null;
                //Built-in levels declare nothing, so clearing here is what keeps
                //them provably stock: no sidecar of a previously-played custom map
                //can still be in effect while one is loading.
                Plugin.BundleNativePlatform = null;
                Plugin.BundleSource = null;
                Plugin.BundleMapVersion = null;
                Plugin.MapGravityMultiplier = 1f;
                if (Plugin.myLoadedAssetBundle != null)
                {
                    Plugin.myLoadedAssetBundle.Unload(true);
                    Plugin.myLoadedAssetBundle = null;
                }
                return true;
            }

            try
            {
                string path = levelEntry.sceneName.TrimStart('$');
                Plugin.Log.LogInfo($"LoadScene prefix: loading custom map bundle '{levelEntry.name}' from {path}");

                //Clear BEFORE anything can fail, matching how LoadedAssetBundle /
                //LoadedTrack are handled below: every abort path out of this method
                //then leaves no previous map's declarations still in effect.
                Plugin.BundleNativePlatform = null;
                Plugin.BundleSource = null;
                Plugin.BundleMapVersion = null;
                Plugin.MapGravityMultiplier = 1f;

                if (Plugin.LoadedAssetBundle == path && Plugin.myLoadedAssetBundle != null)
                {
                    //Same map re-selected and its bundle is still loaded: reuse it.
                    Plugin.Log.LogInfo($"LoadScene prefix: reusing already-loaded bundle {path}");
                }
                else
                {
                    //Switching maps: drop the old bundle and clear its state first,
                    //so an abort below leaves no stale path pointing at a dead handle.
                    if (Plugin.myLoadedAssetBundle != null)
                    {
                        Plugin.myLoadedAssetBundle.Unload(true);
                        Plugin.myLoadedAssetBundle = null;
                        Plugin.Log.LogInfo("LoadScene prefix: unloaded previous custom bundle");
                    }
                    Plugin.LoadedAssetBundle = "";
                    Plugin.LoadedTrack = null;

                    //Snapshot the game's shaders once per session, while nothing
                    //but the game is loaded — after LoadFromFile the bundle's own
                    //same-named shaders are indistinguishable from the game's.
                    //Not gated on ShaderRemapEnabled(): this instant is the only
                    //chance to take it, and it reads without changing anything, so
                    //turning the setting on later still has data to work from.
                    if (Plugin.GameShaders == null)
                        Plugin.SnapshotGameShaders();

                    //Null means the file is missing/corrupt, built for another Unity
                    //version, or already loaded elsewhere.
                    var bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null)
                    {
                        Plugin.Log.LogError($"LoadScene prefix: bundle load failed (file missing/corrupt or already loaded): {path}");
                        return false;
                    }

                    //A bundle with no scene is unusable — unload and abort.
                    string[] scenePath = bundle.GetAllScenePaths();
                    if (scenePath == null || scenePath.Length == 0)
                    {
                        Plugin.Log.LogError($"LoadScene prefix: bundle has no scene paths, unusable: {path}");
                        bundle.Unload(true);
                        return false;
                    }

                    //Commit only now that the load is known good.
                    Plugin.myLoadedAssetBundle = bundle;
                    Plugin.LoadedAssetBundle = path;
                    Plugin.LoadedTrack = scenePath[0];
                }

                //Read the sidecar OUTSIDE the load branch above, so a re-selected
                //map that reuses its already-loaded bundle still picks up an edited
                //sidecar. It has to be in effect before the scene loads, because
                //OnSceneLoaded is what consults ShaderRemapEnabled().
                var sidecar = Plugin.ReadSidecar(path);
                string declaredPlatform, declaredSource, declaredVersion;
                if (sidecar.TryGetValue("platform", out declaredPlatform))
                    Plugin.BundleNativePlatform = declaredPlatform;
                if (sidecar.TryGetValue("source", out declaredSource))
                    Plugin.BundleSource = declaredSource;
                if (sidecar.TryGetValue("mapversion", out declaredVersion))
                    Plugin.BundleMapVersion = declaredVersion;
                string declaredGravity;
                if (sidecar.TryGetValue("gravity", out declaredGravity))
                {
                    //InvariantCulture on purpose: the sidecar is authored once and
                    //shipped to everyone, so "1.6" must not become 16 for a player
                    //whose locale reads ',' as the decimal separator.
                    float g;
                    if (!float.TryParse(declaredGravity, NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out g)
                        || float.IsNaN(g) || float.IsInfinity(g) || g <= 0f)
                    {
                        //Left at 1 rather than clamped: a map asking for gravity 0
                        //or -3 has a broken sidecar, and stock gravity is the only
                        //sane thing to fall back to.
                        Plugin.Log.LogWarning($"[Gravity] '{levelEntry.name}' declares"
                            + $" gravity '{declaredGravity}', which is not a positive"
                            + " number - ignoring it and using stock gravity.");
                    }
                    else if (Plugin.mapGravity != null && !Plugin.mapGravity.Value)
                    {
                        //Left at 1 so the [Sidecar] line below reports what the
                        //rider will actually feel, not what the map asked for.
                        Plugin.Log.LogInfo($"[Gravity] '{levelEntry.name}' declares"
                            + $" gravity {g.ToString(CultureInfo.InvariantCulture)}x,"
                            + " ignored - MapGravity is off in the config.");
                    }
                    else
                    {
                        Plugin.MapGravityMultiplier = g;
                    }
                }
                if (sidecar.Count > 0)
                {
                    Plugin.Log.LogInfo($"[Sidecar] '{levelEntry.name}' declares"
                        + $" platform={Plugin.BundleNativePlatform ?? "(none)"}"
                        + $" source={Plugin.BundleSource ?? "(none)"}"
                        + $" mapversion={Plugin.BundleMapVersion ?? "(none)"}"
                        + $" gravity={Plugin.MapGravityMultiplier.ToString(CultureInfo.InvariantCulture)}x"
                        + $" -> shader remap {(Plugin.ShaderRemapEnabled() ? "active" : "inactive")}");
                }

                //Derive the bundle-internal scene name from LoadedTrack. NOT Path.*:
                //on macOS '\' is an ordinary filename character, not a separator.
                var TempSplitName = Plugin.LoadedTrack.Split('\\', '/');
                string Name = TempSplitName[TempSplitName.Length - 1];
                levelEntry.sceneName = Name.Split('.')[0];
                return true;
            }
            catch (Exception e)
            {
                //Never let a prefix throw: that leaves the menu in a broken state
                //instead of simply declining the selection.
                Plugin.Log.LogError($"LoadScene prefix: exception loading custom map: {e}");
                return false;
            }
        }
    }

    // Custom thumbnails. The game sets each level's menu thumbnail via
    // Resources.Load<Sprite>(thumbnailPath), which can only read sprites baked into
    // the game/bundle — not a loose PNG on disk — and it runs before the map bundle
    // is even loaded. So instead, after the menu is built, we override the thumbnail
    // Image for any custom ("$"-marked) level with a PNG dropped next to its .asset
    // (e.g. Maps/MyMap.png), loading the bytes ourselves via Texture2D.LoadImage.
    [HarmonyPatch(typeof(LevelSelectMenu), "Populate")]
    public class LevelSelectMenu_Populate
    {
        // Cache so we don't re-read the file / leak a texture every time the menu opens.
        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        [HarmonyPostfix]
        public static void Postfix(LevelSelectMenu __instance)
        {
            var entries = Traverse.Create(__instance).Field("levelSelectEntries")
                .GetValue() as List<LevelSelectEntry>;
            if (entries == null) return;

            int count = LevelEntries.GetLevelCount();
            for (int i = 0; i < entries.Count && i < count; i++)
            {
                var entry = LevelEntries.GetEntry(i);
                if (string.IsNullOrEmpty(entry.sceneName) || !entry.sceneName.StartsWith("$"))
                    continue; // built-in level, leave its Resources thumbnail alone

                string assetPath = entry.sceneName.TrimStart('$');
                string pngPath = Path.ChangeExtension(assetPath, ".png");

                var sprite = LoadSprite(pngPath);
                if (sprite == null) continue;

                var img = entries[i].thumbnail;
                if (img == null) continue;
                img.sprite = sprite;
                img.preserveAspect = true; // never distort whatever aspect the user provides
            }

            // Attach the scroller once (it no-ops itself when all cards fit). Put it
            // on the Scroll View GameObject — never on Content (whose children the
            // menu indexes by position) or the Scroll View's RectTransform (it has
            // an Animation component we must not disturb).
            if (Plugin.levelSelectScroll != null && !Plugin.levelSelectScroll.Value) return;
            var scrollRect = __instance.GetComponentInChildren<ScrollRect>(true);
            if (scrollRect == null)
            {
                Debug.LogWarning("[LevelHook] no ScrollRect found under LevelSelectMenu; scrolling disabled");
                return;
            }
            if (scrollRect.gameObject.GetComponent<LevelSelectScroller>() == null)
            {
                var scroller = scrollRect.gameObject.AddComponent<LevelSelectScroller>();
                scroller.Init(scrollRect);
            }
        }

        private static Sprite LoadSprite(string pngPath)
        {
            Sprite cached;
            if (cache.TryGetValue(pngPath, out cached)) return cached;
            cache[pngPath] = null; // remember misses too, so we don't retry disk every menu open
            if (!File.Exists(pngPath)) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(pngPath))) return null;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                cache[pngPath] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LevelHook] thumbnail load failed for " + pngPath + ": " + e.Message);
                return null;
            }
        }
    }

    // Makes the level-select card strip scroll when more cards exist than fit,
    // auto-scrolling to the selected card. The hook registers one card per .asset in
    // Maps/ with no limit, so a handful of custom maps pushes cards past the screen
    // edge where they are unreachable. Lives on the "Scroll View" GameObject.
    //
    // Vanilla layout keeps Content stretch-filled to the viewport with the
    // ContentSizeFitter horizontally Unconstrained, so Content never overflows and
    // the ScrollRect has nothing to scroll — the cards are simply centred and any
    // beyond the edges are clipped. When the cards don't fit we switch Content to a
    // preferred-width, left-anchored layout so it overflows and the ScrollRect can
    // move it; when they DO fit we touch nothing, leaving the menu pixel-identical
    // to vanilla.
    public class LevelSelectScroller : MonoBehaviour
    {
        private ScrollRect scrollRect;
        private RectTransform content;
        private RectTransform viewport;
        // Set in OnEnable when all cards fit: LateUpdate then does nothing and no
        // layout is changed, so the vanilla look is preserved exactly.
        private bool disabled;

        // Card sizeDelta springs between these (LevelSelectEntry.FixedUpdate).
        private const float IdleCardWidth = 640f;
        private const float SelectedCardWidth = 768f;
        // Keep-in-view inset from each viewport edge (px).
        private const float EdgeMargin = 32f;

        // Vanilla Content layout, captured the first time overflow layout is
        // applied so a later all-fits OnEnable can restore it instead of leaving
        // the mutated layout stuck under a disabled scroller.
        private bool overflowApplied;
        private ContentSizeFitter.FitMode origFit;
        private bool origHadFitter;
        private Vector2 origAnchorMin;
        private Vector2 origAnchorMax;
        private float origPivotX;
        private float origAnchoredX;

        public void Init(ScrollRect sr)
        {
            scrollRect = sr;
        }

        private void OnEnable()
        {
            // AddComponent fires OnEnable before Init runs, but we're on the
            // ScrollRect's own GameObject, so GetComponent always resolves it.
            if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
            if (scrollRect == null) { disabled = true; return; }

            content = scrollRect.content;
            viewport = scrollRect.viewport != null
                ? scrollRect.viewport
                : scrollRect.transform.GetChild(0) as RectTransform;
            if (content == null || viewport == null) { disabled = true; return; }

            // An unbuilt/zero rect would make the fit test meaningless (everything
            // "overflows" a 0-wide viewport) — fail safe into vanilla for this
            // enable; the next menu open re-evaluates against a real rect.
            if (viewport.rect.width < 1f) { disabled = true; return; }

            // Child 0 is the inactive card template; count only active cards.
            int n = 0;
            for (int i = 0; i < content.childCount; i++)
                if (content.GetChild(i).gameObject.activeSelf) n++;

            // Use the MAX (selected) width for the one card so the fit decision
            // never flickers as widths spring frame to frame.
            float needed = (n - 1) * IdleCardWidth + SelectedCardWidth;
            if (needed <= viewport.rect.width)
            {
                // Everything fits — vanilla layout. If a previous enable applied
                // overflow layout, put the original back rather than leaving the
                // mutated anchors/fitter stuck under a disabled scroller.
                if (overflowApplied) RestoreVanillaLayout();
                disabled = true;
                return;
            }
            disabled = false;

            // Overflow layout: preferred-width content, left-anchored, pinned to the
            // viewport's left edge. Idempotent — safe to re-apply every menu open.
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (!overflowApplied)
            {
                origHadFitter = fitter != null;
                origFit = origHadFitter ? fitter.horizontalFit : ContentSizeFitter.FitMode.Unconstrained;
                origAnchorMin = content.anchorMin;
                origAnchorMax = content.anchorMax;
                origPivotX = content.pivot.x;
                origAnchoredX = content.anchoredPosition.x;
                overflowApplied = true;
            }
            if (fitter != null)
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, content.pivot.y);
            content.anchoredPosition = new Vector2(0f, content.anchoredPosition.y);
        }

        private void RestoreVanillaLayout()
        {
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (origHadFitter && fitter != null) fitter.horizontalFit = origFit;
            content.anchorMin = origAnchorMin;
            content.anchorMax = origAnchorMax;
            content.pivot = new Vector2(origPivotX, content.pivot.y);
            content.anchoredPosition = new Vector2(origAnchoredX, content.anchoredPosition.y);
            overflowApplied = false;
        }

        private void LateUpdate()
        {
            if (disabled || content == null || viewport == null) return;

            var es = EventSystem.current;
            GameObject sel = es == null ? null : es.currentSelectedGameObject;
            if (sel == null || !sel.transform.IsChildOf(content)) return;

            // Walk up to the card root (the child whose parent is Content).
            Transform card = sel.transform;
            while (card != null && card.parent != content) card = card.parent;
            var cardRt = card as RectTransform;
            if (cardRt == null) return;

            // Card horizontal extent in viewport-local space. Content is a child of
            // the viewport, so shifting content.anchoredPosition.x by d shifts these
            // by d too — the delta to bring an edge in-bounds is a direct target.
            Vector3[] corners = new Vector3[4];
            cardRt.GetWorldCorners(corners);
            float left = float.MaxValue, right = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 lp = viewport.InverseTransformPoint(corners[i]);
                if (lp.x < left) left = lp.x;
                if (lp.x > right) right = lp.x;
            }

            Rect vr = viewport.rect;
            float vLeft = vr.xMin + EdgeMargin;
            float vRight = vr.xMax - EdgeMargin;

            float targetX = content.anchoredPosition.x;
            bool needMove = false;
            if (left < vLeft) { targetX += vLeft - left; needMove = true; }       // card past left edge → move right
            else if (right > vRight) { targetX += vRight - right; needMove = true; } // past right edge → move left
            if (!needMove) return; // card fully visible — don't fight a mouse drag

            // Never scroll past the ends. content.rect.width tracks the springing
            // card widths, so recompute every frame.
            float minX = Mathf.Min(0f, viewport.rect.width - content.rect.width);
            targetX = Mathf.Clamp(targetX, minX, 0f);

            float cur = content.anchoredPosition.x;
            float nx = Mathf.Lerp(cur, targetX, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
            content.anchoredPosition = new Vector2(nx, content.anchoredPosition.y);
        }
    }
}
