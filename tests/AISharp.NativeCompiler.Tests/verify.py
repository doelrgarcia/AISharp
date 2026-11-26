"""Native compiler/CLI bootstrap proof. Never invokes dotnet, MSBuild or C#.

Builds with installed MSVC, CMake and Ninja; runs actual compiler/compiled PE files.
All generated output stays beneath the caller-supplied --work-root directory.
"""
from pathlib import Path
import argparse, hashlib, json, os, shutil, struct, subprocess, time, uuid

parser=argparse.ArgumentParser()
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('--work-root',type=Path,required=True,help='Dedicated disposable directory owned by this verification run')
parser.add_argument('--fixtures',type=Path,help='Run a named native fixture corpus with the same real compiler/PE/runtime checks')
parser.add_argument('--debug-host',action='store_true')
args=parser.parse_args()
repo=Path(__file__).resolve().parents[2]
root=args.work_root.resolve()
assert root.resolve()==root and not root.is_symlink() and not root.is_junction()
root.mkdir(parents=True,exist_ok=True);args.output.mkdir(parents=True,exist_ok=True)
work=root/'work';work.mkdir(exist_ok=True)
def unlink_owned(path):
    assert path.parent.resolve()==work and not path.is_symlink() and not path.is_junction()
    for attempt in range(6):
        try:path.unlink();return
        except FileNotFoundError:return
        except PermissionError as error:
            if getattr(error,'winerror',None) not in {5,32,33} or attempt==5:raise
            print('Retry owned artifact cleanup',path.name,attempt+1,flush=True);time.sleep(0.05*(2**attempt))
ninja=Path(r'C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe')
assert ninja.is_file()
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def run(command,**kw):
    return subprocess.run([str(x) for x in command],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=240,**kw)
def pe(path):
    data=path.read_bytes();assert data[:2]==b'MZ'
    at=struct.unpack_from('<I',data,0x3c)[0];assert data[at:at+4]==b'PE\0\0'
    machine=struct.unpack_from('<H',data,at+4)[0];optional=at+24;magic=struct.unpack_from('<H',data,optional)[0]
    directories=optional+({0x10b:96,0x20b:112}[magic]);assert struct.unpack_from('<II',data,directories+14*8)==(0,0),'CLR header in native executable'
    sections=[];count=struct.unpack_from('<H',data,at+6)[0];section_at=optional+struct.unpack_from('<H',data,at+20)[0]
    for i in range(count):
        off=section_at+i*40;vs,rva,size,raw=struct.unpack_from('<IIII',data,off+8);sections.append((rva,max(vs,size),raw))
    def offset(rva):
        for start,size,raw in sections:
            if start<=rva<start+size:return raw+rva-start
        raise AssertionError('Invalid import RVA')
    imports=[];rva,size=struct.unpack_from('<II',data,directories+8)
    if rva:
        off=offset(rva)
        while data[off:off+20]!=bytes(20):
            name=offset(struct.unpack_from('<I',data,off+12)[0]);imports.append(data[name:data.index(0,name)].decode('ascii'));off+=20
    assert not any(any(s in x.lower() for s in ('mscoree','clr','hostfxr','hostpolicy','coreclr','msvcp','vcruntime','ucrtbase')) for x in imports),imports
    return dict(machine=hex(machine),clrDirectoryAbsent=True,imports=imports,staticCppRuntime=True)

