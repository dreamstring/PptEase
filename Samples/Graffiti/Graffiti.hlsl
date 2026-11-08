// Original PptEase red spray-X material. No third-party shader code embedded.
// @param axisX 0 1 0.5
// @param axisY 0 1 0.5
// @param fiberDensity 0.2 2 1
// @param fiberSpeed 0 3 1
// @param inkSpeed 0 3 1
// @param oilSpeed 0 3 1
// @param oilRelief 0 2 1
// @param fallSpeed 1 4 2.4
// @param lengthVariation 0 1 0.85
cbuffer PreviewParameters : register(b0) { float time; float roughness; float2 resolution; };
float hash21(float2 p){return frac(sin(dot(p,float2(91.37,257.13)))*43713.733);}
float noise2(float2 p){float2 i=floor(p),f=frac(p);f=f*f*(3-2*f);return lerp(lerp(hash21(i),hash21(i+float2(1,0)),f.x),lerp(hash21(i+float2(0,1)),hash21(i+1),f.x),f.y);}
float fb(float2 p){float sum=0,amp=.5;[unroll]for(int i=0;i<4;i++){sum+=amp*noise2(p);p=p*2.07+3.9;amp*=.5;}return sum;}
float pressure(float t,float variant){return variant<0?(.20+1.02*pow(sin(t*2.65),1.3)):(.72+.43*sin(t*3.5))*(1-.82*pow(t,2));}
float stroke(float2 p,float2 a,float2 b,float w){float2 ab=b-a;float t=saturate(dot(p-a,ab)/dot(ab,ab));float width=w*pressure(t,a.x);float2 bend=float2(.009*sin(t*5),.005*sin(t*7));return length(p-a-ab*t-bend)-width;}
float sdfX(float2 p){float2 uv=p/.65+.5;
 if(any(uv<0)||any(uv>1))return .1;
 return (brushMask.SampleLevel(peSampler,uv,0).r-.5)*.2;
}
float lowerEdge(float x){float bottom=-1;
 // Find the lowest interior sample, then refine the actual zero crossing.
 [loop]for(int j=0;j<96;j++){float yy=-.32+j*.0068;if(sdfX(float2(x,yy))<-.001)bottom=yy;}
 if(bottom<-.3)return bottom;
 float lo=bottom,hi=bottom+.0068;
 [unroll]for(int k=0;k<7;k++){float mid=(lo+hi)*.5;if(sdfX(float2(x,mid))<0)lo=mid;else hi=mid;}
 return (lo+hi)*.5;
}
// Smooth temporal lattice: evolution changes the field in place, without UV reset.
float noise3(float3 q){
 float z=floor(q.z),f=frac(q.z);f=f*f*(3-2*f);
 return lerp(noise2(q.xy+z*float2(37.1,19.7)),noise2(q.xy+(z+1)*float2(37.1,19.7)),f);
}
float evolve(float2 q,float t){
 float v=0,a=.55;
 [unroll]for(int j=0;j<4;j++){v+=a*noise3(float3(q,t));q=q*2.03+7.31;t=t*1.19+11.7;a*=.48;}
 return v;
}
// Curved brush coordinates: longitudinal position and signed distance to centerline.
float2 brushCoordinates(float2 p,float2 a,float2 b,float2 c){
 float t=saturate(dot(p-a,c-a)/dot(c-a,c-a));
 [unroll]for(int k=0;k<5;k++){
  float2 v=lerp(lerp(a,b,t),lerp(b,c,t),t);
  float2 dv=2*lerp(b-a,c-b,t),ddv=2*(c-2*b+a);
  float denom=dot(dv,dv)+dot(v-p,ddv);
  t=saturate(t-dot(v-p,dv)/max(.02,denom));
 }
 float2 v=lerp(lerp(a,b,t),lerp(b,c,t),t);
 float2 tangent=normalize(lerp(b-a,c-b,t));
 return float2((t-.5)*length(c-a),dot(p-v,float2(-tangent.y,tangent.x)));
}
float2 materialCoordinates(float2 p,float2 a,float2 b,float2 c){
 float2 start=normalize(b-a),end=normalize(c-b);
 float before=dot(p-a,start),after=dot(p-c,end),halfLength=length(c-a)*.5;
 if(before<0)return float2(-halfLength+before,dot(p-a,float2(-start.y,start.x)));
 if(after>0)return float2(halfLength+after,dot(p-c,float2(-end.y,end.x)));
 return brushCoordinates(p,a,b,c);
}
float fiberGaps(float2 q,float t){
 // Long fibers with a slowly evolving envelope; no screen-space speckles.
 float bend=(evolve(float2(q.x*9,q.y*26),t*.22)-.5)*.003;
 float fine=noise3(float3(q.x*9-t*.55,(q.y+bend)*480,t*.58));
 float bundle=evolve(float2(q.x*13-t*.25,q.y*90),t*.44);
 float lengths=noise3(float3(q.x*18-t*.4,q.y*48,t*.35));
 // Broad sparse openings carry the composition; finer hairs sit within selected bundles.
 float region=noise3(float3(q.x*8,q.y*34+13,t*.25));
 float wide=noise3(float3(q.x*6-t*.45,(q.y+bend)*160+21,t*.48));
 float mainGaps=smoothstep(.62,.82,wide)*smoothstep(.42,.68,region)*smoothstep(.25,.5,lengths);
 float fineGaps=smoothstep(.55,.76,fine)*smoothstep(.39,.64,bundle)*smoothstep(.28,.53,lengths);
 return saturate(mainGaps*.9+fineGaps*.55);
}

