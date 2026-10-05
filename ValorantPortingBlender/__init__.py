import bpy
from pathlib import Path
import json
import os
import socket
import queue
import threading
import re
import traceback
import time
import bpy
import os
import bpy.props
from mathutils import Matrix, Vector, Quaternion, Euler
import math
from .valorant_psk_psa_b5 import pskimport, psaimport
from .valorant_shaders import rebuild_materials, add_default_vertex_colors, merge_duplicate_materials, find_material_info

bl_info = {
    "name": "Valorant Porting",
    "author": "Half, BK, Zain, DeveloperChipmunk",
    "version": (1, 12, 0),
    "blender": (4, 0, 0),
    "description": "Blender Server for Valorant Porting (models + animations, Blender 5 compatible)",
    "category": "Import",
}

global import_assets_root
global import_settings
global import_data

global server
global MAIN_SHADER
global INNER_SHADER


class Log:
    INFO = u"\u001b[36m"
    WARNING = u"\u001b[31m"
    ERROR = u"\u001b[33m"
    RESET = u"\u001b[0m"

    @staticmethod
    def information(message):
        print(f"{Log.INFO}[INFO] {Log.RESET}{message}")

    @staticmethod
    def warning(message):
        print(f"{Log.WARNING}[WARN] {Log.RESET}{message}")

    @staticmethod
    def error(message):
        print(f"{Log.WARNING}[ERROR] {Log.RESET}{message}")


class Receiver(threading.Thread):

    def __init__(self, event):
        threading.Thread.__init__(self, daemon=True)
        self.event = event
        self.messages = queue.Queue()  # every message, in order (one slot lost messages sent while Blender was busy)
        self.socket_server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.keep_alive = True

    def run(self):
        host, port = 'localhost', 24283
        self.socket_server.bind((host, port))
        self.socket_server.settimeout(3.0)
        Log.information(f"ValorantPorting Server Listening at {host}:{port}")

        while self.keep_alive:
            try:
                data_string = ""
                while True:
                    info = self.socket_server.recvfrom(4096)
                    if data := info[0].decode('utf-8'):
                        if data == "MessageFinished":
                            break
                        data_string += data
                self.messages.put(json.loads(data_string))
                self.event.set()

            except OSError:
                pass  # timeouts / socket resets: keep listening
            except Exception as e:
                # a broken or partial message must never stop the receiver
                Log.error(f"Could not read message from Valorant Porting: {e}")

    def stop(self):
        self.keep_alive = False
        self.socket_server.close()
        Log.information("ValorantPorting Server Closed")


def resolve_asset_path(path: str) -> str:
    path = path[1:] if path.startswith("/") else path
    # Unreal's "/Game/" is a virtual mount point; on disk these assets
    # actually live under "ShooterGame/Content/".
    if path.startswith("Game/"):
        path = "ShooterGame/Content/" + path[len("Game/"):]
    return path


def disk_path(path):
    """Windows can't open files whose full path is longer than 260 characters (an app kept deep in folders gets long
    asset paths); the long-path form (prefix \\?\) has no such limit and works for Blender's file and image loading."""
    if os.name != 'nt' or not path or len(path) < 240 or path.startswith(LONG_PATH_PREFIX):
        return path
    return LONG_PATH_PREFIX + os.path.abspath(path)


LONG_PATH_PREFIX = "\\\\?\\"


def import_mesh(path: str) -> bpy.types.Object:
    path = resolve_asset_path(path)
    base_path = os.path.join(import_assets_root, path.split(".")[0])

    candidates = [base_path + "_LOD0"]
    if not base_path.endswith("_Skelmesh"):
        candidates.append(base_path + "_Skelmesh_LOD0")

    for mesh_path in candidates:
        if os.path.exists(disk_path(mesh_path + ".psk")):
            pskimport(
                disk_path(mesh_path + ".psk"),
                bReorientBones=import_settings.get("ReorientBones"),
                bScaleDown=True,
                bToSRGB=False)
            return bpy.context.active_object

        if os.path.exists(disk_path(mesh_path + ".pskx")):
            pskimport(
                disk_path(mesh_path + ".pskx"),
                bScaleDown=True,
                bToSRGB=False)
            return bpy.context.active_object

    print(f"[DEBUG] Mesh not found. Raw path from app: {path!r}")
    print(f"[DEBUG] AssetsRoot: {import_assets_root!r}")
    print(f"[DEBUG] Computed base_path: {base_path!r}")
    return None

def import_texture(path: str) -> bpy.types.Image:
    path, name = path.split(".")

    raw_path = path[1:] if path.startswith("/") else path
    translated_path = resolve_asset_path(path)

    for candidate in [translated_path, raw_path]:
        texture_path = os.path.join(import_assets_root, candidate + ".png")
        if os.path.exists(disk_path(texture_path)):
            return bpy.data.images.load(disk_path(texture_path), check_existing=True)

    print(f"[DEBUG] Texture not found in either location. Tried: "
          f"{os.path.join(import_assets_root, translated_path + '.png')!r} and "
          f"{os.path.join(import_assets_root, raw_path + '.png')!r}")
    missing_textures.add(os.path.basename(raw_path))
    return None


# textures an import couldn't find (shown purple in Blender); reported once the import is done
missing_textures = set()


def find_built_material(name, path):
    """A material an earlier import already built for this game material: by its game path when the app sent one
    (chroma variants reuse material names, e.g. "BoltSniper_Arcade_Simple_MI" in each variant's folder)."""
    if path:
        return next((m for m in bpy.data.materials if m.get("vp_path") == path and m.get("vp_built")), None)
    existing = bpy.data.materials.get(name)
    return existing if existing is not None and existing.get("vp_built") is True else None


def import_material(target_slot: bpy.types.MaterialSlot, material_data, mat_type):
    material_name = material_data.get("MaterialName")
    material_path = material_data.get("MaterialPath")
    # NOTE: Blender 5.0+ made Material.use_nodes always return True (deprecated),
    # AND new materials now come pre-populated with a default node tree (Principled BSDF +
    # Material Output) automatically. Neither of those can be used anymore to tell "a fresh
    # placeholder material" apart from "one we already fully built." Use our own explicit
    # marker instead, set at the bottom of this function once building is actually done.
    if existing := find_built_material(material_name, material_path):
        target_slot.material = existing
        return
    if kind := effect_kind(material_data, mat_type):
        build_effect_material(target_slot, material_data, kind)
        return
    target_material = target_slot.material
    # never rebuild a finished material in place: other imports may use it (chroma materials share names)
    if target_material.name.casefold() != material_name.casefold() or target_material.get("vp_built"):
        target_material = target_material.copy()
        target_material.name = material_name
        target_slot.material = target_material
    if material_path:
        target_material["vp_path"] = material_path
    target_material.use_nodes = True

    nodes = target_material.node_tree.nodes
    nodes.clear()
    links = target_material.node_tree.links
    links.clear()

    #fix parent & eye material
    parent_name = material_data.get("ParentName")
    if parent_name is None:
        parent_name = material_name
    if "Eye" in parent_name:
        target_material.blend_method = 'BLEND'

    output_node = nodes.new(type="ShaderNodeOutputMaterial")
    output_node.location = (200, 0)

    main_shader_node = nodes.new(type="ShaderNodeGroup")
    MAIN_SHADER = main_shader_node
    main_shader_node.name = parent_name
    parent_node_exists = True

    if bpy.data.node_groups.get(parent_name) is not None:
        main_shader_node.node_tree = bpy.data.node_groups.get(main_shader_node.name)
        # assign this so group input stays consistent
        group_inputs = main_shader_node.inputs
    else:
        parent_node_exists = False

        new_shader_internals = bpy.data.node_groups.new(parent_name, 'ShaderNodeTree')
        main_shader_node.node_tree = new_shader_internals
        # create group input
        group_inputs = new_shader_internals.nodes.new("NodeGroupInput")
        group_inputs.location = (-350, 0)
        # create group output
        group_outputs = new_shader_internals.nodes.new("NodeGroupOutput")
        group_outputs.location = (600, 0)
        # create imported inner goup
        imported_shader_node = new_shader_internals.nodes.new(type="ShaderNodeGroup")
        imported_shader_node.name = "1P_Weapon_Mat_Base_V5"
        # ability props mix both kinds: the character shader for character-style textures (Albedo + MRAE)
        # (this module has its own any(), so no generator here)
        uses_mrae = "MRAE" in [t.get("Name") for t in material_data.get("Textures") or []]
        if mat_type == "Character" or (mat_type == "Ability" and uses_mrae):
            imported_shader_node.name = "3P_Character_Mat_V5"
        imported_shader_node.node_tree = bpy.data.node_groups.get(imported_shader_node.name)

        # create output on outer group
        new_shader_internals.interface.new_socket(name="BSDF", in_out="OUTPUT", socket_type="NodeSocketShader")

        # link outer group output to imported inner group's input
        for output in group_inputs.outputs:
            if imported_shader_node.inputs.get(output.name) is not None:
                new_shader_internals.links.new(output, imported_shader_node.node_tree.inputs.get(output.name))
        new_shader_internals.links.new(imported_shader_node.outputs[0], group_outputs.inputs[0])

    # link outer group's outputs to shader output
    links.new(main_shader_node.outputs[0], output_node.inputs[0])

    def texture_parameter(data):
        name = data.get("Name")
        value = data.get("Value")
        tex_image_node = nodes.new(type="ShaderNodeTexImage")
        if (image := import_texture(value)) is None: return
        tex_image_node.image = image
        tex_image_node.image.alpha_mode = 'CHANNEL_PACKED'
        tex_image_node.hide = True
        tex_image_node.image.colorspace_settings.name = 'Linear Rec.709'

        if 'decal' in name.lower():
            uv_node = nodes.new(type="ShaderNodeUVMap")
            uv_node.uv_map = 'EXTRAUVS0'
            links.new(uv_node.outputs[0], tex_image_node.inputs[0])

        if name in MAIN_SHADER.inputs:
            if name == 'Decal Mask Texture':
                links.new(tex_image_node.outputs[1], main_shader_node.inputs[name])
            else:
                links.new(tex_image_node.outputs[0], main_shader_node.inputs[name])
        else:
            MAIN_SHADER.node_tree.interface.new_socket(name=name, in_out="INPUT", socket_type="NodeSocketColor")
            if name == 'Decal Mask Texture':
                links.new(tex_image_node.outputs[1], MAIN_SHADER.inputs[name])
            else:
                links.new(tex_image_node.outputs[0], MAIN_SHADER.inputs[name])

    def scalar_parameter(data):
        name = data.get("Name")
        value = data.get("Value")
        scalar_node = nodes.new(type="ShaderNodeValue")
        scalar_node.label = name
        scalar_node.outputs[0].default_value = value

        if MAIN_SHADER.inputs.get(name) is not None and MAIN_SHADER.inputs[name].type == "VALUE":
            links.new(scalar_node.outputs[0], MAIN_SHADER.inputs[name])
        else:
            MAIN_SHADER.node_tree.interface.new_socket(name=name, in_out="INPUT", socket_type="NodeSocketFloat")
            links.new(scalar_node.outputs[0], MAIN_SHADER.inputs[name])

    def vector_parameter(data):
        name = data.get("Name")
        value = data.get("Value")
        color_node = nodes.new(type="ShaderNodeRGB")
        color_node.label = name
        color_node.outputs[0].default_value = (value["R"], value["G"], value["B"], 1)

        if MAIN_SHADER.inputs.get(name) is not None and MAIN_SHADER.inputs[name].type == "COLOR":
            links.new(color_node.outputs[0], MAIN_SHADER.inputs[name])
        else:
            MAIN_SHADER.node_tree.interface.new_socket(name=name, in_out="INPUT", socket_type="NodeSocketColor")
            links.new(color_node.outputs[0], MAIN_SHADER.inputs[name])

    for texture in material_data.get("Textures"):
        texture_parameter(texture)

    for scalar in material_data.get("Scalars"):
        scalar_parameter(scalar)

    for vector in material_data.get("Vectors"):
        vector_parameter(vector)

    # link inputs to imported inner group
    if not parent_node_exists:
        for output in group_inputs.outputs:
            if imported_shader_node.inputs.get(output.name) is not None:
                new_shader_internals.links.new(output, imported_shader_node.inputs.get(output.name))

    add_iridescence(nodes, links, MAIN_SHADER, material_data)

    # mark this material as fully built so future lookups can safely reuse it
    target_material["vp_built"] = True


