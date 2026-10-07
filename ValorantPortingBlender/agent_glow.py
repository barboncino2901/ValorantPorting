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
("Glow: tattoos").
Clove ("Smonk"): her ultimate's ghost (her resurrection model) fading in, then her ult glow ("Ult: respawn", "Ult: ghost", "Ult: active"); her
legs glowing with her C ("Glow: legs (C)").
Neon ("Sprinter"): her suit lit when she sprints ("Glow: sprint (E)"); in her ultimate her suit, tattoo and the edges of
her skin glow cyan with electricity, her hair blue to yellow ("Glow: ultimate"; after a Blender setup made for her).
Jett ("Wushu"): her arms streaming cyan wind in her ultimate ("Glow: arms (ult)").
Phoenix: flames over him in his ultimate ("Glow: ultimate") and while healing in his fire ("Glow: heal").
Veto ("Pine"): his ultimate's symbiote form (a second model, put on his rig), spreading over him ("Ult form")."""
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


def _build_veto_group(images, values):
    """Veto: his ultimate turns him into his symbiote form, a second model on the same rig. "Ult form" 0 -> 1 spreads
    it over him from his feet up in bubbling cells (the game's Pine_UltTeleportWPO_TFX cells), his normal model
    disappearing where it arrives, with a glowing orange-gold edge between them. Reveal: 0 his normal model's
    materials, 1 the symbiote form's."""
    name = "VP Veto Ult Form"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    for socket_name in ("Form", "Reveal", "Time"):
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

    width = float(values.get("EdgeWidth", 0.06))
    # where on him: height (the model's own box, before posing: the spread stays on the body when he moves) plus cells
    coords = nodes.new("ShaderNodeTexCoord")
    generated = nodes.new("ShaderNodeSeparateXYZ")
    links.new(coords.outputs["Generated"], generated.inputs[0])
    scaled = nodes.new("ShaderNodeVectorMath")
    scaled.operation = 'MULTIPLY'
    scaled.inputs[1].default_value = (4.0, 2.0, 8.0)  # about round cells on a body ~2x as tall as wide
    links.new(coords.outputs["Generated"], scaled.inputs[0])
    cells = nodes.new("ShaderNodeTexImage")
    cells.image = _image(images, "Cells")
    cells.projection = 'BOX'
    cells.projection_blend = 0.3
    links.new(scaled.outputs[0], cells.inputs[0])
    cell_value = cells.outputs[1] if cells.image is not None else 0.5  # alpha: the cells
    field = math_node('ADD', math_node('MULTIPLY', generated.outputs[2], 0.75), math_node('MULTIPLY', cell_value, 0.25))
    # the front: below it the symbiote form, above it his normal model
    front = math_node('SUBTRACT', math_node('MULTIPLY', inputs.outputs["Form"], 1.0 + 2.0 * width), width)
    distance = math_node('SUBTRACT', field, front)
    side = math_node('MULTIPLY', distance, math_node('SUBTRACT', 1.0, math_node('MULTIPLY', inputs.outputs["Reveal"], 2.0)))
    visible = math_node('GREATER_THAN', side, 0.0)
    # the glowing edge, brightest at the front
    edge = math_node('SUBTRACT', 1.0, math_node('DIVIDE', math_node('ABSOLUTE', distance), width), clamp=True)
    tint = nodes.new("ShaderNodeMix")
    tint.data_type = 'RGBA'
    links.new(edge, tint.inputs[0])
    tint.inputs[6].default_value = (*_color(values.get("EdgeColorA", "C68A00")), 1.0)
    tint.inputs[7].default_value = (*_color(values.get("EdgeColorB", "FFBF00")), 1.0)
    glow = nodes.new("ShaderNodeEmission")
    links.new(tint.outputs[2], glow.inputs["Color"])
    links.new(math_node('MULTIPLY', math_node('POWER', edge, 3.0), 5.0), glow.inputs["Strength"])
    lit = nodes.new("ShaderNodeAddShader")
    links.new(inputs.outputs["Shader"], lit.inputs[0])
    links.new(glow.outputs[0], lit.inputs[1])
    gone = nodes.new("ShaderNodeBsdfTransparent")
    mix = nodes.new("ShaderNodeMixShader")
    links.new(visible, mix.inputs[0])
    links.new(gone.outputs[0], mix.inputs[1])
    links.new(lit.outputs[0], mix.inputs[2])
    links.new(mix.outputs[0], output.inputs[0])
    return group


ULT_FORM_TAG = " (ult form)"


