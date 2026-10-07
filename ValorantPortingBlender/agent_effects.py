"""An agent's ability particle effects (the game's Cascade particle systems), rebuilt from the game's own values.

The app sends, for each place the game spawns a system on the agent (a bone, an offset, a delay), the system's emitters
as the game stores them: their modules (spawn rate and bursts, lifetime, size, rotation, location, velocity, colour and
alpha over life, camera offset; random ranges and curves as the game's baked lookup tables) and their materials.

Each emitter becomes an object hung on that bone: a small mesh of points, one per particle the emitter makes over a
30 second cycle (birth time, lifetime, start size, ... picked from the game's ranges), and geometry nodes that show the
ones alive at the current frame as sprites turned to the camera. Its material does the colour and alpha over life
(the game's tables), flipbook frames and the glow. Emitters that only light what is behind them ("fake lights") become
point lights. A control on the armature switches each effect on ("FX: ultimate")."""
import math
import random
import zlib
import bpy

CYCLE = 30.0  # seconds of particles made; the effect repeats after that
UNIT = 0.01  # Unreal cm -> m


# --- the game's values ----------------------------------------------------------------------------------------------

def _table(distribution):
    """(values, entry stride, sub-entry stride, op, time scale, time bias) of a distribution's baked lookup table."""
    table = (distribution or {}).get("Table") or {}
    return (table.get("Values") or [], max(int(table.get("EntryStride") or 1), 1), max(int(table.get("SubEntryStride") or 0), 0),
            int(table.get("Op") or 1), float(table.get("TimeScale") or 0.0), float(table.get("TimeBias") or 0.0))


def _entries(distribution):
    values, stride, _, _, _, _ = _table(distribution)
    return [values[i:i + stride] for i in range(0, len(values) - stride + 1, stride)] or [[0.0]]


def sample(distribution, time=0.0, rng=None, size=1):
    """A distribution's value at `time` (0..1 over a particle's life, or seconds), a random one when it is a range.
    Returns `size` floats."""
    if not distribution:
        return [0.0] * size
    values, stride, sub, op, scale, bias = _table(distribution)
    if not values:
        low, high = distribution.get("MinValue"), distribution.get("MaxValue")
        if isinstance(distribution.get("MinValueVec"), dict):
            low = [distribution["MinValueVec"][k] for k in "XYZ"][:size]
            high = [distribution["MaxValueVec"][k] for k in "XYZ"][:size]
        low = low if isinstance(low, list) else [low or 0.0] * size
        high = high if isinstance(high, list) else [high if high is not None else low[0]] * size
        t = rng.random() if rng else 0.5
        return [a + (b - a) * t for a, b in zip(low, high)]
    entries = _entries(distribution)
    position = (time - bias) * scale if scale else 0.0
    i = min(max(int(math.floor(position)), 0), len(entries) - 1)
    j = min(i + 1, len(entries) - 1)
    f = min(max(position - i, 0.0), 1.0)
    entry = [a + (b - a) * f for a, b in zip(entries[i], entries[j])]
    if op == 2 and sub and len(entry) >= 2 * sub:  # a random value between two sets
        t = rng.random() if rng else 0.5
        low, high = entry[:sub], entry[sub:2 * sub]
        entry = [a + (b - a) * t for a, b in zip(low, high)]
    if len(entry) < size:
        entry = entry + [entry[-1]] * (size - len(entry))
    return entry[:size]


def _module(emitter, kind):
    return next((m.get("Properties") or {} for m in emitter.get("Modules") or [] if m.get("Type") == "ParticleModule" + kind), None)


def _modules(emitter, kind):
    return [m.get("Properties") or {} for m in emitter.get("Modules") or [] if m.get("Type") == "ParticleModule" + kind]