# Effect-only materials (Unreal translucent/additive): liquids and dissolve shells that only show during
# animations, glowing lines, lens glass. Drawn as solid surfaces they hide or smear the gun.
EFFECT_SHELL = re.compile(r"vfx|liquid|appear|dissolve|reveal|hologram|distort|refract|shellmesh", re.IGNORECASE)


def effect_kind(material_data, mat_type=None):
    blend = (material_data.get("BlendMode") or "").lower()
    names = f'{material_data.get("BaseMaterial") or ""} {material_data.get("ParentName") or ""} {material_data.get("MaterialName") or ""}'
    if blend == "additive":
        return "glow"
    if mat_type == "Ability" and not material_data.get("Textures"):
        # a glow strip (Chamber's gun lines): only an emissive colour, no textures
        if [v for v in material_data.get("Vectors") or [] if re.search(r"emissive colou?r", v.get("Name") or "", re.I)]:
            return "glow"
    if blend.startswith("translucent") or blend in ("modulate", "alphacomposite", "alphaholdout"):
        if EFFECT_SHELL.search(names):
            return "hidden"
        # agents' eye overlay: a texture whose alpha says where it shows (the eyelid shadow at the rim, a highlight);
        # drawn as glass it put a white film over the eyes
        if re.search(r"overlay", names, re.I) or [t for t in material_data.get("Textures") or [] if "overlay" in (t.get("Name") or "").lower()]:
            return "overlay"
        return "glass"
    return None


def build_effect_material(target_slot, material_data, kind):
    if kind == "hidden":
        target_slot.material = _hidden_material()
        return

    name = material_data.get("MaterialName")
    material = target_slot.material  # the importer's placeholder of the same name, else a new one
    if material is None or material.name.casefold() != name.casefold() or material.get("vp_built"):
        material = bpy.data.materials.new(name)
    target_slot.material = material
    if material_data.get("MaterialPath"):
        material["vp_path"] = material_data.get("MaterialPath")
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (400, 0)
    material.surface_render_method = 'BLENDED'
    material.use_backface_culling = False

    textures = {t.get("Name"): t.get("Value") for t in material_data.get("Textures") or []}
    vectors = {v.get("Name"): v.get("Value") for v in material_data.get("Vectors") or []}
    scalars = {v.get("Name"): v.get("Value") for v in material_data.get("Scalars") or []}
    texture_path = next((textures[n] for n in textures if re.search(r"albedo|diffuse|emissive|color|base", n, re.I)),
                        next(iter(textures.values()), None))
    color_value = next((vectors[n] for n in vectors if re.search(r"emissive|color|tint", n, re.I)), None)

    color = None
    alpha = None
    if texture_path and (image := import_texture(texture_path)):
        image.alpha_mode = 'CHANNEL_PACKED'
        texture = nodes.new("ShaderNodeTexImage")
        texture.image = image
        texture.location = (-500, 0)
        color = texture.outputs[0]
        alpha = texture.outputs[1]
    if color_value:
        tint = nodes.new("ShaderNodeRGB")
        tint.location = (-500, -250)
        tint.outputs[0].default_value = (color_value["R"], color_value["G"], color_value["B"], 1)
        if color is None:
            color = tint.outputs[0]
        else:
            multiply = nodes.new("ShaderNodeMix")
            multiply.data_type = 'RGBA'
            multiply.blend_type = 'MULTIPLY'
            multiply.inputs[0].default_value = 1
            multiply.location = (-250, 0)
            links.new(color, multiply.inputs[6])
            links.new(tint.outputs[0], multiply.inputs[7])
            color = multiply.outputs[2]

    if kind == "glow":
        emission = nodes.new("ShaderNodeEmission")
        emission.location = (0, 0)
        strength = scalars.get("Emissive Intensity") or scalars.get("Emissive_Intensity") or 2.0
        emission.inputs["Strength"].default_value = max(1.0, min(float(strength), 10.0))
        if color is not None:
            links.new(color, emission.inputs["Color"])
        add = nodes.new("ShaderNodeAddShader")
        add.location = (200, 0)
        links.new(nodes.new("ShaderNodeBsdfTransparent").outputs[0], add.inputs[0])
        links.new(emission.outputs[0], add.inputs[1])
        links.new(add.outputs[0], output.inputs["Surface"])
    elif kind == "overlay":
        # the texture's colour where its alpha is (nothing where it's transparent)
        bsdf = nodes.new("ShaderNodeBsdfPrincipled")
        bsdf.location = (100, 0)
        bsdf.inputs["Roughness"].default_value = 0.4
        if color is not None:
            links.new(color, bsdf.inputs["Base Color"])
        if alpha is not None:
            links.new(alpha, bsdf.inputs["Alpha"])
        else:
            bsdf.inputs["Alpha"].default_value = 0.0
        links.new(bsdf.outputs[0], output.inputs["Surface"])
    else:  # glass
        bsdf = nodes.new("ShaderNodeBsdfPrincipled")
        bsdf.location = (100, 0)
        bsdf.inputs["Roughness"].default_value = 0.05
        bsdf.inputs["Alpha"].default_value = 0.25
        if color is not None:
            links.new(color, bsdf.inputs["Base Color"])
        links.new(bsdf.outputs[0], output.inputs["Surface"])
    material["vp_built"] = True


def place_part(obj, placement):
    """A part's in-game offset (cm), rotation and size inside a model of several parts (Chamber's trap: body and
    rotator 3x, the eye 8.7 cm up). Unreal's Y axis is mirrored in Blender."""
    location, rotation, scale = placement.get("Location") or {}, placement.get("Rotation") or {}, placement.get("Scale") or {}
    obj.location = (location.get("X", 0) * 0.01, -location.get("Y", 0) * 0.01, location.get("Z", 0) * 0.01)
    obj.rotation_mode = 'XYZ'
    obj.rotation_euler = (math.radians(rotation.get("Roll", 0)), math.radians(-rotation.get("Pitch", 0)),
                          math.radians(-rotation.get("Yaw", 0)))
    obj.scale = (scale.get("X", 1), scale.get("Y", 1), scale.get("Z", 1))


def attach_part_to_bone(child, rig, attachment):
    """Hang a part on a bone of the model's rig with its in-game offset, so it follows the rig's animations."""
    bone = attachment.get("Bone")
    if child is None or rig is None or bone not in rig.data.bones:
        return
    place_part(child, attachment)
    constraint = child.constraints.new('CHILD_OF')
    constraint.name = "Valorant Porting attach"
    constraint.target = rig
    constraint.subtarget = bone
    # Child Of: world = bone @ inverse_matrix @ own transform; the inverse matrix turns the Blender bone into Unreal's
    constraint.inverse_matrix = unreal_bone_frame(rig, bone)


# Valorant's horizontal field of view (fixed, 16:9)
FIRST_PERSON_FOV = 103.0


def add_first_person_camera(objects, collection):
    """A camera where the player's eyes are: the 1st person arms' "Camera" bone, looking forward, with the game's field
    of view. It follows the bone, so animations that move the view (inspects, equips) move the camera too."""
    rigs = [o for o in objects if o is not None and o.type == 'ARMATURE' and "Camera" in o.data.bones]
    rig = next((o for o in rigs if o.name.upper().startswith("FP_")), rigs[0] if rigs else None)
    if rig is None:
        Log.warning("No 1st person arms with a Camera bone; no camera added")
        return None
    data = bpy.data.cameras.new("1st person camera")
    data.sensor_fit = 'HORIZONTAL'
    data.angle = math.radians(FIRST_PERSON_FOV)
    data.clip_start = 0.01  # the arms are a few cm from the eyes
    camera = bpy.data.objects.new("1st person camera", data)
    collection.objects.link(camera)
    # at rest the bone is at eye height and the arms point along +X; the camera looks along +X, up +Z
    rest = rig.matrix_world @ rig.data.bones["Camera"].matrix_local
    camera.matrix_world = Matrix.Translation(rest.translation) @ Euler((math.pi / 2, 0, -math.pi / 2)).to_matrix().to_4x4()
    constraint = camera.constraints.new('CHILD_OF')
    constraint.name = "Valorant Porting camera"
    constraint.target = rig
    constraint.subtarget = "Camera"
    constraint.inverse_matrix = rest.inverted()  # rest pose = where it was placed; the bone's motion moves it
    if bpy.context.scene.camera is None:
        bpy.context.scene.camera = camera
    Log.information(f"Added a 1st person camera on {rig.name}")
    return camera


