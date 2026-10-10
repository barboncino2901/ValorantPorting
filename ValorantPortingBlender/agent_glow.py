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
    for name, default in (("Heart", 1.0), ("Eyes", 0.0), ("Ultimate", 0.0), ("Time", 0.0), ("Hair", 0.0), ("Dismiss", 0.0), ("Tattoos", 0.0)):
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
        if isinstance(factor, (int, float)):
            node.inputs[0].default_value = factor
        else:
            links.new(factor, node.inputs[0])
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
    dismiss_in = inputs.outputs["Dismiss"]
    amount = math_node('MULTIPLY', math_node('MULTIPLY', gem, math_node('ADD', heart_on, math_node('MULTIPLY', dismiss_in, 2.0))), 10.0)

    # the chest flare: the game's flare texture as a thin vertical streak over the heart (where on her UVs: the heart's
    # centre, the streak's width and height), brighter in the ultimate
    cu, cv = float(values.get("HeartU", 0.3462)), float(values.get("HeartV", 0.1084))
    fw, fh = float(values.get("FlareWidth", 0.03)), float(values.get("FlareHeight", 0.09))
    flare = texture("ChestFlare", (1 / fw, 1 / fh), location=(0.5 - cu / fw, 0.5 - cv / fh), clip=True)
    if flare is not None:
        flare_on = math_node('ADD', math_node('ADD', heart_on, math_node('MULTIPLY', ult, 0.8)), math_node('MULTIPLY', dismiss_in, 2.0))
        amount = math_node('ADD', amount, math_node('MULTIPLY', math_node('MULTIPLY', separate(flare.outputs[0])[0], flare_on), 12.0))

    # the eyes: iris and pupil (the alpha of the mask set, on her body's UVs); the ultimate lights them too
    xe = texture("MaskSetsXE")
    albedo = separate(inputs.outputs["Albedo"])
    if xe is not None:
        # the mask is a disc over the whole eye: in her colour texture the iris is a purple ring between the grey white
        # and the black pupil, so only what's purple (red and blue both above green) glows, as in game
        iris = math_node('MULTIPLY', math_node('SUBTRACT', math_node('MINIMUM', albedo[0], albedo[2]), albedo[1]), 8.0, clamp=True)
        eyes_on = math_node('MAXIMUM', math_node('MAXIMUM', inputs.outputs["Eyes"], ult), inputs.outputs["Dismiss"])
        amount = math_node('ADD', amount, math_node('MULTIPLY', math_node('MULTIPLY', math_node('MULTIPLY', xe.outputs[1], iris), eyes_on), 2.5))

    # the ultimate: tattoo lines lit by energy flowing through them (3P_Vampire_X_FX: TextureA tiled 3x, scrolling -0.1)
    tattoo = texture("TattooMask")
    if tattoo is not None:
        tile = float(values.get("EnergyTile", 3.0))
        energy = texture("Energy", (tile, tile), (0.0, float(values.get("EnergyScrollV", -0.1))))
        lit = math_node('ADD', separate(energy.outputs[0])[0], 0.35) if energy else 1.0
        strength = 1.0 * float(values.get("UltTattooMultiply", 3.0))
        tattoos_on = math_node('MAXIMUM', math_node('MAXIMUM', ult, inputs.outputs["Tattoos"]), math_node('MULTIPLY', dismiss_in, 1.5))
        amount = math_node('ADD', amount, math_node('MULTIPLY', math_node('MULTIPLY', separate(tattoo.outputs[0])[0], lit),
                                                    math_node('MULTIPLY', tattoos_on, strength)))
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
    # her outline in the ultimate: cyan at the edge (3P_Vampire_X_FX: BaseHighlightB, Fresnel power 3), magenta just
    # inside it (BaseHighlight)
    rim = nodes.new("ShaderNodeLayerWeight")
    rim.inputs["Blend"].default_value = 0.5
    edge = math_node('POWER', rim.outputs["Facing"], 6.0)
    inner = math_node('SUBTRACT', math_node('POWER', rim.outputs["Facing"], 3.0), edge, clamp=True)
    cyan = nodes.new("ShaderNodeEmission")
    cyan.inputs["Color"].default_value = (*_color(values.get("UltRimColor", "00FFFF")), 1.0)
    links.new(math_node('MULTIPLY', math_node('MULTIPLY', edge, ult), math_node('MULTIPLY', not_hair, 1.5)), cyan.inputs["Strength"])
    magenta = nodes.new("ShaderNodeEmission")
    magenta.inputs["Color"].default_value = (*_color(values.get("UltInnerColor", "CF00FF")), 1.0)
    links.new(math_node('MULTIPLY', math_node('MULTIPLY', inner, ult), math_node('MULTIPLY', not_hair, 0.4)), magenta.inputs["Strength"])
    for glow_shader in (cyan, magenta):
        more = nodes.new("ShaderNodeAddShader")
        links.new(add.outputs[0], more.inputs[0])
        links.new(glow_shader.outputs[0], more.inputs[1])
        add = more
    # her hands glow magenta in her ult and Dismiss (HandBase FF00FF, white at the fingertips: HandHighlight): where her
    # hand and finger bones move her ("vp_hands", made on import from her skin weights)
    hands_attribute = nodes.new("ShaderNodeAttribute")
    hands_attribute.attribute_name = HANDS_ATTRIBUTE
    hands = hands_attribute.outputs["Fac"]
    hands_on = inputs.outputs["Dismiss"]  # (not in her ult)
    hand_glow = nodes.new("ShaderNodeEmission")
    # (magenta; a soft rim lighter at the edges, as the in-game glow blooms)
    hand_rim = nodes.new("ShaderNodeLayerWeight")
    hand_rim.inputs["Blend"].default_value = 0.5
    links.new(mix_color('MIX', _color(values.get("HandColor", "E600FF")), _color(values.get("HandHighlight", "FF40FF")),
                        math_node('POWER', hand_rim.outputs["Facing"], 2.0)), hand_glow.inputs["Color"])
    links.new(math_node('MULTIPLY', math_node('MULTIPLY', hands, hands_on), math_node('MULTIPLY', not_hair, 0.55)),
              hand_glow.inputs["Strength"])
    with_hands = nodes.new("ShaderNodeAddShader")
    links.new(add.outputs[0], with_hands.inputs[0])
    links.new(hand_glow.outputs[0], with_hands.inputs[1])
    add = with_hands
    emission_and_hands = nodes.new("ShaderNodeAddShader")
    links.new(emission.outputs[0], emission_and_hands.inputs[0])
    links.new(hand_glow.outputs[0], emission_and_hands.inputs[1])

    # Dismiss (3P_Vampire_E_FX, translucent): she turns see-through deep purple, her outline glowing magenta (stronger
    # toward the edge: its Fresnel alpha 0.75), energy drifting over her (TextureD: Wraith_4Xtile90 tiled 3x, scrolling)
    dismiss = inputs.outputs["Dismiss"]
    facing = nodes.new("ShaderNodeLayerWeight")
    facing.inputs["Blend"].default_value = 0.5
    rim_amount = facing.outputs["Facing"]
    drift = texture("Energy", (3.0, 3.0), (0.0, -0.1))
    drift_value = separate(drift.outputs[0])[0] if drift is not None else 0.5
    # (in game she reads pale and very see-through: the purple shows mostly at her outline)
    ghost_color = mix_color('MIX', _color(values.get("DismissBaseColor", "2A0E5E")), _color(values.get("DismissRimColor", "E01CFF")),
                            math_node('POWER', rim_amount, 2.0))
    ghost_glow = nodes.new("ShaderNodeEmission")
    links.new(ghost_color, ghost_glow.inputs["Color"])
    links.new(math_node('ADD', math_node('ADD', math_node('MULTIPLY', math_node('POWER', rim_amount, 3.0), 1.5), 0.12),
                        math_node('MULTIPLY', drift_value, 0.15)), ghost_glow.inputs["Strength"])
    see_through = nodes.new("ShaderNodeBsdfTransparent")
    ghost_opacity = math_node('ADD', 0.65, math_node('MULTIPLY', math_node('POWER', rim_amount, 1.5), 0.3), clamp=True)
    ghost = nodes.new("ShaderNodeMixShader")
    links.new(ghost_opacity, ghost.inputs[0])
    links.new(see_through.outputs[0], ghost.inputs[1])
    links.new(ghost_glow.outputs[0], ghost.inputs[2])
    # her eyes and chest keep glowing through it, brighter (her Dismiss lights them: eye, HandBase colours)
    lit = nodes.new("ShaderNodeAddShader")
    links.new(ghost.outputs[0], lit.inputs[0])
    links.new(emission_and_hands.outputs[0], lit.inputs[1])
    ghost = lit.outputs[0]
    dismissed = nodes.new("ShaderNodeMixShader")
    links.new(dismiss, dismissed.inputs[0])
    links.new(add.outputs[0], dismissed.inputs[1])
    links.new(ghost, dismissed.inputs[2])
    links.new(dismissed.outputs[0], output.inputs[0])
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
    layout = ((0, "Hands E"), (1, "Hands abilities"), (2, "Outfit"))
    if mask.image is not None and mask.image.name.lower().startswith("fp_"):
        # his 1st person arms' mask: red his bare hand, green his glove's trim, blue a gradient up his fingers (his
        # E's glow, fading toward the wrist); no outfit lines on them
        layout = ((2, "Hands E"), (1, "Hands abilities"))
    first_person = len(layout) == 2
    for channel, switch in layout:
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
    if first_person:
        # (seen up close: a glow strongest at his hands' outline, not flat paint over them)
        rim = nodes.new("ShaderNodeLayerWeight")
        rim.inputs["Blend"].default_value = 0.5
        edge = nodes.new("ShaderNodeMath")
        edge.operation = 'POWER'
        links.new(rim.outputs["Facing"], edge.inputs[0])
        edge.inputs[1].default_value = 2.0
        light = nodes.new("ShaderNodeMath")
        light.operation = 'MULTIPLY_ADD'
        links.new(edge.outputs[0], light.inputs[0])
        light.inputs[1].default_value = 1.4
        light.inputs[2].default_value = 0.15
        softened = nodes.new("ShaderNodeMath")
        softened.operation = 'MULTIPLY'
        links.new(total, softened.inputs[0])
        links.new(light.outputs[0], softened.inputs[1])
        total = softened.outputs[0]
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
    if mask.image is not None and mask.image.name.lower().startswith("1p_"):
        tattoo = mask.outputs[0]  # (his 1st person arms' mask: the tattoo in its colour, its alpha full)

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
    body = next((o for o in sorted(meshes, key=lambda o: 0 if o.name.upper().startswith("TP") else 1)
                 if o is not form and _armature_of(o) is not None), None)  # (the 3rd person model, not character select)
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
    can tell the two apart (where it is hidden, the outfit under it must show). In 1st person: his right arm's
    symbiote version (see _add_veto_symbiote_arm)."""
    objects = _put_form_on_rig(objects, "pine_sym", "pine", offset=0.001)
    arm = _add_veto_symbiote_arm(objects)
    return objects + [arm] if arm is not None else objects


def _add_veto_symbiote_arm(objects):
    """In 1st person his left arm is his symbiote arm (FP_Pine_S0_Sym_MI); in his ultimate the right one turns into
    it too, but the game has no model for that. Rebuilt: a copy of his right arm wearing the symbiote arm's material,
    its texture taken from the left arm mirrored across him (each face of the right arm reads the left arm's texture
    where its mirror image lands), tagged "(ult form)" so "Ult form" spreads it over him like his 3rd person form."""
    import bmesh
    from mathutils import Vector
    from mathutils.bvhtree import BVHTree
    from mathutils.geometry import barycentric_transform
    arm = next((o for o in objects if o.type == 'MESH' and o.name.upper().startswith("FP_") and
                any(m and "fp_pine_s0_sym" in m.name.lower() for m in o.data.materials)), None)
    rig = _armature_of(arm) if arm is not None else None
    if rig is None or "L_Hand" not in rig.data.bones or "R_Hand" not in rig.data.bones:
        return None
    name = f"VP {arm.name} symbiote arm"
    if bpy.data.objects.get(name) is not None:
        return None
    materials = list(arm.data.materials)
    sym_index = next(i for i, m in enumerate(materials) if m and "fp_pine_s0_sym" in m.name.lower())
    me = arm.data
    if not me.uv_layers:
        return None
    uv_name = me.uv_layers[0].name

    # the mirror across him: from his hands' rest positions, in the mesh's own space
    to_mesh = arm.matrix_world.inverted() @ rig.matrix_world
    left, right = to_mesh @ rig.data.bones["L_Hand"].head_local, to_mesh @ rig.data.bones["R_Hand"].head_local
    axis = max(range(3), key=lambda i: abs(left[i] - right[i]))
    middle = (left[axis] + right[axis]) / 2.0

    def mirror(co):
        co = Vector(co)
        co[axis] = 2.0 * middle - co[axis]
        return co

    # the left (symbiote) arm's triangles and their texture coordinates
    me.calc_loop_triangles()
    uv_data = me.uv_layers[uv_name].data
    tris = [t for t in me.loop_triangles if t.material_index == sym_index]
    if not tris:
        return None
    tree = BVHTree.FromPolygons([v.co for v in me.vertices], [tuple(t.vertices) for t in tris])
    tri_uvs = [[Vector((*uv_data[i].uv, 0.0)) for i in t.loops] for t in tris]

    # his right arm (the faces of his other material on his right side) as its own object
    groups = {g.index: g.name for g in arm.vertex_groups}
    data = me.copy()
    data.name = name
    data.use_auto_texspace = False  # (same "where on him" as the arms: the spread lines up)
    data.texspace_location, data.texspace_size = me.texspace_location[:], me.texspace_size[:]
    bm = bmesh.new()
    bm.from_mesh(data)
    deform = bm.verts.layers.deform.active
    uv_layer = bm.loops.layers.uv[uv_name]

    def on_right(face):
        if face.material_index == sym_index or deform is None:
            return False
        sides = [groups.get(max(v[deform].items(), key=lambda x: x[1])[0], "")[:2] for v in face.verts if v[deform]]
        return sides.count("R_") * 2 > len(sides)

    bmesh.ops.delete(bm, geom=[f for f in bm.faces if not on_right(f)], context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    for face in bm.faces:
        found = tree.find_nearest(mirror(face.calc_center_median()))
        if found[2] is None:
            continue
        corners = [me.vertices[i].co for i in tris[found[2]].vertices]
        for loop in face.loops:
            uv = barycentric_transform(mirror(loop.vert.co), *corners, *tri_uvs[found[2]])
            loop[uv_layer].uv = (uv[0], uv[1])
        face.material_index = 0
    bm.to_mesh(data)
    bm.free()
    if not data.polygons:
        bpy.data.meshes.remove(data)
        return None
    data.materials.clear()
    material = materials[sym_index].copy()
    material.name = f"Pine symbiote right arm{ULT_FORM_TAG}"
    data.materials.append(material)
    copy = bpy.data.objects.new(name, data)
    copy.parent = arm.parent
    copy.matrix_world = arm.matrix_world.copy()
    for group in arm.vertex_groups:
        copy.vertex_groups.new(name=group.name)
    for modifier in arm.modifiers:
        if modifier.type == 'ARMATURE':
            new = copy.modifiers.new(modifier.name, 'ARMATURE')
            new.object = modifier.object
    displace = copy.modifiers.new("VP Ult Form Offset", 'DISPLACE')
    displace.direction = 'NORMAL'
    displace.mid_level = 0.0
    displace.strength = 0.001
    for collection in arm.users_collection:
        collection.objects.link(copy)
    return copy


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
    ult_mask_node = texture("UltMask")
    first_person = _image(images, "CMask") is None  # (her 1st person arms: no C mask, no legs)
    if ult_mask_node.image is not None and ult_mask_node.image.name.lower().startswith("fp_"):
        ult_mask = separate(ult_mask_node.outputs[0])[1]  # (her 1st person arms' mask: red the arm, green its glowing spots)
    else:
        ult_mask = separate(ult_mask_node.outputs[0])[0]
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
    if not first_person:
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
    normal_look = result
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
    if first_person:
        # her 1st person arms have no ghost model: they turn into her ghost themselves where it has arrived
        ghost_side = math_node('SUBTRACT', front(inputs.outputs["Ghost"]), field)
        result = mix_shader(math_node('GREATER_THAN', ghost_side, 0.0), normal_look, ghost)
        shown = math_node('GREATER_THAN', respawn_side, 0.0)

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


def _build_omen_group(images, values):
    """Omen: his ultimate (From the Shadows).
    Cast: 0 -> 0.5 he turns to shadow (Omen_Overlay_Ally_MI: Spectre colours, his markings glowing cyan), 0.5 -> 1 he
    dissolves away, the edge glowing cyan. Ghost: 0 -> 1 his ghost (the ethereal form, a second model on his rig;
    Ethereal_Form_ALLY) takes his place, dissolving in; back to 0 he comes back. The ghost: lavender to indigo, a dark
    hood, cyan eyes, its bottom tearing away into shadow, drifting (Wraith_4Xtile90 scrolling down).
    Reveal: 1 on his ghost's material."""
    name = "VP Omen Ult"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    for socket_name, default in (("Cast", 0.0), ("Ghost", 0.0), ("Time", 0.0), ("Reveal", 0.0)):
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

    def texture(role, vector=None, extension='REPEAT'):
        node = nodes.new("ShaderNodeTexImage")
        node.image = _image(images, role)
        node.extension = extension
        links.new(vector if vector is not None else uv.outputs[0], node.inputs[0])
        return node

    def separate(color_socket):
        node = nodes.new("ShaderNodeSeparateColor")
        links.new(color_socket, node.inputs[0])
        return node.outputs

    def mix_color(factor, a, b):
        node = nodes.new("ShaderNodeMix")
        node.data_type = 'RGBA'
        if isinstance(factor, (int, float)):
            node.inputs[0].default_value = factor
        else:
            links.new(factor, node.inputs[0])
        for socket, value in ((node.inputs[6], a), (node.inputs[7], b)):
            if isinstance(value, tuple):
                socket.default_value = (*value, 1.0)
            else:
                links.new(value, socket)
        return node.outputs[2]

    def emission(color, strength):
        node = nodes.new("ShaderNodeEmission")
        if isinstance(color, tuple):
            node.inputs["Color"].default_value = (*color, 1.0)
        else:
            links.new(color, node.inputs["Color"])
        if isinstance(strength, (int, float)):
            node.inputs["Strength"].default_value = strength
        else:
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

    t_cast, ghost, reveal = inputs.outputs["Cast"], inputs.outputs["Ghost"], inputs.outputs["Reveal"]
    rim = nodes.new("ShaderNodeLayerWeight")
    rim.inputs["Blend"].default_value = 0.5
    facing = rim.outputs["Facing"]
    coords = nodes.new("ShaderNodeTexCoord")
    height = separate(coords.outputs["Generated"])[2]  # 0 feet .. 1 head (the model's own box)

    # his cast: shadow (Spectre colours, lighter at his outline), his markings glowing cyan to blue
    spectre_in = math_node('MULTIPLY', t_cast, 2.0, clamp=True)
    shadow = emission(mix_color(math_node('POWER', facing, 1.5), _color(values.get("SpectreColor", "180E20")),
                                _color(values.get("SpectreColorB", "463059"))), 1.0)
    marks = separate(texture("Cracks").outputs["Color"])[0]
    crack_colour = mix_color(math_node('SINE', math_node('MULTIPLY', inputs.outputs["Time"], 3.0)),
                             _color(values.get("CrackColorA", "00FFFF")), _color(values.get("CrackColorB", "2AB3FF")))
    cracks = emission(crack_colour, math_node('MULTIPLY', math_node('MULTIPLY', marks, spectre_in), 8.0))
    body = add(mix_shader(math_node('MULTIPLY', spectre_in, 0.9), inputs.outputs["Shader"], shadow), cracks)

    # his ghost: Specter_Diff's shading in his colours (lavender low on him, indigo higher, a dark hood), darker at
    # its outline, cyan eyes; unlit (shaded softly: Specter_Diff's shading doesn't fit all of this model's parts)
    shade = math_node('SUBTRACT', 1.0, math_node('MULTIPLY', facing, 0.6))
    gradient = math_node('DIVIDE', math_node('SUBTRACT', height, 0.25), 0.55, clamp=True)
    body_colour = mix_color(gradient, _color(values.get("BodyColorLower", "9598BC")), _color(values.get("BodyColorUpper", "4E4D89")))
    hood = math_node('DIVIDE', math_node('SUBTRACT', height, 0.82), 0.05, clamp=True)
    head_colour = mix_color(math_node('DIVIDE', math_node('SUBTRACT', height, 0.85), 0.12, clamp=True),
                            _color(values.get("HeadColorBottom", "30316C")), _color(values.get("HeadColorTop", "151434")))
    colour = mix_color(hood, body_colour, head_colour)
    lit = math_node('MULTIPLY', math_node('ADD', math_node('MULTIPLY', shade, 0.55), 0.15),
                    math_node('SUBTRACT', 1.0, math_node('MULTIPLY', math_node('POWER', facing, 2.0), float(values.get("OutlineDarkness", 0.8)))))
    # its bottom tears away: drifting noise against its height (the game fades it down its UVs, Limbs_Fade2; its
    # arms' UVs run the other way: by height here), dark at the torn edge
    # (on the model's own box: its UVs are cut in small pieces)
    generated = separate(coords.outputs["Generated"])
    drift = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('ADD', generated[0], generated[1]), drift.inputs[0])
    links.new(math_node('ADD', math_node('MULTIPLY', generated[2], 1.5), math_node('MULTIPLY', inputs.outputs["Time"], 0.25)), drift.inputs[1])
    noise = separate(texture("GhostNoise", drift.outputs[0]).outputs["Color"])[0]
    keep = math_node('ADD', math_node('MULTIPLY', height, 1.6), math_node('MULTIPLY', noise, 0.7))
    keep = math_node('MULTIPLY', math_node('SUBTRACT', keep, 0.78), 1.6, clamp=True)
    torn = math_node('SUBTRACT', 1.0, keep)
    colour = mix_color(math_node('POWER', torn, 0.6), colour, _color(values.get("TearawayColor", "090D19")))
    eyes = separate(texture("GhostEyes").outputs["Color"])[0]
    ghost_look = add(emission(colour, lit), emission(_color(values.get("EyeColor", "00FFFF")), math_node('MULTIPLY', eyes, 12.0)))
    ghost_alpha = math_node('MAXIMUM', math_node('MULTIPLY', math_node('MULTIPLY', keep, 2.5, clamp=True), 0.95), math_node('MULTIPLY', eyes, 3.0, clamp=True))
    see_through = nodes.new("ShaderNodeBsdfTransparent")
    ghost_look = mix_shader(ghost_alpha, see_through.outputs[0], ghost_look)
    result = mix_shader(reveal, body, ghost_look)

    # which is shown: he dissolves away in the second half of his cast; "Ghost" swaps him for his ghost (each
    # dissolving through the other); the dissolving edge glows cyan
    dissolve = separate(texture("Dissolve", scrolled((2.0, 2.0), (0.0, 0.05))).outputs["Color"])[0]
    width = 0.04
    gone_cast = math_node('MULTIPLY', math_node('SUBTRACT', math_node('MULTIPLY', t_cast, 2.0), 1.0), 1.0, clamp=True)
    him_out = math_node('MAXIMUM', gone_cast, ghost)  # how far he is gone
    ghost_out = math_node('SUBTRACT', 1.0, ghost)  # how far his ghost is gone
    amount = nodes.new("ShaderNodeMix")
    amount.data_type = 'FLOAT'
    links.new(reveal, amount.inputs[0])
    links.new(him_out, amount.inputs[2])
    links.new(ghost_out, amount.inputs[3])
    out_amount = amount.outputs[0]
    # (shown where the noise is above the threshold; 0 shows all, 1 none)
    side = math_node('SUBTRACT', dissolve, math_node('SUBTRACT', math_node('MULTIPLY', out_amount, 1.0 + 2.0 * width), width))
    shown = math_node('GREATER_THAN', side, 0.0)
    edge = math_node('POWER', math_node('SUBTRACT', 1.0, math_node('DIVIDE', math_node('ABSOLUTE', side), width), clamp=True), 3.0)
    edge = math_node('MULTIPLY', edge, math_node('MULTIPLY', math_node('GREATER_THAN', out_amount, 0.001), math_node('LESS_THAN', out_amount, 0.999)))
    sparks = emission(_color(values.get("CrackColorA", "00FFFF")), math_node('MULTIPLY', edge, 6.0))
    gone = nodes.new("ShaderNodeBsdfTransparent")
    result = mix_shader(shown, gone.outputs[0], add(result, sparks))
    links.new(result, output.inputs[0])
    return group


