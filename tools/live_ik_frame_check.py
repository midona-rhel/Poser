"""Compare live world IK rotation with the renderer and model-space hand rotation."""
import json
import math
import time
from pathlib import Path
from live_actor_scale_check import call


def quaternion(value):
    return [value[k] for k in 'XYZW']


def multiply(a, b):
    x, y, z, w = a
    X, Y, Z, W = b
    return [w*X+x*W+y*Z-z*Y, w*Y-x*Z+y*W+z*X,
            w*Z+x*Y-y*X+z*W, w*W-x*X-y*Y-z*Z]


def angle(a, b):
    dot = abs(sum(x*y for x, y in zip(a, b)))
    dot /= math.sqrt(sum(x*x for x in a)*sum(x*x for x in b))
    return math.degrees(2*math.acos(min(1, dot)))


def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--actor', default='0')
    parser.add_argument('--bone', default='j_te_l')
    parser.add_argument('--report', required=True)
    args = parser.parse_args()
    actor = args.actor
    target = dict(actor=actor, name=args.bone)
    report = {'deployedHead': '4a6aca5a', 'checks': []}
    original = call('movediag', actor=actor)['scale']
    assert not call('ik', **target, action='read')['config']['Enabled'], 'Start with IK disabled.'
    call('pauseall', actor=actor)
    call('movediag', actor=actor, yaw=45)
    try:
        for solver in ('Fabrik', 'Rope'):
            for scale in ([1, 1, 1], [0.005]*3, [-1, 1.5, 0.75]):
                call('ik', **target, enabled=0)
                call('movediag', actor=actor, sx=scale[0], sy=scale[1], sz=scale[2])
                time.sleep(.25)
                call('ik', **target, solver=solver, enabled=1, targetMode='Actor',
                     parentDepth=2, childDepth=0, collisions=0, endRotation=1)
                time.sleep(.25)
                call('ik', **target, targetMode='World')
                time.sleep(.5)
                frame = call('movediag', actor=actor)['renderedRotation']
                bone = quaternion(call('bone', **target)['rotation'])
                control = call('ik', **target, action='read')['config']['Fabrik']
                assert control['Handle']['Mode'] == 1, 'Expected world target'
                error = angle(multiply(frame, bone), quaternion(control['Handle']['Rotation']))
                row = dict(solver=solver, scale=scale, angularErrorDegrees=error, passed=error < .1)
                report['checks'].append(row)
                print(('PASS' if row['passed'] else 'FAIL'), row, flush=True)
        report['passed'] = all(row['passed'] for row in report['checks'])
    finally:
        call('ik', **target, enabled=0)
        call('movediag', actor=actor, sx=original[0], sy=original[1], sz=original[2], yaw=-45)
        Path(args.report).write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    assert report['passed'], 'See live report for failed cases'


if __name__ == '__main__':
    main()
