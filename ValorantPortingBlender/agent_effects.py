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
    if op in (2, 3) and sub and len(entry) >= 2 * sub:  # a random value between two sets (3: "extreme", near enough)
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
    receiver = _module(emitter, "EventReceiverSpawn")
    if receiver and not births:
        # spawned by another emitter's event (a loop that follows its start, as that one dies):
        # here simply there, for as long as the effect is on
        births = [0.0] * max(int(round(sample(receiver.get("SpawnCount"))[0])), 1)

    lifetime = _module(emitter, "Lifetime")
    size = _module(emitter, "Size")
    rotation = _module(emitter, "Rotation")
    location = _module(emitter, "Location")
    velocity = _module(emitter, "Velocity")
    acceleration = _module(emitter, "Acceleration")
    drag = _module(emitter, "AccelerationDrag")
    sphere = _module(emitter, "LocationPrimitiveSphere")
    mesh_rotation = _module(emitter, "MeshRotation")
    particles = []
    for birth in births:
        life = sample((lifetime or {}).get("Lifetime"), rng=rng)[0] if lifetime else (0.0 if receiver else 1.0)
        forever = life <= 0.0
        particles.append(dict(
            birth=-1.0 if forever else birth,
            life=1e9 if forever else life,
            size=(lambda xyz: (xyz[0] * UNIT, (xyz[1] or xyz[0]) * UNIT))(sample((size or {}).get("StartSize"), rng=rng, size=3)) if size else (0.1, 0.1),
            rotation=sample((rotation or {}).get("StartRotation"), rng=rng)[0] * 2 * math.pi if rotation else 0.0,
            location=_vector(sample((location or {}).get("StartLocation"), rng=rng, size=3)) if location else (0.0, 0.0, 0.0),
            velocity=_vector(sample((velocity or {}).get("StartVelocity"), rng=rng, size=3)) if velocity else (0.0, 0.0, 0.0),
            acceleration=_vector(sample((acceleration or {}).get("Acceleration"), rng=rng, size=3)) if acceleration else (0.0, 0.0, 0.0),
            drag=sample((drag or {}).get("DragCoefficientRaw") or (drag or {}).get("DragCoefficient"), rng=rng)[0] if drag else 0.0,
            seed=rng.random(),
            mesh_rotation=(lambda r: (r[0] * 2 * math.pi, -r[1] * 2 * math.pi, -r[2] * 2 * math.pi))(
                sample(mesh_rotation.get("StartRotation"), rng=rng, size=3)) if mesh_rotation else (0.0, 0.0, 0.0)))
        if sphere:
            # somewhere in a sphere around the emitter (and moving out of it, if the module says so)
            radius = sample(sphere.get("StartRadius"), rng=rng)[0] * UNIT
            while True:
                d = (rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))
                if 0.0 < sum(c * c for c in d) <= 1.0:
                    break
            p = particles[-1]
            p["location"] = tuple(a + c * radius for a, c in zip(p["location"], d))
            if sphere.get("bVelocity"):
                scale = sample(sphere.get("VelocityScale"), rng=rng)[0] if sphere.get("VelocityScale") else 1.0
                p["velocity"] = tuple(v + c * radius * scale for v, c in zip(p["velocity"], d))
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
                            ("vp_seed", 'FLOAT'), ("vp_velocity", 'FLOAT_VECTOR'), ("vp_acceleration", 'FLOAT_VECTOR'), ("vp_drag", 'FLOAT'), ("vp_mesh_rotation", 'FLOAT_VECTOR')):
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
    existing = bpy.data.node_groups.get(name)
    if existing is not None:
        if existing.nodes.get("VP Spin") is not None:
            return existing
        existing.name = name + " (older add-on)"  # made by an older version: built again

    group = bpy.data.node_groups.new(name, 'GeometryNodeTree')
    face = group.interface
    face.new_socket("Geometry", in_out='INPUT', socket_type='NodeSocketGeometry')
    face.new_socket("Geometry", in_out='OUTPUT', socket_type='NodeSocketGeometry')
    nodes, links = group.nodes, group.links
    inputs, output = nodes.new("NodeGroupInput"), nodes.new("NodeGroupOutput")
    # set per emitter (each has its own copy of this group): seconds since the effect started, on/off, camera offset
    values = {}
    # (and the timeline's seconds and a turn per second: smoke that keeps swirling while its effect is held)
    for value_name, default in (("Time", 0.0), ("Active", 1.0), ("Camera Offset", 0.0), ("Pivot X", 0.0), ("Pivot Y", 0.0),
                                ("Clock", 0.0), ("Spin", 0.0)):
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
    looped = math_node('ADD', CYCLE, math_node('FLOORED_MODULO', values["Time"], CYCLE))
    loop = nodes.new("ShaderNodeValue")
    loop.name = loop.label = "VP Loop"  # 0: a one-shot emitter (played once from its start)
    loop.outputs[0].default_value = 1.0
    timing = nodes.new("GeometryNodeSwitch")
    timing.input_type = 'FLOAT'
    links.new(math_node('GREATER_THAN', loop.outputs[0], 0.5), timing.inputs["Switch"])
    links.new(values["Time"], timing.inputs["False"])
    links.new(looped, timing.inputs["True"])
    time = timing.outputs[0]
    age = math_node('SUBTRACT', time, attribute("vp_birth"))
    life = attribute("vp_life")
    alive = math_node('MULTIPLY', math_node('MULTIPLY', math_node('GREATER_THAN', age, 0.0), math_node('LESS_THAN', age, life)),
                      math_node('MULTIPLY', math_node('GREATER_THAN', values["Active"], 0.5), math_node('GREATER_THAN', values["Time"], -0.0001)))
    delete = nodes.new("GeometryNodeDeleteGeometry")
    delete.domain = 'POINT'
    links.new(inputs.outputs["Geometry"], delete.inputs["Geometry"])
    links.new(math_node('LESS_THAN', alive, 0.5), delete.inputs["Selection"])
    points = nodes.new("GeometryNodeMeshToPoints")
    links.new(delete.outputs["Geometry"], points.inputs["Mesh"])

    # moving with its velocity; pulled toward the camera by the game's camera offset
    position = nodes.new("GeometryNodeInputPosition")
    # moved by its velocity (slowed by drag: v (1 - e^-kt) / k) and its acceleration (a t^2 / 2)
    drag = attribute("vp_drag")
    dragged = math_node('DIVIDE', math_node('SUBTRACT', 1.0, math_node('EXPONENT', math_node('MULTIPLY', math_node('MULTIPLY', drag, -1.0), age))),
                        math_node('MAXIMUM', drag, 0.0001))
    travel = nodes.new("GeometryNodeSwitch")
    travel.input_type = 'FLOAT'
    links.new(math_node('GREATER_THAN', drag, 0.0001), travel.inputs["Switch"])
    links.new(age, travel.inputs["False"])
    links.new(dragged, travel.inputs["True"])
    moved = vector_math('ADD', position.outputs[0], vector_math('SCALE', attribute("vp_velocity", 'FLOAT_VECTOR'), scale=travel.outputs[0]))
    moved = vector_math('ADD', moved, vector_math('SCALE', attribute("vp_acceleration", 'FLOAT_VECTOR'),
                                                  scale=math_node('MULTIPLY', math_node('MULTIPLY', age, age), 0.5)))
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
    # moved off its centre by the game's pivot offset (KAY/O's sparks start at his elbow and reach out from it)
    pivot = nodes.new("ShaderNodeCombineXYZ")
    links.new(values["Pivot X"], pivot.inputs[0])
    links.new(values["Pivot Y"], pivot.inputs[1])
    shifted = nodes.new("GeometryNodeTransform")
    links.new(card.outputs["Mesh"], shifted.inputs["Geometry"])
    links.new(pivot.outputs[0], shifted.inputs["Translation"])
    store_uv = nodes.new("GeometryNodeStoreNamedAttribute")
    store_uv.data_type = 'FLOAT2'
    store_uv.domain = 'CORNER'
    store_uv.inputs["Name"].default_value = "vp_uv"
    links.new(shifted.outputs[0], store_uv.inputs["Geometry"])
    links.new(card.outputs["UV Map"], store_uv.inputs["Value"])
    # (or the emitter's mesh: mesh particles; object and UV map name set per emitter)
    mesh_info = nodes.new("GeometryNodeObjectInfo")
    mesh_info.name = "VP Mesh"
    mesh_info.transform_space = 'ORIGINAL'
    mesh_uv = nodes.new("GeometryNodeInputNamedAttribute")
    mesh_uv.name = "VP Mesh UV"
    mesh_uv.data_type = 'FLOAT_VECTOR'
    mesh_uv.inputs["Name"].default_value = "UVMap"
    store_mesh_uv = nodes.new("GeometryNodeStoreNamedAttribute")
    store_mesh_uv.data_type = 'FLOAT2'
    store_mesh_uv.domain = 'CORNER'
    store_mesh_uv.inputs["Name"].default_value = "vp_uv"
    links.new(mesh_info.outputs["Geometry"], store_mesh_uv.inputs["Geometry"])
    links.new(mesh_uv.outputs["Attribute"], store_mesh_uv.inputs["Value"])
    use_mesh = nodes.new("ShaderNodeValue")
    use_mesh.name = use_mesh.label = "VP Use Mesh"
    use_mesh.outputs[0].default_value = 0.0
    shape = nodes.new("GeometryNodeSwitch")
    shape.input_type = 'GEOMETRY'
    links.new(math_node('GREATER_THAN', use_mesh.outputs[0], 0.5), shape.inputs["Switch"])
    links.new(store_uv.outputs[0], shape.inputs["False"])
    links.new(store_mesh_uv.outputs[0], shape.inputs["True"])
    set_material = nodes.new("GeometryNodeSetMaterial")
    set_material.name = "VP Material"  # set per emitter (each has its own copy of this group)
    links.new(shape.outputs[0], set_material.inputs["Geometry"])

    facing = nodes.new("FunctionNodeAlignRotationToVector")
    facing.axis = 'Z'
    links.new(to_camera, facing.inputs["Vector"])
    # locked to the emitter's X axis (the game's OrientationAxisLock ROTATE_X): along X, turned to the camera around it
    along = nodes.new("FunctionNodeAlignRotationToVector")
    along.axis = 'X'
    # (or along its velocity: the game's velocity-aligned sprites)
    by_velocity = nodes.new("ShaderNodeValue")
    by_velocity.name = by_velocity.label = "VP Velocity Aligned"
    by_velocity.outputs[0].default_value = 0.0
    lock_axis = nodes.new("ShaderNodeMix")
    lock_axis.data_type = 'VECTOR'
    links.new(by_velocity.outputs[0], lock_axis.inputs[0])
    lock_axis.inputs[4].default_value = (1.0, 0.0, 0.0)
    links.new(attribute("vp_velocity", 'FLOAT_VECTOR'), lock_axis.inputs[5])
    links.new(lock_axis.outputs[1], along.inputs["Vector"])
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
    store_seed = nodes.new("GeometryNodeStoreNamedAttribute")
    store_seed.data_type = 'FLOAT'
    store_seed.domain = 'POINT'
    store_seed.inputs["Name"].default_value = "vp_random"
    links.new(store_age.outputs[0], store_seed.inputs["Geometry"])
    links.new(attribute("vp_seed"), store_seed.inputs["Value"])
    links.new(store_seed.outputs[0], instance.inputs["Points"])
    links.new(set_material.outputs[0], instance.inputs["Instance"])
    fixed = nodes.new("ShaderNodeValue")
    fixed.name = fixed.label = "VP Mesh Fixed"  # 1: a mesh not turned to the camera (its own start rotation)
    fixed.outputs[0].default_value = 0.0
    own_rotation = nodes.new("FunctionNodeEulerToRotation")
    links.new(attribute("vp_mesh_rotation", 'FLOAT_VECTOR'), own_rotation.inputs[0])
    orient = nodes.new("GeometryNodeSwitch")
    orient.input_type = 'ROTATION'
    links.new(math_node('GREATER_THAN', fixed.outputs[0], 0.5), orient.inputs["Switch"])
    links.new(turned.outputs[0], orient.inputs["False"])
    links.new(own_rotation.outputs[0], orient.inputs["True"])
    # turned round the effect's up axis as the timeline plays (Omen's shadows circling him)
    swirl = nodes.new("FunctionNodeAxisAngleToRotation")
    swirl.inputs["Axis"].default_value = (0.0, 0.0, 1.0)
    links.new(math_node('MULTIPLY', values["Clock"], values["Spin"]), swirl.inputs["Angle"])
    swirled = nodes.new("FunctionNodeRotateRotation")
    swirled.rotation_space = 'GLOBAL'
    links.new(orient.outputs[0], swirled.inputs["Rotation"])
    links.new(swirl.outputs[0], swirled.inputs["Rotate By"])
    links.new(swirled.outputs[0], instance.inputs["Rotation"])
    links.new(scale, instance.inputs["Scale"])
    links.new(instance.outputs[0], output.inputs[0])
    return group