def _put_form_on_rig(objects, form_word, codename, offset=0.0):
    """Puts an agent's other model (Veto's symbiote form, Clove's ghost; it comes with its own copy of the same
    skeleton) on the rig of her normal model, gives it its own materials (tagged "(ult form)") and gives the shared
    eye overlay a copy of its own (named after the agent, so it gets the layer)."""
    meshes = [o for o in objects if o.type == 'MESH' and not o.name.upper().startswith("FP_")]
    form = next((o for o in meshes if form_word in o.name.lower()), None)
    body = next((o for o in meshes if o is not form and _armature_of(o) is not None), None)
    if body is not None:
        for slot in body.material_slots:
            if slot.material is not None and codename not in slot.material.name.lower() and slot.material.name != "None":
                copy = slot.material.copy()
                copy.name = f"{slot.material.name} ({codename.capitalize()})"
                slot.material = copy
    if form is None or body is None:
        return objects
    rig, own_rig = _armature_of(body), _armature_of(form)
    if own_rig is not None and own_rig is not rig:
        for modifier in form.modifiers:
            if modifier.type == 'ARMATURE':
                modifier.object = rig
        world = form.matrix_world.copy()
        form.parent = rig
        form.matrix_world = world
        data = own_rig.data
        bpy.data.objects.remove(own_rig)
        if data.users == 0:
            bpy.data.armatures.remove(data)
        for collection in rig.users_collection:
            if collection not in form.users_collection:
                collection.objects.link(form)
    if offset and form.modifiers.get("VP Ult Form Offset") is None:
        displace = form.modifiers.new("VP Ult Form Offset", 'DISPLACE')
        displace.direction = 'NORMAL'
        displace.mid_level = 0.0
        displace.strength = offset
    for slot in form.material_slots:
        if slot.material is not None and ULT_FORM_TAG not in slot.material.name and slot.material.name != "None":
            copy = slot.material.copy()
            copy.name = slot.material.name + ULT_FORM_TAG
            slot.material = copy
    return [o for o in objects if o is not own_rig]


def _prepare_veto(objects):
    """His symbiote form on his rig; its outfit is his normal outfit's shape exactly: pushed out 1 mm so the renderers
    can tell the two apart (where it is hidden, the outfit under it must show)."""
    return _put_form_on_rig(objects, "pine_sym", "pine", offset=0.001)


