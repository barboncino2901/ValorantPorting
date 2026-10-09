using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Export;

// Glow some agents have in game but not in their base materials: Reyna's pulsing heart and her ultimate's tattoos,
// eyes and hair; Iso's glowing outfit lines and hands; Chamber's gold tattoo; Waylay's glowing crystal tattoos; Clove's ultimate (ghost, then glowing) and legs; Jett's wind on her arms; Phoenix's flames; Neon's suit and Overdrive; Veto's ultimate form fading in over him. The game swaps in effect materials for those (3P_Vampire_X_FX for Empress, ...);
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
                }),
            // Waylay: her crystal shards (separate pieces, FP_Terra_S0_Floaters_MI) glow where their mask says, her
            // nails (TP_Terra_Masks_TFX: A) and hair tips too, lit by the game (BP_TattooGlow) in her
            // TP_Terra_S0_Arm_Base_MI colours (G of the arm mask: green on the arm, yellow toward the hand)
            ["Terra"] = (
                new Dictionary<string, string>
                {
                    ["TattooDecal"] = "/Game/Characters/Terra/S0/1P/Materials/FP_Terra_S0_Tattoo_Decal.FP_Terra_S0_Tattoo_Decal",
                    ["Masks"] = "/Game/Characters/Terra/S0/3P/Materials/TP_Terra_Masks_TFX.TP_Terra_Masks_TFX",
                    // her crystal shards are separate pieces (FP_Terra_S0_Floaters_MI); this mask is where they glow
                    ["FloatersMask"] = "/Game/Characters/Terra/S0/1P/Materials/FP_Terra_MasksFloaters_TFX.FP_Terra_MasksFloaters_TFX"
                },
                new Dictionary<string, object>
                {
                    ["ArmColor"] = "C7FF78", ["HandColor"] = "FAFF26", ["HairColor"] = "FFFFFF"
                }),
            // Clove: her ultimate brings her back as a ghost (her resurrection model, see UltForms), then her normal
            // model with her ult's glow (3P_SMO_X_Fight-Body_MI / -Hair_MI: glow mask, blue rim, her TP_ULT_ hair
            // colours) until she gets a kill; her C (TP_Smonk_S0_C_Swap_01_MI_) lights her
            // legs up to 90 cm, with smoke waves and the butterflies on her trousers
            ["Smonk"] = (
                new Dictionary<string, string>
                {
                    ["UltAlbedo"] = "/Game/Characters/Smonk/S0/VFX/X/Textures/TP_ULT_Smonk_S0_DF.TP_ULT_Smonk_S0_DF",
                    ["UltMask"] = "/Game/Characters/Smonk/S0/VFX/X/Textures/TP_ULT_Smonk_S0_Mask.TP_ULT_Smonk_S0_Mask",
                    ["UltHairAlbedo"] = "/Game/Characters/Smonk/S0/VFX/X/Textures/TP_ULT_Smonk_S0_Hair_DF.TP_ULT_Smonk_S0_Hair_DF",
                    ["UltHairMask"] = "/Game/Characters/Smonk/S0/VFX/X/Textures/TP_Ult_Smonk_S0_Hair_Mask.TP_Ult_Smonk_S0_Hair_Mask",
                    ["CMask"] = "/Game/Characters/Smonk/S0/3P/Materials/TP_Smonk_S0_C_Mask.TP_Smonk_S0_C_Mask",
                    ["Waves"] = "/Game/Characters/Smonk/S0/VFX/C/Textures/SMO_C_HUD_Waves_TFX.SMO_C_HUD_Waves_TFX",
                    // her ghost: its colours from its middle to its outline, its eyes
                    ["GhostLut"] = "/Game/Characters/Smonk/S0/VFX/X/Textures/SMO_X_Color-Soft_LUT.SMO_X_Color-Soft_LUT",
                    ["GhostEyes"] = "/Game/Characters/Smonk/S0/VFX/X/Textures/TP_Smonk_S0_Resurrection_Eyes_Mask_DF.TP_Smonk_S0_Resurrection_Eyes_Mask_DF"
                },
                new Dictionary<string, object>
                {
                    ["UltFresnelColor"] = "6BBEFF", ["UltGlow"] = 2.5, ["HornColor"] = "9AFFFF",
                    ["LegColor"] = "DC6CFF", ["ButterflyColor"] = "FFB8FF", ["LegGlowHeight"] = 0.9,
                    ["GhostRimColor"] = "FF3CC8", ["SparkColor"] = "FFB4F0"
                }),
            // Neon: TP_Sprinter_S0_VFX03_M (TP_Sprinter_S0_VFX_MI: "Ultimate", "SprintMask", "TattooMask"): the strips of
            // her suit (Masks_Body B, coloured by Colors_Body) and its lines (R) light up when she sprints and in her
            // ultimate; in her ultimate electricity also crawls over her skin (G; Bioelectricity), her tattoo and hair
            ["Sprinter"] = (
                new Dictionary<string, string>
                {
                    ["UltMasks"] = "/Game/Characters/Sprinter/S0/3P/Materials/TP_Sprinter_Ult_Masks_Body01_T.TP_Sprinter_Ult_Masks_Body01_T",
                    ["UltColors"] = "/Game/Characters/Sprinter/S0/3P/Materials/TP_Sprinter_Ult_Colors_Body01_T.TP_Sprinter_Ult_Colors_Body01_T",
                    ["Bioelectricity"] = "/Game/Characters/Sprinter/S0/3P/Materials/TP_Sprinter_Ult_Bioelectricity01_T.TP_Sprinter_Ult_Bioelectricity01_T",
                    ["TattooMask"] = "/Game/Characters/Sprinter/S0/3P/Materials/TP_Sprinter_Ult_Masks_Tattoo01_T.TP_Sprinter_Ult_Masks_Tattoo01_T",
                    ["HairMask"] = "/Game/Characters/Sprinter/S0/3P/Materials/TP_Sprinter_Ult_Masks_Hair01_T.TP_Sprinter_Ult_Masks_Hair01_T"
                },
                new Dictionary<string, object>
                {
                    ["ElectricColor"] = "2BD8FF", ["RimColor"] = "4FC8FF"
                }),
            // Jett: her arms' wind tattoos (her own material, TP_Wushu_S0_Tattoo: Loop_Anim while her ultimate is up,
            // Burst_Anim as she casts): smoke streams and a burst of wisps on her bare arms (TP_Wushu_S0_Mask G)
            ["Wushu"] = (
                new Dictionary<string, string>
                {
                    ["Mask"] = "/Game/Characters/Wushu/S0/VFX/Textures/TP_Wushu_S0_Mask.TP_Wushu_S0_Mask",
                    ["Smoke"] = "/Game/Characters/Wushu/S0/VFX/Textures/SmokeStream_9x2_TFX.SmokeStream_9x2_TFX",
                    ["Burst"] = "/Game/Characters/Wushu/S0/VFX/Textures/Arm_Tattoo_Burst.Arm_Tattoo_Burst"
                },
                new Dictionary<string, object>
                {
                    ["WindColor"] = "45E6F2", ["WindStrength"] = 1.5, ["ArmIslandU"] = 0.208, ["ArmIslandV"] = 0.2534
                }),
            // Phoenix: his ultimate's / healing fire's overlay (body_Overlays_MI, Phoenix_Body_Overlay_Heal_MI): the
            // tex_tile_07 flames scrolling over him where ULT_Masks R is, the chest honeycomb (G) glowing
            ["Phoenix"] = (
                new Dictionary<string, string>
                {
                    ["Masks"] = "/Game/Characters/Phoenix/S0/3P/Materials/ULT_overlays/ULT_Masks.ULT_Masks",
                    ["Flames"] = "/Game/Characters/Phoenix/S0/VFX/Textures/tex_tile_07.tex_tile_07"
                },
                new Dictionary<string, object>
                {
                    ["FlameScaleU"] = 3.0, ["FlameScaleV"] = 5.0, ["FlameSpeed"] = 0.275, ["FlamePower"] = 6.0,
                    ["FlameColorA"] = "FFC24A", ["FlameColorB"] = "FF6A1A"
                }),
            // KAY/O: his teal panels glow; each ability's overlay (m_overlay_3P_01: mi_Q/C/E/X_overlay_01) recolours them
            // (ColorStable): his flash lime, his frag blue, his knife purple with a purple outline (ColorFresnel), his
            // ultimate violet and flickering (UnstableColor, StableUnstable); tex_pixels_01: the flicker's scanlines
            ["Grenadier"] = (
                new Dictionary<string, string>
                {
                    ["Pixels"] = "/Game/Characters/Grenadier/S0/VFX/Textures/tex_pixels_01.tex_pixels_01"
                },
                new Dictionary<string, object>
                {
                    ["FlashColor"] = "8CE81A" /* (A4BC00 in game: its bloom reads lime) */, ["IdleColor"] = "00E8C6", ["FragColor"] = "2841FF", ["KnifeColor"] = "9550FF", ["KnifeFresnel"] = "9500EE",
                    ["UltColor"] = "9570FF"
                }),
            // Omen: his ultimate (From the Shadows). Cast: Omen_Overlay_Ally_MI turns him into shadow (Spectre colours)
            // with cyan cracks (TP_Wraith_S0_MAT_Emissive, Emissive_Color_A/B) and dissolves him; his ghost (the ethereal
            // form, a second model: see UltForms) drawn by Ethereal_Form_ALLY: lavender to indigo, a dark hood, cyan
            // eyes, tearing away into shadow at its bottom
            ["Wraith"] = (
                new Dictionary<string, string>
                {
                    ["Cracks"] = "/Game/Characters/Wraith/S0/3P/Materials/Omen_Overlays/TP_Wraith_S0_MAT_Emissive.TP_Wraith_S0_MAT_Emissive",
                    ["Dissolve"] = "/Game/VFX/Textures/NoiseDissolve/Wraith_4Xtile_Grayscale.Wraith_4Xtile_Grayscale",
                    ["GhostEyes"] = "/Game/Characters/Wraith/S0/Ability_X/3P/VFX/Textures/eyes_mask.eyes_mask",
                    ["GhostNoise"] = "/Game/Characters/Wraith/S0/VFX/Textures/Wraith_4Xtile90.Wraith_4Xtile90"
                },
                new Dictionary<string, object>
                {
                    ["SpectreColor"] = "180E20", ["SpectreColorB"] = "463059", ["CrackColorA"] = "00FFFF", ["CrackColorB"] = "2AB3FF",
                    ["BodyColorLower"] = "9598BC", ["BodyColorUpper"] = "4E4D89", ["HeadColorBottom"] = "30316C",
                    ["HeadColorTop"] = "151434", ["EyeColor"] = "00FFFF", ["TearawayColor"] = "090D19"
                }),
            // Yoru: his ultimate (Dimensional Drift): mi_ultimate_X_materialOverlay_3P_0/_3 turn him deep blue (BaseColor),
            // a light blue sheen (CoreColor, InnerColor), cells drifting over him (tex_noise_04 tiled 3x3, PanSpeed), a
            // white rim on his hair (FresnelColor); his mask on his face (see UltProps)
            ["Stealth"] = (
                new Dictionary<string, string>
                {
                    ["Cells"] = "/Game/Characters/Stealth/S0/VFX/Textures/tex_noise_04.tex_noise_04"
                },
                new Dictionary<string, object>
                {
                    ["BaseColor"] = "0308BC", ["CoreColor"] = "71ABFF", ["InnerColor"] = "0095FF", ["CellTile"] = 3.0,
                    ["PanSpeed"] = 0.15
                }),
            // Veto: his ultimate turns him into his symbiote form (a second model, see UltForms), which spreads over him
            // in bubbling cells with a glowing edge (Pine_UltTeleportWPO_TFX's cells, Pine_LUT_TransformGlow_TFX's
            // colours: dark, orange, gold)
            ["Pine"] = (
                new Dictionary<string, string>
                {
                    ["Cells"] = "/Game/Characters/Pine/S0/VFX/C/Textures/Pine_UltTeleportWPO_TFX.Pine_UltTeleportWPO_TFX"
                },
                new Dictionary<string, object>
                {
                    ["EdgeColorA"] = "C68A00", ["EdgeColorB"] = "FFBF00", ["EdgeWidth"] = 0.04
                })
        };

    // agents who change model in their ultimate: the other model, same skeleton (Veto's symbiote form, which the game
    // fades in over him with TP_Pine_S0_transform_Mat / TP_Pine_S0_Sym_transform_Mat; Clove's ghost). Sent with the 3rd person model;
    // the add-on puts it on the same rig, hidden until "Ult form" is raised
    private static readonly Dictionary<string, string> UltForms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pine"] = "/Game/Characters/Pine/S0/3P/Models/TP_Pine_Sym_S0_Skelmesh.TP_Pine_Sym_S0_Skelmesh",
        // Omen's ghost as he arrives (FXC_Wraith_X_SwapMesh, Ethereal_Form_ALLY)
        ["Wraith"] = "/Game/Characters/Wraith/S0/Ability_X/3P/Models/ABTP_Wraith_S0_X_New_Skelmesh.ABTP_Wraith_S0_X_New_Skelmesh",
        // Clove's ghost as she comes back (drawn by the game with 3P_SMO_X_Dismiss-Formless_MAT)
        ["Smonk"] = "/Game/Characters/Smonk/S0/Ability_X/3P/Models/AB_Smonk_S0_X_Resurrection_Skelmesh.AB_Smonk_S0_X_Resurrection_Skelmesh"
    };

    // props the game puts on an agent in her ultimate: codename -> (static mesh, its material, bone, offset in the
    // agent's frame, cm: forward, right, up). Yoru's mask (FXC_Stealth_X_MaskCosmetics, placed by the game's code: set
    // on his face by eye); the add-on shows it while the ultimate is on
    private static readonly Dictionary<string, (string Mesh, string Material, string Bone, FVector Offset)> UltProps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Stealth"] = ("/Game/Characters/Stealth/S0/VFX/Meshes/AB_Stealth_S0_X_Staticmesh.AB_Stealth_S0_X_Staticmesh",
            "/Game/Characters/Stealth/S0/3P/Materials/TP_Stealth_S0_Acc_MI.TP_Stealth_S0_Acc_MI", "Head", new FVector(6f, 0f, -3.5f))
    };

    public static void AddUltForm(List<ExportPart> parts)
    {
        AddUltProp(parts);
        var thirdPerson = parts.Select(p => p.MeshPath ?? "").FirstOrDefault(p => p.Contains("/3P/", StringComparison.OrdinalIgnoreCase) &&
                                                                                  p.Contains("/Characters/", StringComparison.OrdinalIgnoreCase));
        if (thirdPerson is null) return;
        var codename = thirdPerson[(thirdPerson.IndexOf("/Characters/", StringComparison.OrdinalIgnoreCase) + "/Characters/".Length)..].Split('/')[0];
        if (!UltForms.TryGetValue(codename, out var meshPath)) return;
        try
        {
            if (AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(meshPath, out USkeletalMesh mesh))
                ExportHelpers.Mesh(mesh, parts);
        }
        catch (Exception)
        {
            // missing from this game version: no ult form
        }
    }

    private static void AddUltProp(List<ExportPart> parts)
    {
        var thirdPerson = parts.Select(p => p.MeshPath ?? "").FirstOrDefault(p => p.Contains("/3P/", StringComparison.OrdinalIgnoreCase) &&
                                                                                  p.Contains("/Characters/", StringComparison.OrdinalIgnoreCase));
        if (thirdPerson is null) return;
        var codename = thirdPerson[(thirdPerson.IndexOf("/Characters/", StringComparison.OrdinalIgnoreCase) + "/Characters/".Length)..].Split('/')[0];
        if (!UltProps.TryGetValue(codename, out var prop)) return;
        try
        {
            if (!AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(prop.Mesh, out UStaticMesh mesh)) return;
            var index = ExportHelpers.SMesh(mesh, parts);
            if (index < 0) return;
            parts[index].Part = "UltProp";
            parts[index].AttachToBone = new PartBoneAttachment(prop.Bone, prop.Offset, FRotator.ZeroRotator, FVector.OneVector);
            if (AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(prop.Material, out UMaterialInstanceConstant material))
                ExportHelpers.OverrideMaterials([material], parts[index]);
        }
        catch (Exception)
        {
            // missing from this game version: no prop
        }
    }

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