environments={};hosts={};builds=[]
for bits in (32,64):
    script=root/f'toolchain-x{bits}.cmd'
    script.write_text('@call "C:\\Program Files\\Microsoft Visual Studio\\2022\\Professional\\VC\\Auxiliary\\Build\\vcvars'+str(bits)+'.bat" >nul\n@if errorlevel 1 exit /b %errorlevel%\n@set\n')
    p=run(['cmd.exe','/d','/c',script]);assert p.returncode==0
    env=os.environ.copy()
    for line in p.stdout.splitlines():
        key,sep,value=line.partition('=')
        if sep and key.upper() in {'PATH','INCLUDE','LIB','LIBPATH','VSCMD_ARG_TGT_ARCH','SYSTEMROOT'}:env[key.upper()]=value
    assert env['VSCMD_ARG_TGT_ARCH']==('x86' if bits==32 else 'x64')
    # Some developer shells omit the installed Windows SDK binary directory.
    # Discover an existing matching tool directory without changing user settings.
    if not shutil.which('rc.exe',path=env['PATH']) or not shutil.which('mt.exe',path=env['PATH']):
        sdk_base=Path(os.environ.get('ProgramFiles(x86)',r'C:\Program Files (x86)'))/'Windows Kits/10/bin'
        sdk_candidates=sorted(sdk_base.glob('10.*'),key=lambda p:tuple(int(x) for x in p.name.split('.')),reverse=True)
        sdk_bin=next((p/('x86' if bits==32 else 'x64') for p in sdk_candidates if (p/('x86' if bits==32 else 'x64')/'rc.exe').is_file() and (p/('x86' if bits==32 else 'x64')/'mt.exe').is_file()),None)
        assert sdk_bin is not None,'Install Windows SDK resource and manifest tools'
        env['PATH']=str(sdk_bin)+';'+env['PATH']

    # Complete SDK include/library discovery when vcvars reports only VC paths.
    kits=Path(os.environ.get('ProgramFiles(x86)',r'C:\Program Files (x86)'))/'Windows Kits/10'
    arch='x86' if bits==32 else 'x64'
    versions=sorted((kits/'Lib').glob('10.*'),key=lambda p:tuple(int(x) for x in p.name.split('.')),reverse=True)
    sdk=next((p.name for p in versions if (p/'um'/arch/'kernel32.lib').is_file() and (p/'ucrt'/arch/'ucrt.lib').is_file() and (kits/'Include'/p.name/'um/Windows.h').is_file()),None)
    assert sdk is not None,'Install Windows SDK headers and libraries'
    for variable,paths in [('INCLUDE',[kits/'Include'/sdk/x for x in ('ucrt','shared','um','winrt','cppwinrt')]),('LIB',[kits/'Lib'/sdk/x/arch for x in ('ucrt','um')])]:
        existing=env.get(variable,'')
        env[variable]=existing+';'+ ';'.join(str(p) for p in paths if p.is_dir() and str(p).lower() not in existing.lower())
    env.pop('AISHARP_MEMORY_EVIDENCE',None);env.pop('AISHARP_MEMORY_FAIL_AT',None)
    env['PATH']=';'.join(x for x in env['PATH'].split(';') if 'dotnet' not in x.lower() and 'microsoft.net' not in x.lower())
    env['DOTNET_ROOT']=str(root/'dotnet-deliberately-absent');env['DOTNET_MULTILEVEL_LOOKUP']='0'
    env['TEMP']=env['TMP']=str(work);environments[bits]=env
    build=root/(f'x{bits}-debug' if args.debug_host else f'x{bits}')
    for i,cmd in enumerate([['cmake','--fresh','-S',repo/'src/AISharp.NativeCompiler','-B',build,'-G','Ninja','-DCMAKE_BUILD_TYPE='+('Debug' if args.debug_host else 'Release'),'-DCMAKE_MAKE_PROGRAM='+str(ninja),'-DCMAKE_RC_COMPILER='+Path(shutil.which('rc.exe',path=env['PATH'])).as_posix(),'-DCMAKE_MT='+Path(shutil.which('mt.exe',path=env['PATH'])).as_posix()],['cmake','--build',build]]):
        p=run(cmd,env=env);log=args.output/f'build-{bits}-{i}.log';log.write_text(p.stdout+p.stderr);assert p.returncode==0,p.stdout+p.stderr
    host=build/'aisharp-native.exe';host_pe=pe(host);assert host_pe['machine']==('0x14c' if bits==32 else '0x8664')
    version=run([host,'--version'],env=env);assert version.returncode==0 and not version.stderr
    version=json.loads(version.stdout);assert version['requiresDotnet'] is False and version['host']=='native-cpp'
    assert version['ownershipTarget']=='rust-style' and version['memoryModel']=='owned-buffers-function-regions'
    assert version['borrowCheckerImplemented'] is True and version['borrowCheckingScope']=='monomorphic-function-regions' and version['interproceduralReferences'] is True and version['languageHeapValues'] is True and version['tracingGcRequired'] is False
    hosts[bits]=host;builds.append(dict(bits=bits,debug=args.debug_host,host=str(host),sha256=sha(host),pe=host_pe,version=version,environmentHasNoDotnetPath=True,dotnetRootAbsent=not Path(env['DOTNET_ROOT']).exists()))

