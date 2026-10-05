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
        AgentAnimationText = Steps(scene.AgentSteps) ?? (scene.AgentAnimation is { } a ? a.Repeat > 1 ? $"{a.Name} ×{a.Repeat}" : a.Name : "—");
        GunAnimationText = Steps(scene.GunSteps) ?? (scene.GunAnimation is { } g ? g.Repeat > 1 ? $"{g.Name} ×{g.Repeat}" : g.Name : "—");
        AbilityText = scene.AbilityName ?? "—";
        AbilityAnimationText = Steps(scene.AbilitySteps) ?? (scene.AbilityAnimation is { } ab ? ab.Repeat > 1 ? $"{ab.Name} ×{ab.Repeat}" : ab.Name : "—");
        AbilityVisibility = scene.AbilityName != null || scene.AbilityAnimation != null
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        var stepCount = new[] { scene.AgentSteps, scene.GunSteps, scene.AbilitySteps }.Sum(s => s?.Count ?? 0);
        Summary = string.Join("  ·  ", new[] { scene.Agent?.Name, scene.Gun?.Name, scene.AbilityName, scene.AgentAnimation?.Name,
                scene.GunAnimation?.Name, scene.AbilityAnimation?.Name, scene.HasSteps ? $"{stepCount} animations" : null }
            .Where(n => n != null));
        SavedText = $"Saved {scene.Saved:d MMM yyyy, HH:mm}";
    }

    // a preset saved from Blender: its animations in order, with how each was applied
    private static string? Steps(System.Collections.Generic.List<SavedScenes.Step>? steps)
    {
        if (steps is not { Count: > 0 }) return null;
        return string.Join("\n", steps.Select((step, i) =>
        {
            var name = step.Animation.Repeat > 1 ? $"{step.Animation.Name} ×{step.Animation.Repeat}" : step.Animation.Name;
            var how = i == 0 || step.Mode == "Replace" ? "" : step.Mode == "Layer" ? "on top: " :
                step.CutFrame is { } cut ? $"after (from frame {cut:0}): " : "after: ";
            return $"{i + 1}. {how}{name}";
        }));
    }

    public SavedScenes.SavedScene Scene { get; }
    public string Name => Scene.Name;
    public string Summary { get; }
    public string SavedText { get; }
    public string AgentText { get; }
    public string GunText { get; }
    public string AgentAnimationText { get; }
    public string GunAnimationText { get; }
    public string AbilityText { get; }
    public string AbilityAnimationText { get; }
    public System.Windows.Visibility AbilityVisibility { get; }

    // every search word in the name or in what the scene holds ("jett vandal", "xerofang run")
    public bool Match(string filter) =>
        filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => Name.Contains(word, StringComparison.OrdinalIgnoreCase) || Summary.Contains(word, StringComparison.OrdinalIgnoreCase));
}