def _build_clove_group(images, values):
    """Clove: her ultimate and her C.
    Ghost: 0 -> 1 her ghost (the resurrection model, see-through pink as in game) takes her model's place from her
    head down. Respawn: 0 -> 1 she (or her ghost) fades in from her head down. Ult: her normal model with her ult's
    glow (the game's glow mask, a blue rim, her ult hair colours), until she gets a kill.
    Legs (C): her legs glow purple up to 90 cm, smoke waves rising and the butterflies on her trousers lit.
    Hair: 1 on her hair's material. Height: her model's height (m), for the legs. Reveal: 1 on her ghost's material."""
    name = "VP Clove Glow"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    for socket_name, default in (("Respawn", 1.0), ("Ghost", 0.0), ("Ult", 0.0), ("Legs", 0.0), ("Time", 0.0),
                                 ("Hair", 0.0), ("Height", 1.75), ("Reveal", 0.0)):
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

    def texture(role):
        uv = nodes.new("ShaderNodeUVMap")
        uv.uv_map = "UV_SINGLE"
        node = nodes.new("ShaderNodeTexImage")
        node.image = _image(images, role)
        links.new(uv.outputs[0], node.inputs[0])
        return node

    def separate(color_socket):
        node = nodes.new("ShaderNodeSeparateColor")
        links.new(color_socket, node.inputs[0])
        return node.outputs

    def emission(color, strength):
        node = nodes.new("ShaderNodeEmission")
        if isinstance(color, tuple):
            node.inputs["Color"].default_value = (*color, 1.0)
        else:
            links.new(color, node.inputs["Color"])
        links.new(strength, node.inputs["Strength"])
        return node.outputs[0]

    def add(a, b):
        node = nodes.new("ShaderNodeAddShader")
        links.new(a, node.inputs[0])
        links.new(b, node.inputs[1])
        return node.outputs[0]

    def mix_shader(fac, a, b):
        node = nodes.new("ShaderNodeMixShader")
        links.new(fac, node.inputs[0])
        links.new(a, node.inputs[1])
        links.new(b, node.inputs[2])
        return node.outputs[0]

    def ramp(fac, stops):
        node = nodes.new("ShaderNodeValToRGB")
        elements = node.color_ramp.elements
        while len(elements) < len(stops):
            elements.new(0.5)
        for element, (position, color) in zip(elements, stops):
            element.position = position
            element.color = (*color, 1.0)
        links.new(fac, node.inputs[0])
        return node.outputs[0]

    hair = inputs.outputs["Hair"]
    body = math_node('SUBTRACT', 1.0, hair)
    coords = nodes.new("ShaderNodeTexCoord")
    generated = separate(coords.outputs["Generated"])  # her model's own box (before posing): 0 feet, 1 head
    rim = nodes.new("ShaderNodeLayerWeight")
    rim.inputs["Blend"].default_value = 0.5
    facing = rim.outputs["Facing"]  # 0 facing the camera, 1 at her outline

    # her ult: the game's glow mask (her TP_ULT_ colours where it is white), a blue rim; her hair's tips
    ult = inputs.outputs["Ult"]
    ult_albedo = texture("UltAlbedo").outputs[0]
    ult_mask = separate(texture("UltMask").outputs[0])[0]
    ult_mask = math_node('DIVIDE', math_node('SUBTRACT', ult_mask, 0.6), 0.4, clamp=True)  # its bright parts (eyes, ...)
    glow = float(values.get("UltGlow", 2.5))
    body_glow = emission(ult_albedo, math_node('MULTIPLY', math_node('MULTIPLY', ult_mask, ult), math_node('MULTIPLY', body, glow)))
    blue_rim = emission(_color(values.get("UltFresnelColor", "6BBEFF")),
                        math_node('MULTIPLY', math_node('MULTIPLY', math_node('POWER', facing, 4.0), ult), 0.5))
    hair_mask = separate(texture("UltHairMask").outputs[0])[0]
    tips = math_node('MULTIPLY', math_node('SUBTRACT', hair_mask, 0.5), 2.0, clamp=True)  # Hair_EM_Mask_Min 0.5
    # her hair glows its ult colours, washed out (Hair_Saturation 0.25): light pink, its tips light blue
    washed = nodes.new("ShaderNodeHueSaturation")
    washed.inputs["Saturation"].default_value = 0.25
    washed.inputs["Value"].default_value = 1.6
    links.new(texture("UltHairAlbedo").outputs[0], washed.inputs["Color"])
    horns = emission(washed.outputs[0], math_node('MULTIPLY', math_node('MULTIPLY', tips, ult), math_node('MULTIPLY', hair, 1.2)))
    result = add(add(add(inputs.outputs["Shader"], body_glow), blue_rim), horns)

    # her C: her legs up to LegGlowHeight glow purple at their outline, smoke waves rising; her trousers' butterflies
    legs = inputs.outputs["Legs"]
    height = math_node('MULTIPLY', generated[2], inputs.outputs["Height"])
    leg_part = math_node('DIVIDE', math_node('SUBTRACT', float(values.get("LegGlowHeight", 0.9)), height), 0.35, clamp=True)
    leg_part = math_node('MULTIPLY', math_node('MULTIPLY', leg_part, legs), body)
    waves_uv = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('MULTIPLY', math_node('ADD', generated[0], generated[1]), 2.0), waves_uv.inputs[0])
    links.new(math_node('SUBTRACT', math_node('MULTIPLY', generated[2], 2.0), math_node('MULTIPLY', inputs.outputs["Time"], 0.4)), waves_uv.inputs[1])
    waves = nodes.new("ShaderNodeTexImage")
    waves.image = _image(images, "Waves")
    links.new(waves_uv.outputs[0], waves.inputs[0])
    wave = separate(waves.outputs[0])[0]
    leg_light = math_node('ADD', math_node('MULTIPLY', math_node('POWER', facing, 2.0), 1.5), math_node('MULTIPLY', wave, 0.4))
    leg_glow = emission(_color(values.get("LegColor", "DC6CFF")), math_node('MULTIPLY', leg_light, leg_part))
    butterflies = separate(texture("CMask").outputs[0])[0]
    butterfly_glow = emission(_color(values.get("ButterflyColor", "FFB8FF")),
                              math_node('MULTIPLY', math_node('MULTIPLY', butterflies, legs), math_node('MULTIPLY', body, 3.0)))
    result = add(add(result, leg_glow), butterfly_glow)

    # her ghost (the resurrection model; the game's 3P_SMO_X_Dismiss-Formless_MAT): unlit and see-through, pale
    # lavender turning pink at its outline (SMO_X_Color-Soft_LUT), a bright pink rim, glowing eyes
    lut = nodes.new("ShaderNodeTexImage")
    lut.image = _image(images, "GhostLut")
    lut.extension = 'EXTEND'
    lut_uv = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('POWER', facing, 0.75), lut_uv.inputs[0])
    lut_uv.inputs[1].default_value = 0.5
    links.new(lut_uv.outputs[0], lut.inputs[0])
    ghost_body = emission(lut.outputs[0] if lut.image is not None else (0.82, 0.64, 1.0), math_node('ADD', 0.0, 0.8))
    ghost_rim = emission(_color(values.get("GhostRimColor", "FF3CC8")),
                         math_node('MULTIPLY', math_node('POWER', facing, 2.0), 6.0))
    eyes = separate(texture("GhostEyes").outputs[0])[0]
    ghost_eyes = emission((1.0, 0.92, 1.0), math_node('MULTIPLY', eyes, 6.0))
    see_through = nodes.new("ShaderNodeBsdfTransparent")
    opacity = math_node('ADD', 0.3, math_node('MULTIPLY', math_node('POWER', facing, 0.75), 0.7), clamp=True)
    opacity = math_node('MAXIMUM', opacity, eyes)
    ghost = mix_shader(opacity, see_through.outputs[0], add(add(ghost_body, ghost_rim), ghost_eyes))
    reveal = inputs.outputs["Reveal"]
    result = mix_shader(reveal, result, ghost)

    # which is shown: "Ghost" swaps her model for her ghost from her head down; "Respawn" fades either in from her head
    # down, the edge sparking pink
    width = 0.05
    edge_noise = nodes.new("ShaderNodeTexNoise")
    edge_noise.inputs["Scale"].default_value = 13.0
    links.new(coords.outputs["Generated"], edge_noise.inputs["Vector"])
    field = math_node('ADD', math_node('MULTIPLY', math_node('SUBTRACT', 1.0, generated[2]), 0.85),
                      math_node('MULTIPLY', edge_noise.outputs["Fac"], 0.15))  # 0 her head .. 1 her feet

    def front(control):
        return math_node('SUBTRACT', math_node('MULTIPLY', control, 1.0 + 2.0 * width), width)

    ghost_side = math_node('MULTIPLY', math_node('SUBTRACT', front(inputs.outputs["Ghost"]), field),
                           math_node('SUBTRACT', math_node('MULTIPLY', reveal, 2.0), 1.0))  # > 0: this one shown
    respawn_side = math_node('SUBTRACT', front(inputs.outputs["Respawn"]), field)
    shown = math_node('MULTIPLY', math_node('GREATER_THAN', ghost_side, 0.0), math_node('GREATER_THAN', respawn_side, 0.0))

    def edge(distance):
        return math_node('POWER', math_node('SUBTRACT', 1.0, math_node('DIVIDE', math_node('ABSOLUTE', distance), width), clamp=True), 3.0)

    sparks = emission(_color(values.get("SparkColor", "FFB4F0")),
                      math_node('MULTIPLY', math_node('MAXIMUM', edge(ghost_side), edge(respawn_side)), 6.0))
    gone = nodes.new("ShaderNodeBsdfTransparent")
    result = mix_shader(shown, gone.outputs[0], add(result, sparks))
    links.new(result, output.inputs[0])
    return group


