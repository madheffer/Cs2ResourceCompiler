"""How much of a Hammer .vmap survives a decompile: the source map against one rebuilt
from its compiled package (VRF's MapExtract today, our own decompiler later).

    python vmap_compare.py <source.vmap> <decompiled.vmap> [--show N]

Entities are paired by nodeID, which the compiler keeps as the last part of
hammeruniqueid. Meshes are compared as totals (faces, vertices, materials), since a
decompile rebuilds them from render triangles and nothing pairs them one to one.
"""
import collections
import sys

import dmx


def walk(root):
    """Every map node under root, depth first, with its parent chain of node types."""
    stack = [(root, ())]
    while stack:
        node, chain = stack.pop()
        yield node, chain
        for child in reversed(node.attrs.get('children') or []):
            if child is not None:
                stack.append((child, chain + (node.type,)))


def props(node):
    p = node.attrs.get('entity_properties')
    return dict(p.attrs) if p is not None else {}


def mesh_stats(mesh):
    md = mesh.attrs.get('meshData')
    if md is None:
        return 0, 0, set()
    faces = len(md.attrs.get('faceEdgeIndices') or [])
    verts = len(md.attrs.get('vertexEdgeIndices') or [])
    return faces, verts, set(md.attrs.get('materials') or [])


def connections(node):
    out = []
    for c in node.attrs.get('connectionsData') or []:
        a = c.attrs
        out.append((a.get('outputName'), a.get('targetName'), a.get('inputName'),
                    a.get('overrideParam'), round(a.get('delay') or 0, 4), a.get('timesToFire')))
    return sorted(out, key=repr)


def summarise(path):
    doc = dmx.read_file(path)
    root = next(e for e in doc['elements'] if e.type == 'CMapRootElement')
    world = root.attrs['world']
    s = {
        'header': doc['header'].strip(),
        'root': {k: v for k, v in root.attrs.items() if isinstance(v, (bool, int, float, str))},
        'types': collections.Counter(),
        'entities': {},
        'unpaired': [],
        'classes': collections.Counter(),
        'meshes': [0, 0, 0, set()],
        'mesh_parents': collections.Counter(),
        'world_props': props(world),
        'refs': (doc['prefix'][0].attrs.get('map_asset_references') or []) if doc['prefix'] else [],
        'thumbnail': bool(doc['prefix'] and doc['prefix'][0].attrs.get('asset_preview_thumbnail')),
        'node_instance_data': len(root.attrs.get('nodeInstanceData') or []),
        'snapshots': len(root.attrs.get('m_ReferencedMeshSnapshots') or []),
        'variables': len((root.attrs.get('mapVariables').attrs.get('variableNames') or [])) if root.attrs.get('mapVariables') else 0,
        'cameras': len(root.attrs['3dcameras'].attrs.get('cameras') or []) if root.attrs.get('3dcameras') else 0,
    }
    for node, chain in walk(world):
        s['types'][node.type] += 1
        if node.type == 'CMapMesh':
            f, v, m = mesh_stats(node)
            s['meshes'][0] += 1; s['meshes'][1] += f; s['meshes'][2] += v; s['meshes'][3] |= m
            s['mesh_parents'][chain[-1] if chain else '-'] += 1
        elif 'entity_properties' in node.attrs and node is not world:
            p = props(node)
            cls = p.get('classname', node.type)
            s['classes'][cls] += 1
            rec = {'type': node.type, 'class': cls, 'props': p, 'io': connections(node),
                   'origin': node.attrs.get('origin'), 'angles': node.attrs.get('angles'),
                   'scales': node.attrs.get('scales'), 'children': sum(1 for c in node.attrs.get('children') or [] if c is not None and c.type == 'CMapMesh'),
                   'name': node.attrs.get('name')}
            nid = node.attrs.get('nodeID')
            if nid in s['entities'] or nid is None:
                s['unpaired'].append(rec)
            else:
                s['entities'][nid] = rec
    return s


def same_value(x, y):
    """Equal as Hammer would read them: numbers by value, vectors part by part."""
    x, y = str(x).strip(), str(y).strip()
    if x == y:
        return True
    xs, ys = x.split(), y.split()
    if len(xs) != len(ys):
        return False
    try:
        return all(abs(float(a) - float(b)) <= 1e-4 * max(1.0, abs(float(a))) for a, b in zip(xs, ys))
    except ValueError:
        return False


def close(a, b, eps=0.01):
    if a is None or b is None:
        return a == b
    return all(abs(x - y) <= eps for x, y in zip(a, b))


