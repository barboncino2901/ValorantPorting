"""Rebuilds Valorant's environment shaders (Base and two-layer Blend) for maps imported through USD.

The USD export only carries a generic material (one diffuse, normal and packed MRA texture). Valorant's environment
materials also use tints, an "AO color", vertex colors and a second texture layer mixed by vertex alpha. This module
rebuilds those materials from the material summary the app writes next to the map (parent chain, switches, colors,
scalars and texture paths).
"""
import os
import re

import bpy
import numpy

BASE_GROUP = "VP_Valorant_Base"
BLEND_GROUP = "VP_Valorant_Blend_v2"  # bump when the group changes: old .blend files keep the old one

# Master shaders whose look can't be rebuilt from these two templates: keep the USD material for them.
SPECIAL_MASTERS = re.compile(r"glass|decal|hologram|screen|lcd|lightshift|water|waterfall|smoke|vfx|unlit|sky|opacity_rgb",
                             re.IGNORECASE)

TEXTURE_SLOTS = {
    "DF": ["Diffuse", "Diffuse A", "Albedo", "Albedo A", "Texture A", "PM_Diffuse"],
    "DF B": ["Diffuse B", "Albedo B", "Texture B"],
    "MRA": ["MRA", "MRA A", "PM_SpecularMasks"],
    "MRA B": ["MRA B"],
    "NM": ["Normal", "Normal A", "Texture A Normal", "PM_Normals"],
    "NM B": ["Normal B", "Texture B Normal"],
}
NON_COLOR_SLOTS = {"MRA", "MRA B", "NM", "NM B"}
# switches that make an environment material glow (otherwise its default white "Emissive Mult" is unused)
EMISSIVE_SWITCHES = ["Use Alpha As Emissive", "Blend Emissive", "Diffuse Color Emissive", "Emissive Map Texture"]


# ----------------------------------------------------------------------------------------------------------------
# Node group construction (built once per .blend, reused by every material)
# ----------------------------------------------------------------------------------------------------------------

def _socket(group, name, kind, default=None, in_out='INPUT'):
    types = {'color': 'NodeSocketColor', 'float': 'NodeSocketFloat', 'shader': 'NodeSocketShader'}
    item = group.interface.new_socket(name=name, in_out=in_out, socket_type=types[kind])
    if default is not None:
        item.default_value = default
    return item


def _mix(nodes, blend='MIX', factor=None):
    node = nodes.new('ShaderNodeMix')
    node.data_type = 'RGBA'
    node.blend_type = blend
    if factor is not None:
        node.inputs['Factor'].default_value = factor
    return node


def _math(nodes, op, value=None, clamp=False):
    node = nodes.new('ShaderNodeMath')
    node.operation = op
    node.use_clamp = clamp
    if value is not None:
        node.inputs[1].default_value = value
    return node


def _layer(nodes, links, df, diffuse_color, tint, mra, ao_color, nm, normal_strength):
    """One material layer: tinted color darkened toward the AO color, metallic/roughness from MRA, fixed normal."""
    colored = _mix(nodes, 'MULTIPLY', 1.0)
    links.new(df, colored.inputs['A'])
    links.new(diffuse_color, colored.inputs['B'])
    tinted = _mix(nodes, 'MULTIPLY', 1.0)
    links.new(colored.outputs['Result'], tinted.inputs['A'])
    links.new(tint, tinted.inputs['B'])

    split = nodes.new('ShaderNodeSeparateColor')
    links.new(mra, split.inputs['Color'])
    ao = _mix(nodes, 'MIX')
    links.new(split.outputs['Blue'], ao.inputs['Factor'])
    links.new(ao_color, ao.inputs['A'])
    ao.inputs['B'].default_value = (1, 1, 1, 1)
    shaded = _mix(nodes, 'MULTIPLY', 1.0)
    links.new(tinted.outputs['Result'], shaded.inputs['A'])
    links.new(ao.outputs['Result'], shaded.inputs['B'])

    # Unreal normal maps are DirectX style (green down); Blender expects OpenGL style.
    nm_split = nodes.new('ShaderNodeSeparateColor')
    links.new(nm, nm_split.inputs['Color'])
    flip = _math(nodes, 'SUBTRACT')
    flip.inputs[0].default_value = 1.0
    links.new(nm_split.outputs['Green'], flip.inputs[1])
    nm_join = nodes.new('ShaderNodeCombineColor')
    links.new(nm_split.outputs['Red'], nm_join.inputs['Red'])
    links.new(flip.outputs['Value'], nm_join.inputs['Green'])
    links.new(nm_split.outputs['Blue'], nm_join.inputs['Blue'])
    normal = nodes.new('ShaderNodeNormalMap')
    links.new(nm_join.outputs['Color'], normal.inputs['Color'])
    links.new(normal_strength, normal.inputs['Strength'])

    return shaded.outputs['Result'], split.outputs['Red'], split.outputs['Green'], normal.outputs['Normal']


