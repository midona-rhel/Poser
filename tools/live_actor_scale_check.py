"""Check actor scale writes and undo/redo against the running Debug bridge."""
import argparse
import json
import math
from pathlib import Path
import time
import urllib.parse
import urllib.request


def call(path, **parameters):
    url = 'http://127.0.0.1:47999/' + path + '?' + urllib.parse.urlencode(parameters)
    with urllib.request.urlopen(url, timeout=15) as response:
        result = json.load(response)
    if 'error' in result or result.get('ok') is False:
        raise AssertionError(result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--actor', default='0')
    parser.add_argument('--report', required=True)
    args = parser.parse_args()
    report = {'actor': args.actor, 'checks': []}
    initial = call('movediag', actor=args.actor)
    report['initialScale'] = initial['scale']
    original = initial['scale']

    def check(label, expected):
        time.sleep(0.25)
        state = call('movediag', actor=args.actor)
        for field in ('scale', 'nativeScale'):
            actual = state[field]
            assert actual is not None and all(
                math.isclose(a, b, rel_tol=1e-5, abs_tol=1e-9)
                for a, b in zip(actual, expected)
            ), f'{label}: {field}: {actual} != {expected}'
        report['checks'].append({'check': label, 'pass': True,
                                 'scale': state['scale'], 'nativeScale': state['nativeScale']})
        print('PASS:', label, state['nativeScale'], flush=True)

    try:
        for label, scale in [('small', [0.005]*3), ('tiny', [0.000001]*3),
                             ('negative X', [-abs(original[0]), original[1], original[2]])]:
            call('movediag', actor=args.actor, sx=scale[0], sy=scale[1], sz=scale[2])
            check(label + ' apply', scale)
            call('undo')
            check(label + ' undo', original)
            call('redo')
            check(label + ' redo', scale)
            call('undo')
            check(label + ' restored', original)
        report['pass'] = True
    except Exception as error:
        report['pass'] = False
        report['failure'] = str(error)
        raise
    finally:
        try:
            state = call('movediag', actor=args.actor)
            if state['scale'] != original:
                call('movediag', actor=args.actor, sx=original[0], sy=original[1], sz=original[2])
            report['finalScale'] = call('movediag', actor=args.actor)['nativeScale']
        finally:
            Path(args.report).write_text(json.dumps(report, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
