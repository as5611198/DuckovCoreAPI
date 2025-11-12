/*
 * =================================================================================
 * 專案「鴨嘴獸核心 API」 (Duckov Core API) v2.8.0
 *
 * 核心 API，用於掃描遊戲物品來源、屬性與配方，供其他 Mod 呼叫。
 *
 * v2.8.0 更新日誌:
 * 1. [v2.8.0 核心] 屬性白名單 (ATTRIBUTE_WHITELIST) 更新：
 * - 刪除了舊的自訂白名單。
 * - [cite_start]導入了基於遊戲內省 (introspection) 的完整屬性白名單 [cite: 1125-1135]。
 * 2. [v2.8.0 核心] 掃描邏輯調整：
 * - 移除了 `ScanForContainerStats` 函數，因其邏輯已合併。
 * - [cite_start]`ScanForStats` 和 `ScanForCustomData` 現在 100% 依賴新的白名單運作 [cite: 1417-1419]。
 *
 * v2.7.0 修正 (保留):
 * 1. 重寫 `ScanAndStoreItemStats` 以提高效能與準確性。
 * =================================================================================
 */

using HarmonyLib;
using Duckov.Modding;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using ItemStatsSystem.Data;
using Duckov.Utilities; // 為了 CustomDataCollection
using UnityEngine;
using System;
using System.Diagnostics;
// 為了 StackTrace
using System.IO;
using System.Reflection; // 為了 StackTrace, AccessTools, BindingFlags
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Text.RegularExpressions; // 為了 Regex
using System.Threading.Tasks;
using Duckov.Economy; // 為了 Cost/Price
using Duckov.UI;
// [v2.5.0] 修正：為了 StatCollection, Stat
using ItemStatsSystem.Stats;
namespace DuckovCoreAPI
{
    // ==================================================
    // [v2.2.0] API 公開資料結構
    // ==================================================

    /// <summary>
    /// [v2.2.0] 儲存單一屬性 (Stat) 的資料
    /// </summary>
    public struct StatEntry
    {
        /// <summary>
        /// 屬性的內部 Key (e.g., "WalkSpeed", "NewArmorPiercingGain")
        /// </summary>
        public string Key;
        /// <summary>
        /// 屬性的「本地化 Key」 (e.g., "Stats.WalkSpeed", "Stats.NewArmorPiercingGain")
        /// </summary>
        public string DisplayNameKey;
        /// <summary>
        /// 屬性的基礎值 (如果是字串屬性，此值為 0)
        /// </summary>
        public float BaseValue;
        /// <summary>
        /// 屬性的最終值 (如果是字串屬性，此值為 0)
        /// </summary>
        public float Value;
        /// <summary>
        /// 屬性的字串值 (僅限 "Caliber" 這類屬性，否則為 null)
        /// </summary>
        public string StringValue;
        // [v2.5.0] 修正：支援 CustomData 類型
        /// <summary>
        /// 屬性的資料類型 (來自 CustomData)
        /// </summary>
        public CustomDataType DataType;
    }

    /// <summary>
    /// [v2.0.0] 儲存單一效果 (Effect / Usage) 的資料
    /// </summary>
    public struct EffectEntry
    {
        /// <summary>
        /// 效果的「本地化 Key」 (e.g., "Effect.ArmorPiercing.Desc")
        /// </summary>
        public string DisplayNameKey;
        /// <summary>
        /// 效果的「本地化描述 Key」
        /// </summary>
        public string DescriptionKey;
        /// <summary>
        /// 效果的類型 (是 "Effect" 還是 "Usage")
        /// </summary>
        public string Type;
    }

    /// <summary>
    /// [v2.0.0] 屬性/效果圖鑑的總條目
    /// </summary>
    public struct StatsAndEffectsEntry
    {
        public int TypeID;
        public List<StatEntry> Stats;
        public List<EffectEntry> Effects;
    }

    /// <summary>
    /// [v1.3.0] 配方輸入材料結構 (簡化版)
    /// </summary>
    public struct RecipeIngredient
    {
        public string ItemNameKey;
        // 材料物品的 Key
        public int Count;
        // 需要的數量
    }

    /// <summary>
    /// [v1.3.0] 配方輸出結果結構 (簡化版)
    /// </summary>
    public struct RecipeOutput
    {
        public string ItemNameKey;
        // 產出物品的 Key
        public int Count;
        // 產出的數量
    }

    /// <summary>
    /// [v1.3.0] 配方圖鑑條目 (API v1.3.0)
    /// </summary>
    public struct RecipeEntry
    {
        public string FormulaID;
        // 配方 ID (e.g., "Craft_Weapon_AK47")
        public List<string> Tags;
        // 配方標籤
        public bool UnlockByDefault;
        // 是否預設解鎖
        public List<RecipeIngredient> Cost;
        // 製作材料列表
        public List<RecipeOutput> Result;
        // 製作結果列表
    }

    /// <summary>
    /// 核心帳本條目 (API v2.3.0)
    /// 這就是 API 掃描後，對外提供的「圖鑑資料」。
    /// </summary>
    public struct LedgerEntry
    {
        // --- 【分類 A：CSI 來源情報】 (v1.0.0 核心) ---
        public int TypeID;
        // 物品的 TypeID (e.g., 5000001)
        public string ItemNameKey;
        // 物品的內部 Key (e.g., "accessory.sliencer001")
        public string BronzeID;
        // 來源 Mod 的「顯示名稱」 (e.g., "Guns Galore Mod")
        public string GoldenID;
        // 來源 Mod 的「Steam ID」 (e.g., "283748374")
        public string SilverID;
        // 來源 Mod 的「DLL 名稱」 (e.g., "GunsGalore.dll")

        // --- 【分類 B：遊戲 API 靜態情報】 (v1.0.1 強化) ---
        // 這些是順手抓的，讓 API 使用者更方便
        public List<string> Tags;
        // [v2.1.0 修正] 物品標籤 (e.g., ["Bullet", "Luxury"])
        public int Quality;
        // 稀有度 (int) (v1.0.0 就抓了)
        public float Value;
        // 基礎價值
        public int MaxStack;
        // 最大堆疊
        public float Weight;
        // [v2.2.2] 新增：物品重量

        // --- 【v1.2.0 新功能】 ---
        public string Description;
        // 物品敘述
    }

    /// <summary>
    /// 鴨嘴獸核心 API (v2.8.0)
    /// [v2.2.1 修正] 加上 partial
    /// </summary>
    public partial class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // ==================================================
        // 核心變數 (v2.8.0)
        // ==================================================
        private static Harmony?
harmonyInstance;
        private const string HARMONY_ID = "com.mingyang.duckovcoreapi";
        private static bool isHarmonyPatched = false;
        // [v1.1.0] 防止重複 Patch

        // [v1.0.0] Key (LocalizationKey/ItemName) -> ModInfoCopy
        private static Dictionary<string, ModInfoCopy> a_WorkshopCache = new Dictionary<string, ModInfoCopy>();
        private static bool isWorkshopCacheBuilt = false;
        private static bool isWorkshopScanRunning = false;
        private static bool forceLedgerRescan = false;
        // [v2.3.0] 標記：是否因 Mod 移除而觸發強制重掃

        // [v1.0.0 核心] Phase 1 (攔截器) 建立的「來源追蹤表」
        internal static Dictionary<string, string> a_Type1_Source_Map = new Dictionary<string, string>();
        // [v1.1.0 核心] StackTrace 掃描時的「忽略名單」
        internal static HashSet<string> a_StackTrace_IgnoreList = new HashSet<string> {
            "Assembly-CSharp.dll",
            "ItemStatsSystem.dll",
            "Duckov.Modding.dll",
            "UnityEngine.CoreModule.dll",
            "mscorlib.dll",
            "TeamSoda.Duckov.Core.dll",

      "DuckovCoreAPI.dll",
            "0Harmony.dll"
        };
        // [v1.0.0 核心] (Phase A) DLL 路徑 -> ModInfo 對照表
        private static Dictionary<string, ModInfoCopy> a_ModInfo_By_DLL_Path = new Dictionary<string, ModInfoCopy>();
        // [v1.0.0 核心] 「記憶體」帳本 (Phase B 掃描時暫存)
        private static Dictionary<int, LedgerEntry> a_MasterLedger = new Dictionary<int, LedgerEntry>();
        // [v1.0.0 核心] 「已儲存」帳本 (API 的主要資料庫)
        private static Dictionary<int, LedgerEntry> a_SavedLedger = new Dictionary<int, LedgerEntry>();
        // [v1.3.0 核心] 「配方」帳本 (API 的配方資料庫)
        private static Dictionary<string, RecipeEntry> a_RecipeLedger = new Dictionary<string, RecipeEntry>();
        // [v2.0.0 核心] 「屬性/效果」帳本
        private static Dictionary<int, StatsAndEffectsEntry> a_StatsEffectsLedger = new Dictionary<int, StatsAndEffectsEntry>();
        // [v1.0.0 核心] 「反向索引」 (用來加速 API 查詢)
        private static Dictionary<string, int> a_ReverseLedger_Golden = new Dictionary<string, int>();
        private static Dictionary<string, int> a_ReverseLedger_Silver = new Dictionary<string, int>();
        private static Dictionary<string, int> a_ReverseLedger_Bronze = new Dictionary<string, int>();
        private static bool isFirstRun_Saved = false;
        private static bool isLedgerReady_Saved = false;
        private static bool isMerging = false;
        private static bool isDirty_Saved = false;
        private static bool hasWarnedLedgerNotReady = false;
        // [v2.4.0] UI 訊息佇列 (用於在 UI 就緒前緩存訊息)
        public static List<(string message, float timestamp, bool isError, float duration)> uiMessageQueue_v2 = new List<(string, float, bool, float)>();
        public static bool isUIReady = false;

        // [v1.0.0 核心] 聖杯訊號！
        private static bool isDatabaseReady = false;
        // [v1.3.0 核心] 配方聖杯訊號！
        private static bool isRecipeReady = false;
        // [v2.0.0 核心] 屬性聖杯訊號！
        private static bool isStatsEffectsReady = false;


        internal static ModBehaviour?
instance;

        private const string LEDGER_FILENAME_SAVED = "ID_Master_Ledger.json";
        private const string RECIPE_FILENAME_SAVED = "ID_Recipe_Ledger.json";
        // [v1.3.0] 新增
        private const string STATS_FILENAME_SAVED = "ID_StatsEffects_Ledger.json";
        // [v2.0.0] 新增
        private const string WORKSHOP_CACHE_FILENAME = "Mod_Workshop_Cache.json";
        // [v1.0.0] (Phase A)
        [Serializable]
        public struct ModInfoCopy
        {
            public string path;
            public string name; // .dll name (SilverID)
            public string displayName;
            // 顯示名稱 (BronzeID)
            public ulong publishedFileId;
            // Steam ID (GoldenID)
            public long lastWriteTime;
            // 檢查 .dll 是否更新

            // [v2.3.0] 標記是否為本地 (非 Workshop) Mod
            public bool isLocalMod;
        }

        // [v2.8.0] 屬性掃描白名單
        // 
        // 
        // 用於 ScanForStats 和 ScanForCustomData。
        // 
        private static readonly HashSet<string> ATTRIBUTE_WHITELIST = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Damage",
            "CritRate",
            "CritDamageFactor",
            "ArmorPiercing",

