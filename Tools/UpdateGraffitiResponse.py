from pathlib import Path
import zipfile,xml.etree.ElementTree as E,json,zlib,hashlib,uuid
import argparse
root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description='Update an existing PptEase demo with the current shader')
parser.add_argument('--input',type=Path,default=root/'Samples/Graffiti/Graffiti-Dual-Control.pptx')
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
src=args.input;dst=args.output
if src.resolve()==dst.resolve():raise ValueError('Use a separate output file')
with zipfile.ZipFile(src) as z:data={n:z.read(n) for n in z.namelist()}
for name in list(data):
 if not name.startswith('ppt/tags/'):continue
 r=E.fromstring(data[name]);tags={e.get('name'):e.get('val') for e in r}
 if 'PPTEASE_SHADER' not in tags:continue
 _,g,c,h=tags['PPTEASE_SHADER'].split(':');d=json.loads(zlib.decompress(bytes.fromhex(''.join(tags[f'PES_{g}_{i:05}'] for i in range(int(c)))),-15));d['Source']=(root/'Samples/Graffiti/Graffiti-Dual-Control.hlsl').read_text(encoding='utf-8-sig').lstrip('\ufeff')
 raw=json.dumps(d,ensure_ascii=False).encode();co=zlib.compressobj(wbits=-15);enc=(co.compress(raw)+co.flush()).hex().upper();g=uuid.uuid4().hex.upper();chunks=[enc[i:i+200] for i in range(0,len(enc),200)];tag=r[0].tag
 for e in list(r):
  if e.get('name','').startswith('PES_') or e.get('name')=='PPTEASE_SHADER':r.remove(e)
 for i,ch in enumerate(chunks):E.SubElement(r,tag,{'name':f'PES_{g}_{i:05}','val':ch})
 E.SubElement(r,tag,{'name':'PPTEASE_SHADER','val':f'1:{g}:{len(chunks)}:{hashlib.sha256(raw).hexdigest().upper()}'})
 data[name]=E.tostring(r,encoding='utf-8',xml_declaration=True)
replacements={}
for name in ['ppt/slides/slide2.xml','ppt/slides/slide4.xml']:
 r=E.fromstring(data[name])
 for e in r.iter():
  if e.tag.endswith('}t') and e.text:
   for a,b in replacements.items():e.text=e.text.replace(a,b)
 data[name]=E.tostring(r,encoding='utf-8',xml_declaration=True)
with zipfile.ZipFile(dst,'w',zipfile.ZIP_DEFLATED) as z:
 for n,b in data.items():z.writestr(n,b)
print(dst)
