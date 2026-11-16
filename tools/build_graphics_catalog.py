"""Build launcher/data/graphics-options.json from GRID Autosport's system/hardware_settings_options.xml.

The launcher embeds the result: the quality categories the game's own Graphics menu shows, each
choice's hardware_settings_config.xml attributes, the game's five overall presets (graphicsDetail,
resolved for DirectX 11 without Intel-only extensions) and the multisampling choices. The game's
first-boot resolution, window, VSync, physics and audio settings are not part of any choice.

    python tools/build_graphics_catalog.py [GAME_FOLDER]
"""
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'launcher' / 'data' / 'graphics-options.json'
GAME = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r'E:\SteamLibrary\steamapps\common\GRID Autosport')

# Category (game name) -> (launcher field, label). Order is the launcher's.
CATEGORIES = [
    ('shadows', 'Shadows', 'Shadows'),
    ('night_lighting', 'NightLighting', 'Night lighting'),
    ('advanced_lighting', 'AdvancedLighting', 'Advanced lighting'),
    ('global_illumination', 'GlobalIllumination', 'Global illumination'),
    ('dynamic_ambient_occ', 'AmbientOcclusion', 'Ambient occlusion'),
    ('dynamic_ambient_occ_soft', 'SoftAmbientOcclusion', 'Soft ambient occlusion'),
    ('advanced_fog', 'AdvancedFog', 'Advanced fog'),
    ('particles', 'Particles', 'Particles'),
    ('crowd', 'Crowd', 'Crowd'),
    ('cloth', 'Cloth', 'Cloth'),
    ('groundCover', 'GroundCover', 'Ground cover'),
    ('trees', 'Trees', 'Trees'),
    ('objects', 'Objects', 'Objects'),
    ('vehicles', 'Vehicles', 'Vehicle detail'),
    ('track', 'Track', 'Track detail'),
    ('car_reflection', 'CarReflections', 'Car reflections'),
    ('water', 'Water', 'Water'),
    ('skidmarks', 'Skidmarks', 'Skidmarks'),
    ('textures', 'Textures', 'Texture quality'),
    ('shaders', 'Shaders', 'Shader quality'),
    ('anisotropic_filtering', 'AnisotropicFiltering', 'Anisotropic filtering'),
    ('avsm', 'Avsm', 'AVSM smoke shadows'),
    ('intel_aoit', 'IntelAoit', 'Intel AOIT'),
]
CHOICE_LABELS = {'ultralow': 'Ultra Low', 'low': 'Low', 'medium': 'Medium', 'high': 'High', 'ultra': 'Ultra',
                 'off': 'Off', 'on': 'On'}
PRESET_LABELS = {'ultralow': 'Ultra Low', 'low': 'Low', 'medium': 'Medium', 'high': 'High', 'ultra': 'Ultra'}


def attributes(element, prefix=''):
    """Flatten an option's elements to {"path@attribute": value}, relative to hardware_settings_config."""
    result = {}
    for child in element:
        path = prefix + child.tag
        for key, value in child.attrib.items():
            result[path + '@' + key] = value
        result.update(attributes(child, path + '/'))
    return result


def main():
    source = GAME / 'system' / 'hardware_settings_options.xml'
    data = source.read_bytes()
    root = ET.fromstring(data)
    by_name = {c.get('name'): c for c in root.findall('category')}
    exposed = [r.get('category') for r in by_name['exposed_configurations'].find("quality_set[@name='quality']")]
    wanted = [c[0] for c in CATEGORIES]
    missing = set(wanted) - set(exposed)
    if missing:
        raise SystemExit('Not in the game quality set: ' + ', '.join(sorted(missing)))

    categories = []
    for name, field, label in CATEGORIES:
        choices = []
        for option in by_name[name].findall('option'):
            if option.get('intelExtensionsOnly') == 'true':
                continue  # Kept Off: Intel-only extensions are not detected.
            attrs = attributes(option)
            if not attrs:
                raise SystemExit(f'{name}/{option.get("name")} has no attributes')
            choices.append({'id': option.get('name'), 'label': CHOICE_LABELS[option.get('name')], 'attributes': attrs})
        keys = {tuple(sorted(c['attributes'].items())) for c in choices}
        if len(keys) != len(choices):
            raise SystemExit(f'{name} has choices with identical attributes')
        # Trees Ultra alone sets meshes_in_all_passes; the game does not save it back, so the launcher
        # matches a choice when every attribute the config holds agrees.
        common = set.intersection(*(set(c['attributes']) for c in choices))
        if not common:
            raise SystemExit(f'{name} choices share no attribute')
        categories.append({'field': field, 'label': label, 'choices': choices})

    presets = []
    for option in by_name['graphicsDetail'].findall('option'):
        values = {}
        for ref in option.findall('ref'):
            name = ref.get('category')
            if name not in wanted:
                continue
            choice = ref.get('option')
            for case in ref.findall('case'):
                if case.get('dx11Only') == 'true' and case.get('intelExtensionsOnly') != 'true':
                    choice = case.get('option')
            values[dict((c[0], c[1]) for c in CATEGORIES)[name]] = choice
        for category in categories:
            if category['field'] not in values or not any(c['id'] == values[category['field']] for c in category['choices']):
                raise SystemExit(f'Preset {option.get("name")} has no valid {category["field"]}')
        presets.append({'id': option.get('name'), 'label': PRESET_LABELS[option.get('name')], 'values': values})

    msaa = []
    for option in by_name['multisampling'].findall('option'):
        value = attributes(option)['graphics_card/resolution@multisampling']
        msaa.append({'id': option.get('name'), 'value': value})

    catalog = {'schema': 1, 'sourceOptionsSha256': hashlib.sha256(data).hexdigest(),
               'categories': categories, 'presets': presets, 'multisampling': msaa}
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(catalog, indent=1) + '\n', encoding='utf-8', newline='\n')
    print(f'{OUTPUT}: {len(categories)} categories, {len(presets)} presets, {len(msaa)} multisampling choices')


if __name__ == '__main__':
    main()
