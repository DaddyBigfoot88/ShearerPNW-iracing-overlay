
import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';

const ENTER_PCT_TOL = 0.15;   // how close to zone.start to trigger BRAKE NOW
const PREP_THRESHOLDS = [3, 2, 1]; // seconds before zone to show prepare 3-2-1

function useZones(){
  const [zones, setZones] = useState([]);
  useEffect(()=>{
    const off = window.overlay?.onZones?.((z)=> setZones(z||[]));
    return ()=> off?.();
  }, []);
  return zones;
}

function useLeadTime(){
  const [lead, setLead] = useState(2.5);
  useEffect(()=>{
    const off = window.overlay?.onSetLeadTime?.((sec)=> setLead(Number(sec)||2.5));
    return ()=> off?.();
  }, []);
  return lead;
}

function useTelemetry(){
  const [t,setT]=useState({ pct:0, throttle:0, brake:0 });
  useEffect(()=>{
    const off = window.overlay?.onTelemetry?.((payload)=>{
      setT({
        pct: Number(payload?.LapDistPct ?? 0),
        throttle: Number(payload?.Throttle ?? 0),
        brake: Number(payload?.Brake ?? 0),
      });
    });
    return ()=> off?.();
  },[]);
  return t;
}

function Overlay(){
  const t = useTelemetry();
  const zones = useZones();
  const lead = useLeadTime();
  const [message, setMessage] = useState('');
  const [severity, setSeverity] = useState('warn'); // warn | danger

  useEffect(()=>{
    if (!zones?.length) { setMessage(''); return; }
    const pct = t.pct ?? 0;
    const next = zones.find(z => z.end > pct);
    if (!next) { setMessage(''); return; }

    // Heuristic: 1 sec ~ 0.6% lap (tune in main UI)
    const secToPct = 0.6;
    const warnStartPct = Math.max(0, next.start - (lead * secToPct));

    if (pct >= next.start - ENTER_PCT_TOL && pct <= next.end){
      setSeverity('danger');
      setMessage('BRAKE NOW');
    } else if (pct >= warnStartPct && pct < next.start - ENTER_PCT_TOL){
      const distPct = (next.start - pct) / secToPct; // ≈ seconds remaining
      const remaining = Math.max(1, Math.round(distPct));
      setSeverity('warn');
      setMessage(`Prepare ${remaining}`);
    } else {
      setMessage('');
    }
  }, [t.pct, JSON.stringify(zones), lead]);

  if (!message) return null;
  return (
    <div style={{width:'100%',height:'100%',display:'flex',alignItems:'center',justifyContent:'center',pointerEvents:'none'}}>
      <div style={{
        padding:'14px 18px',
        borderRadius:16,
        fontSize:28,
        fontWeight:800,
        letterSpacing:0.5,
        background: severity==='danger'?'rgba(220,38,38,0.88)':'rgba(234,179,8,0.88)',
        color:'#0b0b0b',
        boxShadow:'0 8px 30px rgba(0,0,0,0.35)',
        border:'2px solid rgba(255,255,255,0.6)',
        textTransform:'uppercase'
      }}>{message}</div>
    </div>
  );
}

createRoot(document.getElementById('root')).render(<Overlay />);