def schedule(emitter, seed):
    """The particles this emitter makes over a cycle (birth time, life, size, ...), as the game would: bursts at their
    time in each loop of the emitter, plus a steady rate."""
    required = _module(emitter, "Required") or {}
    spawn = _module(emitter, "Spawn") or {}
    rng = random.Random(seed)
    duration = float(required.get("EmitterDuration") or 1.0)
    if required.get("bEmitterDurationUseRange"):
        low = float(required.get("EmitterDurationLow") or 0.0)
        duration_of = lambda: low + (duration - low) * rng.random()
    else:
        duration_of = lambda: duration
    loops = int(required.get("EmitterLoops") or 0)
    rate = sample(spawn.get("Rate"))[0] * (sample(spawn.get("RateScale"))[0] if spawn.get("RateScale") else 1.0)
    bursts = [(float(b.get("Time") or 0.0), int(b.get("Count") or 0), int(b.get("CountLow") if b.get("CountLow") is not None else -1))
              for b in spawn.get("BurstList") or []]

    births = []
    start, loop = 0.0, 0
    while start < 2 * CYCLE and (loops == 0 or loop < loops):
        length = max(duration_of(), 0.05)
        for time, count, low in bursts:
            n = count if low < 0 else rng.randint(min(low, count), max(low, count))
            births += [start + time * length] * n
        if rate > 0:
            n = rate * length
            births += [start + (k + rng.random() * 0.0) / rate for k in range(int(n))]
        start += length
        loop += 1
    if loops and births:
        # a one-shot emitter (a glow that stays): shown the whole time
        pass

    lifetime = _module(emitter, "Lifetime")
    size = _module(emitter, "Size")
    rotation = _module(emitter, "Rotation")
    location = _module(emitter, "Location")
    velocity = _module(emitter, "Velocity")
    particles = []
    for birth in births:
        life = sample((lifetime or {}).get("Lifetime"), rng=rng)[0] if lifetime else 1.0
        forever = life <= 0.0
        particles.append(dict(
            birth=-1.0 if forever else birth,
            life=1e9 if forever else life,
            size=(lambda xyz: (xyz[0] * UNIT, (xyz[1] or xyz[0]) * UNIT))(sample((size or {}).get("StartSize"), rng=rng, size=3)) if size else (0.1, 0.1),
            rotation=sample((rotation or {}).get("StartRotation"), rng=rng)[0] * 2 * math.pi if rotation else 0.0,
            location=_vector(sample((location or {}).get("StartLocation"), rng=rng, size=3)) if location else (0.0, 0.0, 0.0),
            velocity=_vector(sample((velocity or {}).get("StartVelocity"), rng=rng, size=3)) if velocity else (0.0, 0.0, 0.0),
            seed=rng.random()))
    return particles


def _vector(xyz):
    """Unreal cm (Y mirrored in Blender) -> m"""
    return (xyz[0] * UNIT, -xyz[1] * UNIT, xyz[2] * UNIT)


def colour_over_life(emitter, steps=24):
    """[(life 0..1, (r, g, b), alpha)] of a particle over its life: its start colour / colour over life, times any
    colour scale over life."""
    over = _module(emitter, "ColorOverLife")
    start = next((c for c in _modules(emitter, "Color")), None)
    scales = _modules(emitter, "ColorScaleOverLife")
    stops = []
    for k in range(steps + 1):
        t = k / steps
        if over:
            rgb = sample(over.get("ColorOverLife"), t, size=3)
            alpha = sample(over.get("AlphaOverLife"), t)[0] if over.get("AlphaOverLife") else 1.0
        elif start:
            rgb = sample(start.get("StartColor"), size=3)
            alpha = sample(start.get("StartAlpha"))[0] if start.get("StartAlpha") else 1.0
        else:
            rgb, alpha = [1.0, 1.0, 1.0], 1.0
        for scale in scales:
            s = sample(scale.get("ColorScaleOverLife"), t, size=3) if scale.get("ColorScaleOverLife") else [1.0] * 3
            rgb = [a * b for a, b in zip(rgb, s)]
            if scale.get("AlphaScaleOverLife"):
                alpha *= sample(scale.get("AlphaScaleOverLife"), t)[0]
        stops.append((t, tuple(rgb), alpha))
    return stops


