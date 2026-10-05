using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json.Linq;
using ValorantPorting.AppUtils;
using ValorantPorting.Services;
using ValorantPorting.Views;

namespace ValorantPorting.ViewModels;

// "Save from Blender": a preset of what's in Blender, however it got there (sent one by one, mixed, layered, chained).
// The add-on keeps on each armature what the app sent it (the agent or skin, and every animation with how it was
// applied); asked, it answers for the selected agent and what it holds (gun, ability), or the selected gun / ability.
public partial class MainViewModel
{
    [RelayCommand]
    public async Task SaveFromBlender()
    {
        const string title = "Save from Blender";
        BlenderService.RequestPreset();
        var answer = await BlenderSelectionListener.NextPresetAnswer(TimeSpan.FromSeconds(4));
        if (answer is null)
        {
            MessageBox.Show("Blender didn't answer. Is it open, with the Valorant Porting add-on updated (open the app once, then restart Blender)?",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        JObject reply;
        try { reply = JObject.Parse(answer); }
        catch (Exception) { return; }
        if (reply["Error"]?.ToString() is { } error)
        {
            MessageBox.Show(error, title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // each model: what it is, its animations in order, which agent model ("TP", "FP", "CS")
        var unknown = 0;
        List<SavedScenes.Step>? Steps(JToken? model)
        {
            var steps = new List<SavedScenes.Step>();
            foreach (var step in model?["Steps"] as JArray ?? [])
            {
                if (step["Animation"] is not JObject animation || animation.ToObject<SavedScenes.Animation>() is not { } saved)
                {
                    unknown++; // applied before this version, or not from the app's list
                    continue;
                }
                steps.Add(new SavedScenes.Step(saved, step["Mode"]?.ToString() ?? "Replace", step["ChainBlend"]?.ToObject<double?>() ?? 0.2,
                    step["CutFrame"]?.ToObject<double?>()));
            }
            return steps.Count > 0 ? steps : null;
        }

        SavedScenes.Asset? Asset(JToken? model) => model?["Model"]?["Asset"] is JObject asset ? asset.ToObject<SavedScenes.Asset>() : null;

        var agent = reply["Agent"];
        var gun = reply["Gun"];
        var ability = reply["Ability"];
        var (agentSteps, gunSteps, abilitySteps) = (Steps(agent), Steps(gun), Steps(ability));
        var agentAsset = Asset(agent);
        var gunAsset = Asset(gun);
        var abilityId = ability?["Model"]?["AbilityId"]?.ToString();
        var abilityName = ability?["Model"]?["AbilityName"]?.ToString();
        if (agentAsset is null && gunAsset is null && abilityId is null && agentSteps is null && gunSteps is null && abilitySteps is null)
        {
            MessageBox.Show("Nothing to save: the selected model wasn't sent by this version of the app (send it again, apply its animations, " +
                            "then save), or it has nothing on it yet.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var parts = new[] { agentAsset?.Name, gunAsset?.Name, abilityName }.Where(n => n != null).ToList();
        var firstAnimation = (agentSteps ?? gunSteps ?? abilitySteps)?.First().Animation.Name;
        var suggestion = string.Join(" + ", parts.Append(firstAnimation).Where(n => n != null));
        var summary = new List<string>();
        if (agentAsset != null || agentSteps != null) summary.Add($"Agent: {agentAsset?.Name ?? "(the selected agent)"}, {Count(agentSteps)}");
        if (gunAsset != null || gunSteps != null) summary.Add($"Gun: {gunAsset?.Name ?? "(the gun)"}, {Count(gunSteps)}");
        if (abilityId != null || abilitySteps != null) summary.Add($"Ability: {abilityName ?? "(the ability)"}, {Count(abilitySteps)}");
        if (unknown > 0) summary.Add($"{unknown} animation(s) applied before this version (or not from the list) are left out.");

        var name = InputDialog.Ask(title, "In Blender:\n" + string.Join("\n", summary) + "\n\nName of this preset:", suggestion)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (SavedScenes.Exists(name) &&
            MessageBox.Show($"There's already a preset called \"{name}\". Replace it?", title, MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SavedScenes.Save(new SavedScenes.SavedScene(name, DateTime.Now, agentAsset, gunAsset,
            agentSteps?.First().Animation, gunSteps?.First().Animation, abilityId, abilityName, abilitySteps?.First().Animation,
            agentSteps, gunSteps, abilitySteps, agent?["Rig"]?.ToString()));
        RefreshSavedScenes();
        AppLog.Information($"Saved what's in Blender as the preset \"{name}\" (Presets).");
    }

    private static string Count(List<SavedScenes.Step>? steps) => steps is null ? "no animations" : steps.Count == 1 ? "1 animation" : $"{steps.Count} animations";
}