def _prepare_omen(objects):
    """His ghost (the ethereal form) on his rig."""
    return _put_form_on_rig(objects, "x_new", "wraith")


def _build_yoru_group(images, values):
    """Yoru: his ultimate (Dimensional Drift; mi_ultimate_X_materialOverlay_3P_0/_3). Ult: he turns deep blue (his own
    shading kept as light and dark blues: BaseColor to CoreColor), his orange parts pink, a light blue sheen at his
    outline, cells of light drifting over him (tex_noise_04 tiled 3x3, PanSpeed); his hair's outline white."""
    name = "VP Yoru Ult"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    face.new_socket("Albedo", in_out='INPUT', socket_type='NodeSocketColor').default_value = (0.5, 0.5, 0.5, 1.0)
    for socket_name, default in (("Ult", 0.0), ("Glitch", 0.5), ("Time", 0.0), ("Hair", 0.0), ("Prop", 0.0)):
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

    def mix_color(factor, a, b, blend='MIX'):
        node = nodes.new("ShaderNodeMix")
        node.data_type = 'RGBA'
        node.blend_type = blend
        if isinstance(factor, (int, float)):
            node.inputs[0].default_value = factor
        else:
            links.new(factor, node.inputs[0])
        for socket, value in ((node.inputs[6], a), (node.inputs[7], b)):
            if isinstance(value, tuple):
                socket.default_value = (*value, 1.0)
            else:
                links.new(value, socket)
        return node.outputs[2]

    def emission(color, strength):
        node = nodes.new("ShaderNodeEmission")
        if isinstance(color, tuple):
            node.inputs["Color"].default_value = (*color, 1.0)
        else:
            links.new(color, node.inputs["Color"])
        if isinstance(strength, (int, float)):
            node.inputs["Strength"].default_value = strength
        else:
            links.new(strength, node.inputs["Strength"])
        return node.outputs[0]

    def add(a, b):
        node = nodes.new("ShaderNodeAddShader")
        links.new(a, node.inputs[0])
        links.new(b, node.inputs[1])
        return node.outputs[0]

    channels = nodes.new("ShaderNodeSeparateColor")
    links.new(inputs.outputs["Albedo"], channels.inputs[0])
    r, g, b = channels.outputs[0], channels.outputs[1], channels.outputs[2]
    brightness = math_node('ADD', math_node('ADD', math_node('MULTIPLY', r, 0.3), math_node('MULTIPLY', g, 0.59)), math_node('MULTIPLY', b, 0.11))
    # a hologram of him: his own shading kept (dark folds to bright blue), glossy like chrome, lit by the scene, with a
    # glow of its own; his skin a darker violet, his orange parts pink
    dark = _color(values.get("DarkColor", "02031E"))
    # (the game's colours, BaseColor and the others, read violet-blue with its bloom: shifted toward violet here)
    blue = mix_color(math_node('MULTIPLY', math_node('POWER', brightness, 0.9), 1.6, clamp=True), dark, _color(values.get("HoloColor", "1A10D8")))
    blue = mix_color(math_node('MULTIPLY', math_node('POWER', brightness, 4.0), 0.15, clamp=True), blue, _color(values.get("CoreColor", "71ABFF")))
    skin = math_node('MULTIPLY', math_node('MULTIPLY', math_node('SUBTRACT', r, b), 3.0, clamp=True),
                     math_node('SUBTRACT', 1.0, math_node('MULTIPLY', math_node('SUBTRACT', math_node('SUBTRACT', r, g), 0.3), 4.0, clamp=True)))
    # his skin keeps its own colour under the blue (his face, neck, fingers, wrists: a little bluish)
    skin_colour = mix_color(float(values.get("SkinBlue", 0.3)), inputs.outputs["Albedo"], _color(values.get("HoloColor", "1A10D8")))
    skin_colour = mix_color(0.45, skin_colour, (0.0, 0.0, 0.0), 'MULTIPLY')
    skin_colour = mix_color(0.45, skin_colour, (0.25, 0.12, 0.2))  # (darker and warmer, as in game)
    blue = mix_color(math_node('MULTIPLY', skin, 0.9), blue, skin_colour)
    # his mask (Prop): its own light and dark parts clear, so it reads as a mask: near black to pale blue
    mask_colour = mix_color(math_node('MULTIPLY', math_node('SUBTRACT', math_node('POWER', brightness, 0.8), 0.15), 2.2, clamp=True),
                            (0.002, 0.003, 0.03), _color(values.get("MaskLight", "8FB0FF")))
    blue = mix_color(inputs.outputs["Prop"], blue, mask_colour)
    # (saturated orange only: red well above green)
    orange = math_node('MULTIPLY', math_node('SUBTRACT', math_node('SUBTRACT', r, g), 0.3), 4.0, clamp=True)
    colour = mix_color(orange, blue, _color(values.get("WarmColor", "FF5FA8")))

    rim = nodes.new("ShaderNodeLayerWeight")
    rim.inputs["Blend"].default_value = 0.5
    facing = rim.outputs["Facing"]
    surface = nodes.new("ShaderNodeBsdfPrincipled")
    links.new(colour, surface.inputs["Base Color"])
    surface.inputs["Metallic"].default_value = 0.0
    surface.inputs["Roughness"].default_value = 0.3
    surface.inputs["Specular IOR Level"].default_value = 0.35
    surface.inputs["Coat Weight"].default_value = 0.25  # (glossy highlights, not tinting him)
    surface.inputs["Coat Roughness"].default_value = 0.15
    links.new(colour, surface.inputs["Emission Color"])
    # (his skin and his mask glow less: their own colours show)
    links.new(math_node('MULTIPLY', 1.4, math_node('SUBTRACT', 1.0, math_node('MAXIMUM', math_node('MULTIPLY', skin, 0.85),
                                                                                   math_node('MULTIPLY', inputs.outputs["Prop"], 0.5)))),
              surface.inputs["Emission Strength"])
    # faint scanlines running up him
    coords = nodes.new("ShaderNodeTexCoord")
    height = nodes.new("ShaderNodeSeparateXYZ")
    links.new(coords.outputs["Object"], height.inputs[0])
    lines = math_node('ADD', 0.85, math_node('MULTIPLY', math_node('SINE', math_node('ADD', math_node('MULTIPLY', height.outputs[2], 520.0),
                                                                                       math_node('MULTIPLY', inputs.outputs["Time"], -6.0))), 0.15))
    body = surface.outputs[0]
    scan = emission(_color(values.get("HoloColor", "1A10D8")), math_node('MULTIPLY', math_node('SUBTRACT', lines, 0.7), 0.6))
    body = add(body, scan)
    # a blue haze at his outline (his hair's white)
    sheen_colour = mix_color(inputs.outputs["Hair"], _color(values.get("HoloRimColor", "5A50FF")), (1.0, 1.0, 1.0))
    sheen = emission(sheen_colour, math_node('MULTIPLY', math_node('POWER', facing, 2.5), 1.6))

    tile = float(values.get("CellTile", 3.0))
    uv = nodes.new("ShaderNodeUVMap")
    moved = nodes.new("ShaderNodeVectorMath")
    moved.operation = 'MULTIPLY_ADD'
    links.new(uv.outputs[0], moved.inputs[0])
    moved.inputs[1].default_value = (tile, tile, 1.0)
    shift = nodes.new("ShaderNodeCombineXYZ")
    speed = float(values.get("PanSpeed", 0.15))
    links.new(math_node('MULTIPLY', inputs.outputs["Time"], speed), shift.inputs[0])
    links.new(math_node('MULTIPLY', inputs.outputs["Time"], speed * 0.6), shift.inputs[1])
    links.new(shift.outputs[0], moved.inputs[2])
    cells = nodes.new("ShaderNodeTexImage")
    cells.image = _image(images, "Cells")
    links.new(moved.outputs[0], cells.inputs[0])
    cell_lines = math_node('POWER', math_node('SUBTRACT', math_node('MULTIPLY', cells.outputs["Color"], 1.4), 0.4, clamp=True), 3.0)
    cell_glow = emission(_color(values.get("HoloCellColor", "3048FF")), math_node('MULTIPLY', cell_lines, 0.8))

    # the glitch: blocks of him flicker back to his normal look for a frame or two, a cyan/magenta flash at their edge
    # (Glitch Noise Details); a dozen steps a second
    step = math_node('FLOOR', math_node('MULTIPLY', inputs.outputs["Time"], 12.0))
    blocks = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('FLOOR', math_node('MULTIPLY', height.outputs[2], float(values.get("GlitchBands", 28.0)))), blocks.inputs[0])
    links.new(math_node('FLOOR', math_node('MULTIPLY', math_node('ADD', height.outputs[0], height.outputs[1]), 6.0)), blocks.inputs[1])
    links.new(step, blocks.inputs[2])
    chance = nodes.new("ShaderNodeTexWhiteNoise")
    chance.noise_dimensions = '3D'
    links.new(blocks.outputs[0], chance.inputs["Vector"])
    # ("Glitch": 0 none, 0.5 as in game, 1 twice as much)
    glitch = math_node('GREATER_THAN', chance.outputs["Value"],
                       math_node('SUBTRACT', 1.0, math_node('MULTIPLY', inputs.outputs["Glitch"], 2.0 * float(values.get("GlitchAmount", 0.035)))))
    flash_noise = nodes.new("ShaderNodeTexWhiteNoise")
    flash_noise.noise_dimensions = '4D'
    links.new(blocks.outputs[0], flash_noise.inputs["Vector"])
    links.new(math_node('ADD', step, 0.5), flash_noise.inputs["W"])
    flash = emission(mix_color(math_node('GREATER_THAN', flash_noise.outputs["Value"], 0.5), (0.0, 1.0, 1.0), (1.0, 0.1, 0.8)),
                     math_node('MULTIPLY', math_node('MULTIPLY', math_node('GREATER_THAN', flash_noise.outputs["Value"], 0.85), glitch), 0.8))
    hologram = add(add(add(body, sheen), cell_glow), flash)
    glitched = nodes.new("ShaderNodeMixShader")
    links.new(glitch, glitched.inputs[0])
    links.new(hologram, glitched.inputs[1])
    links.new(add(inputs.outputs["Shader"], flash), glitched.inputs[2])

    ult = inputs.outputs["Ult"]
    mixed = nodes.new("ShaderNodeMixShader")
    links.new(ult, mixed.inputs[0])
    links.new(inputs.outputs["Shader"], mixed.inputs[1])
    links.new(glitched.outputs[0], mixed.inputs[2])
    links.new(mixed.outputs[0], output.inputs[0])
    return group


