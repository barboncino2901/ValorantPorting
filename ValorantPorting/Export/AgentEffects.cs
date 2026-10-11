using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Export;

// An agent's ability particle effects (Cascade particle systems), as the game spawns them on her bones: which system,
// on which bone (offset, rotation, start delay), its emitters' modules as the game stores them (spawn rate and bursts,
// lifetime, size, colour over life, ... with their baked value tables) and their materials. The Blender add-on
// rebuilds them as particles on the agent's rig.
public static class AgentEffects
{
    // Boost: how much brighter than the game's values it's drawn in Blender (the game adds bloom; some effects need it)
    // On: its control starts switched on (an idle effect always there in game)
    // FirstPerson: on the 1st person arms (the game's EmitterTemplate1P)
    // Held: played in a loop, its control its strength (0 none .. 1 full) rather than a one-shot's progress
    public record Spawn(string Effect, string System, string Socket, FVector Offset, FRotator Rotation, float Delay, List<JObject> Emitters, float Boost = 1f, bool On = false, bool FirstPerson = false, bool Held = false, bool Timed = false, float Repeat = 0f);
    // (Repeat: a burst played again every so many seconds while its control is on)

    // agent codename -> effect containers to send, by the name of the control that switches them on in Blender
    private static readonly Dictionary<string, (string Control, string Container)[]> Agents = new(StringComparer.OrdinalIgnoreCase)
    {
        // Neon's Overdrive: zaps on her elbow and hands, light in her hands
        ["Sprinter"] = [("FX: ultimate", "/Game/Characters/Sprinter/S0/Ability_X/FXC_Sprinter_X_UltimateActive_Cosmetics")],
        // Reyna: her ultimate (Empress: chest flare, wisps on her chest, head and shoulders, eye trail), its cast
        // burst, and Dismiss (chest flare, eye trail)
        // (her Dismiss is a material: rebuilt as a shader, "Glow: dismiss")
        ["Vampire"] = [("FX: ultimate", "/Game/Characters/Vampire/S0/Ability_X/FXC_Vampire_X_Frenzy_WorldPostProcess"),
                       ("FX: ultimate cast", "/Game/Characters/Vampire/S0/Ability_X/FXC_Vampire_X_Frenzy_Cast")],
        // KAY/O: the nanogel on his elbows while he holds an ability (each its own colour); his ultimate's lightning
        // and sparkles are repeated all over him instead (Repeated: on while his ultimate is)
        ["Grenadier"] = [("FX: flash (Q)", "/Game/Characters/Grenadier/S0/Ability_4/FXC_Grenadier_FlashEquip_MaterialSwap"),
                         ("FX: frag (C)", "/Game/Characters/Grenadier/S0/Ability_Q/FXC_Grenadier_StickyEquip_MaterialSwap"),
                         ("FX: knife (E)", "/Game/Characters/Grenadier/S0/Ability_E/FXC_Grenadier_E_Equip_MaterialSwap")],
        // Yoru: the rift he tears open with his hand to take his mask from, as he casts his ultimate
        ["Stealth"] = [("FX: mask portal", "/Game/Characters/Stealth/S0/Ability_X/FXC_Stealth_X_MaskPortal")],
        // Phoenix: flames on his shoulders and elbows in his ultimate, embers on his shoulders while healing in his fire
        ["Phoenix"] = [("FX: ultimate", "/Game/Characters/Phoenix/S0/Ability_X/Production/FXC_Phoenix_X_Active_Production"),
                       ("FX: heal", "/Game/Characters/Phoenix/S0/Passive_Fire/Production/FXC_Phoenix_HealingFire_Production")]
    };

