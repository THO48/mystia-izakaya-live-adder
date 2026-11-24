using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using GameData.Core.Collections;
using GameData.CoreLanguage.Collections;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NightbirdLiveAdder;

public enum ItemCategory
{
    Beverages,
    Ingredients
}

public sealed class CatalogItem
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Module { get; set; } = "CORE";
    public int Level { get; set; } = 1;
    public int Price { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string IconName { get; set; } = "";
    public bool RuntimeSynced { get; set; }
    public bool ExistsInGameDb { get; set; } = true;

    public string SearchIndex { get; private set; } = "";

    public void RebuildSearchIndex()
    {
        var tagsText = Tags != null && Tags.Length > 0 ? string.Join(" ", Tags) : "";
        SearchIndex = $"{Id} #{Id} {Name} {Module} {tagsText} {Description}".ToLowerInvariant();
    }
}

public static class CatalogService
{
    private const string ResourcePrefix = "NightbirdLiveAdder.";

    private static readonly List<CatalogItem> _beverages = new();
    private static readonly List<CatalogItem> _ingredients = new();
    private static readonly List<string> _beverageTags = new();
    private static readonly List<string> _modules = new() { "ALL", "CORE", "DLC1", "DLC2", "DLC3", "DLC4" };
    private static readonly Dictionary<string, Texture2D> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool _initialized;
    private static bool _runtimeMerged;

    public static IReadOnlyList<CatalogItem> Beverages => _beverages;
    public static IReadOnlyList<CatalogItem> Ingredients => _ingredients;
    public static IReadOnlyList<string> BeverageTags => _beverageTags;
    public static IReadOnlyList<string> Modules => _modules;

    public static IReadOnlyList<CatalogItem> GetItems(ItemCategory category) =>
        category == ItemCategory.Beverages ? _beverages : _ingredients;

    public static string GetCategoryLabel(ItemCategory category) =>
        category == ItemCategory.Beverages ? "饮品" : "食材";

    public static string GetModuleLabel(string module) => module switch
    {
        "ALL" => "全部",
        "CORE" => "本篇",
        _ => module
    };

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        LoadEmbeddedJson("Data.beverages.json", _beverages);
        LoadEmbeddedJson("Data.ingredients.json", _ingredients);

        RebuildBeverageTags();
        RebuildModules();

