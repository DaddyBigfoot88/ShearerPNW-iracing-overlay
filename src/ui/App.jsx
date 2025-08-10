
import React, { useMemo, useState } from "react";
import { Card, CardContent, CardHeader, CardTitle } from "./components/card";
import { Button } from "./components/button";
import { Slider } from "./components/slider";
import { Input } from "./components/input";
import { Label } from "./components/label";
import { Badge } from "./components/badge";
import { LineChart, Line, XAxis, YAxis, Tooltip, Legend, ReferenceArea, ResponsiveContainer } from "recharts";
import { motion } from "framer-motion";
import { Upload, Play, Square, Download, AlertTriangle, CheckCircle2, Clock } from "lucide-react";
import { parseCsv, resampleToStep, buildConformance, segmentsFromConformance, rollingGrade, segmentsFromGrade, detectBrakeZones, brakeCallouts } from "./utils";

function FilePicker({ label, onPick }) {
  const id = React.useMemo(() => Math.random().toString(36).slice(2), []);
  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor={id} className="text-sm text-muted-foreground">{label}</Label>
      <div className="flex items-center gap-2">
        <Input id={id} type="file" accept=".csv" onChange={(e) => { const f = e.target.files?.[0]; if (f) onPick(f); }} />
        <Button type="button" variant="secondary" onClick={() => document.getElementById(id)?.click()}>
          <Upload className="w-4 h-4 mr-2"/> Load CSV
        </Button>
      </div>
    </div>
  );
}

function Heatbar({ segments, height = 12 }) {
  return (
    <div className="w-full rounded-xl overflow-hidden" style={{ height }}>
      <div className="relative w-full h-full bg-neutral-200">
        {segments.map((s, i) => (
          <div key={i} className="absolute top-0 bottom-0" style={{ left: `${s.start}%`, width: `${Math.max(0, s.end - s.start)}%`, background: s.color }} />
        ))}
      </div>
      <div className="flex justify-between text-[10px] text-muted-foreground mt-1">
        <span>0%</span><span>25%</span><span>50%</span><span>75%</span><span>100%</span>
      </div>
    </div>
  );
}

function Zones({ zones, color = "rgba(59,130,246,0.15)" }) {
  return (<>{zones.map((z,i)=>(<ReferenceArea key={i} x1={z.start} x2={z.end} strokeOpacity={0} fill={color} />))}</>);
}

function CalloutRow({ c }) {
  const icon = c.type.includes("late") || c.type.includes("missed") ? <AlertTriangle className="w-4 h-4"/> : c.type.includes("early") ? <Clock className="w-4 h-4"/> : <CheckCircle2 className="w-4 h-4"/>;
  const color = c.severity === "severe" ? "destructive" : c.severity === "moderate" ? "secondary" : "default";
  const label = c.type.replaceAll("_"," ");
  return (
    <div className="flex items-center justify-between px-3 py-2 rounded-xl bg-white border">
      <div className="flex items-center gap-2">
        {icon}
        <div className="text-sm">Zone {c.zoneIndex}: <span className="font-medium capitalize">{label}</span></div>
      </div>
      {c.delta!=null && (
        <Badge variant={color}>{c.type.includes("pressure") ? `${c.delta.toFixed(1)}%` : `${c.delta.toFixed(2)}%`}</Badge>
      )}
    </div>
  );
}

