
import React from 'react'
export function Slider({value=[0],min=0,max=100,step=1,onValueChange=()=>{}}){
  const v=value[0]??0;
  return <input type='range' className='w-full accent-blue-600' min={min} max={max} step={step} value={v} onChange={e=>onValueChange([Number(e.target.value)])}/>
}