# props shown only in an ultimate (Yoru's mask): the object and its rig, given their visibility driver after the import
_ult_props = []


def _prepare_yoru(objects):
    """His ult mask: shown while "Ult: active" is up. His models (and the mask) warp like a hologram in his ult."""
    rig = next((_armature_of(o) for o in objects if o.type == 'MESH' and o.name.upper().startswith("TP_") and _armature_of(o)), None)
    for obj in objects:
        if obj.type == 'MESH' and obj.name.upper().startswith("AB_STEALTH_S0_X") and rig is not None:
            _ult_props.append((obj, rig, "Ult: active"))
    for obj in objects:
        if obj.type == 'MESH' and obj.name.upper().startswith(("TP_", "FP_", "AB_STEALTH_S0_X")) and _armature_of(obj) is not None:
            _ult_warps.append((obj, _armature_of(obj), "Ult: active"))
    return objects


# models that warp in an ultimate (Yoru's hologram), given their modifier and drivers after the import
_ult_warps = []

WARP_GROUP = "VP Hologram Warp"
SLICES_PER_M = 140  # the glitch's slices: ~7 mm


def _warp_group(rig, fps):
    """Geometry nodes, after the rig: a glitch warp. In random patches of the model (moving a few times a second),
    thin horizontal slices jump sideways, each its own way, a dozen times a second; now and then a wider band is jolted
    for a frame.
    Its "VP Amount" (0..1, the agent's ult control) and "VP Time" (s) are driven per rig."""
    name = f"{WARP_GROUP} ({rig.name})"
    if bpy.data.node_groups.get(name) is not None:
        return bpy.data.node_groups[name]
    group = bpy.data.node_groups.new(name, 'GeometryNodeTree')
    group.interface.new_socket("Geometry", in_out='INPUT', socket_type='NodeSocketGeometry')
    group.interface.new_socket("Geometry", in_out='OUTPUT', socket_type='NodeSocketGeometry')
    nodes, links = group.nodes, group.links
    inputs, output = nodes.new("NodeGroupInput"), nodes.new("NodeGroupOutput")
    values = {}
    for value_name in ("Amount", "Time"):
        node = nodes.new("ShaderNodeValue")
        node.name = node.label = "VP " + value_name
        values[value_name] = node.outputs[0]

    def math_node(op, a, b=None):
        node = nodes.new("ShaderNodeMath")
        node.operation = op
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                node.inputs[i].default_value = v
            else:
                links.new(v, node.inputs[i])
        return node.outputs[0]

    # (in world space: his body and his mask, attached another way, warp together)
    position = nodes.new("GeometryNodeInputPosition")
    self_object = nodes.new("GeometryNodeSelfObject")
    info = nodes.new("GeometryNodeObjectInfo")
    info.transform_space = 'ORIGINAL'
    links.new(self_object.outputs[0], info.inputs["Object"])
    to_world = nodes.new("FunctionNodeTransformPoint")
    links.new(position.outputs[0], to_world.inputs["Vector"])
    links.new(info.outputs["Transform"], to_world.inputs["Transform"])
    split = nodes.new("ShaderNodeSeparateXYZ")
    links.new(to_world.outputs[0], split.inputs[0])
    height = split.outputs[2]

    step = math_node('FLOOR', math_node('MULTIPLY', values["Time"], 12.0))  # (a dozen glitch steps a second)

    def white(x, y, z, dimensions='3D'):
        vector = nodes.new("ShaderNodeCombineXYZ")
        for i, v in enumerate((x, y, z)):
            if isinstance(v, (int, float)):
                vector.inputs[i].default_value = v
            else:
                links.new(v, vector.inputs[i])
        node = nodes.new("ShaderNodeTexWhiteNoise")
        node.noise_dimensions = dimensions
        links.new(vector.outputs[0], node.inputs["Vector"])
        return node.outputs["Value"]

    # thin slices (~7 mm), each jumping its own way, again every glitch step
    slice_index = math_node('FLOOR', math_node('MULTIPLY', height, float(SLICES_PER_M)))
    slice_x = math_node('SUBTRACT', white(slice_index, step, 0.0), 0.5)
    slice_y = math_node('SUBTRACT', white(slice_index, step, 3.7), 0.5)
    # only in patches of him: soft blobs of noise over his body, somewhere else a few times a second
    patches = nodes.new("ShaderNodeTexNoise")
    patches.noise_dimensions = '4D'
    patches.inputs["Scale"].default_value = 3.0
    patches.inputs["Detail"].default_value = 0.0
    links.new(to_world.outputs[0], patches.inputs["Vector"])
    links.new(math_node('MULTIPLY', math_node('FLOOR', math_node('MULTIPLY', values["Time"], 4.0)), 7.13), patches.inputs["W"])
    patch = math_node('MULTIPLY', math_node('SUBTRACT', patches.outputs["Fac"], 0.56), 10.0)
    patch = math_node('MINIMUM', math_node('MAXIMUM', patch, 0.0), 1.0)
    # and now and then a wider band jolted for a frame
    band = math_node('FLOOR', math_node('MULTIPLY', height, 14.0))
    jolt_noise = white(band, step, 9.1)
    jolt = math_node('MULTIPLY', math_node('GREATER_THAN', jolt_noise, 0.985),
                     math_node('SUBTRACT', math_node('MULTIPLY', math_node('FRACT', math_node('MULTIPLY', jolt_noise, 97.0)), 2.0), 1.0))
    slice_size, jolt_size = 0.04, 0.05  # m
    shift = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('ADD', math_node('MULTIPLY', math_node('MULTIPLY', slice_x, patch), slice_size), math_node('MULTIPLY', jolt, jolt_size)), shift.inputs[0])
    links.new(math_node('MULTIPLY', math_node('MULTIPLY', slice_y, patch), slice_size), shift.inputs[1])
    scaled = nodes.new("ShaderNodeVectorMath")
    scaled.operation = 'SCALE'
    links.new(shift.outputs[0], scaled.inputs[0])
    links.new(values["Amount"], scaled.inputs["Scale"])
    moved = nodes.new("ShaderNodeVectorMath")
    links.new(to_world.outputs[0], moved.inputs[0])
    links.new(scaled.outputs[0], moved.inputs[1])
    back = nodes.new("FunctionNodeInvertMatrix")
    links.new(info.outputs["Transform"], back.inputs[0])
    to_local = nodes.new("FunctionNodeTransformPoint")
    links.new(moved.outputs[0], to_local.inputs["Vector"])
    links.new(back.outputs[0], to_local.inputs["Transform"])
    move = nodes.new("GeometryNodeSetPosition")
    links.new(inputs.outputs["Geometry"], move.inputs["Geometry"])
    links.new(to_local.outputs[0], move.inputs["Position"])
    links.new(move.outputs[0], output.inputs[0])

    # (Amount: the ult control times the glitch slider, 0.5 = as made here)
    for value_name, expression, props in (("Amount", "value * glitch * 2", (("value", "Ult: active"), ("glitch", "Ult: glitch"))),
                                          ("Time", f"frame / {fps:g}", ())):
        curve = group.driver_add(f'nodes["VP {value_name}"].outputs[0].default_value')
        driver = curve.driver
        driver.type = 'SCRIPTED'
        for var_name, prop in props:
            var = driver.variables.new()
            var.name = var_name
            var.type = 'SINGLE_PROP'
            var.targets[0].id_type = 'OBJECT'
            var.targets[0].id = rig
            var.targets[0].data_path = f'["{prop}"]'
        driver.expression = expression
    return group


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
    if mask.image is None:
        # her 1st person arms (OverlayMaterial_FP_Wushu_S0: no mask): their second UV map is each arm unwrapped around
        # (u) and along it, wrist to shoulder (v); their third keeps the hands apart (below 0.25), left without wind
        arm_uv = nodes.new("ShaderNodeUVMap")
        arm_uv.uv_map = "EXTRAUVS0"
        arm_split = nodes.new("ShaderNodeSeparateXYZ")
        links.new(arm_uv.outputs[0], arm_split.inputs[0])
        around, along = arm_split.outputs[0], arm_split.outputs[1]
        hand_uv = nodes.new("ShaderNodeUVMap")
        hand_uv.uv_map = "EXTRAUVS1"
        hand_split = nodes.new("ShaderNodeSeparateXYZ")
        links.new(hand_uv.outputs[0], hand_split.inputs[0])
        arm = math_node('DIVIDE', math_node('SUBTRACT', hand_split.outputs[1], 0.235), 0.04, clamp=True)

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
    if masks.image is None:
        where, honeycomb = 1.0, 0.0  # (his 1st person arms: no mask, the flames all over them)

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