def remove_disabled_sections(mesh_object, sections):
    """Deletes the faces of mesh sections the game never draws (Unreal "disabled" sections, e.g. the body part a cloth
    piece replaces); drawn, they cover the real surface with stray texture. The .psk has one material slot per section."""
    if not sections or mesh_object is None or mesh_object.type != 'MESH':
        return
    import bmesh
    bm = bmesh.new()
    bm.from_mesh(mesh_object.data)
    doomed = [f for f in bm.faces if f.material_index in set(sections)]
    if doomed:
        bmesh.ops.delete(bm, geom=doomed, context='FACES')
        bm.to_mesh(mesh_object.data)
        mesh_object.data.update()
    bm.free()


def add_iridescence(nodes, links, shader, material_data):
    """Riot's iridescent skins (e.g. Prism III): the albedo is a grayscale position along the "Iridescence Gradient"
    strip, shifted by the viewing angle, on the parts the AEM's blue channel marks. Each chroma has its own strip."""
    def linked_image(socket_name):
        socket = shader.inputs.get(socket_name)
        if socket is None or not socket.is_linked:
            return None
        node = socket.links[0].from_node
        return node if node.type == 'TEX_IMAGE' else None

    gradient, albedo, aem = linked_image("Iridescence Gradient"), linked_image("Albedo"), linked_image("AEM")
    # only with the AEM's mask: agents' iridescent materials (Astra's 1st person arms) have none, and without it the
    # whole model became the gradient strip; their normal texture is what shows
    if gradient is None or albedo is None or aem is None:
        return
    scalars = {v.get("Name"): v.get("Value") for v in material_data.get("Scalars") or []}
    gradient.extension = 'EXTEND'
    gradient.interpolation = 'Linear'

    facing = nodes.new("ShaderNodeLayerWeight")
    view = nodes.new("ShaderNodeMath")
    view.operation = 'MULTIPLY'
    links.new(facing.outputs["Facing"], view.inputs[0])
    view.inputs[1].default_value = float(scalars.get("Camera View Scale", 0.5))
    tone = nodes.new("ShaderNodeSeparateColor")
    links.new(albedo.outputs[0], tone.inputs[0])
    position = nodes.new("ShaderNodeMath")
    position.operation = 'ADD'
    links.new(tone.outputs[0], position.inputs[0])
    links.new(view.outputs[0], position.inputs[1])
    offset = nodes.new("ShaderNodeMath")
    offset.operation = 'ADD'
    offset.use_clamp = True
    links.new(position.outputs[0], offset.inputs[0])
    offset.inputs[1].default_value = float(scalars.get("Diffuse Offset", 0.0))
    uv = nodes.new("ShaderNodeCombineXYZ")
    links.new(offset.outputs[0], uv.inputs[0])
    uv.inputs[1].default_value = 0.5
    links.new(uv.outputs[0], gradient.inputs[0])

    mix = nodes.new("ShaderNodeMix")
    mix.data_type = 'RGBA'
    links.new(albedo.outputs[0], mix.inputs[6])
    links.new(gradient.outputs[0], mix.inputs[7])
    mask = nodes.new("ShaderNodeSeparateColor")
    links.new(aem.outputs[0], mask.inputs[0])
    links.new(mask.outputs[2], mix.inputs[0])
    links.new(mix.outputs[2], shader.inputs["Albedo"])


def import_shaders(shaderName):
    script_root = Path(os.path.dirname(os.path.abspath(__file__)))
    shaders_blend_file = Path(script_root.joinpath(shaderName))
    nodegroups_folder = shaders_blend_file.joinpath("NodeTree")

    with bpy.data.libraries.load(str(shaders_blend_file), link=False) as (data_from, data_to):
        for node_group in data_from.node_groups:
            if node_group not in bpy.data.node_groups.keys():
                data_to.node_groups.append(node_group)


def create_collection(name):
    if name in bpy.context.view_layer.layer_collection.children:
        existing = bpy.context.view_layer.layer_collection.children.get(name)
        bpy.context.view_layer.active_layer_collection = existing
        return existing.collection
    bpy.ops.object.select_all(action='DESELECT')

    new_collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(new_collection)
    bpy.context.view_layer.active_layer_collection = bpy.context.view_layer.layer_collection.children.get(
        new_collection.name)
    return new_collection


def mesh_from_armature(armature) -> bpy.types.Mesh:
    return armature.children[0]  # only used with psk, mesh is always first child


def first(target, expr, default=None):
    if not target:
        return None
    filtered = filter(expr, target)

    return next(filtered, default)


def where(target, expr):
    if not target:
        return None
    filtered = filter(expr, target)

    return filtered


def any(target, expr):
    if not target:
        return None

    filtered = list(filter(expr, target))
    return len(filtered) > 0


SELECTION_PORT = 24284  # the app listens here for what is selected in Blender


def animation_filter_tag(base_key, armature):
    """Tag stored on imported armatures so the app can filter animations for them.
    Agents: agent|<folder>|<name>|TP/FP/CS (3rd person, 1st person, character select). Weapons: weapon|<folder>|<name>."""
    if not base_key:
        return None
    if base_key.startswith("agent|"):
        name = armature.name.upper()
        variant = "FP" if name.startswith("FP_") else "CS" if name.startswith("CS_") else "TP"
        return f"{base_key}|{variant}"
    return base_key


def guess_filter_tag(armature):
    """For agents imported before tagging existed: TP_Wushu_S0_Skelmesh... -> Jett's (Wushu) 3rd-person body."""
    match = re.match(r"^(TP|FP|CS)_([A-Za-z0-9]+)_", armature.name)
    if match:
        return f"agent|Characters/{match.group(2)}/|{match.group(2)}|{match.group(1)}"
    return None


selection_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
selection_state = {"tag": None, "ticks": 0}


def watch_selection():
    """Tells the app which Valorant armature is selected (on change, and every ~2s in case the app restarted)."""
    try:
        armature = find_selected_armature()
        tag = None
        if armature is not None:
            tag = armature.get("vp_filter") or guess_filter_tag(armature)
        selection_state["ticks"] += 1
        if tag and (tag != selection_state["tag"] or selection_state["ticks"] >= 7):
            selection_socket.sendto(("VP_SELECT|" + tag).encode("utf-8"), ("localhost", SELECTION_PORT))
            selection_state["ticks"] = 0
        selection_state["tag"] = tag
        # every ~2 s: this add-on's version, so the app can tell when Blender runs an older add-on than the app
        selection_state["hello"] = selection_state.get("hello", 0) + 1
        if selection_state["hello"] >= 7:
            version = ".".join(str(v) for v in bl_info["version"])
            selection_socket.sendto(("VP_HELLO|" + version).encode("utf-8"), ("localhost", SELECTION_PORT))
            selection_state["hello"] = 0
    except Exception:
        pass
    return 0.3


def find_selected_armature():
    obj = bpy.context.active_object
    if obj is None:
        return None
    if obj.type == 'ARMATURE':
        return obj
    if obj.parent is not None and obj.parent.type == 'ARMATURE':
        return obj.parent
    for modifier in getattr(obj, "modifiers", []):
        if modifier.type == 'ARMATURE' and modifier.object is not None:
            return modifier.object
    return None


# Guns attach to the agent's right-hand weapon bone, which the animations place in the palm (3rd and 1st person).
WEAPON_SOCKET_BONE = "R_WeaponPoint"
# Unreal's weapon socket axes vs. the gun model's (checked on Vandal/Sheriff/Operator, Jett/Brimstone, 1P and 3P).
WEAPON_SOCKET_ROTATION = Matrix.Rotation(-math.pi / 2, 4, 'Y') @ Matrix.Rotation(-math.pi / 2, 4, 'X')
BUDDY_SOCKET_BONE = "Gun_Buddy"
# agents' sockets abilities name as where they're held (Core skeletons): socket -> (bone, rotation)
HOLD_SOCKETS = {
    "R_WeaponPointSocket": ("R_WeaponPoint", WEAPON_SOCKET_ROTATION),
    "L_WeaponPointSocket": ("L_WeaponPoint", WEAPON_SOCKET_ROTATION),
    "L_WeaponMasterSocket": ("L_WeaponMaster", WEAPON_SOCKET_ROTATION),
    "WeaponPoint": ("R_WeaponMaster", WEAPON_SOCKET_ROTATION),
    "CameraSocket": ("Camera", Matrix()),
}


def unreal_bone_frame(armature, bone_name):
    """Rotation from a Blender bone's frame to its original Unreal frame. Agents are imported with reoriented bones
    (nicer in Blender, but no longer Unreal's axes); the importer stores what it changed on each bone."""
    bone = armature.data.bones[bone_name]
    if "post_quat" not in bone or "orig_quat" not in bone:
        return Matrix()
    post = Quaternion(bone["post_quat"])
    orig = Quaternion(bone["orig_quat"])
    return (post.inverted() @ orig.conjugated()).to_matrix().to_4x4()


def attach_to_bone(child, holder, bone_name, socket_rotation, place):
    """Snap an imported gun/buddy onto a bone of the selected armature so it follows its animations."""
    if child is None or holder is None or child == holder or bone_name not in holder.data.bones:
        return False
    for old in [c for c in child.constraints if c.type == 'CHILD_OF']:
        child.constraints.remove(old)
    child.location = (0.0, 0.0, 0.0)
    child.rotation_mode = 'QUATERNION'
    child.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
    constraint = child.constraints.new('CHILD_OF')
    constraint.name = "Valorant Porting attach"
    constraint.target = holder
    constraint.subtarget = bone_name
    # Child Of: world = bone @ inverse_matrix @ own transform. The inverse matrix carries the Unreal frame + socket.
    constraint.inverse_matrix = unreal_bone_frame(holder, bone_name) @ socket_rotation
    Log.information(f"Attached {child.name} to {holder.name} ({bone_name})")
    show_message(f"Attached to the selected {'agent' if place == 'hand' else 'gun'} ({bone_name}). "
                 f"To detach, delete the \"Valorant Porting attach\" constraint.")
    return True