    // particle systems the game spawns from its own code (not an effect container): agent codename -> (control, system,
    // bone, starts on, offset, rotation)
    private static readonly Dictionary<string, (string Control, string System, string Bone, bool On, FVector Offset, FRotator Rotation)[]> Systems = new(StringComparer.OrdinalIgnoreCase)
    {
        // Phoenix: his ultimate's limb flames (the game puts them on his shoulders and elbows) on more of him: his hands,
        // chest, neck, hips and knees
        ["Phoenix"] =
        [
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "R_Hand", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "L_Hand", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "Spine2", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "Spine4", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "Neck", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "R_Hip", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "L_Hip", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "R_Knee", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/Ability_X/ULT_LimbFlames", "L_Knee", false, FVector.ZeroVector, FRotator.ZeroRotator)
        ],
        // KAY/O's face screen (Comp_Grenadier_FaceTracking plays one by what he does): idle, then each ability's
        ["Grenadier"] =
        [
            ("Face: idle", "/Game/Characters/Grenadier/S0/VFX/ps_Idle_faceExpression_02", "Head", true, FVector.ZeroVector, FRotator.ZeroRotator),
            ("Face: flash (Q)", "/Game/Characters/Grenadier/S0/VFX/ps_Q_faceExpression_01", "Head", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("Face: frag (C)", "/Game/Characters/Grenadier/S0/VFX/ps_C_faceExpression_01", "Head", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("Face: knife (E)", "/Game/Characters/Grenadier/S0/VFX/ps_E_faceExpression_01", "Head", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("Face: ultimate", "/Game/Characters/Grenadier/S0/VFX/ps_X_faceExpression_01", "Head", false, FVector.ZeroVector, FRotator.ZeroRotator)
        ],
        // Viper: green smoke pouring from her hands while she holds her ultimate (FXC_Pandemic_X_EquippedUntilCast;
        // its emitters hang on her hands themselves: LocationBoneSocket); the pit itself (a map-sized smoke field) is left out
        // (rebuilt in the add-on as its own gas plume: its sprites look flat in Blender)
        // Yoru: his rift seen in 3rd person too (the game shows its 1st person version only to him), where his left
        // hand tears it in his mask-pulling animation
        // (fixed in front of him where it opens, as in game: where his mask-pulling animation has his left weapon point
        // as it starts, before it moves to his hand)
        ["Stealth"] = [("FX: mask portal", "/Game/Characters/Stealth/S0/VFX/ps_ultMaskPortal_01", "Root", false,
            new FVector(93.3f, -15.9f, 150.9f), new FRotator(-168f, -48f, -120f))],
        // Omen's ultimate (FXC_Wraith_X_GlobalTeleport_Equip / _Depart, the ghost's arrival): the shadow orb in his
        // hand while he holds it, the shadows swirling round him as he casts it, and as his ghost arrives
        ["Wraith"] =
        [
            ("FX: ult hold", "/Game/Characters/Wraith/S0/VFX/Ability_X/Wraith_Teleport_EquipLoop_ALLY", "Root", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ult cast", "/Game/Characters/Wraith/S0/VFX/Ability_X/Wraith_Teleport_Activate_ALLY", "Root", false, FVector.ZeroVector, FRotator.ZeroRotator),
            ("FX: ult arrive", "/Game/Characters/Wraith/S0/VFX/Ability_X/Specter_Arrive", "Root", false, FVector.ZeroVector, FRotator.ZeroRotator)
        ]
    };

    // systems played in a loop at a strength (their control: 0 none .. 1 full)
    private static readonly HashSet<string> HeldSystems = new(StringComparer.OrdinalIgnoreCase) { "Pandemic_X_3P_Equip" };

    // agents whose effects also go on the 1st person arms
    private static readonly HashSet<string> FirstPersonAgents = new(StringComparer.OrdinalIgnoreCase) { "Stealth", "Sprinter", "Phoenix", "Wraith", "Vampire", "Grenadier" };

    // 1st person systems moved (cm, the bone's frame): Yoru's rift pushed back from his eyes so his mask is halfway
    // through it (the game draws it over a closer mask)
    private static readonly Dictionary<string, FVector> FirstPersonShift = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ps_ultMaskPortal_01"] = new FVector(0f, 0f, 6f)
    };

    // bursts repeated on an agent while an effect is on, borrowed from her character select intro for a fuller look:
    // codename -> (control, system, bone, offset, first delay, every seconds). Neon's ultimate: zaps hopping over her
    // hands and backpack, bolts along her arms, her eyes and hair crackling
    private static readonly Dictionary<string, (string Control, string System, string Bone, FVector Offset, float Delay, float Repeat)[]> Repeated = new(StringComparer.OrdinalIgnoreCase)
    {
        // KAY/O's ultimate: his lightning bursts (ps_nanogel, EtherBurst) and sparkles hopping all over him
        ["Grenadier"] =
        [
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "R_Hand", new FVector(0f, 0f, 0f), 0.0f, 0.7f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "L_Hand", new FVector(0f, 0f, 0f), 0.3f, 0.8f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "R_Elbow", new FVector(0f, 0f, 0f), 0.15f, 0.95f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "L_Elbow", new FVector(0f, 0f, 0f), 0.55f, 0.9f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "R_Shoulder", new FVector(0f, 0f, 0f), 0.4f, 1.1f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "L_Shoulder", new FVector(0f, 0f, 0f), 0.75f, 1.05f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "Spine3", new FVector(0f, 0f, 0f), 0.2f, 1.2f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "Spine1", new FVector(0f, 0f, 0f), 0.65f, 1.3f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "R_Hip", new FVector(0f, 0f, 0f), 0.35f, 1.25f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "L_Hip", new FVector(0f, 0f, 0f), 0.9f, 1.15f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "R_Knee", new FVector(0f, 0f, 0f), 0.1f, 1.4f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "L_Knee", new FVector(0f, 0f, 0f), 0.5f, 1.35f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_nanogel_R_3P", "Head", new FVector(0f, 0f, 0f), 0.8f, 1.6f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_sparkles", "Spine3", new FVector(0f, 0f, 0f), 0.0f, 1.0f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_sparkles1", "Spine2", new FVector(0f, 0f, 0f), 0.5f, 1.1f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_sparkles", "R_Hand", new FVector(0f, 0f, 0f), 0.25f, 0.9f),
            ("FX: ultimate", "/Game/Characters/Grenadier/S0/VFX/Ability_X/ps_sparkles1", "L_Hand", new FVector(0f, 0f, 0f), 0.7f, 0.95f)
        ],
        // Phoenix's ultimate: sparks off his hands and chest, embers at his collar and hips (his character select
        // intro's)
        ["Phoenix"] =
        [
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/CS/ps_cs_sparkOnly_01", "R_Hand", new FVector(0f, 0f, 0f), 0.2f, 1.0f),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/CS/ps_cs_sparkOnly_01", "L_Hand", new FVector(0f, 0f, 0f), 0.65f, 1.1f),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/CS/ps_cs_sparkOnly_01", "Spine2", new FVector(0f, 0f, 0f), 0.35f, 1.3f),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/CS/ps_cs_collarEmbers_01", "Neck", new FVector(0f, 0f, 0f), 0.1f, 1.2f),
            ("FX: ultimate", "/Game/Characters/Phoenix/S0/VFX/CS/ps_cs_collarEmbers_01", "Pelvis", new FVector(0f, 0f, 0f), 0.7f, 1.5f)
        ],
        ["Sprinter"] =
        [
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "R_Index3", new FVector(0.0f, 0.0f, 0.0f), 0.0f, 0.55f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "R_Ring3", new FVector(0.0f, 0.0f, 0.0f), 0.2f, 0.7f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "R_Hand", new FVector(-6f, 0f, 5f), 0.35f, 0.62f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "R_Middle1", new FVector(0.0f, 0.0f, 0.0f), 0.5f, 0.8f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "L_Index3", new FVector(0.0f, 0.0f, 0.0f), 0.1f, 0.58f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "L_Middle3", new FVector(0.0f, 0.0f, 0.0f), 0.42f, 0.74f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "L_Ring3", new FVector(0.0f, 0.0f, 0.0f), 0.27f, 0.66f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "L_Thumb3", new FVector(0.0f, 0.0f, 0.0f), 0.6f, 0.9f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "L_Hand", new FVector(-6f, 0f, 5f), 0.15f, 0.68f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "Backpack", new FVector(0f, 2f, -6f), 0.05f, 0.85f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_X_OutOfAmmo_3P_LHand01", "Backpack", new FVector(0f, -5f, -6f), 0.48f, 0.95f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_C/work/Sprinter_C_Cast_3P_RArm01", "R_Hand", new FVector(5f, 0f, 0f), 0.0f, 1.1f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_C/work/Sprinter_C_Cast_3P_RArm01", "R_Hand", new FVector(-10f, -4f, 5f), 0.55f, 1.3f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_C/work/Sprinter_C_Cast_3P_RArm01", "L_Hand", new FVector(-5f, 0f, 0f), 0.3f, 1.2f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_C/work/Sprinter_C_Cast_3P_RArm01", "R_Hand", new FVector(0f, 3f, -3f), 0.85f, 0.95f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_C/work/Sprinter_C_Cast_3P_RArm01", "L_Hand", new FVector(4f, -3f, 2f), 0.7f, 1.05f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_C/work/Sprinter_C_Cast_3P_RArm01", "L_Hand", new FVector(-8f, 2f, 4f), 0.05f, 1.4f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/CharacterSelect/Sprinter_CS_EyeZap01", "L_Eyeball", new FVector(1f, 0f, 0f), 0.4f, 2.4f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/CharacterSelect/Sprinter_CS_EyeZap01", "R_Eyeball", new FVector(-1f, 0f, 0f), 0.4f, 2.4f),
            ("FX: ultimate", "/Game/Characters/Sprinter/S0/VFX/Ability_X/work/Sprinter_CS_HairSparkle", "R_Bang1_03", new FVector(-1f, -0.5f, 0f), 1.0f, 2.0f)
        ]
    };