def _build_kayo_group(images, values):
    """KAY/O: his teal panels glowing (where his colour texture is teal, green or magenta), in their own colour at
    idle; each ability's overlay recolours them (m_overlay_3P_01: ColorStable): flash lime, frag blue, knife purple
    with a purple outline; his ultimate violet, flickering with scanlines (tex_pixels_01), a violet outline."""
    name = "VP KAYO Glow"
    old = bpy.data.node_groups.get(name)
    if old is not None and old.users == 0:
        bpy.data.node_groups.remove(old)
    group = bpy.data.node_groups.new(name, 'ShaderNodeTree')
    face = group.interface
    face.new_socket("Shader", in_out='INPUT', socket_type='NodeSocketShader')
    face.new_socket("Albedo", in_out='INPUT', socket_type='NodeSocketColor').default_value = (0.0, 0.0, 0.0, 1.0)
    for socket_name, default in (("Idle", 1.0), ("Flash", 0.0), ("Frag", 0.0), ("Knife", 0.0), ("Ult", 0.0), ("Time", 0.0)):
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

    def mix_color(factor, a, b):
        node = nodes.new("ShaderNodeMix")
        node.data_type = 'RGBA'
        if isinstance(factor, (int, float)):
            node.inputs[0].default_value = factor
        else:
            links.new(factor, node.inputs[0])
        for socket, value in ((node.inputs[6], a), (node.inputs[7], b)):
            if isinstance(value, tuple):
                socket.default_value = (*value, 1.0)
            else:
                links.new(value, socket)
        return node.outputs[2]

    channels = nodes.new("ShaderNodeSeparateColor")
    links.new(inputs.outputs["Albedo"], channels.inputs[0])
    r, g, b = channels.outputs[0], channels.outputs[1], channels.outputs[2]
    # where he glows: clearly teal/green (green above red), or magenta (red and blue above green: his chest core)
    teal = math_node('SUBTRACT', math_node('MULTIPLY', math_node('SUBTRACT', g, r), 4.0), 0.4, clamp=True)
    magenta = math_node('SUBTRACT', math_node('MULTIPLY', math_node('SUBTRACT', math_node('MINIMUM', r, b), g), 4.0), 0.4, clamp=True)
    panels = math_node('MAXIMUM', teal, magenta)

    # its colour: his own (brightened to full) at idle, each ability's over it
    brightest = math_node('MAXIMUM', math_node('MAXIMUM', r, g), math_node('MAXIMUM', b, 0.05))
    own = nodes.new("ShaderNodeVectorMath")
    own.operation = 'DIVIDE'
    links.new(inputs.outputs["Albedo"], own.inputs[0])
    combine = nodes.new("ShaderNodeCombineXYZ")
    for i in range(3):
        links.new(brightest, combine.inputs[i])
    links.new(combine.outputs[0], own.inputs[1])
    # (his panels' own colour, saturated: the game's glow is a stronger teal than the texture)
    colour = mix_color(0.6, own.outputs[0], _color(values.get("IdleColor", "00E8C6")))
    for socket, key, fallback in (("Flash", "FlashColor", "8CE81A"), ("Frag", "FragColor", "2841FF"),
                                  ("Knife", "KnifeColor", "9550FF"), ("Ult", "UltColor", "9570FF")):
        colour = mix_color(inputs.outputs[socket], colour, _color(values.get(key, fallback)))
    ability = math_node('MAXIMUM', math_node('MAXIMUM', inputs.outputs["Flash"], inputs.outputs["Frag"]),
                        math_node('MAXIMUM', inputs.outputs["Knife"], inputs.outputs["Ult"]))
    # brighter in an ability; the core of the glow whiter
    strength = math_node('ADD', math_node('MULTIPLY', inputs.outputs["Idle"], float(values.get("IdleStrength", 1.4))),
                         math_node('MULTIPLY', ability, float(values.get("AbilityStrength", 2.4))))

    # the ultimate flickers: scanlines running up him, and the whole glow stuttering
    t = inputs.outputs["Time"]
    pixels = _image(images, "Pixels")
    flicker = math_node('ADD', 0.8, math_node('MULTIPLY', math_node('SINE', math_node('MULTIPLY', t, 23.0)),
                                              math_node('MULTIPLY', math_node('SINE', math_node('MULTIPLY', t, 7.0)), 0.25)))
    if pixels is not None:
        uv = nodes.new("ShaderNodeUVMap")
        moved = nodes.new("ShaderNodeVectorMath")
        moved.operation = 'MULTIPLY_ADD'
        links.new(uv.outputs[0], moved.inputs[0])
        moved.inputs[1].default_value = (6.0, 3.0, 1.0)
        shift = nodes.new("ShaderNodeCombineXYZ")
        links.new(math_node('MULTIPLY', t, -0.6), shift.inputs[1])
        links.new(shift.outputs[0], moved.inputs[2])
        lines = nodes.new("ShaderNodeTexImage")
        lines.image = pixels
        links.new(moved.outputs[0], lines.inputs[0])
        flicker = math_node('ADD', flicker, math_node('MULTIPLY', lines.outputs["Color"], 0.8))
    strength = math_node('MULTIPLY', strength, math_node('ADD', math_node('MULTIPLY', inputs.outputs["Ult"], math_node('SUBTRACT', flicker, 1.0)), 1.0))
    glow = nodes.new("ShaderNodeEmission")
    links.new(colour, glow.inputs["Color"])
    links.new(math_node('MULTIPLY', panels, strength), glow.inputs["Strength"])

    # the knife's and the ultimate's outline (ColorFresnel / StableUnstable)
    facing = nodes.new("ShaderNodeLayerWeight")
    facing.inputs["Blend"].default_value = 0.45
    edge = math_node('POWER', facing.outputs["Facing"], 4.0)
    rim_colour = mix_color(inputs.outputs["Ult"], _color(values.get("KnifeFresnel", "9500EE")), _color(values.get("UltColor", "9570FF")))
    rim = nodes.new("ShaderNodeEmission")
    links.new(rim_colour, rim.inputs["Color"])
    links.new(math_node('MULTIPLY', edge, math_node('MAXIMUM', math_node('MULTIPLY', inputs.outputs["Knife"], 0.4),
                                                    math_node('MULTIPLY', inputs.outputs["Ult"], 0.7))), rim.inputs["Strength"])

    result = inputs.outputs["Shader"]
    for shader in (glow.outputs[0], rim.outputs[0]):
        add = nodes.new("ShaderNodeAddShader")
        links.new(result, add.inputs[0])
        links.new(shader, add.inputs[1])
        result = add.outputs[0]
    links.new(result, output.inputs[0])
    return group


