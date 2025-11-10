/*
 * =================================================================================
 * 專案「鴨嘴獸核心 API」 v2.2.1 (「PM 大哥 v109.0 最終修正」版)
 *
 * 總技術經理 (CTO): 咪咪
 * 專案經理 (PM): 大哥 (王名揚)
 *
 * v2.2.1 更新日誌 (CTO 咪咪 v109.0 再次謝罪):
 * 1. [v2.2.1 致命修正]：修復 v2.2.0 的編譯錯誤：
 * - 修正 L129： 'public class ModBehaviour' 忘記加上 'partial' 關鍵字。
 * - 修正 L1204： 'UsageBehavior.DisplaySettings' 是 struct，移除 '== null' 檢查。
 * - 修正 L1207： 'UsageBehavior.DisplaySettings' 沒有 'DisplayName'，
 * 改為使用 'behavior.DisplaySettings.Description' 當作 Key。
 *
 * v2.2.0 核心強化 (保留):
 * 1. [v2.2.0 核心強化]：
 * - **刪除 Phase D**：已移除獨立的 `ScanStatsAndEffectsCoroutine` 函式。
 * - **刪除 Phase A 的屬性掃描**：`BuildWorkshopCacheAsync` (Phase A) 現在只負責掃描 Mod 來源。
 * - **強化 Phase B**：`ScanAndStoreItemStats` (Phase B 的一部分) 現在是*唯一*的屬性掃描器。
 * - **強化 `ScanAndStoreItemStats`**：它現在會*同時*掃描 `item.Stats` (基礎屬性), `item.Variables` (子彈/配件屬性), 和 `item.Constants` (口徑等)。
 * - **強化 `StatEntry` 結構**：新增 `StringValue` 和 `DataType` 欄位，以支援抓取 "Caliber" 這類的文字屬性。
 * 2. [v2.1.0 修正保留]：
 * - Tag Bug 已修復 (使用 `tag.name` (來自 prefab.Tags) 和 `tag` (來自 formula.tags))。
 * - `using TeamSoda.Duckov.Core;` 已移除。
 * - API 公開函數註釋已補全 (為了開源)。
 * =================================================================================
 */

// 這是你的「工具包」
using HarmonyLib;
using Duckov.Modding;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using ItemStatsSystem.Data;
using Duckov.Utilities; // 為了 CustomDataCollection
using UnityEngine;
using System;
using System.Diagnostics; // 為了 StackTrace
using System.IO;
using System.Reflection; // 為了 StackTrace, AccessTools, BindingFlags
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Text.RegularExpressions; // 為了 Regex
using System.Threading.Tasks;

// [v2.1.0 修正] 移除 TeamSoda.Duckov.Core (大哥說會報錯)
// using TeamSoda.Duckov.Core;
using Duckov.Economy; // 為了 Cost/Price

namespace DuckovCoreAPI
{
    // ==================================================
    // [v2.2.0] API 公開資料結構
    // ==================================================