def _prepare_clove(objects):
    """Her ghost (the resurrection model) on her rig."""
    return _put_form_on_rig(objects, "resurrection", "smonk")


def _swap_clove_albedo(tree, node, name, images):
    """Her ult's hair colours (TP_ULT_Smonk_S0_Hair_DF) replace her own while "Ult" is up: a mix after its colour
    texture."""
    if "hair" not in name or ULT_FORM_TAG in name:
        return  # in game her ult keeps her own colours, but for her hair
    image = _image(images, "UltHairAlbedo")
    found = next((n for n in tree.nodes if n.type == 'TEX_IMAGE' and n.image and "_df" in n.image.name.lower()
                  and "ult" not in n.image.name.lower()), None)
    if image is None or found is None:
        return
    ult = tree.nodes.new("ShaderNodeTexImage")
    ult.image = image
    ult.location = (found.location.x, found.location.y - 300)
    uv = found.inputs[0].links[0].from_socket if found.inputs[0].is_linked else None
    if uv is not None:
        tree.links.new(uv, ult.inputs[0])
    mix = tree.nodes.new("ShaderNodeMix")
    mix.data_type = 'RGBA'
    mix.name = mix.label = "VP Clove Ult Colours"
    mix.location = (found.location.x + 300, found.location.y)
    for link in list(found.outputs[0].links):
        target = link.to_socket
        tree.links.remove(link)
        tree.links.new(mix.outputs[2], target)
    tree.links.new(found.outputs[0], mix.inputs[6])
    tree.links.new(ult.outputs[0], mix.inputs[7])


