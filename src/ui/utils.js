
import Papa from 'papaparse';

export function parseCsv(file, onDone){
  Papa.parse(file, {
    header: true,
    dynamicTyping: true,
    skipEmptyLines: true,
    complete: (results) => {
      const rows = (results.data||[]).map(r=>normalizeRow(r)).filter(Boolean);
      onDone(rows);
    }
  });
}

function normalizeRow(r){
  const map = {};
  for (const k of Object.keys(r||{})) map[k.trim().toLowerCase()] = r[k];
  const pct = num(map['lapdistpct'] ?? map['lapdist_pct'] ?? map['pct']);
  if (pct == null) return null;
  return {
    pct: clamp(pct, 0, 100),
    throttle: clamp(num(map['throttle']) ?? 0, 0, 100),
    brake: clamp(num(map['brake']) ?? 0, 0, 100),
    steer: num(map['steeringwheelangle']) ?? num(map['steer']) ?? 0,
    speed: num(map['speed']) ?? null,
    time: num(map['time']) ?? null,
  };
}

const num = (v)=>{ if (v==null || v==='') return null; const n=Number(v); return Number.isFinite(n)?n:null; };
const clamp = (n, lo, hi)=>{ if(n==null) return n; return Math.max(lo, Math.min(hi, n)); };

export function resampleToStep(rows, stepPct=0.5){
  if (!rows?.length) return [];
  const sorted = [...rows].sort((a,b)=>a.pct-b.pct);
  const out=[]; let j=0;
  for (let p=0;p<=100+1e-6;p+=stepPct){
    while (j<sorted.length-1 && sorted[j+1].pct < p) j++;
    const a=sorted[j], b=sorted[Math.min(j+1, sorted.length-1)];
    const t = a.pct===b.pct ? 0 : (p-a.pct)/(b.pct-a.pct);
    const lerp=(x,y)=> (x==null||y==null?null:x+(y-x)*t);
    out.push({ pct:+p.toFixed(3),
      throttle: lerp(a.throttle,b.throttle),
      brake: lerp(a.brake,b.brake),
      steer: lerp(a.steer,b.steer),
      speed: lerp(a.speed,b.speed)
    });
  }
  return out;
}

export function detectBrakeZones(samples, onThr=5, offThr=3, minWidthPct=0.3){
  const zones=[]; let active=false, start=null;
  for (let i=0;i<samples.length;i++){
    const b=samples[i].brake??0; const p=samples[i].pct;
    if (!active && b>=onThr){ active=true; start=p; }
    else if (active && b<=offThr){ active=false; const end=p; if (end-start>=minWidthPct) zones.push({start,end}); }
  }
  if (active && start!=null){ const end=samples.at(-1)?.pct ?? 100; if (end-start>=minWidthPct) zones.push({start,end}); }
  return zones;
}

export function buildConformance(ref, live, tolerances){
  const m = Math.min(ref.length, live.length);
  const out=[];
  for (let i=0;i<m;i++){
    const r=ref[i], l=live[i];
    const thOk = within(r.throttle, l.throttle, tolerances.throttle);
    const brOk = within(r.brake, l.brake, tolerances.brake);
    const stOk = within(r.steer, l.steer, tolerances.steer);
    const okCount = (thOk?1:0)+(brOk?1:0)+(stOk?1:0);
    out.push({ pct: r.pct, score: okCount/3, thOk, brOk, stOk });
  }
  return out;
}
const within=(a,b,t)=>{ if(a==null||b==null) return false; return Math.abs(a-b)<=t; };

export function segmentsFromConformance(conf, toColor){
  if (!conf?.length) return [];
  const colorFor=(s)=> (s>=0.8?'#16a34a': s>=0.5?'#eab308':'#dc2626');
  const segs=[]; let start=conf[0].pct; let curr=colorFor(toColor(conf[0]));
  for (let i=1;i<conf.length;i++){
    const c=colorFor(toColor(conf[i]));
    if (c!==curr){ segs.push({start,end:conf[i-1].pct,color:curr}); start=conf[i].pct; curr=c; }
  }
  segs.push({start, end: conf.at(-1).pct, color: curr});
  return segs;
}

export function rollingGrade(conf, windowPct=3){
  if (!conf?.length) return [];
  const win=Math.max(1, Math.round((windowPct/100)*conf.length));
  const arr=conf.map(c=>c.score);
  let sum=0; const out=[];
  for (let i=0;i<arr.length;i++){ sum+=arr[i]; if(i>=win) sum-=arr[i-win]; const avg=i>=win-1?sum/win:arr[i]; out.push({pct:conf[i].pct, grade:avg}); }
  return out;
}

export function segmentsFromGrade(gradeArr){
  if (!gradeArr?.length) return [];
  const colorFor=(g)=> (g>=0.8?'#16a34a': g>=0.5?'#eab308':'#dc2626');
  const segs=[]; let start=gradeArr[0].pct; let curr=colorFor(gradeArr[0].grade);
  for (let i=1;i<gradeArr.length;i++){
    const c=colorFor(gradeArr[i].grade);
    if (c!==curr){ segs.push({start,end:gradeArr[i-1].pct,color:c}); start=gradeArr[i].pct; curr=c; }
  }
  segs.push({start, end: gradeArr.at(-1).pct, color: curr});
  return segs;
}

export function brakeCallouts(refZones, liveZones, refSamples, liveSamples, opts){
  const out=[];
  const avgBrake=(s,a,b)=>{
    const rows=s.filter(r=>r.pct>=a && r.pct<=b);
    if (!rows.length) return 0;
    return rows.reduce((m,r)=>m+(r.brake??0),0)/rows.length;
  };
  refZones.forEach((rz, idx)=>{
    let best=null, bestOverlap=0;
    liveZones.forEach((lz)=>{
      const overlap = Math.max(0, Math.min(rz.end, lz.end) - Math.max(rz.start, lz.start));
      if (overlap>bestOverlap){ bestOverlap=overlap; best=lz; }
    });
    if (!best){
      out.push({ type:'missed_brake', zoneIndex: idx+1, detail:'No braking where reference did', severity:'severe', pct: rz.start });
      return;
    }
    const startDelta = best.start - rz.start; // + late
    const endDelta   = best.end - rz.end;     // + released later
    const refAvg = avgBrake(refSamples, rz.start, rz.end);
    const liveAvg= avgBrake(liveSamples,best.start,best.end);
    const pressDelta = liveAvg - refAvg;

    if (Math.abs(startDelta) >= opts.startDeltaPct){
      out.push({ type: startDelta>0?'late_brake':'early_brake', zoneIndex: idx+1, delta:startDelta, severity: Math.abs(startDelta)>=opts.startDeltaPct*2?'severe':'moderate', pct: rz.start });
    }
    if (Math.abs(pressDelta) >= opts.pressDeltaPct){
      out.push({ type: pressDelta>0?'too_much_pressure':'not_enough_pressure', zoneIndex: idx+1, delta:pressDelta, severity: Math.abs(pressDelta)>=opts.pressDeltaPct*2?'severe':'moderate', pct:(best.start+best.end)/2 });
    }
    if (Math.abs(endDelta) >= opts.endDeltaPct){
      out.push({ type: endDelta>0?'late_release':'early_release', zoneIndex: idx+1, delta:endDelta, severity: Math.abs(endDelta)>=opts.endDeltaPct*2?'severe':'moderate', pct: rz.end });
    }
  });
  return out;
}
