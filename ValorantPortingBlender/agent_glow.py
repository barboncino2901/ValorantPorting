"""Glow some agents have in game on top of their base materials, rebuilt in Blender (the app sends the textures and
values from the game's effect materials; how they combine is rebuilt here).

Reyna ("Vampire"): her heart pulses; during her ultimate (Empress) her tattoos, eyes and fingertips glow, energy flows
over her, her chest flares and her body tints purple. Two custom properties on her armature drive it, for every one of
her materials at once, and can be keyframed: "Glow: heart" (on by default) and "Glow: ultimate" (off by default)."""
import math
import bpy

GROUP = "VP Reyna Glow"
HEART = "Glow: heart"
ULTIMATE = "Glow: ultimate"


def _color(hex_value, default=(0.54, 0.25, 1.0)):
    try:
        h = hex_value.strip().lstrip("#")
        srgb = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
        return tuple(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb)
    except Exception:
        return default


def _image(images, role):
    image = images.get(role)
    if image is not None:
        image.colorspace_settings.name = 'Non-Color'
    return image


def _build_group(images, values):
    """The shader group: Shader in (the material as it is), glow added on top. Inputs: Heart, Ultimate (0..1),
    Time (seconds), MRAE (the material's texture, its green channel marks the heart's gem)."""
    group = bpy.data.node_groups.get(GROUP)
    if group is not None:
        bpy.data.node_groups.remove(group)
    group = bpy.data.node_groups.new(GROUP, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    face.new_socket("MRAE", in_out='INPUT', socket_type='NodeSocketColor')
    for name, default in (("Heart", 1.0), ("Ultimate", 0.0), ("Time", 0.0), ("Eye", 0.0)):
        socket = face.new_socket(name, in_out='INPUT', socket_type='NodeSocketFloat')
        socket.default_value = default
    face.new_socket("Shader", in_out='OUTPUT', socket_type='NodeSocketShader')
    nodes, links = group.nodes, group.links
    inputs = nodes.new("NodeGroupInput")
    output = nodes.new("NodeGroupOutput")

    def math_node(op, a=None, b=None, clamp=False):
        node = nodes.new("ShaderNodeMath")
        node.operation = op
        node.use_clamp = clamp
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                node.inputs[i].default_value = v
            else:
                links.new(v, node.inputs[i])
        return node.outputs[0]

    def mix_color(op, a, b, factor=1.0):
        node = nodes.new("ShaderNodeMix")
        node.data_type = 'RGBA'
        node.blend_type = op
        node.inputs[0].default_value = factor
        for socket, v in ((node.inputs[6], a), (node.inputs[7], b)):
            if isinstance(v, tuple):
                socket.default_value = (*v, 1.0)
            else:
                links.new(v, socket)
        return node.outputs[2]

    uv = nodes.new("ShaderNodeUVMap")
    uv.uv_map = "UV_SINGLE"

    def texture(role, scale=(1.0, 1.0), scroll=(0.0, 0.0)):
        """a mask/pattern texture on the model's main UVs, tiled and scrolling over time"""
        image = _image(images, role)
        if image is None:
            return None
        mapping = nodes.new("ShaderNodeMapping")
        links.new(uv.outputs[0], mapping.inputs[0])
        mapping.inputs["Scale"].default_value = (scale[0], scale[1], 1.0)
        if scroll != (0.0, 0.0):
            offset = nodes.new("ShaderNodeCombineXYZ")
            links.new(math_node('MULTIPLY', inputs.outputs["Time"], scroll[0]), offset.inputs[0])
            links.new(math_node('MULTIPLY', inputs.outputs["Time"], scroll[1]), offset.inputs[1])
            links.new(offset.outputs[0], mapping.inputs["Location"])
        node = nodes.new("ShaderNodeTexImage")
        node.image = image
        links.new(mapping.outputs[0], node.inputs[0])
        return node

    glow_color = _color(values.get("HeartColor", "8A40FF"))
    # a little more saturated than the heart's colour: bright emission washes out toward white
    deep_color = tuple((c + c * c / max(glow_color)) / 2 for c in glow_color)
    # the heart: the gem is the brightest part of the MRAE's green channel; it pulses (game: PulseSpeedHeart)
    mrae = nodes.new("ShaderNodeSeparateColor")
    links.new(inputs.outputs["MRAE"], mrae.inputs[0])
    gem = nodes.new("ShaderNodeMapRange")
    gem.inputs[1].default_value, gem.inputs[2].default_value = 0.8, 0.97
    links.new(mrae.outputs[1], gem.inputs[0])
    two_pi_speed = 2 * math.pi * float(values.get("HeartPulseSpeed", 0.4))
    pulse = math_node('MULTIPLY_ADD', math_node('SINE', math_node('MULTIPLY', inputs.outputs["Time"], two_pi_speed)), 0.35)
    pulse.node.inputs[2].default_value = 0.65
    heart = math_node('MULTIPLY', math_node('MULTIPLY', math_node('POWER', gem.outputs[0], 2.0), pulse), inputs.outputs["Heart"])
    total = mix_color('MULTIPLY', glow_color, math_node('MULTIPLY', heart, 10.0))

    ult = inputs.outputs["Ultimate"]
    tattoo = texture("TattooMask")
    if tattoo is not None:
        # tattoo lines, lit by energy flowing over her (3P_Vampire_X_FX: TextureA tiled 3x, scrolling -0.1)
        tile = float(values.get("EnergyTile", 3.0))
        energy = texture("Energy", (tile, tile), (0.0, float(values.get("EnergyScrollV", -0.1))))
        lit = math_node('ADD', energy.outputs[0] if energy else 0.5, 0.35) if energy else 1.0
        strength = 1.5 * float(values.get("UltTattooMultiply", 3.0))
        tattoo_glow = math_node('MULTIPLY', math_node('MULTIPLY', tattoo.outputs[0], lit), math_node('MULTIPLY', ult, strength))
        total = mix_color('ADD', total, mix_color('MULTIPLY', deep_color, tattoo_glow))
    sets = texture("MaskSets")
    if sets is not None:
        # fingertips and nails (red channel; the alpha's soft shapes also cover part of the face)
        red = nodes.new("ShaderNodeSeparateColor")
        links.new(sets.outputs[0], red.inputs[0])
        tips = math_node('MULTIPLY', red.outputs[0], math_node('MULTIPLY', ult, 4.0))
        total = mix_color('ADD', total, mix_color('MULTIPLY', deep_color, tips))
    flare = texture("ChestFlare", (float(values.get("FlareUTile", 1.5)), float(values.get("FlareVTile", 2.0))),
                    (float(values.get("FlareU", -0.25)), float(values.get("FlareV", -0.41))))
    if flare is not None:
        # the chest flare, flowing around the heart during the ultimate
        near_heart = nodes.new("ShaderNodeMapRange")
        near_heart.inputs[1].default_value, near_heart.inputs[2].default_value = 0.6, 0.8
        links.new(mrae.outputs[1], near_heart.inputs[0])
        flare_glow = math_node('MULTIPLY', math_node('MULTIPLY', flare.outputs[0], near_heart.outputs[0]), math_node('MULTIPLY', ult, 8.0))
        total = mix_color('ADD', total, mix_color('MULTIPLY', glow_color, flare_glow))

    # her eyes (their own material and UVs: the masks don't apply) glow during the ultimate
    eye = inputs.outputs["Eye"]
    body_part = mix_color('MULTIPLY', total, math_node('SUBTRACT', 1.0, eye))
    eye_glow = mix_color('MULTIPLY', (0.75, 0.35, 1.0), math_node('MULTIPLY', math_node('MULTIPLY', eye, ult), 6.0))
    total = mix_color('ADD', body_part, eye_glow)

    emission = nodes.new("ShaderNodeEmission")
    links.new(total, emission.inputs["Color"])
    emission.inputs["Strength"].default_value = 1.0
    # the body tints dark purple during the ultimate (3P_Vampire_X_FX: BaseColor, BodyColor)
    tint = nodes.new("ShaderNodeBsdfDiffuse")
    tint.inputs["Color"].default_value = (*_color(values.get("UltBodyColor", "150021")), 1.0)
    mix = nodes.new("ShaderNodeMixShader")
    tint_amount = math_node('MULTIPLY', ult, float(values.get("UltBodyAmount", 0.2)) * 2.0, clamp=True)
    links.new(math_node('MULTIPLY', tint_amount, math_node('SUBTRACT', 1.0, eye)), mix.inputs[0])
    links.new(inputs.outputs["Shader"], mix.inputs[1])
    links.new(tint.outputs[0], mix.inputs[2])
    add = nodes.new("ShaderNodeAddShader")
    links.new(mix.outputs[0], add.inputs[0])
    links.new(emission.outputs[0], add.inputs[1])
    links.new(add.outputs[0], output.inputs[0])
    return group


def _drive(tree, node, socket, armature, prop):
    curve = tree.driver_add(f'nodes["{node.name}"].inputs["{socket}"].default_value')
    driver = curve.driver
    driver.type = 'SCRIPTED'
    var = driver.variables.new()
    var.name = "value"
    var.type = 'SINGLE_PROP'
    var.targets[0].id_type = 'OBJECT'
    var.targets[0].id = armature
    var.targets[0].data_path = f'["{prop}"]'
    driver.expression = "value"  # a simple expression: no Python, works without "Auto Run Python Scripts"


def apply(glow, objects, load_image):
    """glow: what the app sent ({"Agent", "Textures": role -> game path, "Values"}); objects: the agent's new objects;
    load_image(game path) -> image. Adds the glow layer to the agent's own materials (not hair, eyes)."""
    if (glow or {}).get("Agent", "").lower() != "vampire":
        return 0
    images = {}
    for role, path in (glow.get("Textures") or {}).items():
        try:
            images[role] = load_image(path)
        except Exception:
            images[role] = None
    group = _build_group(images, glow.get("Values") or {})
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    done = 0
    for obj in objects:
        if obj.type != 'MESH':
            continue
        armature = obj.parent if obj.parent is not None and obj.parent.type == 'ARMATURE' else \
            next((m.object for m in obj.modifiers if m.type == 'ARMATURE' and m.object), None)
        if armature is None or obj.name.upper().startswith("FP_"):
            continue  # the 1st person arms have their own tattoo layout (not done yet)
        if HEART not in armature:
            armature[HEART] = 1.0
            armature[ULTIMATE] = 0.0
            for prop in (HEART, ULTIMATE):
                ui = armature.id_properties_ui(prop)
                ui.update(min=0.0, max=1.0, soft_min=0.0, soft_max=1.0,
                          description="Reyna's glow: her pulsing heart" if prop == HEART else
                          "Reyna's ultimate (Empress): tattoos, eyes, fingertips and chest glow; keyframe it to switch it on")
        for slot in obj.material_slots:
            material = slot.material
            name = (material.name if material else "").lower()
            is_eye = "eye" in name
            if material is None or not material.use_nodes or "hair" in name or ("vampire" not in name and not is_eye):
                continue
            if is_eye and not name.endswith("(reyna glow)"):
                # the eye material is shared by agents: Reyna gets her own, so only her eyes glow
                own = bpy.data.materials.get(f"{material.name} (Reyna glow)") or material.copy()
                own.name = f"{material.name.split(' (Reyna glow)')[0]} (Reyna glow)"
                slot.material = material = own
            tree = material.node_tree
            if any(n.type == 'GROUP' and n.node_tree == group for n in tree.nodes):
                continue
            out = next((n for n in tree.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output), None)
            if out is None or not out.inputs["Surface"].is_linked:
                continue
            source = out.inputs["Surface"].links[0].from_socket
            node = tree.nodes.new("ShaderNodeGroup")
            node.node_tree = group
            node.name = node.label = GROUP
            node.location = (out.location.x, out.location.y - 260)
            node.inputs["Eye"].default_value = 1.0 if is_eye else 0.0
            tree.links.new(source, node.inputs["Shader"])
            tree.links.new(node.outputs[0], out.inputs["Surface"])
            mrae = next((n for n in tree.nodes if n.type == 'TEX_IMAGE' and n.image and "mrae" in n.image.name.lower()), None)
            if mrae is not None:
                tree.links.new(mrae.outputs[0], node.inputs["MRAE"])
            pending.append((material, armature, fps))
            done += 1
    return done


# materials whose glow controls are connected once the import is over (see connect_pending)
pending = []


def connect_pending():
    """Connects the glow layers to their armature's controls ("Glow: heart", "Glow: ultimate") and to the timeline.
    Done after the import: drivers made during it are marked invalid by Blender and never read their target."""
    while pending:
        material, armature, fps = pending.pop()
        try:
            tree = material.node_tree
            node = tree.nodes.get(GROUP)
        except ReferenceError:
            continue  # deleted meanwhile
        if node is None:
            continue
        _drive(tree, node, "Heart", armature, HEART)
        _drive(tree, node, "Ultimate", armature, ULTIMATE)
        time = tree.driver_add(f'nodes["{node.name}"].inputs["Time"].default_value')
        time.driver.expression = f"frame / {fps:g}"
        # Blender's evaluated copy of the armature doesn't have the new properties until it's told it changed;
        # drivers reading them before that are marked invalid
        armature.update_tag()
        for curve in tree.animation_data.drivers:
            curve.driver.is_valid = True
    return None  # (as a timer: don't run again)