# --- Blender -----------------------------------------------------------------------------------------------------------

def _particle_mesh(name, particles):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([p["location"] for p in particles], [], [])
    for p in particles:  # (width, height) -> a vector
        p["scale"] = (p["size"][0], p["size"][1], 1.0)
    for attribute, kind in (("vp_birth", 'FLOAT'), ("vp_life", 'FLOAT'), ("vp_scale", 'FLOAT_VECTOR'), ("vp_rotation", 'FLOAT'),
                            ("vp_seed", 'FLOAT'), ("vp_velocity", 'FLOAT_VECTOR')):
        data = mesh.attributes.new(attribute, kind, 'POINT').data
        key = attribute[3:]
        if kind == 'FLOAT':
            data.foreach_set("value", [p[key] for p in particles])
        else:
            data.foreach_set("vector", [c for p in particles for c in p[key]])
    return mesh


def _node_group():
    """Geometry nodes: the particles alive at "Time", as sprites turned to the camera; each sprite gets its age
    (0..1 over its life) and a random number for its material."""
    name = "VP Particles"
    if bpy.data.node_groups.get(name):
        return bpy.data.node_groups[name]
    group = bpy.data.node_groups.new(name, 'GeometryNodeTree')
    face = group.interface
    face.new_socket("Geometry", in_out='INPUT', socket_type='NodeSocketGeometry')
    face.new_socket("Geometry", in_out='OUTPUT', socket_type='NodeSocketGeometry')
    nodes, links = group.nodes, group.links
    inputs, output = nodes.new("NodeGroupInput"), nodes.new("NodeGroupOutput")
    # set per emitter (each has its own copy of this group): seconds since the effect started, on/off, camera offset
    values = {}
    for value_name, default in (("Time", 0.0), ("Active", 1.0), ("Camera Offset", 0.0)):
        node = nodes.new("ShaderNodeValue")
        node.name = node.label = "VP " + value_name
        node.outputs[0].default_value = default
        values[value_name] = node.outputs[0]

    def attribute(name_, kind='FLOAT'):
        node = nodes.new("GeometryNodeInputNamedAttribute")
        node.data_type = kind
        node.inputs["Name"].default_value = name_
        return node.outputs["Attribute"]

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

    def vector_math(op, a, b=None, scale=None):
        node = nodes.new("ShaderNodeVectorMath")
        node.operation = op
        links.new(a, node.inputs[0])
        if b is not None:
            links.new(b, node.inputs[1])
        if scale is not None:
            if isinstance(scale, (int, float)):
                node.inputs["Scale"].default_value = scale
            else:
                links.new(scale, node.inputs["Scale"])
        return node.outputs["Vector"] if op != 'LENGTH' else node.outputs["Value"]

    # the time within the cycle (the second half of what was made, so particles born before it are there too)
    time = math_node('ADD', CYCLE, math_node('FLOORED_MODULO', values["Time"], CYCLE))
    age = math_node('SUBTRACT', time, attribute("vp_birth"))
    life = attribute("vp_life")
    alive = math_node('MULTIPLY', math_node('MULTIPLY', math_node('GREATER_THAN', age, 0.0), math_node('LESS_THAN', age, life)),
                      math_node('GREATER_THAN', values["Active"], 0.5))
    delete = nodes.new("GeometryNodeDeleteGeometry")
    delete.domain = 'POINT'
    links.new(inputs.outputs["Geometry"], delete.inputs["Geometry"])
    links.new(math_node('LESS_THAN', alive, 0.5), delete.inputs["Selection"])
    points = nodes.new("GeometryNodeMeshToPoints")
    links.new(delete.outputs["Geometry"], points.inputs["Mesh"])

    # moving with its velocity; pulled toward the camera by the game's camera offset
    position = nodes.new("GeometryNodeInputPosition")
    moved = vector_math('ADD', position.outputs[0], vector_math('SCALE', attribute("vp_velocity", 'FLOAT_VECTOR'), scale=age))
    camera = nodes.new("GeometryNodeInputActiveCamera")
    camera_info = nodes.new("GeometryNodeObjectInfo")
    camera_info.transform_space = 'RELATIVE'
    links.new(camera.outputs[0], camera_info.inputs["Object"])
    to_camera = vector_math('NORMALIZE', vector_math('SUBTRACT', camera_info.outputs["Location"], moved))
    placed = vector_math('ADD', moved, vector_math('SCALE', to_camera, scale=math_node('MULTIPLY', values["Camera Offset"], UNIT)))
    set_position = nodes.new("GeometryNodeSetPosition")
    links.new(points.outputs[0], set_position.inputs["Geometry"])
    links.new(placed, set_position.inputs["Position"])

    # size over life (the game's SizeMultiplyLife; flat at 1 unless set per emitter)
    life_ratio = math_node('DIVIDE', age, life)
    curves = []
    for axis in "XY":
        curve = nodes.new("ShaderNodeFloatCurve")
        curve.name = "VP Size " + axis
        curve.mapping.use_clip = False
        curve.mapping.clip_min_y, curve.mapping.clip_max_y = -100.0, 100.0
        points = curve.mapping.curves[0].points
        points[0].location, points[1].location = (0.0, 1.0), (1.0, 1.0)
        curve.mapping.update()
        links.new(life_ratio, curve.inputs["Value"])
        curves.append(curve.outputs[0])
    over_life = nodes.new("ShaderNodeCombineXYZ")
    links.new(curves[0], over_life.inputs[0])
    links.new(curves[1], over_life.inputs[1])
    over_life.inputs[2].default_value = 1.0
    scale = vector_math('MULTIPLY', attribute("vp_scale", 'FLOAT_VECTOR'), over_life.outputs[0])

    store_age = nodes.new("GeometryNodeStoreNamedAttribute")
    store_age.data_type = 'FLOAT'
    store_age.domain = 'POINT'
    store_age.inputs["Name"].default_value = "vp_age"
    links.new(set_position.outputs[0], store_age.inputs["Geometry"])
    links.new(math_node('DIVIDE', age, life), store_age.inputs["Value"])

    # a 1 x 1 card with its UVs, turned to the camera and spun by its rotation
    card = nodes.new("GeometryNodeMeshGrid")
    card.inputs["Size X"].default_value = card.inputs["Size Y"].default_value = 1.0
    card.inputs["Vertices X"].default_value = card.inputs["Vertices Y"].default_value = 2
    store_uv = nodes.new("GeometryNodeStoreNamedAttribute")
    store_uv.data_type = 'FLOAT2'
    store_uv.domain = 'CORNER'
    store_uv.inputs["Name"].default_value = "vp_uv"
    links.new(card.outputs["Mesh"], store_uv.inputs["Geometry"])
    links.new(card.outputs["UV Map"], store_uv.inputs["Value"])
    set_material = nodes.new("GeometryNodeSetMaterial")
    set_material.name = "VP Material"  # set per emitter (each has its own copy of this group)
    links.new(store_uv.outputs[0], set_material.inputs["Geometry"])

    facing = nodes.new("FunctionNodeAlignRotationToVector")
    facing.axis = 'Z'
    links.new(to_camera, facing.inputs["Vector"])
    # locked to the emitter's X axis (the game's OrientationAxisLock ROTATE_X): along X, turned to the camera around it
    along = nodes.new("FunctionNodeAlignRotationToVector")
    along.axis = 'X'
    along.inputs["Vector"].default_value = (1.0, 0.0, 0.0)
    around = nodes.new("FunctionNodeAlignRotationToVector")
    around.axis = 'Z'
    around.pivot_axis = 'X'
    links.new(along.outputs[0], around.inputs["Rotation"])
    links.new(to_camera, around.inputs["Vector"])
    lock = nodes.new("ShaderNodeValue")
    lock.name = lock.label = "VP Axis Lock"
    lock.outputs[0].default_value = 0.0
    switch = nodes.new("GeometryNodeSwitch")
    switch.input_type = 'ROTATION'
    links.new(math_node('GREATER_THAN', lock.outputs[0], 0.5), switch.inputs["Switch"])
    links.new(facing.outputs[0], switch.inputs["False"])
    links.new(around.outputs[0], switch.inputs["True"])
    spin = nodes.new("FunctionNodeAxisAngleToRotation")
    spin.inputs["Axis"].default_value = (0.0, 0.0, 1.0)
    links.new(attribute("vp_rotation"), spin.inputs["Angle"])
    turned = nodes.new("FunctionNodeRotateRotation")
    turned.rotation_space = 'LOCAL'
    links.new(switch.outputs[0], turned.inputs["Rotation"])
    links.new(spin.outputs[0], turned.inputs["Rotate By"])

    instance = nodes.new("GeometryNodeInstanceOnPoints")
    links.new(store_age.outputs[0], instance.inputs["Points"])
    links.new(set_material.outputs[0], instance.inputs["Instance"])
    links.new(turned.outputs[0], instance.inputs["Rotation"])
    links.new(scale, instance.inputs["Scale"])
    links.new(instance.outputs[0], output.inputs[0])
    return group