export default function App() {
  const [refRows, setRefRows] = useState([]);
  const [liveRows, setLiveRows] = useState([]);
  const [tols, setTols] = useState({ throttle: 8, brake: 8, steer: 6 });
  const [simStreaming, setSimStreaming] = useState(false);
  const [simPct, setSimPct] = useState(0);
  const [winPct, setWinPct] = useState(3);
  const [brOpts, setBrOpts] = useState({ startDeltaPct: 0.8, endDeltaPct: 0.8, pressDeltaPct: 6 });
  const [leadTime, setLeadTime] = useState(2.5);

  const onRefCsv = (file) => parseCsv(file, setRefRows);
  const onLiveCsv = (file) => parseCsv(file, setLiveRows);

  const ref = React.useMemo(() => resampleToStep(refRows, 0.5), [refRows]);
  const liveBase = React.useMemo(() => resampleToStep(liveRows, 0.5), [liveRows]);

  React.useEffect(() => {
    if (!simStreaming) return;
    const id = setInterval(() => setSimPct((p) => (p >= 100 ? 0 : p + 0.5)), 60);
    return () => clearInterval(id);
  }, [simStreaming]);

  const live = React.useMemo(() => {
    if (liveBase.length) return liveBase;
    if (!ref.length) return [];
    const j = Math.round((simPct / 100) * (ref.length - 1));
    const jitter=(x,d=3,w=0.5)=>{ if(x==null) return null; const delta=(Math.random()*2-1)*d*w; return x+delta; };
    return ref.map((r,i)=>({ pct:r.pct, throttle:jitter(r.throttle,5,i<=j?0.6:0.15), brake:jitter(r.brake,6,i<=j?0.6:0.15), steer:jitter(r.steer,4,i<=j?0.6:0.15), speed:jitter(r.speed,1.5,i<=j?0.6:0.15)}));
  }, [liveBase, ref, simPct]);

  const conf = React.useMemo(() => buildConformance(ref, live, tols), [ref, live, tols]);
  const confSegs = React.useMemo(() => segmentsFromConformance(conf, (c) => c.score), [conf]);
  const thSegs = React.useMemo(() => segmentsFromConformance(conf, (c) => (c.thOk ? 1 : 0)), [conf]);
  const brSegs = React.useMemo(() => segmentsFromConformance(conf, (c) => (c.brOk ? 1 : 0)), [conf]);
  const stSegs = React.useMemo(() => segmentsFromConformance(conf, (c) => (c.stOk ? 1 : 0)), [conf]);

  const grade = React.useMemo(() => rollingGrade(conf, winPct), [conf, winPct]);
  const gradeSegs = React.useMemo(() => segmentsFromGrade(grade), [grade]);

  const refBrakeZones = React.useMemo(() => detectBrakeZones(ref, 5, 3, 0.3), [ref]);
  const liveBrakeZones = React.useMemo(() => detectBrakeZones(live, 5, 3, 0.3), [live]);
  const callouts = React.useMemo(() => brakeCallouts(refBrakeZones, liveBrakeZones, ref, live, brOpts), [refBrakeZones, liveBrakeZones, ref, live, brOpts]);

  React.useEffect(()=>{
    window.overlay?.sendZones(refBrakeZones);
    window.overlay?.setLeadTime(leadTime);
  }, [JSON.stringify(refBrakeZones), leadTime]);

  const hasData = ref.length > 0;

  return (
    <div className="min-h-screen w-full bg-gradient-to-b from-white to-slate-50 p-6">
      <div className="mx-auto max-w-7xl grid grid-cols-1 xl:grid-cols-4 gap-6">
        <div className="xl:col-span-1 space-y-6">
          <Card className="rounded-2xl shadow-sm">
            <CardHeader><CardTitle className="text-xl">Load Laps</CardTitle></CardHeader>
            <CardContent className="space-y-4">
              <FilePicker label="Preferred / Reference Lap (.csv)" onPick={onRefCsv} />
              <FilePicker label="Live Lap (.csv) — optional" onPick={onLiveCsv} />
              <div className="flex items-center gap-3 pt-2">
                <Button type="button" onClick={() => setSimStreaming((v) => !v)}>
                  {simStreaming ? <><Square className="w-4 h-4 mr-2"/>Stop Sim</> : <><Play className="w-4 h-4 mr-2"/>Simulate Live</>}
                </Button>
                <div className="text-xs text-muted-foreground">Use this if you don’t have live data yet.</div>
              </div>
              <div className="space-y-2">
                <Label>Overlay lead time (s)</Label>
                <input type="number" step="0.1" value={leadTime} onChange={e=>setLeadTime(Number(e.target.value))} className="w-24 px-2 py-1 rounded-lg border border-slate-300" />
              </div>
            </CardContent>
          </Card>

          <Card className="rounded-2xl shadow-sm">
            <CardHeader><CardTitle className="text-xl">Match Tolerances</CardTitle></CardHeader>
            <CardContent className="space-y-4">
              <div><div className="flex justify-between text-sm"><span>Throttle (±{tols.throttle}%)</span><span className="text-muted-foreground">match window</span></div><Slider value={[tols.throttle]} min={2} max={20} step={1} onValueChange={([v]) => setTols((t) => ({ ...t, throttle: v }))} /></div>
              <div><div className="flex justify-between text-sm"><span>Brake (±{tols.brake}%)</span><span className="text-muted-foreground">match window</span></div><Slider value={[tols.brake]} min={2} max={20} step={1} onValueChange={([v]) => setTols((t) => ({ ...t, brake: v }))} /></div>
              <div><div className="flex justify-between text-sm"><span>Steering (±{tols.steer}°)</span><span className="text-muted-foreground">match window</span></div><Slider value={[tols.steer]} min={2} max={20} step={1} onValueChange={([v]) => setTols((t) => ({ ...t, steer: v }))} /></div>
            </CardContent>
          </Card>

          <Card className="rounded-2xl shadow-sm">
            <CardHeader><CardTitle className="text-xl">Grading & Brake Callouts</CardTitle></CardHeader>
            <CardContent className="space-y-4">
              <div>
                <div className="flex justify-between text-sm"><span>Rolling window: {winPct}%</span><span className="text-muted-foreground">for "Rolling Grade"</span></div>
                <Slider value={[winPct]} min={1} max={10} step={1} onValueChange={([v]) => setWinPct(v)} />
              </div>
              <div>
                <div className="text-sm mb-1">Brake timing threshold (start Δ%)</div>
                <Slider value={[brOpts.startDeltaPct]} min={0.2} max={3} step={0.1} onValueChange={([v]) => setBrOpts(o=>({...o, startDeltaPct: Number(v.toFixed(1))}))} />
              </div>
              <div>
                <div className="text-sm mb-1">Release timing threshold (end Δ%)</div>
                <Slider value={[brOpts.endDeltaPct]} min={0.2} max={3} step={0.1} onValueChange={([v]) => setBrOpts(o=>({...o, endDeltaPct: Number(v.toFixed(1))}))} />
              </div>
              <div>
                <div className="text-sm mb-1">Pressure threshold (avg brake Δ%)</div>
                <Slider value={[brOpts.pressDeltaPct]} min={2} max={20} step={1} onValueChange={([v]) => setBrOpts(o=>({...o, pressDeltaPct: v}))} />
              </div>
            </CardContent>
          </Card>

          <Card className="rounded-2xl shadow-sm">
            <CardHeader><CardTitle className="text-xl">Export</CardTitle></CardHeader>
            <CardContent className="space-y-3">
              <Button variant="secondary" onClick={() => exportConformanceCSV(conf)}><Download className="w-4 h-4 mr-2"/> Conformance CSV</Button>
              <div className="text-xs text-muted-foreground">Save a lap-by-lap conformance trace for review or coaching.</div>
            </CardContent>
          </Card>
        </div>

        <div className="xl:col-span-3 space-y-6">
          <Card className="rounded-2xl shadow-sm">
            <CardHeader>
              <div className="flex items-end justify-between">
                <CardTitle className="text-2xl">Overlay — Throttle / Brake / Steering</CardTitle>
                {hasData && (<motion.div initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} className="text-sm text-muted-foreground">Preferred brake zones shaded • Green = match</motion.div>)}
              </div>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="h-[360px] w-full">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={mergeForChart(ref, live)} margin={{ top: 12, right: 24, left: 8, bottom: 12 }}>
                    <XAxis dataKey="pct" type="number" domain={[0, 100]} tickFormatter={(v) => `${v}%`} />
                    <YAxis yAxisId="left" orientation="left" domain={[0, 100]} tickFormatter={(v) => `${v}%`} />
                    <YAxis yAxisId="right" orientation="right" domain={[-180, 180]} />
                    <Tooltip formatter={(val, name) => [Number(val).toFixed(1), name]} labelFormatter={(l) => `Lap ${Number(l).toFixed(1)}%`} />
                    <Legend />
                    <Zones zones={refBrakeZones} />
                    <Line yAxisId="left" type="monotone" dataKey="refThrottle" name="Ref Throttle" dot={false} strokeWidth={2} />
                    <Line yAxisId="left" type="monotone" dataKey="liveThrottle" name="Live Throttle" dot={false} strokeDasharray="4 3" strokeWidth={2} />
                    <Line yAxisId="left" type="monotone" dataKey="refBrake" name="Ref Brake" dot={false} strokeWidth={2} />
                    <Line yAxisId="left" type="monotone" dataKey="liveBrake" name="Live Brake" dot={false} strokeDasharray="4 3" strokeWidth={2} />
                    <Line yAxisId="right" type="monotone" dataKey="refSteer" name="Ref Steer (°)" dot={false} strokeWidth={2} />
                    <Line yAxisId="right" type="monotone" dataKey="liveSteer" name="Live Steer (°)" dot={false} strokeDasharray="4 3" strokeWidth={2} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
              {hasData ? (
                <div className="space-y-3">
                  <div>
                    <div className="text-sm mb-1">Overall Match</div>
                    <Heatbar segments={confSegs} />
                  </div>
                  <div>
                    <div className="text-sm mb-1">Rolling Grade</div>
                    <Heatbar segments={gradeSegs} />
                  </div>
                  <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                    <div><div className="text-sm mb-1">Throttle Match</div><Heatbar segments={thSegs} /></div>
                    <div><div className="text-sm mb-1">Brake Match</div><Heatbar segments={brSegs} /></div>
                    <div><div className="text-sm mb-1">Steering Match</div><Heatbar segments={stSegs} /></div>
                  </div>
                </div>
              ) : (
                <div className="text-sm text-muted-foreground">Load a reference lap to begin. Optionally add a live lap or click Simulate Live.</div>
              )}
            </CardContent>
          </Card>

          {hasData && (
            <Card className="rounded-2xl shadow-sm">
              <CardHeader><CardTitle className="text-xl">Brake Zones: Preferred vs Live</CardTitle></CardHeader>
              <CardContent className="space-y-2">
                <div className="text-sm text-muted-foreground">Blue = Preferred (Reference), Purple = Live</div>
                <div className="relative w-full h-6 rounded-xl bg-neutral-200 overflow-hidden">
                  {refBrakeZones.map((z, i) => (<div key={`r-${i}`} className="absolute h-full" style={{ left: `${z.start}%`, width: `${z.end - z.start}%`, background: "rgba(59,130,246,0.6)" }} />))}
                  {liveBrakeZones.map((z, i) => (<div key={`l-${i}`} className="absolute h-full mix-blend-multiply" style={{ left: `${z.start}%`, width: `${z.end - z.start}%`, background: "rgba(168,85,247,0.55)" }} />))}
                </div>
            </CardContent>
            </Card>
          )}

          {hasData && (
            <Card className="rounded-2xl shadow-sm">
              <CardHeader><CardTitle className="text-xl">Brake Callouts</CardTitle></CardHeader>
              <CardContent className="space-y-2">
                {callouts.length ? callouts.map((c, i) => <CalloutRow key={i} c={c} />) : <div className="text-sm text-muted-foreground">No issues detected at current thresholds.</div>}
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}

function mergeForChart(ref, live){
  const m = Math.min(ref.length, live.length || ref.length);
  const out=[];
  for (let i=0;i<m;i++){
    const r=ref[i]; const l=live[i] ?? {};
    out.push({ pct:r.pct, refThrottle:r.throttle, refBrake:r.brake, refSteer:r.steer, liveThrottle:l.throttle??null, liveBrake:l.brake??null, liveSteer:l.steer??null });
  }
  return out;
}

function exportConformanceCSV(conf){
  if (!conf?.length) return;
  const header=['pct','overall_score','throttle_ok','brake_ok','steer_ok'];
  const rows=conf.map((c)=>[c.pct.toFixed(3),c.score.toFixed(3),c.thOk?1:0,c.brOk?1:0,c.stOk?1:0]);
  const csv=[header.join(','), ...rows.map(r=>r.join(','))].join('\n');
  const blob=new Blob([csv], {type:'text/csv;charset=utf-8;'});
  const url=URL.createObjectURL(blob); const a=document.createElement('a'); a.href=url; a.download=`conformance_${Date.now()}.csv`; document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url);
}