_alpha_cache = {}


def _has_alpha(image):
    """Whether an image's alpha holds a shape (else its brightness does: smoke, wisps)."""
    if image is None:
        return False
    if image.name in _alpha_cache:
        return _alpha_cache[image.name]
    found = False
    try:
        if image.channels == 4 and image.size[0] > 0:
            import numpy
            pixels = numpy.empty(image.size[0] * image.size[1] * 4, dtype=numpy.float32)
            image.pixels.foreach_get(pixels)
            alpha = pixels[3::4]
            found = bool(alpha.min() < 0.9)
    except Exception:
        found = True
    _alpha_cache[image.name] = found
    return found


# Yoru's rift: which end of its mesh (its V) it tears open from
TEAR_FROM_HIGH_V = False

# Omen's shadow smoke materials (their look is made in their shaders: see _material)
SHADOW_MATERIALS = {"ground_power", "ground_power_activate_ALLY", "back_power", "specter_ground_tether", "Radial_Mesh",
                    "air_splitting"}


def _material(emitter, images, name, boost=1.0):
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
    # flipbook frames: the emitter's own grid (SubImages), else the material's; in order over the particle's life
    # (up to the last frame its SubUV module reaches), or one at random
    required = _module(emitter, "Required") or {}
    face = (spec.get("BaseMaterial") or "").lower().startswith("m_faceexpressions")
    columns = int(required.get("SubImages_Horizontal") or scalars.get("FlipbookColumns") or (scalars.get("Columns") if face else 0) or 1)
    rows = int(required.get("SubImages_Vertical") or scalars.get("FlipbookRows") or (scalars.get("Rows") if face else 0) or 1)
    frames = max(rows * columns, 1)
    if frames > 1:
        subuv = _module(emitter, "SubUV")
        last = frames - 1
        if subuv and _table(subuv.get("SubImageIndex"))[0]:
            last = min(int(round(max(_table(subuv.get("SubImageIndex"))[0]))), frames - 1)
        if "Random" in str(required.get("InterpolationMethod", "")):
            frame = math_node('FLOOR', math_node('MULTIPLY', instancer("vp_random").outputs["Fac"], frames - 0.001))
        else:
            frame = math_node('MINIMUM', math_node('FLOOR', math_node('MULTIPLY', age, last + 1)), last)
        column = math_node('FLOORED_MODULO', frame, columns)
        row = math_node('SUBTRACT', rows - 1, math_node('FLOOR', math_node('DIVIDE', frame, columns)))
        split = nodes.new("ShaderNodeSeparateXYZ")
        links.new(uv, split.inputs[0])
        combine = nodes.new("ShaderNodeCombineXYZ")
        links.new(math_node('DIVIDE', math_node('ADD', split.outputs[0], column), columns), combine.inputs[0])
        links.new(math_node('DIVIDE', math_node('ADD', split.outputs[1], row), rows), combine.inputs[1])
        uv = combine.outputs[0]

    textures = spec.get("Textures") or {}
    # its sprite: the first texture that isn't a normal map (Reyna's ult embers list a sphere normal first)
    # (and not a colour gradient when it has a shape texture too: KAY/O's sparks list their gradient first)
    shapes = [path for key, path in textures.items() if "normal" not in (key + path).lower()]
    # (a lightning texture over a helper mask: Neon's arm bolts list HardEdges_TEX first)
    texture_path = next((path for path in shapes if any(w in path.lower() for w in ("lightning", "bolt"))), None) or         next((path for path in shapes if "gradient" not in path.lower() and "edges" not in path.lower()), None) or         next((path for path in shapes if "gradient" not in path.lower()), next(iter(shapes), next(iter(textures.values()), None)))
    image = images.get(texture_path) if texture_path else None
    colour = None
    # smoke (Reyna's ult wisps: Vampire_Wisp_Smoke_B): the game shapes it with a flow map in its shader; here a soft
    # round puff of its noise texture, drifting up over the puff's life, and dark
    noise_path = next((path for path in textures.values() if "NoiseDissolve" in path and images.get(path) is not None), None)
    # Omen's shadow (his ult's smoke meshes: a shape mask, wisps of noise drifting through it), dark violet
    shadow = (spec.get("BaseMaterial") or "") in SHADOW_MATERIALS
    # (Viper's poison gas: drawn the same way, its own soft cloud as the noise, glowing green)
    gas = "PoisonGas" in (spec.get("Name") or "") and images.get(textures.get("Emissive_Base")) is not None
    if gas:
        noise_path = textures["Emissive_Base"]
    smoke = ("Smoke" in (spec.get("BaseMaterial") or "") or gas) and noise_path is not None
    if shadow:
        mask_path = next((path for key, path in textures.items() if "mask" in key.lower() and "body" not in key.lower()
                          and "arm" not in key.lower() and images.get(path) is not None), None) or             next((path for path in textures.values() if images.get(path) is not None), None)
        wisp_path = next((path for key, path in textures.items() if key.lower().startswith("wraith_4xtile") and images.get(path) is not None), None)
        shape = nodes.new("ShaderNodeTexImage")
        shape.image = images.get(mask_path)
        shape.extension = 'CLIP'
        links.new(uv, shape.inputs[0])
        mask = nodes.new("ShaderNodeSeparateColor")
        links.new(shape.outputs["Color"], mask.inputs[0])
        mask = math_node('MULTIPLY', mask.outputs[0], shape.outputs["Alpha"]) if _has_alpha(shape.image) else mask.outputs[0]
        if wisp_path is not None:
            moved = nodes.new("ShaderNodeVectorMath")
            moved.operation = 'MULTIPLY_ADD'
            links.new(uv, moved.inputs[0])
            moved.inputs[1].default_value = (1.0, 1.0, 1.0)
            shift = nodes.new("ShaderNodeCombineXYZ")
            # drifting with the particle's age and with the timeline (it keeps moving while the effect is held)
            clock = nodes.new("ShaderNodeValue")
            clock.name = clock.label = "VP Clock"
            links.new(math_node('ADD', math_node('MULTIPLY', instancer("vp_random").outputs["Fac"], 3.7),
                                math_node('ADD', math_node('MULTIPLY', age, 0.3), math_node('MULTIPLY', clock.outputs[0], 0.08))), shift.inputs[0])
            links.new(math_node('ADD', math_node('MULTIPLY', age, -0.6), math_node('MULTIPLY', clock.outputs[0], -0.15)), shift.inputs[1])
            links.new(shift.outputs[0], moved.inputs[2])
            wisps = nodes.new("ShaderNodeTexImage")
            wisps.image = images[wisp_path]
            links.new(moved.outputs[0], wisps.inputs[0])
            channels = nodes.new("ShaderNodeSeparateColor")
            links.new(wisps.outputs["Color"], channels.inputs[0])
            mask = math_node('MULTIPLY', mask, math_node('POWER', math_node('MULTIPLY', math_node('SUBTRACT', channels.outputs[0], 0.35), 2.5, clamp=True), 1.5))
        mask = math_node('MULTIPLY', mask, 0.85)
        colour = None
    elif smoke:
        centre = nodes.new("ShaderNodeVectorMath")
        centre.operation = 'DISTANCE'
        links.new(uv, centre.inputs[0])
        centre.inputs[1].default_value = (0.5, 0.5, 0.0)
        puff = math_node('POWER', math_node('SUBTRACT', 1.0, math_node('DIVIDE', centre.outputs["Value"], 0.5), clamp=True), 1.5)
        random = instancer("vp_random").outputs["Fac"]
        shift = nodes.new("ShaderNodeCombineXYZ")
        links.new(math_node('MULTIPLY', random, 7.31), shift.inputs[0])
        links.new(math_node('SUBTRACT', math_node('MULTIPLY', random, 3.17), math_node('MULTIPLY', age, 0.6)), shift.inputs[1])
        moved = nodes.new("ShaderNodeVectorMath")
        moved.operation = 'MULTIPLY_ADD'
        links.new(uv, moved.inputs[0])
        moved.inputs[1].default_value = (1.5, 1.5, 1.0)
        links.new(shift.outputs[0], moved.inputs[2])
        noise = nodes.new("ShaderNodeTexImage")
        noise.image = images[noise_path]
        links.new(moved.outputs[0], noise.inputs[0])
        channels = nodes.new("ShaderNodeSeparateColor")
        links.new(noise.outputs["Color"], channels.inputs[0])
        wisps = math_node('MULTIPLY', math_node('SUBTRACT', channels.outputs[0], 0.15), 1.6, clamp=True)
        mask = math_node('MULTIPLY', math_node('MULTIPLY', puff, wisps), float(emitter.get("Opacity") or 1.0))
    elif (spec.get("BaseMaterial") or "") == "m_X_portalTear_01" and images.get(textures.get("Alpha")) is not None:
        # Yoru's rift (m_X_portalTear_01): its jagged shape (Alpha), dark inside (BaseColor) with blue crystal light
        # drifting in its depth (NoiseBack in DepthColor / RadianiteColor), its torn edge glowing (AlphaB in EdgeColor)
        def rgb(name, fallback):
            value = vectors.get(name) or fallback
            return (float(value[0]), float(value[1]), float(value[2]), 1.0)

        def sample(key, vector, extension='CLIP'):
            node = nodes.new("ShaderNodeTexImage")
            node.image = images.get(textures.get(key))
            node.extension = extension
            links.new(vector, node.inputs[0])
            return node

        shape = nodes.new("ShaderNodeSeparateColor")
        links.new(sample("Alpha", uv).outputs["Color"], shape.inputs[0])
        edge = nodes.new("ShaderNodeSeparateColor")
        links.new((sample("AlphaB", uv) if images.get(textures.get("AlphaB")) else sample("Alpha", uv)).outputs["Color"], edge.inputs[0])
        drift = nodes.new("ShaderNodeVectorMath")
        drift.operation = 'MULTIPLY_ADD'
        links.new(uv, drift.inputs[0])
        drift.inputs[1].default_value = (1.5, 1.5, 1.0)
        # (with the timeline, "VP Clock": it keeps moving while the effect is held open)
        clock = nodes.new("ShaderNodeValue")
        clock.name = clock.label = "VP Clock"
        offset = nodes.new("ShaderNodeCombineXYZ")
        links.new(math_node('MULTIPLY', clock.outputs[0], 0.12), offset.inputs[0])
        links.new(math_node('MULTIPLY', clock.outputs[0], 0.05), offset.inputs[1])
        links.new(offset.outputs[0], drift.inputs[2])
        drift_b = nodes.new("ShaderNodeVectorMath")
        drift_b.operation = 'MULTIPLY_ADD'
        links.new(uv, drift_b.inputs[0])
        drift_b.inputs[1].default_value = (2.3, 2.3, 1.0)
        offset_b = nodes.new("ShaderNodeCombineXYZ")
        links.new(math_node('MULTIPLY', clock.outputs[0], -0.09), offset_b.inputs[0])
        links.new(math_node('MULTIPLY', clock.outputs[0], 0.07), offset_b.inputs[1])
        links.new(offset_b.outputs[0], drift_b.inputs[2])
        if images.get(textures.get("NoiseBack")):
            layer_a, layer_b = nodes.new("ShaderNodeSeparateColor"), nodes.new("ShaderNodeSeparateColor")
            links.new(sample("NoiseBack", drift.outputs[0], 'REPEAT').outputs["Color"], layer_a.inputs[0])
            links.new(sample("NoiseBack", drift_b.outputs[0], 'REPEAT').outputs["Color"], layer_b.inputs[0])
            crystals = math_node('MINIMUM', math_node('ADD', math_node('POWER', layer_a.outputs[2], 3.0),
                                                       math_node('MULTIPLY', math_node('POWER', layer_b.outputs[1], 4.0), 0.7)), 1.0)
        else:
            crystals = 0.3
        inside = nodes.new("ShaderNodeMix")
        inside.data_type = 'RGBA'
        links.new(crystals, inside.inputs[0]) if not isinstance(crystals, float) else None
        inside.inputs[6].default_value = rgb("BaseColor", (0.0, 0.006, 0.02))
        inside.inputs[7].default_value = rgb("DepthColor", (0.02, 0.05, 1.0))
        rim = nodes.new("ShaderNodeMix")
        rim.data_type = 'RGBA'
        flicker = nodes.new("ShaderNodeTexNoise")
        flicker.noise_dimensions = '2D'
        flicker.inputs["Scale"].default_value = 3.0
        flick_uv = nodes.new("ShaderNodeCombineXYZ")
        links.new(math_node('MULTIPLY', clock.outputs[0], 4.0), flick_uv.inputs[0])
        links.new(flick_uv.outputs[0], flicker.inputs["Vector"])
        edge_amount = math_node('MULTIPLY', edge.outputs[0], math_node('ADD', 1.0, math_node('MULTIPLY', flicker.outputs["Fac"], 1.0)))
        links.new(math_node('MINIMUM', edge_amount, 1.0), rim.inputs[0])
        links.new(inside.outputs[2], rim.inputs[6])
        rim.inputs[7].default_value = rgb("EdgeColor", (0.33, 0.81, 1.0))
        # it tears open along its length (right to left as he looks, 0 -> 0.4 of its control), stays open, and pinches
        # shut across its width (0.75 -> 1); "VP Reveal" is the control, driven after the import
        reveal = nodes.new("ShaderNodeValue")
        reveal.name = reveal.label = "VP Reveal"
        reveal.outputs[0].default_value = 0.5
        split = nodes.new("ShaderNodeSeparateXYZ")
        links.new(uv, split.inputs[0])
        jag = nodes.new("ShaderNodeTexNoise")
        jag.inputs["Scale"].default_value = 6.0
        links.new(uv, jag.inputs["Vector"])
        along = math_node('ADD', math_node('SUBTRACT', 1.0, split.outputs[1]) if TEAR_FROM_HIGH_V else split.outputs[1],
                          math_node('MULTIPLY', math_node('SUBTRACT', jag.outputs["Fac"], 0.5), 0.12))
        opened = math_node('MULTIPLY', math_node('DIVIDE', reveal.outputs[0], 0.4), 1.15)
        front = math_node('SUBTRACT', opened, along)  # > 0: torn open
        shown = math_node('MINIMUM', math_node('MAXIMUM', math_node('MULTIPLY', front, 25.0), 0.0), 1.0)
        tip = math_node('MULTIPLY', math_node('MULTIPLY', shown, math_node('SUBTRACT', 1.0, math_node('MINIMUM', math_node('MULTIPLY', front, 8.0), 1.0))),
                        math_node('LESS_THAN', reveal.outputs[0], 0.4))  # (the tearing edge, bright while it opens)
        closing = math_node('MINIMUM', math_node('MAXIMUM', math_node('DIVIDE', math_node('SUBTRACT', reveal.outputs[0], 0.75), 0.25), 0.0), 1.0)
        across = math_node('ABSOLUTE', math_node('MULTIPLY', math_node('SUBTRACT', split.outputs[0], 0.5), 2.0))
        pinched = math_node('MINIMUM', math_node('MAXIMUM', math_node('MULTIPLY', math_node('SUBTRACT', math_node('SUBTRACT', 1.0, closing), across), 12.0), 0.0), 1.0)
        bright = nodes.new("ShaderNodeMix")
        bright.data_type = 'RGBA'
        links.new(math_node('MAXIMUM', tip, math_node('MULTIPLY', closing, 0.1)), bright.inputs[0])
        links.new(rim.outputs[2], bright.inputs[6])
        bright.inputs[7].default_value = (0.25, 0.75, 1.0, 1.0)
        colour = bright.outputs[2]
        mask = math_node('MULTIPLY', math_node('MINIMUM', math_node('MULTIPLY', shape.outputs[0], 1.2), 1.0), math_node('MULTIPLY', shown, pinched))
        boost = boost * 2.0
        life_alpha = 1.0  # (its reveal is its fade; its colour its own)
        white = nodes.new("ShaderNodeRGB")
        white.outputs[0].default_value = (1.0, 1.0, 1.0, 1.0)
        life_colour = white.outputs[0]
    elif (spec.get("BaseMaterial") or "") == "Sprinter_CS_Swipe":
        # Neon's energy trails (character select): a streak running along the mesh over its life, a wiggly band
        # (TextureA_Wipe) of cyan energy (ColorA) with a hot yellow leading edge (TextureA_Edge_Color)
        def rgb(name, fallback):
            value = vectors.get(name) or fallback
            peak = max(float(value[0]), float(value[1]), float(value[2]), 1.0)
            return (float(value[0]) / peak, float(value[1]) / peak, float(value[2]) / peak, 1.0)

        split = nodes.new("ShaderNodeSeparateXYZ")
        links.new(uv, split.inputs[0])
        along = split.outputs[0]
        if images.get(textures.get("TextureA_Wipe")) is not None:
            wiggle = nodes.new("ShaderNodeTexImage")
            wiggle.image = images[textures["TextureA_Wipe"]]
            links.new(uv, wiggle.inputs[0])
            channels = nodes.new("ShaderNodeSeparateColor")
            links.new(wiggle.outputs["Color"], channels.inputs[0])
            along = math_node('ADD', along, math_node('MULTIPLY', math_node('SUBTRACT', channels.outputs[0], 0.5), 0.12))
        front = math_node('SUBTRACT', math_node('MULTIPLY', age, 1.5), 0.2)
        behind = math_node('SUBTRACT', front, along)  # > 0: the streak has passed here
        tail = math_node('MULTIPLY', math_node('GREATER_THAN', behind, 0.0),
                         math_node('POWER', math_node('SUBTRACT', 1.0, math_node('DIVIDE', behind, 0.22), clamp=True), 2.0))
        edge = math_node('MULTIPLY', math_node('GREATER_THAN', behind, 0.0), math_node('SUBTRACT', 1.0, math_node('DIVIDE', behind, 0.06), clamp=True))
        across = math_node('SUBTRACT', 1.0, math_node('ABSOLUTE', math_node('MULTIPLY', math_node('SUBTRACT', split.outputs[1], 0.5), 2.0)), clamp=True)
        mixed = nodes.new("ShaderNodeMix")
        mixed.data_type = 'RGBA'
        links.new(edge, mixed.inputs[0])
        mixed.inputs[6].default_value = rgb("ColorA", (0.0, 0.57, 1.0))
        mixed.inputs[7].default_value = rgb("TextureA_Edge_Color", (1.0, 0.75, 0.0))
        colour = mixed.outputs[2]
        mask = math_node('MULTIPLY', math_node('MAXIMUM', math_node('MULTIPLY', tail, 0.6), edge), math_node('POWER', across, 2.0))
        life_alpha = 1.0
        white = nodes.new("ShaderNodeRGB")
        white.outputs[0].default_value = (1.0, 1.0, 1.0, 1.0)
        life_colour = white.outputs[0]
        boost = boost * 3.0
    elif face and images.get(textures.get("Flipbook")) is not None:
        # KAY/O's face screen (m_faceExpressions_flipbook): the expression's lines (R) and their halo (G) in ColorA, on
        # a round purple glow (ColorRadialGradient, Radius); his face as a 10 x 10 flipbook
        flipbook = nodes.new("ShaderNodeTexImage")
        flipbook.image = images[textures["Flipbook"]]
        flipbook.extension = 'CLIP'
        links.new(uv, flipbook.inputs[0])
        channels = nodes.new("ShaderNodeSeparateColor")
        links.new(flipbook.outputs["Color"], channels.inputs[0])
        lines, halo = channels.outputs[0], channels.outputs[1]
        centre = nodes.new("ShaderNodeVectorMath")
        centre.operation = 'DISTANCE'
        links.new(uv_node.outputs["Vector"], centre.inputs[0])
        centre.inputs[1].default_value = (0.5, 0.5, 0.0)
        radius = float(scalars.get("Radius", 0.3)) + float(scalars.get("RadiusGradient", 0.4))
        screen = math_node('MULTIPLY', math_node('POWER', math_node('SUBTRACT', 1.0, math_node('DIVIDE', centre.outputs["Value"], radius), clamp=True), 1.5), 0.6)

        def rgb(name, fallback):
            value = vectors.get(name) or fallback
            return (float(value[0]), float(value[1]), float(value[2]), 1.0)

        mixed = nodes.new("ShaderNodeMix")
        mixed.data_type = 'RGBA'
        mixed.inputs[6].default_value = rgb("ColorRadialGradient", (0.62, 0.0, 0.91))
        mixed.inputs[7].default_value = rgb("ColorA", (1.0, 0.0, 1.0))
        links.new(math_node('MAXIMUM', lines, math_node('MULTIPLY', halo, 0.6)), mixed.inputs[0])
        whiter = nodes.new("ShaderNodeMix")
        whiter.data_type = 'RGBA'
        links.new(math_node('POWER', lines, 3.0), whiter.inputs[0])
        links.new(mixed.outputs[2], whiter.inputs[6])
        whiter.inputs[7].default_value = (1.0, 0.85, 1.0, 1.0)
        colour = whiter.outputs[2]
        mask = math_node('MAXIMUM', math_node('MAXIMUM', screen, lines), math_node('MULTIPLY', halo, 0.8))
    elif "Emissive_Base" in textures or "Alpha_Base" in textures:
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
        texture = nodes.new("ShaderNodeTexImage")
        texture.image = image
        texture.extension = 'CLIP'
        links.new(uv, texture.inputs[0])
        channels = nodes.new("ShaderNodeSeparateColor")
        links.new(texture.outputs["Color"], channels.inputs[0])
        # its shape: the alpha, or (a texture without one) its brightness
        # (its dark background, often a dark grey, left out)
        mask = texture.outputs["Alpha"] if _has_alpha(image) else             math_node('MULTIPLY', math_node('SUBTRACT', channels.outputs[0], 0.25), 1.333, clamp=True)
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
    if shadow and not emitter.get("Tint"):
        emitter = dict(emitter, Tint="241433")  # (his Spectre colours)
    if emitter.get("Tint"):
        # (a grey texture the game colours inside its material)
        hex_ = emitter["Tint"]
        rgb = [((int(hex_[i:i + 2], 16) / 255.0 + 0.055) / 1.055) ** 2.4 for i in (0, 2, 4)]
        # its hue replaced (the game's own colour over life can be another hue: KAY/O's ult sparks are teal), its
        # brightness kept
        channels = nodes.new("ShaderNodeSeparateColor")
        links.new(life_colour, channels.inputs[0])
        value = math_node('MAXIMUM', math_node('MAXIMUM', channels.outputs[0], channels.outputs[1]), channels.outputs[2])
        tinted = nodes.new("ShaderNodeVectorMath")
        tinted.operation = 'SCALE'
        tinted.inputs[0].default_value = rgb
        links.new(value, tinted.inputs["Scale"])
        life_colour = tinted.outputs[0]
    links.new(life_colour, tint.inputs[6])
    if colour is not None:
        links.new(colour, tint.inputs[7])
    else:
        tint.inputs[7].default_value = (1.0, 1.0, 1.0, 1.0)
    glow = nodes.new("ShaderNodeEmission")
    links.new(tint.outputs[2], glow.inputs["Color"])
    opacity = math_node('MULTIPLY', mask, life_alpha, clamp=False) if not isinstance(life_alpha, float) else mask
    # (an effect held at a strength, like Viper's hand smoke: its control fades it, "VP Fade")
    fade = nodes.new("ShaderNodeValue")
    fade.name = fade.label = "VP Fade"
    fade.outputs[0].default_value = 1.0
    opacity = math_node('MULTIPLY', opacity, fade.outputs[0])
    # (smoke: barely lit, it darkens what's behind it)
    links.new(math_node('MULTIPLY', opacity, 0.04 if smoke and not gas else 2.0 * boost if gas else 0.15 if shadow else min(brightest, BRIGHTEST) * boost), glow.inputs["Strength"])
    if boost > 1.0:
        opacity = math_node('MULTIPLY', opacity, min(boost, 2.0))  # (and less see-through)
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
    """Connects the effects to their controls once the import is over: Blender's evaluated copy of the armature
    doesn't have its new controls until then, and drivers made before that are marked invalid (and stay so).
    (As a timer: runs once.)"""
    rigs = {id(rig): rig for rig, *_ in _pending}
    for rig in rigs.values():
        try:
            rig.update_tag()
        except ReferenceError:
            pass
    try:
        bpy.context.view_layer.update()
    except Exception:
        pass
    while _pending:
        rig, target, path, variables, expression = _pending.pop(0)
        try:
            curve = target.driver_add(path)
            driver = curve.driver
            driver.type = 'SCRIPTED'
            for name, prop in variables:
                var = driver.variables.new()
                var.name = name
                var.type = 'SINGLE_PROP'
                var.targets[0].id_type = 'OBJECT'
                var.targets[0].id = rig
                var.targets[0].data_path = f'["{prop}"]'
            driver.expression = expression
            driver.is_valid = True
        except ReferenceError:
            pass
        except Exception as error:  # (one bad driver doesn't stop the others)
            print(f"VP effects: no driver for {path}: {error}")
    return None