def _material(emitter, images, name):
    """The emitter's look: its colour and alpha over life (the game's tables, as a ramp on the sprite's age), its
    texture (a flipbook played over the particle's life, coloured by the material's own colours when it has them),
    or a soft radial glow; added on top of what's behind (additive) or blended over it."""
    spec = emitter.get("Material") or {}
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    if hasattr(material, "surface_render_method"):
        material.surface_render_method = 'BLENDED'
    if hasattr(material, "use_transparent_shadow"):
        material.use_transparent_shadow = True
    tree = material.node_tree
    nodes, links = tree.nodes, tree.links
    for node in list(nodes):
        nodes.remove(node)
    output = nodes.new("ShaderNodeOutputMaterial")

    def instancer(attribute_name):
        node = nodes.new("ShaderNodeAttribute")
        node.attribute_type = 'INSTANCER'
        node.attribute_name = attribute_name
        return node

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

    age = instancer("vp_age").outputs["Fac"]
    uv_node = nodes.new("ShaderNodeAttribute")
    uv_node.attribute_type = 'GEOMETRY'
    uv_node.attribute_name = "vp_uv"
    uv = uv_node.outputs["Vector"]

    # colour and alpha over life: a ramp of the game's values (scaled down to fit; the brightness put back after)
    stops = colour_over_life(emitter)
    brightest = max(max(rgb) for _, rgb, _ in stops) or 1.0
    # the game's colours go far above 1 for its bloom (x1111 on Jett's knife flare): kept within what reads in Blender
    BRIGHTEST = 12.0
    ramp = nodes.new("ShaderNodeValToRGB")
    elements = ramp.color_ramp.elements
    while len(elements) < min(len(stops), 32):
        elements.new(0.5)
    picked = [stops[round(i * (len(stops) - 1) / (min(len(stops), 32) - 1))] for i in range(min(len(stops), 32))]
    for element, (t, rgb, alpha) in zip(elements, picked):
        element.position = t
        element.color = (rgb[0] / brightest, rgb[1] / brightest, rgb[2] / brightest, min(max(alpha, 0.0), 1.0))
    links.new(age, ramp.inputs[0])
    life_colour, life_alpha = ramp.outputs["Color"], ramp.outputs["Alpha"]

    scalars, vectors = spec.get("Scalars") or {}, spec.get("Vectors") or {}
    textures = spec.get("Textures") or {}
    texture_path = next(iter(textures.values()), None)
    image = images.get(texture_path) if texture_path else None
    colour = None
    if "Emissive_Base" in textures or "Alpha_Base" in textures:
        # the game's generic effect material (0_GenericA01_MAT): a colour texture and an alpha texture, each raised to
        # a power, scaled and turned
        def generic(role, power, multiply, turn):
            img = images.get(textures.get(role))
            if img is None:
                return None
            rotate = nodes.new("ShaderNodeVectorRotate")
            rotate.rotation_type = 'Z_AXIS'
            rotate.inputs["Center"].default_value = (0.5, 0.5, 0.0)
            rotate.inputs["Angle"].default_value = float(scalars.get(turn, 0.0)) * 2 * math.pi
            links.new(uv, rotate.inputs["Vector"])
            node = nodes.new("ShaderNodeTexImage")
            node.image = img
            node.extension = 'CLIP'
            links.new(rotate.outputs[0], node.inputs[0])
            return node, float(scalars.get(power, 1.0)), float(scalars.get(multiply, 1.0))

        emissive = generic("Emissive_Base", "Emissive_Base_Power", "Emissive_Base_Multiply", "Emissive_Rotate")
        alpha_tex = generic("Alpha_Base", "Alpha_Base_Power", "Alpha_ColorMult", "Alpha_Rotate")
        if emissive is not None:
            node, power, multiply = emissive
            raised = nodes.new("ShaderNodeVectorMath")
            raised.operation = 'POWER' if hasattr(bpy.types, "ShaderNodeVectorMath") else 'MULTIPLY'
            raised.inputs[1].default_value = (power, power, power)
            links.new(node.outputs["Color"], raised.inputs[0])
            colour = raised.outputs[0]
        if alpha_tex is not None:
            node, power, multiply = alpha_tex
            channels = nodes.new("ShaderNodeSeparateColor")
            links.new(node.outputs["Color"], channels.inputs[0])
            mask = math_node('MULTIPLY', math_node('POWER', channels.outputs[0], power), multiply)
        else:
            mask = 1.0
    elif image is not None:
        rows, columns = int(scalars.get("FlipbookRows") or 1), int(scalars.get("FlipbookColumns") or 1)
        frames = max(rows * columns, 1)
        if frames > 1:
            # the frame for the particle's age, left to right, top to bottom
            frame = math_node('MINIMUM', math_node('FLOOR', math_node('MULTIPLY', age, frames)), frames - 1)
            column = math_node('FLOORED_MODULO', frame, columns)
            row = math_node('SUBTRACT', rows - 1, math_node('FLOOR', math_node('DIVIDE', frame, columns)))
            split = nodes.new("ShaderNodeSeparateXYZ")
            links.new(uv, split.inputs[0])
            combine = nodes.new("ShaderNodeCombineXYZ")
            links.new(math_node('DIVIDE', math_node('ADD', split.outputs[0], column), columns), combine.inputs[0])
            links.new(math_node('DIVIDE', math_node('ADD', split.outputs[1], row), rows), combine.inputs[1])
            uv = combine.outputs[0]
        texture = nodes.new("ShaderNodeTexImage")
        texture.image = image
        texture.extension = 'CLIP'
        links.new(uv, texture.inputs[0])
        mask = texture.outputs["Alpha"]
        channels = nodes.new("ShaderNodeSeparateColor")
        links.new(texture.outputs["Color"], channels.inputs[0])
        shades = [vectors.get(k) for k in ("Color0_Shadow", "Color0_Mid", "Color0_Highlight")]
        if all(shades):
            # the material's own colours from shadow to highlight, by the texture's brightness
            gradient = nodes.new("ShaderNodeValToRGB")
            gradient.color_ramp.elements.new(0.5)
            for element, position, value in zip(gradient.color_ramp.elements, (0.0, 0.5, 1.0), shades):
                element.position = position
                element.color = (value[0], value[1], value[2], 1.0)
            links.new(channels.outputs[0], gradient.inputs[0])
            colour = gradient.outputs["Color"]
        else:
            colour = texture.outputs["Color"]
    else:
        # a soft radial glow (the game's RadialGradientGlow: one wide gradient, one tight)
        centre = nodes.new("ShaderNodeVectorMath")
        centre.operation = 'DISTANCE'
        links.new(uv, centre.inputs[0])
        centre.inputs[1].default_value = (0.5, 0.5, 0.0)
        distance = centre.outputs["Value"]

        def gradient(radius, density):
            return math_node('MULTIPLY', math_node('POWER', math_node('SUBTRACT', 1.0, math_node('DIVIDE', distance, max(radius, 0.01)), clamp=True), 2.0), density)

        mask = gradient(float(scalars.get("Gradient01_Radius", 0.5)), float(scalars.get("Gradient01_Density", 1.0)))
        if "Gradient02_Radius" in scalars:
            mask = math_node('ADD', mask, gradient(float(scalars["Gradient02_Radius"]), float(scalars.get("Gradient02_Density", 1.0))))

    tint = nodes.new("ShaderNodeMix")
    tint.data_type = 'RGBA'
    tint.blend_type = 'MULTIPLY'
    tint.inputs[0].default_value = 1.0
    links.new(life_colour, tint.inputs[6])
    if colour is not None:
        links.new(colour, tint.inputs[7])
    else:
        tint.inputs[7].default_value = (1.0, 1.0, 1.0, 1.0)
    glow = nodes.new("ShaderNodeEmission")
    links.new(tint.outputs[2], glow.inputs["Color"])
    opacity = math_node('MULTIPLY', mask, life_alpha, clamp=False)
    links.new(math_node('MULTIPLY', opacity, min(brightest, BRIGHTEST)), glow.inputs["Strength"])
    see_through = nodes.new("ShaderNodeBsdfTransparent")
    if "Additive" in (spec.get("BlendMode") or ""):
        result = nodes.new("ShaderNodeAddShader")
        links.new(see_through.outputs[0], result.inputs[0])
        links.new(glow.outputs[0], result.inputs[1])
    else:
        result = nodes.new("ShaderNodeMixShader")
        links.new(math_node('MINIMUM', opacity, 1.0), result.inputs[0])
        links.new(see_through.outputs[0], result.inputs[1])
        links.new(glow.outputs[0], result.inputs[2])
    links.new(result.outputs[0], output.inputs["Surface"])
    return material


