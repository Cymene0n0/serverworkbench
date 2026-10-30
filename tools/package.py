from pathlib import Path
import hashlib,json,shutil,zipfile,tempfile

root=Path(__file__).resolve().parent.parent
source=root/'src/ServerWorkbench.Desktop/bin/Release/net48'
live=root/'dist/ServerWorkbench'
live.mkdir(parents=True,exist_ok=True)
staging=tempfile.TemporaryDirectory(prefix='swb-package-')
target=Path(staging.name)/'ServerWorkbench'
target.mkdir()
for item in source.iterdir():
    if item.suffix.lower() in ('.exe','.dll','.config'): shutil.copy2(item,target/item.name)
for relative in ['README.md','docs/使用说明.md','docs/Windows验收.md','docs/测试报告.md','docs/内网穿透.md','THIRD-PARTY-NOTICES.txt','FRP-LICENSE.txt','tools/启动检查.cmd']:
    item=root/relative
    shutil.copy2(item,target/item.name)
(target/'README.md').write_text((root/'README.md').read_text(encoding='utf-8-sig').replace('(docs/','('),encoding='utf-8')
readme='开服工坊 ServerWorkbench 0.1\r\n\r\n请先将整个压缩包解压到可写目录，再运行 ServerWorkbench.exe。\r\n需要64位Windows与.NET Framework 4.8（或新系统已有的4.8.1）。\r\n实测范围、通过项与未验证项请阅读测试报告.md。\r\n'
(target/'README.txt').write_text(readme,encoding='utf-8-sig')
# Replace shipped files only; never delete Data, settings or existing backups.
for item in target.iterdir(): shutil.copy2(item,live/item.name)
archive=root/'dist/ServerWorkbench-win-x64-v0.1.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for item in sorted(target.rglob('*')):
        if item.is_file():z.write(item,item.relative_to(target.parent))
digest=hashlib.sha256(archive.read_bytes()).hexdigest()
(root/'dist/SHA256SUMS.txt').write_text(digest+'  '+archive.name+'\n',encoding='utf-8')
verification_file=root/'docs/windows-verification.json'
verification=json.loads(verification_file.read_text(encoding='utf-8-sig')) if verification_file.exists() else None
manifest={'version':'0.1','platform':'Windows x64','framework':'.NET Framework 4.8','archive':archive.name,'sha256':digest,'windows_execution_verified':bool(verification and verification.get('desktop_started')),'verification':verification,'files':[{'path':str(f.relative_to(target)),'bytes':f.stat().st_size} for f in sorted(target.rglob('*')) if f.is_file()]}
(root/'dist/build-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print(archive)
print('SHA256 '+digest)
source_archive=root/'dist/ServerWorkbench-source-v0.1.zip'
with zipfile.ZipFile(source_archive,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for item in sorted(root.rglob('*')):
        relative=item.relative_to(root)
        if not item.is_file() or item.name=='.DS_Store' or any(part in {'bin','obj','.git','dist','dotnet','cli-home','nuget','Data','work','__MACOSX','__pycache__'} for part in relative.parts):continue
        z.write(item,Path('ServerWorkbench')/relative)
print(source_archive)
source_digest=hashlib.sha256(source_archive.read_bytes()).hexdigest()
(root/'dist/SHA256SUMS.txt').write_text(digest+'  '+archive.name+'\n'+source_digest+'  '+source_archive.name+'\n',encoding='utf-8')
staging.cleanup()
