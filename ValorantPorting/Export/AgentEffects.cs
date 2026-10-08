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
    public record Spawn(string Effect, string System, string Socket, FVector Offset, FRotator Rotation, float Delay, List<JObject> Emitters, float Boost = 1f, bool On = false);

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
        // KAY/O: the nanogel on his elbows while he holds an ability (each its own colour), and in his ultimate with
        // sparkles
        ["Grenadier"] = [("FX: flash (Q)", "/Game/Characters/Grenadier/S0/Ability_4/FXC_Grenadier_FlashEquip_MaterialSwap"),
                         ("FX: frag (C)", "/Game/Characters/Grenadier/S0/Ability_Q/FXC_Grenadier_StickyEquip_MaterialSwap"),
                         ("FX: knife (E)", "/Game/Characters/Grenadier/S0/Ability_E/FXC_Grenadier_E_Equip_MaterialSwap"),
                         ("FX: ultimate", "/Game/Characters/Grenadier/S0/Ability_X/FXC_Grenadier_X_Empowered")],
        // Phoenix: flames on his shoulders and elbows in his ultimate, embers on his shoulders while healing in his fire
        ["Phoenix"] = [("FX: ultimate", "/Game/Characters/Phoenix/S0/Ability_X/Production/FXC_Phoenix_X_Active_Production"),
                       ("FX: heal", "/Game/Characters/Phoenix/S0/Passive_Fire/Production/FXC_Phoenix_HealingFire_Production")]
    };

    // particle systems the game spawns from its own code (not an effect container): agent codename -> (control, system,
    // bone, starts on)
    private static readonly Dictionary<string, (string Control, string System, string Bone, bool On)[]> Systems = new(StringComparer.OrdinalIgnoreCase)
    {
        // KAY/O's face screen (Comp_Grenadier_FaceTracking plays one by what he does): idle, then each ability's
        ["Grenadier"] =
        [
            ("Face: idle", "/Game/Characters/Grenadier/S0/VFX/ps_Idle_faceExpression_02", "Head", true),
            ("Face: flash (Q)", "/Game/Characters/Grenadier/S0/VFX/ps_Q_faceExpression_01", "Head", false),
            ("Face: frag (C)", "/Game/Characters/Grenadier/S0/VFX/ps_C_faceExpression_01", "Head", false),
            ("Face: knife (E)", "/Game/Characters/Grenadier/S0/VFX/ps_E_faceExpression_01", "Head", false),
            ("Face: ultimate", "/Game/Characters/Grenadier/S0/VFX/ps_X_faceExpression_01", "Head", false)
        ]
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
        // KAY/O: his sparks (m_lightning_damage colours a grey flipbook with a gradient): his frag blue, his flash
        // lime, his ultimate violet
        ["ps_C_equipElements_3P|None"] = (3f, 1f, "5A8CFF"),
        ["ps_C_equipElements_3P|None|mi_lightning_damage_08"] = (0f, 0f, null), // (a filled blob in Blender)
        ["ps_trail_equip_3P|None"] = (3f, 1f, "B6FF3A"),
        ["ps_nanogel_R_3P|EtherBurst_Right"] = (3f, 1.2f, "B48CFF"),
        ["ps_nanogel_L_3P|EtherBurst_Right"] = (3f, 1.2f, "B48CFF"),
        ["ps_sparkles|Glow"] = (3f, 1.5f, "C8A8FF"),
        ["ps_sparkles1|Glow"] = (3f, 1.5f, "C8A8FF")
    };

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
        foreach (var (control, systemPath, bone, on) in Systems.GetValueOrDefault(codename) ?? [])
        {
            try
            {
                if (!AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(systemPath, out UObject system)) continue;
                var emitters = Emitters(system);
                if (emitters.Count > 0)
                    spawns.Add(new Spawn(control, system.Name, bone, FVector.ZeroVector, FRotator.ZeroRotator, 0f, emitters, 1f, on));
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
                    spawns.Add(new Spawn(control, system.Name, export.GetOrDefault<FName>("AttachPoint3P").Text,
                        export.GetOrDefault("EmitterOffset3P", FVector.ZeroVector),
                        export.GetOrDefault("EmitterRotation3P", FRotator.ZeroRotator),
                        Delay(export), emitters));
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
