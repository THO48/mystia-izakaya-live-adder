using System;
using System.Collections.Generic;
using Common.UI.StorageUtility;
using GameData.Core.Collections;
using GameData.RunTime.Common;
using UnityEngine;

namespace NightbirdLiveAdder;

public readonly struct AddOperationResult
{
    public bool Success { get; init; }
    public int AddedCount { get; init; }
    public int NewTotal { get; init; }
    public int SkippedCount { get; init; }
    public string Message { get; init; }
}

public static class StorageService
{
    public static int MaxObjectCount
    {
        get
        {
            try
            {
                int max = RunTimeStorage.MAX_OBJECT_COUNT;
                return max > 0 ? max : 99999;
            }
            catch
            {
                return 99999;
            }
        }
    }

    public static bool IsStorageReady()
    {
        try
        {
            var ings = RunTimeStorage.Ingredients;
            var bevs = RunTimeStorage.Beverages;
            return ings != null && bevs != null;
        }
        catch
        {
            return false;
        }
    }

    public static int GetCount(ItemCategory category, int id)
    {
        try
        {
            var dict = category == ItemCategory.Beverages
                ? RunTimeStorage.Beverages
                : RunTimeStorage.Ingredients;
            if (dict == null) return 0;
            return dict.TryGetValue(id, out int count) ? count : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static int CountOwned(ItemCategory category, IReadOnlyList<CatalogItem> items)
    {
        if (!IsStorageReady()) return 0;
        int owned = 0;
        try
        {
            var dict = category == ItemCategory.Beverages
                ? RunTimeStorage.Beverages
                : RunTimeStorage.Ingredients;
            if (dict == null) return 0;

            for (int i = 0; i < items.Count; i++)
            {
                if (dict.TryGetValue(items[i].Id, out int c) && c > 0)
                    owned++;
            }
        }
        catch
        {
            // Ignore transient state during scene loading
        }
        return owned;
    }

    public static AddOperationResult AddSingle(
        ItemCategory category,
        CatalogItem item,
        int amount,
        bool unlockAlbum)
    {
        if (item == null)
        {
            return new AddOperationResult { Success = false, Message = "未选择物品" };
        }

        if (amount <= 0)
        {
            return new AddOperationResult { Success = false, Message = "添加数量必须大于 0" };
        }

        if (!IsStorageReady())
        {
            return new AddOperationResult { Success = false, Message = "仓库未就绪：请先进入游戏存档后再添加" };
        }

        if (!ItemExistsInDatabase(category, item.Id))
        {
            return new AddOperationResult
            {
                Success = false,
                SkippedCount = 1,
                Message = $"跳过「{item.Name}」：当前游戏未加载 {item.Module} 数据包"
            };
        }

        try
        {
            var dict = category == ItemCategory.Beverages
                ? RunTimeStorage.Beverages
                : RunTimeStorage.Ingredients;
            if (dict == null)
            {
                return new AddOperationResult { Success = false, Message = "无法访问运行时仓库字典" };
            }

            int maxCount = MaxObjectCount;
            int current = dict.TryGetValue(item.Id, out int existing) ? existing : 0;
            long target = (long)current + amount;
            int clamped = (int)Math.Min(maxCount, target);
            int actualDelta = clamped - current;

            if (actualDelta <= 0)
            {
                return new AddOperationResult
                {
                    Success = false,
                    NewTotal = current,
                    Message = $"「{item.Name}」已达上限 {maxCount:N0}，无需继续添加"
                };
            }

            dict[item.Id] = clamped;

            if (unlockAlbum)
                TryUnlockAlbum(category, item.Id);

            NotifyStorageChanged(category);

            string suffix = clamped >= maxCount ? $"（已达上限 {maxCount:N0}）" : $"（当前库存 {clamped:N0}）";
            return new AddOperationResult
            {
                Success = true,
                AddedCount = 1,
                NewTotal = clamped,
                Message = $"已实时添加 {item.Name} +{actualDelta:N0}{suffix}"
            };
        }
        catch (Exception ex)
        {
            Plugin.LogSource?.LogError($"AddSingle({category}, {item.Id}, +{amount}) failed: {ex}");
            return new AddOperationResult
            {
                Success = false,
                Message = $"添加失败：{ex.Message}"
            };
        }
    }

    public static AddOperationResult AddBatch(
        ItemCategory category,
        IReadOnlyList<CatalogItem> items,
        int amount,
        bool unlockAlbum)
    {
        if (items == null || items.Count == 0)
        {
            return new AddOperationResult { Success = false, Message = "当前筛选列表为空" };
        }

        if (amount <= 0)
        {
            return new AddOperationResult { Success = false, Message = "添加数量必须大于 0" };
        }

        if (!IsStorageReady())
        {
            return new AddOperationResult { Success = false, Message = "仓库未就绪：请先进入游戏存档后再添加" };
        }

        try
        {
            var dict = category == ItemCategory.Beverages
                ? RunTimeStorage.Beverages
                : RunTimeStorage.Ingredients;
            if (dict == null)
            {
                return new AddOperationResult { Success = false, Message = "无法访问运行时仓库字典" };
            }

            int maxCount = MaxObjectCount;
            int updated = 0;
            int skippedDlc = 0;
            int alreadyMax = 0;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!ItemExistsInDatabase(category, item.Id))
                {
                    skippedDlc++;
                    continue;
                }

                int current = dict.TryGetValue(item.Id, out int existing) ? existing : 0;
                int clamped = (int)Math.Min(maxCount, (long)current + amount);
                if (clamped > current)
                {
                    dict[item.Id] = clamped;
                    updated++;
                }
                else
                {
                    alreadyMax++;
                }

                if (unlockAlbum)
                    TryUnlockAlbum(category, item.Id);
            }

            if (updated > 0)
            {
                NotifyStorageChanged(category);
            }

            var catName = CatalogService.GetCategoryLabel(category);
            var parts = new List<string> { $"批量实时添加完成：{updated} 种{catName} +{amount:N0}" };
            if (alreadyMax > 0) parts.Add($"{alreadyMax} 种已达上限");
            if (skippedDlc > 0) parts.Add($"跳过 {skippedDlc} 种未加载 DLC 物品");

            return new AddOperationResult
            {
                Success = updated > 0,
                AddedCount = updated,
                SkippedCount = skippedDlc,
                Message = string.Join("，", parts)
            };
        }
        catch (Exception ex)
        {
            Plugin.LogSource?.LogError($"AddBatch({category}, count={items.Count}, +{amount}) failed: {ex}");
            return new AddOperationResult
            {
                Success = false,
                Message = $"批量添加失败：{ex.Message}"
            };
        }
    }

