using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using ValorantPorting.Export;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.AppUtils;

// Scenes the user saved as presets (agent + gun + animations), in .data\saved-scenes.json. Items are kept by their game
// paths, so a saved scene works after restarts and updates without the agent/skin lists being loaded.
public static class SavedScenes
{
    private static readonly string FilePath = Path.Combine(App.DataFolder.FullName, "saved-scenes.json");
    private static readonly object Lock = new();
    private static List<SavedScene> scenes = Load();

    // an agent or gun skin: ObjectPath = the asset the app loads it from; StylePath = the chosen variant (chroma)
    public record Asset(string ObjectPath, string PackagePath, EAssetType Type, string DisplayName, string Name,
        string? StylePath, int? WeaponLevel, ECharacterModels Models);

    // an animation: one entry of the list (ItemId) or a mix of two (UpperId + LowerId); ids are the list's LibraryIds
    public record Animation(string Name, string? ItemId, string? UpperId, string? LowerId, int Repeat);

    // one animation as applied in Blender: Mode "Replace", "Layer" (on top) or "Chain" (after the previous one, with
    // this blend in seconds, cut at CutFrame when it started at a chosen frame)
    public record Step(Animation Animation, string Mode, double ChainBlend, double? CutFrame);

    // AbilityId: the ability model's library id ("ability:..."), looked up in the Abilities list.
    // AgentSteps/GunSteps/AbilitySteps: saved from Blender, every animation applied to each model in order (then the
    // single XAnimation is the first one); AgentRig: which agent model they're on ("TP", "FP", "CS")
    public record SavedScene(string Name, DateTime Saved, Asset? Agent, Asset? Gun, Animation? AgentAnimation, Animation? GunAnimation,
        string? AbilityId = null, string? AbilityName = null, Animation? AbilityAnimation = null,
        List<Step>? AgentSteps = null, List<Step>? GunSteps = null, List<Step>? AbilitySteps = null, string? AgentRig = null)
    {
        public bool HasSteps => AgentSteps is { Count: > 1 } || GunSteps is { Count: > 1 } || AbilitySteps is { Count: > 1 } ||
                                new[] { AgentSteps, GunSteps, AbilitySteps }.Any(s => s?.Any(x => x.Mode != "Replace") == true);
    }

    public static IReadOnlyList<SavedScene> All
    {
        get
        {
            lock (Lock) return scenes.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static bool Exists(string name)
    {
        lock (Lock) return scenes.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // adds it, replacing one of the same name
    public static void Save(SavedScene scene)
    {
        lock (Lock)
        {
            scenes.RemoveAll(s => s.Name.Equals(scene.Name, StringComparison.OrdinalIgnoreCase));
            scenes.Add(scene);
            Write();
        }
    }

    public static void Rename(SavedScene scene, string name)
    {
        lock (Lock)
        {
            var index = scenes.FindIndex(s => s.Name.Equals(scene.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            scenes.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && s != scenes[index]);
            index = scenes.FindIndex(s => s.Name.Equals(scene.Name, StringComparison.OrdinalIgnoreCase));
            scenes[index] = scenes[index] with { Name = name };
            Write();
        }
    }

    public static void Delete(SavedScene scene)
    {
        lock (Lock)
        {
            scenes.RemoveAll(s => s.Name.Equals(scene.Name, StringComparison.OrdinalIgnoreCase));
            Write();
        }
    }

    private static List<SavedScene> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonConvert.DeserializeObject<List<SavedScene>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not read the saved scenes ({ex.Message}).");
        }

        return [];
    }

    private static void Write()
    {
        try
        {
            App.DataFolder.Create();
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(scenes, Formatting.Indented));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not save the scenes: {ex.Message}");
        }
    }
}
