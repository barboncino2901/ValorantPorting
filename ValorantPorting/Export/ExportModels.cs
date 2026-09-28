using System.Collections.Generic;
using CUE4Parse.UE4.Objects.Core.Math;
using Newtonsoft.Json;

namespace ValorantPorting.Export;

public class ExportPart
{
    public List<ExportAttatchment> Attatchments = new();
    public List<ExportMaterial> Materials = new();
    public string MeshName;
    public string MeshPath;
    public List<ExportMaterial> OverrideMaterials = new();
    public string? Part;
    public List<ExportMaterial> StyleMaterials = new();

    public List<int> DisabledSections = new(); // sections the game never draws (leftovers under cloth/body parts)

    // Unreal material slot of each mesh section (Blender gets one material slot per section)
    [JsonIgnore] public List<int> SectionMaterialSlots = new();
}

// Which of an agent's models to export
[System.Flags]
public enum ECharacterModels
{
    FirstPerson = 1,
    ThirdPerson = 2,
    CharacterSelect = 4,
    All = FirstPerson | ThirdPerson | CharacterSelect
}

// What the user picked besides the chroma: a weapon's upgrade level (index into its Levels, null = fully upgraded)
// and an agent's models
public record ExportChoices(int? WeaponLevel = null, ECharacterModels Models = ECharacterModels.All);

public record ExportMaterial
{
    public string BlendMode; // Opaque, Masked, Translucent, Additive, ...
    public string BaseMaterial; // the root material (shader) the instance is made from
    public string MaterialName;
    public string? MaterialNameToSwap;
    public string ParentName;
    public List<ScalarParameter> Scalars = new();
    public int SlotIndex;
    public List<TextureParameter> Textures = new();
    public List<VectorParameter> Vectors = new();
}

public record ExportAttatchment
{
    public string AttatchmentName;
    public string BoneName;
    public FVector Offset;
    public FRotator Rotation;
}

public record TextureParameter(string Name, string Value);

public record ScalarParameter(string Name, float Value);

public record VectorParameter(string Name, FLinearColor Value);