def _drive(target, path, armature, prop, expression="value", variables=None):
    """A driver from the armature's control, made once the import is over (see revalidate)."""
    _pending.append((armature, target, path, variables or [("value", prop)], expression))


def _spark_material():
    """Yoru's rift sparks: little glowing motes, cyan-white at their core, fading out over their life."""
    name = "VP FX rift sparks"
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
    fade = nodes.new("ShaderNodeAttribute")
    fade.attribute_type = 'INSTANCER'
    fade.attribute_name = "vp_fade"
    tone = nodes.new("ShaderNodeAttribute")
    tone.attribute_type = 'INSTANCER'
    tone.attribute_name = "vp_tone"
    colour = nodes.new("ShaderNodeMix")
    colour.data_type = 'RGBA'
    links.new(tone.outputs["Fac"], colour.inputs[0])
    colour.inputs[6].default_value = (0.15, 0.35, 1.0, 1.0)
    colour.inputs[7].default_value = (0.75, 0.95, 1.0, 1.0)
    glow = nodes.new("ShaderNodeEmission")
    links.new(colour.outputs[2], glow.inputs["Color"])
    strength = nodes.new("ShaderNodeMath")
    strength.operation = 'MULTIPLY'
    links.new(fade.outputs["Fac"], strength.inputs[0])
    strength.inputs[1].default_value = 9.0
    links.new(strength.outputs[0], glow.inputs["Strength"])
    see_through = nodes.new("ShaderNodeBsdfTransparent")
    add = nodes.new("ShaderNodeAddShader")
    links.new(see_through.outputs[0], add.inputs[0])
    links.new(glow.outputs[0], add.inputs[1])
    links.new(add.outputs[0], output.inputs["Surface"])
    return material