def _build_base_group():
    group = bpy.data.node_groups.new(BASE_GROUP, 'ShaderNodeTree')
    _socket(group, "BSDF", 'shader', in_out='OUTPUT')
    for name, kind, default in [
        ("DF", 'color', (0.5, 0.5, 0.5, 1)), ("DF Alpha", 'float', 1.0), ("MRA", 'color', (0, 0.5, 1, 1)),
        ("NM", 'color', (0.5, 0.5, 1, 1)), ("Diffuse Color", 'color', (1, 1, 1, 1)), ("Tint", 'color', (1, 1, 1, 1)),
        ("Vertex Color", 'color', (1, 1, 1, 1)), ("Use Vertex Color", 'float', 0.0), ("AO Color", 'color', (0, 0, 0, 1)),
        ("Normal Strength", 'float', 1.0), ("Emissive Mult", 'color', (0, 0, 0, 1)), ("Use Alpha as Emissive", 'float', 0.0),
        ("Use Alpha", 'float', 0.0), ("Alpha Clip", 'float', 0.333)]:
        _socket(group, name, kind, default)

    nodes, links = group.nodes, group.links
    inp = nodes.new('NodeGroupInput')
    out = nodes.new('NodeGroupOutput')
    bsdf = nodes.new('ShaderNodeBsdfPrincipled')
    links.new(bsdf.outputs['BSDF'], out.inputs['BSDF'])

    color, metallic, roughness, normal = _layer(nodes, links, inp.outputs['DF'], inp.outputs['Diffuse Color'],
                                                inp.outputs['Tint'], inp.outputs['MRA'], inp.outputs['AO Color'],
                                                inp.outputs['NM'], inp.outputs['Normal Strength'])
    vertex = _mix(nodes, 'MULTIPLY')
    links.new(inp.outputs['Use Vertex Color'], vertex.inputs['Factor'])
    links.new(color, vertex.inputs['A'])
    links.new(inp.outputs['Vertex Color'], vertex.inputs['B'])
    links.new(vertex.outputs['Result'], bsdf.inputs['Base Color'])
    links.new(metallic, bsdf.inputs['Metallic'])
    links.new(roughness, bsdf.inputs['Roughness'])
    links.new(normal, bsdf.inputs['Normal'])

    # Emission: color x Emissive Mult, optionally masked by the diffuse alpha.
    glow = _mix(nodes, 'MULTIPLY', 1.0)
    links.new(vertex.outputs['Result'], glow.inputs['A'])
    links.new(inp.outputs['Emissive Mult'], glow.inputs['B'])
    alpha_mask = _mix(nodes, 'MIX')
    links.new(inp.outputs['Use Alpha as Emissive'], alpha_mask.inputs['Factor'])
    alpha_mask.inputs['A'].default_value = (1, 1, 1, 1)
    links.new(inp.outputs['DF Alpha'], alpha_mask.inputs['B'])
    masked_glow = _mix(nodes, 'MULTIPLY', 1.0)
    links.new(glow.outputs['Result'], masked_glow.inputs['A'])
    links.new(alpha_mask.outputs['Result'], masked_glow.inputs['B'])
    links.new(masked_glow.outputs['Result'], bsdf.inputs['Emission Color'])
    bsdf.inputs['Emission Strength'].default_value = 1.0

    # Masked (cut-out) materials: alpha = diffuse alpha above the clip value.
    cut = _math(nodes, 'GREATER_THAN')
    links.new(inp.outputs['DF Alpha'], cut.inputs[0])
    links.new(inp.outputs['Alpha Clip'], cut.inputs[1])
    use_cut = nodes.new('ShaderNodeMix')
    use_cut.data_type = 'FLOAT'
    links.new(inp.outputs['Use Alpha'], use_cut.inputs['Factor'])
    use_cut.inputs['A'].default_value = 1.0
    links.new(cut.outputs['Value'], use_cut.inputs['B'])
    links.new(use_cut.outputs['Result'], bsdf.inputs['Alpha'])
    return group