def show_message(message, title="Valorant Porting", icon='INFO'):
    def draw(self, context):
        self.layout.label(text=message)
    if bpy.app.background:
        return
    window_manager = bpy.context.window_manager
    if not window_manager.windows:
        return
    try:
        # called from a timer, so give the popup an explicit window to appear in
        with bpy.context.temp_override(window=window_manager.windows[0]):
            window_manager.popup_menu(draw, title=title, icon=icon)
    except Exception:
        pass


def import_animation(data, armature=None):
    """armature: where it goes (a scene says so); otherwise the selected armature"""
    armature = armature or find_selected_armature()
    # "Layer": on top of the current animation (only the bones this one moves change); "Chain": after it
    mode = data.get("Mode")
    keep = mode in ("Layer", "Chain")
    base = armature.animation_data.action if keep and armature is not None and armature.animation_data else None
    _apply_animation(data, armature)
    action = armature.animation_data.action if armature is not None and armature.animation_data else None
    sounds_start = None
    if action is not None and action != base:
        continuous_quaternions(action)
        # whether it loops (a run, an idle): a layer put on it later can repeat it to its own length
        action["vp_loops"] = bool(data.get("LowerLoops")) and (not data.get("UpperAnimationPath") or bool(data.get("UpperLoops")))
        if base is not None and mode == "Layer":
            action = layer_actions(armature, base, action, data.get("Name"))
        elif base is not None:
            # starting at the timeline's current frame cuts the rest of the current animation (an animation cancel)
            cut = bpy.context.scene.frame_current if data.get("ChainFrom") == "CurrentFrame" else None
            blend = data.get("ChainBlend")
            action, sounds_start, join = chain_actions(armature, base, action, data.get("Name"),
                                                       CHAIN_BLEND_SECONDS if blend is None else float(blend), cut)
            # the earlier animations' sound effects stop where the next one takes over: their loops always, the
            # rest when this one brings sounds of its own; voice lines play on
            stop_animation_sounds(armature, join, loops_only=not data.get("Sounds"))
    if armature is not None and base is None:
        remove_animation_sounds(armature)  # the previous animation's sounds go with it (a layer or chain keeps them)
    if action is not None:
        add_animation_sounds(data, action, owner=armature.name, start=sounds_start)


def remove_animation_sounds(armature):
    """The sound strips added with the animations applied to this armature before."""
    editor = bpy.context.scene.sequence_editor
    if editor is None:
        return
    strips = getattr(editor, "strips", None)
    if strips is None:
        strips = editor.sequences
    old = [s for s in strips if s.get("vp_armature") == armature.name]
    for strip in old:
        strips.remove(strip)
    if old:
        Log.information(f"Removed {len(old)} sound(s) of the previous animation on {armature.name}")


def stop_animation_sounds(armature, frame, loops_only=False):
    """The sound effects of the animations applied to this armature before stop at this frame (cut short, or removed
    if they'd start later); voice lines play on. loops_only: only the looping ones (a beam's hum, a fire loop)."""
    editor = bpy.context.scene.sequence_editor
    if editor is None:
        return
    strips = editor.strips if hasattr(editor, "strips") else editor.sequences
    stopped = 0
    for strip in list(strips):
        if strip.get("vp_armature") != armature.name or strip.get("vp_voice") or (loops_only and not strip.get("vp_loop")):
            continue
        if strip.frame_final_start >= frame:
            strips.remove(strip)
            stopped += 1
        elif strip.frame_final_end > frame:
            strip.frame_final_end = int(frame)
            stopped += 1
    if stopped:
        Log.information(f"Stopped {stopped} sound(s) of the previous animation at frame {int(frame)}")


def add_animation_sounds(data, action, owner=None, start=None):
    """The game's sounds for this animation (gun handling, ability casts, ...) as sound strips in the Video Sequencer,
    each at its moment of the animation; they play with the timeline and go into renders with audio.
    start: the frame the animation starts at (a chained one starts after the previous); default the action's start."""
    sounds = data.get("Sounds") or []
    if not sounds:
        return
    scene = bpy.context.scene
    editor = scene.sequence_editor or scene.sequence_editor_create()
    strips = getattr(editor, "strips", None)
    if strips is None:
        strips = editor.sequences  # Blender before 4.4
    fps = scene.render.fps / scene.render.fps_base
    start = action.frame_range[0] if start is None else start

    # the same sound at the same moment only once (1st person arms and the gun often share a cue)
    def key(path, frame):
        return os.path.normcase(os.path.abspath(path.replace(LONG_PATH_PREFIX, ""))), int(frame)
    existing = {key(bpy.path.abspath(s.sound.filepath), s.frame_start) for s in strips if s.type == 'SOUND' and s.sound}

    added = 0
    for sound in sounds:
        path = disk_path(sound.get("Path") or "")
        if not path or not os.path.exists(path):
            continue
        frame = int(round(start + float(sound.get("Time") or 0) * fps))
        if key(path, frame) in existing:
            continue
        existing.add(key(path, frame))
        try:
            strip = strips.new_sound(name=sound.get("Name") or os.path.basename(path), filepath=path, channel=1, frame_start=frame)
        except Exception as e:
            Log.error(f"Could not add the sound {path}: {e}")
            continue
        # a free channel: sounds that overlap go on the rows above
        def taken(row):  # (this module has its own any())
            return next((o for o in strips if o != strip and o.channel == row and o.frame_final_start < strip.frame_final_end
                         and strip.frame_final_start < o.frame_final_end), None) is not None
        channel = 1
        while taken(channel):
            channel += 1
        strip.channel = channel
        if owner:
            strip["vp_armature"] = owner
        strip["vp_voice"] = bool(sound.get("Voice"))
        strip["vp_loop"] = bool(sound.get("Loop"))
        added += 1
        # a looping sound (an ultimate's hum, a beam) repeats until the animation ends, as in game
        if sound.get("Loop") and strip.frame_final_duration > 1:
            end = action.frame_range[1]
            copy_start = strip.frame_final_end
            copies = 0
            while copy_start < end and copies < 200:
                try:
                    again = strips.new_sound(name=strip.name, filepath=path, channel=strip.channel, frame_start=int(copy_start))
                except Exception:
                    break
                if again.channel != strip.channel:
                    again.channel = strip.channel
                if owner:
                    again["vp_armature"] = owner
                again["vp_voice"] = False
                again["vp_loop"] = True
                copy_start = again.frame_final_end
                copies += 1
    if added:
        Log.information(f"Added {added} sound(s) for {data.get('Name')} (Video Sequencer)")


def import_sound(data):
    """A sound from the app's Sounds tab: a sound strip at the current frame of the timeline."""
    scene = bpy.context.scene
    sounds = [{"Path": p, "Name": data.get("Name"), "Time": 0} for p in (data.get("Paths") or [])]
    if not sounds:
        return

    class Here:  # add_animation_sounds places sounds from an action's first frame: here, the current frame
        frame_range = (scene.frame_current, scene.frame_current)
    add_animation_sounds({"Name": data.get("Name"), "Sounds": sounds}, Here)
    show_message(f"{data.get('Name')}: added to the Video Sequencer at frame {scene.frame_current}.", icon='INFO')


def _apply_animation(data, armature):
    name = data.get("Name")
    path = data.get("AnimationPath")
    if armature is None:
        Log.error(f"No armature selected for animation {name}")
        show_message("Select the agent or gun armature first, then apply the animation again.", icon='ERROR')
        return

    Log.information(f"Applying animation {name} to {armature.name}")
    # bKeepProportions keeps the agent's own face/body proportions (Valorant animations share one base skeleton)
    def on_error(message):
        Log.error(message)
        show_message(message, icon='ERROR')

    psaimport(disk_path(path), context=bpy.context, oArmature=armature, bKeepProportions=True, bRealTime=True, bUpdateTimelineRange=True,
              error_callback=on_error)
    lower_action = armature.animation_data.action if armature.animation_data else None
    # montages that play several clips in a row (e.g. a character select intro, then its idle): the app sends every
    # clip; they're imported in order and joined into one action, back to back
    if len(clips := data.get("SequencePaths") or []) > 1 and lower_action:
        actions = [lower_action]
        for clip in clips[1:]:
            psaimport(disk_path(clip), context=bpy.context, oArmature=armature, bKeepProportions=True, bRealTime=True, bUpdateTimelineRange=True,
                      error_callback=on_error)
            if armature.animation_data and armature.animation_data.action not in actions:
                actions.append(armature.animation_data.action)
        lower_action = join_actions(armature, actions, name)
    repeat = max(1, min(int(data.get("Repeat") or 1), 20))
    lower_loops = bool(data.get("LowerLoops"))

    upper_path = data.get("UpperAnimationPath")
    if not upper_path:
        if lower_action and repeat > 1 and lower_loops:
            repeat_action(lower_action, repeat)
            _fit_timeline(lower_action)
        return

    # upper + lower body: legs from the first animation, everything else from this one (Riot's "_UB"/"_LB" halves,
    # or any two the user combines, e.g. an equip over a run)
    psaimport(disk_path(upper_path), context=bpy.context, oArmature=armature, bKeepProportions=True, bRealTime=True,
              bUpdateTimelineRange=True, error_callback=on_error)
    upper_action = armature.animation_data.action if armature.animation_data else None
    if not (lower_action and upper_action and lower_action != upper_action):
        return
    upper_loops = bool(data.get("UpperLoops"))

    # looping halves (runs, idles) repeat: as often as asked, and at least until they cover the other half
    lower_times = repeat if lower_loops else 1
    upper_times = repeat if upper_loops else 1
    lower_length, upper_length = _action_length(lower_action), _action_length(upper_action)
    if lower_loops and lower_length * lower_times < upper_length * upper_times:
        lower_times = math.ceil(upper_length * upper_times / lower_length)
    if upper_loops and upper_length * upper_times < lower_length * lower_times:
        upper_times = math.ceil(lower_length * lower_times / upper_length)
    if lower_times > 1:
        repeat_action(lower_action, lower_times)
    if upper_times > 1:
        repeat_action(upper_action, upper_times)
    ends = {"upper body": upper_length * upper_times, "lower body": lower_length * lower_times}
    shorter = min(ends, key=ends.get)
    if ends[shorter] < max(ends.values()) - 0.5:
        Log.information(f"The {shorter} animation ends at frame {int(ends[shorter])} and holds its last pose after that")
    merge_upper_lower(armature, lower_action, upper_action, name)


