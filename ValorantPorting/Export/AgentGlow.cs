using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Export;

// Glow some agents have in game but not in their base materials: Reyna's pulsing heart and her ultimate's tattoos,
// eyes and hair; Iso's glowing outfit lines and hands; Chamber's gold tattoo. The game swaps in effect materials for those (3P_Vampire_X_FX for Empress, ...);
// their textures and values are sent with the agent, and the Blender add-on adds a glow layer to her materials, with
// sliders (heart, ultimate) to animate. The values come from those effect materials; how they combine is rebuilt.
public static class AgentGlow
{
    public record Spec(string Agent, Dictionary<string, string> Textures, Dictionary<string, object> Values);

    // agent codename (as in "/Characters/<codename>/") -> what to send
    private static readonly Dictionary<string, (Dictionary<string, string> Textures, Dictionary<string, object> Values)> Agents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Vampire"] = (
                new Dictionary<string, string>
                {
                    // where it glows: tattoo lines; R/G tattoos, B earrings, A eyes; R fingertips, G forearms, A fingertip glow
                    ["TattooMask"] = "/Game/Characters/Vampire/S0/VFX/Texture/TP_Vampire_S0_TattooMask_3P.TP_Vampire_S0_TattooMask_3P",
                    ["MaskSetsXE"] = "/Game/Characters/Vampire/S0/VFX/Texture/TP_Vampire_S0_TattooMask_3P_Masksets_XE.TP_Vampire_S0_TattooMask_3P_Masksets_XE",
                    ["MaskSets"] = "/Game/Characters/Vampire/S0/VFX/Texture/vampire_MaskSets.vampire_MaskSets",
                    // what moves in it: the chest flare and the energy flowing over her (3P_Vampire_X_FX_Inst's TextureA)
                    ["ChestFlare"] = "/Game/Characters/Vampire/S0/VFX/Texture/Vampire_Chest_Flare.Vampire_Chest_Flare",
                    ["Energy"] = "/Game/Characters/Pandemic/S0/VFX/Textures/Wraith_4Xtile90_GrayScale.Wraith_4Xtile90_GrayScale",
                    ["TattooColor"] = "/Game/Characters/Vampire/S0/VFX/Texture/Vampire_Tattoo_Color.Vampire_Tattoo_Color"
                },
                new Dictionary<string, object>
                {
                    // TP_Vampire_S0_Heart_MI / 3P_Vampire_X_FX_Inst
                    ["HeartColor"] = "8A40FF", ["HeartPulseSpeed"] = 0.4, ["HeartPower"] = 4.0,
                    ["UltBodyColor"] = "150021", ["UltBodyAmount"] = 0.2, ["UltTattooMultiply"] = 3.0,
                    ["EnergyTile"] = 3.0, ["EnergyScrollV"] = -0.1,
                    ["FlareU"] = -0.25, ["FlareV"] = -0.409525, ["FlareUTile"] = 1.5, ["FlareVTile"] = 2.0,
                    ["EmissiveBrightness"] = 150.0
                }),
            // Iso: TP_Sequoia_S0_MI's glow mask (R forearms: his E's charged punches, G gloves: his other abilities,
            // B the lines of his outfit), switched on by the game (GlowToggle_Hands_E / _Hands_XQC / _Outfit)
            ["Sequoia"] = (
                new Dictionary<string, string>
                {
                    ["GlowMask"] = "/Game/Characters/Sequoia/S0/3P/Materials/TP_Sequoia_S0_VFXMask.TP_Sequoia_S0_VFXMask"
                },
                new Dictionary<string, object>
                {
                    ["GlowColor"] = "D6AAFF", ["HighlightColor"] = "FABCFF", ["EmissiveBrightness"] = 10.0
                }),
            // Chamber: his colour texture is DE_NoTattoo_TEX; his master material draws the tattoo on top (the alpha of
            // DE_MultiMask_TEX: side of his head, wrists), gold and metallic, revealed left to right with a glowing edge
            // in character select (TattooAlpha 0 -> 1.4, Glow Edge Thickness 0.3)
            ["Deadeye"] = (
                new Dictionary<string, string>
                {
                    ["MultiMask"] = "/Game/Characters/Deadeye/S0/VFX/Texture/DE_MultiMask_TEX.DE_MultiMask_TEX"
                },
                new Dictionary<string, object>
                {
                    ["TattooColor"] = "BA7E4C", ["TattooMetallic"] = 1.0, ["TattooRoughness"] = 0.5,
                    ["EdgeColor"] = "FFFFCF", ["EdgeThickness"] = 0.3
                })
        };

    // the glow of the agent these parts are (null: none known); exports its textures with the other files
    public static Spec? For(IEnumerable<ExportPart> parts)
    {
        var path = parts.Select(p => p.MeshPath ?? "").FirstOrDefault(p => p.Contains("/Characters/", StringComparison.OrdinalIgnoreCase));
        if (path is null) return null;
        var codename = path[(path.IndexOf("/Characters/", StringComparison.OrdinalIgnoreCase) + "/Characters/".Length)..].Split('/')[0];
        if (!Agents.TryGetValue(codename, out var glow)) return null;

        var textures = new Dictionary<string, string>();
        foreach (var (role, texturePath) in glow.Textures)
        {
            try
            {
                if (!AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(texturePath, out UTexture2D texture)) continue;
                ExportHelpers.Save(texture);
                textures[role] = texture.GetPathName();
            }
            catch (Exception)
            {
                // missing from this game version: that part of the glow is left out
            }
        }

        return textures.Count == 0 ? null : new Spec(codename, textures, glow.Values);
    }
}
