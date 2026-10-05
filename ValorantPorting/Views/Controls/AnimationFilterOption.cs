using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ValorantPorting.Views.Controls;

// One entry of the "Show animations for" dropdown in the Animations tab.
public partial class AnimationFilterOption : ObservableObject
{
    [ObservableProperty] private string label = string.Empty;

    public AnimationFilterOption(string label, string? key, bool isFollow = false)
    {
        Label = label;
        Key = key;
        IsFollow = isFollow;
    }

    // agent|<folder>|<name>|TP/FP/CS  or  weapon|<folder>|<name>;  null = no filter (or "follow" before anything is selected)
    public string? Key { get; }
    public bool IsFollow { get; }

    public static string Describe(string key)
    {
        var parts = key.Split('|');
        if (parts.Length < 3) return key;
        if (parts[0] != "agent" || parts.Length < 4) return parts[2];

        var variant = parts[3] switch
        {
            "FP" => "1st person",
            "CS" => "character select",
            _ => "3rd person"
        };
        return $"{parts[2]} ({variant})";
    }

    // Returns the folder that owns the animations, the allowed name prefixes and whether shared _Core_ agent animations apply.
    public static (string Folder, string[] Prefixes, bool SharedAgentAnimations)? Parse(string? key)
    {
        if (key is null) return null;
        var parts = key.Split('|');
        if (parts.Length < 3) return null;

        if (parts[0] == "agent")
        {
            var variant = parts.Length >= 4 ? parts[3] : "TP";
            return variant switch
            {
                "FP" => (parts[1], ["FP_"], true),
                "CS" => (parts[1], ["CS_"], false),
                _ => (parts[1], ["TP_"], true)
            };
        }

        if (parts[0] == "ability") // an ability's models: its AB_ (1st person) / ABTP_ (3rd person) animations
            return (parts[1], ["AB_", "ABTP_", "ABCS_"], false);

        if (parts[0] == "item") // a held item that isn't a gun (the spike): its own EQ_ (1st person) / EQTP_ (3rd person) animations
            return (parts[1], ["EQ_", "EQTP_"], false);

        if (parts[0] == "weapon")
        {
            var isMelee = parts[1].Contains("/Melee", StringComparison.OrdinalIgnoreCase);
            return (parts[1], isMelee ? ["EQ_", "GN_"] : ["GN_", "GNTP_"], false); // GN_: 1st person, GNTP_: 3rd person
        }

        return null;
    }
}