def _build_blend_group():
    group = bpy.data.node_groups.new(BLEND_GROUP, 'ShaderNodeTree')
    _socket(group, "BSDF", 'shader', in_out='OUTPUT')
    for name, kind, default in [
        ("DF", 'color', (0.5, 0.5, 0.5, 1)), ("DF Alpha", 'float', 0.0), ("MRA", 'color', (0, 0.5, 1, 1)),
        ("NM", 'color', (0.5, 0.5, 1, 1)), ("Diffuse Color", 'color', (1, 1, 1, 1)), ("Tint", 'color', (1, 1, 1, 1)),
        ("DF B", 'color', (0.5, 0.5, 0.5, 1)), ("DF B Alpha", 'float', 0.0), ("MRA B", 'color', (0, 0.5, 1, 1)),
        ("NM B", 'color', (0.5, 0.5, 1, 1)), ("Tint B", 'color', (1, 1, 1, 1)),
        ("AO Color", 'color', (0, 0, 0, 1)), ("VC", 'color', (1, 1, 1, 1)),
        ("Vertex Color", 'color', (1, 1, 1, 1)), ("Use Vertex Color", 'float', 0.0), ("Vertex Alpha", 'float', 0.0), ("Vertex Blend", 'float', 10.0), ("Vertex Mix", 'float', 1.0),
        ("Invert Vertex", 'float', 0.0), ("Use B Alpha", 'float', 0.0), ("Invert Alpha", 'float', 0.0),
        ("Use 2 DF Maps", 'float', 1.0), ("Use 2 NM Maps", 'float', 1.0), ("Blend Tint Only", 'float', 0.0),
        ("Normal Strength", 'float', 1.0), ("Normal Strength B", 'float', 1.0)]:
        _socket(group, name, kind, default)

    nodes, links = group.nodes, group.links
    inp = nodes.new('NodeGroupInput')
    out = nodes.new('NodeGroupOutput')
    bsdf = nodes.new('ShaderNodeBsdfPrincipled')
    links.new(bsdf.outputs['BSDF'], out.inputs['BSDF'])

    # Layer B can reuse layer A's texture and only change the tint ("Blend Tint Only").
    df_b = _mix(nodes, 'MIX')
    links.new(inp.outputs['Blend Tint Only'], df_b.inputs['Factor'])
    links.new(inp.outputs['DF B'], df_b.inputs['A'])
    links.new(inp.outputs['DF'], df_b.inputs['B'])

    a_color, a_metal, a_rough, a_normal = _layer(nodes, links, inp.outputs['DF'], inp.outputs['Diffuse Color'],
                                                 inp.outputs['Tint'], inp.outputs['MRA'], inp.outputs['AO Color'],
                                                 inp.outputs['NM'], inp.outputs['Normal Strength'])
    white = nodes.new('ShaderNodeRGB')
    white.outputs[0].default_value = (1, 1, 1, 1)
    b_color, b_metal, b_rough, b_normal = _layer(nodes, links, df_b.outputs['Result'], white.outputs[0],
                                                 inp.outputs['Tint B'], inp.outputs['MRA B'], inp.outputs['AO Color'],
                                                 inp.outputs['NM B'], inp.outputs['Normal Strength B'])

    # Layer mask: (vertex alpha ^ 2.2) / (1 - texture alpha), remapped from 0..Vertex Mix to -sharpness/2..1, so layer B
    # only shows where the painted alpha is (nearly) full and the texture alpha breaks up the edge.
    vertex = _math(nodes, 'POWER', 2.2)
    links.new(inp.outputs['Vertex Alpha'], vertex.inputs[0])
    vertex_inv = _math(nodes, 'SUBTRACT')
    vertex_inv.inputs[0].default_value = 1.0
    links.new(vertex.outputs['Value'], vertex_inv.inputs[1])
    vertex_sel = nodes.new('ShaderNodeMix')
    vertex_sel.data_type = 'FLOAT'
    links.new(inp.outputs['Invert Vertex'], vertex_sel.inputs['Factor'])
    links.new(vertex.outputs['Value'], vertex_sel.inputs['A'])
    links.new(vertex_inv.outputs['Value'], vertex_sel.inputs['B'])

    tex_alpha = nodes.new('ShaderNodeMix')
    tex_alpha.data_type = 'FLOAT'
    links.new(inp.outputs['Use B Alpha'], tex_alpha.inputs['Factor'])
    links.new(inp.outputs['DF Alpha'], tex_alpha.inputs['A'])
    links.new(inp.outputs['DF B Alpha'], tex_alpha.inputs['B'])
    tex_alpha_inv = _math(nodes, 'SUBTRACT')
    tex_alpha_inv.inputs[0].default_value = 1.0
    links.new(tex_alpha.outputs['Result'], tex_alpha_inv.inputs[1])
    tex_alpha_sel = nodes.new('ShaderNodeMix')
    tex_alpha_sel.data_type = 'FLOAT'
    links.new(inp.outputs['Invert Alpha'], tex_alpha_sel.inputs['Factor'])
    links.new(tex_alpha.outputs['Result'], tex_alpha_sel.inputs['A'])
    links.new(tex_alpha_inv.outputs['Value'], tex_alpha_sel.inputs['B'])

    denominator = _math(nodes, 'SUBTRACT')
    denominator.inputs[0].default_value = 1.0
    links.new(tex_alpha_sel.outputs['Result'], denominator.inputs[1])
    denominator_safe = _math(nodes, 'MAXIMUM', 0.0001)
    links.new(denominator.outputs['Value'], denominator_safe.inputs[0])
    dodge = _math(nodes, 'DIVIDE')
    links.new(vertex_sel.outputs['Result'], dodge.inputs[0])
    links.new(denominator_safe.outputs['Value'], dodge.inputs[1])

    half_blend = _math(nodes, 'MULTIPLY', -0.5)
    links.new(inp.outputs['Vertex Blend'], half_blend.inputs[0])
    remap = nodes.new('ShaderNodeMapRange')
    remap.clamp = True
    links.new(dodge.outputs['Value'], remap.inputs['Value'])
    remap.inputs['From Min'].default_value = 0.0
    links.new(inp.outputs['Vertex Mix'], remap.inputs['From Max'])
    links.new(half_blend.outputs['Value'], remap.inputs['To Min'])
    remap.inputs['To Max'].default_value = 1.0

    layer_b = _math(nodes, 'MULTIPLY')
    links.new(remap.outputs['Result'], layer_b.inputs[0])
    links.new(inp.outputs['Use 2 DF Maps'], layer_b.inputs[1])

    color = _mix(nodes, 'MIX')
    links.new(layer_b.outputs['Value'], color.inputs['Factor'])
    links.new(a_color, color.inputs['A'])
    links.new(b_color, color.inputs['B'])
    lit = _mix(nodes, 'MULTIPLY', 1.0)
    links.new(color.outputs['Result'], lit.inputs['A'])
    links.new(inp.outputs['VC'], lit.inputs['B'])
    vertex_tint = _mix(nodes, 'MULTIPLY')
    links.new(inp.outputs['Use Vertex Color'], vertex_tint.inputs['Factor'])
    links.new(lit.outputs['Result'], vertex_tint.inputs['A'])
    links.new(inp.outputs['Vertex Color'], vertex_tint.inputs['B'])
    links.new(vertex_tint.outputs['Result'], bsdf.inputs['Base Color'])

    for a_value, b_value, target in [(a_metal, b_metal, 'Metallic'), (a_rough, b_rough, 'Roughness')]:
        mixed = nodes.new('ShaderNodeMix')
        mixed.data_type = 'FLOAT'
        links.new(layer_b.outputs['Value'], mixed.inputs['Factor'])
        links.new(a_value, mixed.inputs['A'])
        links.new(b_value, mixed.inputs['B'])
        links.new(mixed.outputs['Result'], bsdf.inputs[target])

    normal_factor = _math(nodes, 'MULTIPLY')
    links.new(layer_b.outputs['Value'], normal_factor.inputs[0])
    links.new(inp.outputs['Use 2 NM Maps'], normal_factor.inputs[1])
    normal = nodes.new('ShaderNodeMix')
    normal.data_type = 'VECTOR'
    links.new(normal_factor.outputs['Value'], normal.inputs['Factor'])
    links.new(a_normal, normal.inputs['A'])
    links.new(b_normal, normal.inputs['B'])
    links.new(normal.outputs['Result'], bsdf.inputs['Normal'])
    return group


