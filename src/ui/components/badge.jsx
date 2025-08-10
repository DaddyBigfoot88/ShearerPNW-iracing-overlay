
import React from 'react'
export function Badge({variant='default',className='',children}){
  const base='px-2.5 py-1 rounded-lg text-xs font-semibold';
  const styles=variant==='destructive'?'bg-red-600 text-white':variant==='secondary'?'bg-amber-500/20 text-amber-700':'bg-slate-200 text-slate-800';
  return <span className={`${base} ${styles} ${className}`}>{children}</span>
}
