import bpy
from pathlib import Path
import json
import os
import socket
import threading
import re
import traceback
import bpy
import os
import bpy.props
from mathutils import Matrix, Vector
import math
from .valorant_psk_psa_b5 import pskimport, psaimport
from .valorant_shaders import rebuild_materials, add_default_vertex_colors

bl_info = {
    "name": "Valorant Porting",
    "author": "Half, BK, Zain, DeveloperChipmunk",
    "version": (1, 5, 1),
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
        self.data = None
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
                self.data = json.loads(data_string)
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


def import_mesh(path: str) -> bpy.types.Object:
    path = resolve_asset_path(path)
    base_path = os.path.join(import_assets_root, path.split(".")[0])

    candidates = [base_path + "_LOD0"]
    if not base_path.endswith("_Skelmesh"):
        candidates.append(base_path + "_Skelmesh_LOD0")

    for mesh_path in candidates:
        if os.path.exists(mesh_path + ".psk"):
            pskimport(
                mesh_path + ".psk",
                bReorientBones=import_settings.get("ReorientBones"),
                bScaleDown=True,
                bToSRGB=False)
            return bpy.context.active_object

        if os.path.exists(mesh_path + ".pskx"):
            pskimport(
                mesh_path + ".pskx",
                bScaleDown=True,
                bToSRGB=False)
            return bpy.context.active_object

    print(f"[DEBUG] Mesh not found. Raw path from app: {path!r}")
    print(f"[DEBUG] AssetsRoot: {import_assets_root!r}")
    print(f"[DEBUG] Computed base_path: {base_path!r}")
    return None

def import_texture(path: str) -> bpy.types.Image:
    path, name = path.split(".")
    if existing := bpy.data.images.get(name):
        return existing

    raw_path = path[1:] if path.startswith("/") else path
    translated_path = resolve_asset_path(path)

    for candidate in [translated_path, raw_path]:
        texture_path = os.path.join(import_assets_root, candidate + ".png")
        if os.path.exists(texture_path):
            return bpy.data.images.load(texture_path, check_existing=True)

    print(f"[DEBUG] Texture not found in either location. Tried: "
          f"{os.path.join(import_assets_root, translated_path + '.png')!r} and "
          f"{os.path.join(import_assets_root, raw_path + '.png')!r}")
    return None


def import_material(target_slot: bpy.types.MaterialSlot, material_data, mat_type):
    material_name = material_data.get("MaterialName")
    # NOTE: Blender 5.0+ made Material.use_nodes always return True (deprecated),
    # AND new materials now come pre-populated with a default node tree (Principled BSDF +
    # Material Output) automatically. Neither of those can be used anymore to tell "a fresh
    # placeholder material" apart from "one we already fully built." Use our own explicit
    # marker instead, set at the bottom of this function once building is actually done.
    if (existing := bpy.data.materials.get(material_name)) and existing.get("vp_built") is True:
        target_slot.material = existing
        return
    target_material = target_slot.material
    if target_material.name.casefold() != material_name.casefold():
        target_material = target_material.copy()
        target_material.name = material_name
        target_slot.material = target_material
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
        if mat_type == "Character":
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

    # mark this material as fully built so future lookups can safely reuse it
    target_material["vp_built"] = True


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


def import_animation(data):
    name = data.get("Name")
    path = data.get("AnimationPath")
    armature = find_selected_armature()
    if armature is None:
        Log.error(f"No armature selected for animation {name}")
        show_message("Select the agent or gun armature first, then apply the animation again.", icon='ERROR')
        return

    Log.information(f"Applying animation {name} to {armature.name}")
    # bKeepProportions keeps the agent's own face/body proportions (Valorant animations share one base skeleton)
    def on_error(message):
        Log.error(message)
        show_message(message, icon='ERROR')

    psaimport(path, context=bpy.context, oArmature=armature, bKeepProportions=True, bUpdateTimelineRange=True,
              error_callback=on_error)


def fix_valorant_materials(materials, summary):
    """Valorant-specific fixes on top of Blender's USD material import:
    - MRA textures pack Metallic (R), Roughness (G), AO (B); the USD export wires them as G/B.
    - Stylized foliage fills the transparent part of its diffuse texture with the material's "AO color"."""
    mra_fixed = ao_fixed = 0
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

        info = summary.get(re.sub(r"\.\d{3}$", "", material.name), {})
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
    Log.information(f"Material fixes: {mra_fixed} MRA, {ao_fixed} foliage AO color")


# Materials that only exist in the editor or as effects: developer grids/blockouts, light-shaft cards and smoke/glow effect meshes.
HELPER_MATERIALS = re.compile(r"^(M_SuperGrid|WorldGridMaterial|M_Flat_|MI_LS_|M_LightShaft|LightShaft|OmenFunLand|MI_Smoke|MI_SpriteGlow)", re.IGNORECASE)


def remove_helper_objects(objects):
    """Deletes imported objects whose materials are all editor helpers (they show up as white/grid shapes)."""
    removed = 0
    for obj in list(objects):
        if obj.type != 'MESH':
            continue
        materials = [slot.material for slot in obj.material_slots if slot.material]
        if materials and all(HELPER_MATERIALS.match(re.sub(r"\.\d{3}$", "", m.name)) for m in materials):
            bpy.data.objects.remove(obj, do_unlink=True)
            removed += 1
    Log.information(f"Removed {removed} editor helper objects (blockout grids, light shafts)")


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
        if direction.z < -0.05:  # skip the "look up" helper lights that shine upward
            suns.append((light.data.energy, direction, tuple(light.data.color)))
    for obj in objects:
        if obj.type == 'MESH' and (SKY_MESHES.search(obj.name) or (obj.parent and SKY_MESHES.search(obj.parent.name))):
            obj.visible_shadow = False
            obj.visible_diffuse = False
            obj.visible_glossy = False
    for light in lights:  # local UE lights only add to the baked lighting; their units don't match Blender's
        bpy.data.objects.remove(light, do_unlink=True)

    scene = bpy.context.scene
    if suns:
        _, direction, color = max(suns, key=lambda s: s[0])
        # the USD export mirrors the light direction on X (checked against in-game shadows)
        direction = Vector((-direction.x, direction.y, direction.z))
        sun_data = bpy.data.lights.new(f"{map_name} Sun", 'SUN')
        sun_data.energy = MAP_SUN_STRENGTH
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


def import_map(data, assets_root=""):
    name = data.get("Name")
    path = data.get("MapPath")
    Log.information(f"Importing map {name} from {path}")
    options = dict(filepath=path, import_usd_preview=True, support_scene_instancing=True, import_visible_only=True,
                   create_collection=True, import_lights=True, import_cameras=False, set_frame_range=False,
                   read_mesh_colors=True, apply_unit_conversion_scale=True)
    materials_before = set(bpy.data.materials)
    objects_before = set(bpy.data.objects)
    window_manager = bpy.context.window_manager
    if window_manager.windows:
        with bpy.context.temp_override(window=window_manager.windows[0]):
            bpy.ops.wm.usd_import(**options)
    else:
        bpy.ops.wm.usd_import(**options)

    summary = {}
    materials_path = data.get("MaterialsPath")
    if materials_path and os.path.exists(materials_path):
        with open(materials_path, encoding="utf-8") as file:
            summary = json.load(file)
    remove_helper_objects([o for o in bpy.data.objects if o not in objects_before])
    new_materials = [m for m in bpy.data.materials if m not in materials_before]
    base, blend, kept = rebuild_materials(new_materials, summary, assets_root)
    Log.information(f"Valorant shaders: {base} base, {blend} two-layer blend, {kept} kept as imported")
    fix_valorant_materials(new_materials, summary)  # fallback fixes for materials that weren't rebuilt
    add_default_vertex_colors([o for o in bpy.data.objects if o not in objects_before])
    setup_map_lighting([o for o in bpy.data.objects if o not in objects_before], name)
    Log.information(f"Imported map {name}")


def import_response(response):
    if (response.get("Data") or {}).get("Type") == "Animation":
        import_animation(response.get("Data"))
        return
    if (response.get("Data") or {}).get("Type") == "Map":
        import_map(response.get("Data"), response.get("AssetsRoot") or "")
        return

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

            imported_parts.append({
                "Attachments": attachments,
                "Parent": imported_part,
                "Mesh": mesh
            })

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

    import_part(import_data.get("Parts"))

    # attachments
    for imported_part in imported_parts:
        attachments = imported_part.get("Attachments")
        parent_obj = imported_part.get("Parent")
        for attachment in attachments:
            child_name = attachment.get("AttatchmentName")
            if child_name is not None:
                child_obj = bpy.context.scene.objects[child_name]
                if child_obj.parent is not None:
                    # Attachment Name somehow points to mesh, get parent armature instead
                    child_obj = child_obj.parent
                if "revolver" in parent_obj.name.lower():
                    bone_name = "Magazine_Extra"
                if child_obj:
                    constraint_object(child_obj, parent_obj, attachment.get("BoneName"), attachment.get("Offset"), attachment.get("Rotation"))
        if new_collection not in parent_obj.users_collection:
            new_collection.objects.link(parent_obj)


def register():
    import_event = threading.Event()

    global server
    server = Receiver(import_event)
    server.start()

    def handler():
        if import_event.is_set():
            import_event.clear()
            try:
                import_response(server.data)
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
