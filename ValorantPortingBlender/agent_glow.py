"""Glow some agents have in game on top of their base materials, rebuilt in Blender (the app sends the textures and
values from the game's effect materials; how they combine is rebuilt here). Each agent's controls are custom
properties on its armature ("Glow: ..."), driving all of its materials at once; they can be keyframed.

Reyna ("Vampire"): her heart pulses behind her top, with a flare streaking over it; her eyes (iris and pupil) can
glow; during her ultimate (Empress) her tattoos and hair tips glow, energy flows through the tattoos and her body tints
purple ("Glow: heart" on by default, "Glow: eyes", "Glow: ultimate"; the ultimate also lights the eyes).
Iso ("Sequoia"): his glow mask lights the lines of his outfit, his hands (his E) and his gloves (his other abilities)
("Glow: outfit", "Glow: hands (E)", "Glow: hands (abilities)").
Chamber ("Deadeye"): his gold tattoo (side of his head, wrists), which his colour texture leaves out ("Tattoo": 1 as
in game; animating it from 0 reveals it with a glowing edge, as in character select).
Waylay ("Terra"): the crystal shards on her arms glow at their edges, green-yellow, with her nails and hair tips
("Glow: tattoos")."""
import math
import bpy

GROUP = "VP Reyna Glow"
HEART = "Glow: heart"
EYES = "Glow: eyes"
ULTIMATE = "Glow: ultimate"
CONTROLS = {
    HEART: (1.0, "Reyna's pulsing heart and the flare over it"),
    EYES: (0.0, "Reyna's eyes glowing (the ultimate lights them too)"),
    ULTIMATE: (0.0, "Reyna's ultimate (Empress): tattoos, hair tips and eyes glow; keyframe it to switch it on"),
}
# her glow, as it looks in game: a pink-leaning violet (the heart's game colour, 8A40FF, reads bluer in Blender's AgX
# view than the game's rendering)
DEFAULT_GLOW = "E03CFF"


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
    """The shader group: Shader in (the material as it is), glow added on top. Inputs: Heart, Eyes, Ultimate (0..1),
    Time (seconds), MRAE and Albedo (the material's textures), Hair (1 on her hair material)."""
    group = bpy.data.node_groups.get(GROUP)
    if group is not None and group.users == 0:
        bpy.data.node_groups.remove(group)
    # (one already in use stays: a Reyna in the scene keeps hers; this one gets "VP Reyna Glow.001")
    group = bpy.data.node_groups.new(GROUP, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    face.new_socket("MRAE", in_out='INPUT', socket_type='NodeSocketColor')
    face.new_socket("Albedo", in_out='INPUT', socket_type='NodeSocketColor')
    for name, default in (("Heart", 1.0), ("Eyes", 0.0), ("Ultimate", 0.0), ("Time", 0.0), ("Hair", 0.0)):
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

    def separate(color_socket):
        node = nodes.new("ShaderNodeSeparateColor")
        links.new(color_socket, node.inputs[0])
        return node.outputs

    def map_range(value, low, high):
        node = nodes.new("ShaderNodeMapRange")
        node.inputs[1].default_value, node.inputs[2].default_value = low, high
        links.new(value, node.inputs[0])
        return node.outputs[0]

    uv = nodes.new("ShaderNodeUVMap")
    uv.uv_map = "UV_SINGLE"

    def texture(role, scale=(1.0, 1.0), scroll=(0.0, 0.0), location=(0.0, 0.0), clip=False):
        """a mask/pattern texture on the model's main UVs: tiled, placed, scrolling over time"""
        image = _image(images, role)
        if image is None:
            return None
        mapping = nodes.new("ShaderNodeMapping")
        links.new(uv.outputs[0], mapping.inputs[0])
        mapping.inputs["Scale"].default_value = (scale[0], scale[1], 1.0)
        mapping.inputs["Location"].default_value = (location[0], location[1], 0.0)
        if scroll != (0.0, 0.0):
            offset = nodes.new("ShaderNodeCombineXYZ")
            links.new(math_node('ADD', math_node('MULTIPLY', inputs.outputs["Time"], scroll[0]), location[0]), offset.inputs[0])
            links.new(math_node('ADD', math_node('MULTIPLY', inputs.outputs["Time"], scroll[1]), location[1]), offset.inputs[1])
            links.new(offset.outputs[0], mapping.inputs["Location"])
        node = nodes.new("ShaderNodeTexImage")
        node.image = image
        if clip:
            node.extension = 'CLIP'
        links.new(mapping.outputs[0], node.inputs[0])
        return node

    glow = _color(values.get("GlowColor", DEFAULT_GLOW))
    hair_color = _color(values.get("HairGlowColor", "FF4DE8"))
    t = inputs.outputs["Time"]
    ult = inputs.outputs["Ultimate"]
    not_hair = math_node('SUBTRACT', 1.0, inputs.outputs["Hair"])

    # the heart: a soft glow behind her top's mesh (the brightest part of the MRAE's green channel), pulsing
    # (game: PulseSpeedHeart 0.4)
    mrae = separate(inputs.outputs["MRAE"])
    gem = math_node('POWER', map_range(mrae[1], 0.8, 0.97), 2.0)
    pulse = math_node('MULTIPLY_ADD', math_node('SINE', math_node('MULTIPLY', t, 2 * math.pi * float(values.get("HeartPulseSpeed", 0.4)))), 0.35)
    pulse.node.inputs[2].default_value = 0.65
    heart_on = math_node('MULTIPLY', inputs.outputs["Heart"], pulse)
    amount = math_node('MULTIPLY', math_node('MULTIPLY', gem, heart_on), 10.0)

    # the chest flare: the game's flare texture as a thin vertical streak over the heart (where on her UVs: the heart's
    # centre, the streak's width and height), brighter in the ultimate
    cu, cv = float(values.get("HeartU", 0.3462)), float(values.get("HeartV", 0.1084))
    fw, fh = float(values.get("FlareWidth", 0.03)), float(values.get("FlareHeight", 0.09))
    flare = texture("ChestFlare", (1 / fw, 1 / fh), location=(0.5 - cu / fw, 0.5 - cv / fh), clip=True)
    if flare is not None:
        flare_on = math_node('ADD', heart_on, math_node('MULTIPLY', ult, 0.8))
        amount = math_node('ADD', amount, math_node('MULTIPLY', math_node('MULTIPLY', separate(flare.outputs[0])[0], flare_on), 12.0))

    # the eyes: iris and pupil (the alpha of the mask set, on her body's UVs); the ultimate lights them too
    xe = texture("MaskSetsXE")
    albedo = separate(inputs.outputs["Albedo"])
    if xe is not None:
        # the mask is a disc over the whole eye: in her colour texture the iris is a purple ring between the grey white
        # and the black pupil, so only what's purple (red and blue both above green) glows, as in game
        iris = math_node('MULTIPLY', math_node('SUBTRACT', math_node('MINIMUM', albedo[0], albedo[2]), albedo[1]), 8.0, clamp=True)
        eyes_on = math_node('MAXIMUM', inputs.outputs["Eyes"], ult)
        amount = math_node('ADD', amount, math_node('MULTIPLY', math_node('MULTIPLY', math_node('MULTIPLY', xe.outputs[1], iris), eyes_on), 2.5))

    # the ultimate: tattoo lines lit by energy flowing through them (3P_Vampire_X_FX: TextureA tiled 3x, scrolling -0.1)
    tattoo = texture("TattooMask")
    if tattoo is not None:
        tile = float(values.get("EnergyTile", 3.0))
        energy = texture("Energy", (tile, tile), (0.0, float(values.get("EnergyScrollV", -0.1))))
        lit = math_node('ADD', separate(energy.outputs[0])[0], 0.35) if energy else 1.0
        strength = 1.0 * float(values.get("UltTattooMultiply", 3.0))
        amount = math_node('ADD', amount, math_node('MULTIPLY', math_node('MULTIPLY', separate(tattoo.outputs[0])[0], lit),
                                                    math_node('MULTIPLY', ult, strength)))
    # (no finger glow: the mask set's finger shapes are gradients for an effect of their own; as a glow they streak
    # along the fingers)
    total = mix_color('MULTIPLY', glow, math_node('MULTIPLY', amount, not_hair))

    # her hair: the pink tips (where the hair's own colour is pink) glow in the ultimate
    pink = math_node('SUBTRACT', math_node('MULTIPLY', math_node('ADD', albedo[0], albedo[2]), 0.5), albedo[1], clamp=True)
    hair_glow = math_node('MULTIPLY', math_node('MULTIPLY', math_node('POWER', math_node('MULTIPLY', pink, 2.5, clamp=True), 1.5), ult),
                          math_node('MULTIPLY', inputs.outputs["Hair"], 1.2))
    total = mix_color('ADD', total, mix_color('MULTIPLY', hair_color, hair_glow))

    emission = nodes.new("ShaderNodeEmission")
    links.new(total, emission.inputs["Color"])
    emission.inputs["Strength"].default_value = 1.0
    # the body tints dark purple during the ultimate (3P_Vampire_X_FX: BaseColor, BodyColor)
    tint = nodes.new("ShaderNodeBsdfDiffuse")
    tint.inputs["Color"].default_value = (*_color(values.get("UltBodyColor", "150021")), 1.0)
    mix = nodes.new("ShaderNodeMixShader")
    tint_amount = math_node('MULTIPLY', ult, float(values.get("UltBodyAmount", 0.2)) * 2.0, clamp=True)
    links.new(math_node('MULTIPLY', tint_amount, not_hair), mix.inputs[0])
    links.new(inputs.outputs["Shader"], mix.inputs[1])
    links.new(tint.outputs[0], mix.inputs[2])
    add = nodes.new("ShaderNodeAddShader")
    links.new(mix.outputs[0], add.inputs[0])
    links.new(emission.outputs[0], add.inputs[1])
    links.new(add.outputs[0], output.inputs[0])
    return group


def _build_iso_group(images, values):
    """Iso: his glow mask lights his forearms (red: his E's charged punches), gloves (green: his other abilities) and
    the lines of his outfit (blue), each switched on by the game during those moments."""
    name = "VP Iso Glow"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    for socket_name in ("Outfit", "Hands E", "Hands abilities", "Time"):
        face.new_socket(socket_name, in_out='INPUT', socket_type='NodeSocketFloat').default_value = 0.0
    face.new_socket("Shader", in_out='OUTPUT', socket_type='NodeSocketShader')
    nodes, links = group.nodes, group.links
    inputs, output = nodes.new("NodeGroupInput"), nodes.new("NodeGroupOutput")
    uv = nodes.new("ShaderNodeUVMap")
    uv.uv_map = "EXTRAUVS0"  # the mask is laid out on his second UV map (on the first the outfit lines become blobs)
    mask = nodes.new("ShaderNodeTexImage")
    mask.image = _image(images, "GlowMask")
    links.new(uv.outputs[0], mask.inputs[0])
    channels = nodes.new("ShaderNodeSeparateColor")
    links.new(mask.outputs[0], channels.inputs[0])

    total = None
    for channel, switch in ((0, "Hands E"), (1, "Hands abilities"), (2, "Outfit")):
        part = nodes.new("ShaderNodeMath")
        part.operation = 'MULTIPLY'
        links.new(channels.outputs[channel], part.inputs[0])
        links.new(inputs.outputs[switch], part.inputs[1])
        if total is None:
            total = part.outputs[0]
        else:
            add = nodes.new("ShaderNodeMath")
            add.operation = 'ADD'
            links.new(total, add.inputs[0])
            links.new(part.outputs[0], add.inputs[1])
            total = add.outputs[0]
    emission = nodes.new("ShaderNodeEmission")
    emission.inputs["Color"].default_value = (*_color(values.get("GlowColor", "D6AAFF")), 1.0)
    strength = nodes.new("ShaderNodeMath")
    strength.operation = 'MULTIPLY'
    links.new(total, strength.inputs[0])
    strength.inputs[1].default_value = 2.5
    links.new(strength.outputs[0], emission.inputs["Strength"])
    add = nodes.new("ShaderNodeAddShader")
    links.new(inputs.outputs["Shader"], add.inputs[0])
    links.new(emission.outputs[0], add.inputs[1])
    links.new(add.outputs[0], output.inputs[0])
    return group


def _build_chamber_group(images, values):
    """Chamber: his colour texture leaves his tattoo out (side of his head, wrists); the game draws it on top, gold
    and metallic (the alpha of his multi-mask). "Tattoo" reveals it left to right (game: TattooAlpha), with a glowing
    edge where it's appearing (character select); at 1 it's fully there, as in game."""
    name = "VP Chamber Tattoo"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    for socket_name, default in (("Tattoo", 1.0), ("Time", 0.0)):
        face.new_socket(socket_name, in_out='INPUT', socket_type='NodeSocketFloat').default_value = default
    face.new_socket("Shader", in_out='OUTPUT', socket_type='NodeSocketShader')
    nodes, links = group.nodes, group.links
    inputs, output = nodes.new("NodeGroupInput"), nodes.new("NodeGroupOutput")

    def math_node(op, a, b=None, clamp=False):
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

    uv = nodes.new("ShaderNodeUVMap")
    uv.uv_map = "UV_SINGLE"
    mask = nodes.new("ShaderNodeTexImage")
    mask.image = _image(images, "MultiMask")
    links.new(uv.outputs[0], mask.inputs[0])
    tattoo = mask.outputs[1]  # alpha

    # the reveal: a left-to-right ramp across the texture (game: T_Mask_Ramp_LeftRight, tiled once), shown where it's
    # below the "Tattoo" value; a thin band at the front glows
    position = nodes.new("ShaderNodeSeparateXYZ")
    links.new(uv.outputs[0], position.inputs[0])
    ramp = math_node('SUBTRACT', 1.0, position.outputs[0])
    reveal = math_node('MULTIPLY', inputs.outputs["Tattoo"], 1.02)  # at 1 everything is shown
    shown = math_node('MULTIPLY', math_node('SUBTRACT', reveal, ramp), 60.0, clamp=True)
    edge_width = float(values.get("EdgeThickness", 0.3)) * 0.1
    edge = math_node('MULTIPLY', math_node('SUBTRACT', 1.0, math_node('MULTIPLY', math_node('ABSOLUTE', math_node('SUBTRACT', reveal, ramp)), 1 / edge_width), clamp=True),
                     math_node('SUBTRACT', 1.0, math_node('GREATER_THAN', inputs.outputs["Tattoo"], 0.999)))
    amount = math_node('MULTIPLY', tattoo, shown)

    gold = nodes.new("ShaderNodeBsdfPrincipled")
    gold.inputs["Base Color"].default_value = (*_color(values.get("TattooColor", "BA7E4C")), 1.0)
    gold.inputs["Metallic"].default_value = float(values.get("TattooMetallic", 1.0))
    gold.inputs["Roughness"].default_value = float(values.get("TattooRoughness", 0.5))
    mix = nodes.new("ShaderNodeMixShader")
    links.new(amount, mix.inputs[0])
    links.new(inputs.outputs["Shader"], mix.inputs[1])
    links.new(gold.outputs[0], mix.inputs[2])
    glow = nodes.new("ShaderNodeEmission")
    glow.inputs["Color"].default_value = (*_color(values.get("EdgeColor", "FFFFCF")), 1.0)
    links.new(math_node('MULTIPLY', math_node('MULTIPLY', tattoo, edge), 8.0), glow.inputs["Strength"])
    add = nodes.new("ShaderNodeAddShader")
    links.new(mix.outputs[0], add.inputs[0])
    links.new(glow.outputs[0], add.inputs[1])
    links.new(add.outputs[0], output.inputs[0])
    return group


def _build_waylay_group(images, values):
    """Waylay: her crystal shards (separate pieces on her arms, their own material) glow at their edges (the floaters
    mask: white edges, dark cores), lime green turning yellow toward the hand; her nails glow softly and her hair's
    light tips glow white; all when the game lights her tattoos (BP_TattooGlow, raised by her abilities).
    Part: 0 her arm, 1 her hair, 2 her shards."""
    name = "VP Waylay Glow"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    face.new_socket("Albedo", in_out='INPUT', socket_type='NodeSocketColor')
    for socket_name in ("Tattoos", "Time", "Hair", "Shard"):
        face.new_socket(socket_name, in_out='INPUT', socket_type='NodeSocketFloat').default_value = 0.0
    face.new_socket("Shader", in_out='OUTPUT', socket_type='NodeSocketShader')
    nodes, links = group.nodes, group.links
    inputs, output = nodes.new("NodeGroupInput"), nodes.new("NodeGroupOutput")

    def math_node(op, a, b=None, clamp=False):
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

    def texture(role, uv_name="UV_SINGLE"):
        uv = nodes.new("ShaderNodeUVMap")
        uv.uv_map = uv_name
        node = nodes.new("ShaderNodeTexImage")
        node.image = _image(images, role)
        links.new(uv.outputs[0], node.inputs[0])
        return node

    def separate(color_socket):
        node = nodes.new("ShaderNodeSeparateColor")
        links.new(color_socket, node.inputs[0])
        return node.outputs

    on = inputs.outputs["Tattoos"]
    hair_part, shard_part = inputs.outputs["Hair"], inputs.outputs["Shard"]
    arm_part = math_node('SUBTRACT', math_node('SUBTRACT', 1.0, hair_part), shard_part, clamp=True)
    green = _color(values.get("ArmColor", "C7FF78"))
    yellow = _color(values.get("HandColor", "FAFF26"))

    # her shards: bright at the edges, fading into dark cores; green, a touch yellower than the arm's green
    shard_mask = separate(texture("FloatersMask").outputs[0])[0]
    shard_glow = math_node('MULTIPLY', math_node('MULTIPLY', math_node('POWER', shard_mask, 2.0), on), math_node('MULTIPLY', shard_part, 1.8))
    shard_color = tuple((g * 2 + y) / 3 for g, y in zip(green, yellow))
    shards = nodes.new("ShaderNodeEmission")
    shards.inputs["Color"].default_value = (*shard_color, 1.0)
    links.new(shard_glow, shards.inputs["Strength"])
    # her nails (the alpha of her arm mask), softly
    nails_mask = texture("Masks").outputs[1]
    nails = nodes.new("ShaderNodeEmission")
    nails.inputs["Color"].default_value = (*yellow, 1.0)
    links.new(math_node('MULTIPLY', math_node('MULTIPLY', nails_mask, on), math_node('MULTIPLY', arm_part, 1.2)), nails.inputs["Strength"])
    # hair: its light tips (bright in its colour texture) glow white
    albedo = separate(inputs.outputs["Albedo"])
    light = math_node('MULTIPLY', math_node('SUBTRACT', math_node('MAXIMUM', albedo[0], albedo[1]), 0.35), 2.5, clamp=True)
    hair = nodes.new("ShaderNodeEmission")
    hair.inputs["Color"].default_value = (*_color(values.get("HairColor", "FFFFFF")), 1.0)
    links.new(math_node('MULTIPLY', math_node('MULTIPLY', light, on), math_node('MULTIPLY', hair_part, 0.6)), hair.inputs["Strength"])

    result = inputs.outputs["Shader"]
    for glow in (shards, nails, hair):
        add = nodes.new("ShaderNodeAddShader")
        links.new(result, add.inputs[0])
        links.new(glow.outputs[0], add.inputs[1])
        result = add.outputs[0]
    links.new(result, output.inputs[0])
    return group


# each agent with a glow: its shader, which of its materials get it (name contains), and its controls on the
# armature: (custom property, the group's input, default, description)
AGENTS = {
    "vampire": dict(
        node=GROUP, build=_build_group, materials="vampire",
        controls=[(HEART, "Heart", 1.0, CONTROLS[HEART][1]), (EYES, "Eyes", 0.0, CONTROLS[EYES][1]),
                  (ULTIMATE, "Ultimate", 0.0, CONTROLS[ULTIMATE][1])]),
    "sequoia": dict(
        node="VP Iso Glow", build=_build_iso_group, materials="sequoia", skip=("hair",),
        controls=[("Glow: outfit", "Outfit", 0.0, "Iso's outfit lines glowing"),
                  ("Glow: hands (E)", "Hands E", 0.0, "Iso's forearms glowing, as with his E (charged punches)"),
                  ("Glow: hands (abilities)", "Hands abilities", 0.0, "Iso's gloves glowing, as with his other abilities")]),
    "deadeye": dict(
        node="VP Chamber Tattoo", build=_build_chamber_group, materials="deadeye", skip=("hair", "glass"),
        controls=[("Tattoo", "Tattoo", 1.0, "Chamber's gold tattoo (side of his head, wrists); from 0 it reveals left to right with a glowing edge, as in character select")]),
    "terra": dict(
        node="VP Waylay Glow", build=_build_waylay_group, materials=("terra_s0_arm", "terra_s0_hair", "terra_s0_floaters"),
        controls=[("Glow: tattoos", "Tattoos", 0.0, "Waylay's crystal tattoos, nails and hair tips glowing, as when she uses her abilities")]),
}


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


def _armature_of(obj):
    if obj.parent is not None and obj.parent.type == 'ARMATURE':
        return obj.parent
    return next((m.object for m in obj.modifiers if m.type == 'ARMATURE' and m.object), None)


def apply(glow, objects, load_image):
    """glow: what the app sent ({"Agent", "Textures": role -> game path, "Values"}); objects: the agent's new objects;
    load_image(game path) -> image. Adds the agent's glow layer to its own materials (body, hair; not the eye overlay
    shared by agents) and its controls to its armature."""
    spec = AGENTS.get((glow or {}).get("Agent", "").lower())
    if spec is None:
        return 0
    images = {}
    for role, path in (glow.get("Textures") or {}).items():
        try:
            images[role] = load_image(path)
        except Exception:
            images[role] = None
    group = spec["build"](images, glow.get("Values") or {})
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    done = 0
    for obj in objects:
        if obj.type != 'MESH':
            continue
        armature = _armature_of(obj)
        if armature is None or obj.name.upper().startswith("FP_"):
            continue  # the 1st person arms have their own layouts (not done yet)
        for prop, _, default, description in spec["controls"]:
            if prop not in armature:
                armature[prop] = default
                armature.id_properties_ui(prop).update(min=0.0, max=1.0, soft_min=0.0, soft_max=1.0, description=description)
        for slot in obj.material_slots:
            material = slot.material
            name = (material.name if material else "").lower()
            wanted = (spec["materials"],) if isinstance(spec["materials"], str) else spec["materials"]
            if material is None or not material.use_nodes or not [word for word in wanted if word in name] or \
                    [word for word in spec.get("skip", ()) if word in name]:
                continue
            tree = material.node_tree
            if tree.nodes.get(spec["node"]) is not None:  # sent again: it already glows
                continue
            out = next((n for n in tree.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output), None)
            if out is None or not out.inputs["Surface"].is_linked:
                continue
            source = out.inputs["Surface"].links[0].from_socket
            node = tree.nodes.new("ShaderNodeGroup")
            node.node_tree = group
            node.name = node.label = spec["node"]
            node.location = (out.location.x, out.location.y - 260)
            if "Hair" in node.inputs:
                node.inputs["Hair"].default_value = 1.0 if "hair" in name else 0.0
            if "Shard" in node.inputs:
                node.inputs["Shard"].default_value = 1.0 if "floaters" in name else 0.0
            tree.links.new(source, node.inputs["Shader"])
            tree.links.new(node.outputs[0], out.inputs["Surface"])
            for role, key in (("MRAE", "mrae"), ("Albedo", "_df")):
                found = next((n for n in tree.nodes if n.type == 'TEX_IMAGE' and n.image and key in n.image.name.lower()), None)
                if found is not None and role in node.inputs:
                    tree.links.new(found.outputs[0], node.inputs[role])
            pending.append((material, armature, fps, spec))
            done += 1
    return done


# materials whose glow controls are connected once the import is over (see connect_pending)
pending = []


def connect_pending():
    """Connects the glow layers to their armature's controls (the "Glow: ..." properties) and to the timeline. Done
    after the import, and the armature is told it changed: Blender's evaluated copy of it doesn't have the new
    properties until then, and drivers reading them before that are marked invalid."""
    while pending:
        material, armature, fps, spec = pending.pop()
        try:
            tree = material.node_tree
            node = tree.nodes.get(spec["node"])
        except ReferenceError:
            continue  # deleted meanwhile
        if node is None:
            continue
        for prop, socket, _, _ in spec["controls"]:
            _drive(tree, node, socket, armature, prop)
        time = tree.driver_add(f'nodes["{node.name}"].inputs["Time"].default_value')
        time.driver.expression = f"frame / {fps:g}"
        armature.update_tag()
        for curve in tree.animation_data.drivers:
            curve.driver.is_valid = True
    return None  # (as a timer: don't run again)
