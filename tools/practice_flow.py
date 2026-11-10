"""Prepare a guarded GRID Autosport flow graph for player-driven -benchmarkAttract sessions.

Research for launcher Practice / Race (as GRID2VR's tools/practice_entry.py does for GRID 2).
A -benchmarkAttract session loads the car, track, laps and grid from a benchmark-style XML, but
the flow treats it as attract mode: at the race start it takes the attract branches, and when a
pre-race cutscene is skipped it retires the player, hands the car to the AI and ends the race.
This redirects the three attract_mode_chosen decisions to their normal (player) branches.

Nothing is installed: it decodes the stock system/flow.bin and states.bin with the pinned EGO
converter (DiRT2VR tools/build-xml-converter.ps1), refuses any changed graph instead of guessing,
encodes the result, checks the semantic round trip and the pinned output hash, and writes
flow.bin plus manifest.json to a new folder outside the game. Staging it for a run must back
up and restore the stock file (capture/xrsim_play.py --flow).

    python -I tools/practice_flow.py --game-root "<Steam>/steamapps/common/GRID Autosport" \
        --converter "<DiRT2VR>/artifacts/xml-convert/xml-convert.exe" --output artifacts/practice-flow/<name>
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

AVX_SHA256 = 'c88929ba8ed5327799fb6f57dc7889b2f5e8baec47b92832038af913ff54fc48'
FLOW_SHA256 = 'b5f326eb2ad9ff1307c6bbf05ace2af99f21fcb9908704d66bd209882c30f20b'
STATES_SHA256 = 'a48dda4e72e9f3fc9bb2062dda6b6a062c9222906fe382b2b8eb54cb64b52ec4'
CONVERTER_SHA256 = '4448ec5a685b30beeb27cf21f6492ae4f4ad64d137c19d354b678bf52f10d029'
PRACTICE_SHA256 = 'fd8383f6b6332ec400f9035c1a89006d037ebd7e249c42f758f1fa005595ac88'
# Decision node, its state, the attract ("yes") target and the normal ("no") target.
ATTRACT_DECISION_STATES = ('3558501894', '1593529596')
CHANGES = [
    dict(node='27003617', state='3558501894', before='187956605', after='69698779'),   # race start
    dict(node='150186653', state='3558501894', before='55604350', after='69698779'),   # after first-boot track overview
    dict(node='159558068', state='1593529596', before='44935758', after='154770648'),  # cutscene skipped: AI takeover
]


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def signature(element):
    # Whitespace and attribute order carry no flow semantics.
    return (element.tag, sorted(element.attrib.items()), (element.text or '').strip(),
            tuple(signature(child) for child in element))


def unique(root, tag, identity):
    matches = [node for node in root.iter(tag) if node.get('id') == identity]
    if len(matches) != 1:
        raise ValueError(f'Expected exactly one {tag} {identity}')
    return matches[0]


def redirect(flow, states):
    if flow.tag != 'flow':
        raise ValueError('Unexpected flow root')
    for identity in ATTRACT_DECISION_STATES:
        decision = unique(states, 'StateDecisionBool', identity)
        if decision.attrib != dict(id=identity, data='attract_mode_chosen', ifTrue='yes', ifFalse='no'):
            raise ValueError('Attract decision changed')
    users = [node.get('id') for node in flow.iter('node') if node.get('state') in ATTRACT_DECISION_STATES]
    if sorted(users) != sorted(change['node'] for change in CHANGES):
        raise ValueError('The attract decisions are used by other nodes than expected')
    if unique(states, 'StateSetVehicleControl', '2553799761').attrib != dict(id='2553799761', controller='ai'):
        raise ValueError('AI takeover state changed')
    original = signature(flow)
    result = copy.deepcopy(flow)
    for change in CHANGES:
        node = unique(result, 'node', change['node'])
        if node.attrib != dict(id=change['node'], state=change['state']) or len(node) != 2:
            raise ValueError(f"Decision {change['node']} changed")
        if [child.attrib for child in node] != [dict(id='yes', target=change['before']), dict(id='no', target=change['after'])]:
            raise ValueError(f"Decision {change['node']} branches changed")
        if any(child.tag != 'link' or len(child) for child in node):
            raise ValueError('Unexpected decision children')
        unique(result, 'node', change['before']); unique(result, 'node', change['after'])
        node[0].set('target', change['after'])
    if signature(flow) != original:
        raise ValueError('Source flow was modified')
    return result


def prepare(game_root, output, converter):
    game, output, converter = Path(game_root).resolve(), Path(output).resolve(), Path(converter).resolve()
    if output.is_relative_to(game) or output.exists():
        raise ValueError('Use a new output folder outside the game')
    if digest(converter) != CONVERTER_SHA256:
        raise ValueError('Unsupported converter fingerprint')
    for name, expected in (('GRIDAutosport_avx.exe', AVX_SHA256), ('system/flow.bin', FLOW_SHA256), ('system/states.bin', STATES_SHA256)):
        if digest(game/name) != expected:
            raise ValueError(f'Unsupported installed file: {name}')
    output.mkdir(parents=True)

    def convert(mode, source, target):
        subprocess.run([str(converter), mode, str(source), str(target)], check=True,
                       creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))

    convert('decode', game/'system/flow.bin', output/'source-flow.xml')
    convert('decode', game/'system/states.bin', output/'source-states.xml')
    expected = redirect(ET.parse(output/'source-flow.xml').getroot(), ET.parse(output/'source-states.xml').getroot())
    ET.ElementTree(expected).write(output/'practice-flow.xml', encoding='utf-8', xml_declaration=True)
    convert('encode', output/'practice-flow.xml', output/'flow.bin')
    convert('decode', output/'flow.bin', output/'roundtrip-flow.xml')
    if signature(expected) != signature(ET.parse(output/'roundtrip-flow.xml').getroot()):
        raise ValueError('Converter changed flow semantics')
    if digest(output/'flow.bin') != PRACTICE_SHA256:
        raise ValueError('Practice flow differs from the expected guarded build')
    result = dict(schema=1, gameRoot=str(game), executableSha256=AVX_SHA256, originalFlowSha256=FLOW_SHA256,
                  statesSha256=STATES_SHA256, modifiedFlowSha256=PRACTICE_SHA256, converterSha256=CONVERTER_SHA256,
                  changes=CHANGES, semanticRoundtrip=True, installed=False, playerControlValidated=False)
    (output/'manifest.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--game-root', type=Path, required=True)
    parser.add_argument('--converter', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare(args.game_root, args.output, args.converter), indent=2))
