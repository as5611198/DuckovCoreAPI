# DuckovCoreAPI

一個 逃離鴨科夫 的前置 API 模組，提供物品來源、屬性、配方等資料庫供其他 Mod 使用。

如何安裝 (給玩家)

前往 steam工作紡訂閱即可使用 。

注意需要排序在Harmony下一位即可

如何使用 (給 Mod 開發者)

1. 加入參考

下載 DuckovCoreAPI.dll 並在你的 C# 專案中加入參考 (Reference)。

2. 開發者編譯指南

本專案 (原始碼) 不會提供遊戲本體的 .dll 檔案。

你必須自行在你的 Visual Studio 專案中，加入以下位於你遊戲安裝目錄下的 .dll 檔案作為參考 (Reference)：

Assembly-CSharp.dll

ItemStatsSystem.dll

Duckov.Modding.dll

Duckov.Utilities.dll

(其他有用到的...)

3. API 範例 (重點！)

你必須等待 API 掃描完畢後才能抓取資料。DuckovCoreAPI 提供了三個「聖杯訊號」(bool) 供你檢查：

DuckovCoreAPI.ModBehaviour.IsDatabaseReady(): 物品來源資料庫 (最常用)

DuckovCoreAPI.ModBehaviour.IsRecipeReady(): 配方資料庫

DuckovCoreAPI.ModBehaviour.IsStatsEffectsReady(): 屬性/效果資料庫

範例程式碼

強烈建議使用 Coroutine (協程) 在背景等待，避免卡住遊戲。

// 範例：如何在背景等待 API 並抓取資料
private IEnumerator WaitForAPI_Coroutine(Item item, TextMeshProUGUI textInstance)
{
    // 1. 檢查 API 是否好了？
    // (我們在這裡等待物品資料庫)
    while (!DuckovCoreAPI.ModBehaviour.IsDatabaseReady())
    {
        // API 沒好，先顯示提示
        textInstance.text = "<color=#808080>來源: 正在掃描...</color>";
        // 等待 0.5 秒再檢查一次
        yield return new WaitForSeconds(0.5f);
    }

    // 2. API 好了，執行抓取
    // 使用 GetEntry() 搭配 TypeID 查詢
    if (DuckovCoreAPI.ModBehaviour.GetEntry(item.TypeID, out var entry))
    {
        // 抓到了！ (entry.BronzeID 就是 Mod 顯示名稱)
        textInstance.text = $"<color=#80E0FF>來源: {entry.BronzeID}</color>";
    }
    else
    {
        // 帳本裡沒有這個物品
        textInstance.text = "<color=#FF6060>來源: 未知</color>";
    }
}
