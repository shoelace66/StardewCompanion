"""Seed isolated test authorization metadata, never dispatch game skills."""
import argparse
import json
import pathlib
import re
import sqlite3
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--run-root', required=True)
parser.add_argument('--request-file', required=True)
args = parser.parse_args()
root = pathlib.Path(args.run_root).resolve()
request = json.loads(pathlib.Path(args.request_file).read_text(encoding='utf-8-sig'))
session = json.loads((root / 'session.json').read_text(encoding='utf-8-sig'))
base = session['endpoint'].replace('ws:', 'http:').removesuffix('/bridge')

def get(path):
    req = urllib.request.Request(base + path, headers={'Authorization': 'Bearer ' + session['token']})
    with urllib.request.urlopen(req, timeout=5) as response:
        return json.load(response)

state = get('/state')
agent = get('/agent/state')
assert re.fullmatch(r'SC\d+D_\d{8}_\d{6}', root.name), 'Not an isolated test run'
assert state['WorldReady'] and state['ControlMode'] == 'solo_test'
assert state['TestRunId'] == root.name and state['PlayerName'] == 'SC5Host'
assert not state['Multiplayer'] and not agent['running'] and not state['ActiveTaskId']
scope = f"{state['SaveId']}:{state['PlayerId']}"
world = json.loads(state['WorldView'])
records = []
for chest in request.get('chests', []):
    assert chest['Location'] == state['Location'], 'Fixture chest must be currently observable'
    assert any(c.get('id') == chest['Id'] and c['x'] == chest['X'] and c['y'] == chest['Y']
               for c in world['chests']), 'Native chest identity or position mismatch'
    assert isinstance(chest['AllowedIds'], list) and isinstance(chest['ProtectedIds'], list)
    records.append(('place', chest))
for project in request.get('projects', []):
    assert project['Status'] == 'active' and project['VerifiedResults'] == []
    records.append(('project', project))
assert records, 'No explicit metadata preconditions supplied'
stamp = str(time.time_ns())
backup_path = root / ('memory-precondition-' + stamp + '.db')
with sqlite3.connect(root / 'memory.db', timeout=10) as database:
    with sqlite3.connect(backup_path) as backup:
        database.backup(backup)
    for kind, value in records:
        existing = [(row[0], json.loads(row[1])) for row in database.execute(
            'SELECT id,text FROM memory WHERE scope=? AND kind=?', (scope, kind))]
        assert not any(v.get('Id') == value['Id'] for _, v in existing), 'Duplicate fixture identity'
        database.execute('INSERT INTO memory(scope,kind,text,state,source,day) VALUES(?,?,?,?,?,?)',
                         (scope, kind, json.dumps(value, ensure_ascii=False), 'active',
                          'isolated_test_fixture', state['GameDay']))
    database.commit()
evidence = {'fixtureOnly': True, 'runId': root.name, 'scope': scope,
            'records': records, 'backup': str(backup_path), 'noGameDispatch': True}
(root / ('memory-precondition-' + stamp + '.json')).write_text(
    json.dumps(evidence, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'fixtureOnly': True, 'recordsPrepared': len(records), 'runId': root.name}))
