
import React from 'react'
export function Label({htmlFor,className='',children}){return <label htmlFor={htmlFor} className={`text-sm text-slate-500 ${className}`}>{children}</label>}
