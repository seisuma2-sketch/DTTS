export function LogoMark({ className = "h-7 w-7" }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" className={className} aria-hidden>
      <defs>
        <linearGradient id="dtts-logo" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#22D3EE" />
          <stop offset="1" stopColor="#3E8EF7" />
        </linearGradient>
      </defs>
      <rect x="8" y="5" width="13" height="19" rx="2.6" fill="none" stroke="url(#dtts-logo)" strokeWidth="1.6" transform="rotate(-11 14.5 14.5)" opacity="0.7" />
      <rect x="11" y="8" width="13" height="19" rx="2.6" fill="#0B1F3F" stroke="url(#dtts-logo)" strokeWidth="1.6" transform="rotate(8 17.5 17.5)" />
      {/* 雫：深海とスペードの中間的な記号 */}
      <path d="M17.6 12.8c1.8 1.8 2.9 3.1 2.9 4.5a2.9 2.9 0 0 1-5.8 0c0-1.4 1.1-2.7 2.9-4.5z" fill="url(#dtts-logo)" />
    </svg>
  );
}

export default function Logo() {
  return (
    <a href="#top" className="flex items-center gap-2.5" aria-label="DTTS トップへ">
      <LogoMark />
      <span className="text-[17px] font-bold tracking-tight text-ice">DTTS</span>
    </a>
  );
}