def join_actions(armature, actions, name):
    """Appends each action's keyframes after the previous one's end into the first action; removes the others."""
    first = actions[0]
    target = {(fc.data_path, fc.array_index): fc for fc in _action_fcurves(first)}
    offset = first.frame_range[1]
    for action in actions[1:]:
        start, end = action.frame_range
        for source in _action_fcurves(action):
            curve = target.get((source.data_path, source.array_index))
            if curve is None:
                curve = target[(source.data_path, source.array_index)] = _fcurve_new_on(first, armature, source.data_path, source.array_index)
            count = len(source.keyframe_points)
            coords = [0.0] * (count * 2)
            source.keyframe_points.foreach_get("co", coords)
            # the next clip starts where this one ends (skip its first frame when it lands on the seam)
            keys = [(offset + frame - start, value) for frame, value in zip(coords[0::2], coords[1::2])
                    if not (frame <= start + 1e-4 and len(curve.keyframe_points) and offset > 0)]
            old = len(curve.keyframe_points)
            curve.keyframe_points.add(len(keys))
            flat = [0.0] * ((old + len(keys)) * 2)
            curve.keyframe_points.foreach_get("co", flat)
            flat[old * 2:] = [x for key in keys for x in key]
            curve.keyframe_points.foreach_set("co", flat)
            for point in curve.keyframe_points[old:]:
                point.interpolation = 'LINEAR'
            curve.update()
        offset += end - start
        bpy.data.actions.remove(action)
    first.name = name
    armature.animation_data.action = first
    if hasattr(armature.animation_data, "action_slot") and len(first.slots) > 0:
        armature.animation_data.action_slot = first.slots[0]
    _fit_timeline(first)
    return first


def _action_length(action):
    start, end = action.frame_range
    return max(end - start, 1.0)


def _fit_timeline(action):
    start, end = action.frame_range
    bpy.context.scene.frame_start, bpy.context.scene.frame_end = int(start), int(end)


def repeat_action(action, times):
    """Plays a looping animation (run, walk, idle) several times in a row by copying its keyframes, like extending it
    by hand. The loop's last frame is its first pose again, so each copy starts where the previous one ends."""
    start, end = action.frame_range
    period = end - start
    if times <= 1 or period <= 0:
        return
    for curve in _action_fcurves(action):
        count = len(curve.keyframe_points)
        if count == 0:
            continue
        coords = [0.0] * (count * 2)
        curve.keyframe_points.foreach_get("co", coords)
        keys = [(coords[i], coords[i + 1]) for i in range(0, len(coords), 2)]
        extra = [(frame + period * copy, value) for copy in range(1, times) for frame, value in keys
                 if frame > start + 1e-4]  # the loop's first frame is the previous copy's last one
        if not extra:
            continue
        curve.keyframe_points.add(len(extra))
        flat = [x for key in keys + extra for x in key]
        curve.keyframe_points.foreach_set("co", flat)
        for point in curve.keyframe_points:
            point.interpolation = 'LINEAR'
        curve.update()


def _action_fcurves(action):
    if hasattr(action, "fcurves"):
        return action.fcurves
    from bpy_extras import anim_utils
    return anim_utils.action_get_channelbag_for_slot(action, action.slots[0]).fcurves


def lower_body_bones(armature):
    """Bones Riot's lower-body animations drive: the pelvis and legs, the IK targets and the bones above the
    "Splitter" bone (whose children are Spine1 = upper body, Pelvis = legs and the weapon aim bones)."""
    bones = armature.data.bones
    lower = set()
    if splitter := bones.get("Splitter"):
        lower.update(b.name for b in [splitter, *splitter.parent_recursive])
    for root in ("Pelvis", "IK_RootTarget"):
        if bone := bones.get(root):
            lower.add(bone.name)
            lower.update(child.name for child in bone.children_recursive)
    return lower


def merge_upper_lower(armature, lower_action, upper_action, name):
    """One action from a lower-body and an upper-body animation: the lower one's curves for the legs, the upper
    one's for everything else. Keeps the lower action (renamed) and removes the upper one."""
    lower_bones = lower_body_bones(armature)
    if not lower_bones:
        Log.warning("No Splitter/Pelvis bones found; can't split this skeleton into upper and lower body")
        return
    target = _action_fcurves(lower_action)
    existing = {(fc.data_path, fc.array_index): fc for fc in target}
    for source in _action_fcurves(upper_action):
        bone = source.data_path.split('"')[1] if '"' in source.data_path else None
        if bone is None or bone in lower_bones:
            continue
        curve = existing.get((source.data_path, source.array_index))
        if curve is None:
            curve = _fcurve_new_on(lower_action, armature, source.data_path, source.array_index)
        curve.keyframe_points.clear()
        count = len(source.keyframe_points)
        curve.keyframe_points.add(count)
        coords = [0.0] * (count * 2)
        source.keyframe_points.foreach_get("co", coords)
        curve.keyframe_points.foreach_set("co", coords)
        for point, original in zip(curve.keyframe_points, source.keyframe_points):
            point.interpolation = original.interpolation
        curve.update()
    lower_action.name = f"{name} (upper + lower)"
    armature.animation_data.action = lower_action
    if hasattr(armature.animation_data, "action_slot") and len(lower_action.slots) > 0:
        armature.animation_data.action_slot = lower_action.slots[0]
    start = min(lower_action.frame_range[0], upper_action.frame_range[0])
    end = max(lower_action.frame_range[1], upper_action.frame_range[1])
    bpy.data.actions.remove(upper_action)
    bpy.context.scene.frame_start, bpy.context.scene.frame_end = int(start), int(end)
    Log.information(f"Merged upper and lower body into {lower_action.name}")


def _moving_bones(action):
    """Bones whose keys actually change. Riot's animations key every bone (a face animation also keys the body, frozen
    in its rest pose), so being keyed doesn't mean being animated."""
    moving = set()
    for curve in _action_fcurves(action):
        if '"' not in curve.data_path or len(curve.keyframe_points) < 2:
            continue
        bone = curve.data_path.split('"')[1]
        if bone in moving:
            continue
        coords = [0.0] * (len(curve.keyframe_points) * 2)
        curve.keyframe_points.foreach_get("co", coords)
        values = coords[1::2]
        if max(values) - min(values) > 1e-4:
            moving.add(bone)
    return moving


def layer_actions(armature, base, layer, name):
    """The current animation (base) with another on top: the bones the new one moves take its keys, every other bone
    keeps the base's (a face animation over a body animation; an upper-body one over a run). A looping one repeats to
    cover the other; otherwise the shorter holds its last pose. Returns the result (a copy: base stays as it was)."""
    base_length, layer_length = _action_length(base), _action_length(layer)
    if layer.get("vp_loops") and layer_length < base_length - 0.5:
        repeat_action(layer, math.ceil(base_length / layer_length))
    result = base.copy()
    if base.get("vp_loops") and base_length < _action_length(layer) - 0.5:
        repeat_action(result, math.ceil(_action_length(layer) / base_length))
    result.name = f"{base.name} + {name}"
    result["vp_loops"] = bool(base.get("vp_loops")) and bool(layer.get("vp_loops"))
    armature.animation_data.action = result
    if hasattr(armature.animation_data, "action_slot") and len(result.slots) > 0:
        armature.animation_data.action_slot = result.slots[0]

    moving = _moving_bones(layer)
    target = {(fc.data_path, fc.array_index): fc for fc in _action_fcurves(result)}
    for source in _action_fcurves(layer):
        bone = source.data_path.split('"')[1] if '"' in source.data_path else None
        if bone not in moving:
            continue
        curve = target.get((source.data_path, source.array_index))
        if curve is None:
            curve = _fcurve_new_on(result, armature, source.data_path, source.array_index)
        curve.keyframe_points.clear()
        count = len(source.keyframe_points)
        curve.keyframe_points.add(count)
        coords = [0.0] * (count * 2)
        source.keyframe_points.foreach_get("co", coords)
        curve.keyframe_points.foreach_set("co", coords)
        for point, original in zip(curve.keyframe_points, source.keyframe_points):
            point.interpolation = original.interpolation
        curve.update()
    bpy.data.actions.remove(layer)
    _fit_timeline(result)
    Log.information(f"{name} on top of {base.name}: {len(moving)} bone(s) from it, the rest as before")
    return result


def continuous_quaternions(action):
    """q and -q are the same rotation, and the game's keys sometimes switch between them from one key to the next
    (Breach's ultimate fire: the weapon socket). Blender blends keys number by number, so between two such keys the
    rotation passes through nothing and the bone spins (seen between whole frames: motion blur, a shifted or chained
    clip). Each key takes the sign closest to the previous one."""
    quaternions = {}
    for curve in _action_fcurves(action):
        if curve.data_path.endswith("rotation_quaternion"):
            quaternions.setdefault(curve.data_path, {})[curve.array_index] = curve
    fixed = 0
    for parts in quaternions.values():
        if len(parts) != 4 or len({len(parts[i].keyframe_points) for i in range(4)}) != 1:
            continue
        count = len(parts[0].keyframe_points)
        coords = []
        for i in range(4):
            flat = [0.0] * (count * 2)
            parts[i].keyframe_points.foreach_get("co", flat)
            coords.append(flat)
        changed = False
        for k in range(1, count):
            if sum(coords[i][2 * k - 1] * coords[i][2 * k + 1] for i in range(4)) < 0:
                for i in range(4):
                    coords[i][2 * k + 1] = -coords[i][2 * k + 1]
                changed = True
        if changed:
            for i in range(4):
                parts[i].keyframe_points.foreach_set("co", coords[i])
                parts[i].update()
            fixed += 1
    if fixed:
        Log.information(f"{action.name}: {fixed} bone(s) kept from spinning between keys")


CHAIN_BLEND_SECONDS = 0.2  # between chained animations: the last pose eases into the next one's first