def _add_rift_sparks(group, edge_image):
    """Sparks off Yoru's rift: points on its torn edge (where its edge texture is bright), each a mote drifting out
    from the rift and up over a second, again and again (with the timeline: "VP Clock"); only where it is torn open
    ("VP Rift Reveal", its control, as its material), gone as it pinches shut."""
    nodes, links = group.nodes, group.links
    output = next(n for n in nodes if n.type == 'GROUP_OUTPUT')
    rift = output.inputs[0].links[0].from_socket
    clock = nodes["VP Clock"].outputs[0]
    reveal = nodes.new("ShaderNodeValue")
    reveal.name = reveal.label = "VP Rift Reveal"
    reveal.outputs[0].default_value = 0.5

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

    def clamp01(v):
        return math_node('MINIMUM', math_node('MAXIMUM', v, 0.0), 1.0)

    real = nodes.new("GeometryNodeRealizeInstances")
    links.new(rift, real.inputs["Geometry"])
    spread = nodes.new("GeometryNodeDistributePointsOnFaces")
    links.new(real.outputs[0], spread.inputs["Mesh"])
    spread.inputs["Density"].default_value = 40000.0
    store_normal = nodes.new("GeometryNodeStoreNamedAttribute")
    store_normal.data_type = 'FLOAT_VECTOR'
    store_normal.domain = 'POINT'
    store_normal.inputs["Name"].default_value = "vp_n"
    links.new(spread.outputs["Points"], store_normal.inputs["Geometry"])
    links.new(spread.outputs["Normal"], store_normal.inputs["Value"])
    uv = nodes.new("GeometryNodeInputNamedAttribute")
    uv.data_type = 'FLOAT_VECTOR'
    uv.inputs["Name"].default_value = "vp_uv"
    split = nodes.new("ShaderNodeSeparateXYZ")
    links.new(uv.outputs["Attribute"], split.inputs[0])

    # kept: on the torn edge (its edge texture), where it is open, a share of them at random
    keep = 1.0
    if edge_image is not None:
        sample = nodes.new("GeometryNodeImageTexture")
        sample.inputs["Image"].default_value = edge_image
        sample.extension = 'CLIP'
        links.new(uv.outputs["Attribute"], sample.inputs["Vector"])
        tone = nodes.new("FunctionNodeSeparateColor")
        links.new(sample.outputs["Color"], tone.inputs[0])
        keep = math_node('GREATER_THAN', tone.outputs[0], 0.35)
    along = math_node('SUBTRACT', 1.0, split.outputs[1]) if TEAR_FROM_HIGH_V else split.outputs[1]
    opened = math_node('MULTIPLY', math_node('DIVIDE', reveal.outputs[0], 0.4), 1.15)
    open_here = math_node('GREATER_THAN', math_node('SUBTRACT', opened, along), 0.0)
    closing = clamp01(math_node('DIVIDE', math_node('SUBTRACT', reveal.outputs[0], 0.75), 0.25))
    ident = nodes.new("GeometryNodeInputID")
    def random(seed, low=0.0, high=1.0):
        node = nodes.new("FunctionNodeRandomValue")
        node.data_type = 'FLOAT'
        live = lambda sockets: [x for x in sockets if x.enabled and x.type == 'VALUE']
        links.new(ident.outputs[0], node.inputs["ID"])
        node.inputs["Seed"].default_value = seed
        low_socket, high_socket = [x for x in live(node.inputs) if x.name in ("Min", "Max")][:2]
        low_socket.default_value, high_socket.default_value = low, high
        return live(node.outputs)[0]

    chance = math_node('LESS_THAN', random(11), math_node('SUBTRACT', 1.0, closing))
    shown = math_node('MULTIPLY', math_node('MULTIPLY', keep, open_here), chance)
    drop = nodes.new("GeometryNodeDeleteGeometry")
    drop.domain = 'POINT'
    links.new(store_normal.outputs[0], drop.inputs["Geometry"])
    links.new(math_node('LESS_THAN', shown, 0.5), drop.inputs["Selection"])

    # each mote's life: its own phase and speed; drifting out (the rift's normal, either side) and up, a little sideways

    life = math_node('FRACT', math_node('ADD', math_node('MULTIPLY', clock, random(3, 0.6, 1.4)), random(5)))
    side = math_node('SUBTRACT', math_node('MULTIPLY', math_node('GREATER_THAN', random(7), 0.5), 2.0), 1.0)
    normal = nodes.new("GeometryNodeInputNamedAttribute")
    normal.data_type = 'FLOAT_VECTOR'
    normal.inputs["Name"].default_value = "vp_n"
    out = nodes.new("ShaderNodeVectorMath")
    out.operation = 'SCALE'
    links.new(normal.outputs["Attribute"], out.inputs[0])
    links.new(math_node('MULTIPLY', math_node('MULTIPLY', life, random(9, 0.02, 0.09)), side), out.inputs["Scale"])
    lift = nodes.new("ShaderNodeCombineXYZ")
    links.new(math_node('MULTIPLY', math_node('SUBTRACT', random(13), 0.5), math_node('MULTIPLY', life, 0.04)), lift.inputs[0])
    links.new(math_node('MULTIPLY', math_node('SUBTRACT', random(15), 0.5), math_node('MULTIPLY', life, 0.04)), lift.inputs[1])
    links.new(math_node('MULTIPLY', life, random(17, 0.01, 0.06)), lift.inputs[2])
    move = nodes.new("ShaderNodeVectorMath")
    links.new(out.outputs[0], move.inputs[0])
    links.new(lift.outputs[0], move.inputs[1])
    moved = nodes.new("GeometryNodeSetPosition")
    links.new(drop.outputs[0], moved.inputs["Geometry"])
    links.new(move.outputs[0], moved.inputs["Offset"])
    # bright as it leaves the edge, fading out; some whiter
    fade = math_node('MULTIPLY', math_node('SUBTRACT', 1.0, life), math_node('MINIMUM', math_node('MULTIPLY', life, 8.0), 1.0))
    store_fade = nodes.new("GeometryNodeStoreNamedAttribute")
    store_fade.data_type = 'FLOAT'
    store_fade.domain = 'POINT'
    store_fade.inputs["Name"].default_value = "vp_fade"
    links.new(moved.outputs[0], store_fade.inputs["Geometry"])
    links.new(fade, store_fade.inputs["Value"])
    store_tone = nodes.new("GeometryNodeStoreNamedAttribute")
    store_tone.data_type = 'FLOAT'
    store_tone.domain = 'POINT'
    store_tone.inputs["Name"].default_value = "vp_tone"
    links.new(store_fade.outputs[0], store_tone.inputs["Geometry"])
    links.new(random(19), store_tone.inputs["Value"])
    mote = nodes.new("GeometryNodeMeshIcoSphere")
    mote.inputs["Radius"].default_value = 1.0
    mote.inputs["Subdivisions"].default_value = 1
    on_points = nodes.new("GeometryNodeInstanceOnPoints")
    links.new(store_tone.outputs[0], on_points.inputs["Points"])
    links.new(mote.outputs["Mesh"], on_points.inputs["Instance"])
    links.new(math_node('MULTIPLY', math_node('ADD', math_node('MULTIPLY', fade, 0.7), 0.3), random(21, 0.0015, 0.0045)), on_points.inputs["Scale"])
    painted = nodes.new("GeometryNodeSetMaterial")
    links.new(on_points.outputs[0], painted.inputs["Geometry"])
    painted.inputs["Material"].default_value = _spark_material()
    join = nodes.new("GeometryNodeJoinGeometry")
    links.new(rift, join.inputs[0])
    links.new(painted.outputs[0], join.inputs[0])
    links.new(join.outputs[0], output.inputs[0])