    /// <summary>
    // [v2.2.0] 儲存單一屬性 (Stat) 的資料
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
        public string ItemNameKey; // 材料物品的 Key
        public int Count;          // 需要的數量
    }

    /// <summary>
    /// [v1.3.0] 配方輸出結果結構 (簡化版)
    /// </summary>
    public struct RecipeOutput
    {
        public string ItemNameKey; // 產出物品的 Key
        public int Count;          // 產出的數量
    }

    /// <summary>
    /// [v1.3.0] 配方圖鑑條目 (API v1.3.0)
    /// </summary>
    public struct RecipeEntry
    {
        public string FormulaID;            // 配方 ID (e.g., "Craft_Weapon_AK47")
        public List<string> Tags;           // 配方標籤
        public bool UnlockByDefault;        // 是否預設解鎖
        public List<RecipeIngredient> Cost; // 製作材料列表
        public List<RecipeOutput> Result;   // 製作結果列表
    }

    /// <summary>
    /// 核心帳本條目 (API v1.3.0)
    /// 這就是 API 掃描後，對外提供的「圖鑑資料」。
    /// </summary>
    public struct LedgerEntry
    {
        // --- 【分類 A：CSI 來源情報】 (v1.0.0 核心) ---
        public int TypeID;          // 物品的 TypeID (e.g., 5000001)
        public string ItemNameKey;     // 物品的內部 Key (e.g., "accessory.sliencer001")
        public string BronzeID;        // 來源 Mod 的「顯示名稱」 (e.g., "Guns Galore Mod")
        public string GoldenID;        // 來源 Mod 的「Steam ID」 (e.g., "283748374")
        public string SilverID;        // 來源 Mod 的「DLL 名稱」 (e.g., "GunsGalore.dll")

        // --- 【分類 B：遊戲 API 靜態情報】 (v1.0.1 強化) ---
        // 這些是順手抓的，讓 API 使用者更方便
        public List<string> Tags;      // [v2.1.0 修正] 物品標籤 (e.g., ["Bullet", "Luxury"])
        public int Quality;         // 稀有度 (int)
        public float Value;           // 基礎價值
        public int MaxStack;        // 最大堆疊

        // --- 【v1.2.0 新功能】 ---
        public string Description;   // 物品敘述
    }

    /// <summary>
    /// 鴨嘴獸核心 API (v2.2.1)
    /// [v2.2.1 修正] 加上 partial
    /// </summary>
    public partial class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // ==================================================
        // 核心變數 (v2.2.0)
        // ==================================================
        private static Harmony? harmonyInstance;
        private const string HARMONY_ID = "com.mingyang.duckovcoreapi";
        private static bool isHarmonyPatched = false; // [v1.1.0] 防止重複 Patch

        // [v1.0.0] Key (LocalizationKey/ItemName) -> ModInfoCopy
        private static Dictionary<string, ModInfoCopy> a_WorkshopCache = new Dictionary<string, ModInfoCopy>();
        private static bool isWorkshopCacheBuilt = false;
        private static bool isWorkshopScanRunning = false;

        // [v1.0.0 核心] Phase 1 (攔截器) 建立的「V12 追蹤帳本」
        internal static Dictionary<string, string> a_Type1_Source_Map = new Dictionary<string, string>();

        // [v1.1.0 核心] 黑名單 (v20 廢案版，移除了 Mod 載入器)
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

        // [v1.0.0] UI 提示
        public static List<(string message, bool isError)> uiMessageQueue = new List<(string, bool isError)>();
        public static bool isUIReady = false;

        // [v1.0.0 核心] 聖杯訊號！
        private static bool isDatabaseReady = false;
        // [v1.3.0 核心] 配方聖杯訊號！
        private static bool isRecipeReady = false;
        // [v2.0.0 核心] 屬性聖杯訊號！
        private static bool isStatsEffectsReady = false;


        internal static ModBehaviour? instance;

        private const string LEDGER_FILENAME_SAVED = "ID_Master_Ledger.json";
        private const string RECIPE_FILENAME_SAVED = "ID_Recipe_Ledger.json"; // [v1.3.0] 新增
        private const string STATS_FILENAME_SAVED = "ID_StatsEffects_Ledger.json"; // [v2.0.0] 新增
        private const string WORKSHOP_CACHE_FILENAME = "Mod_Workshop_Cache.json";

        // [v1.0.0] (Phase A)
        [Serializable]
        public struct ModInfoCopy
        {
            public string path;
            public string name; // .dll name (SilverID)
            public string displayName; // 顯示名稱 (BronzeID)
            public ulong publishedFileId; // Steam ID (GoldenID)
            public long lastWriteTime; // 檢查 .dll 是否更新
        }


        // ==================================================
        // [Phase 3] Mod 啟動與關閉 (v2.2.0)
        // ==================================================

        /// <summary>
        /// [v1.1.0 核心] 遊戲一載入 (Awake)
        /// </summary>
        void Awake()
        {
            Log("CTO 咪咪: v2.2.1 Awake() 啟動！");
            instance = this;

            if (isHarmonyPatched)
            {
                Log("CTO 咪咪: v2.2.1 Awake() 偵測到 Harmony 已安裝，跳過。");
                return;
            }

            if (harmonyInstance == null)
            {
                harmonyInstance = new Harmony(HARMONY_ID);
            }

            // --- Phase 1 (DLL 攔截) ---
            Log("CTO 咪咪: v2.2.1 正在「即刻」安裝 Phase 1 (DLL 攔截器)...");
            try
            {
                var p1_original = AccessTools.Method(typeof(ItemAssetsCollection), "AddDynamicEntry", new Type[] { typeof(Item) });
                if (p1_original == null) throw new Exception("找不到 ItemAssetsCollection.AddDynamicEntry(Item)");
                var p1_postfix = AccessTools.Method(typeof(Patch_ItemAssetsCollection_Intercept), "Postfix");
                harmonyInstance.Patch(p1_original, null, new HarmonyMethod(p1_postfix));
                Log("CTO 咪咪: Phase 1 (DLL StackTrace 攔截器) 竊聽器... 成功！");
                isHarmonyPatched = true;
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\nPhase 1 (DLL 攔截) 綁定失敗！\n{e.Message}\nType 1 (純 DLL) 模組物品將 100% 遺失！");
            }
        }

        /// <summary>
        /// [v1.1.0] Mod 載入器 (OnAfterSetup)
        /// </summary>
        protected override void OnAfterSetup()
        {
            instance = this;

            Log("CTO 咪咪: 正在安裝 v2.2.1 核心 API (真正最終開源版)...");

            // --- Phase UI 建立 ---
            try
            {
                if (GameObject.Find("DuckovCoreAPI_UI") == null)
                {
                    GameObject uiHost = new GameObject("DuckovCoreAPI_UI");
                    uiHost.AddComponent<CoreUI>();
                    UnityEngine.Object.DontDestroyOnLoad(uiHost);
                    Log("CTO 咪咪: Phase UI (OnGUI) 建立完畢！");
                }
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\nPhase UI (OnGUI) 建立失敗！\n{e.Message}");
            }

            // --- Phase B 訂閱 ---
            try
            {
                // (API: events.txt)
                LevelManager.OnAfterLevelInitialized -= OnLevelLoaded_DatabaseScan;
                LevelManager.OnAfterLevelInitialized += OnLevelLoaded_DatabaseScan;
                Log("CTO 咪咪: Phase B (駭客掃描) 鉤子已訂閱。");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\nPhase B (駭客掃描) 訂閱失敗！\n{e.Message}");
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
                    Log("CTO 咪咪: Phase A (Workshop 掃描) 已在執行中，跳過。");
                    return;
                }
                Log("CTO 咪咪: 偵測到 OnAfterSetup，正在啟動 Phase A (延遲掃描 Coroutine)...");
                StartCoroutine(InitializePhaseA_Coroutine());
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\nPhase A (Workshop 掃描) 延遲啟動失敗！\n{e.Message}");
                isWorkshopCacheBuilt = true;
                isWorkshopScanRunning = false;
            }

            Log("PM 大哥，你的「鴨嘴獸核心 API v2.2.1」模組已上線！");
        }

        private IEnumerator InitializePhaseA_Coroutine()
        {
            isWorkshopScanRunning = true;
            Log("CTO 咪咪: Phase A 正在等待 3 秒鐘 (等待遊戲 Mod 載入器)...");
            yield return new WaitForSeconds(3.0f);
            Log("CTO 咪咪: 3 秒延遲完畢，啟動 Phase A (Workshop 掃描)...");
            StartWorkshopScanProcess();
        }

        protected override void OnBeforeDeactivate()
        {
            try
            {
                harmonyInstance?.UnpatchAll(HARMONY_ID);
                isHarmonyPatched = false;
                Log("CTO 咪咪: 竊聽器已全部移除。");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 停用時發生錯誤: {e.Message}");
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
            uiMessageQueue.Clear();
        }

        // ==================================================
        // [Phase A] Workshop 掃描 (v2.2.0)
        // ==================================================

        /// <summary>
        /// [v1.1.2] 「掃全家(爬路徑)」版 Phase A
        /// </summary>
        private static async void StartWorkshopScanProcess()
        {
            try
            {
                // 1. 讀取舊快取
                await LoadWorkshopCacheAsync();

                // 2. [v1.1.2] 取得「所有」Mod 資料夾路徑 (無論是否啟用)
                List<string> allModFoldersToScan = new List<string>();
                string gameWorkshopPath = ""; // "...\3167020"

                try
                {
                    // 2a. 掃描本地 Mods 資料夾
                    string localModsPath = Path.Combine(Application.dataPath, "Mods");
                    if (Directory.Exists(localModsPath))
                    {
                        allModFoldersToScan.AddRange(Directory.GetDirectories(localModsPath));
                        Log($"[Phase A v2.2.1] 掃到 {allModFoldersToScan.Count} 個本地 Mod 資料夾。");
                    }

                    // 2b. [v1.1.2 核心修正] 暴力爬路徑！
                    try
                    {
                        string steamAppsPath = Directory.GetParent(Directory.GetParent(Directory.GetParent(Application.dataPath).FullName).FullName).FullName;
                        gameWorkshopPath = Path.Combine(steamAppsPath, "workshop", "content", "3167020");
                    }
                    catch (Exception e)
                    {
                        Log($"[Phase A v2.2.1] 警告: 爬路徑找 steamapps 失敗: {e.Message}");
                        gameWorkshopPath = "";
                    }

                    // [v1.1.2 修正] 掃描 "3167020" 裡面的「所有」資料夾
                    if (Directory.Exists(gameWorkshopPath))
                    {
                        Log($"[Phase A v2.2.1] 抓到 Workshop 遊戲目錄: {gameWorkshopPath}");
                        var workshopFolders = Directory.GetDirectories(gameWorkshopPath);
                        allModFoldersToScan.AddRange(workshopFolders);
                        Log($"[Phase A v2.2.1] 掃到 {workshopFolders.Length} 個 Workshop Mod 資料夾。");
                    }
                    else
                    {
                        Log("[Phase A v2.2.1] 警告: 找不到 Workshop 遊戲目錄 (你是不是用非 Steam 版？)");
                    }
                }
                catch (Exception e)
                {
                    ShowError($"[鴨嘴獸 API] 嚴重錯誤：\nPhase A (v2.2.1) 掃描資料夾失敗！\n{e.Message}");
                }

                if (allModFoldersToScan.Count == 0)
                {
                    ShowWarning("[鴨嘴獸 API] 找不到任何 Mod 資料夾，跳過 Workshop 掃描。");
                    isWorkshopCacheBuilt = true;
                    isWorkshopScanRunning = false;
                    return;
                }

                a_ModInfo_By_DLL_Path.Clear();

                // 3. [v1.1.3] 暴力掃描所有 info.ini 和 .dll
                List<ModInfoCopy> currentModInfos = new List<ModInfoCopy>();
                foreach (string modFolderPath in allModFoldersToScan.Distinct())
                {
                    try
                    {
                        string infoIniPath = Path.Combine(modFolderPath, "info.ini");
                        if (!File.Exists(infoIniPath)) continue;

                        // 3a. [v1.1.3 修正] 手動解析 info.ini
                        ModInfoCopy infoCopy = ParseInfoIni(infoIniPath, modFolderPath);
                        if (string.IsNullOrEmpty(infoCopy.name))
                        {
                            Log($"[Phase A v2.2.1] 警告: {infoIniPath} 缺少 'name' 欄位 (或 Parse 失敗)，跳過。");
                            continue;
                        }

                        // 3b. 找 .dll
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
                                dllPath = ""; // .dll 不存在
                                lastWriteTime = File.GetLastWriteTime(modFolderPath).Ticks;
                            }
                        }
                        catch { }

                        infoCopy.lastWriteTime = lastWriteTime;
                        currentModInfos.Add(infoCopy);

                        // 3c. 建立 DLL -> ModInfo 的反向對照表
                        if (!string.IsNullOrEmpty(dllPath) && !a_ModInfo_By_DLL_Path.ContainsKey(dllPath))
                        {
                            a_ModInfo_By_DLL_Path.Add(dllPath, infoCopy);
                        }
                    }
                    catch (Exception e)
                    {
                        Log($"[Phase A v2.2.1] 處理資料夾 {Path.GetFileName(modFolderPath)} 失敗: {e.Message}");
                    }
                }

                Log($"[Phase A v2.2.1] 「掃全家」完畢。共找到 {currentModInfos.Count} 個 info.ini，登記了 {a_ModInfo_By_DLL_Path.Count} 個 .dll。");


                // 4. [v1.0.0] 差異比對！(使用 Steam ID)
                ShowWarning("[鴨嘴獸 API] 正在比對 Mod 快取...");

                var oldCacheBySteamID = a_WorkshopCache.Values.ToLookup(m => m.publishedFileId);
                var currentModsBySteamID = currentModInfos
                    .GroupBy(m => m.publishedFileId)
                    .Where(g => g.Key > 0) // 0 = 本地 Mod，不比對
                    .ToDictionary(g => g.Key, g => g.First());

                List<ModInfoCopy> modsToScan = new List<ModInfoCopy>();
                bool cacheNeedsUpdate = false;

                // 4a. 檢查新增 / 更新
                foreach (var mod in currentModInfos)
                {
                    if (mod.publishedFileId == 0) // 本地 Mod 永遠掃描
                    {
                        modsToScan.Add(mod);
                        continue;
                    }

                    if (!oldCacheBySteamID.Contains(mod.publishedFileId) || oldCacheBySteamID[mod.publishedFileId].First().lastWriteTime != mod.lastWriteTime)
                    {
                        modsToScan.Add(mod);
                    }
                }

                // 4b. 檢查移除
                foreach (var oldModItems in oldCacheBySteamID)
                {
                    if (oldModItems.Key == 0) continue; // 不移除本地 Mod 快取
                    if (!currentModsBySteamID.ContainsKey(oldModItems.Key))
                    {
                        cacheNeedsUpdate = true;
                        foreach (var item in a_WorkshopCache.Where(kvp => kvp.Value.publishedFileId == oldModItems.Key).ToList())
                        {
                            a_WorkshopCache.Remove(item.Key);
                        }
                    }
                }

                // 5. 執行掃描 (如果需要)
                if (modsToScan.Count > 0)
                {
                    ShowWarning($"[鴨嘴獸 API] 發現 {modsToScan.Count} 個新/更新的 Mod，正在背景掃描 .json...");
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
                    Log("[Phase A v2.2.1] .json 快取比對完畢，無需更新。");
                }

                ShowWarning("[鴨嘴獸 API] Mod 物品索引已就緒！");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\nPhase A (v2.2.1 掃全家) 失敗！\n{e.Message}");
            }
            finally
            {
                isWorkshopCacheBuilt = true;
                isWorkshopScanRunning = false;
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
                    // (API: 大哥 v92.0 範例 -> name = FancyItems)
                    // 我們必須分割 '=' 並 Trim()
                    string[] parts = line.Split(new char[] { '=' }, 2); // 只切一次
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
                Log($"[ParseInfoIni v2.2.1] 解析 {Path.GetFileName(iniPath)} 失敗: {e.Message}");
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
                    // 1. 先把舊的掃掉 (使用 Steam ID 或 Path)
                    foreach (var mod in modsToScan)
                    {
                        // 0 = 本地 Mod，用資料夾路徑當 Key
                        if (mod.publishedFileId == 0)
                        {
                            foreach (var item in a_WorkshopCache.Where(kvp => kvp.Value.path == mod.path).ToList())
                            {
                                a_WorkshopCache.Remove(item.Key);
                            }
                        }
                        else
                        {
                            foreach (var item in a_WorkshopCache.Where(kvp => kvp.Value.publishedFileId == mod.publishedFileId).ToList())
                            {
                                a_WorkshopCache.Remove(item.Key);
                            }
                        }
                    }

                    // 2. 再掃描新的
                    foreach (var modInfo in modsToScan)
                    {
                        Log($"[Phase A] (背景) 正在掃描 Mod (掃全家): {modInfo.displayName} (ID: {modInfo.publishedFileId})");
                        try
                        {
                            List<string> jsonFiles = new List<string>();

                            // 核心修正：掃描 Mod 根目錄 + 「所有」子資料夾！
                            if (Directory.Exists(modInfo.path))
                            {
                                // (API: v20 廢案邏輯 -> SearchOption.AllDirectories)
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

                                    // (API: v20 廢案邏輯 -> 手動 Regex 拔掉 '//' 註解)
                                    string cleanedJsonText = Regex.Replace(jsonText, @"^\s*//.*$", "", RegexOptions.Multiline);
                                    JToken token = JToken.Parse(cleanedJsonText);

                                    if (token is JArray array)
                                    {
                                        foreach (var item in array)
                                        {
                                            // (API: v20 廢案邏輯 -> 抓 Key)
                                            string? itemKey = item["LocalizationKey"]?.ToString() ??
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
                                        string? itemKey = obj["LocalizationKey"]?.ToString() ??
                                                          obj["ItemName"]?.ToString() ??
                                                          obj["DisplayName"]?.ToString();

                                        if (itemKey != null)
                                        {
                                            bool isNew = !a_WorkshopCache.ContainsKey(itemKey);
                                            a_WorkshopCache[itemKey] = modInfo; // 允許覆蓋
                                            if (isNew) itemsFoundInJsons++;

                                            // [v2.2.0 移除] 不再於此處掃描 AmmoProperties
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
                }); // 背景執行緒結束
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\nPhase A (Workshop 掃描) 背景執行緒失敗！\n{e.Message}");
            }
            finally
            {
                Log($"CTO 咪咪: Phase A (JSON 掃描) 完畢。在 {modsToScan.Count} 個 Mod 中找到 {itemsFoundInJsons} 筆新物品。");
                // [v1.1.3 核心修正] 變數現在 100% 在 Scope 內了！
                if (cacheNeedsUpdate)
                {
                    await SaveWorkshopCacheAsync();
                }
            }
        }


        // ==================================================
        // [Phase B] 物品掃描 (v2.2.0)
        // ==================================================

        /// <summary>
        /// [v1.0.0 Phase B] (由 OnAfterLevelInitialized 觸發)
        /// </summary>
        private static void OnLevelLoaded_DatabaseScan()
        {
            try
            {
                isUIReady = true;
                if (uiMessageQueue.Count > 0)
                {
                    Log($"CTO 咪咪: 正在清空 {uiMessageQueue.Count} 筆 UI 緩衝...");
                }
            }
            catch (Exception e)
            {
                Log($"[DuckovCoreAPI] UI 緩衝區清空失敗: {e.Message}");
            }

            if (isMerging) return;

            if (instance != null)
            {
                Log("CTO 咪咪: Phase B (駭客掃描) 啟動。");
                instance.StartCoroutine(DatabaseScanCoroutine());
            }
            else
            {
                ShowError("[鴨嘴獸 API] 嚴重錯誤：ModBehaviour 實例為 null！無法啟動 Coroutine 掃描！");
            }
        }

        /// <summary>
        /// [v2.2.0] CSI 核心掃描器 (Phase B + Phase C)
        /// </summary>
        private static IEnumerator DatabaseScanCoroutine()
        {
            isMerging = true;
            isDatabaseReady = false;
            isRecipeReady = false; // [v1.3.0] 重置配方聖杯訊號
            isStatsEffectsReady = false; // [v2.0.0] 重置屬性聖杯訊號

            // 1. [v1.0.0] 等待 Phase A 和 (舊)帳本
            if (!isWorkshopCacheBuilt || !isLedgerReady_Saved)
            {
                ShowWarning("[鴨嘴獸 API] 正在等待 Mod 索引或歷史帳本...");
                yield return new WaitUntil(() => isWorkshopCacheBuilt && isLedgerReady_Saved);
            }
            // 2. [v1.0.0] 檢查 ItemAssetsCollection.Instance
            if (ItemAssetsCollection.Instance == null)
            {
                ShowError("[鴨嘴獸 API] 駭客掃描 Part 1 發生致命錯誤：ItemAssetsCollection.Instance 是 null！CSI 失敗！");
                isMerging = false;
                yield break;
            }
            // 3. [v1.1.4] 等待 dynamicDic (90秒)
            FieldInfo? field = AccessTools.Field(typeof(ItemAssetsCollection), "dynamicDic");
            if (field == null)
            {
                ShowError("[鴨嘴獸 API] 駭客掃描 Part 2 發生致命錯誤：找不到 'dynamicDic' 欄位！CSI 失敗！");
                isMerging = false;
                yield break;
            }
            ShowWarning("[鴨嘴獸 API] 正在等待遊戲本體 Mod 載入器 (dynamicDic)...");
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
            Dictionary<int, ItemAssetsCollection.DynamicEntry>? dynamicDatabase = dynamicDicValue as Dictionary<int, ItemAssetsCollection.DynamicEntry>;
            if (dynamicDatabase == null)
            {
                ShowError($"[鴨嘴獸 API] 駭客掃描 Part 2 發生致命錯誤：等待 90 秒後 'dynamicDic' 還是 null！");
                isMerging = false;
                yield break;
            }

            // 4. [Phase B] 執行物品駭客掃描 (CSI)
            ShowWarning("[鴨嘴獸 API] 遊戲 Mod 載入完畢！正在駭入物品資料庫 (CSI)...");

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

            // --- Part 1: 竊取「遊戲本體」資料庫 (entries) ---
            List<ItemAssetsCollection.Entry>? baseGameEntries = null;
            try
            {
                baseGameEntries = ItemAssetsCollection.Instance.entries;
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 駭客掃描 Part 1 (BaseGame) 發生致命錯誤 (無法取得 entries): {e.Message}");
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
                        Log($"[鴨嘴獸 API] 處理 BaseGame 物品 {entry?.typeID} 失敗: {e.Message}");
                    }
                    yieldCount++;
                    if (yieldCount % 50 == 0) yield return null;
                }
            }

            // --- Part 2: 竊取「所有 Mod」資料庫 (dynamicDic) ---
            foreach (var kvp in dynamicDatabase)
            {
                try
                {
                    ItemAssetsCollection.DynamicEntry? entry = kvp.Value;
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

                    ScanAndStoreItemStats(entry.prefab); // [v2.2.0] 掃描屬性
                }
                catch (Exception e)
                {
                    Log($"[鴨嘴獸 API] 處理 Mod 物品 {kvp.Key} 失敗: {e.Message}");
                }

                yieldCount++;
                if (yieldCount % 50 == 0) yield return null;
            }

            ShowWarning($"[鴨嘴獸 API] 物品掃描完畢，正在比對歷史帳本...");
            yield return null;

            // 5. [v1.0.0] 執行「即時合併」
            int newItems = 0;
            int conflicts = 0;
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

                        // [v2.1.0 修正] 檢查 Tag 是否有變化
                        if (existingEntry.BronzeID != newEntry.BronzeID ||
                            existingEntry.GoldenID != newEntry.GoldenID ||
                            existingEntry.Description != newEntry.Description ||
                            !existingEntry.Tags.SequenceEqual(newEntry.Tags)) // 檢查 Tag
                        {
                            if (existingEntry.GoldenID != "BaseGame" && !existingEntry.BronzeID.Contains("Type 1") && newEntry.GoldenID != existingEntry.GoldenID)
                            {
                                conflicts++;
                            }
                            else
                            {
                                a_SavedLedger[kvp.Key] = newEntry;
                                isDirty_Saved = true;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 帳本合併時發生嚴重錯誤！\n{e.Message}");
            }

            // 6. [v1.0.0] 清理與儲存
            a_MasterLedger.Clear();
            Log($"CTO 咪咪: 成功抓取 {totalItems_Instance} (本體) + {totalItems_Dynamic} (JSON) + {totalItems_Type1} (DLL) = {totalItems_Instance + totalItems_Dynamic + totalItems_Type1} 件物品。");
            Log($"CTO 咪咪: 成功建立 {a_ReverseLedger_Golden.Count} (金) / {a_ReverseLedger_Silver.Count} (銀) / {a_ReverseLedger_Bronze.Count} (銅) 筆反向索引。");

            if (newItems > 0)
            {
                isDirty_Saved = true;
                if (isFirstRun_Saved) { ShowWarning($"[鴨嘴獸 API] 首次運行：建立 {newItems} 筆物品帳本。"); }
                else { ShowWarning($"[鴨嘴獸 API] 合併完畢！發現 {newItems} 個新物品。"); }
            }
            else { ShowWarning("[鴨嘴獸 API] 物品帳本比對完畢，無需更新。"); }
            ShowWarning($"[鴨嘴獸 API] 總共 {a_SavedLedger.Count} 筆物品記錄在案。");
            if (conflicts > 0) { ShowError($"[鴨嘴獸 API] 警告：偵測到 {conflicts} 起 ID 衝突！(策略：保留歷史紀錄)"); }

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
            isStatsEffectsReady = true; // [v2.2.0] 屬性聖杯訊號！
            Log("CTO 咪咪: Phase B 完畢！CSI 完畢！聖杯訊號 (isDatabaseReady & isStatsEffectsReady) 啟動！");

            // 7. [v1.3.0 核心] 啟動 Phase C (配方掃描)
            // 必須在 isDatabaseReady = true 之後，因為 Phase C 需要用 a_SavedLedger 查表
            yield return instance!.StartCoroutine(ScanCraftingFormulas());

            Log("CTO 咪咪: 最終報告：物品/配方/屬性掃描全部完成。");
        }


        /// <summary>
        /// [v2.1.0] 處理 Type 2/3 (JSON) 和 遊戲本體
        /// </summary>
        private static bool ProcessItemPrefab_Json(Item prefab, int id, string type) // type = "BaseGame" or "Mod"
        {
            LedgerEntry newEntry = new LedgerEntry();

            // --- v1.0.0 戶口名簿 ---
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

            // --- v1.2.0 身家調查 ---
            try
            {
                // [v2.1.0 核心修正] 抓 tag.name 而不是 tag.DisplayName
                newEntry.Tags = prefab.Tags.Select(tag => tag.name).ToList(); // <-- 關鍵修正
                newEntry.Quality = (int)prefab.DisplayQuality;
                newEntry.Value = prefab.GetTotalRawValue();
                newEntry.MaxStack = prefab.MaxStackCount;
                newEntry.Description = prefab.Description ?? ""; // [v1.2.0]
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
        /// [v2.1.0] 處理 Type 1 (純 DLL) 和 幽靈 Mod
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
            string? dll_path_from_trace = null;
            if (!a_Type1_Source_Map.TryGetValue(newEntry.ItemNameKey, out dll_path_from_trace))
            {
                // Phase 1 失敗！ (e.g. 91001)
                // 啟用 V11 反射備案
                try { dll_path_from_trace = prefab.GetType().Assembly.Location; } catch { }

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
                        // 這 100% 是遊戲本體 (e.g. ItemStatsSystem.dll)
                        Log($"[v1.1.7] Type 1 降級: V11 反射抓到黑名單 {assemblyFileName}，判定為 遊戲本體。 (ID: {id})");
                        dll_path_from_trace = "BaseGame"; // 標記為 BaseGame
                    }
                    else
                    {
                        Log($"[v1.1.7] Type 1 警告 (ID: {id}): V12 追蹤帳本 找不到 Key '{newEntry.ItemNameKey}'！ 降級為 V11 反射！(Path: {dll_path_from_trace})");
                    }
                }
            }

            // 4. [v1.1.7] 根據 V12/V11 的結果，去 Phase A (掃全家) 建立的「DLL 對照表」查詢
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
                Log($"[v1.1.7] Type 1 處理成功：ID {id} ({newEntry.ItemNameKey}) 來自 {modInfo.displayName}");
            }
            else
            {
                // [v1.1.7] 這就是「幽靈 Mod」！ (Phase 1 抓到了，但 Phase A (掃全家) 還是找不到 info.ini)
                string ghost_dll_name = "Unknown Ghost Mod";
                try
                {
                    ghost_dll_name = Path.GetFileNameWithoutExtension(dll_path_from_trace);
                }
                catch { }

                newEntry.GoldenID = "Unknown (Ghost Mod)";
                newEntry.SilverID = Path.GetFileName(dll_path_from_trace);
                newEntry.BronzeID = ghost_dll_name + " (Type 1)";
                Log($"[v1.1.7] Type 1 (Ghost) 處理成功：ID {id} ({newEntry.ItemNameKey}) 來自幽靈 DLL: {newEntry.SilverID}");
            }

            // --- v1.2.0 身家調查 ---
            try
            {
                // [v2.1.0 核心修正] 抓 tag.name 而不是 tag.DisplayName
                newEntry.Tags = prefab.Tags.Select(tag => tag.name).ToList(); // <-- 關鍵修正
                newEntry.Quality = (int)prefab.DisplayQuality;
                newEntry.Value = prefab.GetTotalRawValue();
                newEntry.MaxStack = prefab.MaxStackCount;
                newEntry.Description = prefab.Description ?? ""; // [v1.2.0]
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
        /// [v1.3.7 咪咪謝罪版] Phase C: 掃描所有「製作配方」
        /// </summary>
        private static IEnumerator ScanCraftingFormulas()
        {
            Log("CTO 咪咪: Phase C (配方掃描) 啟動。");
            ShowWarning("[鴨嘴獸 API] 正在啟動 Phase C (配方掃描)...");

            // 必須在 Phase B (isDatabaseReady) 完成後才能執行
            // 因為我們需要 a_SavedLedger 來反查 ItemNameKey
            if (!isDatabaseReady || a_SavedLedger == null)
            {
                ShowError("[鴨嘴獸 API] Phase C 致命錯誤：Phase B (物品掃描) 尚未完成！無法建立配方圖鑑！");
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
                ShowError($"[鴨嘴獸 API] Phase C 致命錯誤：建立 TypeID 查表失敗！ {e.Message}");
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
                    ShowError("[鴨嘴獸 API] Phase C 致命錯誤：CraftingFormulaCollection.Instance 或 Entries 為 null！");
                    yield break;
                }
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] Phase C 致命錯誤 (取得配方 Collection 時)：\n{e.Message}");
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
                        Tags = formula.tags?.Select(tag => tag).ToList() ?? new List<string>(), // [v2.2.1 修正] formula.tags 是 string[]，不是 Tag[]
                        UnlockByDefault = formula.unlockByDefault, // [v1.3.6] API 檔案證實有
                        Cost = new List<RecipeIngredient>(),
                        Result = new List<RecipeOutput>()
                    };

                    // --- 處理 Cost (材料) ---
                    if (formula.cost.items != null)
                    {
                        foreach (Cost.ItemEntry costItem in formula.cost.items)
                        {
                            if (costItem.id > 0 && costItem.amount > 0)
                            {
                                string itemKey = "Unknown_Key_ID_" + costItem.id;
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
                    // [v1.3.5 修正] formula.result 是單一 ItemEntry，不是陣列
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
            Log($"CTO 咪咪: Phase C (配方掃描) 完畢。共掃到 {formulasProcessed} 個配方。");

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
                ShowWarning($"[鴨嘴獸 API] 配方帳本已更新 (共 {formulasProcessed} 筆)，正在儲存...");
                a_RecipeLedger = newRecipeLedger; // 替換掉舊的
                _ = SaveRecipeLedgerAsync(a_RecipeLedger, GetLedgerPath(RECIPE_FILENAME_SAVED));
            }
            else
            {
                ShowWarning("[鴨嘴獸 API] 配方帳本比對完畢，無需更新。");
            }

            isRecipeReady = true; // 配方聖杯訊號！
            Log("CTO 咪咪: Phase C (配方掃描) 聖杯訊號 (isRecipeReady) 啟動！");
        }


        // ==================================================
        // [Phase D] 屬性掃描 (v2.2.0)
        // ==================================================

        /// <summary>
        /// [v2.2.0 核心] 掃描單一物品的 Stats, Variables, Constants, Effects, Usage
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

            // --- 1. 抓取 Stats (屬性) [v2.0.0] ---
            if (item.Stats != null)
            {
                foreach (Stat stat in item.Stats)
                {
                    if (stat == null || !stat.Display) continue;

                    // [v2.2.0] 檢查重複
                    if (statsEntry.Stats.Any(s => s.Key == stat.Key)) continue;

                    statsEntry.Stats.Add(new StatEntry
                    {
                        Key = stat.Key,
                        DisplayNameKey = stat.DisplayNameKey, // [v2.1.0] 本地化修正
                        BaseValue = stat.BaseValue,
                        Value = stat.Value,
                        DataType = CustomDataType.Float // Stats 系統都是 Float
                    });
                }
            }

            // --- 2. [v2.2.0 核心修正] 抓取 Variables (隱藏屬性 e.g., 子彈) ---
            if (item.Variables != null)
            {
                // (根據 utilities.txt API, item.Variables 是一個 CustomDataCollection)
                foreach (CustomData data in item.Variables)
                {
                    if (data == null) continue; // [v2.2.1 修正] 移除 !data.Display 檢查

                    // [v2.2.0] 檢查重複
                    if (statsEntry.Stats.Any(s => s.Key == data.Key)) continue;

                    // 這就是子彈/配件的屬性！ (e.g., "NewArmorPiercingGain", "DamageMultiplier")
                    statsEntry.Stats.Add(new StatEntry
                    {
                        Key = data.Key,
                        DisplayNameKey = data.DisplayName, // CustomData 裡有 DisplayName
                        BaseValue = data.GetFloat(),       // 假設它們都是 float
                        Value = data.GetFloat(),
                        DataType = CustomDataType.Float // Variables 預設為 Float
                    });
                }
            }

            // --- 3. [v2.2.0 核心修正] 抓取 Constants (隱藏屬性 e.g., 口徑) ---
            if (item.Constants != null)
            {
                foreach (CustomData data in item.Constants)
                {
                    if (data == null) continue; // [v2.2.1 修正] 移除 !data.Display 檢查

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
                        StringValue = stringVal, // [v2.2.0] 儲存字串
                        DataType = type
                    });
                }
            }

            // --- 4. 抓取 Effects (靜態效果) [v2.0.0] ---
            if (item.Effects != null)
            {
                foreach (Effect effect in item.Effects)
                {
                    if (effect == null || !effect.Display) continue;
                    statsEntry.Effects.Add(new EffectEntry
                    {
                        DisplayNameKey = effect.name, // Effect 的 name 通常就是 Key
                        DescriptionKey = effect.Description, // Description 是 Key
                        Type = "Effect"
                    });
                }
            }

            // --- 5. 抓取 Usage (使用效果) [v2.2.1 修正] ---
            UsageUtilities usage = item.GetComponent<UsageUtilities>();
            if (usage != null && usage.behaviors != null)
            {
                foreach (UsageBehavior behavior in usage.behaviors)
                {
                    // [v2.2.1 修正] 
                    // 1. DisplaySettings 是 struct，移除 == null 檢查
                    // 2. 檢查 .display (bool)
                    // 3. DisplayName 不存在，改用 Description
                    if (behavior != null && behavior.DisplaySettings.display && !string.IsNullOrEmpty(behavior.DisplaySettings.Description))
                    {
                        statsEntry.Effects.Add(new EffectEntry
                        {
                            DisplayNameKey = behavior.DisplaySettings.Description, // [v2.2.1] 用 Description 當 Key
                            DescriptionKey = behavior.DisplaySettings.Description,
                            Type = "Usage"
                        });
                    }
                }
            }

            // 只有在真的有抓到東西時才存
            if (statsEntry.Stats.Count > 0 || statsEntry.Effects.Count > 0)
            {
                a_StatsEffectsLedger[typeID] = statsEntry;
            }
        }


        // ==================================================
        // [v1.3.1] 輔助函數 (全部搬進 Class)
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

        // --- v1.3.1 讀寫函數 (搬家) ---
        private static async void LoadLedgerAsync()
        {
            string path = GetLedgerPath(LEDGER_FILENAME_SAVED);
            if (!File.Exists(path))
            {
                isFirstRun_Saved = true;
                a_SavedLedger = new Dictionary<int, LedgerEntry>();
                Log("CTO 咪咪: 找不到歷史帳本，標記為首次運行。");
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
                Log("CTO 咪咪: 成功讀取 " + a_SavedLedger.Count + " 筆歷史記錄。");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 警告：\n讀取 historical 帳本失敗！\n{e.Message}\n本次啟動將視為首次運行。");
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
                Log("CTO 咪咪: 找不到 Workshop 快取，標記為首次掃描。");
                return;
            }

            try
            {
                string json = await Task.Run(() => File.ReadAllText(path));
                var cache = await Task.Run(() => JsonConvert.DeserializeObject<Dictionary<string, ModInfoCopy>>(json));
                if (cache == null) throw new Exception("反序列化失敗 (Workshop 快取)。");
                a_WorkshopCache = cache;
                Log("CTO 咪咪: 成功讀取 " + a_WorkshopCache.Count + " 筆 Workshop 快取記錄。");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 警告：\n讀取 Workshop 快取失敗！\n{e.Message}\n本次啟動將視為首次掃描。");
                a_WorkshopCache = new Dictionary<string, ModInfoCopy>();
            }
        }

        private static async Task SaveWorkshopCacheAsync()
        {
            string path = GetLedgerPath(WORKSHOP_CACHE_FILENAME);
            string tempPath = path + ".tmp";
            try
            {
                Log("CTO 咪咪: 正在背景儲存(Workshop 快取)...");
                string json = await Task.Run(() => JsonConvert.SerializeObject(a_WorkshopCache, Formatting.Indented));
                await Task.Run(() => File.WriteAllText(tempPath, json));
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(tempPath, path);
                Log("CTO 咪咪: 成功儲存 " + a_WorkshopCache.Count + " 筆(Workshop 快取)。");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\n儲存(Workshop 快取)失敗！\n{e.Message}");
            }
        }

        /// <summary>
        /// [v1.1.5 核心修正] 刪掉垃圾 "Gett" code
        /// </summary>
        private static async Task SaveLedgerAsync(Dictionary<int, LedgerEntry> ledger, string path)
        {
            string tempPath = path + ".tmp";
            try
            {
                ShowWarning("[鴨嘴獸 API] 正在背景儲存(歷史)帳本...");
                string json = await Task.Run(() => JsonConvert.SerializeObject(ledger, Formatting.Indented));
                await Task.Run(() => File.WriteAllText(tempPath, json));
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(tempPath, path);
                Log("CTO 咪咪: 成功儲存 " + ledger.Count + " 筆(歷史)帳本。");
                ShowWarning("[鴨嘴獸 API] 歷史帳本儲存完畢！");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\n儲存(歷史)帳本失敗！\n{e.Message}");
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
                ShowWarning("[鴨嘴獸 API] 正在背景儲存(配方)帳本...");
                string json = await Task.Run(() => JsonConvert.SerializeObject(ledger, Formatting.Indented));
                await Task.Run(() => File.WriteAllText(tempPath, json));
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(tempPath, path);
                Log("CTO 咪咪: 成功儲存 " + ledger.Count + " 筆(配方)帳本。");
                ShowWarning("[鴨嘴獸 API] 配方帳本儲存完畢！");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\n儲存(配方)帳本失敗！\n{e.Message}");
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
                ShowWarning("[鴨嘴獸 API] 正在背景儲存(屬性/效果)帳本...");
                string json = await Task.Run(() => JsonConvert.SerializeObject(ledger, Formatting.Indented));
                await Task.Run(() => File.WriteAllText(tempPath, json));
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(tempPath, path);
                Log("CTO 咪咪: 成功儲存 " + ledger.Count + " 筆(屬性/效果)帳本。");
                ShowWarning("[鴨嘴獸 API] 屬性/效果帳本儲存完畢！");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 嚴重錯誤：\n儲存(屬性/效果)帳本失敗！\n{e.Message}");
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
                Log("CTO 咪咪: 找不到配方帳本，建立新的。");
                return;
            }

            try
            {
                string json = await Task.Run(() => File.ReadAllText(path));
                var ledger = await Task.Run(() => JsonConvert.DeserializeObject<Dictionary<string, RecipeEntry>>(json));
                if (ledger == null) throw new Exception("反序列化失敗 (配方帳本)。");
                a_RecipeLedger = ledger;
                Log("CTO 咪咪: 成功讀取 " + a_RecipeLedger.Count + " 筆配方記錄。");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 警告：\n讀取配方帳本失敗！\n{e.Message}");
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
                Log("CTO 咪咪: 找不到屬性帳本，建立新的。");
                return;
            }

            try
            {
                string json = await Task.Run(() => File.ReadAllText(path));
                var ledger = await Task.Run(() => JsonConvert.DeserializeObject<Dictionary<int, StatsAndEffectsEntry>>(json));
                if (ledger == null) throw new Exception("反序列化失敗 (屬性帳V本)。");
                a_StatsEffectsLedger = ledger;
                Log("CTO 咪咪: 成功讀取 " + a_StatsEffectsLedger.Count + " 筆屬性記錄。");
            }
            catch (Exception e)
            {
                ShowError($"[鴨嘴獸 API] 警告：\n讀取屬性帳本失敗！\n{e.Message}");
                a_StatsEffectsLedger = new Dictionary<int, StatsAndEffectsEntry>();
            }
        }

        // --- v1.3.1 Log 函數 (搬家) ---
        internal static void Log(string message)
        {
            UnityEngine.Debug.Log($"[DuckovCoreAPI] {message}");
        }

        public static void ShowError(string message, bool forcePush = false)
        {
            UnityEngine.Debug.LogError($"[DuckovCoreAPI] {message}");

            if (!isUIReady && !forcePush)
            {
                uiMessageQueue.Add((message, true));
                return;
            }
            try
            {
                CoreUI.AddMessage(message, true);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[DuckovCoreAPI] CoreUI.AddMessage 呼叫失敗: {e.Message}");
            }
        }

        public static void ShowWarning(string message, bool forcePush = false)
        {
            UnityEngine.Debug.LogWarning($"[DuckovCoreAPI] {message}");

            if (!isUIReady && !forcePush)
            {
                uiMessageQueue.Add((message, false));
                return;
            }
            try
            {
                CoreUI.AddMessage(message, false);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[DuckovCoreAPI] CoreUI.AddMessage 呼叫失敗: {e.Message}");
            }
        }
    } // [v1.3.1 修正] ModBehaviour 類別 結束點

    // ==================================================
    // [v1.0.0] UI 提示 (v1.3.1 搬家)
    // ==================================================
    public class CoreUI : MonoBehaviour
    {
        private static List<(string message, float timestamp, bool isError)> activeMessages = new List<(string, float, bool isError)>();
        private const float MESSAGE_DURATION = 10.0f;

        void Awake()
        {
            ModBehaviour.isUIReady = true;
            foreach (var (msg, isError) in ModBehaviour.uiMessageQueue)
            {
                AddMessage(msg, isError);
            }
            ModBehaviour.uiMessageQueue.Clear();
        }

        public static void AddMessage(string message, bool isError)
        {
            if (ModBehaviour.instance != null)
            {
                activeMessages.Add((message, Time.time, isError));
            }
            else
            {
                ModBehaviour.uiMessageQueue.Add((message, isError));
            }
        }

        void OnGUI()
        {
            if (activeMessages.Count == 0) return;

            activeMessages.RemoveAll(msg => Time.time - msg.timestamp > MESSAGE_DURATION);
            if (activeMessages.Count == 0) return;

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
            for (int i = activeMessages.Count - 1; i >= 0; i--)
            {
                if (count >= 5) break;

                try
                {
                    var (message, timestamp, isError) = activeMessages[i];
                    float alpha = 1.0f - Mathf.Clamp01((Time.time - timestamp) / MESSAGE_DURATION);
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
    // [v1.0.0] Phase 1 攔截器 (v1.3.1 搬家)
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
                    MethodBase? method = frame.GetMethod();
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
    // [v2.2.1 修正] API 公開函數 (partial)
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
        public static bool GetTypeID(string goldenID, string itemNameKey, out int typeID)
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
        public static bool GetTypeIDBySilver(string silverID, string itemNameKey, out int typeID)
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
        /// <returns>如果成功在帳本中找到該 ID 則回傳 true</returns>
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