def _build_neon_group(images, values):
    """Neon (after a Blender setup made for her ultimate): Ult (Overdrive): her suit's lines and tattoo glow cyan, the
    edges of her skin glow with electricity moving over them (TP_Sprinter_Ult_Masks_Body01_T R and G, the model's
    vertex colours G, TP_Sprinter_Ult_Bioelectricity01_T), the strips of her suit in their own colours
    (TP_Sprinter_Ult_Colors_Body01_T), and her hair blue at the roots turning yellow at the tips
    (TP_Sprinter_Ult_Masks_Hair01_T: the way along each strand).
    Sprint (her E): her suit's strips and lines only. Hair: 1 on her hair. Albedo: her colour texture."""
    name = "VP Neon Glow"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    face.new_socket("Albedo", in_out='INPUT', socket_type='NodeSocketColor')
    for socket_name in ("Sprint", "Ult", "Time", "Hair"):
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

    def texture(role, vector=None):
        node = nodes.new("ShaderNodeTexImage")
        node.image = _image(images, role)
        if vector is None:
            uv = nodes.new("ShaderNodeUVMap")
            uv.uv_map = "UV_SINGLE"
            vector = uv.outputs[0]
        links.new(vector, node.inputs[0])
        return node

    def separate(color_socket):
        node = nodes.new("ShaderNodeSeparateColor")
        links.new(color_socket, node.inputs[0])
        return node.outputs

    def mix(blend, factor, a, b):
        node = nodes.new("ShaderNodeMix")
        node.data_type = 'RGBA'
        node.blend_type = blend
        for socket, v in ((node.inputs[0], factor), (node.inputs[6], a), (node.inputs[7], b)):
            if isinstance(v, (int, float)):
                socket.default_value = v
            elif isinstance(v, tuple):
                socket.default_value = (*v, 1.0)
            else:
                links.new(v, socket)
        return node.outputs[2]

    def ramp(fac, stops):
        node = nodes.new("ShaderNodeValToRGB")
        elements = node.color_ramp.elements
        while len(elements) < len(stops):
            elements.new(0.5)
        for element, (position, color) in zip(elements, stops):
            element.position = position
            element.color = (*color, 1.0)
        links.new(fac, node.inputs[0])
        return node.outputs[0]

    def emission(color, strength):
        node = nodes.new("ShaderNodeEmission")
        links.new(color, node.inputs["Color"])
        links.new(strength, node.inputs["Strength"])
        return node.outputs[0]

    hair = inputs.outputs["Hair"]
    body = math_node('SUBTRACT', 1.0, hair)
    ult = inputs.outputs["Ult"]
    strength = float(values.get("GlowStrength", 4.0))
    cyan = (0.0364, 0.864, 1.0)
    vertex = nodes.new("ShaderNodeVertexColor")
    vertex.layer_name = "PSKVTXCOL_0"
    vertex_rgb = separate(vertex.outputs["Color"])
    rim = nodes.new("ShaderNodeLayerWeight")
    rim.inputs["Blend"].default_value = 0.3
    fresnel = rim.outputs["Fresnel"]

    # her body: the lines (R) and tattoo, the edges of her skin (G + vertex G) with the electricity moving over them
    masks = separate(texture("UltMasks").outputs[0])
    tattoo = separate(texture("TattooMask").outputs[0])[0]
    uv = nodes.new("ShaderNodeUVMap")
    uv.uv_map = "UV_SINGLE"
    moving = nodes.new("ShaderNodeVectorMath")
    moving.operation = 'MULTIPLY_ADD'
    links.new(uv.outputs[0], moving.inputs[0])
    moving.inputs[1].default_value = (2.0, 2.0, 1.0)
    shift = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('MULTIPLY', inputs.outputs["Time"], -0.5), shift.inputs[1])
    links.new(shift.outputs[0], moving.inputs[2])
    veins = separate(texture("Bioelectricity", moving.outputs[0]).outputs[0])
    edge = nodes.new("ShaderNodeMapRange")
    links.new(fresnel, edge.inputs[0])
    edge.inputs[1].default_value, edge.inputs[2].default_value = 0.05, 0.3
    skin = mix('MULTIPLY', 1.0, mix('ADD', 1.0, masks[1], vertex_rgb[1]), edge.outputs[0])
    skin = mix('SOFT_LIGHT', 1.0, skin, mix('SOFT_LIGHT', 1.0, veins[1], veins[0]))
    glow = mix('ADD', 1.0, mix('ADD', 1.0, masks[0], tattoo), skin)
    colors = texture("UltColors").outputs[0]
    body_color = mix('MIX', glow, colors, cyan)
    # her sprint: the strips and lines only
    sprint = math_node('MULTIPLY', inputs.outputs["Sprint"], body)
    sprint_color = mix('MIX', masks[0], colors, cyan)

    # her hair: blue roots to yellow tips (its mask is the way along each strand; the setup used the character select
    # model's vertex colours, which the 3rd person hair doesn't have), with a faint rim in its own colours turned around
    turned = nodes.new("ShaderNodeHueSaturation")
    turned.inputs["Hue"].default_value = 0.5
    turned.inputs["Saturation"].default_value = 2.0
    links.new(inputs.outputs["Albedo"], turned.inputs["Color"])
    hair_mask = separate(texture("HairMask").outputs[0])[0]  # dark at the roots, bright at the tips
    along = ramp(hair_mask, (
        (0.00, (0.0, 0.00402, 0.0369)), (0.12, (0.00121, 0.0151, 0.134)), (0.25, (0.00243, 0.0262, 0.231)),
        (0.38, (0.0, 0.0765, 0.297)), (0.50, (0.0316, 0.171, 0.179)), (0.62, (0.13, 0.184, 0.013)),
        (0.75, (0.462, 0.445, 0.0)), (0.88, (0.73, 0.708, 0.0)), (1.00, (1.0, 0.965, 0.0))))
    hair_color = mix('ADD', 1.0, along, mix('MULTIPLY', 1.0, mix('MULTIPLY', 1.0, fresnel, turned.outputs[0]), (0.3, 0.3, 0.3)))

    color = mix('MIX', hair, body_color, hair_color)
    result = nodes.new("ShaderNodeAddShader")
    links.new(inputs.outputs["Shader"], result.inputs[0])
    links.new(emission(color, math_node('MULTIPLY', math_node('MULTIPLY', ult, strength), math_node('SUBTRACT', 1.0, math_node('MULTIPLY', hair, 0.4)))), result.inputs[1])
    with_sprint = nodes.new("ShaderNodeAddShader")
    links.new(result.outputs[0], with_sprint.inputs[0])
    links.new(emission(sprint_color, math_node('MULTIPLY', sprint, strength * 0.75)),
              with_sprint.inputs[1])
    links.new(with_sprint.outputs[0], output.inputs[0])
    return group