     "AttackSpeed",
            "AttackRange",
            "StaminaCost",
            "WalkSpeed",
            "RunSpeed",
            "TurnSpeed",
            "AimSpeed",
            "Stability",

 "StabilityScoping",
            "Ergonomics",
            "RecoilPercentage",
            "RecoilPercentageScoping",
            "Recoil",
            "VerticalRecoil",
            "HorizontalRecoil",
            "RecoilScoping",
            "VerticalRecoilScoping",

         "HorizontalRecoilScoping",
            "RecoilVisual",
            "VerticalRecoilVisual",
            "HorizontalRecoilVisual",
            "RecoilVisualScoping",
            "VerticalRecoilVisualScoping",
            "HorizontalRecoilVisualScoping",
            "RecoilCompensation",

     "RecoilCompensationScoping",
            "RecoilDrift",
            "RecoilDriftScoping",
            "RecoilDriftHorizontal",
            "RecoilDriftHorizontalScoping",
            "RecoilDispersion",
            "RecoilDispersionScoping",
            "Scatter",

 "ScatterScoping",
            "ScatterFire",
            "ScatterFireScoping",
            "Deviation",
            "DeviationScoping",
            "Accuracy",
            "HipAccuracy",
            "ScopingAccuracy",
            "AimSensitivity",

         "ScopingSensitivity",
            "MaxDurability",
            "Durability",
            "DurabilityBurn",
            "Heat",
            "MalfunctionChance",
            "MuzzleVelocity",
            "Armor",

     "ArmorClass",
            "ArmorDamageReduction",
            "HelmetArmor",
            "Encumbrance",
            "Noise",
            "NoiseScoping",
            "Loudness",
            "Hearing",

 "DamageMultiplier",
            "ArmorPiercingMultiplier",
            "ArmorDamageMultiplier",
            "AccuracyMultiplier",
            "RecoilMultiplier",
            "RecoilXMultiplier",
            "RecoilYMultiplier",
            "ErgonomicsMultiplier",
            "MuzzleVelocityMultiplier",

         "StaminaCostMultiplier",
            "AimSpeedMultiplier",
            "ScopingThreatReduction",
            "MoveSpeedPenalty",
            "TurnSpeedPenalty",
            "ErgonomicsPenalty",
            "NewArmorPiercingGain",
            "NewDamageGain",

     "StaminaBurn",
            "AmmoCost",
            "Caliber" // [v2.8.0] 補上 v2.2.0 就有的 Caliber
        };
        // ==================================================
        // [Phase 3] Mod 啟動與關閉 (v2.5.1)
        // ==================================================

        /// <summary>
        /// [v1.1.0 核心] 遊戲一載入 (Awake)
        /// </summary>
        void Awake()
        {
            Log("v2.8.0 Awake() 啟動。");
            instance = this;

            if (isHarmonyPatched)
            {
                Log("v2.8.0 Awake() 偵測到 Harmony 已安裝，跳過。");
                return;
            }

            if (harmonyInstance == null)
            {
                harmonyInstance = new Harmony(HARMONY_ID);
            }

            // --- Phase 1 (DLL 攔截) ---
            Log("正在安裝 Phase 1 (DLL 攔截器)...");
            try
            {
                var p1_original = AccessTools.Method(typeof(ItemAssetsCollection), "AddDynamicEntry", new Type[] { typeof(Item) });
                if (p1_original == null) throw new Exception("找不到 ItemAssetsCollection.AddDynamicEntry(Item)");
                var p1_postfix = AccessTools.Method(typeof(Patch_ItemAssetsCollection_Intercept), "Postfix");
                harmonyInstance.Patch(p1_original, null, new HarmonyMethod(p1_postfix));
                Log("Phase 1 (DLL StackTrace 攔截器) 安裝成功。");
                isHarmonyPatched = true;
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\nPhase 1 (DLL 攔截) 綁定失敗！\n{e.Message}\nType 1 (純 DLL) 模組物品的來源可能無法被追蹤！");
            }
        }

        /// <summary>
        /// [v1.1.0] Mod 載入器 (OnAfterSetup)
        /// </summary>
        protected override void OnAfterSetup()
        {
            instance = this;
            Log("正在安裝 v2.8.0 核心 API (1.txt 照抄版)...");

            // --- Phase UI 建立 ---
            try
            {
                if (GameObject.Find("DuckovCoreAPI_UI") == null)
                {
                    GameObject uiHost = new GameObject("DuckovCoreAPI_UI");
                    uiHost.AddComponent<CoreUI>();
                    UnityEngine.Object.DontDestroyOnLoad(uiHost);
                    Log("Phase UI (OnGUI) 建立完畢！");
                }
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\nPhase UI (OnGUI) 建立失敗！\n{e.Message}");
            }

            // --- Phase B 訂閱 ---
            try
            {
                LevelManager.OnAfterLevelInitialized -= OnLevelLoaded_DatabaseScan;
                LevelManager.OnAfterLevelInitialized += OnLevelLoaded_DatabaseScan;
                Log("Phase B (資料庫掃描) 鉤子已訂閱。");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\nPhase B (資料庫掃描) 訂閱失敗！\n{e.Message}");
            }

            // --- 讀取帳本 ---
            if (!isLedgerReady_Saved && !isFirstRun_Saved)
            {
                LoadLedgerAsync();
            }
            // [v1.3.0] 讀取配方帳本
            LoadRecipeLedgerAsync();
            // [v2.0.0] 讀取屬性帳本
            LoadStatsEffectsLedgerAsync();
            // --- Phase A 延遲啟動 ---
            try
            {
                if (isWorkshopScanRunning || isWorkshopCacheBuilt)
                {
                    Log("Phase A (Workshop 掃描) 已在執行中，跳過。");
                    return;
                }
                Log("偵測到 OnAfterSetup，正在啟動 Phase A (延遲掃描 Coroutine)...");
                StartCoroutine(InitializePhaseA_Coroutine());
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\nPhase A (Workshop 掃描) 延遲啟動失敗！\n{e.Message}");
                isWorkshopCacheBuilt = true;
                isWorkshopScanRunning = false;
            }

            Log("「鴨嘴獸核心 API v2.8.0」模組已啟動。");
        }

        private IEnumerator InitializePhaseA_Coroutine()
        {
            isWorkshopScanRunning = true;
            Log("Phase A 正在等待 3 秒鐘 (等待遊戲 Mod 載入器)...");
            yield return new WaitForSeconds(3.0f);
            Log("3 秒延遲完畢，啟動 Phase A (Workshop 掃描)...");
            StartWorkshopScanProcess();
        }

        protected override void OnBeforeDeactivate()
        {
            try
            {
                harmonyInstance?.UnpatchAll(HARMONY_ID);
                isHarmonyPatched = false;
                Log("Harmony 攔截器已全部移除。");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 停用時發生錯誤: {e.Message}");
            }

            LevelManager.OnAfterLevelInitialized -= OnLevelLoaded_DatabaseScan;

            var uiHost = GameObject.Find("DuckovCoreAPI_UI");
            if (uiHost != null)
            {
                UnityEngine.Object.Destroy(uiHost);
            }

            if (instance == this)
            {
                instance = null;
            }

            // --- 清理靜態變數 ---
            a_WorkshopCache.Clear();
            isWorkshopCacheBuilt = false;
            isWorkshopScanRunning = false;
            forceLedgerRescan = false; // [v2.3.0]
            a_Type1_Source_Map.Clear();
            a_ModInfo_By_DLL_Path.Clear();
            a_MasterLedger.Clear();
            a_SavedLedger.Clear();
            a_RecipeLedger.Clear();
            a_StatsEffectsLedger.Clear(); // [v2.0.0]
            a_ReverseLedger_Golden.Clear();
            a_ReverseLedger_Silver.Clear();
            a_ReverseLedger_Bronze.Clear();
            isLedgerReady_Saved = false;
            isFirstRun_Saved = false;
            isMerging = false;
            isDirty_Saved = false;
            hasWarnedLedgerNotReady = false;
            isUIReady = false;
            isDatabaseReady = false;
            isRecipeReady = false;
            isStatsEffectsReady = false; // [v2.0.0]
            uiMessageQueue_v2.Clear();
            // [v2.4.0]
        }

        // ==================================================
        // [Phase A] Workshop 掃描 (v2.5.1)
        // ==================================================

        /// <summary>
        /// [v2.3.0] 遞迴掃描 Mod 資料夾 (Phase A)
        /// </summary>
        private static async void StartWorkshopScanProcess()
        {

            try
            {
                // 1. 讀取舊快取
                await LoadWorkshopCacheAsync();
                // 2. [v1.1.2] 取得「所有」Mod 資料夾路徑 (無論是否啟用)
                List<ModInfoCopy> currentModInfos = new List<ModInfoCopy>();
                // [v2.3.0]
                string gameWorkshopPath = "";
                // "...\3167020"

                try
                {
                    // 2a. 掃描本地 Mods 資料夾
                    string localModsPath = Path.Combine(Application.dataPath, "Mods");
                    if (Directory.Exists(localModsPath))
                    {
                        var localFolders = Directory.GetDirectories(localModsPath);
                        foreach (var folder in localFolders)
                        {
                            ModInfoCopy?
info = ParseModFolder(folder, true);
                            if (info != null) currentModInfos.Add(info.Value);
                        }
                        Log($"[Phase A v2.8.0] 掃到 {currentModInfos.Count} 個本地 Mod。");
                    }

                    // 2b. [v1.1.2 核心修正] 向上爬路徑以尋找 Steam Workshop 資料夾
                    try
                    {
                        string steamAppsPath = Directory.GetParent(Directory.GetParent(Directory.GetParent(Application.dataPath).FullName).FullName).FullName;
                        gameWorkshopPath = Path.Combine(steamAppsPath, "workshop", "content", "3167020");
                    }
                    catch (Exception e)
                    {
                        Log($"[Phase A v2.8.0] 警告: 爬路徑找 steamapps 失敗: {e.Message}");
                        gameWorkshopPath = "";
                    }

                    // 2c. [v1.1.2 修正] 掃描 "3167020" 裡面的「所有」資料夾
                    if (Directory.Exists(gameWorkshopPath))
                    {
                        Log($"[Phase A v2.8.0] 抓到 Workshop 遊戲目錄: {gameWorkshopPath}");
                        var workshopFolders = Directory.GetDirectories(gameWorkshopPath);
                        int steamModCount = 0;
                        foreach (var folder in workshopFolders)
                        {
                            ModInfoCopy?
info = ParseModFolder(folder, false);
                            if (info != null)
                            {
                                currentModInfos.Add(info.Value);
                                steamModCount++;
                            }
                        }
                        Log($"[Phase A v2.8.0] 掃到 {steamModCount} 個 Workshop Mod。");
                    }
                    else
                    {
                        Log("[Phase A v2.8.0] 警告: 找不到 Workshop 遊戲目錄 (可能是非 Steam 版)");
                    }
                }
                catch (Exception e)
                {
                    ShowError($"[DuckovCoreAPI] 嚴重錯誤：\nPhase A (v2.8.0) 掃描資料夾失敗！\n{e.Message}");
                }

                if (currentModInfos.Count == 0)
                {
                    ShowWarning("[DuckovCoreAPI] 找不到任何 Mod 資料夾，跳過 Workshop 掃描。");
                    isWorkshopCacheBuilt = true;
                    isWorkshopScanRunning = false;
                    return;
                }

                // 3. [v2.3.0] 建立 DLL 對照表
                a_ModInfo_By_DLL_Path.Clear();
                foreach (var info in currentModInfos)
                {
                    string dllPath = Path.Combine(info.path, info.name + ".dll");
                    if (File.Exists(dllPath) && !a_ModInfo_By_DLL_Path.ContainsKey(dllPath))
                    {
                        a_ModInfo_By_DLL_Path.Add(dllPath, info);
                    }
                }
                Log($"[Phase A v2.8.0] 遞迴掃描完畢。共找到 {currentModInfos.Count} 個 info.ini，登記了 {a_ModInfo_By_DLL_Path.Count} 個 .dll。");
                // 4. [v2.3.0] 執行差異比對 (Diff Check)
                ShowWarning("[DuckovCoreAPI] 正在比對 Mod 快取...");
                forceLedgerRescan = false; // [v2.3.0]
                bool cacheNeedsUpdate = false;
                var oldCacheByPath = a_WorkshopCache.Values.ToLookup(m => m.path);
                var currentModsByPath = currentModInfos.ToDictionary(m => m.path, m => m);

                List<ModInfoCopy> modsToScan = new List<ModInfoCopy>();
                // 4a. 檢查新增 / 更新
                foreach (var mod in currentModInfos)
                {
                    if (!oldCacheByPath.Contains(mod.path) || oldCacheByPath[mod.path].First().lastWriteTime != mod.lastWriteTime)
                    {

                        modsToScan.Add(mod);
                    }
                }

                // 4b. [v2.3.0] 檢查已移除的 Mod
                foreach (var oldModGroup in oldCacheByPath)
                {
                    if (!currentModsByPath.ContainsKey(oldModGroup.Key))
                    {

                        cacheNeedsUpdate = true;
                        forceLedgerRescan = true;
                        // 觸發強制重掃！
                        var oldMod = oldModGroup.First();
                        Log($"[Phase A] 偵測到 Mod 已被移除：{oldMod.displayName} (Path: {oldMod.path})");

                        // 從快取中刪除
                        foreach (var item in a_WorkshopCache.Where(kvp => kvp.Value.path == oldMod.path).ToList())
                        {
                            a_WorkshopCache.Remove(item.Key);
                        }
                    }
                }

                if (forceLedgerRescan)
                {
                    ShowError("[DuckovCoreAPI] 偵測到 Mod 被移除！\n將觸發強制重新掃描所有物品來源 (修正幽靈 Mod 衝突)。");
                }

                // 5. 執行掃描 (如果需要)
                if (modsToScan.Count > 0)
                {
                    ShowWarning($"[DuckovCoreAPI] 發現 {modsToScan.Count} 個新/更新的 Mod，正在背景掃描 .json...");
                    cacheNeedsUpdate = true;
                    // [v1.1.3 核心修正] 把 cacheNeedsUpdate 傳進去！
                    await BuildWorkshopCacheAsync(modsToScan, cacheNeedsUpdate);
                }
                else
                {
                    // [v1.1.2] 就算沒有要掃描的 Mod，如果快取有變 (Mod 被移除)，也要存檔
                    if (cacheNeedsUpdate)
                    {

                        await SaveWorkshopCacheAsync();
                    }
                    Log("[Phase A v2.8.0] .json 快取比對完畢，無需更新。");
                }

                ShowWarning("[DuckovCoreAPI] Mod 物品索引已就緒！");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\nPhase A (v2.8.0 遞迴掃描) 失敗！\n{e.Message}");
            }
            finally
            {
                isWorkshopCacheBuilt = true;
                isWorkshopScanRunning = false;
            }
        }

        /// <summary>
        /// [v2.3.0] 解析單一 Mod 資料夾 (info.ini)
        /// </summary>
        private static ModInfoCopy?