def _group(name):
    group = bpy.data.node_groups.get(name)
    if group is None:
        try:
            group = _build_base_group() if name == BASE_GROUP else _build_blend_group()
        except Exception:
            broken = bpy.data.node_groups.get(name)  # never leave a half-built template behind
            if broken is not None:
                bpy.data.node_groups.remove(broken)
            raise
    return group


# ----------------------------------------------------------------------------------------------------------------
# Per-material setup
# ----------------------------------------------------------------------------------------------------------------

def _color(value):
    return (value["R"], value["G"], value["B"], 1.0)


def _find(mapping, names):
    lowered = {k.lower(): v for k, v in (mapping or {}).items()}
    for name in names:
        if name.lower() in lowered:
            return lowered[name.lower()]
    return None


# Riot's stand-in textures: what a base material shows when a material doesn't set that texture ("Default_Base",
# "Albedo_DF" with "Albedo" written on it, "MRA_MRA", "Normal_NM", ...). Never real surfaces.
PLACEHOLDER_FOLDERS = ("/LayeredEnv/_Textures_Base/", "/Environment/Materials/BaseMats/EnvBaseMat/")


def _is_placeholder(relative_path):
    path = relative_path.replace("\\", "/")
    return any(folder in path for folder in PLACEHOLDER_FOLDERS)