def _build_jett_group(images, values):
    """Jett: her arms' wind tattoos (TP_Wushu_S0_Tattoo: "Loop_Anim" while her ultimate is up, "Burst_Anim" as she
    casts). On her bare arms (TP_Wushu_S0_Mask G; one unwrapped arm, wrist to shoulder, shared by both): smoke streams
    (SmokeStream_9x2_TFX, 18 frames played over time) rising from the wrist, and a burst of wisps
    (Arm_Tattoo_Burst) running up the arm. How the game lays them on the arm isn't in its files: rebuilt by eye."""
    name = "VP Jett Tattoos"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    for socket_name in ("Loop", "Burst", "Time"):
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

    def sample(role, u, v, extension='CLIP'):
        combine = nodes.new("ShaderNodeCombineXYZ")
        links.new(u, combine.inputs[0])
        links.new(v, combine.inputs[1])
        node = nodes.new("ShaderNodeTexImage")
        node.image = _image(images, role)
        node.extension = extension
        links.new(combine.outputs[0], node.inputs[0])
        return node

    uv = nodes.new("ShaderNodeUVMap")
    uv.uv_map = "UV_SINGLE"
    split = nodes.new("ShaderNodeSeparateXYZ")
    links.new(uv.outputs[0], split.inputs[0])
    mask = nodes.new("ShaderNodeTexImage")
    mask.image = _image(images, "Mask")
    links.new(uv.outputs[0], mask.inputs[0])
    arms = nodes.new("ShaderNodeSeparateColor")
    links.new(mask.outputs[0], arms.inputs[0])
    arm = math_node('MULTIPLY', math_node('SUBTRACT', arms.outputs[1], 0.5), 2.0, clamp=True)  # (its grey elsewhere is not arm)

    # the arm's own coordinates: around it (0..1) and along it (0 wrist .. 1 shoulder)
    island_u, island_v = float(values.get("ArmIslandU", 0.208)), float(values.get("ArmIslandV", 0.2534))
    around = math_node('DIVIDE', split.outputs[0], island_u)
    along = math_node('DIVIDE', math_node('SUBTRACT', split.outputs[1], 1.0 - island_v), island_v)

    # smoke streams: three around the arm, the flipbook's 18 frames (9 x 2) played at 15 per second
    frame = math_node('FLOORED_MODULO', math_node('FLOOR', math_node('MULTIPLY', inputs.outputs["Time"], 15.0)), 18.0)
    column = math_node('FLOORED_MODULO', frame, 9.0)
    row = math_node('FLOOR', math_node('DIVIDE', frame, 9.0))
    cell_u = math_node('DIVIDE', math_node('ADD', math_node('FRACT', math_node('MULTIPLY', around, 3.0)), column), 9.0)
    cell_v = math_node('DIVIDE', math_node('ADD', math_node('MINIMUM', math_node('MAXIMUM', along, 0.0), 0.999), math_node('SUBTRACT', 1.0, row)), 2.0)
    smoke = sample("Smoke", cell_u, cell_v).outputs["Alpha"]
    # the burst: its wisps around the arm, a wave running from the wrist to the shoulder as it goes 0 -> 1, fading
    burst_v = math_node('ADD', math_node('MULTIPLY', math_node('SUBTRACT', along, math_node('SUBTRACT', math_node('MULTIPLY', inputs.outputs["Burst"], 1.4), 0.4)), 0.5), 0.5)
    wisps = sample("Burst", math_node('FRACT', around), burst_v).outputs["Alpha"]
    wisps = math_node('MULTIPLY', wisps, math_node('MULTIPLY', math_node('GREATER_THAN', burst_v, 0.5), math_node('LESS_THAN', burst_v, 1.0)))
    burst = math_node('MULTIPLY', wisps, math_node('MULTIPLY', math_node('SUBTRACT', 1.0, inputs.outputs["Burst"]),
                                                   math_node('GREATER_THAN', inputs.outputs["Burst"], 0.0)))

    amount = math_node('MULTIPLY', arm, math_node('ADD', math_node('MULTIPLY', smoke, inputs.outputs["Loop"]), burst))
    glow = nodes.new("ShaderNodeEmission")
    glow.inputs["Color"].default_value = (*_color(values.get("WindColor", "45E6F2")), 1.0)
    links.new(math_node('MULTIPLY', amount, float(values.get("WindStrength", 1.5))), glow.inputs["Strength"])
    add = nodes.new("ShaderNodeAddShader")
    links.new(inputs.outputs["Shader"], add.inputs[0])
    links.new(glow.outputs[0], add.inputs[1])
    links.new(add.outputs[0], output.inputs[0])
    return group


