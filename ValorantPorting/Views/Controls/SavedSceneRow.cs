using System;
using System.Linq;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Views.Controls;

// One saved scene in the Scenes tab: its name, what's in it, when it was saved.
public class SavedSceneRow
{
    public SavedSceneRow(SavedScenes.SavedScene scene)
    {
        Scene = scene;
        AgentText = scene.Agent?.Name ?? "—";
        GunText = scene.Gun?.Name ?? "—";
        AgentAnimationText = scene.AgentAnimation is { } a ? a.Repeat > 1 ? $"{a.Name} ×{a.Repeat}" : a.Name : "—";
        GunAnimationText = scene.GunAnimation is { } g ? g.Repeat > 1 ? $"{g.Name} ×{g.Repeat}" : g.Name : "—";
        Summary = string.Join("  ·  ", new[] { scene.Agent?.Name, scene.Gun?.Name, scene.AgentAnimation?.Name, scene.GunAnimation?.Name }
            .Where(n => n != null));
        SavedText = $"Saved {scene.Saved:d MMM yyyy, HH:mm}";
    }

    public SavedScenes.SavedScene Scene { get; }
    public string Name => Scene.Name;
    public string Summary { get; }
    public string SavedText { get; }
    public string AgentText { get; }
    public string GunText { get; }
    public string AgentAnimationText { get; }
    public string GunAnimationText { get; }

    // every search word in the name or in what the scene holds ("jett vandal", "xerofang run")
    public bool Match(string filter) =>
        filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => Name.Contains(word, StringComparison.OrdinalIgnoreCase) || Summary.Contains(word, StringComparison.OrdinalIgnoreCase));
}