def main():
    args = sys.argv[1:]
    show = 12
    if '--show' in args:
        i = args.index('--show'); show = int(args[i + 1]); del args[i:i + 2]
    src, dec = summarise(args[0]), summarise(args[1])

    print('header      ', src['header'], '|', dec['header'])
    print('root scalars', {k: (v, dec['root'].get(k)) for k, v in src['root'].items() if dec['root'].get(k) != v})
    print('thumbnail   ', src['thumbnail'], '|', dec['thumbnail'])
    print('asset refs  ', len(src['refs']), '|', len(dec['refs']),
          ' missing:', len(set(src['refs']) - set(dec['refs'])), ' extra:', len(set(dec['refs']) - set(src['refs'])))
    print('nodeInstanceData', src['node_instance_data'], '| snapshots', src['snapshots'], '| variables', src['variables'], '| cameras', src['cameras'],
          '  ->  ', dec['node_instance_data'], dec['snapshots'], dec['variables'], dec['cameras'])
    print()
    print('node types (source | decompiled)')
    for t in sorted(set(src['types']) | set(dec['types'])):
        a, b = src['types'][t], dec['types'][t]
        print(f'  {t:28} {a:6} {b:6}' + ('' if a == b else '   <-'))
    print()
    sm, dm = src['meshes'], dec['meshes']
    print(f'meshes      {sm[0]} meshes, {sm[1]} faces, {sm[2]} verts, {len(sm[3])} materials | {dm[0]}, {dm[1]}, {dm[2]}, {len(dm[3])}')
    print('  mesh parents', dict(src['mesh_parents']), '|', dict(dec['mesh_parents']))
    print('  materials lost  ', sorted(sm[3] - dm[3])[:show])
    print('  materials gained', sorted(dm[3] - sm[3])[:show])
    print()

    wp_s, wp_d = src['world_props'], dec['world_props']
    print('worldspawn keys lost', sorted(set(wp_s) - set(wp_d)))
    print('worldspawn changed  ', {k: (v, wp_d[k]) for k, v in wp_s.items() if k in wp_d and wp_d[k] != v})
    print()

    S, D = src['entities'], dec['entities']
    missing = [S[k] for k in S if k not in D]
    extra = [D[k] for k in D if k not in S]
    paired = [(S[k], D[k]) for k in S if k in D]
    print(f'entities    source {len(S)} (+{len(src["unpaired"])} unpaired), decompiled {len(D)} (+{len(dec["unpaired"])} unpaired); paired {len(paired)}')
    print('  missing by class', collections.Counter(e['class'] for e in missing).most_common(show))
    print('  extra by class  ', collections.Counter(e['class'] for e in extra).most_common(show))

    stats = collections.Counter()
    key_lost, key_added, key_changed = collections.Counter(), collections.Counter(), collections.Counter()
    changed_examples, lost_examples, added_examples = {}, {}, {}
    class_changed = collections.Counter()
    for a, b in paired:
        if a['class'] != b['class']:
            class_changed[(a['class'], b['class'])] += 1
        if a['type'] != b['type']:
            stats['node type changed'] += 1
        if not close(a['origin'], b['origin']): stats['origin differs'] += 1
        if not close(a['angles'], b['angles'], 0.05): stats['angles differ'] += 1
        if not close(a['scales'], b['scales']): stats['scales differ'] += 1
        if a['io'] != b['io']: stats['outputs differ'] += 1
        if a['io']: stats['has outputs'] += 1
        if a['children'] != b['children']: stats['mesh children differ'] += 1
        # Keys compare case-blind (the lump lowercases them) and an empty value equals
        # a missing key; what is left is a real loss, a real change or a compiler key.
        pa = {k.lower(): v for k, v in a['props'].items() if str(v) != ''}
        pb = {k.lower(): v for k, v in b['props'].items() if str(v) != ''}
        same = True
        for k in pa:
            if k not in pb:
                key_lost[k] += 1; same = False
                lost_examples.setdefault(k, (a['class'], pa[k]))
            elif not same_value(pa[k], pb[k]):
                key_changed[k] += 1; same = False
                changed_examples.setdefault(k, (a['class'], pa[k], pb[k]))
        for k in pb:
            if k not in pa:
                key_added[k] += 1; same = False
                added_examples.setdefault(k, (b['class'], pb[k]))
        stats['props identical' if same else 'props differ'] += 1
    print('  paired stats', dict(stats))
    print('  class changed', dict(class_changed))
    print('  keys lost   ', key_lost.most_common(show))
    print('  keys added  ', key_added.most_common(show))
    print('  keys changed', key_changed.most_common(show))
    for k, (cls, x, y) in list(changed_examples.items())[:show]:
        print(f'      changed {k:28} {cls:28} {str(x)[:50]!r} -> {str(y)[:50]!r}')
    for k, (cls, x) in list(lost_examples.items())[:show]:
        print(f'      lost    {k:28} {cls:28} {str(x)[:50]!r}')
    for k, (cls, x) in list(added_examples.items())[:show]:
        print(f'      added   {k:28} {cls:28} {str(x)[:50]!r}')
    io_examples = [(a['class'], a['io'], b['io']) for a, b in paired if a['io'] != b['io']]
    for cls, x, y in io_examples[:3]:
        print(f'      io      {cls}: {x[:2]} -> {y[:2]}')


if __name__ == '__main__':
    main()