def chain_actions(armature, base, clip, name, blend_seconds=CHAIN_BLEND_SECONDS, cut=None):
    """The current animation (base), then another one after it: the clip's keys follow the base's end, after a short
    blend (easing from the base's last pose into the clip's first). cut: a frame inside the base where it stops instead
    (the rest is dropped, like an animation cancel). Returns the result (a copy: base stays as it was), the frame the
    clip starts at and the frame the base stops at."""
    scene = bpy.context.scene
    blend = max(1, round(blend_seconds * scene.render.fps / scene.render.fps_base))
    base_start, base_end = base.frame_range
    if cut is not None and not base_start < cut < base_end:
        Log.warning(f"Frame {cut} isn't inside the current animation ({int(base_start)}-{int(base_end)}): {name} plays after its end")
        cut = None
    if cut is not None:
        base_end = float(cut)
    clip_start = clip.frame_range[0]
    offset = base_end + blend - clip_start

    result = base.copy()
    result.name = f"{base.name} → {name}"
    result["vp_loops"] = False
    armature.animation_data.action = result
    if hasattr(armature.animation_data, "action_slot") and len(result.slots) > 0:
        armature.animation_data.action_slot = result.slots[0]
    target = {(fc.data_path, fc.array_index): fc for fc in _action_fcurves(result)}
    if cut is not None:
        for curve in target.values():
            _truncate_curve(curve, base_end)
    clip_curves = list(_action_fcurves(clip))

    # q and -q are the same rotation: a clip stored with the other sign would blend the long way round (a spin)
    flip = set()
    quaternions = {}
    for curve in clip_curves:
        if curve.data_path.endswith("rotation_quaternion"):
            quaternions.setdefault(curve.data_path, {})[curve.array_index] = curve
    for path, parts in quaternions.items():
        if len(parts) == 4 and all((path, i) in target for i in range(4)):
            before = [target[(path, i)].evaluate(base_end) for i in range(4)]
            after = [parts[i].evaluate(clip_start) for i in range(4)]
            if sum(a * b for a, b in zip(before, after)) < 0:
                flip.add(path)

    for source in clip_curves:
        key = (source.data_path, source.array_index)
        curve = target.get(key)
        if curve is None:
            # a bone the base doesn't animate stays at rest during it
            curve = target[key] = _fcurve_new_on(result, armature, *key)
            rest = 1.0 if (key[0].endswith("rotation_quaternion") and key[1] == 0) or key[0].endswith("scale") else 0.0
            curve.keyframe_points.add(2)
            curve.keyframe_points.foreach_set("co", [base_start, rest, base_end, rest])
        elif len(curve.keyframe_points) and curve.keyframe_points[-1].co[0] < base_end - 1e-3:
            curve.keyframe_points.insert(base_end, curve.evaluate(base_end), options={'FAST'})
        if len(curve.keyframe_points):
            last = curve.keyframe_points[-1]
            last.interpolation = 'SINE'
            last.easing = 'EASE_IN_OUT'
        sign = -1.0 if source.data_path in flip else 1.0
        count = len(source.keyframe_points)
        coords = [0.0] * (count * 2)
        source.keyframe_points.foreach_get("co", coords)
        old = len(curve.keyframe_points)
        curve.keyframe_points.add(count)
        flat = [0.0] * ((old + count) * 2)
        curve.keyframe_points.foreach_get("co", flat)
        flat[old * 2:] = [x for frame, value in zip(coords[0::2], coords[1::2]) for x in (frame + offset, value * sign)]
        curve.keyframe_points.foreach_set("co", flat)
        for point, original in zip(curve.keyframe_points[old:], source.keyframe_points):
            point.interpolation = original.interpolation
        curve.update()
    bpy.data.actions.remove(clip)
    _fit_timeline(result)
    Log.information(f"{name} plays after {base.name}, from frame {int(base_end + blend)}"
                    + (f" (cut at frame {int(base_end)})" if cut is not None else ""))
    return result, base_end + blend, base_end


def _truncate_curve(curve, end):
    """The curve stops at this frame: a key there (its value at that moment) and none after it."""
    points = curve.keyframe_points
    if not len(points) or points[-1].co[0] <= end + 1e-4:
        return
    value = curve.evaluate(end)
    kept = [(p.co[0], p.co[1], p.interpolation) for p in points if p.co[0] < end - 1e-4]
    points.clear()
    kept.append((end, value, 'LINEAR'))
    points.add(len(kept))
    points.foreach_set("co", [x for frame, v, _ in kept for x in (frame, v)])
    for point, (_, _, interpolation) in zip(points, kept):
        point.interpolation = interpolation
    curve.update()


def _fcurve_new_on(action, owner, data_path, index):
    ad = owner.animation_data
    if ad.action != action:
        ad.action = action
    if hasattr(action, "fcurves"):
        return action.fcurves.new(data_path, index=index)
    return action.fcurve_ensure_for_datablock(owner, data_path, index=index)


def fix_valorant_materials(materials, summary):
    """Valorant-specific fixes on top of Blender's USD material import:
    - MRA textures pack Metallic (R), Roughness (G), AO (B); the USD export wires them as G/B.
    - Stylized foliage fills the transparent part of its diffuse texture with the material's "AO color"."""
    mra_fixed = ao_fixed = flat_colored = 0
    for material in materials:
        if not material.use_nodes:
            continue
        nodes = material.node_tree.nodes
        links = material.node_tree.links
        principled = next((n for n in nodes if n.type == 'BSDF_PRINCIPLED'), None)
        if principled is None:
            continue

        for separate in [n for n in nodes if n.type == 'SEPARATE_COLOR']:
            source = separate.inputs['Color'].links[0].from_node if separate.inputs['Color'].is_linked else None
            if source is None or source.type != 'TEX_IMAGE' or source.image is None or '_MRA' not in source.image.name.upper():
                continue
            for link in [l for l in links if l.from_node == separate]:
                links.remove(link)
            links.new(separate.outputs['Red'], principled.inputs['Metallic'])
            links.new(separate.outputs['Green'], principled.inputs['Roughness'])
            mra_fixed += 1

        info = find_material_info(summary, material.name) or {}
        base_input = principled.inputs['Base Color']
        effect = effect_surface(material.name, info)
        if effect == "hidden":
            material.user_remap(_hidden_material())  # few materials: the slow remap is fine here
            continue
        if effect == "water":
            for link in list(base_input.links):
                links.remove(link)
            base_input.default_value = WATER_COLOR
            principled.inputs['Roughness'].default_value = 0.05
            principled.inputs['Metallic'].default_value = 0.0
            continue
        if not base_input.is_linked:
            # untextured material left white by the USD import: use its own color parameter if it has one
            color = next((c for name, c in (info.get("Colors") or {}).items()
                          if isinstance(c, dict) and "R" in c and not NON_ALBEDO_COLORS.search(name)), None)
            if color is not None:
                rgb = [color["R"], color["G"], color["B"]]
                peak = max(rgb)
                if peak > 1.0:  # glow colors are HDR (e.g. 1500, 0, 0): keep the hue, not the brightness
                    rgb = [c / peak for c in rgb]
                base_input.default_value = (*rgb, 1.0)
                flat_colored += 1
        ao = (info.get("Colors") or {}).get("AO color")
        base_link = principled.inputs['Base Color'].links[0] if principled.inputs['Base Color'].is_linked else None
        if ao and base_link is not None and not principled.inputs['Alpha'].is_linked:
            diffuse = next((n for n in nodes if n.type == 'TEX_IMAGE' and n.image and n.image.channels == 4 and
                            '_DF' in n.image.name.upper()), None)
            if diffuse is not None:
                mix = nodes.new('ShaderNodeMix')
                mix.data_type = 'RGBA'
                mix.inputs['A'].default_value = (ao["R"], ao["G"], ao["B"], 1.0)
                links.new(diffuse.outputs['Alpha'], mix.inputs['Factor'])
                links.new(base_link.from_socket, mix.inputs['B'])
                links.new(mix.outputs['Result'], principled.inputs['Base Color'])
                ao_fixed += 1
    Log.information(f"Material fixes: {mra_fixed} MRA, {ao_fixed} foliage AO color, {flat_colored} flat colors")


WATER_COLOR = (0.01, 0.045, 0.05, 1.0)  # dark teal, like the maps' ponds and rivers
WATER_SURFACE = re.compile(r"water_?surface|river_?(water|surface)|pond", re.IGNORECASE)
# thin water sheets running over walls, and shadow/foam decals: effects drawn on top of a real surface
WATER_FILM = re.compile(r"water_.*(wall|marble)", re.IGNORECASE)
SHADOW_DECAL = re.compile(r"shadow|foam", re.IGNORECASE)


def effect_surface(name, info):
    """'water', 'hidden' or None for a map material the Valorant shaders didn't rebuild."""
    if WATER_FILM.search(name):
        return "hidden"
    if WATER_SURFACE.search(name):
        return "water"
    if info.get("BlendMode") == 4 or SHADOW_DECAL.search(name):  # 4 = Modulate: only darkens what's under it
        return "hidden"
    return None


# Color parameters that aren't the surface color itself.
NON_ALBEDO_COLORS = re.compile(r"AO|Emissi|Lightmass|Min Light|Specular|Sparkle|Channel|Normal|Mult|Impurity|Glow|Fog",
                               re.IGNORECASE)


# Materials that only exist in the editor or as effects: developer grids/blockouts, light-shaft cards and smoke/glow effect meshes.
HELPER_MATERIALS = re.compile(r"^(M_SuperGrid|WorldGridMaterial|M_Flat_|MI_LS_|M_LightShaft|LightShaft|OmenFunLand|MI_Smoke|MI_SpriteGlow|Callout_Volume|[A-Za-z]*_HeadHeightRef)", re.IGNORECASE)


def _hidden_material():
    material = bpy.data.materials.get("VP_Hidden")
    if material is None:
        material = bpy.data.materials.new("VP_Hidden")
        material.use_nodes = True
        nodes = material.node_tree.nodes
        nodes.clear()
        output = nodes.new('ShaderNodeOutputMaterial')
        transparent = nodes.new('ShaderNodeBsdfTransparent')
        material.node_tree.links.new(transparent.outputs['BSDF'], output.inputs['Surface'])
        material.surface_render_method = 'DITHERED'
    return material