MASK_CONTROL = "Ult: mask"


def _setup_viper_mask(values, objects):
    """Viper's ult mask: built into her model, closed over her head by the game's MaskOn animation. Its mask pieces'
    motion (only those) is kept in an action and played by "Ult: mask" (0 her mouth mask, 1 the full mask closed),
    with Action constraints: on top of whatever animation she has."""
    from .valorant_psk_psa_b5 import psaimport
    path = values.get("MaskAnimation")
    done = _setup_viper_first_person(values, objects)
    if not path:
        return done
    for rig in {_armature_of(o) for o in objects if o.type == 'MESH' and o.name.upper().startswith("TP_")} - {None}:
        if MASK_CONTROL in rig:
            continue
        previous = rig.animation_data.action if rig.animation_data else None
        scene = bpy.context.scene
        frame = scene.frame_current
        try:
            psaimport(path, context=bpy.context, oArmature=rig, bKeepProportions=True, bRealTime=True,
                      bUpdateTimelineRange=False, error_callback=print)
        except Exception as error:
            print(f"VP: Viper's mask animation not loaded: {error}")
            continue
        action = rig.animation_data.action if rig.animation_data else None
        rig.animation_data.action = previous
        if previous is None:
            # (the pose it left behind: back to her rest pose)
            for bone in rig.pose.bones:
                bone.location = (0.0, 0.0, 0.0)
                bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
                bone.rotation_euler = (0.0, 0.0, 0.0)
                bone.scale = (1.0, 1.0, 1.0)
        scene.frame_set(frame)
        if action is None or action == previous:
            continue
        action.name = "VP Viper mask"
        action.use_fake_user = True
        start, end = action.frame_range
        # the bones it moves: her mask's pieces
        moving = set()
        for curve in _action_curves(action, rig):
            bone = curve.data_path.split('"')[1] if curve.data_path.startswith("pose.bones") else None
            if bone is None or not ("mask" in bone.lower() or bone.lower() == "hair"):  # (her hair tucks in under it)
                continue
            values_ = [k.co[1] for k in curve.keyframe_points]
            if values_ and max(values_) - min(values_) > 1e-4:
                moving.add(bone)
        rig[MASK_CONTROL] = 0.0
        rig.id_properties_ui(MASK_CONTROL).update(min=0.0, max=1.0, soft_min=0.0, soft_max=1.0,
                                                  description="Viper's ultimate: 0 her mouth mask, 1 her full mask closed over her head (keyframe it for her putting it on)")
        for name in sorted(moving):
            bone = rig.pose.bones.get(name)
            if bone is None:
                continue
            constraint = bone.constraints.new('ACTION')
            constraint.name = "VP Viper mask"
            constraint.action = action
            if hasattr(constraint, "action_slot") and getattr(action, "slots", None):
                constraint.action_slot = action.slots[0]
            constraint.use_eval_time = True
            constraint.frame_start, constraint.frame_end = int(start), int(end)
            constraint.mix_mode = 'BEFORE_FULL'
            curve = constraint.driver_add("eval_time")
            driver = curve.driver
            driver.type = 'SCRIPTED'
            var = driver.variables.new()
            var.name = "value"
            var.type = 'SINGLE_PROP'
            var.targets[0].id_type = 'OBJECT'
            var.targets[0].id = rig
            var.targets[0].data_path = f'["{MASK_CONTROL}"]'
            driver.expression = "value"
            _mask_drivers.append((rig, constraint))
            done += 1
        for hand in ("L_Hand", "R_Hand"):
            if hand in rig.data.bones:
                _add_viper_gas(rig, hand, objects)
        rig.update_tag()
    if done:
        bpy.app.timers.register(_validate_mask_drivers, first_interval=0.05)
    return done


GAS_CONTROL = "FX: hand smoke"


def _gas_material():
    """Viper's gas: soft lime puffs, wispy (noise eaten), glowing, fading in and out over each puff's life."""
    name = "VP Viper gas"
    if bpy.data.materials.get(name) is not None:
        return bpy.data.materials[name]
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    if hasattr(material, "surface_render_method"):
        material.surface_render_method = 'BLENDED'
    tree = material.node_tree
    nodes, links = tree.nodes, tree.links
    for node in list(nodes):
        nodes.remove(node)
    output = nodes.new("ShaderNodeOutputMaterial")

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

    def instancer(attribute):
        node = nodes.new("ShaderNodeAttribute")
        node.attribute_type = 'INSTANCER'
        node.attribute_name = attribute
        return node.outputs["Fac"]

    uv = nodes.new("ShaderNodeAttribute")
    uv.attribute_type = 'GEOMETRY'
    uv.attribute_name = "vp_uv"
    life, seed, amount = instancer("vp_life"), instancer("vp_seed"), instancer("vp_amount")
    centre = nodes.new("ShaderNodeVectorMath")
    centre.operation = 'DISTANCE'
    links.new(uv.outputs["Vector"], centre.inputs[0])
    centre.inputs[1].default_value = (0.5, 0.5, 0.0)
    puff = math_node('POWER', math_node('SUBTRACT', 1.0, math_node('MULTIPLY', centre.outputs["Value"], 2.0), clamp=True), 1.6)
    # wisps: noise drifting through the puff (each its own), eating it away more as it ages
    where = nodes.new("ShaderNodeCombineXYZ")
    split = nodes.new("ShaderNodeSeparateXYZ")
    links.new(uv.outputs["Vector"], split.inputs[0])
    links.new(math_node('ADD', math_node('MULTIPLY', split.outputs[0], 2.5), math_node('MULTIPLY', seed, 17.0)), where.inputs[0])
    links.new(math_node('ADD', math_node('MULTIPLY', split.outputs[1], 2.5), math_node('MULTIPLY', life, -1.2)), where.inputs[1])
    links.new(seed, where.inputs[2])
    noise = nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 1.6
    noise.inputs["Detail"].default_value = 4.0
    noise.inputs["Roughness"].default_value = 0.6
    links.new(where.outputs[0], noise.inputs["Vector"])
    wisp = math_node('MULTIPLY', math_node('SUBTRACT', noise.outputs["Fac"], math_node('ADD', 0.32, math_node('MULTIPLY', life, 0.18))), 3.0, clamp=True)
    fade = math_node('MULTIPLY', math_node('MINIMUM', math_node('MULTIPLY', life, 6.0), 1.0), math_node('SUBTRACT', 1.0, life))
    alpha = math_node('MULTIPLY', math_node('MULTIPLY', math_node('MULTIPLY', puff, wisp), fade), amount)
    alpha = math_node('MULTIPLY', alpha, 1.3, clamp=True)
    # colour: hot lime-white near the hand, deeper green as it rises and thins
    colour = nodes.new("ShaderNodeMix")
    colour.data_type = 'RGBA'
    links.new(math_node('POWER', life, 0.7), colour.inputs[0])
    colour.inputs[6].default_value = (0.85, 1.0, 0.35, 1.0)
    colour.inputs[7].default_value = (0.25, 1.0, 0.05, 1.0)
    glow = nodes.new("ShaderNodeEmission")
    links.new(colour.outputs[2], glow.inputs["Color"])
    links.new(math_node('MULTIPLY', alpha, 9.0), glow.inputs["Strength"])
    see_through = nodes.new("ShaderNodeBsdfTransparent")
    mixed = nodes.new("ShaderNodeMixShader")
    links.new(alpha, mixed.inputs[0])
    links.new(see_through.outputs[0], mixed.inputs[1])
    links.new(glow.outputs[0], mixed.inputs[2])
    links.new(mixed.outputs[0], output.inputs["Surface"])
    return material