def _build_phoenix_group(images, values):
    """Phoenix: his ultimate (Run It Back) and his healing fire (body_Overlays_MI / Phoenix_Body_Overlay_Heal_MI):
    flames crawling over him (tex_tile_07 tiled 3 x 5, scrolling, raised to the 6th power so only the wisps' cores
    show; ULT_Masks R: where), the honeycomb on his chest glowing (ULT_Masks G), his outline glowing; orange to
    yellow. Ult: his ultimate; Heal: standing in his fire (softer, redder)."""
    name = "VP Phoenix Fire"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    for socket_name in ("Ult", "Heal", "Time", "Hair"):
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

    def emission(color, strength):
        node = nodes.new("ShaderNodeEmission")
        node.inputs["Color"].default_value = (*color, 1.0)
        links.new(strength, node.inputs["Strength"])
        return node.outputs[0]

    uv = nodes.new("ShaderNodeUVMap")
    uv.uv_map = "UV_SINGLE"
    fire = math_node('MAXIMUM', inputs.outputs["Ult"], math_node('MULTIPLY', inputs.outputs["Heal"], 0.5))
    body = math_node('SUBTRACT', 1.0, inputs.outputs["Hair"])

    # the flames: the tile scrolled up and sideways over time, wobbled by a second, slower copy
    def scrolled(scale, speed):
        node = nodes.new("ShaderNodeVectorMath")
        node.operation = 'MULTIPLY_ADD'
        links.new(uv.outputs[0], node.inputs[0])
        node.inputs[1].default_value = (*scale, 1.0)
        shift = nodes.new("ShaderNodeCombineXYZ")
        links.new(math_node('MULTIPLY', inputs.outputs["Time"], speed[0]), shift.inputs[0])
        links.new(math_node('MULTIPLY', inputs.outputs["Time"], speed[1]), shift.inputs[1])
        links.new(shift.outputs[0], node.inputs[2])
        return node.outputs[0]

    tile = float(values.get("FlameScaleU", 3.0)), float(values.get("FlameScaleV", 5.0))
    speed = float(values.get("FlameSpeed", 0.275))
    wobble = nodes.new("ShaderNodeTexImage")
    wobble.image = _image(images, "Flames")
    links.new(scrolled((tile[0] * 0.5, tile[1] * 0.5), (-speed * 0.7, -speed * 1.3)), wobble.inputs[0])
    flame_uv = nodes.new("ShaderNodeVectorMath")
    flame_uv.operation = 'MULTIPLY_ADD'
    links.new(wobble.outputs["Color"], flame_uv.inputs[0])
    flame_uv.inputs[1].default_value = (0.15, 0.15, 0.0)
    links.new(scrolled(tile, (speed * 2.5, speed)), flame_uv.inputs[2])
    flames_tex = nodes.new("ShaderNodeTexImage")
    flames_tex.image = _image(images, "Flames")
    links.new(flame_uv.outputs[0], flames_tex.inputs[0])
    # thin wisps: where the moving flame texture crosses a level (its contour lines), brighter where it is brighter
    level = math_node('ABSOLUTE', math_node('SUBTRACT', flames_tex.outputs["Color"], 0.62))
    wisps = math_node('POWER', math_node('SUBTRACT', 1.0, math_node('MULTIPLY', level, 22.0), clamp=True), 2.0)
    # only in patches (where the slower copy is bright), so they come and go over him
    patches = math_node('MULTIPLY', math_node('SUBTRACT', wobble.outputs["Color"], 0.6), 3.0, clamp=True)
    wisps = math_node('MULTIPLY', wisps, patches)

    masks = nodes.new("ShaderNodeTexImage")
    masks.image = _image(images, "Masks")
    links.new(uv.outputs[0], masks.inputs[0])
    channels = nodes.new("ShaderNodeSeparateColor")
    links.new(masks.outputs["Color"], channels.inputs[0])
    where, honeycomb = channels.outputs[0], channels.outputs[1]

    rim = nodes.new("ShaderNodeLayerWeight")
    rim.inputs["Blend"].default_value = 0.4
    edge = math_node('POWER', rim.outputs["Facing"], 2.5)

    hot = _color(values.get("FlameColorA", "FFC24A"))
    deep = _color(values.get("FlameColorB", "FF6A1A"))
    flames = emission(hot, math_node('MULTIPLY', math_node('MULTIPLY', math_node('MULTIPLY', wisps, where), fire), math_node('MULTIPLY', body, 4.0)))
    glow = emission(deep, math_node('MULTIPLY', math_node('MULTIPLY', honeycomb, fire), math_node('MULTIPLY', body, 4.0)))
    outline = emission(deep, math_node('MULTIPLY', math_node('MULTIPLY', edge, fire), math_node('MULTIPLY', where, 0.6)))

    result = inputs.outputs["Shader"]
    for shader in (flames, glow, outline):
        add = nodes.new("ShaderNodeAddShader")
        links.new(result, add.inputs[0])
        links.new(shader, add.inputs[1])
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
    "smonk": dict(
        node="VP Clove Glow", build=_build_clove_group, materials="smonk", prepare=_prepare_clove,
        per_material=_swap_clove_albedo, see_through=True,
        extra_drivers=[("VP Clove Ult Colours", "Factor", "Ult: active")],
        controls=[("Ult: respawn", "Respawn", 1.0, "Clove fading in from her head down (0 to 1), as she comes back with her ultimate (with \"Ult: ghost\" at 1: her ghost)"),
                  ("Ult: ghost", "Ghost", 0.0, "Clove's ghost (her resurrection model) instead of her: 0 to 1 it takes her place from her head down; back to 0 for her ult form"),
                  ("Ult: active", "Ult", 0.0, "Clove's ultimate is on: her ult colours and glow, until she gets a kill"),
                  ("Glow: legs (C)", "Legs", 0.0, "Clove's legs glowing, smoke rising and her trousers' butterflies lit, as with her C")]),
    "sprinter": dict(
        node="VP Neon Glow", build=_build_neon_group, materials="sprinter",
        controls=[("Glow: sprint (E)", "Sprint", 0.0, "Neon's suit lighting up blue, as when she sprints (her E)"),
                  ("Glow: ultimate", "Ult", 0.0, "Neon's ultimate: her suit, tattoo and the edges of her skin glowing cyan with electricity, her hair blue to yellow")]),
    "wushu": dict(
        node="VP Jett Tattoos", build=_build_jett_group, materials="wushu_s0_tattoo",
        controls=[("Glow: arms (ult)", "Loop", 0.0, "Jett's arms streaming wind, as while her ultimate is up")]),
    "phoenix": dict(
        node="VP Phoenix Fire", build=_build_phoenix_group, materials="phoenix_s0",
        controls=[("Glow: ultimate", "Ult", 0.0, "Phoenix's ultimate (Run It Back): flames crawling over him, his chest glowing"),
                  ("Glow: heal", "Heal", 0.0, "Phoenix healing in his own fire: softer flames over him")]),
    "pine": dict(
        node="VP Veto Ult Form", build=_build_veto_group, materials="pine", prepare=_prepare_veto, see_through=True,
        controls=[("Ult form", "Form", 0.0, "Veto's ultimate: from 0 to 1 his symbiote form spreads over him from his feet up, with a glowing edge")]),
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
    if spec.get("prepare"):
        objects = spec["prepare"](objects)
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
            if "Height" in node.inputs:
                node.inputs["Height"].default_value = max(obj.dimensions.z, 0.5)
            if spec.get("per_material"):
                spec["per_material"](tree, node, name, images)
            if "Reveal" in node.inputs:
                node.inputs["Reveal"].default_value = 1.0 if ULT_FORM_TAG in name else 0.0
            if spec.get("see_through"):
                if hasattr(material, "use_transparent_shadow"):
                    material.use_transparent_shadow = True  # its hidden parts cast no shadow (Eevee)

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
        for node_name, socket, prop in spec.get("extra_drivers", ()):
            if tree.nodes.get(node_name) is not None:
                _drive(tree, tree.nodes[node_name], socket, armature, prop)
        time = tree.driver_add(f'nodes["{node.name}"].inputs["Time"].default_value')
        time.driver.expression = f"frame / {fps:g}"
        armature.update_tag()
        for curve in tree.animation_data.drivers:
            curve.driver.is_valid = True
    return None  # (as a timer: don't run again)
