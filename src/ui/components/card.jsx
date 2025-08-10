
import React from 'react'
export function Card({className='',children}){return <div className={`rounded-2xl shadow-sm bg-white border border-slate-200 ${className}`}>{children}</div>}
export function CardHeader({children}){return <div className='p-4 pb-0'>{children}</div>}
export function CardTitle({className='',children}){return <h2 className={`text-slate-900 font-semibold ${className}`}>{children}</h2>}
export function CardContent({className='',children}){return <div className={`p-4 ${className}`}>{children}</div>}