    // systems hung on another bone than the game's: its weapon points only reach the hand in animations (Yoru's rift)
    private static readonly Dictionary<string, string> SocketOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ps_ultMaskPortal_3P"] = "L_Hand"
    };

    // systems left out: they don't look like the game in Blender (Neon's head glow, which the game hides with depth)
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "Sprinter_X_Active_3P_Hair01",
        // (KAY/O's frag gel on the grenade in his hand: on his weapon socket, which isn't at his hand in Blender)
        "ps_nanoGel_02" };

    // ability models with effects on their bones: model name -> (control, particle system, bones)
    private static readonly Dictionary<string, (string Control, string System, string[] Bones, float Boost)> Abilities = new(StringComparer.OrdinalIgnoreCase)
    {
        // Jett's Blade Storm: wind swirling around each floating knife (Ability_Wushu_X_Dagger_Production)
        ["ABTP_Wushu_S0_X_Skelmesh"] = ("FX: knives", "/Game/Characters/Wushu/S0/VFX/Ability_X/Wushu_X_3P_DaggerFloat",
            ["Knife1", "Knife2", "Knife3", "Knife4", "Knife5"], 3f),
        // (the Abilities tab's animated set uses the 1st person rig, same bones)
        ["AB_Wushu_S0_X_Skelmesh"] = ("FX: knives", "/Game/Characters/Wushu/S0/VFX/Ability_X/Wushu_X_3P_DaggerFloat",
            ["Knife1", "Knife2", "Knife3", "Knife4", "Knife5"], 3f)
    };

    // single emitters drawn differently from the game's values: "system|emitter" -> (brightness, size, tint as hex
    // or null). Tints are for materials that colour a grey texture inside their (unreadable) graph
    private static readonly Dictionary<string, (float Boost, float Scale, string? Tint)> EmitterTweaks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wushu_X_3P_DaggerFloat|GlowFlareLoop"] = (3f, 1.5f, null), // the glow at each knife's ring
        ["ULT_LimbFlames|None"] = (2f, 1f, "FF8A2E"), // Phoenix's flames on his shoulders and elbows (solar_flare_4x4: grey)
        // Reyna: grey flipbooks her materials colour pink
        // (her ult's big radianite scribbles and star sparkle don't look like the game in Blender: left out)
        ["Vampire_X_3p_ChestFlare|radianite"] = (0f, 0f, null),
        ["Vampire_E_3p_ChestFlare|radianite"] = (1.5f, 0.7f, "FF3FD2"),
        ["Vampire_X_3p_ChestFlare|a"] = (0f, 0f, null),
        // her ult's wisps: faint dark smoke off her body (rebuilt as smoke puffs in Blender)
        ["Vampire_X_Wisp_Chest|None"] = (1f, 1.3f, "2A1838"),
        ["Vampire_X_Wisp_Head|None"] = (0f, 0f, null), // (not over her head)
        ["Vampire_X_Wisp_Shoulder|None"] = (1f, 1.3f, "2A1838"),
        // her cast's flat background glow and goo cylinder draw as grey rectangles / a white streak in Blender
        ["Vampire_X_Intro|GlowBackground"] = (0f, 0f, null),
        ["Vampire_X_Intro|None|Vampire_X_Goo_Inst"] = (0f, 0f, null),
        // Neon's borrowed intro zaps: only their lightning strands (their star bursts, muzzle flashes and glows left out)
        ["Sprinter_X_OutOfAmmo_3P_LHand01|Burst"] = (0f, 0f, null), ["Sprinter_X_OutOfAmmo_3P_LHand01|MFlash"] = (0f, 0f, null),
        ["Sprinter_X_OutOfAmmo_3P_LHand01|Glow"] = (0f, 0f, null),
        ["Sprinter_CS_HairSparkle|Burst"] = (0f, 0f, null), ["Sprinter_CS_HairSparkle|MFlash"] = (0f, 0f, null),
        ["Sprinter_CS_HairSparkle|Glow"] = (0f, 0f, null),
        ["Sprinter_CS_EyeZap01|Spark Core"] = (0f, 0f, null), ["Sprinter_CS_EyeZap01|Glow"] = (0f, 0f, null),
        ["Sprinter_CS_EyeZap01|Flare"] = (0f, 0f, null),
        // KAY/O: his sparks (m_lightning_damage colours a grey flipbook with a gradient): his frag blue, his flash
        // lime, his ultimate violet
        ["ps_C_equipElements_3P|None"] = (3f, 1f, "5A8CFF"),
        ["ps_C_equipElements_3P|None|mi_lightning_damage_08"] = (0f, 0f, null), // (a filled blob in Blender)
        ["ps_trail_equip_3P|None"] = (3f, 1f, "B6FF3A"),
        ["ps_nanogel_R_3P|EtherBurst_Right"] = (6f, 2.2f, "B48CFF"),
        ["ps_nanogel_L_3P|EtherBurst_Right"] = (6f, 2.2f, "B48CFF"),
        // Yoru's rift: its liquid film, screen distortion and ground splashes are drawn by the game's own passes
        // (grey sheets in Blender)
        ["ps_ultMaskPortal_01|None|mi_ferrofluid_trail_05"] = (0f, 0f, null),
        ["ps_ultMaskPortal_01|None|m_X_distortion"] = (0f, 0f, null),
        ["ps_ultMaskPortal_01|None|mi_groundfluid_02"] = (0f, 0f, null),
        ["ps_ultMaskPortal_01|None|mi_groundfluid_03"] = (0f, 0f, null),
        ["ps_ultMaskPortal_01|None|mi_groundfluid_05"] = (0f, 0f, null),
        ["ps_ultMaskPortal_01|None|mi_flipbookQuad_05"] = (0f, 0f, null), // (its smoke and ripples: grey squares)
        ["ps_ultMaskPortal_01|None|m_ripples_01"] = (0f, 0f, null),
        ["ps_ultMaskPortal_3P|None|mi_flipbookQuad_18"] = (0f, 0f, null),
        ["ps_sparkles|Glow"] = (5f, 2f, "C8A8FF"),
        // Viper's hand smoke: its gas strands brighter and bigger (the game's bloom and distortion)
        ["Pandemic_X_3P_Equip|GasUP_L_Loop"] = (2.5f, 1.4f, null),
        ["Pandemic_X_3P_Equip|GasUP_R_Loop"] = (2.5f, 1.4f, null),
        ["Pandemic_X_3P_Equip|GasUP_L_Intro"] = (2.5f, 1.4f, null),
        ["Pandemic_X_3P_Equip|GasUP_Intro"] = (2.5f, 1.4f, null),
        ["ps_sparkles1|Glow"] = (5f, 2f, "C8A8FF")
    };

    // The particle effects an animation plays in game (its notifies: AnimNotify_PlayParticleEffect, the game's own
    // PlayParticle notify), each at its moment on its bone: an agent's character select intro (Neon's zaps, eye sparks,
    // tattoo glow), ability casts. Background-only effects (the intro's backdrop) are left out.
    public static List<Spawn>? FromAnimation(IEnumerable<string> objectPaths)
    {
        var spawns = new List<Spawn>();
        var systems = new Dictionary<string, List<JObject>?>(StringComparer.OrdinalIgnoreCase);
        List<JObject>? EmittersOf(string path)
        {
            if (systems.TryGetValue(path, out var cached)) return cached;
            List<JObject>? result = null;
            try
            {
                if (AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(path, out UObject system) && system.ExportType == "ParticleSystem" &&
                    !Skipped.Contains(system.Name))
                    result = Emitters(system) is { Count: > 0 } emitters ? emitters : null;
            }
            catch (Exception)
            {
                // unreadable / not a Cascade system
            }
            return systems[path] = result;
        }

        foreach (var objectPath in objectPaths)
        {
            try
            {
                if (!AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(objectPath, out CUE4Parse.UE4.Assets.Exports.Animation.UAnimSequenceBase animation) ||
                    animation.Notifies is not { } notifies) continue;
                foreach (var notify in notifies)
                {
                    if (notify.Notify is not { IsNull: false } index || index.Load() is not { } notifyObject) continue;
                    var time = (float) Math.Max(0, notify.LinkValue + notify.TriggerTimeOffset);
                    string? path = null, socket = null;
                    FVector offset = FVector.ZeroVector;
                    FRotator rotation = FRotator.ZeroRotator;
                    if (notifyObject.TryGetValue(out FPackageIndex template, "PSTemplate") && !template.IsNull)
                    {
                        path = template.ResolvedObject?.GetPathName();
                        socket = notifyObject.GetOrDefault<FName>("SocketName").Text;
                        offset = notifyObject.GetOrDefault("LocationOffset", FVector.ZeroVector);
                        rotation = notifyObject.GetOrDefault("RotationOffset", FRotator.ZeroRotator);
                    }
                    else
                    {
                        // the game's own notify: a list of particles, each with its attach point and offsets
                        var json = JObject.Parse(JsonConvert.SerializeObject(notifyObject))["Properties"] as JObject;
                        var infos = json?.Properties().FirstOrDefault(p => p.Name.StartsWith("Particle Info"))?.Value as JArray;
                        foreach (var info in infos?.OfType<JObject>() ?? [])
                        {
                            string? Field(string start) => info.Properties().FirstOrDefault(p => p.Name.StartsWith(start))?.Value?.ToString();
                            JToken? Token(string start) => info.Properties().FirstOrDefault(p => p.Name.StartsWith(start))?.Value;
                            var asset = (Token("Particle_") as JObject)?["AssetPathName"]?.ToString();
                            if (string.IsNullOrEmpty(asset) || asset.Split('/').Last().StartsWith("BG", StringComparison.OrdinalIgnoreCase)) continue;
                            if (Token("IsFirstPerson") is { } first && first.Type == JTokenType.Boolean && first.Value<bool>()) continue;
                            var o = Token("ParticleSpawnLocationOffset") as JObject;
                            var r = Token("ParticleSpawnRotationOffset") as JObject;
                            if (EmittersOf(asset) is { } infoEmitters)
                                spawns.Add(new Spawn("FX: animation effects", asset.Split('.').Last(), Field("VFXAttachPoint") ?? "Root",
                                    new FVector(o?["X"]?.Value<float>() ?? 0, o?["Y"]?.Value<float>() ?? 0, o?["Z"]?.Value<float>() ?? 0),
                                    new FRotator(r?["Pitch"]?.Value<float>() ?? 0, r?["Yaw"]?.Value<float>() ?? 0, r?["Roll"]?.Value<float>() ?? 0),
                                    time, infoEmitters, 1f, true, Timed: true));
                        }
                        continue;
                    }
                    if (path is null || EmittersOf(path) is not { } emitters) continue;
                    spawns.Add(new Spawn("FX: animation effects", path.Split('.').Last(), string.IsNullOrEmpty(socket) || socket == "None" ? "Root" : socket,
                        offset, rotation, time, emitters, 1f, true, Timed: true));
                }
            }
            catch (Exception)
            {
                // unreadable: none from it
            }
        }
        return spawns.Count == 0 ? null : spawns;
    }

    public static List<Spawn>? ForAbility(IEnumerable<ExportPart> parts)
    {
        var spawns = new List<Spawn>();
        foreach (var part in parts)
        {
            var name = (part.MeshPath ?? "")[((part.MeshPath ?? "").LastIndexOf('.') + 1)..];
            if (!Abilities.TryGetValue(name, out var effect)) continue;
            try
            {
                if (!AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(effect.System, out UObject system)) continue;
                var emitters = Emitters(system);
                if (emitters.Count == 0) continue;
                foreach (var bone in effect.Bones)
                    spawns.Add(new Spawn(effect.Control, system.Name, bone, FVector.ZeroVector, FRotator.ZeroRotator, 0f, emitters, effect.Boost));
            }
            catch (Exception)
            {
                // missing from this game version: left out
            }
        }
        return spawns.Count == 0 ? null : spawns;
    }

    public static List<Spawn>? For(IEnumerable<ExportPart> parts)
    {
        var path = parts.Select(p => p.MeshPath ?? "").FirstOrDefault(p => p.Contains("/Characters/", StringComparison.OrdinalIgnoreCase));
        if (path is null) return null;
        var codename = path[(path.IndexOf("/Characters/", StringComparison.OrdinalIgnoreCase) + "/Characters/".Length)..].Split('/')[0];
        var containers = Agents.GetValueOrDefault(codename) ?? [];

        var spawns = new List<Spawn>();
        foreach (var (control, systemPath, bone, on, offset, rotation) in Systems.GetValueOrDefault(codename) ?? [])
        {
            try
            {
                if (!AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(systemPath, out UObject system)) continue;
                var emitters = Emitters(system);
                if (emitters.Count > 0)
                    spawns.Add(new Spawn(control, system.Name, bone, offset, rotation, 0f, emitters, 1f, on, Held: HeldSystems.Contains(system.Name)));
            }
            catch (Exception)
            {
                // missing from this game version: left out
            }
        }
        foreach (var (control, systemPath, bone, offset, delay, repeat) in Repeated.GetValueOrDefault(codename) ?? [])
        {
            try
            {
                if (!AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(systemPath, out UObject system)) continue;
                if (Emitters(system) is { Count: > 0 } emitters)
                {
                    spawns.Add(new Spawn(control, system.Name, bone, offset, FRotator.ZeroRotator, delay, emitters, Repeat: repeat));
                    // the same on her 1st person arms, where it's on her hands and arms (not her backpack, eyes, hair)
                    if (FirstPersonAgents.Contains(codename) && System.Text.RegularExpressions.Regex.IsMatch(bone, @"^[LR]_(Hand|Index|Middle|Ring|Pinky|Thumb)"))
                        spawns.Add(new Spawn(control, system.Name, bone, offset, FRotator.ZeroRotator, delay, emitters, FirstPerson: true, Repeat: repeat));
                }
            }
            catch (Exception)
            {
                // missing from this game version: left out
            }
        }
        foreach (var (control, container) in containers)
        {
            try
            {
                foreach (var export in AppVM.CUE4ParseVM.Provider.LoadPackageObjects(container))
                {
                    if (!export.ExportType.StartsWith("Comp_FXC_SpawnParticle", StringComparison.Ordinal)) continue;
                    // (on the held ability model, not on the agent)
                    if (export.ExportType.Contains("Equippable", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!export.TryGetValue(out UObject system, "EmitterTemplate3P") || Skipped.Contains(system.Name)) continue;
                    var emitters = Emitters(system);
                    if (emitters.Count == 0) continue;
                    spawns.Add(new Spawn(control, system.Name, SocketOverrides.GetValueOrDefault(system.Name) ?? export.GetOrDefault<FName>("AttachPoint3P").Text,
                        export.GetOrDefault("EmitterOffset3P", FVector.ZeroVector),
                        export.GetOrDefault("EmitterRotation3P", FRotator.ZeroRotator),
                        Delay(export), emitters));
                    // its 1st person version, for the agents whose 1st person effects are done (Yoru's rift)
                    if (FirstPersonAgents.Contains(codename) && export.TryGetValue(out UObject system1P, "EmitterTemplate1P") &&
                        Emitters(system1P) is { Count: > 0 } emitters1P)
                        spawns.Add(new Spawn(control, system1P.Name, export.GetOrDefault<FName>("AttachPoint1P").Text,
                            export.GetOrDefault("EmitterOffset1P", FVector.ZeroVector) + FirstPersonShift.GetValueOrDefault(system1P.Name),
                            export.GetOrDefault("EmitterRotation1P", FRotator.ZeroRotator),
                            Delay(export), emitters1P, FirstPerson: true));
                }
            }
            catch (Exception)
            {
                // missing from this game version: left out
            }
        }
        return spawns.Count == 0 ? null : spawns;
    }

    // seconds after the effect starts (stored as a float or a double)
    private static float Delay(UObject spawn) =>
        spawn.TryGetValue(out double asDouble, "ParticleStartDelay3P") ? (float) asDouble : spawn.GetOrDefault<float>("ParticleStartDelay3P");

    private static List<JObject> Emitters(UObject system)
    {
        var emitters = new List<JObject>();
        foreach (var emitterIndex in system.GetOrDefault("Emitters", Array.Empty<FPackageIndex>()))
        {
            if (!emitterIndex.TryLoad(out var emitter)) continue;
            var lods = emitter.GetOrDefault("LODLevels", Array.Empty<FPackageIndex>());
            if (lods.Length == 0 || !lods[0].TryLoad(out var lod)) continue;
            if (!lod.GetOrDefault("bEnabled", true)) continue;

            var modules = new JArray();
            foreach (var name in new[] { "RequiredModule", "SpawnModule", "TypeDataModule" })
                if (lod.TryGetValue(out UObject module, name)) modules.Add(Module(module));
            foreach (var moduleIndex in lod.GetOrDefault("Modules", Array.Empty<FPackageIndex>()))
                if (moduleIndex.TryLoad(out var module) && module.GetOrDefault("bEnabled", true)) modules.Add(Module(module));

            var emitterName = emitter.GetOrDefault<FName>("EmitterName").Text;
            var description = new JObject
            {
                ["Name"] = emitterName,
                ["Type"] = emitter.ExportType,
                ["Modules"] = modules
            };
            if (lod.TryGetValue(out UObject required, "RequiredModule") && required.TryGetValue(out UMaterialInterface material, "Material"))
                description["Material"] = Material(material);
            var materialName = description["Material"]?["Name"]?.ToString() ?? "";
            // decals (they colour the ground under them in game: boxes in Blender)
            if (description["Material"]?["Vectors"]?["DecalColor"] != null) continue;
            // KAY/O's nanogel: its shards are pushed out of his forearm by its shader (flat panes in Blender)
            if ((description["Material"]?["BaseMaterial"]?.ToString() ?? "").StartsWith("m_nanogel", StringComparison.OrdinalIgnoreCase)) continue;
            if (EmitterTweaks.TryGetValue($"{system.Name}|{emitterName}|{materialName}", out var tweak) ||
                EmitterTweaks.TryGetValue($"{system.Name}|{emitterName}", out tweak))
            {
                if (tweak.Boost <= 0f) continue; // left out
                description["Boost"] = tweak.Boost;
                description["Scale"] = tweak.Scale;
                if (tweak.Tint != null) description["Tint"] = tweak.Tint;
            }
            // mesh particles: their mesh (and, unless the emitter overrides it, its own material)
            if (lod.TryGetValue(out UObject typeData, "TypeDataModule") && typeData.TryGetValue(out UStaticMesh mesh, "Mesh"))
            {
                ExportHelpers.Save(mesh);
                description["Mesh"] = mesh.GetPathName();
                if (!typeData.GetOrDefault<bool>("bOverrideMaterial") && mesh.Materials is { Length: > 0 } meshMaterials &&
                    meshMaterials[0]?.TryLoad(out var meshMaterial) == true && meshMaterial is UMaterialInterface own)
                    description["Material"] = Material(own);
            }
            emitters.Add(description);
        }
        return emitters;
    }

    private static JObject Module(UObject module)
    {
        var json = JObject.Parse(JsonConvert.SerializeObject(module));
        return new JObject { ["Type"] = module.ExportType, ["Properties"] = json["Properties"] ?? new JObject() };
    }

    // its name, how it's drawn, its parameters (an instance's, then its base material's defaults) and its textures
    private static JObject Material(UMaterialInterface material)
    {
        var result = new JObject { ["Name"] = material.Name };
        var textures = new JObject();
        var scalars = new JObject();
        var vectors = new JObject();
        UMaterialInterface current = material;
        for (var depth = 0; depth < 16; depth++)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(current));
            var properties = json["Properties"] as JObject ?? new JObject();
            if (result["BlendMode"] == null)
            {
                var overrides = properties["BasePropertyOverrides"];
                if (overrides?["bOverride_BlendMode"]?.Value<bool>() == true) result["BlendMode"] = overrides["BlendMode"]?.ToString();
            }
            if (current is UMaterialInstanceConstant instance)
            {
                var (instanceTextures, instanceScalars, instanceVectors) = ExportHelpers.MaterialParameters(instance);
                foreach (var t in instanceTextures) textures[t.Name] ??= t.Value;
                foreach (var s in instanceScalars) scalars[s.Name] ??= s.Value;
                foreach (var v in instanceVectors) vectors[v.Name] ??= new JArray(v.Value.R, v.Value.G, v.Value.B, v.Value.A);
            }
            if (current is UMaterial baseMaterial)
            {
                result["BaseMaterial"] = baseMaterial.Name;
                result["BlendMode"] ??= properties["BlendMode"]?.ToString() ?? "EBlendMode::BLEND_Opaque";
                result["ShadingModel"] = properties["ShadingModel"]?.ToString();
                result["TwoSided"] = properties["TwoSided"]?.Value<bool>() ?? false;
                // the base material's own parameter defaults (names in the order of its value lists)
                var cached = (json["CachedExpressionData"] ?? properties["CachedExpressionData"]) as JObject;
                if (cached != null)
                {
                    var names = new List<List<string>>();
                    foreach (var key in cached.Properties().Select(p => p.Name).Where(n => n.StartsWith("RuntimeEntries")).OrderBy(n => n.Length).ThenBy(n => n))
                        names.Add(cached[key]?["ParameterInfoSet"]?.Select(p => p["Name"]?.ToString() ?? "").ToList() ?? []);
                    if (names.Count > 1)
                    {
                        var scalarValues = cached["ScalarValues"] as JArray ?? [];
                        for (var i = 0; i < Math.Min(names[0].Count, scalarValues.Count); i++) scalars[names[0][i]] ??= scalarValues[i];
                        var vectorValues = cached["VectorValues"] as JArray ?? [];
                        for (var i = 0; i < Math.Min(names[1].Count, vectorValues.Count); i++)
                            vectors[names[1][i]] ??= new JArray(vectorValues[i]["R"], vectorValues[i]["G"], vectorValues[i]["B"], vectorValues[i]["A"]);
                    }
                }
                foreach (var reference in baseMaterial.ReferencedTextures)
                {
                    if (reference is null || !reference.TryLoad(out UTexture2D texture)) continue;
                    if (texture.Name.StartsWith("Debug", StringComparison.OrdinalIgnoreCase) || texture.Name.StartsWith("Default", StringComparison.OrdinalIgnoreCase) ||
                        texture.GetPathName().StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase)) continue;
                    ExportHelpers.Save(texture);
                    textures[texture.Name] ??= texture.GetPathName();
                }
                break;
            }
            if (current is not UMaterialInstance next || next.Parent == null || !next.Parent.TryLoad(out var parent) || parent is not UMaterialInterface parentMaterial) break;
            current = parentMaterial;
        }
        result["Textures"] = textures;
        result["Scalars"] = scalars;
        result["Vectors"] = vectors;
        return result;
    }
}