def _gas_volume_material(rig, fps):
    """Viper's gas as a volume: dense lime fog at the core, thinning into wisps (animated noise eats its edges),
    glowing a little, drifting with the timeline; "FX: hand smoke" thickens it."""
    name = f"VP Viper gas volume ({rig.name})"
    if bpy.data.materials.get(name) is not None:
        return bpy.data.materials[name]
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    tree = material.node_tree
    nodes, links = tree.nodes, tree.links
    for node in list(nodes):
        nodes.remove(node)
    output = nodes.new("ShaderNodeOutputMaterial")

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

    amount = nodes.new("ShaderNodeValue")
    amount.name = amount.label = "VP Amount"
    clock = nodes.new("ShaderNodeValue")
    clock.name = clock.label = "VP Clock"
    density_in = nodes.new("ShaderNodeAttribute")
    density_in.attribute_type = 'GEOMETRY'
    density_in.attribute_name = "density"
    coords = nodes.new("ShaderNodeTexCoord")
    drift = nodes.new("ShaderNodeVectorMath")
    drift.operation = 'ADD'
    links.new(coords.outputs["Object"], drift.inputs[0])
    shift = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('MULTIPLY', clock.outputs[0], 0.05), shift.inputs[0])
    links.new(math_node('MULTIPLY', clock.outputs[0], 0.25), shift.inputs[2])  # (wisps sinking with it)
    links.new(shift.outputs[0], drift.inputs[1])
    noise = nodes.new("ShaderNodeTexNoise")
    noise.noise_dimensions = '4D'
    noise.inputs["Scale"].default_value = 22.0
    noise.inputs["Detail"].default_value = 6.0
    noise.inputs["Roughness"].default_value = 0.55
    links.new(drift.outputs[0], noise.inputs["Vector"])
    links.new(math_node('MULTIPLY', clock.outputs[0], 0.6), noise.inputs["W"])
    wisps = math_node('POWER', math_node('MULTIPLY', math_node('SUBTRACT', noise.outputs["Fac"], 0.38), 3.0, clamp=True), 1.6)
    # (the grid's own density is applied by the volume shader: its "density" attribute)
    density = math_node('MULTIPLY', math_node('MULTIPLY', wisps, density_in.outputs["Fac"]), math_node('MULTIPLY', amount.outputs[0], 40.0))
    volume = nodes.new("ShaderNodeVolumePrincipled")
    volume.inputs["Color"].default_value = (0.55, 1.0, 0.2, 1.0)
    volume.inputs["Absorption Color"].default_value = (0.75, 1.0, 0.5, 1.0)
    links.new(density, volume.inputs["Density"])
    volume.inputs["Density Attribute"].default_value = ""  # (applied above)
    volume.inputs["Emission Color"].default_value = (0.5, 1.0, 0.08, 1.0)
    # its wisps glow (brightest where densest): the game's lime fog lit from within
    links.new(math_node('MULTIPLY', math_node('POWER', density, 1.3), 0.06), volume.inputs["Emission Strength"])
    links.new(volume.outputs[0], output.inputs["Volume"])
    for node_name, expression, prop in (("VP Amount", "value", GAS_CONTROL), ("VP Clock", f"frame / {fps:g}", None)):
        curve = tree.driver_add(f'nodes["{node_name}"].outputs[0].default_value')
        driver = curve.driver
        driver.type = 'SCRIPTED'
        if prop:
            var = driver.variables.new()
            var.name = "value"
            var.type = 'SINGLE_PROP'
            var.targets[0].id_type = 'OBJECT'
            var.targets[0].id = rig
            var.targets[0].data_path = f'["{prop}"]'
        driver.expression = expression
    return material


def _gas_group(rig, fps, body=None):
    """Geometry nodes: a plume of gas puffs from this object (on her hand): each puff born at the hand, rising and
    curling in world space, growing, over its own ~1 s life, again and again with the timeline; facing the camera.
    "VP Amount" (her "FX: hand smoke") thins and fades it; "VP Clock" is the timeline's seconds."""
    name = f"VP Viper gas ({rig.name})"
    if bpy.data.node_groups.get(name) is not None:
        return bpy.data.node_groups[name]
    group = bpy.data.node_groups.new(name, 'GeometryNodeTree')
    group.interface.new_socket("Geometry", in_out='INPUT', socket_type='NodeSocketGeometry')
    group.interface.new_socket("Geometry", in_out='OUTPUT', socket_type='NodeSocketGeometry')
    nodes, links = group.nodes, group.links
    nodes.new("NodeGroupInput")
    output = nodes.new("NodeGroupOutput")
    values = {}
    for value_name in ("Amount", "Clock"):
        node = nodes.new("ShaderNodeValue")
        node.name = node.label = "VP " + value_name
        values[value_name] = node.outputs[0]

    def math_node(op, a, b=None):
        node = nodes.new("ShaderNodeMath")
        node.operation = op
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                node.inputs[i].default_value = v
            else:
                links.new(v, node.inputs[i])
        return node.outputs[0]

    count = 220
    points = nodes.new("GeometryNodePoints")
    points.inputs["Count"].default_value = count
    index = nodes.new("GeometryNodeInputIndex")

    def random(seed, low=0.0, high=1.0):
        node = nodes.new("FunctionNodeRandomValue")
        node.data_type = 'FLOAT'
        links.new(index.outputs[0], node.inputs["ID"])
        node.inputs["Seed"].default_value = seed
        live = [x for x in node.inputs if x.enabled and x.type == 'VALUE' and x.name in ("Min", "Max")]
        live[0].default_value, live[1].default_value = low, high
        return [x for x in node.outputs if x.enabled and x.type == 'VALUE'][0]

    life = math_node('FRACT', math_node('ADD', math_node('MULTIPLY', values["Clock"], random(1, 0.75, 1.25)), random(2)))
    # where: world space (rising straight up whatever her hand does), then into this object's space
    self_object = nodes.new("GeometryNodeSelfObject")
    info = nodes.new("GeometryNodeObjectInfo")
    info.transform_space = 'ORIGINAL'
    links.new(self_object.outputs[0], info.inputs["Object"])
    curl = nodes.new("ShaderNodeTexNoise")
    curl.noise_dimensions = '4D'
    curl.inputs["Scale"].default_value = 1.0
    curl.inputs["Detail"].default_value = 1.0
    seed_vector = nodes.new("ShaderNodeCombineXYZ")
    links.new(random(3, 0.0, 50.0), seed_vector.inputs[0])
    links.new(math_node('MULTIPLY', life, 1.5), seed_vector.inputs[1])
    links.new(seed_vector.outputs[0], curl.inputs["Vector"])
    links.new(math_node('MULTIPLY', values["Clock"], 0.4), curl.inputs["W"])
    curl_split = nodes.new("FunctionNodeSeparateColor")
    links.new(curl.outputs["Color"], curl_split.inputs[0])
    spread = math_node('ADD', 0.012, math_node('MULTIPLY', life, 0.13))
    offset = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('MULTIPLY', math_node('SUBTRACT', curl_split.outputs[0], 0.5), math_node('MULTIPLY', spread, 2.0)), offset.inputs[0])
    links.new(math_node('MULTIPLY', math_node('SUBTRACT', curl_split.outputs[1], 0.5), math_node('MULTIPLY', spread, 2.0)), offset.inputs[1])
    # heavy gas: ejected downward from the valve (slowed by the air: 1 - e^-6u), then sinking under its own weight
    # (u^2), spreading as it falls
    eject = math_node('MULTIPLY', math_node('SUBTRACT', 1.0, math_node('EXPONENT', math_node('MULTIPLY', life, -4.0))), random(10, -0.1, -0.05))
    lift = math_node('MULTIPLY', math_node('MULTIPLY', life, life), random(4, -0.45, -0.28))
    links.new(math_node('ADD', eject, lift), offset.inputs[2])
    world = nodes.new("ShaderNodeVectorMath")
    links.new(info.outputs["Location"], world.inputs[0])
    links.new(offset.outputs[0], world.inputs[1])
    back = nodes.new("FunctionNodeInvertMatrix")
    links.new(info.outputs["Transform"], back.inputs[0])
    local = nodes.new("FunctionNodeTransformPoint")
    links.new(world.outputs[0], local.inputs["Vector"])
    links.new(back.outputs[0], local.inputs["Transform"])
    size = math_node('MULTIPLY', math_node('ADD', 0.07, math_node('MULTIPLY', life, 0.13)), random(9, 0.7, 1.3))
    where = local.outputs[0]
    if body is not None:
        # it can't go through her: a puff inside her or touching her is pushed out along her surface (her body as
        # posed, in this object's space)
        body_info = nodes.new("GeometryNodeObjectInfo")
        body_info.name = "VP Body"
        body_info.transform_space = 'RELATIVE'
        body_info.inputs["Object"].default_value = body
        nearest = nodes.new("GeometryNodeProximity")
        nearest.target_element = 'FACES'
        links.new(body_info.outputs["Geometry"], nearest.inputs[0])
        links.new(where, nearest.inputs["Source Position"])
        normal = nodes.new("GeometryNodeInputNormal")
        surface_normal = nodes.new("GeometryNodeSampleNearestSurface")
        surface_normal.data_type = 'FLOAT_VECTOR'
        links.new(body_info.outputs["Geometry"], surface_normal.inputs["Mesh"])
        links.new(normal.outputs[0], surface_normal.inputs["Value"])
        links.new(where, surface_normal.inputs["Sample Position"])
        away = nodes.new("ShaderNodeVectorMath")
        away.operation = 'SUBTRACT'
        links.new(where, away.inputs[0])
        links.new(nearest.outputs["Position"], away.inputs[1])
        side = nodes.new("ShaderNodeVectorMath")
        side.operation = 'DOT_PRODUCT'
        links.new(away.outputs[0], side.inputs[0])
        links.new(surface_normal.outputs["Value"], side.inputs[1])
        margin = math_node('MULTIPLY', size, 0.35)
        # (only near her: within a few cm of her surface, and never more than a few cm)
        near = math_node('LESS_THAN', nearest.outputs["Distance"], 0.06)
        push = math_node('MULTIPLY', math_node('MINIMUM', math_node('MAXIMUM', math_node('SUBTRACT', margin, side.outputs["Value"]), 0.0), 0.06), near)
        pushed = nodes.new("ShaderNodeVectorMath")
        pushed.operation = 'MULTIPLY_ADD'
        links.new(surface_normal.outputs["Value"], pushed.inputs[0])
        combine = nodes.new("ShaderNodeCombineXYZ")
        for i in range(3):
            links.new(push, combine.inputs[i])
        links.new(combine.outputs[0], pushed.inputs[1])
        links.new(where, pushed.inputs[2])
        where = pushed.outputs[0]
    placed = nodes.new("GeometryNodeSetPosition")
    links.new(points.outputs[0], placed.inputs["Geometry"])
    links.new(where, placed.inputs["Position"])
    # thinner at lower amounts: a share of the puffs only
    keep = nodes.new("GeometryNodeDeleteGeometry")
    keep.domain = 'POINT'
    links.new(placed.outputs[0], keep.inputs["Geometry"])
    links.new(math_node('GREATER_THAN', random(5), math_node('ADD', math_node('MULTIPLY', values["Amount"], 0.7), 0.3)), keep.inputs["Selection"])
    stored = keep.outputs[0]
    for attribute, value in (("vp_life", life), ("vp_seed", random(6)), ("vp_amount", values["Amount"])):
        store = nodes.new("GeometryNodeStoreNamedAttribute")
        store.data_type = 'FLOAT'
        store.domain = 'POINT'
        store.inputs["Name"].default_value = attribute
        links.new(stored, store.inputs["Geometry"])
        links.new(value, store.inputs["Value"])
        stored = store.outputs[0]
    # a real volume: each puff a soft ball of gas (growing as it rises, shrinking away at the end of its life), the
    # balls merging into one cloud; its wisps come from its material
    envelope = math_node('MULTIPLY', math_node('MINIMUM', math_node('ADD', math_node('MULTIPLY', life, 12.0), 0.35), 1.0),
                         math_node('POWER', math_node('SUBTRACT', 1.0, life), 0.6))
    radius = math_node('MULTIPLY', math_node('MULTIPLY', size, 0.55), envelope)
    store_radius = nodes.new("GeometryNodeSetPointRadius")
    links.new(stored, store_radius.inputs["Points"])
    links.new(radius, store_radius.inputs["Radius"])
    volume = nodes.new("GeometryNodePointsToVolume")
    links.new(store_radius.outputs[0], volume.inputs["Points"])
    volume.inputs["Density"].default_value = 1.0
    links.new(radius, volume.inputs["Radius"])
    if "Resolution Mode" in [i.name for i in volume.inputs]:
        pass
    try:
        volume.resolution_mode = 'VOXEL_SIZE'
    except Exception:
        pass
    if "Voxel Size" in volume.inputs:
        volume.inputs["Voxel Size"].default_value = 0.006
    painted = nodes.new("GeometryNodeSetMaterial")
    links.new(volume.outputs[0], painted.inputs["Geometry"])
    painted.inputs["Material"].default_value = _gas_volume_material(rig, fps)
    links.new(painted.outputs[0], output.inputs[0])
    for value_name, expression, prop in (("Amount", "value", GAS_CONTROL), ("Clock", f"frame / {fps:g}", None)):
        curve = group.driver_add(f'nodes["VP {value_name}"].outputs[0].default_value')
        driver = curve.driver
        driver.type = 'SCRIPTED'
        if prop:
            var = driver.variables.new()
            var.name = "value"
            var.type = 'SINGLE_PROP'
            var.targets[0].id_type = 'OBJECT'
            var.targets[0].id = rig
            var.targets[0].data_path = f'["{prop}"]'
        driver.expression = expression
    _mask_drivers.append((rig, None))
    return group


