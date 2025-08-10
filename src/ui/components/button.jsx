
import React from 'react'
export function Button({variant='default',className='',children,...props}){
  const base='inline-flex items-center gap-2 px-3 py-2 rounded-xl text-sm font-medium transition';
  const styles=variant==='secondary'?'bg-slate-100 hover:bg-slate-200 text-slate-900 border border-slate-200':'bg-blue-600 hover:bg-blue-700 text-white';
  return <button className={`${base} ${styles} ${className}`} {...props}>{children}</button>
}