def _find_texture(textures, names):
    """The first of these texture parameters that is a real texture (not a placeholder)."""
    lowered = {k.lower(): v for k, v in (textures or {}).items()}
    for name in names:
        value = lowered.get(name.lower())
        if value and not _is_placeholder(value):
            return value
    return None


def _image(assets_root, relative_path, non_color):
    path = os.path.join(assets_root, relative_path)
    if not os.path.exists(path):
        return None
    image = bpy.data.images.load(path, check_existing=True)
    if non_color:
        image.colorspace_settings.name = 'Non-Color'
    # Riot packs masks into the alpha channel (mortar lines, layer masks, emissive masks); as regular transparency
    # Blender would drop the color wherever alpha is 0 (e.g. bricks turning black)
    image.alpha_mode = 'CHANNEL_PACKED'
    return image


def _build_radianite(material, info, assets_root):
    """Summit's jade/emerald "radianite" roofs and crystals: a glossy green stone with cloudy color variation and a
    faint glow, approximated from the material's cloud colors (the real shader is animated VFX)."""
    colors = info.get("Colors") or {}
    scalars = info.get("Scalars") or {}
    textures = info.get("Textures") or {}
    cloud_a = _color(colors.get("Cloud Color A", {"R": 0.01, "G": 0.44, "B": 0.26}))
    extra = colors.get("Additional Cloud Color", {"R": 0.2, "G": 0.8, "B": 1.0})
    extra_weight = 0.35 * scalars.get("Additional Cloud Intensity", 0.25)
    # in game: a calm teal (cloud color A pulled slightly toward the bluish additional cloud color) with soft
    # darker/lighter clouds, fairly matte and faintly self-lit
    base = tuple(0.65 * cloud_a[i] + extra_weight * extra[ch] for i, ch in enumerate("RGB"))
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    output = nodes.new('ShaderNodeOutputMaterial')
    bsdf = nodes.new('ShaderNodeBsdfPrincipled')
    links.new(bsdf.outputs['BSDF'], output.inputs['Surface'])
    position = nodes.new('ShaderNodeNewGeometry')  # world space, so neighbouring roof pieces line up
    noise = nodes.new('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = 0.08 * scalars.get("Cloud UV Scale", 1.0)
    noise.inputs['Detail'].default_value = 3.0
    noise.inputs['Roughness'].default_value = 0.45
    links.new(position.outputs['Position'], noise.inputs['Vector'])
    ramp = nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = 0.35
    ramp.color_ramp.elements[0].color = tuple(c * 0.7 for c in base) + (1.0,)
    ramp.color_ramp.elements[1].position = 0.7
    ramp.color_ramp.elements[1].color = tuple(min(c * 1.25, 1.0) for c in base) + (1.0,)
    links.new(noise.outputs['Fac'], ramp.inputs['Fac'])
    links.new(ramp.outputs['Color'], bsdf.inputs['Base Color'])
    links.new(ramp.outputs['Color'], bsdf.inputs['Emission Color'])
    bsdf.inputs['Emission Strength'].default_value = 0.2
    bsdf.inputs['Metallic'].default_value = 0.0
    bsdf.inputs['Roughness'].default_value = 0.6
    relative = _find_texture(textures, TEXTURE_SLOTS["NM"])
    image = _image(assets_root, relative, True) if relative else None
    if image is not None:
        uv = nodes.new('ShaderNodeUVMap')
        uv.uv_map = "st"
        tex = nodes.new('ShaderNodeTexImage')
        tex.image = image
        links.new(uv.outputs['UV'], tex.inputs['Vector'])
        split = nodes.new('ShaderNodeSeparateColor')
        links.new(tex.outputs['Color'], split.inputs['Color'])
        flip = _math(nodes, 'SUBTRACT')
        flip.inputs[0].default_value = 1.0
        links.new(split.outputs['Green'], flip.inputs[1])
        join = nodes.new('ShaderNodeCombineColor')
        links.new(split.outputs['Red'], join.inputs['Red'])
        links.new(flip.outputs['Value'], join.inputs['Green'])
        links.new(split.outputs['Blue'], join.inputs['Blue'])
        normal = nodes.new('ShaderNodeNormalMap')
        links.new(join.outputs['Color'], normal.inputs['Color'])
        links.new(normal.outputs['Normal'], bsdf.inputs['Normal'])


def _is_blend(info, master):
    switches = info.get("Switches") or {}
    colors = info.get("Colors") or {}
    textures = info.get("Textures") or {}
    if _is_overlay(master):
        return False  # one layer, with an overlay texture on top (see rebuild_material)
    return ("blend" in master.lower() or switches.get("Use 2 Diffuse Maps") or "Layer B Tint" in colors
            or _find_texture(textures, TEXTURE_SLOTS["DF B"]) is not None)


def _is_overlay(master):
    """BaseEnv_MAT_V4_Overlay (Corrode's walls): one layer, plus a grime texture blended over it in "overlay" mode
    (mid grey changes nothing) and a skirt texture on a third UV map; no second layer."""
    return re.search(r"_overlay$", master or "", re.IGNORECASE) is not None


def rebuild_material(material, info, assets_root):
    """Replaces an imported USD material with a Valorant Base/Blend setup. Returns 'base', 'blend' or None."""
    parents = info.get("Parents") or []
    master = parents[-1] if parents else ""
    if re.search(r"radianite", master, re.IGNORECASE):
        _build_radianite(material, info, assets_root)
        return "base"
    if SPECIAL_MASTERS.search(master) or SPECIAL_MASTERS.search(material.name):
        return None

    textures = info.get("Textures") or {}
    colors = info.get("Colors") or {}
    scalars = info.get("Scalars") or {}
    switches = info.get("Switches") or {}

    # Summit's wet ground variants: no diffuse of their own (a placeholder), their pattern is the "Custom Texture"
    diffuse = _find_texture(textures, TEXTURE_SLOTS["DF"])
    custom = _find_texture(textures, ["Custom Texture"])
    if diffuse is None and custom and _find(switches, ["Use Custom Texture"]):
        textures = {k: v for k, v in textures.items() if k not in TEXTURE_SLOTS["DF"]}
        textures["Diffuse"] = custom

    images = {}
    for slot, names in TEXTURE_SLOTS.items():
        relative = _find_texture(textures, names)  # placeholders count as no texture
        if relative:
            images[slot] = _image(assets_root, relative, slot in NON_COLOR_SLOTS)
    if images.get("DF") is None and not any(k.startswith("Color A") for k in colors) and "DiffuseColor" not in colors:
        return None  # nothing to build a color from: keep the USD material

    blend = _is_blend(info, master)
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    output = nodes.new('ShaderNodeOutputMaterial')
    shader = nodes.new('ShaderNodeGroup')
    shader.node_tree = _group(BLEND_GROUP if blend else BASE_GROUP)
    links.new(shader.outputs['BSDF'], output.inputs['Surface'])
    uv = nodes.new('ShaderNodeUVMap')
    uv.uv_map = "st"

    for slot, image in images.items():
        if image is None or slot not in shader.inputs:
            continue
        tex = nodes.new('ShaderNodeTexImage')
        tex.image = image
        links.new(uv.outputs['UV'], tex.inputs['Vector'])
        links.new(tex.outputs['Color'], shader.inputs[slot])
        if slot in ("DF", "DF B") and f"{slot} Alpha" in shader.inputs:
            links.new(tex.outputs['Alpha'], shader.inputs[f"{slot} Alpha"])

    # Overlay materials: the grime texture over the base color, in "overlay" mode, as strong as "Overlay Amount"
    overlay_path = _find_texture(textures, ["Overlay Texture"]) if _is_overlay(master) else None
    if overlay_path and shader.inputs["DF"].links and (overlay_image := _image(assets_root, overlay_path, False)):
        base_color = shader.inputs["DF"].links[0].from_socket
        overlay_tex = nodes.new('ShaderNodeTexImage')
        overlay_tex.image = overlay_image
        scale_x = _find(scalars, ["Overlay UV - X"]) or 1.0
        scale_y = _find(scalars, ["Overlay UV - Y"]) or 1.0
        if (scale_x, scale_y) != (1.0, 1.0):
            mapping = nodes.new('ShaderNodeMapping')
            mapping.inputs['Scale'].default_value = (scale_x, scale_y, 1.0)
            links.new(uv.outputs['UV'], mapping.inputs['Vector'])
            links.new(mapping.outputs['Vector'], overlay_tex.inputs['Vector'])
        else:
            links.new(uv.outputs['UV'], overlay_tex.inputs['Vector'])
        mix = nodes.new('ShaderNodeMix')
        mix.data_type = 'RGBA'
        mix.blend_type = 'OVERLAY'
        mix.inputs[0].default_value = max(0.0, min(1.0, _find(scalars, ["Overlay Amount (Layer 1)"]) or 1.0))
        links.new(base_color, mix.inputs[6])
        links.new(overlay_tex.outputs['Color'], mix.inputs[7])
        links.new(mix.outputs[2], shader.inputs["DF"])

    # Color-only materials (no diffuse texture): average of the A colors as a flat albedo.
    if images.get("DF") is None:
        a = [colors[k] for k in ("Color A1", "Color A2") if k in colors]
        if a:
            shader.inputs["DF"].default_value = tuple(sum(c[ch] for c in a) / len(a) for ch in "RGB") + (1.0,)
        else:
            shader.inputs["DF"].default_value = (1, 1, 1, 1)  # flat material: its DiffuseColor is the color
        b = [colors[k] for k in ("Color B1", "Color B2") if k in colors]
        if b and "DF B" in shader.inputs:
            shader.inputs["DF B"].default_value = tuple(sum(c[ch] for c in b) / len(b) for ch in "RGB") + (1.0,)

    def set_color(socket, *names):
        value = _find(colors, names)
        if value is not None and socket in shader.inputs:
            shader.inputs[socket].default_value = _color(value)

    set_color("Diffuse Color", "DiffuseColor")
    set_color("Tint", "Layer A Tint", "Color Mult", "Texture Tint A")
    set_color("Tint B", "Layer B Tint", "Texture Tint B")
    set_color("AO Color", "AO color")
    set_color("VC", "Lightmass-only Vertex Color")
    # "Emissive Mult" is white on almost every environment material; it only glows when an emissive switch is on
    if any(_find(switches, [name]) for name in EMISSIVE_SWITCHES):
        set_color("Emissive Mult", "Emissive Mult")

    blend_power = _find(scalars, ["Mask Blend Power"])
    if blend_power is not None and "Vertex Blend" in shader.inputs:
        shader.inputs["Vertex Blend"].default_value = abs(blend_power)
        shader.inputs["Invert Vertex"].default_value = 1.0 if blend_power < 0 else 0.0

    def set_switch(socket, *names):
        value = _find(switches, names)
        if value is not None and socket in shader.inputs:
            shader.inputs[socket].default_value = 1.0 if value else 0.0

    set_switch("Use 2 DF Maps", "Use 2 Diffuse Maps")
    set_switch("Use 2 NM Maps", "Use 2 Normal Maps")
    set_switch("Blend Tint Only", "Blend Tint Only")
    set_switch("Use B Alpha", "Use Diffuse B Alpha")
    set_switch("Invert Alpha", "Invert Alpha (Texture)")
    set_switch("Use Alpha as Emissive", "Use Alpha as Emissive")
    # "Blend To Flat" (plaster over brick etc.): layer B is just its tint, no texture; same idea for its MRA/normal.
    for switch, input_name, flat in [("Blend To Flat", "DF B", (1, 1, 1, 1)), ("Blend To Flat MRA", "MRA B", (0, 0.5, 1, 1)),
                                     ("Flat Normal B", "NM B", (0.5, 0.5, 1, 1))]:
        if blend and _find(switches, [switch]) and input_name in shader.inputs:
            for link in list(shader.inputs[input_name].links):
                links.remove(link)
            shader.inputs[input_name].default_value = flat
    if blend and _find(switches, ["Blend To Flat"]):
        shader.inputs["Blend Tint Only"].default_value = 0.0
    elif blend and images.get("DF B") is None and "Blend Tint Only" in shader.inputs:
        shader.inputs["Blend Tint Only"].default_value = 1.0  # no second texture: layer B = layer A with its own tint

    # Vertex color / alpha painted on the mesh (USD: displayColor / displayOpacity).
    if "Vertex Color" in shader.inputs and _find(switches, ["Use Vertex Color"]):
        vertex_color = nodes.new('ShaderNodeVertexColor')
        vertex_color.layer_name = "displayColor"
        links.new(vertex_color.outputs['Color'], shader.inputs["Vertex Color"])
        shader.inputs["Use Vertex Color"].default_value = 1.0
    if "Vertex Alpha" in shader.inputs:
        vertex_alpha = nodes.new('ShaderNodeAttribute')
        vertex_alpha.attribute_name = "displayOpacity"
        links.new(vertex_alpha.outputs['Fac'], shader.inputs["Vertex Alpha"])

    if info.get("BlendMode") == 1 and "Use Alpha" in shader.inputs:  # masked: cut out by diffuse alpha
        shader.inputs["Use Alpha"].default_value = 1.0
        material.surface_render_method = 'DITHERED'
    return "blend" if blend else "base"


def _uses_vertex_data(material):
    """(needs displayColor, needs displayOpacity) for a rebuilt material."""
    if material is None or not material.use_nodes:
        return False, False
    for node in material.node_tree.nodes:
        if node.type == 'GROUP' and node.node_tree and node.node_tree.name in (BASE_GROUP, BLEND_GROUP):
            color = node.inputs["Use Vertex Color"].default_value > 0 if "Use Vertex Color" in node.inputs else False
            return color, "Vertex Alpha" in node.inputs
    return False, False


def add_default_vertex_colors(objects):
    """Unreal treats a mesh without vertex colors as painted white; Blender reads a missing attribute as black.
    Give such meshes a white displayColor / opaque displayOpacity where a rebuilt material reads them."""
    added = 0
    for mesh in {o.data for o in objects if o.type == 'MESH' and o.data is not None}:
        needs_color = needs_alpha = False
        for material in mesh.materials:
            color, alpha = _uses_vertex_data(material)
            needs_color |= color
            needs_alpha |= alpha
        count = len(mesh.vertices)
        if needs_color and "displayColor" not in mesh.attributes and count:
            mesh.attributes.new("displayColor", 'FLOAT_COLOR', 'POINT').data.foreach_set(
                "color", numpy.ones(count * 4, dtype=numpy.float32))
            added += 1
        if needs_alpha and "displayOpacity" not in mesh.attributes and count:
            mesh.attributes.new("displayOpacity", 'FLOAT', 'POINT').data.foreach_set(
                "value", numpy.ones(count, dtype=numpy.float32))
            added += 1
    return added


def find_material_info(summary, material_name):
    """Summary entry for a Blender material: ignores Blender's ".001" copies, the USD export's "_1" renames of
    duplicate names, and letter case (asset names like "ColorPalette_M0_OraNGE" vs. the material "..._Orange")."""
    lowered = summary.get("__lowered__")
    if lowered is None:
        lowered = summary["__lowered__"] = {k.lower(): v for k, v in summary.items() if k != "__lowered__"}
    name = re.sub(r"\.\d{3}$", "", material_name).lower()
    return lowered.get(name) or lowered.get(re.sub(r"_\d+$", "", name))


def merge_duplicate_materials(materials, objects):
    """The USD import creates one copy of a material per mesh that references it ("X", "X.001", ...). Point the
    imported meshes at one copy so every material is rebuilt once. Returns the materials that remain."""
    groups = {}
    for material in materials:
        groups.setdefault(re.sub(r"\.\d{3}$", "", material.name), []).append(material)
    replace = {}
    kept = []
    for name, copies in groups.items():
        keep = next((m for m in copies if m.name == name), copies[0])
        kept.append(keep)
        replace.update({copy: keep for copy in copies if copy is not keep})
    if not replace:
        return kept

    for mesh in {o.data for o in objects if o.type == 'MESH' and o.data is not None}:
        for index, material in enumerate(mesh.materials):
            if material in replace:
                mesh.materials[index] = replace[material]
    for obj in objects:
        for slot in obj.material_slots:
            if slot.link == 'OBJECT' and slot.material in replace:
                slot.material = replace[slot.material]
    unused = [m for m in replace if m.users == 0]
    bpy.data.batch_remove(unused)
    kept.extend(m for m in replace if m not in unused)  # still used elsewhere: leave it as it is
    return kept


def rebuild_materials(materials, summary, assets_root):
    """Rebuild every material that has summary data; returns (base, blend, kept) counts."""
    base = blend = kept = 0
    for material in materials:
        info = find_material_info(summary, material.name)
        if not info or not material.use_nodes or not (info.get("Textures") or info.get("Colors")):
            kept += 1
            continue
        try:
            result = rebuild_material(material, info, assets_root)
        except Exception as error:  # never let one material stop the import
            print(f"[Valorant Porting] could not rebuild {material.name}: {error}")
            result = None
        if result == "base":
            base += 1
        elif result == "blend":
            blend += 1
        else:
            kept += 1
    return base, blend, kept