cases=[]
def case(name,body,value=None,error=None,pre='',type='int'):
    cases.append(dict(name=name,body=body,value=value,error=error,pre=pre,type=type))
case('zero','return 0',0);case('minimum','return -2147483648',-2147483648);case('maximum','return 2147483647',2147483647)
case('boolean','return true',True,type='bool');case('false','return false',False,type='bool')
case('precedence','return 2+3*4-8/2',10);case('parentheses','return (2+3)*(4-1)',15)
case('hex-binary','return 0x7fff_ffff ^ 0b1010',2147483637)
case('unary','let x=7\nreturn ~(-x)',6)
case('assignment-order','var x=1\nreturn x+(x=2)',3)
case('compound-snapshot','var x=1\nx+=(x=2)\nreturn x',3)
case('arguments-order','var x=1\nreturn pair(x,x=2)',12,pre='fn pair(a: int,b: int) -> int\nensures true\n{return a*10+b}\n')
case('pre-post','var x=1\nlet a=x++\nlet b=++x\nreturn a*100+b*10+x',133)
case('bit-compounds','var x=7\nx&=3\nx|=8\nx^=2\nx<<=1\nx>>=1\nx>>>=0\nreturn x',9)
case('shifts','let x= -2147483648\nif((x>>>1)==1073741824 && (x>>1)== -1073741824 && (1<< -1)== -2147483648){return 1}\nreturn 0',1)
case('short-circuit','var n=0\nlet a=false && ((n=1)==1)\nlet b=true || ((n=2)==2)\nreturn n',0)
case('boolean-bits','let a=true\nlet b=false\nreturn (a ^ b) & (a | b)',True,type='bool')
case('if-flow','var x: int\nlet b=true\nif(b){x=7}else{x=9}\nreturn x',7)
case('if-constant-return','if(true){return 8}',8)
case('while','var i=0\nvar n=0\nwhile(i<5){i++\nn+=i}\nreturn n',15)
case('for-continue','var n=0\nfor(var i=0;i<6;i++){if(i==3){continue}\nn+=i}\nreturn n',12)
case('loop-break','var i=0\nwhile(true){i++\nif(i==4){break}}\nreturn i',4)
case('nested-targets','var n=0\nfor(var i=0;i<3;i++){for(var j=0;j<3;j++){if(j==1){continue}\nif(i==2){break}\nn++}}\nreturn n',4)
case('forward-call','return doubleIt(21)',42,pre='fn doubleIt(n: i32) -> int\nensures true\n{return n*2}\n')
case('recursion','return fact(5)',120,pre='fn fact(n: int) -> int\nensures true\n{if(n<=1){return 1}\nreturn n*fact(n-1)}\n')
case('unit-call','notice()\nreturn 9',9,pre='fn notice() -> unit\nensures true\n{return}\n')
case('escaped-binding','let @int=6\nreturn @int',6)
case('escaped-canonical-binding','let @value=7\nreturn value',7)
case('comments-unicode','// Ã°Å¸Â§Âª .NET class unsafe are comment text\n/* bool fn */\nreturn 4',4)
for name,body,error in [('add-overflow','var x=2147483647\nreturn x+1','System.OverflowException'),('subtract-overflow','var x= -2147483648\nreturn x-1','System.OverflowException'),('multiply-overflow','var x=65536\nreturn x*x','System.OverflowException'),('negate-overflow','var x= -2147483648\nreturn -x','System.OverflowException'),('divide-overflow','var x= -2147483648\nreturn x/ -1','System.OverflowException'),('divide-zero','var z=0\nreturn 1/z','System.DivideByZeroException'),('remainder-zero','var z=0\nreturn 1%z','System.DivideByZeroException'),('update-overflow','var x=2147483647\nx++\nreturn x','System.OverflowException')]:case(name,body,error=error)
case('remainder-minimum','var x= -2147483648\nreturn x% -1',error='System.OverflowException')
case('remainder-minimum-unchecked','var x= -2147483648\nreturn unchecked(x% -1)',error='System.OverflowException')
case('remainder-minimum-long','var x= -9223372036854775808L\nreturn (int)(x% -1L)',error='System.OverflowException')
case('remainder-minimum-long-unchecked','var x= -9223372036854775808L\nreturn (int)unchecked(x% -1L)',error='System.OverflowException')
case('remainder-minimum-constant','const x= -2147483648\nreturn x% -1',0)
case('remainder-minimum-constant-long','const x= -9223372036854775808L\nreturn (int)(x% -1L)',0)
case('recursion-limit','return forever()',error='AISharp.RecursionLimitException',pre='fn forever() -> int\nensures true\n{return forever()}\n')
negative=[('unassigned','var x: int\nreturn @@x','AI3102'),('unknown','return @@missing','AI3103'),('immutable','let x=1\n@@x=2\nreturn x','AI3105'),('duplicate','let x=1\nlet @@x=2\nreturn x','AI3106'),('numeric-condition','if(@@1){return 1}\nreturn 0','AI3101'),('return-type','@@return true','AI3020'),('local-type','let b: bool=@@1\nreturn 0','AI3107'),('literal-high','return @@2147483648','AI3107'),('literal-low','return -@@2147483649','AI3107'),('break-target','@@break\nreturn 0','AI3104'),('continue-target','@@continue\nreturn 0','AI3104'),('scope','{let x=1}\nreturn @@x','AI3103'),('branch-assignment','var x: int\nlet b=true\nif(b){x=1}\nreturn @@x','AI3102'),('loop-assignment','var x: int\nwhile(false){x=1}\nreturn @@x','AI3102'),('short-circuit-assignment','var x: int\nlet b=false\nlet z=b && ((x=1)==1)\nreturn @@x','AI3102'),('continue-assignment','var x: int\nlet b=true\nfor(var i=0;i<2;i+=@@x){if(b){continue}\nx=1}\nreturn 0','AI3102'),('unsupported-type','let x: @@f32=1\nreturn 0','AI3100'),('unsupported-volatile-local','@@volatile let x=1\nreturn 0','AI3107'),('unsupported-passthrough','System@@.Console.WriteLine(1)\nreturn 0','AI3100'),('wrong-bool-operator','return true@@+1','AI3107'),('nonmutable-argument','@@n=2\nreturn n','AI3105')]
rows=[];diagnostics=[];owned=[]
negative.extend([('local-shadows-function','let main=1\nreturn @@main()','AI3107'),('escaped-duplicate','let @x=1\nlet @@x=2\nreturn x','AI3106')])
if args.fixtures:
    selected=json.loads(args.fixtures.read_text(encoding='utf-8'))
    cases=[{'pre':'','type':'int','value':None,'error':None,**c} for c in selected['cases']]
    negative=selected['negative']
    assert len({c['name'] for c in cases})==len(cases)