VALVE_CONTROL = "Ult: valves"


def _setup_viper_first_person(values, objects):
    """Her 1st person ult: the valve canisters unfolding into her palms (their own animations, kept in actions played
    by Action constraints from "Ult: valves" on the arms: 0 hidden, 1 unfolded) and gas from her hands."""
    from .valorant_psk_psa_b5 import psaimport
    arms = next((o for o in objects if o.type == 'ARMATURE' and o.name.upper().startswith("FP_")), None)
    if arms is None:
        return 0
    done = 0
    for side, key in (("LEFT", "LeftValveAnimation"), ("RIGHT", "RightValveAnimation")):
        valve = next((o for o in objects if o.type == 'ARMATURE' and o.name.upper().startswith(f"AB_PANDEMIC_S0_X_{side}HAND")), None)
        path = values.get(key)
        if valve is None or not path:
            continue
        if VALVE_CONTROL not in arms:
            arms[VALVE_CONTROL] = 0.0
            arms.id_properties_ui(VALVE_CONTROL).update(min=0.0, max=1.0, soft_min=0.0, soft_max=1.0,
                                                        description="Viper's ultimate (1st person): the gas canisters unfolding into her palms (0 hidden, 1 out)")
        try:
            psaimport(path, context=bpy.context, oArmature=valve, bKeepProportions=False, bRealTime=True,
                      bUpdateTimelineRange=False, error_callback=print)
        except Exception as error:
            print(f"VP: Viper's valve animation not loaded: {error}")
            continue
        action = valve.animation_data.action if valve.animation_data else None
        if action is None:
            continue
        valve.animation_data.action = None
        for bone in valve.pose.bones:
            bone.location = (0.0, 0.0, 0.0)
            bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
            bone.scale = (1.0, 1.0, 1.0)
        action.name = f"VP Viper valve ({side.lower()})"
        action.use_fake_user = True
        start, end = action.frame_range
        # (it unfolds, then folds back at its end: the slider runs to the frame it is most open)
        bpy.context.view_layer.update()
        rest = {b.name: b.head.copy() for b in valve.pose.bones}
        valve.animation_data.action = action
        widest, widest_frame = -1.0, end
        for frame in range(int(start), int(end) + 1):
            bpy.context.scene.frame_set(frame)
            spread = sum((valve.pose.bones[n].head - h).length for n, h in rest.items())
            if spread > widest:
                widest, widest_frame = spread, frame
        valve.animation_data.action = None
        bpy.context.scene.frame_set(int(start))
        for bone in valve.pose.bones:
            bone.location = (0.0, 0.0, 0.0)
            bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
            bone.scale = (1.0, 1.0, 1.0)
        for bone in valve.pose.bones:
            constraint = bone.constraints.new('ACTION')
            constraint.name = "VP Viper valve"
            constraint.action = action
            if hasattr(constraint, "action_slot") and getattr(action, "slots", None):
                constraint.action_slot = action.slots[0]
            constraint.use_eval_time = True
            constraint.frame_start, constraint.frame_end = int(start), int(widest_frame)
            constraint.mix_mode = 'BEFORE_FULL'
            _drive_by_control(constraint, "eval_time", arms, VALVE_CONTROL, "value")
            done += 1
        for mesh in valve.children:
            if mesh.type == 'MESH':
                for prop in ("hide_viewport", "hide_render"):
                    _drive_by_control(mesh, prop, arms, VALVE_CONTROL, "value < 0.01")
        _mask_drivers.append((arms, None))
    for hand in ("L_Hand", "R_Hand"):
        if hand in arms.data.bones and not any(o.name == f"VP Viper gas ({hand}) (1st person)" for o in bpy.data.objects):
            _add_viper_gas(arms, hand, objects, suffix=" (1st person)")
            done += 1
    if done:
        bpy.app.timers.register(_validate_mask_drivers, first_interval=0.05)
    return done


def _drive_by_control(target, path, rig, prop, expression):
    curve = target.driver_add(path)
    driver = curve.driver
    driver.type = 'SCRIPTED'
    var = driver.variables.new()
    var.name = "value"
    var.type = 'SINGLE_PROP'
    var.targets[0].id_type = 'OBJECT'
    var.targets[0].id = rig
    var.targets[0].data_path = f'["{prop}"]'
    driver.expression = expression


def _add_viper_gas(rig, hand, objects, suffix=""):
    """A gas plume on her hand (the valve on her gauntlet), for "FX: hand smoke"."""
    if GAS_CONTROL not in rig:
        rig[GAS_CONTROL] = 0.0
        rig.id_properties_ui(GAS_CONTROL).update(min=0.0, max=1.0, soft_min=0.0, soft_max=1.0,
                                                 description="Viper's ultimate: green gas pouring from her hands (0 none, 1 full)")
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    mesh = bpy.data.meshes.new(f"VP Viper gas ({hand}){suffix}")
    mesh.from_pydata([(0.0, 0.0, 0.0)], [], [])
    obj = bpy.data.objects.new(f"VP Viper gas ({hand}){suffix}", mesh)
    for collection in rig.users_collection:
        collection.objects.link(obj)
    obj.parent = rig
    obj.parent_type = 'BONE'
    obj.parent_bone = hand
    bone = rig.pose.bones[hand]
    # (at the back of her hand, where the valve is: a little toward her wrist from the hand bone)
    placed = rig.matrix_world @ bone.matrix
    along = placed.to_3x3().col[1].normalized()  # (Blender bones point along their Y: from her wrist into her hand)
    placed.translation = placed.translation + along * 0.06
    obj.matrix_world = placed
    modifier = obj.modifiers.new("VP Viper gas", 'NODES')
    body = next((o for o in objects if o.type == 'MESH' and o.name.upper().startswith(("TP_", "FP_")) and _armature_of(o) == rig), None)
    modifier.node_group = _gas_group(rig, fps, body)


# Viper's mask drivers: made valid once the import is over (the rig's evaluated copy doesn't have "Ult: mask" before)
_mask_drivers = []


def _validate_mask_drivers():
    rigs = set()
    while _mask_drivers:
        rig, constraint = _mask_drivers.pop()
        try:
            rig.update_tag()
            rigs.add(rig)
            for curve in (rig.animation_data.drivers if rig.animation_data else []):
                curve.driver.is_valid = True
        except ReferenceError:
            pass
    # (and every driver reading them: on the valves, the gas, its materials, her hair; made during the import)
    trees = [m.node_tree for m in bpy.data.materials if m.node_tree is not None]
    for collection in (bpy.data.objects, bpy.data.node_groups, bpy.data.materials, trees):
        for item in collection:
            data = getattr(item, "animation_data", None)
            if data is None:
                continue
            for curve in data.drivers:
                if not curve.driver.is_valid:
                    curve.driver.is_valid = True
            if collection is bpy.data.objects:
                item.update_tag()
            elif hasattr(item, "update_tag"):
                item.update_tag()
    try:
        bpy.context.view_layer.update()
    except Exception:
        pass
    return None


def _action_curves(action, owner):
    """An action's F-curves (Blender 5: in its layers' channelbags)."""
    if hasattr(action, "fcurves") and len(getattr(action, "fcurves", [])):
        return list(action.fcurves)
    curves = []
    for layer in getattr(action, "layers", []):
        for strip in layer.strips:
            for bag in getattr(strip, "channelbags", []):
                curves += list(bag.fcurves)
    return curves