def _bone(rig, name):
    """The rig's bone of that name, whatever its case (the game says "root" for "Root")."""
    if not name:
        return None
    if name in rig.data.bones:
        return name
    return next((bone.name for bone in rig.data.bones if bone.name.lower() == name.lower()), None)


def apply(effects, rig, load_image, attach, collection, load_mesh=None, modes=None, start_frame=None):
    """effects: what the app sent (spawns); rig: the agent's armature; load_image(game path) -> image;
    attach(obj, rig, {"Bone", "Location", "Rotation"}): hangs an object on a bone with an Unreal offset;
    start_frame: for an animation's effects ("Timed"): the frame its animation starts at, each playing from its own
    moment (its delay) for its length;
    load_mesh(game path) -> object (mesh particles' meshes); modes: {control: (one-shot, length)} from another rig's
    effects (the 1st person version of an effect plays like the 3rd person one), filled in with this call's."""
    meshes = {}
    # effects whose emitters all play once (a cast burst): their control is how far through the burst (0 -> 1) they
    # are, to keyframe; the others switch on (1) and keep going with the timeline
    one_shot, lengths, counts, held = {}, {}, {}, {}
    # effects held at a strength (0 none .. 1 full), playing in a loop: "Held" (Viper's hand smoke)
    strength = {spawn.get("Effect") or "FX": True for spawn in effects if spawn.get("Held")}
    for spawn in effects:
        control = spawn.get("Effect") or "FX"
        for emitter in spawn.get("Emitters") or []:
            loops = int((_module(emitter, "Required") or {}).get("EmitterLoops") or 0)
            particles = schedule(emitter, seed=0) if loops > 0 else []
            # (one whose particles stay, held as long as the effect is on: an on/off effect)
            stays = any(p["life"] >= 1e8 for p in particles)
            once, total = counts.get(control, (0, 0))
            counts[control] = (once + (loops > 0 and not stays), total + 1)
            held[control] = held.get(control, False) or stays
            if loops > 0:
                end = max((p["birth"] + min(p["life"], 10.0) for p in particles), default=0.0)
                lengths[control] = max(lengths.get(control, 0.0), float(spawn.get("Delay") or 0.0) + end)
    # one-shot when most of its emitters play once (a few looping embers in a burst, like Yoru's rift, don't make it
    # an on/off effect) and none of them stays
    for control, (once, total) in counts.items():
        one_shot[control] = not held.get(control) and not strength.get(control) and once >= 0.6 * total
    if modes is not None:
        for control, (shot, length) in modes.items():
            one_shot[control], lengths[control] = shot, length
        for control in one_shot:
            modes.setdefault(control, (one_shot[control], lengths.get(control, 1.0)))
    if not effects or rig is None:
        return 0
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    group = _node_group()
    images = {}
    made = 0
    for number, spawn in enumerate(effects):
        socket = _bone(rig, spawn.get("Socket"))
        if socket is None:
            continue
        control = spawn.get("Effect") or "FX"
        if control not in rig:
            rig[control] = 1.0 if spawn.get("On") else 0.0
            rig.id_properties_ui(control).update(
                min=0.0, max=1.0, soft_min=0.0, soft_max=1.0,
                description=("A burst that plays once: keyframe it from 0 (start) to 1 (over), over about "
                             f"{max(lengths.get(control, 1.0), 0.1):.1f} s" if one_shot.get(control)
                             else "The agent's effect particles, as in game: 1 on, 0 off"))
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
            if ((emitter.get("Material") or {}).get("BaseMaterial") or "") == "m_X_portalTear_01":
                # (Yoru's rift: there for the whole effect; its control opens and closes it)
                particles = particles[:1]
                for particle in particles:
                    particle["birth"], particle["life"] = -1.0, 1e9
            mesh_object = None
            if emitter.get("Mesh") and load_mesh is not None:
                if emitter["Mesh"] not in meshes:
                    try:
                        meshes[emitter["Mesh"]] = load_mesh(emitter["Mesh"])
                        if meshes[emitter["Mesh"]] is not None:
                            source = meshes[emitter["Mesh"]]
                            source.hide_viewport = source.hide_render = True
                            source.name = "VP FX mesh " + source.name
                            for users in list(source.users_collection):
                                users.objects.unlink(source)
                            collection.objects.link(source)
                    except Exception:
                        meshes[emitter["Mesh"]] = None
                mesh_object = meshes.get(emitter["Mesh"])
                if mesh_object is not None:
                    # a mesh's size is a scale of the mesh (not cm)
                    for particle in particles:
                        particle["size"] = (particle["size"][0] / UNIT, particle["size"][1] / UNIT)
            grow = float(emitter.get("Scale") or 1.0)
            for particle in particles:
                particle["size"] = (particle["size"][0] * grow, particle["size"][1] * grow)
            if not particles:
                continue
            # (on a bone of its own: the game's LocationBoneSocket, e.g. Omen's orb in his hand, his ult's smoke on
            # his back; there, its own start location left out)
            bone_socket = ((_module(emitter, "LocationBoneSocket") or {}).get("SourceLocations") or [{}])[0]
            own_bone = _bone(rig, bone_socket.get("BoneSocketName"))
            if own_bone is not None:
                for particle in particles:
                    particle["location"] = (0.0, 0.0, 0.0)
            obj = bpy.data.objects.new(name, _particle_mesh(name, particles))
            collection.objects.link(obj)
            if own_bone is not None:
                bone_anchor = bpy.data.objects.new(f"VP FX {emitter.get('Name')} ({own_bone})", None)
                bone_anchor.empty_display_size = 0.05
                collection.objects.link(bone_anchor)
                attach(bone_anchor, rig, {"Bone": own_bone, "Location": bone_socket.get("Offset"), "Rotation": spawn.get("Rotation")})
                obj.parent = bone_anchor
            else:
                obj.parent = anchor
            material = _material(emitter, images, name, float(spawn.get("Boost") or 1.0) * float(emitter.get("Boost") or 1.0))
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
            if "PSA_Velocity" in str((_module(emitter, "Required") or {}).get("ScreenAlignment", "")):
                own.nodes["VP Axis Lock"].outputs[0].default_value = 1.0
                own.nodes["VP Velocity Aligned"].outputs[0].default_value = 1.0
            if mesh_object is not None:
                own.nodes["VP Mesh"].inputs["Object"].default_value = mesh_object
                if mesh_object.data.uv_layers:
                    # (KAY/O's face screen: its flipbook is laid on the mesh's last UV map)
                    face = ((emitter.get("Material") or {}).get("BaseMaterial") or "").lower().startswith("m_faceexpressions")
                    own.nodes["VP Mesh UV"].inputs["Name"].default_value = mesh_object.data.uv_layers[-1 if face else 0].name
                own.nodes["VP Use Mesh"].outputs[0].default_value = 1.0
                type_data = _module(emitter, "TypeDataMesh") or {}
                if not type_data.get("bCameraFacing"):
                    own.nodes["VP Mesh Fixed"].outputs[0].default_value = 1.0
            loops = int((_module(emitter, "Required") or {}).get("EmitterLoops") or 0)
            if loops > 0 and not spawn.get("Held"):
                own.nodes["VP Loop"].outputs[0].default_value = 0.0
            if spawn.get("Held"):
                _drive(material.node_tree, 'nodes["VP Fade"].outputs[0].default_value', rig, control)
                own.nodes["VP Active"].outputs[0].default_value = 1.0
            pivot = (_module(emitter, "PivotOffset") or {}).get("PivotOffset") or {}
            # (the game's card spans offset .. offset + 1, -0.5 centring it; its V runs down the screen)
            own.nodes["VP Pivot X"].outputs[0].default_value = 0.5 + float(pivot.get("X", -0.5))
            own.nodes["VP Pivot Y"].outputs[0].default_value = -0.5 - float(pivot.get("Y", -0.5))
            offset = _module(emitter, "CameraOffset")
            own.nodes["VP Camera Offset"].outputs[0].default_value = sample(offset.get("CameraOffset"))[0] if offset else 0.0
            if material.node_tree.nodes.get("VP Reveal") is not None:
                _drive(material.node_tree, 'nodes["VP Reveal"].outputs[0].default_value', rig, control)
                _drive(material.node_tree, 'nodes["VP Clock"].outputs[0].default_value', rig, control, f"frame / {fps:g}", variables=[])
                spark_image = images.get(((emitter.get("Material") or {}).get("Textures") or {}).get("AlphaB"))
                _add_rift_sparks(own, spark_image)
                _drive(own, 'nodes["VP Rift Reveal"].outputs[0].default_value', rig, control)
            # the timeline's seconds: shadow smoke keeps drifting and circling while its effect is held
            _drive(own, 'nodes["VP Clock"].outputs[0].default_value', rig, control, f"frame / {fps:g}", variables=[])
            if (emitter.get("Material") or {}).get("BaseMaterial") in SHADOW_MATERIALS:
                own.nodes["VP Spin"].outputs[0].default_value = 0.7 if mesh_object is not None else 0.0
                if material.node_tree.nodes.get("VP Clock") is not None:
                    _drive(material.node_tree, 'nodes["VP Clock"].outputs[0].default_value', rig, control, f"frame / {fps:g}", variables=[])
            # seconds since the effect started (its delay), and on/off from the armature
            delay = float(spawn.get('Delay') or 0.0)
            if spawn.get("Timed") and start_frame is not None:
                # an animation's effect: from its moment of the animation (the timeline), for its length (a looping
                # emitter: 2 s), switched by the armature's control
                ends = [p["birth"] + min(p["life"], 10.0) for p in particles if p["life"] < 1e8]
                length = max(ends, default=0.0) if loops > 0 else 2.0
                length = max(length, 0.3)
                seconds = f"(frame - {start_frame:g}) / {fps:g}"
                _drive(own, 'nodes["VP Time"].outputs[0].default_value', rig, control, f"{seconds} - {delay:g}", variables=[])
                _drive(own, 'nodes["VP Active"].outputs[0].default_value', rig, control,
                       f"value * ({seconds} >= {delay:g}) * ({seconds} < {delay + length:g})")
                made += 1
                continue
            if one_shot.get(control):
                # how far through the burst: 0 -> 1 over its length (shown while between)
                length = max(lengths.get(control, 1.0), 0.1)
                _drive(own, 'nodes["VP Time"].outputs[0].default_value', rig, control,
                       f"value * {length:g} - {delay:g} - (value <= 0.0) * 1000.0")  # (a simple expression: no Python needed)
                _drive(own, 'nodes["VP Active"].outputs[0].default_value', rig, control, "(value > 0.0) * (value < 1.0)")
            else:
                _drive(own, 'nodes["VP Time"].outputs[0].default_value', rig, control, f"frame / {fps:g} - {delay:g}",
                       variables=[])
                # (held at a strength: on from anything above 0)
                _drive(own, 'nodes["VP Active"].outputs[0].default_value', rig, control,
                       "(value > 0.0) * 1.0" if spawn.get("Held") else "value")
            made += 1
    rig.update_tag()
    return made