// Material coordinates: center = static brush, angle = adjacent material families.
// Dual-phase UV flow follows Valve/Catlike Coding flow-map principles.
float flowingNoise(float2 q,float2 velocity,float clock){
 float phase=frac(clock*.12),phaseB=frac(clock*.12+.5);
 float weight=1-abs(phase*2-1);
 return lerp(fb(q-velocity*phaseB),fb(q-velocity*phase),weight);
}
float4 main(float4 pos:SV_POSITION):SV_TARGET {
 float2 uv=pos.xy/resolution,p=(uv-float2(.5,.39))*float2(resolution.x/resolution.y,1);
 float2 disk=float2(axisX,axisY)*2-1;
 float strength=smoothstep(0,1,saturate(length(disk)));
 float angle=frac(atan2(disk.y,disk.x)/6.2831853+1)*5;
 float weights[5];
 [unroll]for(int k=0;k<5;k++){float delta=abs(angle-k);delta=min(delta,5-delta);weights[k]=strength*smoothstep(1,0,delta);}
 float dry=weights[0],spray=weights[1],water=weights[2],wet=weights[3],oil=weights[4];
 float motion=dry*1.25+water*.6+wet*.85+oil*.60;
 float clock=time*motion;
 float aa=1.1/resolution.y;
 float baseDistance=sdfX(p);
 float2 qa=brushCoordinates(p,float2(-.145,-.265),float2(-.10,.075),float2(.21,.24));
 float2 qb=brushCoordinates(p,float2(-.235,.23),float2(-.025,-.055),float2(.245,-.215));
 float branch=smoothstep(-.012,.012,abs(qb.y)-abs(qa.y));
 float2 local=lerp(qb,qa,branch);
 float2 flowUV=float2(local.x*24,local.y*140);
 float field=evolve(float2(local.x*14,local.y*100),clock*.12);
 float fibersA=fiberGaps(qa,clock*fiberSpeed);
 float fibersB=fiberGaps(qb,clock*fiberSpeed);

 float holes=lerp(fibersB,fibersA,branch);
 // Keep the crossing dense, with longer open fibers away from the center.
 holes*=smoothstep(.025,.11,length(p));
 // Stationary local edge wear: intact shoulders alternate with torn bristle bundles.
 float patch=smoothstep(.49,.68,noise2(p*float2(13,17)+9));
 float edgeBand=1-smoothstep(.004,.035,abs(baseDistance));
 float strand=noise2(float2(local.x*16,local.y*570));
 float tears=smoothstep(.48,.76,strand)*smoothstep(.32,.58,noise2(float2(local.x*49,local.y*100)+17));
 float edgeShift=patch*edgeBand*(tears*.023-.003);
 float d=baseDistance+edgeShift*(1-spray*.75);

 float grain=hash21(floor(pos.xy));
 float paperGrain=noise2(p*270);
 float3 paper=float3(.965,.958,.94)+(paperGrain-.5)*.009;
 float dryMask=(1-smoothstep(-aa,aa,d))*(1-sqrt(dry)*smoothstep(.09,.40,holes*fiberDensity));
 float spread=.003+spray*.038;
 float coverage=1-smoothstep(-aa,spread,d);
 float particles=step(grain,pow(saturate(coverage),2.6));
 float solid=dryMask;
 float mask=lerp(solid,max(1-smoothstep(-.025,-.003,d),particles*coverage),spray);
 // Xuan-paper wash: broad ink pools, capillary fringe and tide-line deposits.
 // Advect pigment in curved brush coordinates while the paper fringe stays anchored.
 float inkClock=clock*inkSpeed;
 float2 inkUV=p+float2(.012*sin(inkClock*.38+p.y*12),-.020*inkClock);
 float2 warp=float2(evolve(inkUV*12,inkClock*.22),evolve(inkUV*12+31,inkClock*.22))-.5;
 float wash=evolve(inkUV*16+warp*1.1,inkClock*.24);
 float bloom=evolve(inkUV*39+warp*1.6,inkClock*.29);
 float paperFiber=noise2(p*float2(340,120));
 float inkEdge=d+(evolve(p*95,clock*.035)-.5)*.008;
 float feather=1-smoothstep(-.005,.012+paperFiber*.006,inkEdge);
 float pool=smoothstep(.16,.82,wash);
 float tide=exp(-abs(bloom-.47)*40)*.08;
 float inkDensity=.12+pool*1.65+tide;
 float washAlpha=(1-exp(-inkDensity*1.4))*feather;
 mask=lerp(mask,washAlpha,water);
 // Coverage holes and relief are distinct: moving white is actual bare paper.
 // Preserve the original fine paint relief; advect its coordinates, not broad ridges.
 // Advect each complete material along its own curved stroke coordinate.
 // Blend sampled fields, never UV coordinates across the crossing.
 float offset=time*oil*oilSpeed*.035;
 float2 paintA=qa-float2(offset,0),paintB=qb-float2(offset,0);
 float groovesA=fb(float2(paintA.x*16,paintA.y*185)-float2(1,.06));
 float groovesB=fb(float2(paintB.x*16,paintB.y*185)-float2(1,.06));
 float oilGrooves=lerp(groovesB,groovesA,branch);
 float grooves=lerp(flowingNoise(float2(local.x*16,local.y*185),float2(2,.12),clock),oilGrooves,oil);
 float oilField=lerp(evolve(float2(paintB.x*14,paintB.y*100),0),evolve(float2(paintA.x*14,paintA.y*100),0),branch);
 float pigment=lerp(.96,.70+.36*field,(dry+oil*.7)*smoothstep(.02,.10,length(p)));
 float frozenPigment=lerp(.96,.70+.36*oilField,.7*smoothstep(.02,.10,length(p)));
 pigment=lerp(pigment,frozenPigment,oil);
 float3 ink=float3(.69,.012,.022)*pigment;
 float3 cinnabar=lerp(float3(.74,.12,.065),float3(.32,.012,.022),pool);
 ink=lerp(ink,cinnabar,water);
 float height=(.00015+oil*.004*oilRelief)*grooves*(1-water);
 height+=(1-smoothstep(-.026,.002,baseDistance))*(.0004+wet*.002+oil*.001);
 float3 normal=normalize(float3(-ddx(height)*resolution.x,-ddy(height)*resolution.y,1));
 float3 light=normalize(float3(-.45,-.65,1));
 float diffuse=saturate(dot(normal,light));
 float spec=pow(saturate(dot(normal,normalize(light+float3(0,0,1)))),lerp(18,52,wet));
 ink=ink*(.86+.18*diffuse)+float3(1,.89,.81)*spec*(.008+.16*wet+.12*oil)*(1-water);
 float wetMask=0,highlight=0;
 // Unequal rivulets begin at the actual X silhouette. A narrow neck joins
 // a teardrop; no detached sphere is appended to a line segment.
 [unroll]for(int i=0;i<4;i++){
   // Independent clocks and duty cycles allow one to four overlapping trails.
   float age=max(time,0)*motion*(.18+.035*hash21(float2(i,81)))+i*.237;
   float cycle=floor(age),phase=frac(age);
   float lifetime=i==0?.995:lerp(.48,.88,hash21(float2(i+cycle*13,72)));
   if(phase>=lifetime)continue;
   float seed=hash21(float2(i+cycle*17,41));float ax=-.17+i*.085+.045*hash21(float2(i+cycle*17,15));
   float root=lowerEdge(ax)-.006;
   if(root<-.3)continue;
   // Growth is directional. The head accelerates, detaches, then a new
   // head grows along the same wetted trail. No sinusoidal retracting tube.
   float fallJitter=lerp(.82,1.18,hash21(float2(i+cycle*23,91)));
   float growth=smoothstep(0,.36/(max(fallSpeed,1)*fallJitter),phase);
   float envelope=smoothstep(0,.035,phase)*(1-smoothstep(lifetime-.18,lifetime,phase));
   float randomLength=hash21(float2(i+cycle*19,6));
   float maxLen=lerp(.045,.018+.067*pow(randomLength,1.7),lengthVariation)*wet;
   float len=maxLen*(.20+.80*growth);
   float z=p.y-root,t=saturate(z/max(len,.0001));
   float width=.0018+.0024*pow(seed,1.5);
   float center=ax+(.0006+.0005*seed)*sin(z*21+seed*9);
   float neck=width*(1.6-.75*smoothstep(0,.7,t))*(.8+.2*noise2(float2(z*95,seed*71)));
   // Head dimensions use physical coordinates, not normalized trail length.
   // This keeps short trails from flattening the droplet.
   float headWidth=width*1.25;
   float headHeight=headWidth*1.65;
   float headY=root+len;
   float headDistance=(length(float2((p.x-center)/headWidth,(p.y-headY)/headHeight))-1)*headWidth;
   float radius=neck;
   float side=abs(p.x-center)-radius;
   float bounds=smoothstep(-.008,.003,z)*(1-smoothstep(len-aa,len+aa,z));
   float trail=(1-smoothstep(-aa,aa,side))*bounds;
   float head=(1-smoothstep(-aa,aa,headDistance))*smoothstep(.005,.025,len);
   float rivulet=max(trail,head)*envelope;
   wetMask=max(wetMask,rivulet);
   // Root accumulation stays inside the painted edge, avoiding dark disks.
   float rootPool=(1-smoothstep(.006,.013,length(float2((p.x-ax),(p.y-root)*1.8))))*dryMask;
   wetMask=max(wetMask,rootPool*wet*envelope);
   highlight=max(highlight,rivulet*(1-smoothstep(.12,.40,abs((p.x-center)/max(radius,.0001)+.3)))*(.4+.6*noise2(float2(z*60-clock*2,seed))));
 }
 float3 result=lerp(paper,ink,mask);
 // Keep the root beneath existing pigment; never paint a dark attachment disk.
 float hanging=wetMask*step(.002,wet)*(1-mask);
 float3 wetRed=float3(.54,.008,.016)+highlight*float3(.16,.08,.07);
 result=lerp(result,wetRed,hanging);
 return float4(saturate(result),1);
}
