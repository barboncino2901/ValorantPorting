using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ValorantPorting.AppUtils;

public enum ELibraryFilter
{
    All,
    Favorites,
    Recent
}

// The user's favorites and recently sent items (skins, agents, buddies, animations, maps), saved in .data\library.json.
// Items are identified by their game path, e.g. "anim:/Game/.../TP_Core_AK_S0_Reload_UB.TP_Core_AK_S0_Reload_UB".
public static class UserLibrary
{
    private const int MaxRecent = 50;
    private static readonly string FilePath = Path.Combine(App.DataFolder.FullName, "library.json");
    private static readonly object Lock = new();
    private static Data data = Load();

    private class Data
    {
        public List<string> Favorites = new();
        public List<string> Recent = new(); // newest first
    }

    public static event Action? Changed;

    public static bool IsFavorite(string id)
    {
        lock (Lock) return data.Favorites.Contains(id);
    }

    public static bool ToggleFavorite(string id)
    {
        bool favorite;
        lock (Lock)
        {
            favorite = !data.Favorites.Remove(id);
            if (favorite) data.Favorites.Add(id);
            Save();
        }

        Changed?.Invoke();
        return favorite;
    }

    public static void AddRecent(string id)
    {
        lock (Lock)
        {
            data.Recent.Remove(id);
            data.Recent.Insert(0, id);
            if (data.Recent.Count > MaxRecent) data.Recent.RemoveRange(MaxRecent, data.Recent.Count - MaxRecent);
            Save();
        }

        Changed?.Invoke();
    }

    // 0 = most recent; int.MaxValue = not recently used (sorts last).
    public static int RecentRank(string id)
    {
        lock (Lock)
        {
            var index = data.Recent.IndexOf(id);
            return index < 0 ? int.MaxValue : index;
        }
    }

    public static bool Matches(string id, ELibraryFilter filter) => filter switch
    {
        ELibraryFilter.Favorites => IsFavorite(id),
        ELibraryFilter.Recent => RecentRank(id) != int.MaxValue,
        _ => true
    };

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonConvert.DeserializeObject<Data>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    loaded.Favorites = loaded.Favorites.Distinct().ToList();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not read favorites/recent list ({ex.Message}); starting with an empty one.");
        }

        return new Data();
    }

    private static void Save()
    {
        try
        {
            App.DataFolder.Create();
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(data, Formatting.Indented));
            File.Move(temp, FilePath, overwrite: true); // never leave a half-written file behind
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not save favorites/recent list: {ex.Message}");
        }
    }
}