# drivers made by the last import, re-checked once it is over (see revalidate)
_pending = []


def revalidate():
    """Blender's evaluated copy of the armature doesn't have its new controls until the import is over; drivers that
    read them before that are marked invalid. Re-checks them (as a timer: once)."""
    while _pending:
        rig, curve = _pending.pop()
        try:
            rig.update_tag()
            curve.driver.is_valid = True
        except ReferenceError:
            pass
    return None


def _drive(target, path, armature, prop, expression="value"):
    curve = target.driver_add(path)
    driver = curve.driver
    driver.type = 'SCRIPTED'
    var = driver.variables.new()
    var.name = "value"
    var.type = 'SINGLE_PROP'
    var.targets[0].id_type = 'OBJECT'
    var.targets[0].id = armature
    var.targets[0].data_path = f'["{prop}"]'
    driver.expression = expression
    _pending.append((armature, curve))
    return curve


def apply(effects, rig, load_image, attach, collection):
    """effects: what the app sent (spawns); rig: the agent's armature; load_image(game path) -> image;
    attach(obj, rig, {"Bone", "Location", "Rotation"}): hangs an object on a bone with an Unreal offset."""
    if not effects or rig is None:
        return 0
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    group = _node_group()
    images = {}
    made = 0
    for number, spawn in enumerate(effects):
        socket = spawn.get("Socket")
        if socket not in rig.data.bones:
            continue
        control = spawn.get("Effect") or "FX"
        if control not in rig:
            rig[control] = 0.0
            rig.id_properties_ui(control).update(min=0.0, max=1.0, soft_min=0.0, soft_max=1.0,
                                                 description="The agent's effect particles, as in game (1: on)")
        anchor = bpy.data.objects.new(f"VP FX {spawn.get('System')} ({socket})", None)
        anchor.empty_display_size = 0.05
        collection.objects.link(anchor)
        attach(anchor, rig, {"Bone": socket, "Location": spawn.get("Offset"), "Rotation": spawn.get("Rotation")})
        for emitter in spawn.get("Emitters") or []:
            name = f"VP FX {emitter.get('Name')} ({socket})"
            blend = (emitter.get("Material") or {}).get("BlendMode") or ""
            if "ColoredTransmittance" in blend:
                # a "fake light": it only tints what is behind it -> a light
                start = next((c for c in _modules(emitter, "Color")), None)
                rgb = sample(start.get("StartColor"), size=3) if start else [1.0, 1.0, 1.0]
                peak = max(rgb) or 1.0
                light = bpy.data.lights.new(name, 'POINT')
                light.color = tuple(c / peak for c in rgb)
                light.shadow_soft_size = 0.05
                obj = bpy.data.objects.new(name, light)
                collection.objects.link(obj)
                obj.parent = anchor
                _drive(light, "energy", rig, control, f"value * {min(peak, 100.0) * 0.5:g}")
                made += 1
                continue
            for path in ((emitter.get("Material") or {}).get("Textures") or {}).values():
                if path not in images:
                    try:
                        images[path] = load_image(path)
                    except Exception:
                        images[path] = None
            # the same particles every time (Python's hash() changes between runs)
            particles = schedule(emitter, seed=zlib.crc32(f"{spawn.get('System')}|{socket}|{emitter.get('Name')}|{number}".encode()))
            if not particles:
                continue
            obj = bpy.data.objects.new(name, _particle_mesh(name, particles))
            collection.objects.link(obj)
            obj.parent = anchor
            material = _material(emitter, images, name)
            obj.data.materials.append(material)
            modifier = obj.modifiers.new("VP Particles", 'NODES')
            modifier.node_group = group.copy()
            modifier.node_group.name = f"VP Particles: {name[6:]}"
            modifier.node_group.nodes["VP Material"].inputs["Material"].default_value = material
            own = modifier.node_group
            multiply = _module(emitter, "SizeMultiplyLife")
            if multiply and multiply.get("LifeMultiplier"):
                for i, axis in enumerate("XY"):
                    if (axis == "X" and multiply.get("MultiplyX", True) is False) or (axis == "Y" and multiply.get("MultiplyY", True) is False):
                        continue
                    mapping = own.nodes["VP Size " + axis].mapping
                    points = mapping.curves[0].points
                    values = [sample(multiply["LifeMultiplier"], k / 8, size=3)[i] for k in range(9)]
                    points[0].location, points[1].location = (0.0, values[0]), (1.0, values[-1])
                    for k in range(1, 8):
                        points.new(k / 8, values[k])
                    mapping.update()
            lock = _module(emitter, "OrientationAxisLock")
            if lock and "ROTATE_X" in str(lock.get("LockAxisFlags", "")):
                own.nodes["VP Axis Lock"].outputs[0].default_value = 1.0
            offset = _module(emitter, "CameraOffset")
            own.nodes["VP Camera Offset"].outputs[0].default_value = sample(offset.get("CameraOffset"))[0] if offset else 0.0
            # seconds since the effect started (its delay), and on/off from the armature
            curve = own.driver_add('nodes["VP Time"].outputs[0].default_value')
            curve.driver.expression = f"frame / {fps:g} - {float(spawn.get('Delay') or 0.0):g}"
            _drive(own, 'nodes["VP Active"].outputs[0].default_value', rig, control)
            made += 1
    rig.update_tag()
    return made