def source(body,pre='',type='int'):
    return 'module NativeHostProof\nintent "Native compiler, CLI and program; no CLR"\n'+pre+'fn main() -> '+type+'\nensures true\n{\n'+body+'\n}\n'
def bits_pick(record, key, bits):
    if not isinstance(record,dict):
        return None
    return record.get(key+str(bits), record.get(key))

try:
    for bits in (32,64):
        host=hosts[bits];env=environments[bits];cc=shutil.which('cl.exe',path=env['PATH']);assert cc
        for c in cases:
            path=work/(c['name']+'.aisharp');path.write_text(source(c['body'],c['pre'],c['type']),encoding='utf-8',newline='\n');owned.append(path)
            for optimized in (False,True):
                exe=work/(c['name']+'-'+uuid.uuid4().hex+'.exe');assert not exe.exists();owned.append(exe)
                command=[host,'build-native',path,'--out',exe,'--bits',bits,'--cc',cc,'--json']+(['--optimize'] if optimized else [])
                built=run(command,env=env)
                if built.returncode:
                    payload=json.loads(built.stdout);d=payload['diagnostics'][0];code=bits_pick(c,'code',bits)
                    assert code is not None and d['code']==code,(c['name'],d)
                    rows.append(dict(name=c['name'],bits=bits,optimized=optimized,build=payload,result=dict(outcome='diagnostic',code=d['code']),exit=2))
                    (args.output/'runtime.json').write_text(json.dumps(dict(rows=rows,failures=[]),indent=2)+'\n')
                    print('native',bits,c['name'],flush=True)
                    continue
                receipt=json.loads(built.stdout);assert receipt['success'] and receipt['sourceSha256']==sha(path) and receipt['artifactSha256']==sha(exe)
                assert receipt['pointerBits']==bits and receipt['optimized']==optimized
                inspected=pe(exe);assert inspected['machine']==('0x14c' if bits==32 else '0x8664')
                actual=run([exe],env=env);result=json.loads(actual.stdout);assert not actual.stderr;assert result.pop('schemaVersion')=='aisharp.native-result/1'
                expected_error=bits_pick(c,'error',bits);expected_value=bits_pick(c,'value',bits)
                expected=dict(outcome='threw',exception=expected_error) if expected_error else dict(outcome='returned',value=expected_value)
                if c['type']=='bool' and not c['error']:expected['type']='bool';assert type(result['value']) is bool
                assert result==expected,(c['name'],result,expected);assert actual.returncode==(70 if c['error'] else 0)
                row=dict(name=c['name'],bits=bits,optimized=optimized,build=receipt,pe=inspected,result=result,exit=actual.returncode)
                if c['name'] in {'minimum','maximum','boolean'}:
                    before=sha(path)
                    with path.open('rb') as read_only:
                        p=subprocess.run([str(exe)],stdout=read_only,stderr=subprocess.PIPE,text=True,env=env,timeout=30)
                    assert p.returncode==74 and not p.stderr and sha(path)==before;row['outputFailure']=74
                rows.append(row);unlink_owned(exe)
                (args.output/'runtime.json').write_text(json.dumps(dict(rows=rows,failures=[]),indent=2)+'\n')
            print('native',bits,c['name'],flush=True)
        for item in negative:
            if isinstance(item,dict):name,body,code=item['name'],item['body'],bits_pick(item,'code',bits);text=source(body,item.get('pre',''),item.get('type','int'))
            else:name,body,code=item;text=source(body)
            if name=='nonmutable-argument':
                text='module Probe\nintent "Parameter"\nfn helper(n: int) -> int\nensures true\n{\n'+body+'\n}\nfn main() -> int\nensures true\n{return helper(1)}\n'
            if name in {'unknown','literal-high'} or isinstance(item,dict) and item.get('crlf'):text=text.replace('{\n','{\n// Ã°Å¸Â§Âª Unicode location\n',1).replace('\n','\r\n')
            at=text.index('@@');plain=text.replace('@@','');path=work/(name+'-negative.aisharp');path.write_text(plain,encoding='utf-8',newline='\n');owned.append(path)
            exe=work/(name+'-must-not-exist.exe');assert not exe.exists()
            p=run([host,'build-native',path,'--out',exe,'--bits',bits,'--cc',cc,'--json'],env=env);payload=json.loads(p.stdout)
            assert p.returncode==2 and not exe.exists(),(name,p.stdout)
            assert len(payload['diagnostics'])==1;d=payload['diagnostics'][0];units=lambda s:len(s.encode('utf-16-le'))//2
            assert d['code']==code,(name,d);expected=dict(path=str(path),offset=units(plain[:at]),line=plain[:at].count('\n')+1,column=units(plain[:at].rsplit('\n',1)[-1])+1)
            assert {k:d['location'][k] for k in expected}==expected,(name,d,expected)
            if isinstance(item,dict) and 'length' in item:assert d['location']['length']==item['length'],(name,d)
            assert payload['sourceSha256']==sha(path);diagnostics.append(dict(name=name,bits=bits,result=payload))
    report=dict(hosts=builds,runtimeOutcomes=len(rows),exactDiagnostics=len(diagnostics),outputFailureChecks=sum('outputFailure' in r for r in rows),rows=rows,diagnostics=diagnostics,failures=[])
    (args.output/'acceptance.json').write_text(json.dumps(report,indent=2)+'\n');print('PASS native compiler',len(rows),'runtime outcomes',len(diagnostics),'exact diagnostics')
finally:
    for p in owned:
        if p.exists():
            assert p.resolve().parent==work and not p.is_symlink();unlink_owned(p)