        Plugin.LogSource?.LogInfo(
            $"Catalog loaded from embedded data: {_ingredients.Count} ingredients, {_beverages.Count} beverages, {_beverageTags.Count} beverage tags.");
    }

    public static void TrySyncWithRuntimeDatabase()
    {
        try
        {
            var coreIngs = DataBaseCore.Ingredients;
            var coreBevs = DataBaseCore.Beverages;
            if (coreIngs == null || coreBevs == null || coreIngs.Count == 0 || coreBevs.Count == 0)
                return;

            SyncCategory(_ingredients, ItemCategory.Ingredients, coreIngs.Count);
            SyncCategory(_beverages, ItemCategory.Beverages, coreBevs.Count);

            if (!_runtimeMerged)
            {
                _runtimeMerged = true;
                DiscoverExtraRuntimeItems();
                RebuildBeverageTags();
                RebuildModules();
            }
        }
        catch
        {
            // Database may not be initialized yet on title screen; retry silently later.
        }
    }

    private static void SyncCategory(List<CatalogItem> list, ItemCategory category, int dbCount)
    {
        if (dbCount <= 0) return;

        foreach (var item in list)
        {
            bool exists = category == ItemCategory.Beverages
                ? DataBaseCore.BeveragesExists(item.Id)
                : DataBaseCore.IngredientsExists(item.Id);

            item.ExistsInGameDb = exists;
            item.RuntimeSynced = true;
        }
    }

    private static void DiscoverExtraRuntimeItems()
    {
        // Discover any extra items in the running game (e.g. DLC5 or future patches)
        try
        {
            var ingIds = new HashSet<int>(_ingredients.Select(x => x.Id));
            var coreIngs = DataBaseCore.Ingredients;
            if (coreIngs != null)
            {
                foreach (var kv in coreIngs)
                {
                    int id = kv.Key;
                    if (ingIds.Contains(id)) continue;

                    string mod = ResolveModuleFromId(id, DataBaseCore.GetIngredientsDLCMapping(id));
                    string name = TryGetRuntimeName(ItemCategory.Ingredients, id, $"食材 #{id}");
                    var entry = new CatalogItem
                    {
                        Id = id,
                        Name = name,
                        Description = "",
                        Module = mod,
                        Level = kv.Value != null ? kv.Value.Level : 1,
                        Price = kv.Value != null ? kv.Value.BaseValue : 0,
                        Tags = Array.Empty<string>(),
                        IconName = $"ing_{id}.png",
                        ExistsInGameDb = true,
                        RuntimeSynced = true
                    };
                    entry.RebuildSearchIndex();
                    _ingredients.Add(entry);
                    ingIds.Add(id);
                }
                _ingredients.Sort((a, b) => a.Id.CompareTo(b.Id));
            }

            var bevIds = new HashSet<int>(_beverages.Select(x => x.Id));
            var coreBevs = DataBaseCore.Beverages;
            if (coreBevs != null)
            {
                foreach (var kv in coreBevs)
                {
                    int id = kv.Key;
                    if (bevIds.Contains(id)) continue;

                    string mod = ResolveModuleFromId(id, DataBaseCore.GetBeveragesDLCMapping(id));
                    string name = TryGetRuntimeName(ItemCategory.Beverages, id, $"饮品 #{id}");
                    var entry = new CatalogItem
                    {
                        Id = id,
                        Name = name,
                        Description = "",
                        Module = mod,
                        Level = kv.Value != null ? kv.Value.Level : 1,
                        Price = kv.Value != null ? kv.Value.BaseValue : 0,
                        Tags = Array.Empty<string>(),
                        IconName = $"bev_{id}.png",
                        ExistsInGameDb = true,
                        RuntimeSynced = true
                    };
                    entry.RebuildSearchIndex();
                    _beverages.Add(entry);
                    bevIds.Add(id);
                }
                _beverages.Sort((a, b) => a.Id.CompareTo(b.Id));
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource?.LogWarning($"Runtime catalog discovery skipped: {ex.Message}");
        }
    }

    private static string ResolveModuleFromId(int id, string mapping)
    {
        if (!string.IsNullOrWhiteSpace(mapping))
        {
            var upper = mapping.Trim().ToUpperInvariant();
            if (upper.StartsWith("DLC")) return upper;
        }
        if (id < 1000) return "CORE";
        return $"DLC{id / 1000}";
    }

    private static string TryGetRuntimeName(ItemCategory category, int id, string fallback)
    {
        try
        {
            var lang = category == ItemCategory.Beverages
                ? DataBaseLanguage.GetBeverageLang(id)
                : DataBaseLanguage.GetIngredientLang(id);
            if (lang != null)
            {
                // Reflection-safe fallback in case property name differs across builds
                var nameProp = lang.GetType().GetProperty("Name") ?? lang.GetType().GetProperty("name");
                if (nameProp != null)
                {
                    var val = nameProp.GetValue(lang) as string;
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
        }
        catch
        {
            // Ignore
        }
        return fallback;
    }

    private static void LoadEmbeddedJson(string relResourceName, List<CatalogItem> target)
    {
        target.Clear();
        var asm = Assembly.GetExecutingAssembly();
        var fullName = ResourcePrefix + relResourceName;
        using var stream = asm.GetManifestResourceStream(fullName);
        if (stream == null)
        {
            Plugin.LogSource?.LogWarning($"Embedded resource not found: {fullName}");
            return;
        }

        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("items", out var itemsElem) ||
            itemsElem.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var el in itemsElem.EnumerateArray())
        {
            int id = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : -1;
            if (id < 0) continue;

            var tags = new List<string>();
            if (el.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsProp.EnumerateArray())
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) tags.Add(s);
                }
            }

            var item = new CatalogItem
            {
                Id = id,
                Name = el.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? $"#{id}") : $"#{id}",
                Description = el.TryGetProperty("desc", out var dProp) ? (dProp.GetString() ?? "") : "",
                Module = el.TryGetProperty("module", out var mProp) ? (mProp.GetString() ?? "CORE") : "CORE",
                Level = el.TryGetProperty("level", out var lProp) ? lProp.GetInt32() : 1,
                Price = el.TryGetProperty("price", out var pProp) ? pProp.GetInt32() : 0,
                Tags = tags.ToArray(),
                IconName = el.TryGetProperty("icon", out var iProp) ? (iProp.GetString() ?? "") : "",
                ExistsInGameDb = true
            };
            item.RebuildSearchIndex();
            target.Add(item);
        }

        target.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    private static void RebuildBeverageTags()
    {
        _beverageTags.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _beverages)
        {
            if (item.Tags == null) continue;
            foreach (var tag in item.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag))
                    _beverageTags.Add(tag);
            }
        }
    }

    private static void RebuildModules()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ALL", "CORE" };
        var extras = new List<string>();
        foreach (var item in _ingredients.Concat(_beverages))
        {
            if (!string.IsNullOrWhiteSpace(item.Module) && seen.Add(item.Module))
                extras.Add(item.Module);
        }
        extras.Sort(StringComparer.OrdinalIgnoreCase);

        _modules.Clear();
        _modules.Add("ALL");
        _modules.Add("CORE");
        _modules.AddRange(extras);
    }

    public static Texture2D GetIcon(string iconFileName)
    {
        if (string.IsNullOrWhiteSpace(iconFileName)) return null;

        if (_iconCache.TryGetValue(iconFileName, out var cached))
        {
            if (cached != null) return cached;
            _iconCache.Remove(iconFileName);
        }

        var asm = Assembly.GetExecutingAssembly();
        var fullName = ResourcePrefix + "Icons." + iconFileName;
        using var stream = asm.GetManifestResourceStream(fullName);
        if (stream == null)
        {
            _iconCache[iconFileName] = null;
            return null;
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] bytes = ms.ToArray();

        try
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;

            var il2cppBytes = new Il2CppStructArray<byte>(bytes);
            if (ImageConversion.LoadImage(tex, il2cppBytes, false))
            {
                tex.filterMode = FilterMode.Point;
                _iconCache[iconFileName] = tex;
                return tex;
            }
            UnityEngine.Object.Destroy(tex);
        }
        catch (Exception ex)
        {
            Plugin.LogSource?.LogWarning($"Failed to load icon {iconFileName}: {ex.Message}");
        }

        _iconCache[iconFileName] = null;
        return null;
    }
}