ParseModFolder(string modFolderPath, bool isLocal)
        {
            try
            {
                string infoIniPath = Path.Combine(modFolderPath, "info.ini");
                if (!File.Exists(infoIniPath)) return null;

                // 3a. [v1.1.3 修正] 手動解析 info.ini
                ModInfoCopy infoCopy = ParseInfoIni(infoIniPath, modFolderPath);
                if (string.IsNullOrEmpty(infoCopy.name))
                {
                    Log($"[Phase A v2.8.0] 警告: {infoIniPath} 缺少 'name' 欄位 (或 Parse 失敗)，跳過。");
                    return null;
                }

                // [v2.3.0] 標記是否為本地 Mod
                infoCopy.isLocalMod = isLocal;
                if (!isLocal && infoCopy.publishedFileId == 0)
                {
                    try
                    {
                        infoCopy.publishedFileId = ulong.Parse(Path.GetFileName(modFolderPath));
                    }
                    catch { /* 轉失敗則忽略 */ }
                }

                // 3b. 尋找 .dll 並取得最後修改時間
                long lastWriteTime = 0;
                string dllPath = "";
                try
                {
                    dllPath = Path.Combine(modFolderPath, infoCopy.name + ".dll");
                    if (File.Exists(dllPath))
                    {
                        lastWriteTime = File.GetLastWriteTime(dllPath).Ticks;
                    }
                    else
                    {
                        dllPath = "";
                        // .dll 不存在
                        lastWriteTime = File.GetLastWriteTime(modFolderPath).Ticks;
                    }
                }
                catch { }

                infoCopy.lastWriteTime = lastWriteTime;
                return infoCopy;
            }
            catch (Exception e)
            {
                Log($"[Phase A v2.8.0] 處理資料夾 {Path.GetFileName(modFolderPath)} 失敗: {e.Message}");
                return null;
            }
        }


        /// <summary>
        /// [v1.1.3 核心修正] 手動解析 info.ini (聰明版)
        /// </summary>
        private static ModInfoCopy ParseInfoIni(string iniPath, string modFolderPath)
        {
            var info = new ModInfoCopy { path = modFolderPath };
            try
            {
                string[] lines = File.ReadAllLines(iniPath);
                foreach (string line in lines)
                {
                    string[] parts = line.Split(new char[] { '=' }, 2);
                    if (parts.Length < 2) continue;

                    string key = parts[0].Trim();
                    string value = parts[1].Trim();
                    if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                        info.name = value;
                    else if (key.Equals("displayName", StringComparison.OrdinalIgnoreCase))
                        info.displayName = value;
                    else if (key.Equals("publishedFileId", StringComparison.OrdinalIgnoreCase))
                        ulong.TryParse(value, out info.publishedFileId);
                }
            }
            catch (Exception e)
            {
                Log($"[ParseInfoIni v2.8.0] 解析 {Path.GetFileName(iniPath)} 失敗: {e.Message}");
            }
            // 如果沒有 displayName，就用 name
            if (string.IsNullOrEmpty(info.displayName))
                info.displayName = info.name;
            return info;
        }

        /// <summary>
        /// [v2.2.0 核心修正] Phase A 現在*只*負責掃描來源，*不*掃描屬性
        /// </summary>
        private static async Task BuildWorkshopCacheAsync(List<ModInfoCopy> modsToScan, bool cacheNeedsUpdate)
        {
            int itemsFoundInJsons = 0;
            try
            {
                await Task.Run(() =>
                {
                    // 1. 先把舊的掃掉 (使用 Path)
                    foreach (var mod in modsToScan)

                    {
                        foreach (var item in a_WorkshopCache.Where(kvp => kvp.Value.path == mod.path).ToList())
                        {
                            a_WorkshopCache.Remove(item.Key);

                        }
                    }

                    // 2. 再掃描新的
                    foreach (var modInfo in modsToScan)

                    {
                        Log($"[Phase A] (背景) 正在掃描 Mod (遞迴): {modInfo.displayName} (ID: {modInfo.publishedFileId})");
                        try
                        {

                            List<string> jsonFiles = new List<string>();

                            // 核心修正：掃描 Mod 根目錄 + 「所有」子資料夾！
                            if (Directory.Exists(modInfo.path))

                            {
                                // [v1.1.2] 使用 SearchOption.AllDirectories 進行遞迴掃描
                                jsonFiles.AddRange(Directory.GetFiles(modInfo.path, "*.json", SearchOption.AllDirectories));
                            }

                            // [v1.1.2] 如果 JSON 掃描結果為 0，發出警告
                            if (jsonFiles.Count == 0 && modInfo.publishedFileId > 1000) // 忽略本地 Mod
                            {

                                Log($"[Phase A] (背景) 警告: Mod {modInfo.displayName} (ID: {modInfo.publishedFileId}) 掃描不到任何 .json 檔案。");
                            }

                            foreach (string jsonFile in jsonFiles.Distinct())
                            {
                                try

                                {
                                    string jsonText = File.ReadAllText(jsonFile);
                                    // 移除 JSON 檔案中的 '//' 註解，以允許更寬鬆的 JSON 格式
                                    string cleanedJsonText = Regex.Replace(jsonText, @"^\s*//.*$", "", RegexOptions.Multiline);
                                    JToken token = JToken.Parse(cleanedJsonText);

                                    if (token is JArray array)
                                    {
                                        foreach (var item in array)

                                        {
                                            // 嘗試從多個欄位抓取物品 Key

                                            string?
                      itemKey = item["LocalizationKey"]?.ToString() ??
                                                                                    item["ItemName"]?.ToString() ??
                                                                                    item["DisplayName"]?.ToString();

                                            if (itemKey != null)
                                            {

                                                bool isNew = !a_WorkshopCache.ContainsKey(itemKey);
                                                a_WorkshopCache[itemKey] = modInfo; // 允許覆蓋
                                                if (isNew) itemsFoundInJsons++;
                                            }
                                        }
                                    }

                                    else if (token is JObject obj)
                                    {
                                        string?
itemKey = obj["LocalizationKey"]?.ToString() ??
                                                          obj["ItemName"]?.ToString() ??
                                                          obj["DisplayName"]?.ToString();

                                        if (itemKey != null)
                                        {
                                            bool isNew = !a_WorkshopCache.ContainsKey(itemKey);
                                            a_WorkshopCache[itemKey] = modInfo; // 允許覆蓋
                                            if (isNew) itemsFoundInJsons++;
                                            // [v2.2.0 移除] 不再於此處掃描屬性
                                        }
                                    }

                                }
                                catch (Exception e)
                                {

                                    Log($"[Phase A] (背景) 解析 {modInfo.name} の {Path.GetFileName(jsonFile)} 失敗: {e.Message}");
                                }
                            }
                        }
                        catch (Exception e)

                        {
                            Log($"[Phase A] (背景) 掃描 Mod {modInfo.name} 資料夾失敗: {e.Message}");
                        }
                    }
                });
                // 背景執行緒結束
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\nPhase A (Workshop 掃描) 背景執行緒失敗！\n{e.Message}");
            }
            finally
            {
                Log($"Phase A (JSON 掃描) 完畢。在 {modsToScan.Count} 個 Mod 中找到 {itemsFoundInJsons} 筆新物品。");
                if (cacheNeedsUpdate)
                {
                    await SaveWorkshopCacheAsync();
                }
            }
        }


        // ==================================================
        // [Phase B] 物品掃描 (v2.3.0)
        // ==================================================

        /// <summary>
        /// [v1.0.0 Phase B] (由 OnAfterLevelInitialized 觸發)
        /// </summary>
        private static void OnLevelLoaded_DatabaseScan()

        {
            try
            {
                isUIReady = true;
                // [v2.4.0]
                foreach (var (msg, timestamp, isError, duration) in uiMessageQueue_v2)
                {
                    CoreUI.AddMessage(msg, isError, duration);
                }
                uiMessageQueue_v2.Clear();
                Log($"正在清空 UI 緩衝...");
            }
            catch (Exception e)
            {
                Log($"[DuckovCoreAPI] UI 緩衝區清空失敗: {e.Message}");
            }

            if (isMerging) return;
            if (instance != null)
            {
                Log("Phase B (資料庫掃描) 啟動。");
                instance.StartCoroutine(DatabaseScanCoroutine());
            }
            else
            {
                ShowError("[DuckovCoreAPI] 嚴重錯誤：ModBehaviour 實例為 null！無法啟動 Coroutine 掃描！");
            }
        }

        /// <summary>
        /// [v2.3.0] CSI 核心掃描器 (Phase B + Phase C)
        /// </summary>
        private static IEnumerator DatabaseScanCoroutine()
        {
            isMerging = true;
            isDatabaseReady = false;
            isRecipeReady = false; // [v1.3.0] 重置配方聖杯訊號
            isStatsEffectsReady = false;
            // [v2.0.0] 重置屬性聖杯訊號

            // 1. 等待 Phase A (Workshop 掃描) 和 (舊)帳本讀取完畢
            if (!isWorkshopCacheBuilt || !isLedgerReady_Saved)
            {
                ShowWarning("[DuckovCoreAPI] 正在等待 Mod 索引或歷史帳本...");
                yield return new WaitUntil(() => isWorkshopCacheBuilt && isLedgerReady_Saved);
            }
            // 2. 檢查 ItemAssetsCollection.Instance
            if (ItemAssetsCollection.Instance == null)
            {
                ShowError("[DuckovCoreAPI] 掃描 Part 1 發生致命錯誤：ItemAssetsCollection.Instance 是 null！CSI 失敗！");
                isMerging = false;
                yield break;
            }
            // 3. 等待遊戲的 `dynamicDic` 欄位被填入 (最多 90 秒)
            FieldInfo?
field = AccessTools.Field(typeof(ItemAssetsCollection), "dynamicDic");
            if (field == null)
            {
                ShowError("[DuckovCoreAPI] 掃描 Part 2 發生致命錯誤：找不到 'dynamicDic' 欄位！CSI 失敗！");
                isMerging = false;
                yield break;
            }
            ShowWarning("[DuckovCoreAPI] 正在等待遊戲本體 Mod 載入器 (dynamicDic)...");
            object? dynamicDicValue = null;
            float waitTimer = 0f;
            while (dynamicDicValue == null && waitTimer < 90.0f) // [v1.1.4] 90 秒
            {
                dynamicDicValue = field.GetValue(null);
                if (dynamicDicValue == null)
                {
                    yield return new WaitForSeconds(0.5f);
                    waitTimer += 0.5f;
                }
            }
            Dictionary<int, ItemAssetsCollection.DynamicEntry>?
dynamicDatabase = dynamicDicValue as Dictionary<int, ItemAssetsCollection.DynamicEntry>;
            if (dynamicDatabase == null)
            {
                ShowError($"[DuckovCoreAPI] 掃描 Part 2 發生致命錯誤：等待 90 秒後 'dynamicDic' 還是 null！");
                isMerging = false;
                yield break;
            }

            // 4. [Phase B] 執行物品資料庫掃描 (CSI)
            ShowWarning("[DuckovCoreAPI] 遊戲 Mod 載入完畢！正在掃描物品資料庫 (CSI)...");
            int totalItems_Instance = 0;
            int totalItems_Dynamic = 0;
            int totalItems_Type1 = 0;
            int yieldCount = 0;

            a_MasterLedger.Clear();
            a_ReverseLedger_Golden.Clear();
            a_ReverseLedger_Silver.Clear();
            a_ReverseLedger_Bronze.Clear();
            a_StatsEffectsLedger.Clear(); // [v2.2.0] 在此處清空屬性帳本

            // [v1.1.2] 確保 Core 也在黑名單
            if (!a_StackTrace_IgnoreList.Contains("TeamSoda.Duckov.Core.dll"))
                a_StackTrace_IgnoreList.Add("TeamSoda.Duckov.Core.dll");
            // --- Part 1: 掃描「遊戲本體」資料庫 (entries) ---
            List<ItemAssetsCollection.Entry>?
baseGameEntries = null;
            try
            {
                baseGameEntries = ItemAssetsCollection.Instance.entries;
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 掃描 Part 1 (BaseGame) 發生致命錯誤 (無法取得 entries): {e.Message}");
            }

            if (baseGameEntries != null)
            {
                totalItems_Instance = baseGameEntries.Count;
                foreach (var entry in baseGameEntries)
                {
                    try
                    {
                        if (entry == null || entry.prefab == null) continue;
                        ProcessItemPrefab_Json(entry.prefab, entry.typeID, "BaseGame");
                        ScanAndStoreItemStats(entry.prefab); // [v2.2.0] 掃描屬性
                    }
                    catch (Exception e)
                    {
                        Log($"[DuckovCoreAPI] 處理 BaseGame 物品 {entry?.typeID} 失敗: {e.Message}");
                    }
                    yieldCount++;
                    if (yieldCount % 50 == 0) yield return null;
                }
            }

            // --- Part 2: 掃描「Mod」資料庫 (dynamicDic) ---
            foreach (var kvp in dynamicDatabase)
            {
                try

                {
                    ItemAssetsCollection.DynamicEntry?
entry = kvp.Value;
                    if (entry == null || entry.prefab == null) continue;

                    bool isJsonMod = ProcessItemPrefab_Json(entry.prefab, entry.typeID, "Mod");
                    if (!isJsonMod)
                    {
                        ProcessItemPrefab_Type1(entry.prefab, entry.typeID);
                        totalItems_Type1++;
                    }
                    else
                    {
                        totalItems_Dynamic++;
                    }

                    ScanAndStoreItemStats(entry.prefab);
                    // [v2.2.0] 掃描屬性
                }
                catch (Exception e)
                {
                    Log($"[DuckovCoreAPI] 處理 Mod 物品 {kvp.Key} 失敗: {e.Message}");
                }

                yieldCount++;
                if (yieldCount % 50 == 0) yield return null;
            }

            ShowWarning($"[DuckovCoreAPI] 物品掃描完畢，正在比對歷史帳本...");
            yield return null;

            // 5. [v2.3.0] 執行「即時合併」 (Live Merge)
            int newItems = 0;
            int conflicts = 0;

            // [v2.3.0] 如果 Mod 被移除 (forceLedgerRescan)，強制用 a_MasterLedger 覆蓋 a_SavedLedger
            if (forceLedgerRescan)
            {
                ShowWarning("[DuckovCoreAPI] (強制重掃) 偵測到 forceLedgerRescan，正在用新掃描覆蓋舊帳本...");
                a_SavedLedger.Clear();
                isDirty_Saved = true; // 強制標記為
                forceLedgerRescan = false;
            }

            try
            {
                foreach (var kvp in a_MasterLedger)
                {
                    if (!a_SavedLedger.ContainsKey(kvp.Key))

                    {
                        a_SavedLedger[kvp.Key] = kvp.Value;
                        newItems++;
                    }
                    else
                    {
                        var existingEntry = a_SavedLedger[kvp.Key];
                        var newEntry = kvp.Value;

                        // [v2.2.2 修正] 檢查 Tag 和 Weight 是否有變化
                        if (existingEntry.BronzeID != newEntry.BronzeID ||
                            existingEntry.GoldenID != newEntry.GoldenID ||
                            existingEntry.Description !=
newEntry.Description ||
                            existingEntry.Weight != newEntry.Weight || // [v2.2.2] 檢查 Weight
                            !existingEntry.Tags.SequenceEqual(newEntry.Tags)) // 檢查 Tag
                        {

                            // [v2.3.0] 檢查「真實衝突」(Type 2/3 Mod 搶 ID)
                            if (existingEntry.GoldenID != "BaseGame" && !existingEntry.BronzeID.Contains("Type 1") &&

                       newEntry.GoldenID != "BaseGame" && !newEntry.BronzeID.Contains("Type 1") &&
                                newEntry.GoldenID != existingEntry.GoldenID)
                            {

                                conflicts++;
                                // 衝突！
                                ShowError($"[DuckovCoreAPI] 偵測到 ID 衝突 (TypeID: {kvp.Key})！\n" +
                                          $" - 舊 Mod: {existingEntry.BronzeID} (SteamID: {existingEntry.GoldenID})\n" +

                                $" - 新 Mod: {newEntry.BronzeID} (SteamID: {newEntry.GoldenID})\n" +
                                          $" - 策略：保留歷史紀錄 (舊 Mod)。");
                            }
                            else
                            {
                                // (非衝突：判定為 Type 1 -> Type 2/3 升級，或資料更新，安全覆蓋)

                                a_SavedLedger[kvp.Key] = newEntry;
                                isDirty_Saved = true;
                            }
                        }
                    }
                }
            }
            catch (Exception e)

            {
                ShowError($"[DuckovCoreAPI] 帳本合併時發生嚴重錯誤！\n{e.Message}");
            }

            // 6. [v1.0.0] 清理與儲存
            a_MasterLedger.Clear();
            Log($"成功抓取 {totalItems_Instance} (本體) + {totalItems_Dynamic} (JSON) + {totalItems_Type1} (DLL) = {totalItems_Instance + totalItems_Dynamic + totalItems_Type1} 件物品。");
            Log($"成功建立 {a_ReverseLedger_Golden.Count} (金) / {a_ReverseLedger_Silver.Count} (銀) / {a_ReverseLedger_Bronze.Count} (銅) 筆反向索引。");
            if (newItems > 0)
            {
                isDirty_Saved = true;
                if (isFirstRun_Saved) { ShowWarning($"[DuckovCoreAPI] 首次運行：建立 {newItems} 筆物品帳本。"); }
                else
                {
                    ShowWarning($"[DuckovCoreAPI] 合併完畢！發現 {newItems} 個新物品。");
                }
            }
            else
            {
                ShowWarning("[DuckovCoreAPI] 物品帳本比對完畢，無需更新。");
            }
            ShowWarning($"[DuckovCoreAPI] 總共 {a_SavedLedger.Count} 筆物品記錄在案。");
            if (conflicts > 0)
            {
                ShowError($"[DuckovCoreAPI] 警告：偵測到 {conflicts} 起 ID 衝突！(策略：保留歷史紀錄)");
            }

            if (isDirty_Saved)
            {
                _ = SaveLedgerAsync(a_SavedLedger, GetLedgerPath(LEDGER_FILENAME_SAVED));
                isDirty_Saved = false;
                isFirstRun_Saved = false;
            }

            // [v2.2.0] 儲存屬性帳本
            _ = SaveStatsEffectsLedgerAsync(a_StatsEffectsLedger, GetLedgerPath(STATS_FILENAME_SAVED));
            isMerging = false;
            isDatabaseReady = true; // 物品聖杯訊號！
            isStatsEffectsReady = true;
            // [v2.2.0] 屬性聖杯訊號！
            Log("Phase B 完畢！CSI 完畢！聖杯訊號 (isDatabaseReady & isStatsEffectsReady) 啟動！");
            // 7. [v1.3.0 核心] 啟動 Phase C (配方掃描)
            // 必須在 isDatabaseReady = true 之後，因為 Phase C 需要用 a_SavedLedger 查表
            yield return instance!.StartCoroutine(ScanCraftingFormulas());
            Log("最終報告：物品/配方/屬性掃描全部完成。");
        }


        /// <summary>
        /// [v2.2.2] 處理 Type 2/3 (JSON) 和 遊戲本體
        /// </summary>
        private static bool ProcessItemPrefab_Json(Item prefab, int id, string type) // type = "BaseGame" or "Mod"
        {
            LedgerEntry newEntry = new LedgerEntry();
            // --- v1.0.0 物品 Key ---
            newEntry.ItemNameKey = prefab.DisplayNameRaw;
            newEntry.TypeID = id;

            if (type == "BaseGame")
            {
                newEntry.GoldenID = "BaseGame";
                newEntry.SilverID = "ItemStatsSystem";
                newEntry.BronzeID = "遊戲本體";
            }
            else
            {
                if (a_WorkshopCache.TryGetValue(newEntry.ItemNameKey, out ModInfoCopy modInfo))
                {
                    // 成功！這 是 Type 2/3 (JSON Mod)！

                    newEntry.GoldenID = modInfo.publishedFileId.ToString();
                    newEntry.SilverID = modInfo.name + ".dll";
                    newEntry.BronzeID = modInfo.displayName;
                }
                else
                {
                    // 失敗！ 這不是 JSON Mod，讓 Type 1 處理
                    return false;
                }
            }

            // --- v1.2.0 靜態資料掃描 ---
            try
            {
                // [v2.1.0 核心修正] 抓 tag.name 而不是 tag.DisplayName
                newEntry.Tags = prefab.Tags.Select(tag => tag.name).ToList();
                newEntry.Quality = (int)prefab.DisplayQuality;
                newEntry.Value = prefab.GetTotalRawValue();
                newEntry.MaxStack = prefab.MaxStackCount;
                newEntry.Weight = prefab.UnitSelfWeight; // [v2.2.2]
                newEntry.Description = prefab.Description ??
""; // [v1.2.0] 物品敘述
            }
            catch (Exception e)
            {
                Log($"[API] 抓取 {id} 靜態資料失敗: {e.Message}");
                newEntry.Tags = new List<string>();
                newEntry.Description = "";
            }

            // 存入「記憶體帳本」
            if (!a_MasterLedger.ContainsKey(id))
            {
                a_MasterLedger[id] = newEntry;
            }

            // 建立「反向索引」
            if (newEntry.GoldenID != "Unknown" && !a_ReverseLedger_Golden.ContainsKey($"{newEntry.GoldenID}:{newEntry.ItemNameKey}"))
                a_ReverseLedger_Golden[$"{newEntry.GoldenID}:{newEntry.ItemNameKey}"] = id;
            if (newEntry.SilverID != "Unknown" && !newEntry.SilverID.StartsWith("ItemStatsSystem") && !a_ReverseLedger_Silver.ContainsKey($"{newEntry.SilverID}:{newEntry.ItemNameKey}"))
                a_ReverseLedger_Silver[$"{newEntry.SilverID}:{newEntry.ItemNameKey}"] = id;
            if (newEntry.BronzeID != "遊戲本體" && !a_ReverseLedger_Bronze.ContainsKey($"{newEntry.BronzeID}:{newEntry.ItemNameKey}"))
                a_ReverseLedger_Bronze[$"{newEntry.BronzeID}:{newEntry.ItemNameKey}"] = id;
            return true;
        }

        /// <summary>
        /// [v2.2.2] 處理 Type 1 (純 DLL) 和 幽靈 Mod
        /// </summary>
        private static void ProcessItemPrefab_Type1(Item prefab, int id)
        {
            LedgerEntry newEntry = new LedgerEntry();
            // 1. 取得 prefab 的 Key
            newEntry.TypeID = id;
            newEntry.ItemNameKey = prefab.DisplayNameRaw;

            // 2. 檢查「記憶體帳本」
            if (a_MasterLedger.TryGetValue(id, out LedgerEntry existingEntry))
            {
                return;
            }

            // 3. 核心：查「V12 追蹤帳本」(Phase 1)
            string?
dll_path_from_trace = null;
            if (!a_Type1_Source_Map.TryGetValue(newEntry.ItemNameKey, out dll_path_from_trace))
            {
                // Phase 1 失敗！ (e.g. 91001)
                // 啟用 V11 反射備案
                try
                {
                    dll_path_from_trace = prefab.GetType().Assembly.Location;
                }
                catch { }

                if (string.IsNullOrEmpty(dll_path_from_trace))
                {
                    dll_path_from_trace = "Unknown Assembly (V12 Trace Failed)";
                }
                else
                {
                    // [v1.1.7] 檢查 V11 反射抓到的 DLL 是不是也在黑名單！
                    string assemblyFileName = "Unknown";
                    try { assemblyFileName = Path.GetFileName(dll_path_from_trace); } catch { }

                    if (a_StackTrace_IgnoreList.Contains(assemblyFileName))
                    {
                        // V11 反射抓到的是黑名單內的 DLL，判定為 遊戲本體

                        Log($"[v1.1.7] Type 1 降級: V11 反射抓到黑名單 {assemblyFileName}，判定為 遊戲本體。 (ID: {id})");
                        dll_path_from_trace = "BaseGame";
                        // 標記為 BaseGame
                    }
                    else
                    {
                        Log($"[v1.1.7] Type 1 警告 (ID: {id}): V12 追蹤帳本 找不到 Key '{newEntry.ItemNameKey}'！ 降級為 V11 反射！(Path: { dll_path_from_trace})");
                    }
                }
            }

            // 4. [v1.1.7] 根據 V12/V11 的結果，去 Phase A (遞迴掃描) 建立的「DLL 對照表」查詢
            if (dll_path_from_trace == "BaseGame")
            {
                // 判定為 遊戲本體

                newEntry.GoldenID = "BaseGame";
                newEntry.SilverID = "ItemStatsSystem (Fallback)";
                newEntry.BronzeID = "遊戲本體 (動態)";
            }
            else if (a_ModInfo_By_DLL_Path.TryGetValue(dll_path_from_trace, out ModInfoCopy modInfo))
            {
                // 找到了！這 是 Type 1 Mod！ (來自 Phase A 的 info.ini)
                newEntry.GoldenID = modInfo.publishedFileId.ToString();
                newEntry.SilverID = modInfo.name + ".dll";
                newEntry.BronzeID = modInfo.displayName + " (Type 1)";
                Log($"[v1.1.7] Type 1 掃描成功：ID {id} ({newEntry.ItemNameKey}) 歸屬於 {modInfo.displayName}");
            }
            else
            {
                // [v1.1.7] 這就是「幽靈 Mod」！ (Phase 1 抓到了，但 Phase A 還是找不到 info.ini)
                string ghost_dll_name = "Unknown Ghost Mod";
                try
                {
                    ghost_dll_name = Path.GetFileNameWithoutExtension(dll_path_from_trace);
                }
                catch { }

                newEntry.GoldenID = "Unknown (Ghost Mod)";
                newEntry.SilverID = Path.GetFileName(dll_path_from_trace);
                newEntry.BronzeID = ghost_dll_name + " (Type 1)";
                Log($"[v1.1.7] Type 1 (Ghost) 掃描成功：ID {id} ({newEntry.ItemNameKey}) 來自未索引的 DLL: {newEntry.SilverID}");
            }

            // --- v1.2.0 靜態資料掃描 ---
            try
            {
                // [v2.1.0 核心修正] 抓 tag.name 而不是 tag.DisplayName
                newEntry.Tags = prefab.Tags.Select(tag => tag.name).ToList();
                newEntry.Quality = (int)prefab.DisplayQuality;
                newEntry.Value = prefab.GetTotalRawValue();
                newEntry.MaxStack = prefab.MaxStackCount;
                newEntry.Weight = prefab.UnitSelfWeight; // [v2.2.2]
                newEntry.Description = prefab.Description ??
""; // [v1.2.0] 物品敘述
            }
            catch (Exception e)
            {
                Log($"[API] 抓取 {id} 靜態資料失敗: {e.Message}");
                newEntry.Tags = new List<string>();
                newEntry.Description = "";
            }

            // 5. 存入「記憶體帳本」 
            a_MasterLedger[id] = newEntry;
            // 6. 建立「反向索引」
            if (newEntry.GoldenID != "Unknown" && !newEntry.GoldenID.Contains("No ModInfo") && !newEntry.GoldenID.Contains("Ghost Mod") && !newEntry.GoldenID.Contains("Type 1") && !a_ReverseLedger_Golden.ContainsKey($"{newEntry.GoldenID}:{newEntry.ItemNameKey}"))
                a_ReverseLedger_Golden[$"{newEntry.GoldenID}:{newEntry.ItemNameKey}"] = id;
            if (newEntry.SilverID != "Unknown" && newEntry.SilverID != "BaseGame" && !newEntry.SilverID.StartsWith("ItemStatsSystem") && !newEntry.SilverID.Contains("(Clone)") && !a_ReverseLedger_Silver.ContainsKey($"{newEntry.SilverID}:{newEntry.ItemNameKey}"))
                a_ReverseLedger_Silver[$"{newEntry.SilverID}:{newEntry.ItemNameKey}"] = id;
            if (newEntry.BronzeID != "遊戲本體" && !newEntry.BronzeID.Contains("No ModInfo") && !newEntry.BronzeID.Contains("Ghost Mod") && !newEntry.BronzeID.Contains("Type 1") && !a_ReverseLedger_Bronze.ContainsKey($"{newEntry.BronzeID}:{newEntry.ItemNameKey}"))
                a_ReverseLedger_Bronze[$"{newEntry.BronzeID}:{newEntry.ItemNameKey}"] = id;
        }

        // ==================================================
        // [Phase C] 配方掃描 (v1.3.3)
        // ==================================================

        /// <summary>
        /// [v1.3.7] Phase C: 掃描所有「製作配方」
        /// </summary>
        private static IEnumerator ScanCraftingFormulas()
        {
            Log("Phase C (配方掃描) 啟動。");
            ShowWarning("[DuckovCoreAPI] 正在啟動 Phase C (配方掃描)...");

            // 必須在 Phase B (isDatabaseReady) 完成後才能執行
            // 因為我們需要 a_SavedLedger 來反查 ItemNameKey
            if (!isDatabaseReady || a_SavedLedger == null)
            {
                ShowError("[DuckovCoreAPI] Phase C 致命錯誤：Phase B (物品掃描) 尚未完成！無法建立配方圖鑑！");
                isRecipeReady = false; // 確保訊號是 false
                yield break;
            }

            // [v1.3.3 修正] 建立一個 TypeID -> ItemNameKey 的快速查表
            Dictionary<int, string> idToKeyLookup = new Dictionary<int, string>();
            try
            {
                foreach (var kvp in a_SavedLedger)
                {
                    if (!idToKeyLookup.ContainsKey(kvp.Key))
                    {

                        idToKeyLookup.Add(kvp.Key, kvp.Value.ItemNameKey);
                    }
                }
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] Phase C 致命錯誤：建立 TypeID 查表失敗！ {e.Message}");
                yield break;
            }

            Dictionary<string, RecipeEntry> newRecipeLedger = new Dictionary<string, RecipeEntry>();
            int yieldCount = 0;
            int formulasProcessed = 0;
            bool isDirty_Recipe = false;

            CraftingFormulaCollection? formulaCollection = null;
            try
            {
                formulaCollection = CraftingFormulaCollection.Instance;
                if (formulaCollection == null || formulaCollection.Entries == null)
                {
                    ShowError("[DuckovCoreAPI] Phase C 致命錯誤：CraftingFormulaCollection.Instance 或 Entries 為 null！");
                    yield break;
                }
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] Phase C 致命錯誤 (取得配方 Collection 時)：\n{e.Message}");
                yield break;
            }

            // 遍歷所有配方
            // [v1.3.7 修正] 把 try-catch 移到迴圈內部
            foreach (CraftingFormula formula in formulaCollection.Entries)
            {
                try
                {

                    if (string.IsNullOrEmpty(formula.id)) continue;
                    RecipeEntry entry = new RecipeEntry
                    {
                        FormulaID = formula.id,
                        // [v2.1.0 修正] 抓 tag.name

                        Tags = formula.tags?.Select(tag => tag).ToList() ?? new List<string>(), // [v2.2.1 修正] formula.tags 是 string[]
                        UnlockByDefault = formula.unlockByDefault, // [v1.3.6]
                        Cost = new List<RecipeIngredient>(),
                        Result = new
                    List<RecipeOutput>()
                    };
                    // --- 處理 Cost (材料) ---
                    if (formula.cost.items != null)
                    {
                        foreach (Cost.ItemEntry costItem in formula.cost.items)

                        {
                            if (costItem.id > 0 && costItem.amount > 0)
                            {
                                string itemKey = "Unknown_Key_ID_" +
costItem.id;
                                if (idToKeyLookup.ContainsKey(costItem.id))
                                {
                                    itemKey = idToKeyLookup[costItem.id];
                                }
                                entry.Cost.Add(new RecipeIngredient
                                {

                                    ItemNameKey = itemKey,
                                    Count = (int)costItem.amount
                                });
                            }
                        }
                    }

                    // --- 處理 Result (產物) ---
                    // [v1.3.5 修正] formula.result 是單一 ItemEntry

                    if (formula.result.id > 0 && formula.result.amount > 0)
                    {
                        string itemKey = "Unknown_Key_ID_" + formula.result.id;
                        if (idToKeyLookup.ContainsKey(formula.result.id))
                        {
                            itemKey = idToKeyLookup[formula.result.id];
                        }
                        entry.Result.Add(new RecipeOutput
                        {
                            ItemNameKey = itemKey,

                            Count = (int)formula.result.amount
                        });
                    }

                    // 加入新的配方帳本
                    newRecipeLedger[entry.FormulaID] = entry;
                    formulasProcessed++;
                }
                catch (Exception e)
                {
                    Log($"[Phase C] 處理配方 {formula.id} 時失敗: {e.Message}");
                }

                // [v1.3.7 修正] yield 必須在 try-catch 外面
                yieldCount++;
                if (yieldCount % 20 == 0) yield return null;
            }


            // --- 處理完畢，比對舊帳本 ---
            Log($"Phase C (配方掃描) 完畢。共掃到 {formulasProcessed} 個配方。");
            try
            {
                if (newRecipeLedger.Count != a_RecipeLedger.Count || !newRecipeLedger.Keys.All(a_RecipeLedger.ContainsKey))
                {
                    isDirty_Recipe = true;
                }
                else
                {
                    foreach (var kvp in newRecipeLedger)
                    {

                        if (!a_RecipeLedger.TryGetValue(kvp.Key, out RecipeEntry oldEntry) ||
                                                    oldEntry.Cost.Count != kvp.Value.Cost.Count ||
                                                    oldEntry.Result.Count != kvp.Value.Result.Count)
                        {

                            isDirty_Recipe = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log($"[Phase C] 比對舊配方帳本失敗: {e.Message}。強制儲存。");
                isDirty_Recipe = true;
            }


            if (isDirty_Recipe)
            {
                ShowWarning($"[DuckovCoreAPI] 配方帳本已更新 (共 {formulasProcessed} 筆)，正在儲存...");
                a_RecipeLedger = newRecipeLedger; // 替換掉舊的
                _ = SaveRecipeLedgerAsync(a_RecipeLedger, GetLedgerPath(RECIPE_FILENAME_SAVED));
            }
            else
            {
                ShowWarning("[DuckovCoreAPI] 配方帳本比對完畢，無需更新。");
            }

            isRecipeReady = true;
            // [v1.3.0] 配方聖杯訊號！
            Log("Phase C (配方掃描) 聖杯訊號 (isRecipeReady) 啟動！");
        }


        // ==================================================
        // [Phase D] 屬性掃描 (v2.8.0)
        // ==================================================

        /// <summary>
        /// [v2.8.0 核心] 掃描單一物品的 Stats, Variables, Constants, Effects, Usage
        /// (在 Phase B 遍歷時被呼叫)
        /// </summary>
        private static void ScanAndStoreItemStats(Item item)
        {

            if (item == null) return;
            int typeID = item.TypeID;
            // [v2.2.0] 統一在這裡建立或取出條目
            if (!a_StatsEffectsLedger.TryGetValue(typeID, out var statsEntry))
            {
                statsEntry = new StatsAndEffectsEntry
                {
                    TypeID = typeID,

                    Stats = new List<StatEntry>(),
                    Effects = new List<EffectEntry>()
                };
            }

            // [v2.8.0] 使用 v2.8.0 的掃描邏輯
            // --- 1. 抓取 Stats (屬性) (e.g., 裝備耐久度、背包負重) ---
            ScanForStats(item, ref statsEntry);
            // --- 2. 抓取 Variables (隱藏屬性 e.g., 配件人體工學) ---
            ScanForCustomData(item.Variables, ref statsEntry);
            // --- 3. 抓取 Constants (隱藏屬性 e.g., 口徑) ---
            ScanForCustomData(item.Constants, ref statsEntry);
            // [v2.8.1 修正] 呼叫 GetPropertyValueTextPair() 以掃描遊戲內建屬性 (例如頭盔、裝甲、背包、配件)
            ScanForPropertyValues(item, ref statsEntry);
            // --- 5. 抓取 Effects (靜態效果) [v2.0.0] ---
            ScanForEffects(item, ref statsEntry);
            // --- 6. 抓取 Usage (使用效果 e.g., 圖騰) [v2.5.2] ---
            ScanForUsage(item, ref statsEntry);
            // 只有在真的有抓到東西時才存
            if (statsEntry.Stats.Count > 0 || statsEntry.Effects.Count > 0)
            {
                a_StatsEffectsLedger[typeID] = statsEntry;
            }
        }

        /// <summary>
        /// [v2.8.0] Part 1:
        /// 掃描 item.Stats (e.g., 裝備耐久度、防護等級、背包負重)
        /// </summary>
        private static void ScanForStats(Item item, ref StatsAndEffectsEntry statsEntry)
        {
            if (item.Stats == null) return;
            foreach (Stat stat in item.Stats)
            {
                if (stat == null) continue;
                // [v2.8.0 修正]：
                // 1. 如果 stat.Display 是 true，抓取。
                // 2. 如果 stat.Display 是 false，則檢查是否在白名單 (ATTRIBUTE_WHITELIST) 中。
                if (!stat.Display && !ATTRIBUTE_WHITELIST.Contains(stat.Key))
                {

                    continue;
                }

                // [v2.2.0] 檢查重複
                if (statsEntry.Stats.Any(s => s.Key == stat.Key)) continue;
                statsEntry.Stats.Add(new StatEntry
                {
                    Key = stat.Key,
                    DisplayNameKey = stat.DisplayNameKey, // [v2.1.0] 
                    BaseValue = stat.BaseValue,

                    Value = stat.Value,
                    DataType = CustomDataType.Float // Stats 預設為 Float
                });
            }
        }

        /// <summary>
        /// [v2.8.0] Part 2:
        /// 掃描 item.Variables / item.Constants (e.g., 配件人體工學、口徑)
        /// </summary>
        private static void ScanForCustomData(CustomDataCollection dataCollection, ref StatsAndEffectsEntry statsEntry)
        {
            if (dataCollection == null) return;
            foreach (CustomData data in dataCollection)
            {
                if (data == null) continue;
                // [v2.8.0 修正]：
                // 1. 如果 data.Display 是 true，抓取。
                // 2. 如果 data.Display 是 false，則檢查是否在白名單 (ATTRIBUTE_WHITELIST) 中。
                if (!data.Display && !ATTRIBUTE_WHITELIST.Contains(data.Key))
                {

                    continue;
                }

                // [v2.2.0] 檢查重複
                if (statsEntry.Stats.Any(s => s.Key == data.Key)) continue;
                CustomDataType type = data.DataType;
                float floatVal = (type == CustomDataType.Float) ? data.GetFloat() : 0;
                string stringVal = (type == CustomDataType.String) ? data.GetString() : null;
                statsEntry.Stats.Add(new StatEntry
                {
                    Key = data.Key,
                    DisplayNameKey = data.DisplayName,
                    BaseValue = floatVal,

                    Value = floatVal,
                    StringValue = stringVal, // [v2.2.0] 
                    DataType = type
                });
            }
        }

        /// <summary>
        /// [v2.8.0] 移除了 ScanForContainerStats (邏輯已合併)
        /// </summary>
        // private static void ScanForContainerStats(Item item, ref StatsAndEffectsEntry statsEntry) { ... }


        /// <summary>
        /// [v2.5.2] Part 3:
        /// 掃描 item.Effects (靜態效果)

        /// </summary>
        private static void ScanForEffects(Item item, ref StatsAndEffectsEntry statsEntry)
        {
            if (item.Effects == null) return;
            foreach (Effect effect in item.Effects)
            {
                if (effect == null || !effect.Display) continue;
                statsEntry.Effects.Add(new EffectEntry
                {
                    DisplayNameKey = effect.name, // Effect 的 name 欄位
                    DescriptionKey = effect.Description, // Description 欄位
                    Type = "Effect"

                });
            }
        }

        /// <summary>
        /// [v2.5.2] Part 4:
        /// 掃描 item.UsageUtilities (使用效果 e.g., 圖騰)
        /// </summary>
        private static void ScanForUsage(Item item, ref StatsAndEffectsEntry statsEntry)
        {
            // [v2.5.2 修正]：抓取 UsageUtilities (使用效果)

            UsageUtilities usage = item.UsageUtilities;
            if (usage == null || usage.behaviors == null) return;
            foreach (UsageBehavior behavior in usage.behaviors)
            {
                // [v2.2.1 修正] 
                if (behavior != null && behavior.DisplaySettings.display && !string.IsNullOrEmpty(behavior.DisplaySettings.Description))
                {
                    statsEntry.Effects.Add(new EffectEntry

                    {
                        DisplayNameKey = behavior.DisplaySettings.Description, // [v2.2.1] 
                        DescriptionKey = behavior.DisplaySettings.Description,
                        Type = "Usage"

                    });
                }
            }
        }
        /// <summary>
        /// [v2.8.1] (Part 5) 抓取 `item.GetPropertyValueTextPair()` 提供的所有屬性。
        /// 這是為了補足 `ScanForStats` 和 `ScanForCustomData` 遺漏的內建屬性 (e.g., 頭盔、裝甲)。

        /// </summary>
        private static void ScanForPropertyValues(Item item, ref StatsAndEffectsEntry statsEntry)
        {
            try
            {

                // 呼叫遊戲內建的 GetPropertyValueTextPair() 函數
                List<ValueTuple<string, string, Polarity>> properties = item.GetPropertyValueTextPair();
                if (properties == null) return;

                foreach (var prop in properties)
                {
                    string key = prop.Item1;
                    string stringValue = prop.Item2;

                    // 邏輯：兩個都空就跳過
                    if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(stringValue)) continue;
                    // 邏輯：如果 key 是空的，就用 value 當 key
                    if (string.IsNullOrEmpty(key)) key = stringValue;
                    if (string.IsNullOrEmpty(key)) continue; // 再次檢查 (如果 value 也是空的)

                    // 關鍵：我們只添加「還沒被抓到」的屬性
                    // (ScanForStats/ScanForCustomData 抓到的優先，避免重複)
                    if (statsEntry.Stats.Any(s => s.Key == key)) continue;
                    // 存成「字串」類型的屬性
                    // (下游 Mod 應能處理 String 類型的屬性)
                    statsEntry.Stats.Add(new StatEntry
                    {
                        Key = key,

                        DisplayNameKey = key, // (沒有 DisplayNameKey，直接使用 Key 作為回傳)
                        BaseValue = 0,
                        Value = 0,
                        StringValue =
stringValue,
                        DataType = CustomDataType.String // 標記為 String 類型
                    });
                }
            }
            catch (Exception e)
            {
                // (如果遊戲更新，此函數簽名可能改變)
                Log($"[v2.8.1] ScanForPropertyValues 失敗 (可能遊戲已更新): {e.Message}");
            }
        }

        // ==================================================
        // [v1.3.1] 輔助函數
        // ==================================================

        /// <summary>
        /// [v1.0.0] 取得 Mod 儲存路徑
        /// </summary>
        private static string GetLedgerPath(string filename)
        {

            string modDataPath = Path.Combine(Application.persistentDataPath, "ModData", HARMONY_ID);
            if (!Directory.Exists(modDataPath))
            {
                Directory.CreateDirectory(modDataPath);
            }
            return Path.Combine(modDataPath, filename);
        }

        // --- v1.3.1 讀寫函數 ---
        private static async void LoadLedgerAsync()
        {
            string path = GetLedgerPath(LEDGER_FILENAME_SAVED);
            if (!File.Exists(path))
            {
                isFirstRun_Saved = true;
                a_SavedLedger = new Dictionary<int, LedgerEntry>();
                Log("找不到歷史帳本，標記為首次運行。");
                isLedgerReady_Saved = true;
                return;
            }

            try
            {
                string json = await Task.Run(() => File.ReadAllText(path));
                var ledger = await Task.Run(() => JsonConvert.DeserializeObject<Dictionary<int, LedgerEntry>>(json));
                if (ledger == null) throw new Exception("反序列化失敗 (歷史帳本)。");
                a_SavedLedger = ledger;
                isFirstRun_Saved = false;
                Log("成功讀取 " + a_SavedLedger.Count + " 筆歷史記錄。");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 警告：\n讀取 historical 帳本失敗！\n{e.Message}\n本次啟動將視為首次運行。");
                isFirstRun_Saved = true;
                a_SavedLedger = new Dictionary<int, LedgerEntry>();
            }
            finally
            {
                isLedgerReady_Saved = true;
            }
        }

        private static async Task LoadWorkshopCacheAsync()
        {
            string path = GetLedgerPath(WORKSHOP_CACHE_FILENAME);
            if (!File.Exists(path))
            {
                a_WorkshopCache = new Dictionary<string, ModInfoCopy>();
                Log("找不到 Workshop 快取，標記為首次掃描。");
                return;
            }

            try
            {
                string json = await Task.Run(() => File.ReadAllText(path));
                var cache = await Task.Run(() => JsonConvert.DeserializeObject<Dictionary<string, ModInfoCopy>>(json));
                if (cache == null) throw new Exception("反序列化失敗 (Workshop 快取)。");
                a_WorkshopCache = cache;
                Log("成功讀取 " + a_WorkshopCache.Count + " 筆 Workshop 快取記錄。");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 警告：\n讀取 Workshop 快取失敗！\n{e.Message}\n本次啟動將視為首次掃描。");
                a_WorkshopCache = new Dictionary<string, ModInfoCopy>();
            }
        }

        private static async Task SaveWorkshopCacheAsync()
        {
            string path = GetLedgerPath(WORKSHOP_CACHE_FILENAME);
            string tempPath = path + ".tmp";
            try
            {
                Log("正在背景儲存(Workshop 快取)...");
                string json = await Task.Run(() => JsonConvert.SerializeObject(a_WorkshopCache, Formatting.Indented));
                await Task.Run(() => File.WriteAllText(tempPath, json));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
                Log("成功儲存 " + a_WorkshopCache.Count + " 筆(Workshop 快取)。");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\n儲存(Workshop 快取)失敗！\n{e.Message}");
            }
        }

        /// <summary>
        /// [v1.1.5 修正] 儲存帳本 (非同步)
        /// </summary>
        private static async Task SaveLedgerAsync(Dictionary<int, LedgerEntry> ledger, string path)
        {
            string tempPath = path + ".tmp";
            try
            {
                ShowWarning("[DuckovCoreAPI] 正在背景儲存(歷史)帳本...");
                string json = await Task.Run(() => JsonConvert.SerializeObject(ledger, Formatting.Indented));
                await Task.Run(() => File.WriteAllText(tempPath, json));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
                Log("成功儲存 " + ledger.Count + " 筆(歷史)帳本。");
                ShowWarning("[DuckovCoreAPI] 歷史帳本儲存完畢！");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\n儲存(歷史)帳本失敗！\n{e.Message}");
            }
        }

        /// <summary>
        /// [v1.3.0 新增] 儲存配方帳本
        /// </summary>
        private static async Task SaveRecipeLedgerAsync(Dictionary<string, RecipeEntry> ledger, string path)
        {
            string tempPath = path + ".tmp";
            try
            {
                ShowWarning("[DuckovCoreAPI] 正在背景儲存(配方)帳本...");
                string json = await Task.Run(() => JsonConvert.SerializeObject(ledger, Formatting.Indented));
                await Task.Run(() => File.WriteAllText(tempPath, json));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
                Log("成功儲存 " + ledger.Count + " 筆(配方)帳本。");
                ShowWarning("[DuckovCoreAPI] 配方帳本儲存完畢！");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\n儲存(配方)帳本失敗！\n{e.Message}");
            }
        }

        /// <summary>
        /// [v2.0.0 新增] 儲存屬性帳本
        /// </summary>
        private static async Task SaveStatsEffectsLedgerAsync(Dictionary<int, StatsAndEffectsEntry> ledger, string path)
        {
            string tempPath = path + ".tmp";
            try
            {
                ShowWarning("[DuckovCoreAPI] 正在背景儲存(屬性/效果)帳本...");
                string json = await Task.Run(() => JsonConvert.SerializeObject(ledger, Formatting.Indented));
                await Task.Run(() => File.WriteAllText(tempPath, json));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
                Log("成功儲存 " + ledger.Count + " 筆(屬性/效果)帳本。");
                ShowWarning("[DuckovCoreAPI] 屬性/效果帳本儲存完畢！");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 嚴重錯誤：\n儲存(屬性/效果)帳本失敗！\n{e.Message}");
            }
        }

        /// <summary>
        /// [v1.3.0 新增] 讀取配方帳本
        /// </summary>
        private static async void LoadRecipeLedgerAsync()
        {
            string path = GetLedgerPath(RECIPE_FILENAME_SAVED);
            if (!File.Exists(path))
            {
                a_RecipeLedger = new Dictionary<string, RecipeEntry>();
                Log("找不到配方帳本，建立新的。");
                return;
            }

            try
            {
                string json = await Task.Run(() => File.ReadAllText(path));
                var ledger = await Task.Run(() => JsonConvert.DeserializeObject<Dictionary<string, RecipeEntry>>(json));
                if (ledger == null) throw new Exception("反序列化失敗 (配方帳本)。");
                a_RecipeLedger = ledger;
                Log("成功讀取 " + a_RecipeLedger.Count + " 筆配方記錄。");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 警告：\n讀取配方帳本失敗！\n{e.Message}");
                a_RecipeLedger = new Dictionary<string, RecipeEntry>();
            }
        }

        /// <summary>
        /// [v2.0.0 新增] 讀取屬性帳本
        /// </summary>
        private static async void LoadStatsEffectsLedgerAsync()
        {
            string path = GetLedgerPath(STATS_FILENAME_SAVED);
            if (!File.Exists(path))
            {
                a_StatsEffectsLedger = new Dictionary<int, StatsAndEffectsEntry>();
                Log("找不到屬性帳本，建立新的。");
                return;
            }

            try
            {
                string json = await Task.Run(() => File.ReadAllText(path));
                var ledger = await Task.Run(() => JsonConvert.DeserializeObject<Dictionary<int, StatsAndEffectsEntry>>(json));
                if (ledger == null) throw new Exception("反序列化失敗 (屬性帳本)。");
                a_StatsEffectsLedger = ledger;
                Log("成功讀取 " + a_StatsEffectsLedger.Count + " 筆屬性記錄。");
            }
            catch (Exception e)
            {
                ShowError($"[DuckovCoreAPI] 警告：\n讀取屬性帳本失敗！\n{e.Message}");
                a_StatsEffectsLedger = new Dictionary<int, StatsAndEffectsEntry>();
            }
        }

        // --- v1.3.1 Log 函數 (v2.4.0 升級) ---
        internal static void Log(string message)
        {
            UnityEngine.Debug.Log($"[DuckovCoreAPI] {message}");
        }

        // [v2.4.0] 
        public static void ShowError(string message, float duration = 10f)
        {
            UnityEngine.Debug.LogError($"[DuckovCoreAPI] {message}");
            if (!isUIReady)
            {
                // [v2.8.1 修正] 修正參數順序
                uiMessageQueue_v2.Add((message, Time.time, true, duration));
                return;
            }
            try
            {
                CoreUI.AddMessage(message, true, duration);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[DuckovCoreAPI] CoreUI.AddMessage 呼叫失敗: {e.Message}");
            }
        }

        // [v2.4.0] 
        public static void ShowWarning(string message, float duration = 10f)
        {
            UnityEngine.Debug.LogWarning($"[DuckovCoreAPI] {message}");
            if (!isUIReady)
            {
                // [v2.8.1 修正] 修正參數順序
                uiMessageQueue_v2.Add((message, Time.time, false, duration));
                return;
            }
            try
            {
                CoreUI.AddMessage(message, false, duration);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[DuckovCoreAPI] CoreUI.AddMessage 呼叫失敗: {e.Message}");
            }
        }
    } // [v1.3.1 修正] ModBehaviour 類別 結束點

    // ==================================================
    // [v1.0.0] UI 提示 (v2.4.0 升級)
    // ==================================================
    public class CoreUI : MonoBehaviour
    {
        // [v2.4.0] UI 訊息佇列
        private static List<(string message, float timestamp, bool isError, float duration)> activeMessages_v2 = new List<(string, float, bool, float)>();
        void Awake()
        {
            ModBehaviour.isUIReady = true;
            foreach (var (msg, timestamp, isError, duration) in ModBehaviour.uiMessageQueue_v2)
            {
                AddMessage(msg, isError, duration, timestamp);
                // 
            }
            ModBehaviour.uiMessageQueue_v2.Clear();
        }

        // [v2.4.0] 
        public static void AddMessage(string message, bool isError, float duration = 10f, float startTime = -1f)
        {
            if (ModBehaviour.instance != null)
            {
                // [v2.5.1] 

                activeMessages_v2.Add((message, (startTime == -1f) ? Time.time : startTime, isError, duration));
            }
            else
            {
                // [v2.5.1] 
                ModBehaviour.uiMessageQueue_v2.Add((message, (startTime == -1f) ? Time.time : startTime, isError, duration));
            }
        }

        void OnGUI()
        {
            if (activeMessages_v2.Count == 0) return;
            // [v2.4.0] 
            activeMessages_v2.RemoveAll(msg => Time.time - msg.timestamp > msg.duration);
            if (activeMessages_v2.Count == 0) return;

            float yPos = Screen.height - 40;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.alignment = TextAnchor.MiddleLeft;
            style.fontSize = 28;
            style.richText = true;

            GUIStyle shadowStyle = new GUIStyle(style);
            shadowStyle.normal.textColor = Color.black;
            shadowStyle.alignment = TextAnchor.MiddleLeft;
            shadowStyle.richText = true;
            int count = 0;
            for (int i = activeMessages_v2.Count - 1; i >= 0; i--)
            {
                if (count >= 5) break;
                try
                {
                    var (message, timestamp, isError, duration) = activeMessages_v2[i];
                    // [v2.4.0] 
                    float alpha = 1.0f;
                    float timeRemaining = (timestamp + duration) - Time.time;
                    if (timeRemaining < 1.0f) // 
                    {
                        alpha = Mathf.Clamp01(timeRemaining);
                    }

                    if (alpha <= 0) continue;
                    if (isError)
                    {
                        style.normal.textColor = new Color(1, 0.6f, 0.6f, alpha);
                    }
                    else
                    {
                        style.normal.textColor = new Color(1, 1, 1, alpha);
                    }
                    shadowStyle.normal.textColor = new Color(0, 0, 0, alpha);
                    Rect rect = new Rect(12, yPos - 35, Screen.width / 1.5f, 40);
                    GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), message, shadowStyle);
                    GUI.Label(rect, message, style);

                    yPos -= 35;
                    count++;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError($"[DuckovCoreAPI] OnGUI 繪製時發生錯誤: {e.Message}");
                }
            }
        }
    }


    // ==================================================
    // [v1.0.0] Phase 1 Harmony 攔截器
    // ==================================================
    [HarmonyPatch(typeof(ItemAssetsCollection), "AddDynamicEntry", new Type[] { typeof(Item) })]
    class Patch_ItemAssetsCollection_Intercept
    {
        // 綁定 Postfix (在 AddDynamicEntry 執行「之後」)
        static void Postfix(Item prefab)
        {

            if (prefab == null || string.IsNullOrEmpty(prefab.DisplayNameRaw))
            {
                ModBehaviour.Log($"[v1.0.0] Phase 1 攔截器：抓到一個 null prefab 或 null Key，跳過！");
                return;
            }

            try
            {
                if (ModBehaviour.a_Type1_Source_Map.ContainsKey(prefab.DisplayNameRaw))
                {
                    return;
                }

                // 1. 建立 StackTrace
                StackTrace stackTrace = new StackTrace();
                string? callingAssemblyPath = null;

                // 2. 遍歷 StackTrace，找出「是誰呼叫我的」
                foreach (StackFrame frame in stackTrace.GetFrames())
                {
                    MethodBase?
method = frame.GetMethod();
                    if (method == null) continue;

                    string assemblyFileName = Path.GetFileName(method.Module.Assembly.Location);
                    // 3. 檢查「黑名單」
                    if (ModBehaviour.a_StackTrace_IgnoreList.Contains(assemblyFileName))
                    {
                        continue;
                    }

                    // 4. 抓到了！
                    callingAssemblyPath = method.Module.Assembly.Location;
                    break;
                }

                // 5. 登記
                if (callingAssemblyPath != null)
                {
                    ModBehaviour.a_Type1_Source_Map[prefab.DisplayNameRaw] = callingAssemblyPath;
                }
            }
            catch (Exception e)
            {
                ModBehaviour.ShowError($"[v1.0.0] Phase 1 攔截器 發生致命錯誤：\n{e.Message}\nType 1 物品 {prefab.DisplayNameRaw} 可能 遺失！");
            }
        }
    }

    // ==================================================
    // [v2.2.1] API 公開函數 (Public API)
    // ==================================================
    public partial class ModBehaviour
    {
        /// <summary>
        /// [API v1.0.0] 檢查「物品來源」資料庫是否已準備就緒。
        /// (建議搭配 Coroutine 或 Update 檢查)
        /// </summary>
        /// <returns>如果 Phase B 掃描完畢則回傳 true</returns>

        public static bool IsDatabaseReady()
        {
            if (!isDatabaseReady && !hasWarnedLedgerNotReady)
            {
                hasWarnedLedgerNotReady = true;
                Log("API 警告：有 Mod 嘗試在 IsDatabaseReady=false 時存取帳本。");
            }
            return isDatabaseReady;
        }

        /// <summary>
        /// [API v1.3.0] 檢查「配方」資料庫是否已準備就緒。
        /// (建議搭配 Coroutine 或 Update 檢查)
        /// </summary>
        /// <returns>如果 Phase C 掃描完畢則回傳 true</returns>
        public static bool IsRecipeReady()
        {
            return isRecipeReady;
        }

        /// <summary>
        /// [API v2.0.0] 檢查「屬性/效果」資料庫是否已準備就緒。
        /// (建議搭配 Coroutine 或 Update 檢查)
        /// </summary>
        /// <returns>如果 Phase B 掃描完畢則回傳 true</returns>
        public static bool IsStatsEffectsReady()
        {
            return isStatsEffectsReady;
        }

        /// <summary>
        /// [API v1.0.0] (核心) 透過 TypeID 取得物品的「來源帳本條目」。
        /// </summary>
        /// <param name="typeID">物品的 TypeID</param>
        /// <param name="entry">成功時，回傳的 LedgerEntry 結構</param>
        /// <returns>如果成功在帳本中找到該 ID 則回傳 true</returns>
        public static bool GetEntry(int typeID, out LedgerEntry entry)
        {

            if (!isDatabaseReady)
            {
                entry = default(LedgerEntry);
                return false;
            }
            return a_SavedLedger.TryGetValue(typeID, out entry);
        }

        /// <summary>
        /// [API v1.0.0] 取得「完整」的物品來源圖鑑。
        /// (警告：回傳的是副本，請勿頻繁呼叫)
        /// </summary>
        /// <returns>一個包含所有物品來源資料的字典</returns>
        public static Dictionary<int, LedgerEntry> GetMasterLedgerCopy()
        {
            return new Dictionary<int, LedgerEntry>(a_SavedLedger);
        }

        /// <summary>
        /// [API v1.0.0] (反向查詢) 透過 Mod 的 Steam ID (GoldenID) 和物品的 ItemNameKey 取得 TypeID。
        /// </summary>
        /// <param name="goldenID">Mod 的 Steam ID (e.g., "283748374")</param>
        /// <param name="itemNameKey">物品的 ItemNameKey (e.g., "accessory.sliencer001")</param>
        /// <param name="typeID">成功時，回傳的 TypeID</param>
        /// <returns>如果成功在反向索引中找到該組合則回傳 true</returns>
        public static bool GetTypeID(string
goldenID, string itemNameKey, out int typeID)
        {
            if (!isDatabaseReady)
            {
                typeID = -1;
                return false;
            }
            return a_ReverseLedger_Golden.TryGetValue($"{goldenID}:{itemNameKey}", out typeID);
        }

        /// <summary>
        /// [API v1.0.0] (反向查詢) 透過 Mod 的 DLL 名稱 (SilverID) 和物品的 ItemNameKey 取得 TypeID。
        /// </summary>
        /// <param name="silverID">Mod 的 DLL 名稱 (e.g., "GunsGalore.dll")</param>
        /// <param name="itemNameKey">物品的 ItemNameKey (e.g., "accessory.sliencer001")</param>
        /// <param name="typeID">成功時，回傳的 TypeID</param>
        /// <returns>如果成功在反向索引中找到該組合則回傳 true</returns>
        public static bool GetTypeIDBySilver(string
silverID, string itemNameKey, out int typeID)
        {
            if (!isDatabaseReady)
            {
                typeID = -1;
                return false;
            }
            return a_ReverseLedger_Silver.TryGetValue($"{silverID}:{itemNameKey}", out typeID);
        }

        /// <summary>
        /// [API v1.0.0] (反向查詢) 透過 Mod 的顯示名稱 (BronzeID) 和物品的 ItemNameKey 取得 TypeID。
        /// (注意：顯示名稱可能重複，不保證準確)
        /// </summary>
        /// <param name="bronzeID">Mod 的顯示名稱 (e.g., "Guns Galore Mod")</param>
        /// <param name="itemNameKey">物品的 ItemNameKey (e.g., "accessory.sliencer001")</param>
        /// <param name="typeID">成功時，回傳的 TypeID</param>
        /// <returns>如果成功在反向索引中找到該組合則回傳 true</returns>

        public static bool GetTypeIDByBronze(string bronzeID, string itemNameKey, out int typeID)
        {
            if (!isDatabaseReady)
            {
                typeID = -1;
                return false;
            }
            return a_ReverseLedger_Bronze.TryGetValue($"{bronzeID}:{itemNameKey}", out typeID);
        }

        /// <summary>
        /// [API v1.3.0] (核心) 透過配方 ID (FormulaID) 取得「配方帳本條目」。
        /// </summary>
        /// <param name="formulaID">配方的 ID (e.g., "Craft_Weapon_AK47")</param>
        /// <param name="entry">成功時，回傳的 RecipeEntry 結構</param>
        /// <returns>如果成功在帳本中找到該 ID 則回傳 true</returns>
        public static bool GetRecipe(string formulaID, out RecipeEntry entry)
        {

            if (!isRecipeReady)
            {
                entry = default(RecipeEntry);
                return false;
            }
            return a_RecipeLedger.TryGetValue(formulaID, out entry);
        }

        /// <summary>
        /// [API v1.3.0] 取得「完整」的配方圖鑑。
        /// (警告：回傳的是副本，請勿頻繁呼叫)
        /// </summary>
        /// <returns>一個包含所有配方資料的字典</returns>
        public static Dictionary<string, RecipeEntry> GetRecipeLedgerCopy()
        {
            return new Dictionary<string, RecipeEntry>(a_RecipeLedger);
        }

        /// <summary>
        /// [API v2.0.0] (核心) 透過 TypeID 取得物品的「屬性/效果帳本條目」。
        /// </summary>
        /// <param name="typeID">物品的 TypeID</param>
        /// <param name="entry">成功時，回傳的 StatsAndEffectsEntry 結構</param>
        /// <returns>如果成功在帳本中找到該 ID 則回傳 true</Vreturns>
        public static bool GetStatsAndEffects(int typeID, out StatsAndEffectsEntry entry)
        {

            if (!isStatsEffectsReady)
            {
                entry = default(StatsAndEffectsEntry);
                return false;
            }
            return a_StatsEffectsLedger.TryGetValue(typeID, out entry);
        }

        /// <summary>
        /// [API v2.0.0] 取得「完整」的屬性/效果圖鑑。
        /// (警告：回傳的是副本，請勿頻繁呼叫)
        /// </summary>
        /// <returns>一個包含所有屬性/效果資料的字典</returns>
        public static Dictionary<int, StatsAndEffectsEntry> GetStatsEffectsLedgerCopy()
        {
            return new Dictionary<int, StatsAndEffectsEntry>(a_StatsEffectsLedger);
        }
    }
}