def remove_helper_objects(objects):
    """Deletes imported objects whose materials are all editor helpers (they show up as white/grid shapes); on objects
    that mix helpers with real materials, only the helper parts are made invisible."""
    hidden = 0
    doomed = []
    for obj in list(objects):
        if obj.type != 'MESH':
            continue
        helpers = [HELPER_MATERIALS.match(re.sub(r"\.\d{3}$", "", slot.material.name)) is not None if slot.material else False
                   for slot in obj.material_slots]
        if helpers and all(helpers):
            doomed.append(obj)
        elif True in helpers:  # (this module defines its own any())
            for slot, helper in zip(obj.material_slots, helpers):
                if helper:
                    slot.material = _hidden_material()
            hidden += 1
    meshes = {obj.data for obj in doomed if obj.data is not None}
    bpy.data.batch_remove(doomed)  # one batch: removing objects one by one is very slow in big scenes
    bpy.data.batch_remove([mesh for mesh in meshes if mesh.users == 0])
    removed = len(doomed)
    Log.information(f"Removed {removed} editor helper objects (blockout grids, light shafts), hid helper parts of {hidden}")


SKY_MESHES = re.compile(r"sky ?(dome|sphere|box)|^sky_|_sky_", re.IGNORECASE)


def setup_map_lighting(objects, map_name):
    """Daylight like in game: the map's own sun (UE bakes everything else into lightmaps, which don't export),
    a soft sky fill, and the sky dome kept out of the lighting so it doesn't shade the whole map."""
    lights = [o for o in objects if o.type == 'LIGHT']
    suns = []
    for light in lights:
        if light.data.type != 'SUN':
            continue
        direction = (light.matrix_world.to_3x3() @ Vector((0, 0, -1))).normalized()
        # the map's light actor is the parent ("DirectionalLight_Sun", "DirectionalLight_Aurora_FillLight")
        names = f"{light.name} {light.parent.name if light.parent else ''}".lower()
        is_sun = "sun" in names and "fill" not in names
        if is_sun and direction.z > -0.15:
            # a sunset sun right at the horizon (Summit): keep its heading, just low enough to graze the buildings
            direction = Vector((direction.x, direction.y, 0)).normalized() * 0.99 + Vector((0, 0, -0.15))
            direction.normalize()
        if direction.z < -0.05:  # skip the "look up" helper lights that shine upward
            suns.append((is_sun, light.data.energy, direction, tuple(light.data.color), names.strip()))
    for obj in objects:
        if obj.type == 'MESH' and (SKY_MESHES.search(obj.name) or (obj.parent and SKY_MESHES.search(obj.parent.name))):
            obj.visible_shadow = False
            obj.visible_diffuse = False
            obj.visible_glossy = False
    # local UE lights only add to the baked lighting; their units don't match Blender's
    bpy.data.batch_remove(lights)

    scene = bpy.context.scene
    if suns:
        # the light named "Sun" (Summit also has a stronger white fill light pointing straight down), else the brightest
        _, energy, direction, color, sun_name = max(suns, key=lambda s: (s[0], s[1]))
        Log.information(f"Map sun: {sun_name} (of {len(suns)} directional lights)")
        # the USD export mirrors the light direction on X (checked against in-game shadows)
        direction = Vector((-direction.x, direction.y, direction.z))
        sun_data = bpy.data.lights.new(f"{map_name} Sun", 'SUN')
        # relative to Ascent's sun (intensity 7, imported as 28): brighter/dimmer maps keep their difference
        sun_data.energy = MAP_SUN_STRENGTH * min(max(energy / 28.0, 0.5), 1.5)
        sun_data.color = color
        sun_data.angle = math.radians(1.5)
        sun = bpy.data.objects.new(f"{map_name} Sun", sun_data)
        sun.rotation_mode = 'QUATERNION'
        sun.rotation_quaternion = direction.to_track_quat('-Z', 'Y')
        target = next((c for c in bpy.data.collections if c.name.startswith(map_name)), scene.collection)
        target.objects.link(sun)

    world = bpy.data.worlds.get("Valorant Sky") or bpy.data.worlds.new("Valorant Sky")
    world.use_nodes = True
    background = next((n for n in world.node_tree.nodes if n.type == 'BACKGROUND'), None)
    if background is not None:
        background.inputs['Color'].default_value = MAP_SKY_COLOR
        background.inputs['Strength'].default_value = MAP_SKY_STRENGTH
    scene.world = world
    scene.view_settings.view_transform = 'Standard'  # closest to the game's saturated look (AgX washes it out)
    Log.information(f"Map lighting: {'sun from the map' if suns else 'no sun found'}, sky fill")


MAP_SUN_STRENGTH = 5.0
MAP_SKY_COLOR = (0.62, 0.72, 0.88, 1.0)
MAP_SKY_STRENGTH = 0.8


def share_identical_meshes(objects):
    """The USD import gives every placed copy of an asset its own mesh data. Copies with identical geometry, materials
    and vertex colors now share one mesh (like linked duplicates): same look, far less memory."""
    import numpy
    first = {}
    replaced = []
    for obj in objects:
        if obj.type != 'MESH' or obj.data is None or obj.data.users > 1:
            continue
        mesh = obj.data
        if len(mesh.vertices) == 0 or mesh.shape_keys is not None:
            continue
        coords = numpy.empty(len(mesh.vertices) * 3, dtype=numpy.float32)
        mesh.vertices.foreach_get("co", coords)
        corners = numpy.empty(len(mesh.loops), dtype=numpy.int32)
        mesh.loops.foreach_get("vertex_index", corners)
        slots = numpy.empty(len(mesh.polygons), dtype=numpy.int32)
        mesh.polygons.foreach_get("material_index", slots)
        parts = [len(mesh.vertices), len(mesh.polygons), len(mesh.loops), coords.tobytes(), corners.tobytes(),
                 slots.tobytes(), tuple(m.name if m else "" for m in mesh.materials)]
        for layer in mesh.uv_layers:  # same shape can be textured differently
            uvs = numpy.empty(len(mesh.loops) * 2, dtype=numpy.float32)
            layer.data.foreach_get("uv", uvs)
            parts.append((layer.name, uvs.tobytes()))
        for name in ("displayColor", "displayOpacity"):  # painted colors differ per placed copy
            attribute = mesh.attributes.get(name)
            if attribute is not None:
                size = 4 if attribute.data_type in ('FLOAT_COLOR', 'BYTE_COLOR') else 1
                values = numpy.empty(len(attribute.data) * size, dtype=numpy.float32)
                attribute.data.foreach_get("color" if size == 4 else "value", values)
                parts.append((name, attribute.domain, values.tobytes()))
        key = hash(tuple(parts))
        original = first.get(key)
        if original is None:
            first[key] = mesh
        else:
            obj.data = original
            replaced.append(mesh)
    unused = [m for m in replaced if m.users == 0]
    bpy.data.batch_remove(unused)
    return len(unused)


def import_map(data, assets_root=""):
    name = data.get("Name")
    path = data.get("MapPath")
    Log.information(f"Importing map {name} from {path}")
    options = dict(filepath=path, import_usd_preview=True, support_scene_instancing=True, import_visible_only=True,
                   create_collection=True, import_lights=True, import_cameras=False, set_frame_range=False,
                   read_mesh_colors=True, apply_unit_conversion_scale=True,
                   # one Blender material per Valorant material instead of a copy per mesh
                   mtl_name_collision_mode='REFERENCE_EXISTING',
                   # collision/trigger shapes are never visible in game
                   import_shapes=False, create_world_material=False)
    materials_before = set(bpy.data.materials)
    objects_before = set(bpy.data.objects)
    timings = []
    clock = time.perf_counter()

    def step(label):
        nonlocal clock
        now = time.perf_counter()
        timings.append(f"{label} {now - clock:.1f}s")
        clock = now

    window_manager = bpy.context.window_manager
    if window_manager.windows:
        with bpy.context.temp_override(window=window_manager.windows[0]):
            bpy.ops.wm.usd_import(**options)
    else:
        bpy.ops.wm.usd_import(**options)
    step("USD read")

    summary = {}
    materials_path = data.get("MaterialsPath")
    if materials_path and os.path.exists(materials_path):
        with open(materials_path, encoding="utf-8") as file:
            summary = json.load(file)
    remove_helper_objects([o for o in bpy.data.objects if o not in objects_before])
    new_objects = [o for o in bpy.data.objects if o not in objects_before]
    step("helpers")
    new_materials = merge_duplicate_materials([m for m in bpy.data.materials if m not in materials_before], new_objects)
    step("merge materials")
    shared = share_identical_meshes(new_objects)
    Log.information(f"Shared mesh data: {shared} duplicate meshes removed")
    step("share meshes")
    base, blend, kept = rebuild_materials(new_materials, summary, assets_root)
    Log.information(f"Valorant shaders: {base} base, {blend} two-layer blend, {kept} kept as imported")
    step("shaders")
    fix_valorant_materials(new_materials, summary)  # fallback fixes for materials that weren't rebuilt
    step("fallback fixes")
    add_default_vertex_colors(new_objects)
    step("vertex colors")
    setup_map_lighting(new_objects, name)
    step("lighting")
    Log.information(f"Imported map {name} ({', '.join(timings)})")


last_import_main = None  # the main body of the latest import (a gun's body, not its magazine or scope)


def select_only(obj):
    """Makes obj the only selected and the active object (None: nothing selected), like clicking it."""
    for other in list(bpy.context.selected_objects):
        other.select_set(False)
    bpy.context.view_layer.objects.active = obj
    if obj is not None:
        obj.select_set(True)