# each agent with a glow: its shader, which of its materials get it (name contains), and its controls on the
# armature: (custom property, the group's input, default, description)
AGENTS = {
    "vampire": dict(
        node=GROUP, build=_build_group, materials="vampire", hands=True, first_person=True, fp_none=("MaskSetsXE", "ChestFlare"),
        controls=[(HEART, "Heart", 1.0, CONTROLS[HEART][1]), (EYES, "Eyes", 0.0, CONTROLS[EYES][1]),
                  (ULTIMATE, "Ultimate", 0.0, CONTROLS[ULTIMATE][1]),
                  ("Glow: dismiss", "Dismiss", 0.0, "Reyna's Dismiss: she turns see-through purple with a glowing magenta outline"),
                  ("Glow: tattoos", "Tattoos", 0.0, "Reyna's arm tattoos glowing on their own (1 on, 0 off), without her ultimate or Dismiss")]),
    "sequoia": dict(
        node="VP Iso Glow", build=_build_iso_group, materials="sequoia", skip=("hair",), first_person=True,
        controls=[("Glow: outfit", "Outfit", 0.0, "Iso's outfit lines glowing"),
                  ("Glow: hands (E)", "Hands E", 0.0, "Iso's forearms glowing, as with his E (charged punches)"),
                  ("Glow: hands (abilities)", "Hands abilities", 0.0, "Iso's gloves glowing, as with his other abilities")]),
    "deadeye": dict(
        node="VP Chamber Tattoo", build=_build_chamber_group, materials="deadeye", skip=("hair", "glass"), first_person=True,
        controls=[("Tattoo", "Tattoo", 1.0, "Chamber's gold tattoo (side of his head, wrists); from 0 it reveals left to right with a glowing edge, as in character select")]),
    "terra": dict(
        node="VP Waylay Glow", build=_build_waylay_group, materials=("terra_s0_arm", "terra_s0_hair", "terra_s0_floaters"), first_person=True,
        controls=[("Glow: tattoos", "Tattoos", 0.0, "Waylay's crystal tattoos, nails and hair tips glowing, as when she uses her abilities")]),
    "smonk": dict(
        node="VP Clove Glow", build=_build_clove_group, materials="smonk", prepare=_prepare_clove, first_person=True,
        fp_none=("UltHairMask", "CMask", "GhostEyes"),
        per_material=_swap_clove_albedo, see_through=True,
        extra_drivers=[("VP Clove Ult Colours", "Factor", "Ult: active")],
        controls=[("Ult: respawn", "Respawn", 1.0, "Clove fading in from her head down (0 to 1), as she comes back with her ultimate (with \"Ult: ghost\" at 1: her ghost)"),
                  ("Ult: ghost", "Ghost", 0.0, "Clove's ghost (her resurrection model) instead of her: 0 to 1 it takes her place from her head down; back to 0 for her ult form"),
                  ("Ult: active", "Ult", 0.0, "Clove's ultimate is on: her ult colours and glow, until she gets a kill"),
                  ("Glow: legs (C)", "Legs", 0.0, "Clove's legs glowing, smoke rising and her trousers' butterflies lit, as with her C")]),
    "sprinter": dict(
        node="VP Neon Glow", build=_build_neon_group, materials="sprinter",
        first_person=True, fp_none=("TattooMask", "HairMask"),
        controls=[("Glow: sprint (E)", "Sprint", 0.0, "Neon's suit lighting up blue, as when she sprints (her E)"),
                  ("Glow: ultimate", "Ult", 0.0, "Neon's ultimate: her suit, tattoo and the edges of her skin glowing cyan with electricity, her hair blue to yellow")]),
    "wushu": dict(
        node="VP Jett Tattoos", build=_build_jett_group, materials=("wushu_s0_tattoo", "fp_wushu_s0"),
        first_person=True, fp_none=("Mask",), fp_always=True,
        controls=[("Glow: arms (ult)", "Loop", 0.0, "Jett's arms streaming wind, as while her ultimate is up")]),
    "phoenix": dict(
        node="VP Phoenix Fire", build=_build_phoenix_group, materials="phoenix_s0", first_person=True, fp_none=("Masks",), fp_always=True,
        controls=[("Glow: ultimate", "Ult", 0.0, "Phoenix's ultimate (Run It Back): flames crawling over him, his chest glowing"),
                  ("Glow: heal", "Heal", 0.0, "Phoenix healing in his own fire: softer flames over him")]),
    "grenadier": dict(
        node="VP KAYO Glow", build=_build_kayo_group, materials="grenadier", first_person=True,
        controls=[("Glow: idle", "Idle", 1.0, "KAY/O's teal panels glowing, as always in game"),
                  ("Glow: flash (Q)", "Flash", 0.0, "KAY/O holding his flash: his panels glow lime"),
                  ("Glow: frag (C)", "Frag", 0.0, "KAY/O holding his frag: his panels glow blue"),
                  ("Glow: knife (E)", "Knife", 0.0, "KAY/O holding his knife: his panels glow purple, a purple outline"),
                  ("Glow: ultimate", "Ult", 0.0, "KAY/O's ultimate (NULL/cmd): his panels violet and flickering, a violet outline")]),
    "wraith": dict(
        node="VP Omen Ult", build=_build_omen_group, materials=("wraith", "ethereal"), prepare=_prepare_omen, see_through=True,
        first_person=True, fp_none=("Cracks",), fp_always=True,
        controls=[("Ult: cast", "Cast", 0.0, "Omen casting his ultimate: 0 -> 0.5 he turns to shadow, his markings glowing cyan; 0.5 -> 1 he dissolves away"),
                  ("Ult: ghost", "Ghost", 0.0, "Omen's ghost (his ethereal form as he arrives): 0 -> 1 it takes his place; back to 0 he comes back")]),
    "stealth": dict(
        node="VP Yoru Ult", build=_build_yoru_group, materials="stealth", prepare=_prepare_yoru, first_person=True,
        per_material=lambda tree, node, name, images: setattr(node.inputs["Prop"], "default_value", 1.0 if "_acc_" in name else 0.0),
        controls=[("Ult: active", "Ult", 0.0, "Yoru's ultimate (Dimensional Drift): he turns into a glowing blue hologram, his mask on his face"),
                  ("Ult: glitch", "Glitch", 0.5, "How much his hologram glitches: 0 none, 0.5 as in game, 1 twice as much (the warp and the flickering strips)")]),
    "pandemic": dict(rig_only=_setup_viper_mask),
    "pine": dict(
        node="VP Veto Ult Form", build=_build_veto_group, materials="pine", prepare=_prepare_veto, see_through=True, first_person=True,
        # (in 1st person: his left arm is his symbiote arm, always there; his right arm stays under its symbiote
        # version, its skin showing where the symbiote's material leaves it: as on the left arm)
        skip=("fp_pine_s0_sym", "fp_pine_s0_mi"),
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
    return next((m.object for m in obj.modifiers if m.type == 'ARMATURE' and m.object), None) or         next((c.target for c in obj.constraints if c.type == 'CHILD_OF' and c.target is not None and c.target.type == 'ARMATURE'), None)


HANDS_ATTRIBUTE = "vp_hands"
HAND_BONES = ("Hand", "Thumb", "Index", "Middle", "Ring", "Pinky")


def _mark_hands(obj):
    """A per-vertex attribute: how much its hand and finger bones move it (0..1), for hand glows."""
    groups = [g.index for g in obj.vertex_groups
              if g.name.split("_", 1)[-1].rstrip("0123456789") in HAND_BONES and g.name[:2] in ("L_", "R_")]
    if not groups or HANDS_ATTRIBUTE in obj.data.attributes:
        return
    hand_only = {g.index for g in obj.vertex_groups if g.name in ("L_Hand", "R_Hand")}
    weights = [min(1.0, sum(e.weight * (0.35 if e.group in hand_only else 1.0) for e in v.groups if e.group in groups))
               for v in obj.data.vertices]
    obj.data.attributes.new(HANDS_ATTRIBUTE, 'FLOAT', 'POINT').data.foreach_set("value", weights)


def apply(glow, objects, load_image):
    """glow: what the app sent ({"Agent", "Textures": role -> game path, "Values"}); objects: the agent's new objects;
    load_image(game path) -> image. Adds the agent's glow layer to its own materials (body, hair; not the eye overlay
    shared by agents) and its controls to its armature."""
    spec = AGENTS.get((glow or {}).get("Agent", "").lower())
    if spec is None:
        return 0
    if spec.get("rig_only"):
        # (no glow: something on her rig, like Viper's mask slider)
        return spec["rig_only"](glow.get("Values") or {}, objects)
    if spec.get("hands"):
        for obj in objects:
            if obj.type == 'MESH':
                _mark_hands(obj)
    images = {}
    for role, path in (glow.get("Textures") or {}).items():
        try:
            images[role] = load_image(path)
        except Exception:
            images[role] = None
    group = spec["build"]({k: v for k, v in images.items() if not k.startswith("FP:")}, glow.get("Values") or {})
    # the 1st person arms' own glow: the same shader with their own masks ("FP:" roles; the others it lacks left out)
    group_fp = None
    if spec.get("first_person") and (any(k.startswith("FP:") for k in images) or spec.get("fp_always")):
        name = group.name
        group.name = name + " (3rd person)"
        fp_images = {k: v for k, v in images.items() if not k.startswith("FP:")}
        for k, v in images.items():
            if k.startswith("FP:"):
                fp_images[k[3:]] = v
        for k in spec.get("fp_none", ()):
            fp_images[k] = None  # (what the arms don't have: Neon's hair, Reyna's eyes and heart)
        group_fp = spec["build"](fp_images, glow.get("Values") or {})
        group_fp.name = name + " (1st person)"
        group.name = name
    if spec.get("prepare"):
        objects = spec["prepare"](objects)
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    done = 0
    # the 3rd person model first: a material it shares with the character select model is driven by its rig
    for obj in sorted(objects, key=lambda o: 0 if o.name.upper().startswith("TP") else 1):
        if obj.type != 'MESH':
            continue
        armature = _armature_of(obj)
        if armature is None or (obj.name.upper().startswith("FP_") and not spec.get("first_person")):
            continue  # the 1st person arms have their own layouts (done for some agents)
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
            node.node_tree = group_fp if (group_fp is not None and obj.name.upper().startswith("FP_")) else group
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
    while _ult_warps:
        obj, rig, prop = _ult_warps.pop()
        try:
            if obj.modifiers.get("VP Hologram Warp") is None:
                fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
                modifier = obj.modifiers.new("VP Hologram Warp", 'NODES')  # (after the rig: last in the stack)
                modifier.node_group = _warp_group(rig, fps)
            rig.update_tag()
        except ReferenceError:
            pass
    while _ult_props:
        obj, rig, prop = _ult_props.pop()
        try:
            for path in ("hide_viewport", "hide_render"):
                curve = obj.driver_add(path)
                driver = curve.driver
                driver.type = 'SCRIPTED'
                var = driver.variables.new()
                var.name = "value"
                var.type = 'SINGLE_PROP'
                var.targets[0].id_type = 'OBJECT'
                var.targets[0].id = rig
                var.targets[0].data_path = f'["{prop}"]'
                driver.expression = "value < 0.5"
            rig.update_tag()
        except ReferenceError:
            pass
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