    private static bool ItemExistsInDatabase(ItemCategory category, int id)
    {
        try
        {
            // If database is not loaded yet, do not falsely reject
            if (DataBaseCore.Ingredients == null || DataBaseCore.Beverages == null)
                return true;

            return category == ItemCategory.Beverages
                ? DataBaseCore.BeveragesExists(id)
                : DataBaseCore.IngredientsExists(id);
        }
        catch
        {
            return true;
        }
    }

    private static void TryUnlockAlbum(ItemCategory category, int id)
    {
        try
        {
            if (category == ItemCategory.Beverages)
                RunTimeAlbum.TryRecordBeverages(id);
            else
                RunTimeAlbum.TryRecordIngredients(id);
        }
        catch
        {
            // Ignore if album is not initialized
        }
    }

    private static void NotifyStorageChanged(ItemCategory category)
    {
        try
        {
            RunTimeStorage.OnAnyObjectInCallback?.Invoke();
        }
        catch
        {
            // Ignore listener exceptions
        }

        // If the in-game storage panel is currently open, refresh it immediately
        try
        {
            var panel = UnityEngine.Object.FindObjectOfType<StorageOtherPannel>();
            if (panel != null && panel.isActiveAndEnabled)
            {
                if (category == ItemCategory.Beverages)
                    panel.UpdateBeverageCollection();
                else
                    panel.UpdateIngredientCollection();
            }
        }
        catch
        {
            // Safe fallback: closing and reopening the storage panel always reads fresh dictionary values
        }
    }
}