def import_scene(data):
    """A scene from the app, in order: the agent, the gun in the agent's hand, then the animations on each. Each step
    is a normal import/animation; the scene only picks which armature is selected before it."""
    rigs = {}  # "agent:TP" / "agent:FP" / "agent:CS" / "gun" / "ability" -> armature
    focus = None  # the agent armature the scene used (selected at the end, like after importing it)
    for step in data.get("Steps") or []:
        settings = step.get("Settings") or {}
        role = settings.get("SceneRole")
        target = step.get("SceneTarget") or settings.get("SceneTarget")
        if target and target not in rigs:
            what = "animation" if (step.get("Data") or {}).get("Type") == "Animation" else "gun"
            Log.warning(f"Scene: no {target} armature for the {what}")
            if what == "animation":
                show_message(f"The scene's agent has no armature for this animation ({target}); it was skipped.", icon='ERROR')
                continue
        select_only(rigs.get(target))  # what the user sees selected; the steps get their target directly
        if target and target.startswith("agent:") and target in rigs:
            focus = rigs[target]
        before = set(bpy.data.objects)
        if (step.get("Data") or {}).get("Type") == "Animation":
            if target and target in rigs:
                import_animation(step.get("Data"), armature=rigs[target])
            else:
                import_animation(step.get("Data"))
        else:
            import_response(step, holder=rigs.get(target) if target else None, use_selection=False)
        new_rigs = [o for o in bpy.data.objects if o not in before and o.type == 'ARMATURE']
        if role == "agent":
            for rig in new_rigs:
                rigs.setdefault("agent:" + rig.name.split("_")[0].upper(), rig)
        elif role == "ability" and new_rigs:
            # the ability's rig, which its animation goes on (Jett's knife rig, Chamber's guns)
            rigs["ability"] = last_import_main if last_import_main in new_rigs else new_rigs[0]
        elif role == "gun" and new_rigs:
            # the gun's body (its magazine and scope are armatures too, and come first by name for some skins)
            rigs["gun"] = last_import_main if last_import_main in new_rigs else                 next((r for r in new_rigs if r.name.upper().startswith("GN_")), new_rigs[0])
    select_only(focus or rigs.get("agent:TP") or next(iter(rigs.values()), None))
    Log.information(f"Imported scene {data.get('Name')} ({len(data.get('Steps') or [])} steps)")


def import_response(response, holder=None, use_selection=True):
    """holder: the armature a gun/buddy goes on (a scene says so); otherwise (use_selection) the selected armature"""
    if (response.get("Data") or {}).get("Type") == "Scene":
        import_scene(response.get("Data"))
        return
    if (response.get("Data") or {}).get("Type") == "Animation":
        import_animation(response.get("Data"))
        return
    if (response.get("Data") or {}).get("Type") == "Map":
        import_map(response.get("Data"), response.get("AssetsRoot") or "")
        return
    if (response.get("Data") or {}).get("Type") == "Sound":
        import_sound(response.get("Data"))
        return
    if (response.get("Data") or {}).get("Parts") is None:
        # something a newer app sends that this add-on doesn't know
        kind = (response.get("Data") or {}).get("Type") or "unknown"
        Log.error(f"Unsupported message from Valorant Porting ({kind})")
        show_message("This add-on is older than the Valorant Porting app. Install the add-on from the app's "
                     "\"Blender Add-ons\" folder (Edit > Preferences > Add-ons > Install from Disk), then restart Blender.",
                     icon='ERROR')
        return

    # whatever was selected before the import: a gun is attached to a selected agent, a buddy to a selected gun
    holder = holder or (find_selected_armature() if use_selection else None)

    import_shaders("VALORANT_Weapon.blend")
    import_shaders("VALORANT_Agent.blend")

    global import_assets_root
    import_assets_root = response.get("AssetsRoot")

    global import_settings
    import_settings = response.get("Settings")

    global import_data
    import_data = response.get("Data")

    name = import_data.get("Name")
    import_type = import_data.get("Type")

    Log.information(f"Received Import for {import_type}: {name}")
    new_collection = create_collection(name)

    def constraint_object(child: bpy.types.Object, parent: bpy.types.Object, bone: str, loc, rot):
        if parent is not None:
            constraint = child.constraints.new('CHILD_OF')
            constraint.target = parent
            if bone is not None: constraint.subtarget = bone
            child.rotation_mode = 'XYZ'
            constraint.inverse_matrix = Matrix()
            if loc is not None:
                child.location = (0.01 * loc["X"], 0.01 * loc["Y"], 0.01 * loc["Z"])
            if rot is not None:
                child.rotation_euler = (rot["Pitch"], rot["Yaw"], rot["Roll"])

    imported_parts = []

    def import_part(parts):
        for part in parts:
            imported_part = import_mesh(part.get("MeshPath"))
            attachments = part.get("Attatchments")

            if imported_part is None:
                continue
            has_armature = imported_part.type == "ARMATURE"
            if has_armature:
                tag = animation_filter_tag(import_settings.get("AnimationFilterKey"), imported_part)
                if tag:
                    imported_part["vp_filter"] = tag
                mesh = mesh_from_armature(imported_part)
            else:
                mesh = imported_part
            bpy.context.view_layer.objects.active = mesh

            if placement := part.get("Placement"):
                place_part(imported_part, placement)

            imported_parts.append({
                "MeshName": part.get("MeshName"),
                "BoneAttachment": part.get("AttachToBone"),
                "Attachments": attachments,
                "Parent": imported_part,
                "Mesh": mesh
            })

            remove_disabled_sections(mesh, part.get("DisabledSections") or [])

            for material in part.get("Materials"):
                index = material.get("SlotIndex")
                if len(mesh.material_slots) > index:
                    import_material(mesh.material_slots.values()[index], material, import_type)

            for override_material in part.get("OverrideMaterials"):
                index = override_material.get("SlotIndex")
                if len(mesh.material_slots) > index:
                    import_material(mesh.material_slots.values()[index], override_material, import_type)
            for style_material in part.get("StyleMaterials"):
                index = style_material.get("SlotIndex")
                if len(mesh.material_slots) > index:
                    import_material(mesh.material_slots.values()[index], style_material, import_type)

            # ability props: a section with no material in the files (Fade's Haunt orb body; the game draws it with
            # effects) takes the prop's own material instead of plain white
            if import_type == "Ability" and part.get("Materials"):
                filled = {m.get("SlotIndex") for m in part.get("Materials")}
                source = mesh.material_slots.values()[min(filled)] if min(filled) < len(mesh.material_slots) else None
                for index, slot in enumerate(mesh.material_slots.values()):
                    if index not in filled and source is not None and source.material is not None:
                        slot.material = source.material

    import_part(import_data.get("Parts"))

    # parts the game hangs on the model's bones (Jett's Blade Storm: a knife on each rig bone Knife1-5)
    rig = next((p["Parent"] for p in imported_parts if p["Parent"].type == 'ARMATURE'), None)
    for imported_part in imported_parts:
        if (bone_attachment := imported_part.get("BoneAttachment")) and rig is not None:
            attach_part_to_bone(imported_part["Parent"], rig, bone_attachment)

    if import_settings.get("FirstPersonCamera"):
        add_first_person_camera([p["Parent"] for p in imported_parts], new_collection)

    # attachments: the parts of this import, by the name the app gave them (a name lookup in the scene would find
    # the same part of an earlier import of this gun, e.g. "Scope" instead of "Scope.001")
    parts_by_name = {p["MeshName"]: p["Parent"] for p in imported_parts if p.get("MeshName")}
    for imported_part in imported_parts:
        attachments = imported_part.get("Attachments")
        parent_obj = imported_part.get("Parent")
        for attachment in attachments:
            child_name = attachment.get("AttatchmentName")
            if child_name is not None:
                child_obj = parts_by_name.get(child_name)
                if child_obj is not None:
                    constraint_object(child_obj, parent_obj, attachment.get("BoneName"), attachment.get("Offset"), attachment.get("Rotation"))
        if new_collection not in parent_obj.users_collection:
            new_collection.objects.link(parent_obj)

    # the main body: the first part that isn't itself attached to another part (scopes, magazines, ...)
    main = next((p["Parent"] for p in imported_parts
                 if not [c for c in p["Parent"].constraints if c.type == 'CHILD_OF']), imported_parts[0]["Parent"]) if imported_parts else None
    global last_import_main
    last_import_main = main
    if main is not None and holder is not None:
        if import_type == "Weapon":
            attach_to_bone(main, holder, WEAPON_SOCKET_BONE, WEAPON_SOCKET_ROTATION, "hand")
        elif import_type == "GunBuddy":
            attach_to_bone(main, holder, BUDDY_SOCKET_BONE, Matrix(), "gun")
        elif import_type == "Ability":
            # an ability sent while an agent is selected: where the agent holds it (Chamber's guns in the hand,
            # Jett's Blade Storm knives on her left weapon socket, or her camera in 1st person)
            first_person = "Camera" in holder.data.bones and holder.name.upper().startswith("FP")
            socket = import_settings.get("HoldSocket1P" if first_person else "HoldSocket3P") or WEAPON_SOCKET_BONE
            bone, rotation = HOLD_SOCKETS.get(socket, (socket, WEAPON_SOCKET_ROTATION))
            if bone not in holder.data.bones:
                bone, rotation = WEAPON_SOCKET_BONE, WEAPON_SOCKET_ROTATION
            attach_to_bone(main, holder, bone, rotation, "hand" if bone == WEAPON_SOCKET_BONE else bone)


def register():
    import_event = threading.Event()

    global server
    server = Receiver(import_event)
    server.start()

    def handler():
        if not server.messages.empty():  # one message per tick, in the order they came
            try:
                missing_textures.clear()
                import_response(server.messages.get_nowait())
                if missing_textures:
                    names = sorted(missing_textures)
                    Log.error(f"{len(names)} texture(s) not found: {', '.join(names)}")
                    show_message(f"{len(names)} texture(s) weren't found on disk (they show purple), e.g. {names[0]}. "
                                 "Send it again from the app; if it stays purple, check the app's Assets folder is readable.",
                                 icon='ERROR')
                # imports run from a timer, outside any operator: without an undo step of their own, Ctrl+Z
                # afterwards can step into a half-undone state and crash Blender
                try:
                    bpy.ops.ed.undo_push(message="Valorant Porting import")
                except Exception:
                    pass
            except Exception as e:
                # Never let one failed import stop the timer (Blender unregisters timers that raise).
                Log.error(f"Import failed: {e}")
                traceback.print_exc()
                show_message(f"Valorant Porting: import failed ({e}). See the system console for details.", icon='ERROR')
        return 0.01

    global import_handler
    import_handler = handler
    bpy.app.timers.register(handler, persistent=True)
    bpy.app.timers.register(watch_selection, persistent=True)


def unregister():
    server.stop()
    if bpy.app.timers.is_registered(import_handler):
        bpy.app.timers.unregister(import_handler)
    if bpy.app.timers.is_registered(watch_selection):
        bpy.app.timers.unregister(watch_selection)
