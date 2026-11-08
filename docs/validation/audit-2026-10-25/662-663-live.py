import json,time,urllib.request,urllib.parse,pathlib,traceback
base='http://127.0.0.1:47999/'
e={'candidate':'e8f12dc4 plus Debug-only native-pan probe','checks':{},'samples':{}}
def c(r,**q):
 with urllib.request.urlopen(base+r+'?'+urllib.parse.urlencode(q),timeout=15) as f:return json.load(f)
def require(ok,msg):
 if not ok:raise AssertionError(msg)
def command(r,**q):
 v=c(r,**q);require(v.get('ok',v.get('Success',False)),(r,v));return v
def cam():return next(v for v in c('cameras')['cameras'] if v['IsDefault'])
def close(a,b):return max(abs(a[k]-b[k]) for k in ('X','Y'))<0.0001
def remove():
 v=c('selectionremove',start=1)
 for _ in range(50):
  if not v.get('running'):break
  time.sleep(.05);v=c('selectionremove')
 require('error' not in v and not v.get('running'),v);return v
try:
 prior_lights=c('testlight');prior_ids={v['id'] for v in prior_lights}
 for _ in range(2):require(c('testlight',spawn=1)['ok'],'spawn')
 lights=[v for v in c('testlight') if v['id'] not in prior_ids];ids=[v['id'] for v in lights];names=sorted(v['Name'] for v in lights)
 selected=c('selection',entities=','.join('light:'+i for i in ids));require(isinstance(selected,list) and len(selected)==2,selected)
 time.sleep(.3);before=cam();pan=before['Pan'];desired={'X':pan['X']+.15,'Y':pan['Y']+.08}
 command('camera',camera=before['id'],action='probe-native-pan',x=desired['X'],y=desired['Y']);time.sleep(.25)
 after=cam();require(close(after['Pan'],desired),'native pan did not change')
 e['samples']['pending_history']=c('history');e['samples']['removal']=remove();require(len(c('testlight'))==len(prior_lights),'test lights remain')
 e['samples']['removed_history']=c('history');command('undo');time.sleep(.25)
 require(sorted(v['Name'] for v in c('testlight') if v['id'] not in prior_ids)==names,'undo failed to restore lights')
 require(close(cam()['Pan'],desired),'group undo also moved camera')
 require(c('history')['undo']=='Move camera',c('history'))
 command('undo');time.sleep(.25);require(close(cam()['Pan'],pan),'second undo did not restore native pan')
 command('redo');time.sleep(.25);require(close(cam()['Pan'],desired),'redo camera failed')
 command('redo');time.sleep(.25);require(len(c('testlight'))==len(prior_lights),'redo removal failed')
 e['checks']['camera_move_separate_from_group_delete_undo_redo']='PASS';e['samples']['camera']={'before':pan,'moved':desired,'final':cam()['Pan']}
 command('camera',camera=cam()['id'],action='pan',x=pan['X'],y=pan['Y'])
 actor=c('actors')['actors'][0]['index'];command('reset',actor=actor,slot=0)
 command('pauseall',actor=actor);time.sleep(.25)
 a=c('state',actor=actor);time.sleep(.3);b=c('state',actor=actor)
 require(a['paused'] and b['paused'], 'pauseall state')
 require(abs(a['controls'][0]['time']-b['controls'][0]['time'])<.01,'native animation did not hold')
 command('resumeall',actor=actor);time.sleep(.25);a=c('state',actor=actor);time.sleep(.3);b=c('state',actor=actor)
 require(not b['paused'],'play still paused');require(abs(a['controls'][0]['time']-b['controls'][0]['time'])>.05,'native animation did not advance')
 command('undo');time.sleep(.25);a=c('state',actor=actor);time.sleep(.3);b=c('state',actor=actor)
 require(a['paused'] and b['paused'],'undo did not pause')
 require(abs(a['controls'][0]['time']-b['controls'][0]['time'])<.01,'undo pause did not hold')
 held=[a['controls'][0]['time'],b['controls'][0]['time']]
 command('redo');time.sleep(.25);a=c('state',actor=actor);time.sleep(.3);b=c('state',actor=actor)
 require(not b['paused'] and abs(a['controls'][0]['time']-b['controls'][0]['time'])>.05,'redo play did not advance')
 e['checks']['native_actor_pause_play_undo_redo']='PASS';e['samples']['animation']={'undo_held_frames':held,'redo_frames':[a['controls'][0]['time'],b['controls'][0]['time']]}
 e['limitations']=['Native camera pan was changed through the Debug probe; Windows could not activate the game for a physical drag.', 'Restart refusal and partial-failure undo are covered by automated tests, not induced in the game.', 'The initial attempt to check exact held-frame restoration used the already-running game idle, which Poser had not applied. That attempt did not establish layer frame restoration.']

except Exception as ex:
 e['failure']=str(ex);traceback.print_exc()
finally:
 pathlib.Path('docs/validation/audit-2026-10-25/662-663-live.json').write_text(json.dumps(e,indent=2)+'\n');print(json.dumps(e,indent=2))

raise SystemExit(1 if "failure" in e else 0)
